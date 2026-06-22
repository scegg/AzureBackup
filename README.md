# AzureBackup

将挂载的卷(volume)增量备份到 Azure Storage Blob 的工具,运行在 Docker(Linux)。
配套一个**独立的还原工具**。

- **技术栈**:C# / .NET 10
- **上传**:原生 Azure SDK(`Azure.Storage.Blobs`)
- **加密**:AES-256-GCM,密码经 Argon2id 派生;**文件名/目录结构也加密**
- **压缩**:7-Zip 最大压缩,跳过不可压类型,小文件分组,大包分卷(~100MB)
- **增量**:内容寻址(每文件 hash)+ 引用计数 GC
- **存储**:数据本体 Archive 层,结构文件 Hot 层

> 状态:🚧 文档/设计阶段。源码为骨架,尚未实现。

## 目录结构

```
.
├── src/
│   ├── AzureBackup.Core/      # 共享库:格式/加密/压缩/打包/Blob 访问
│   ├── AzureBackup.Backup/    # 备份 CLI(产物 azbackup)
│   └── AzureBackup.Restore/   # 还原 CLI(产物 azrestore)
├── tests/AzureBackup.Core.Tests/
├── docker/
│   ├── backup.Dockerfile      # azbackup-backup 镜像
│   └── restore.Dockerfile     # azbackup-restore 镜像
├── build/                     # 构建脚本
├── docs/                      # 文档(见下表)
└── .github/workflows/         # CI
```

## 文档

| 文档 | 内容 |
|------|------|
| [需求](docs/requirements.md) | 完整需求(含风险标注与待决项) |
| [架构](docs/architecture.md) | 组件、数据流、锁、GC |
| [格式规范](docs/format-spec.md) | Blob 布局、结构文件、加密信封 |
| [配置](docs/configuration.md) | 环境变量 |
| [使用](docs/usage.md) | 备份运行方式 |
| [还原](docs/restore.md) | 独立还原工具 |
| [构建与部署](docs/build-and-deploy.md) | 构建脚本、镜像、CI |

## 快速开始(开发)

```bash
./build/build.sh                  # 构建 + 测试
./build/docker-build.sh all       # 构建两个镜像
```

## 已确认的关键设计取舍

- **纯增量 + 引用计数 GC**:存储最省;⚠️ 还原某次备份可能需解冻多个历史 pack。
- **hash 间接索引**:快照/目录树只按内容 hash 引用,`hash→物理位置`由全局索引维护 → 重复变更、老包压实**只改索引、不动已写快照**,干净处理复杂交叉替换。
- **数据固定 Archive 层**:成本最低;⚠️ 最短期 180 天,保留期更短会产生**早删费用**(已接受)。
- **老包压实默认 30%**:死重达比例即重打回收空间;⚠️ 需解冻+重写(费用没所谓,可调/关)。
- **monorepo + 共享 Core**:备份/还原是独立镜像与可执行,但共享格式库以**保证读写格式不漂移**。

## 仍开放的少量决策

绝大多数补充需求已敲定(见 [需求](docs/requirements.md))。剩余开放项(详见 [需求 §11](docs/requirements.md)):

1. **dry-run**:是否需要「只算变更不上传」的预演模式。
2. **手动解锁**:崩溃后锁租约 TTL 自动释放已设计,是否还要手动 `unlock` 命令。
3. **hash 算法**:默认 SHA-256,是否改 BLAKE3。
