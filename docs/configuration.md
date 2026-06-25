# 配置

配置优先经**环境变量**注入(便于 Docker / K8s)。⚠️ 凭据与密码**切勿**写入镜像或提交仓库。

> 🔧 变量名为草案,实现期可微调。带 `?` 的为可选。

## 备份(azbackup):env 作用域三分类

配合 [多任务 jobs 文件](#jobs-文件多任务可选):
- **A 共享/全局**:整批生效;jobs 模式下仍取 env。
- **B 每任务·与 jobs 互斥**:定义"备份什么";**jobs 模式下由各 job 提供,这些 env 被忽略(若设置则警告)**。
- **C 托底默认·每任务可覆盖**:job 未指定时取 env 默认。

### A. 共享 / 全局

| 变量 | 说明 | 默认 |
|------|------|------|
| `AZBACKUP_PASSWORD` | 加密密码(派生主密钥)。建议用 `*_FILE` | — |
| `AZBACKUP_PASSWORD_FILE?` | 从文件读取密码(优先) | — |
| `AZURE_STORAGE_ACCOUNT` | Storage 账户名 | — |
| `AZURE_STORAGE_CONNECTION_STRING?` / `AZURE_STORAGE_SAS?` / Managed Identity / SP | 鉴权(经 `Azure.Identity`,`AZURE_CLIENT_ID` 等) | — |
| `AZBACKUP_JOBS_FILE?` | 多任务 jobs 文件路径;**设置即进入多任务模式** | — |
| `AZBACKUP_DRY_RUN?` | 干跑:只扫描算变更与统计,不压缩/加密/上传/改仓库 | `false` |
| `AZBACKUP_HASH_ALGO?` | hash 算法(仓库初始化时固定,之后不可改) | `blake3` |
| `AZBACKUP_CRON?` | 备份 cron;空=单次运行后退出 | — |
| `AZBACKUP_GC_MODE?` | `after-backup` / `cron` / `both` / `off` | `after-backup` |
| `AZBACKUP_GC_CRON?` | GC 独立 cron(当 `GC_MODE=cron`/`both`) | — |
| `AZBACKUP_VERIFY_MODE?` | 远端 verify 时机:`after-backup` / `cron` / `both` / `off` | `off` |
| `AZBACKUP_VERIFY_CRON?` | 远端 verify 独立 cron | — |
| `AZBACKUP_UPLOAD_CONCURRENCY?` | 并发上传 pack 数 | `5` |
| `AZBACKUP_SPOOL_DIR?` | 待上传卷暂存目录 | 容器内临时目录 |
| `AZBACKUP_SPOOL_MAX_FILES?` | spool 卷数上限 | `500` |
| `AZBACKUP_SPOOL_MAX_BYTES?` | spool 总字节上限,如 `2GB` | `2GB` |
| `AZBACKUP_LOCK_TTL?` | 锁租约时长(运行时自动续租;崩溃后约此时长内自动过期)。高级项,一般不必改 | `60s` |
| `AZBACKUP_LOG_LEVEL?` | 日志级别(`debug/info/warn/error`) | `info` |
| `AZBACKUP_REPORT_PATH?` | 运行报告输出文件;空=仅 stdout | — |
| `AZBACKUP_WEBHOOK_*` | 通知,见下「通知 / Webhook」 | — |
| `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`(含小写) | 标准 Linux 代理变量,**备份与还原均遵循**(.NET/Azure SDK 默认读取);非本工具私有 | — |

### B. 每任务 · 与 jobs 文件互斥

> 单任务模式取自 env;**多任务模式由各 job 提供,以下 env 被忽略**。

| 变量 | job 字段 | 说明 | 默认 |
|------|---------|------|------|
| `AZURE_STORAGE_CONTAINER` | `container` | 目标容器(**一个 container = 一个备份**) | — |
| `AZBACKUP_SOURCE_PATH` | `source` | 源根路径(容器内) | `/backup/source` |
| `AZBACKUP_EXCLUDE_FILE?` | `excludeFile` | 排除规则文件(gitignore 语义,支持 `!`),可在源外 | — |
| `AZBACKUP_NOCOMPRESS_FILE?` | `noCompressFile` | 不压缩规则文件(gitignore 风格:扩展名/递归路径,支持 `!`) | — |

### C. 托底默认 · 每任务可覆盖

> job 未指定则取 env 默认值。

| 变量 | job 字段 | 说明 | 默认 |
|------|---------|------|------|
| `AZBACKUP_VOLUME_SIZE?` | `volumeSize` | 分卷大小 | `100MB` |
| `AZBACKUP_GROUP_FILE_MAX?` | `groupFileMax` | ≤此值的文件才参与分组打包;更大者单独成包 | `1MB` |
| `AZBACKUP_PACK_TARGET_SIZE?` | `packTargetSize` | 分组包目标总大小(压缩前),达到即封包 | `256MB` |
| `AZBACKUP_PACK_MAX_FILES?` | `packMaxFiles` | 分组包最大文件数,达到即封包 | `4096` |
| `AZBACKUP_PACK_COMPACTION?` | `packCompaction` | 死重压实阈值:死重比例 ≥ 此值则重打存活成员、删旧包回收空间(⚠️ 对仅存于历史快照的成员需解冻 Archive + 重写 + 早删);`off`=100%死才删(不重打) | `30%` |
| `AZBACKUP_NOCOMPRESS_EXT?` | `noCompressExt` | 不压缩扩展名清单(逗号分隔) | `7z,rar,zip,gz,mp4,mkv,jpg,png,...` |
| `AZBACKUP_FORCE_HASH?` | `forceHash` | mtime 未变仍强制重算 hash | `false` |
| `AZBACKUP_VOLATILE_FILE_MAX_REPACK?` | `volatileFileMaxRepack` | 文件持续变动时该 pack 的最大重打包次数(≠ 上传退避重试) | `3` |
| `AZBACKUP_PRESERVE_SYMLINKS?` | `preserveSymlinks` | 保留符号链接 | `true` |
| `AZBACKUP_PRESERVE_HARDLINKS?` | `preserveHardlinks` | 保留硬链接关系 | `true` |
| `AZBACKUP_PRESERVE_ATTRS?` | `preserveAttrs` | 保留属主/权限/xattr/mtime | `true` |
| `AZBACKUP_RETENTION_COUNT?` | `retentionCount` | 保留最近 X 个 | —(不限数量) |
| `AZBACKUP_RETENTION_DAYS?` | `retentionDays` | 保留最近 Y 天 | `180` |
| `AZBACKUP_RETENTION_MODE?` | `retentionMode` | COUNT+DAYS 组合:`and`/`or` | `and` |
| `AZBACKUP_DATA_TIER?` | `dataTier` | 数据本体存储层 | `Archive` |

### jobs 文件(多任务,可选)

`AZBACKUP_JOBS_FILE` 指向的 YAML;**A 类共享 env 与 C 类默认仍来自 env**,各 job 只写 B 类与需覆盖的 C 类:

```yaml
jobs:
  - name: web                      # 必填,任务标识(用于报告)
    source: /backup/web            # 必填(B)
    container: web-backup          # 必填(B)
    excludeFile: /config/web.exclude     # 可选(B)
    noCompressFile: /config/web.nocompress
    retentionCount: 14             # 可选(C 覆盖)
    retentionDays: 30
    retentionMode: and
  - name: db
    source: /backup/db
    container: db-backup
    connectionStringEnv: DB_CONN   # 用另一个 storage account(见下)
    passwordEnv: DB_PW             # 用独立加密口令
    spoolDir: /spool/db            # 独立工作目录(可选)
    webhookUrl: https://api.day.app/<key>   # 该 job 单独通知(可选)
    retentionDays: 7
```

- 一个容器**按 jobs 顺序**逐个执行;一个 `AZBACKUP_CRON` 触发整批。
- 每 job 独立 container/仓库/`lock`/报告;**某 job 失败不阻断其余**,失败计入整批报告与通知。
- 多任务模式下若仍设置 B 类 env → **忽略并警告**。

**per-job 凭据与覆盖(C 类扩展)**:为避免把密钥写进 jobs 文件,连接串与口令用**环境变量名引用**:

| 字段 | 作用 | 未设时回退 |
|------|------|------------|
| `connectionStringEnv` | 该 job 用的 storage account——值是**环境变量名**,如 `DB_CONN`,再由 env 提供 `DB_CONN=DefaultEndpoints...` | 全局 `AZURE_STORAGE_CONNECTION_STRING` |
| `passwordEnv` | 该 job 的加密口令——同样是**环境变量名** | 全局 `AZBACKUP_PASSWORD(_FILE)` |
| `spoolDir` | 该 job 的工作目录 | 全局 `AZBACKUP_SPOOL_DIR` |
| `webhookUrl` / `webhookKind` / `webhookMethod` / `webhookEvents` | 该 job 跑完**单独发**的即时通知(与全局汇总通知并存) | 不发 per-job 通知 |

> ✅ 因此多任务**可以各用不同的 storage account 和不同口令**:在 jobs 里写 `connectionStringEnv`/`passwordEnv` 指向不同环境变量即可。

### 通知 / Webhook(azbackup,共享)

| 变量 | 说明 | 默认 |
|------|------|------|
| `AZBACKUP_WEBHOOK_URL?` | 通知地址。Bark 形如 `https://api.day.app/<key>` | — |
| `AZBACKUP_WEBHOOK_KIND?` | `bark` 或 `generic` | `bark` |
| `AZBACKUP_WEBHOOK_METHOD?` | `GET` 或 `POST` | `POST` |
| `AZBACKUP_WEBHOOK_EVENTS?` | 触发事件:`error` / `success` / `both` | `error` |
| `AZBACKUP_WEBHOOK_TITLE?` | 标题模板(可含占位符,如 `{status} {host}`) | `AzureBackup {status}` |
| `AZBACKUP_WEBHOOK_BODY?` | 内容模板(占位符如 `{uploaded} {errors}`) | 运行摘要 |

> Bark:`GET` 时拼成 `…/<key>/<title>/<content>`;`POST` 时以 JSON body 提交 `title`/`body`。

> ⚠️ 失败退避重试策略(固定):`5s / 30s / 90s / 300s`,之后每 `300s` 一次,总时长上限 `2h`。

> 保留策略:`COUNT` 与 `DAYS` 同时设置时,由 `AZBACKUP_RETENTION_MODE`(`and`/`or`)决定组合语义。
> - `and`:仅当某快照**既**超出数量**又**超出天数时才删除(保留更多)。
> - `or`:某快照**超出数量或超出天数**任一即删除(保留更少)。

## 还原(azrestore)

还原工具**除密码外**的配置均来自仓库结构文件。仅需:

| 变量 | 说明 | 默认 |
|------|------|------|
| `AZBACKUP_PASSWORD` / `_FILE` | 解密密码 | — |
| `AZURE_STORAGE_ACCOUNT` + 鉴权 | 源 account 与鉴权(同 A 类) | — |
| `AZURE_STORAGE_CONTAINER?` | 指定要还原/校验的 container;**不指定则列出 account 下所有 container 供选择** | — |
| `AZRESTORE_TARGET_PATH?` | 还原目的地(容器内) | `/restore/target` |
| `AZRESTORE_REHYDRATE_PRIORITY?` | 解冻优先级 `Standard`/`High` 的**默认值**;还原时用户可交互覆盖 | `Standard` |

本地 verify(深度校验,仅在还原工具;远端 verify 在备份工具,见上 `AZBACKUP_VERIFY_MODE`):

| 变量 | 说明 | 默认 |
|------|------|------|
| `AZRESTORE_VERIFY?` | `local`(解冻+下载+解密,逐文件比对 hash)/ `off` | `off` |

> 网络代理:还原同样遵循标准 `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`(含小写)。

> ⚠️ 凭据建议用 K8s Secret / Docker secret 挂载为文件并用 `*_FILE` 读取,而非明文 env。
