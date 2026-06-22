# 仓库格式规范(草案 v1)

定义 azbackup 写入、azrestore 读取的 Blob 仓库布局、结构文件与加密信封。
写入端与读取端必须遵守同一版本(`FormatVersion.Current`)。

> 🚧 草案。加密构造需在实现期定稿并经安全评审。

> 仓库根 = 一个 `<container>/`(**一个 container = 一个备份**,见 [需求 §6.1](requirements.md))。下文 `<repo>` 即该 container。

## 1. 容器内 Blob 布局(逻辑)—— 二级结构

```
<repo>/
  config                  # 仓库配置 + 密码校验令牌 + KDF 盐/参数。Hot,加密
  refs/snapshots          # 快照清单(id + 时间),小。Hot,加密
  refs/index              # 全局索引的当前分片清单(小,可覆盖)。Hot,加密
  root/<snapshotId>       # 快照根对象:指向根 tree + run 元数据(**不含物理位置**)。Hot,加密
  struct/<objid>.<n>      # tree 对象 与 索引分片;过大时再分卷(.<n>)。Hot,加密
  packs/<packid>.<n>      # pack 的第 n 个卷(数据本体)。Archive,加密
  lock                    # 分布式锁(租约)。Hot
```

- `<snapshotId>`、`<objid>`、`<packid>` 为**不透明标识**,**不泄露任何明文**。
- ✅ **所有结构对象一律 Hot**;**仅 `packs/*` 在 Archive**。
- 🔑 **关键解耦(应对复杂交叉替换)**:**快照/目录树只按内容 `hash` 引用**;`hash → 物理位置(pack/entry)` 由**全局索引**维护。重复变更、压实重打包**只更新索引**,**已写的快照永不改动**。

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
  rootTree: <objid>,                 // 指向根目录的 tree 对象
  configSnapshot: { ... } }          // 本次运行关键配置快照
```
- **不含物理位置**:快照只认 `rootTree` → 各级 tree → 文件 `hash`;物理位置全由全局索引解析。快照一旦写出**永不修改**。

### tree 对象(每目录一个,内容寻址)`struct/<objid>`
```
{ entries: [
    { name, type: dir,  child: <objid> },                  // 子目录 → 另一个 tree 对象
    { name, type: file, size, mtime, mode, hash } ],       // **仅按 hash 引用内容,不写 pack**
  next?: <objid> }                                          // 超大目录:条目过多则再分片
```
- **增量复用**:未变目录的 `objid` 不变 → 直接复用,不重写、不重传。
- 文件名只存在于(加密后的)tree 对象中;blob 名不可逆。

### 全局索引(`hash → 物理位置`,可演进)`struct/<objid>` + `refs/index`
```
// 索引分片(追加 + 周期压实;latest-wins):
{ byHash: { <hash>: { pack: <packid>, offset, size } },         // 内容在该包"解压后明文"中的字节范围
  packs:  { <packid>: { volumes, totalSize, wrappedKey,
                        members: [<hash>...], liveCount } } }     // GC / 压实统计
// refs/index 列出当前生效的索引分片清单
```
- **解析**:还原/校验时 `文件 hash →(查索引)→ pack/entry/卷`。
- **去重**:新文件 hash 命中索引即引用既有包,不重传。
- **交叉替换/压实只动索引**:某内容从旧包搬到新包(压实),只**改写其 `byHash` 条目** `hash→新 pack`,**快照与 tree 一字不动**。重复变更、链式压实皆如此。
- **GC**:`live = 所有保留快照的 tree 可达 hash 集合`;不在其中的 hash 从索引剔除;`liveCount=0` 的包(全部卷)删除;`死重比例 = 1 - liveCount/members` 触发压实。
- **可扩展**:索引按分片存储、可分卷、周期压实,绝不形成单一巨型文件。

### config(仓库配置,部分非机密)
```
{ formatVersion, kdf: { algo: "argon2id", salt, params },
  pwCheck: <用 master_key 加密的已知校验值>,    // 密码校验令牌
  hashAlgo: "blake3",                           // 仓库初始化时固定,之后不可改
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
- **切分方式**:对一个 pack「**先压缩(LZMA2/`xz`)再加密**」得到的字节流,**由本工具自己按固定大小切分**为卷。卷即密文分块,前向写入、互不回改。还原需取回某 pack 的**全部卷**按序拼接后解密、解压。
- ⚠️ **整 pack 完成才有效**:压缩流未结束前不是有效流(xz 末尾才完整);任一卷在整个 compress+encrypt **成功结束前都不可独立使用**。压缩中途报错 → 全部已产出卷作废。
- **产出与纳入分离**:卷先产到**压缩工作区(work dir)**;**仅当整个 pack 成功**,其**全部卷才原子性一次性纳入 spool**(等上传)。失败则丢弃。
- 由此:**spool 的水位/背压只统计已纳入的就绪卷**;work dir 中在产的大 pack 属「允许临时超限」的磁盘占用(见 [架构 §2.4b](architecture.md))。
- **结构分片同理可分卷**:`struct/<objid>` 过大时,用相同机制(压缩+加密后切分)切成 `struct/<objid>.<n>` 多卷,确保结构文件也不会因过大而单次上传失败。

## 6. 版本与兼容

- `FormatVersion.Current`(见 `src/AzureBackup.Core/FormatVersion.cs`)。
- 任何不兼容变更必须 bump;azrestore 读取时校验,拒绝高于自身支持的版本。
