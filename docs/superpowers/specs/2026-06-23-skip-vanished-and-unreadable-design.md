# 设计:备份过程中跳过丢失/打不开的文件,并在最后终结结构

日期:2026-06-23
状态:已获用户批准设计方向(spec 评审第 2 轮)

## 背景与问题

备份是一次**流式遍历**,不做文件系统快照(无 VSS/snapshot)。`FileScanner` 用 `yield return`
惰性枚举,而文件内容真正被读取是在后面的打包阶段(`PackBuilder.Build` 调用延迟的
`Func<Stream>` opener)。因此从"文件被 stat 登记"到"内容被读进 pack"之间存在时间窗口(TOCTOU)。

当前行为的缺陷:
- 文件在登记后、打包前被**删除** → `File.OpenRead` 抛 `FileNotFoundException`,异常一路抛出,
  **整个备份任务失败**(`PackBuilder.cs:62` 在 try 内,经 finally 清理后上抛,无逐成员容错)。
- 目录因权限无法枚举(`UnauthorizedAccessException`)时被**静默吞掉**(`FileScanner.cs:43-46` `yield break`),
  用户无从得知。
- 目录在枚举瞬间被删 → `Directory.EnumerateFileSystemEntries` 抛 `DirectoryNotFoundException`,
  当前 `Walk` 的 catch **只捕 `UnauthorizedAccessException`**,故会**直接崩溃**。
- 扫描阶段算哈希时读文件(`ChangeDetector` 的 `computeHash` 回调,`BackupRunner.cs:101`)同样可能抛异常并使任务崩溃。
- `FileScanner.cs:67` 的 `info.Length` 对刚消失的文件会抛异常(第四个接触点)。

## 目标

1. 文件/文件夹**丢失**(已删除)→ 静默跳过,不报错,从本次快照结构中**省略**。
2. 文件/文件夹**打不开**(权限/锁定/占用)→ 跳过,但在**最终报告**中报错;结构里**沿用上次快照的版本**;
   若该路径无历史版本可沿用,则只能省略,但仍要报错。
3. 备份结束后再写完整结构(快照)——因为开始时无法知道哪些文件会被跳过,完整列表只有打包后才能确定。
4. 单个文件的失败**绝不**导致整任务崩溃(成功带警告语义)。
5. 任何情况下**不得产生悬空引用**:快照里的文件 entry 其 hash 必须指向仓库中实际存在的内容。

非目标(本次不做):
- 同内容兄弟文件的 opener 回退(用户明确不纳入)。其后果在「去重边界」一节显式说明并保证安全。
- 处理"扫描期间文件被修改导致哈希/内容不一致"的 TOCTOU(超出本次范围)。
- 可配置失败阈值(确定采用固定的"成功带警告")。

## 方案

采用**方案 A —— 延迟终结结构**:保留惰性流式架构,在所有接触文件的地方捕获失败并分类记录,
等所有 pack 打完、确定"哪些内容未能进入仓库"后,再终结 `entries` 并写快照。

(已否决方案 B「扫描期 eager 读全部内容」:变更文件被读两次、丢失流式优势、I/O 翻倍,
且并不能消除 TOCTOU。)

### 失败分类规则

| 异常类型 | 含义 | 处理 |
|---|---|---|
| `FileNotFoundException` / `DirectoryNotFoundException` | **Missing**(已删除) | 静默跳过,结构中省略,不计入警告 |
| `UnauthorizedAccessException` / `IOException`(含共享冲突/锁定) | **Unreadable**(打不开) | 跳过 + 记入报告警告;结构沿用上次版本(无则省略,仍报警告) |

其它异常类型(如存储异常、`OutOfMemoryException`)**不在此捕获**,继续上抛(视为真正的程序/存储错误)。

### 数据模型(修正 B1 的核心)

失败必须**按路径**追踪,不能只按 hash,否则会误伤与之去重的可读文件并产生悬空引用。

- `failedContent`:`Dictionary<string hash, SkipReason>` —— 记录**哪些内容 hash 未能进入仓库**
  (opener 失败 / 扫描算哈希失败)。这是"内容是否可用"的权威集合。
- `failedPath`:`Dictionary<string path, SkipReason>` —— 记录**触发失败的具体路径**及其原因
  (用于决定该路径是否报警告:Missing 静默 / Unreadable 报警)。
- opener 在 `BackupRunner` 里按 hash 去重注册(`openers[hash]`),但注册时**同时记下其绑定路径**
  (`hash → boundPath`),失败时即可同时写 `failedContent[hash]` 与 `failedPath[boundPath]`。

### 三个接触点的改动

1. **`FileScanner`**(`src/AzureBackup.Core/Scan/FileScanner.cs`)
   - `Walk` 的 catch 从只捕 `UnauthorizedAccessException` **扩展**为:
     - `DirectoryNotFoundException` / `FileNotFoundException` → 静默跳过该子树(Missing)。
     - `UnauthorizedAccessException` / `IOException` → 跳过该子树并产出一条 **Unreadable 警告**。
   - `FileInfo`/`info.Length`(`FileScanner.cs:50,67`)读取失败同样按上表分类(单个 entry 跳过,不中断遍历)。
   - 通过「警告通道」产出 Unreadable 警告(见下)。

2. **扫描阶段算哈希**(`BackupRunner.RunAsync` 主循环 `ChangeDetector.Detect` 的 `computeHash` 回调)
   - 仅 New/Modified/ForceHash 才会走 `computeHash()` 读文件;Unchanged 走快路径(`ChangeDetector.cs:37-38`)**不接触文件**,不受影响。
   - 回调读取失败时按分类规则处理:Missing → 该文件不进 `entries`、不登记 opener;
     Unreadable → 记 `failedPath`,若 `prior` 含该路径则改加入"沿用上次版本"的 entry,否则跳过 + 警告。
   - **控制流**:捕获须包住 `Detect` 调用,失败即 `continue`,确保该 entry 不被 `entries.Add`(`BackupRunner.cs:104`)。
   - `GetMode`(`BackupRunner.cs:252`)已 try/catch 返回 0,安全,无需改动。

3. **打包读取**(`PackBuilder.Build` 的 `m.Open()`,`src/AzureBackup.Core/Pack/PackBuilder.cs`)
   - **新增可选的"容错成员跳过"行为**(见下「PackBuilder 契约」),仅捕获上表的 FS 异常集合:
     某成员 `m.Open()`/拷贝失败时记录该成员 hash 跳过,pack 用剩余成员照常构建。
   - `Build` 把"失败成员 hash 列表"返回给调用方,`BackupRunner` 据此填 `failedContent`/`failedPath`。
   - 若某 pack 成员**全部**失败 → 不产卷、不进索引(packsCreated 不计)。

### PackBuilder 契约变化(修正 M4:保护 Compactor)

- `BuiltPack` 新增 `IReadOnlyList<(string Hash, ...)> FailedMembers`(或并列返回结构);`Entries`/`spans` **只含成功成员**。
- **容错跳过仅在备份路径启用**。`PackBuilder.Build` 增加一个开关(如 `bool tolerateMemberFailures`,默认 `false`)。
  - 备份路径(`BackupRunner`)传 `true`。
  - **压实路径(`Compactor.cs:57`)保持 `false`**:Compactor 的成员是 `() => new MemoryStream(bytes)`
    (`Compactor.cs:51`,从已下载解密的 plaintext 切片),**不接触文件系统**,正常不会抛 FS 异常;
    一旦异常即为真错误,**必须上抛**——压实期跳过 live 成员等于永久丢历史内容,绝不允许。
- 两个调用点(`BackupRunner.cs:127`、`Compactor.cs:57`)随 `BuiltPack` 形状变化**同步更新**编译。

### 终结结构(修正 B1:按路径决定处置,杜绝悬空引用)

打包结束后,`failedContent`(hash 级)+ `failedPath`(路径级)已就绪。遍历扫描期登记的 `entries`(文件项):

对每个文件 entry(其内容 hash = H,路径 = P):
- 若 **H ∈ `failedContent`**(该内容未能进入仓库):
  - 若 `prior[P]` 存在 → 用上次版本替换该 entry(沿用 `prior` 的 hash;该 hash 内容已在仓库、且被新快照引用,
    GC 的 `ReachableHashesAsync` 会保活,见 `SnapshotStore.cs`)。
  - 否则 → 删除该 entry(省略)。
  - 警告:若 P ∈ `failedPath` 且原因为 Unreadable → 报警告;若为 Missing → 静默。
    **去重边界**:若 P 不是触发失败的那个 boundPath(即 P 与某丢失/不可读文件去重共享 H,而 P 本身可能仍可读),
    由于本设计不做兄弟回退,H 的内容确实未被读入仓库,P 只能沿用上次版本或省略;此时**一律报 Unreadable 警告**
    (而非静默),以免在 dedup 边界静默丢内容。此举保证不产生悬空引用(目标 5)。
- 若 **H ∉ `failedContent`** → entry 原样保留(内容已在仓库或已成功打包)。

`SnapshotEntry`(`src/AzureBackup.Core/Repo/TreeBuilder.cs`,字段 RelativePath/IsDirectory/Size/Mtime/Mode/Hash)
的"沿用"构造来源:`prior[P]` 需提供 Size/Mtime/**Mode**/Hash。`PriorFile`(`ChangeDetector.cs:11`)当前仅
Size/Mtime/Hash,需**补 Mode**:扩展 `PriorFile` 增 `int Mode`,并在 `LoadPriorAsync`(`BackupRunner.cs:228-230`)
从上次 `SnapshotFile.Mode`(`SnapshotStore.cs`)取值填入(当前被丢弃)。

`TreeBuilder.Build` 内部用 `SortedDictionary` 重组目录树,对 `entries` 顺序不敏感,故终结阶段的删除/替换安全;
某文件 entry 被删后若父目录无其它文件,会留下空目录 entry——可接受(空目录本就会被记录)。

终结完成后才 `SnapshotStore.WriteAsync` 写快照(保持现状:快照在 retention/GC 之前的最后一步,`BackupRunner.cs:146`)。

### 警告通道

`FileScanner.Scan` 目前是纯 `IEnumerable<ScannedEntry>`。增加警告产出的最简方式:给 `FileScanner`
构造或 `Scan` 传入一个 `ICollection<SkipWarning>`(或 `Action<SkipWarning>`),scanner 跳过 Unreadable
子树/entry 时往里追加;`BackupRunner` 提供该集合并并入报告。

`SkipWarning` 记录:`Path`、`Reason`(枚举:`Unreadable`)、`Detail`(异常消息,可选)。
Missing 不产生警告(目标 1)。(注:Reason 枚举当前只 Unreadable 一值;保留枚举以便日后扩展诊断级别。)

### 报告与通知

- `BackupReport`(`BackupRunner.cs:35-48`,positional record)**追加带默认值的字段**(置于 `Skipped` 之后,
  避免打乱现有位置参数;现有构造点 `BackupRunner.cs:69,114,153` 无需逐一改写):
  - `int SkippedMissing = 0` — 静默省略的文件数。
  - `int SkippedUnreadable = 0` — 因打不开被跳过/沿用的文件/目录数。
  - `IReadOnlyList<SkipWarning>? Warnings = null` — 路径 + 原因清单(仅 Unreadable)。
  - **计数语义**:去重边界(场景 5)中,被沿用/省略的兄弟路径 P 计入 `SkippedUnreadable`;
    其触发失败的 boundPath 若为 Missing 则计入 `SkippedMissing`——即一次逻辑失败可能同时触及两个计数器。
- 运行状态:**成功带警告**。任务仍返回成功(不抛异常、退出码 0),与 `Program.cs` 现有
  `ShouldFire(events, success:!jobFailed)` 一致;通知 title 保持 `OK`。
- `Program.cs:69` 汇总行追加 `skipMissing=… skipUnreadable=…`;`Warnings` 非空时把清单附到 summary/通知 body。
- `DryRun`(`BackupRunner.cs:113-114`,扫描后即 return,不打包):仅能获知**扫描期**(scanner 枚举 + `computeHash`)
  的跳过;opener 阶段失败无从得知。故 DryRun **不做完整终结**,报告中的跳过数仅为**下界**,需在文案/字段语义上说明。

## 受影响文件

- `src/AzureBackup.Core/Scan/FileScanner.cs` — 枚举/`info.Length` 容错(扩 catch)+ 警告通道。
- `src/AzureBackup.Core/Pack/PackBuilder.cs` — 可选容错成员跳过 + `BuiltPack.FailedMembers`。
- `src/AzureBackup.Core/Backup/BackupRunner.cs` — 分类、扫描接触点容错、`failedContent`/`failedPath`、终结结构、报告字段、opener 绑定路径。
- `src/AzureBackup.Core/Scan/ChangeDetector.cs` — `PriorFile` 增 `Mode`。
- `src/AzureBackup.Core/Repo/Compactor.cs` — 随 `BuiltPack` 形状同步编译;显式 `tolerateMemberFailures:false`。
- `src/AzureBackup.Backup/Program.cs` — 汇总行/通知 body 加警告。
- 新增 `SkipWarning` / `SkipReason` 类型(放 `Scan` 命名空间)。

## 测试

用现有 `InMemoryBlobStore` + 临时目录;失败用可抛异常的 `Func<Stream>` 模拟。

1. **扫描中途删文件**:opener 抛 `FileNotFoundException` → 任务不崩,该文件从快照省略,无警告,`SkippedMissing+1`。
2. **扫描中途删目录**:枚举抛 `DirectoryNotFoundException` → 子树静默跳过,无警告。
3. **文件打不开(锁定)**:opener 抛 `IOException` → 跳过 + 1 警告;有 prior → 快照沿用上次 hash(且含正确 Mode);无 prior → 省略 + 仍有警告。
4. **目录打不开(权限)**:枚举抛 `UnauthorizedAccessException` → 子树跳过 + 警告。
5. **去重边界**:两个同内容文件 A、B 共享 hash H,A(boundPath)丢失;验证 B 不产生悬空引用——B 沿用 prior 或省略,且报 Unreadable 警告;H 不在任何卷/索引。
6. **整 pack 全失败**:该 pack 不产卷、不进索引;其它 pack 正常。
7. **Compactor 不容错**:Compactor 路径成员异常应上抛(`tolerateMemberFailures:false`),不静默丢 live 成员。
8. **报告/退出码**:有 Unreadable 警告时仍 `success`(退出码 0),计数与清单正确。
9. **其它异常**(存储异常等)继续上抛,不被吞。
10. **DryRun**:扫描期删/锁文件被统计为跳过下界,不写快照不打包。

## 风险与权衡

- 沿用上次版本依赖 `prior` 完整(含 Mode);若上次快照本身就缺该文件,则只能省略。
- 去重边界(场景 5)下,可读的兄弟文件也会被沿用/省略(无兄弟回退的必然后果),但通过强制告警避免静默丢失。
- "成功带警告"意味着长期打不开的文件会一直只在历史快照里;由用户通过报告关注。
