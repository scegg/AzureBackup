# 实现状态与续作指南

> 给"换机器后继续"的交接文档。任务清单/记忆只存在于上一台机器的本地 `~/.claude`,
> 不随 git clone 走 —— **本文件是权威的进度与待办来源**。

最后更新:见 git log。当前:**功能闭环完成,126 单测全过,已 push 到 `origin/main`。**

## 1. 已实现(全部在 `AzureBackup.Core`,均有单测)

| 层 | 模块 | 说明 |
|----|------|------|
| 加密 | `Crypto/` | Argon2id KDF、AES-256-GCM 信封(`Aead`)、内容密钥包裹、密码校验令牌、**分段流式加密** `SegmentedCipher`(大 pack) |
| 哈希 | `Hashing/ContentHasher` | BLAKE3(缓冲 + 流式) |
| 压缩 | `Compression/` | `ICompressor` + `Store` + `Xz`(LZMA2,shell `xz -9e`) |
| 扫描 | `Scan/` | `GitignoreMatcher`、`FileScanner`(剪枝/空目录/符号链接)、`ChangeDetector`(mtime 闸门) |
| 打包 | `Pack/` | `NoCompressPolicy`、`PackGrouper`(目录×codec)、`PackBuilder`/`PackReader` |
| 分卷 | `Volumes/` | `VolumeSplitter`、`VolumeWriter`(写入即滚动切分) |
| 存储 | `Storage/` | `IBlobStore`、`RetryPolicy`(5/30/90/300s,2h)、`InMemoryBlobStore`(测试)、`AzureBlobStore`、租约锁 |
| 仓库 | `Repo/` | `Repository`(init/open+密码校验)、`StructCodec`、`RetentionPolicy`、`RepoIndex`、`GarbageCollector`、`Compactor`(压实执行)、`RemoteVerifier`、`TreeBuilder`(内容寻址树)、`SnapshotStore` |
| 备份 | `Backup/BackupRunner` | 端到端:扫描→去重打包→上传(Archive)→写快照(Hot)→保留+GC+压实;锁跳过/续租;dry-run |
| 还原 | `Restore/RestoreRunner` | 全量/选择性还原(按 pack 分组解码一次)、空目录/mtime/mode、还原后 hash 校验、本地 verify |
| 通知 | `Notifications/WebhookNotifier` | Bark / 通用,GET/POST,事件过滤 |

**CLI**(均 env 驱动,已接线):
- `src/AzureBackup.Backup`(`azbackup`):单任务或 jobs 文件(YAML)、cron 常驻(NCrontab)、webhook。
- `src/AzureBackup.Restore`(`azrestore`):列快照→还原最新/指定、选择性、本地 verify。

**最强验证**:集成测试真实地 备份临时目录→读快照→经 索引/pack/卷 解码→还原到另一目录→逐字节一致;
并验证 去重(二次未变零上传)、改动产新包、压实存活成员后内容完好。

## 2. 待办(优先级从高到低)

1. **真机端到端验证**:对真实 Azure Storage 跑一次备份+还原。
   - ✅ **本地已用 Azurite 跑通完整逻辑链路**(含 `AzureBlobStore`:上传/下载/列举/删除/blob 租约锁)——见 §3 的 `build/local-e2e-test.sh`。
   - 真机仅剩两点与本地不同需现场确认:**Archive 解冻(数小时)** 与 **container 需预先创建**(代码不自动建)。
   - 测试时设 `AZBACKUP_DATA_TIER=Hot` 即可避免等待解冻(见下)。
2. ~~**(便于测试)数据层可配开关**~~ ✅ **已完成**:`BackupOptions.DataTier` 已接通,
   `BackupRunner` 上传数据卷 + `Compactor` 重打包均使用该层;`EnvOptions` 读取 `AZBACKUP_DATA_TIER`
   (env / job `dataTier` 覆盖,默认 `Archive`,可选 `Hot`/`Cool`/`Cold`/`Archive`)。
3. **Docker 镜像实际构建验证**:`./build/docker-build.sh backup`(本机单架构);多架构需 `PUSH=1`。镜像已装 `xz-utils`。
4. **发布流水线**:GHCR + Docker Hub(打 tag 构建推送);见 `docs/build-and-deploy.md`,此前定为"测试通过后再发"。
5. **远端 verify 调度接线**:`RemoteVerifier` 已实现,但 `AZBACKUP_VERIFY_MODE`(after-backup/cron/both)尚未接入 CLI。
6. **GC/verify 独立 cron**:`AZBACKUP_GC_MODE=cron`、`AZBACKUP_GC_CRON`、`AZBACKUP_VERIFY_CRON` 尚未在 Program 接线(目前 GC 随备份执行)。

## 3. 如何继续

```bash
git clone https://github.com/scegg/AzureBackup.git && cd AzureBackup
./build/build.sh                    # 编译 + 全部单测(126)
./build/docker-build.sh backup      # 构建备份镜像(本机单架构)
docker build -f docker/restore.Dockerfile -t azrestore:local .
```

### 本地 Docker 端到端测试(无需 Azure 凭据)✅ 已验证

用 Azurite 模拟 Azure Storage,一键跑通 备份→去重→还原→逐字节校验→改动产新包→local verify→错密码拒绝。
关键:数据层用 `AZBACKUP_DATA_TIER=Hot`(Azurite 不支持 Archive 解冻;逻辑链路与真机一致):

```bash
BUILD=1 ./build/local-e2e-test.sh   # 先构建镜像再测;之后可省 BUILD=1 复用镜像
KEEP=1  ./build/local-e2e-test.sh   # 保留 azurite/网络/临时目录便于排查
```

脚本自动:起 Azurite → 预建 container → 造测试数据(文本/二进制/不压缩扩展名/子目录/空目录/特定 mtime)
→ 备份×2 → 还原 → `diff -r` 校验 → 改动再备份 → 内置 local verify → 错密码拒绝 → 清理。

跑一次真机备份(需 Azure 连接串 + 已存在的空 container):
```bash
docker run --rm \
  -v /要备份:/backup/source:ro \
  -e AZURE_STORAGE_CONNECTION_STRING="..." \
  -e AZURE_STORAGE_CONTAINER=mybackup \
  -e AZBACKUP_PASSWORD=口令 \
  -e AZBACKUP_RETENTION_COUNT=7 \
  azbackup-backup:local
```

## 4. 设计与格式参考

- [需求](requirements.md) · [架构](architecture.md) · [格式规范](format-spec.md) · [配置](configuration.md) · [使用](usage.md) · [还原](restore.md) · [构建与发布](build-and-deploy.md)

## 5. 关键设计取舍(已定)

- 纯增量 + 引用计数 GC;**hash 间接索引**(快照按 hash 引用,`hash→位置`在全局索引)——重定位/压实只改索引,快照不可变。
- 数据本体 Archive,结构文件全 Hot;压实默认 30%(env 可调/off)。
- 加密 AES-256-GCM + Argon2id;文件名也加密;blob 名不透明。
- 压缩 LZMA2(`xz`);不压缩类型仍加密;小文件分组(目录×codec)+ 分卷(默认 100MB)。
- 一个 container = 一个备份;多任务用 jobs 文件;密码错即拒、不支持改/找回。
