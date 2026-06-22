# 仓库格式规范(草案 v1)

定义 azbackup 写入、azrestore 读取的 Blob 仓库布局、结构文件与加密信封。
写入端与读取端必须遵守同一版本(`FormatVersion.Current`)。

> 🚧 草案。加密构造需在实现期定稿并经安全评审。

> 仓库根 = 一个 `<container>/`(**一个 container = 一个备份**,见 [需求 §6.1](requirements.md))。下文 `<repo>` 即该 container。

## 1. 容器内 Blob 布局(逻辑)—— 二级结构

```
<repo>/
  config                  # 仓库配置 + 密码校验令牌 + KDF 盐/参数。Hot,加密(盐非机密但受完整性保护)
  root/<snapshotId>       # 【顶层】快照根对象:很小,指向根 tree 对象 + 索引分片清单 + run 元数据。Hot,加密
  refs/snapshots          # 快照清单(id + 时间),小。Hot,加密
  struct/<objid>.<n>      # 【二级】结构分片:tree 对象 / 索引分片;过大时再分卷(.<n>)。Hot,加密
  packs/<packid>.<n>      # pack 的第 n 个卷(数据本体)。Archive,加密
  lock                    # 分布式锁(租约)。Hot
```

- `<snapshotId>`、`<objid>`、`<packid>` 为**不透明标识**(随机或基于密文 hash),**不泄露任何明文**。
- 卷号 `<n>` 从 0 起;某对象/pack 的卷数记录在引用它的上层对象里。
- ✅ **所有结构对象(config / root / refs / struct/*)一律 Hot**;**仅 `packs/*` 在 Archive**。

## 2. 存储层

| 对象 | 层 | 理由 |
|------|----|------|
| `packs/*`(数据本体) | **Archive** | 成本最低;⚠️ 最短期 180 天,早删计费,入层后不读不改 |
| `config` / `root/*` / `refs/*` / `struct/*`(全部结构) | **Hot** | 还原/增量入口,频繁读写;**结构文件必须全 Hot** |
| `lock` | **Hot** | 租约协调 |

## 3. 结构文件模型(二级、内容寻址、可分卷)

> 目标:**不存在枚举全部文件的单一巨型文件**。结构按目录拆成大量小对象,未变对象跨快照复用,过大对象再分卷。

### 顶层:snapshot 根对象 `root/<snapshotId>`
```
{ formatVersion, snapshotId, createdAtUtc,
  rootTree: <objid>,                 // 指向根目录的 tree 对象(二级)
  indexShards: [ <objid>... ],       // 本快照可见的索引分片清单
  configSnapshot: { ... } }          // 本次运行关键配置快照
```

### 二级:tree 对象(每目录一个,内容寻址)`struct/<objid>`
```
{ entries: [
    { name, type: dir, child: <objid> },                         // 子目录 → 另一个 tree 对象
    { name, type: file, size, mtime, mode, hash,
      pack: <packid>, entry: <序号> } ],
  // 超大目录:entries 过多时本对象再分片,next: <objid> 链接后续分片
  next?: <objid> }
```
- **增量复用**:未变目录的 tree 对象 `objid` 不变 → 直接复用,不重写、不重传。
- 文件名只存在于(加密后的)tree 对象中;blob 名不可逆。

### 二级:索引分片 `struct/<objid>`(追加式)
```
{ byHash: { <hash>: { pack, entry, size } },                     // 去重
  packs:  { <packid>: { volumes: <n>, totalSize, members: [<hash>...] } } } // GC 引用
```
- 每批新 pack 产生**新的索引分片**(追加),不重写巨型索引;GC 时合并压实旧分片。

### config(仓库配置,部分非机密)
```
{ formatVersion, kdf: { algo: "argon2id", salt, params },
  pwCheck: <用 master_key 加密的已知校验值>,    // 密码校验令牌
  volumeSizeBytes, repoLabel?, ... }
```

## 4. 加密信封(草案)

- **KDF**:`master_key = Argon2id(password, salt, params)`。`salt`/`params` 存于 `config`。
- **数据加密**:每个 pack 生成随机 `content_key`,以 `master_key` 包裹后存于**索引分片**对应的 `packs.<packid>` 项;
  pack 字节流用 `AES-256-GCM` 加密(分段,每段独立 nonce),再切分为卷。
- **结构文件**:`root/*`、`refs/*`、`struct/*`、`config`(机密字段)同样以 `master_key` 体系加密。
- **密码校验**:`config.pwCheck` 为用 `master_key` 加密的已知值;运行开始解密比对即可判定密码对错(不匹配立即失败)。
- **完整性**:GCM 自带认证;`config` 的非机密字段(盐等)用 MAC 防篡改。
- **文件名保密**:明文路径只出现在加密后的 tree 对象中;任何 blob 名均不可逆。

> ⚠️ 上述为方向性设计。nonce 唯一性、密钥分层、分段大小、抗篡改边界等将在实现期定稿,并通过 `/security-review`。

## 5. 分卷

- 默认卷大小 ~100 MB,经 env 配置。
- **切分方式**:对一个 pack「**先压缩(7z 单档)再加密**」得到的字节流,**由本工具自己按固定大小切分**为卷;**不使用 7z 原生分卷**。卷即密文分块,前向写入、互不回改。还原需取回某 pack 的**全部卷**按序拼接后解密、解压。
- ⚠️ **整 pack 完成才有效**:压缩档在收尾前不是有效档(7z 元数据头在最后);任一卷在整个 compress+encrypt **成功结束前都不可独立使用**。压缩中途报错 → 全部已产出卷作废。
- **产出与纳入分离**:卷先产到**压缩工作区(work dir)**;**仅当整个 pack 成功**,其**全部卷才原子性一次性纳入 spool**(等上传)。失败则丢弃。
- 由此:**spool 的水位/背压只统计已纳入的就绪卷**;work dir 中在产的大 pack 属「允许临时超限」的磁盘占用(见 [架构 §2.4b](architecture.md))。
- **结构分片同理可分卷**:`struct/<objid>` 过大时,用相同机制(压缩+加密后切分)切成 `struct/<objid>.<n>` 多卷,确保结构文件也不会因过大而单次上传失败。

## 6. 版本与兼容

- `FormatVersion.Current`(见 `src/AzureBackup.Core/FormatVersion.cs`)。
- 任何不兼容变更必须 bump;azrestore 读取时校验,拒绝高于自身支持的版本。
