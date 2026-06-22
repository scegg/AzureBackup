# 备份跳过丢失/打不开文件并终结结构 — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 备份过程中文件/文件夹丢失则静默跳过省略,打不开则跳过并报警告且沿用上次版本,所有失败均不使任务崩溃;结构在打包后终结再写快照。

**Architecture:** 保留惰性流式架构(方案 A)。在三个接触点(FileScanner 枚举、扫描算哈希、PackBuilder 打包读取)按异常分类捕获,记录 `failedContent`(hash 级)+ `failedPath`(路径级);打包后遍历 entries 终结(沿用 prior / 省略),最后写快照。压实路径(Compactor)显式不启用容错。

**Tech Stack:** C# / .NET 10、xUnit、现有 `InMemoryBlobStore`。

设计来源:`docs/superpowers/specs/2026-06-23-skip-vanished-and-unreadable-design.md`

---

## 文件结构

- 新增 `src/AzureBackup.Core/Scan/SkipWarning.cs` — `SkipReason` 枚举 + `SkipWarning` record。
- 改 `src/AzureBackup.Core/Scan/ChangeDetector.cs` — `PriorFile` 增 `int Mode`。
- 改 `src/AzureBackup.Core/Scan/FileScanner.cs` — 扩展 catch + 警告 sink。
- 改 `src/AzureBackup.Core/Pack/PackBuilder.cs` — 容错成员跳过开关 + `BuiltPack.FailedMembers`。
- `src/AzureBackup.Core/Repo/Compactor.cs` — **大概率无需改动**:它只读 `built.Entries/VolumePaths/CiphertextSize`(字段未改名),不构造 `BuiltPack`,且默认 `tolerateMemberFailures:false`。仅在构建报错时同步。
- 改 `src/AzureBackup.Core/Backup/BackupRunner.cs` — 分类、终结结构、报告字段、opener 绑定路径、`LoadPriorAsync` 带 Mode。
- 改 `src/AzureBackup.Backup/Program.cs` — 汇总行/通知 body 加跳过计数与警告。
- 测试:`tests/AzureBackup.Core.Tests/` 下新增/扩展 `SkipWarningTests`? 不必;在 `FileScannerTests`、`PackTests`、`BackupRunnerTests` 内加用例。

通用命令:
- 构建:`dotnet build AzureBackup.slnx -c Debug`
- 全测:`dotnet test AzureBackup.slnx -c Debug`
- 单测:`dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "FullyQualifiedName~<Name>"`

---

## Task 1: SkipReason + SkipWarning 类型

**Files:**
- Create: `src/AzureBackup.Core/Scan/SkipWarning.cs`

- [ ] **Step 1: 创建类型文件**

```csharp
namespace AzureBackup.Core.Scan;

/// <summary>为什么一个文件/目录被跳过。Missing 静默;Unreadable 进报告。</summary>
public enum SkipReason
{
    /// <summary>文件/目录已删除(FileNotFound/DirectoryNotFound)——静默跳过。</summary>
    Missing,
    /// <summary>打不开(权限/锁定/IO)——跳过但报警告。</summary>
    Unreadable,
}

/// <summary>一条跳过警告(仅 Unreadable 产生),用于最终报告。</summary>
public sealed record SkipWarning(string Path, SkipReason Reason, string? Detail);
```

- [ ] **Step 2: 构建确认编译**

Run: `dotnet build AzureBackup.slnx -c Debug`
Expected: 成功(无引用方,纯新增类型)。

- [ ] **Step 3: 提交**

```bash
git add src/AzureBackup.Core/Scan/SkipWarning.cs
git commit -m "feat(scan): 新增 SkipReason/SkipWarning 跳过分类类型"
```

---

## Task 2: PriorFile 增 Mode

终结时"沿用上次版本"需要 Mode 构造 `SnapshotEntry`,而 `PriorFile` 目前缺 Mode。

**Files:**
- Modify: `src/AzureBackup.Core/Scan/ChangeDetector.cs:11`
- Modify: `src/AzureBackup.Core/Backup/BackupRunner.cs:228-230`(`LoadPriorAsync` 构造)

- [ ] **Step 1: 改 PriorFile 加 Mode 字段**

`ChangeDetector.cs` 第 11 行:

```csharp
/// <summary>A file's metadata as recorded by the previous snapshot.</summary>
public sealed record PriorFile(long Size, DateTimeOffset Mtime, string Hash, int Mode);
```

- [ ] **Step 2: 构建确认失败点**

Run: `dotnet build AzureBackup.slnx -c Debug`
Expected: FAIL —— `BackupRunner.cs` 中 `new PriorFile(f.Size, f.Mtime, f.Hash)` 缺参数。

- [ ] **Step 3: 修复 LoadPriorAsync 带入 Mode**

`BackupRunner.cs` 的 `LoadPriorAsync` 内(约 230 行):

```csharp
            if (!f.IsDirectory && f.Hash is not null)
                prior[f.Path] = new PriorFile(f.Size, f.Mtime, f.Hash, f.Mode);
```

- [ ] **Step 4: 构建确认通过**

Run: `dotnet build AzureBackup.slnx -c Debug`
Expected: 成功。`ChangeDetector.Detect` 不受影响(只读 Size/Mtime/Hash)。

- [ ] **Step 5: 全测确认无回归**

Run: `dotnet test AzureBackup.slnx -c Debug`
Expected: 全绿(现有用例不依赖 Mode)。

- [ ] **Step 6: 提交**

```bash
git add src/AzureBackup.Core/Scan/ChangeDetector.cs src/AzureBackup.Core/Backup/BackupRunner.cs
git commit -m "feat(scan): PriorFile 携带 Mode,LoadPriorAsync 保留上次模式位"
```

---

## Task 3: FileScanner 扩展异常容错 + 警告 sink

枚举目录、读取 `FileInfo.Length` 时,Missing 静默跳过子树/项,Unreadable 跳过并产警告。

**Files:**
- Modify: `src/AzureBackup.Core/Scan/FileScanner.cs`
- Test: `tests/AzureBackup.Core.Tests/FileScannerTests.cs`

- [ ] **Step 1: 写失败测试(权限拒绝目录 → 子树跳过 + 警告)**

`FileScannerTests.cs` 末尾追加(Unix-only,Windows 跳过):

```csharp
    [Fact]
    public void Unreadable_directory_is_skipped_with_warning()
    {
        if (OperatingSystem.IsWindows()) return; // chmod 语义仅 Unix

        Touch("ok.txt");
        Touch("secret/inside.txt");
        string secret = Path.Combine(_root, "secret");
        File.SetUnixFileMode(secret, UnixFileMode.None); // 000:不可枚举
        try
        {
            var warnings = new List<SkipWarning>();
            var paths = new FileScanner(GitignoreMatcher.Empty)
                .Scan(_root, warnings).Select(e => e.RelativePath).ToHashSet();

            Assert.Contains("ok.txt", paths);
            Assert.DoesNotContain("secret/inside.txt", paths);
            Assert.Contains(warnings, w => w.Reason == SkipReason.Unreadable && w.Path.Contains("secret"));
        }
        finally
        {
            File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
```

- [ ] **Step 2: 运行确认编译失败(Scan 无 warnings 重载)**

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "Unreadable_directory"`
Expected: 编译 FAIL —— `Scan` 不接受第二参数。

- [ ] **Step 3: 实现 — Scan 增加可选警告 sink,Walk 扩展 catch**

`FileScanner.cs` 替换 `Scan` 与 `Walk`:

```csharp
    public IEnumerable<ScannedEntry> Scan(string root, ICollection<SkipWarning>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"source root not found: {fullRoot}");

        foreach (ScannedEntry e in Walk(fullRoot, fullRoot, warnings))
            yield return e;
    }

    private IEnumerable<ScannedEntry> Walk(string dir, string root, ICollection<SkipWarning>? warnings)
    {
        IEnumerable<string> children;
        try
        {
            // 立即物化,把枚举期异常在此捕获(而非延迟到 foreach)。
            children = Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            yield break; // Missing:静默跳过该子树
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            warnings?.Add(new SkipWarning(Rel(root, dir), SkipReason.Unreadable, ex.Message));
            yield break;
        }

        foreach (string path in children.OrderBy(p => p, StringComparer.Ordinal))
        {
            ScannedEntry? entry;
            bool isDir;
            try
            {
                var info = new FileInfo(path);
                isDir = (info.Attributes & FileAttributes.Directory) != 0;
                bool isSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                string rel = Rel(root, path);

                if (_exclude.IsIgnored(rel, isDir)) continue;

                if (isDir && !isSymlink)
                    entry = new ScannedEntry(rel, true, 0, ToUtc(info.LastWriteTimeUtc), false);
                else
                {
                    long size = isSymlink ? 0 : info.Length;
                    entry = new ScannedEntry(rel, false, size, ToUtc(info.LastWriteTimeUtc), isSymlink);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // Missing:该项消失,静默跳过
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                warnings?.Add(new SkipWarning(Rel(root, path), SkipReason.Unreadable, ex.Message));
                continue;
            }

            yield return entry;
            if (entry.IsDirectory)
                foreach (ScannedEntry e in Walk(path, root, warnings))
                    yield return e;
        }
    }
```

> 说明:原 `Walk` 在 `isDir && !isSymlink` 分支递归;此处保持等价(先 yield 目录项,再递归)。符号链接与文件走同一条 `yield return entry`。

- [ ] **Step 4: 运行新测试确认通过**

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "Unreadable_directory"`
Expected: PASS。

- [ ] **Step 5: 全测确认旧用例无回归**

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "FileScanner"`
Expected: 全绿(原 4 个 + 新 1 个;原 `Scan(root)` 单参调用仍可用,因 warnings 可选)。

- [ ] **Step 6: 提交**

```bash
git add src/AzureBackup.Core/Scan/FileScanner.cs tests/AzureBackup.Core.Tests/FileScannerTests.cs
git commit -m "feat(scan): FileScanner 容错枚举失败,Unreadable 产出警告"
```

---

## Task 4: PackBuilder 容错成员跳过 + FailedMembers

**Files:**
- Modify: `src/AzureBackup.Core/Pack/PackBuilder.cs`
- Verify(大概率不改): `src/AzureBackup.Core/Repo/Compactor.cs:57,67`
- Test: `tests/AzureBackup.Core.Tests/PackTests.cs`

- [ ] **Step 1: 写失败测试(一个成员 opener 抛异常 → 被跳过,其余成功,FailedMembers 含其 hash)**

`PackTests.cs` 追加(参考该文件现有 PackBuilder 用法的 using/字段;若无临时目录则自建):

```csharp
    [Fact]
    public void Build_tolerates_failing_member_and_reports_it()
    {
        if (!XzCompressor.IsAvailable()) return;
        string work = Path.Combine(Path.GetTempPath(), "azbk-pb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            byte[] key = ContentKey.Generate();
            var members = new List<PackMember>
            {
                new("hashGOOD", () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("alpha"))),
                new("hashBAD",  () => throw new FileNotFoundException("gone")),
                new("hashGOOD2",() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("beta"))),
            };

            BuiltPack built = new PackBuilder(work, 1024)
                .Build("pid", CompressionCodec.Store, key, members, tolerateMemberFailures: true);

            Assert.Contains("hashGOOD", built.Entries.Keys);
            Assert.Contains("hashGOOD2", built.Entries.Keys);
            Assert.DoesNotContain("hashBAD", built.Entries.Keys);
            Assert.Contains("hashBAD", built.FailedMembers.Select(f => f.Hash));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Build_without_tolerance_rethrows_member_failure()
    {
        string work = Path.Combine(Path.GetTempPath(), "azbk-pb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var members = new List<PackMember>
            {
                new("h", () => throw new FileNotFoundException("gone")),
            };
            Assert.ThrowsAny<IOException>(() =>
                new PackBuilder(work, 1024).Build("pid", CompressionCodec.Store, ContentKey.Generate(), members));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
```

> 注:`FileNotFoundException` 派生自 `IOException`,故 `ThrowsAny<IOException>` 可捕获默认(不容错)路径的上抛。

- [ ] **Step 2: 运行确认编译失败**

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "Build_tolerates_failing_member"`
Expected: 编译 FAIL —— `Build` 无 `tolerateMemberFailures` 参数;`BuiltPack` 无 `FailedMembers`。

- [ ] **Step 3: 实现 — BuiltPack 加 FailedMembers,Build 加开关**

`PackBuilder.cs`:`BuiltPack` 记录加字段(带默认便于其它构造):

```csharp
public sealed record BuiltPack(
    string PackId,
    CompressionCodec Codec,
    IReadOnlyList<string> VolumePaths,
    long PlaintextSize,
    long CiphertextSize,
    IReadOnlyDictionary<string, ContentSpan> Entries,
    IReadOnlyList<FailedMember> FailedMembers);

/// <summary>打包时未能读取的成员(仅容错模式下产生)。</summary>
public readonly record struct FailedMember(string Hash, SkipReason Reason, string? Detail);
```

`Build` 签名与成员循环:

```csharp
    public BuiltPack Build(string packId, CompressionCodec codec, byte[] contentKey,
        IReadOnlyList<PackMember> members, bool tolerateMemberFailures = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(packId);
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(members);

        Directory.CreateDirectory(_workDir);
        string plaintextPath = Path.Combine(_workDir, packId + ".plain");
        string compressedPath = Path.Combine(_workDir, packId + ".comp");
        var entries = new Dictionary<string, ContentSpan>(StringComparer.Ordinal);
        var failed = new List<FailedMember>();

        try
        {
            long offset = 0;
            using (var pt = new FileStream(plaintextPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (PackMember m in members)
                {
                    if (entries.ContainsKey(m.Hash)) continue;
                    Stream? src = null;
                    try { src = m.Open(); }
                    catch (Exception ex) when (tolerateMemberFailures && IsSkippable(ex))
                    {
                        failed.Add(new FailedMember(m.Hash, Classify(ex), ex.Message));
                        continue;
                    }
                    try
                    {
                        long copied = Copy(src, pt);
                        entries[m.Hash] = new ContentSpan(offset, copied);
                        offset += copied;
                    }
                    catch (Exception ex) when (tolerateMemberFailures && IsSkippable(ex))
                    {
                        failed.Add(new FailedMember(m.Hash, Classify(ex), ex.Message));
                        // 已写入 pt 的部分字节不记 span;后续成员 offset 从当前 pt 位置续。
                        offset = pt.Position;
                        continue;
                    }
                    finally { src?.Dispose(); }
                }
            }
            long plaintextSize = offset;

            using (Stream input = File.OpenRead(plaintextPath))
            using (var output = new FileStream(compressedPath, FileMode.Create, FileAccess.Write, FileShare.None))
                Compressors.For(codec).Compress(input, output);

            var writer = new VolumeWriter(_workDir, packId, _volumeSize);
            using (writer)
            using (Stream compressed = File.OpenRead(compressedPath))
                SegmentedCipher.Encrypt(contentKey, compressed, writer, _segmentSize);

            return new BuiltPack(packId, codec, [.. writer.VolumePaths], plaintextSize,
                writer.TotalBytesWritten, entries, failed);
        }
        finally
        {
            TryDelete(plaintextPath);
            TryDelete(compressedPath);
        }
    }

    private static bool IsSkippable(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException
              or UnauthorizedAccessException or IOException;

    private static SkipReason Classify(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException
            ? SkipReason.Missing : SkipReason.Unreadable;
```

> `FailedMember`/`SkipReason` 在 `Scan` 命名空间;`PackBuilder.cs` 顶部加 `using AzureBackup.Core.Scan;`。
> 边界:成员中途拷贝失败时把已落盘的脏字节排除在 span 之外(`offset = pt.Position` 后续 span 会覆盖在其后,
> 脏字节成为不被任何 span 引用的空洞——可接受,因为不影响其它成员的 offset/size 正确性)。

- [ ] **Step 4: 修复 Compactor 调用点编译(不启用容错)**

`Compactor.cs:57` 保持默认 `tolerateMemberFailures:false`(无需传参,默认即 false);若 `BuiltPack` 解构/字段访问处编译报错则同步。一般仅需确认 `built.Entries`、`built.VolumePaths`、`built.CiphertextSize` 仍可用(字段未改名),`FailedMembers` 在压实路径恒为空。

Run: `dotnet build AzureBackup.slnx -c Debug`
Expected: 成功(Compactor 行为不变)。

- [ ] **Step 5: 运行新测试确认通过**

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "Build_tolerates_failing_member|Build_without_tolerance"`
Expected: 两个均 PASS。

- [ ] **Step 6: 全测无回归**

Run: `dotnet test AzureBackup.slnx -c Debug`
Expected: 全绿(含 CompactorTests)。

- [ ] **Step 7: 提交**

```bash
git add src/AzureBackup.Core/Pack/PackBuilder.cs src/AzureBackup.Core/Repo/Compactor.cs tests/AzureBackup.Core.Tests/PackTests.cs
git commit -m "feat(pack): PackBuilder 可选容错成员跳过 + FailedMembers;Compactor 维持严格"
```

---

## Task 5: BackupRunner 接线 — 分类、终结结构、报告字段

**Files:**
- Modify: `src/AzureBackup.Core/Backup/BackupRunner.cs`
- Test: `tests/AzureBackup.Core.Tests/BackupRunnerTests.cs`

- [ ] **Step 1: 写集成失败测试(打不开的变更文件 → 沿用上次版本 + 警告 + 成功)**

`BackupRunnerTests.cs` 追加(Unix-only):

```csharp
    [Fact]
    public async Task Unreadable_changed_file_carries_forward_prior_with_warning()
    {
        if (!XzCompressor.IsAvailable() || OperatingSystem.IsWindows()) return;

        Write("a.txt", "v1");
        var store = new InMemoryBlobStore();
        await BackupRunner.RunAsync(store, Password, Options());           // run1:备份 v1

        // 改内容(变更 size/mtime 触发读取),随后 chmod 000 使其打不开。
        System.Threading.Thread.Sleep(10);
        Write("a.txt", "v2 changed");
        string full = Path.Combine(_src, "a.txt");
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(5));
        File.SetUnixFileMode(full, UnixFileMode.None);
        try
        {
            BackupReport r = await BackupRunner.RunAsync(store, Password, Options());

            Assert.NotNull(r.SnapshotId);                                  // 成功
            Assert.Equal(1, r.SkippedUnreadable);
            Assert.NotNull(r.Warnings);
            Assert.Contains(r.Warnings!, w => w.Path.Contains("a.txt"));
            // 沿用:最新快照里 a.txt 仍能还原出 v1。
            await AssertRecovers(store, "a.txt", "v1");
        }
        finally
        {
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Unreadable_new_file_is_omitted_with_warning()
    {
        if (!XzCompressor.IsAvailable() || OperatingSystem.IsWindows()) return;

        Write("keep.txt", "ok");
        Write("locked.txt", "secret");
        string locked = Path.Combine(_src, "locked.txt");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var store = new InMemoryBlobStore();
            BackupReport r = await BackupRunner.RunAsync(store, Password, Options());

            Assert.NotNull(r.SnapshotId);
            Assert.True(r.SkippedUnreadable >= 1);
            Assert.Contains(r.Warnings!, w => w.Path.Contains("locked.txt"));
            await AssertRecovers(store, "keep.txt", "ok");
            await AssertMissing(store, "locked.txt"); // 省略:无 prior 可沿用
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
```

> 去重边界(spec 场景 5)的精确语义——`failedContent` 含 hash H、某存活 entry 的路径 B 引用 H 但 B 非
> boundPath → B 沿用/省略且补 Unreadable 警告、不悬空——**无法用 chmod 稳定复现**(chmod 让 boundPath 在
> 扫描期就失败,于是它从不登记 opener,H 也不进 `failedContent`)。该分支改由 **Task 5 Step 3.5 的终结
> 纯函数单测**确定性覆盖,见下。这里两个集成测试覆盖最常见的扫描期 Unreadable 路径。

并在 helpers 区追加:

```csharp
    private static async Task AssertMissing(InMemoryBlobStore store, string path)
    {
        Repository repo = await Repository.OpenAsync(store, Password);
        var list = await SnapshotStore.ReadSnapshotListAsync(store, repo.MasterKey);
        var root = await SnapshotStore.ReadRootAsync(store, repo.MasterKey, list.OrderByDescending(s => s.CreatedAtUtc).First().Id);
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, repo.MasterKey, root.RootTree))
            Assert.False(f.Path == path && !f.IsDirectory, $"{path} should be omitted");
    }
```

- [ ] **Step 2: 运行确认编译失败(report 无 SkippedUnreadable/Warnings)**

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "Unreadable_changed_file|Unreadable_new_file"`
Expected: 编译 FAIL。

- [ ] **Step 3: 实现 — BackupReport 加字段**

`BackupRunner.cs` 的 `BackupReport`(在 `Skipped` 之后追加,带默认值):

```csharp
public sealed record BackupReport(
    string? SnapshotId,
    int Files,
    int Directories,
    int NewOrModified,
    int Unchanged,
    int PacksCreated,
    int VolumesUploaded,
    long UploadedBytes,
    int SnapshotsDeleted,
    int PacksDeleted,
    int PacksCompacted,
    bool DryRun,
    bool Skipped = false,
    int SkippedMissing = 0,
    int SkippedUnreadable = 0,
    IReadOnlyList<SkipWarning>? Warnings = null);
```

- [ ] **Step 3.5: 终结纯函数单测(确定性覆盖去重边界 / 沿用 / 省略)**

新建 `tests/AzureBackup.Core.Tests/FinalizeEntriesTests.cs`。注:`FinalizeEntries` 为 `internal`,
测试程序集需能访问——`AzureBackup.Core` 若未对测试程序集开放 `InternalsVisibleTo`,则在
`src/AzureBackup.Core/AzureBackup.Core.csproj` 加
`<ItemGroup><InternalsVisibleTo Include="AzureBackup.Core.Tests" /></ItemGroup>`(执行时先确认是否已存在)。

```csharp
using AzureBackup.Core.Backup;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Scan;
using Xunit;

namespace AzureBackup.Core.Tests;

public sealed class FinalizeEntriesTests
{
    private static SnapshotEntry File(string path, string hash)
        => new(path, false, 1, DateTimeOffset.UnixEpoch, 0, hash);

    [Fact]
    public void Carries_forward_when_prior_exists()
    {
        var entries = new List<SnapshotEntry> { File("a.txt", "Hnew") };
        var failedContent = new Dictionary<string, SkipReason> { ["Hnew"] = SkipReason.Unreadable };
        var failedPath = new Dictionary<string, SkipReason> { ["a.txt"] = SkipReason.Unreadable };
        var prior = new Dictionary<string, PriorFile> { ["a.txt"] = new(9, DateTimeOffset.UnixEpoch, "Hold", 0o644) };
        var miss = new HashSet<string>(); var unr = new HashSet<string>(); var warn = new List<SkipWarning>();

        var result = BackupRunner.FinalizeEntries(entries, failedContent, failedPath, prior, miss, unr, warn);

        Assert.Single(result);
        Assert.Equal("Hold", result[0].Hash);          // 沿用上次内容
        Assert.Equal(0o644, result[0].Mode);           // Mode 取自 prior
        Assert.Contains("a.txt", unr);
        Assert.Empty(miss);
    }

    [Fact]
    public void Omits_when_no_prior()
    {
        var entries = new List<SnapshotEntry> { File("a.txt", "Hnew") };
        var failedContent = new Dictionary<string, SkipReason> { ["Hnew"] = SkipReason.Unreadable };
        var failedPath = new Dictionary<string, SkipReason> { ["a.txt"] = SkipReason.Unreadable };
        var prior = new Dictionary<string, PriorFile>();
        var miss = new HashSet<string>(); var unr = new HashSet<string>(); var warn = new List<SkipWarning>();

        var result = BackupRunner.FinalizeEntries(entries, failedContent, failedPath, prior, miss, unr, warn);

        Assert.Empty(result);                          // 省略
        Assert.Contains("a.txt", unr);
    }

    [Fact]
    public void Dedup_sibling_carries_or_omits_and_warns_without_dangling()
    {
        // A 是 boundPath(失败原因 Missing),B 与 A 同 hash H 但非 boundPath。
        var entries = new List<SnapshotEntry> { File("A.txt", "H"), File("B.txt", "H") };
        var failedContent = new Dictionary<string, SkipReason> { ["H"] = SkipReason.Missing };
        var failedPath = new Dictionary<string, SkipReason> { ["A.txt"] = SkipReason.Missing }; // 仅 boundPath
        var prior = new Dictionary<string, PriorFile>();                                         // 都无 prior
        var miss = new HashSet<string>(); var unr = new HashSet<string>(); var warn = new List<SkipWarning>();

        var result = BackupRunner.FinalizeEntries(entries, failedContent, failedPath, prior, miss, unr, warn);

        Assert.Empty(result);                          // 两者都无 prior → 都省略,绝不留指向 H 的悬空 entry
        Assert.Contains("A.txt", miss);                // boundPath:Missing(静默,无警告)
        Assert.Contains("B.txt", unr);                 // 兄弟:强制 Unreadable
        Assert.Contains(warn, w => w.Path == "B.txt" && w.Reason == SkipReason.Unreadable);
        Assert.DoesNotContain(warn, w => w.Path == "A.txt"); // boundPath Missing 不在此产警告
    }
}
```

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "FinalizeEntries"`
Expected: 编译 FAIL(`FinalizeEntries` 尚未实现),Step 6 实现后转 PASS。本步先写测试并确认它编译失败/红。

- [ ] **Step 4: 实现 — RunAsync 主循环接线分类**

在 `RunAsync` 内:声明集合(扫描前)。**计数统一用 `HashSet<string>`(唯一路径数),避免扫描期与打包期对同一路径重复计数**:

```csharp
        var warnings = new List<SkipWarning>();
        var failedContent = new Dictionary<string, SkipReason>(StringComparer.Ordinal); // hash → 原因
        var failedPath = new Dictionary<string, SkipReason>(StringComparer.Ordinal);     // path → 原因
        var hashBoundPath = new Dictionary<string, string>(StringComparer.Ordinal);      // hash → 触发路径
        var missingPaths = new HashSet<string>(StringComparer.Ordinal);                  // 静默省略的唯一路径
        var unreadablePaths = new HashSet<string>(StringComparer.Ordinal);               // 打不开的唯一路径
```

扫描调用改为带 warnings:`foreach (ScannedEntry se in scanner.Scan(root, warnings))`。

文件分支(替换原 99-110 行块)——把 `Detect` 包进 try,失败按分类 `continue`:

```csharp
            files++;
            prior.TryGetValue(se.RelativePath, out PriorFile? p);
            ChangeDetector.Decision d;
            try
            {
                d = ChangeDetector.Detect(p, se.Size, se.Mtime, o.ForceHash,
                    () => ContentHasher.ToHex(ContentHasher.HashStream(File.OpenRead(full))));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                missingPaths.Add(se.RelativePath);
                continue; // Missing:不进 entries
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unreadablePaths.Add(se.RelativePath);
                warnings.Add(new SkipWarning(se.RelativePath, SkipReason.Unreadable, ex.Message));
                if (p is not null) // 沿用上次版本
                    entries.Add(new SnapshotEntry(se.RelativePath, false, p.Size, p.Mtime, p.Mode, p.Hash));
                continue;
            }

            if (d.Kind == ChangeKind.Unchanged) unchanged++; else changed++;
            entries.Add(new SnapshotEntry(se.RelativePath, false, se.Size, se.Mtime, GetMode(full), d.Hash));

            if (!index.Contains(d.Hash) && !openers.ContainsKey(d.Hash))
            {
                openers[d.Hash] = () => File.OpenRead(full);
                hashBoundPath[d.Hash] = se.RelativePath;
                toUpload.Add(new GroupItem(se.RelativePath, d.Hash, se.Size, o.NoCompress.CodecFor(se.RelativePath)));
            }
```

- [ ] **Step 5: 实现 — 打包循环收集 FailedMembers,启用容错**

打包循环里 `builder.Build(...)` 加 `tolerateMemberFailures: true`,收集失败,并把上传/AddPack 包进
`if (built.Entries.Count > 0)`;整 pack 全失败时仅清理 spool 卷文件(不产卷、不进索引)。
替换原打包循环体(`BackupRunner.cs:121-143`)为:

```csharp
        foreach (PackPlan plan in grouper.Group(toUpload))
        {
            string packId = Guid.NewGuid().ToString("N");
            byte[] contentKey = ContentKey.Generate();
            var members = plan.Items.Select(i => new PackMember(i.Hash, openers[i.Hash])).ToList();

            BuiltPack built = builder.Build(packId, plan.Codec, contentKey, members, tolerateMemberFailures: true);
            foreach (FailedMember fm in built.FailedMembers)
            {
                failedContent[fm.Hash] = fm.Reason;
                if (hashBoundPath.TryGetValue(fm.Hash, out string? bp))
                    failedPath[bp] = fm.Reason;
            }

            if (built.Entries.Count == 0)
            {
                // 整 pack 全失败:不产卷不进索引,仅清理已落盘的 spool 卷。
                foreach (string vp in built.VolumePaths) TryDelete(vp);
                continue;
            }

            for (int i = 0; i < built.VolumePaths.Count; i++)
            {
                string volName = RepoLayout.Volume(packId, i);
                using (Stream vs = File.OpenRead(built.VolumePaths[i]))
                {
                    uploadedBytes += vs.Length;
                    await store.PutAsync(volName, vs, o.DataTier, overwrite: true, ct).ConfigureAwait(false);
                }
                TryDelete(built.VolumePaths[i]);
                volumesUploaded++;
            }

            string wrapped = Convert.ToBase64String(ContentKey.Wrap(key, contentKey));
            index.AddPack(packId, built.VolumePaths.Count, built.CiphertextSize, wrapped, built.Entries, plan.Codec);
            packsCreated++;
        }
```

- [ ] **Step 6: 实现 — 终结结构(写快照前)**

终结逻辑抽成静态纯函数 `FinalizeEntries`(便于 Step 3.5 直接单测)。在 `BackupRunner` 内新增:

```csharp
    /// <summary>
    /// 终结结构:对内容未进仓库(hash ∈ failedContent)的文件 entry,按各自路径沿用 prior 或省略,
    /// 杜绝悬空引用。就地更新 missingPaths/unreadablePaths(唯一路径计数)与 warnings。返回终结后的 entries。
    /// </summary>
    internal static List<SnapshotEntry> FinalizeEntries(
        List<SnapshotEntry> entries,
        IReadOnlyDictionary<string, SkipReason> failedContent,
        IReadOnlyDictionary<string, SkipReason> failedPath,
        IReadOnlyDictionary<string, PriorFile> prior,
        HashSet<string> missingPaths,
        HashSet<string> unreadablePaths,
        List<SkipWarning> warnings)
    {
        if (failedContent.Count == 0) return entries;

        var finalized = new List<SnapshotEntry>(entries.Count);
        foreach (SnapshotEntry e in entries)
        {
            if (e.IsDirectory || e.Hash is null || !failedContent.ContainsKey(e.Hash))
            { finalized.Add(e); continue; }

            // 内容未进仓库。boundPath 用其真实原因;去重边界(非 boundPath)强制 Unreadable。
            bool isBound = failedPath.ContainsKey(e.RelativePath);
            SkipReason reason = isBound ? failedPath[e.RelativePath] : SkipReason.Unreadable;

            if (prior.TryGetValue(e.RelativePath, out PriorFile? pf)) // 沿用上次版本
                finalized.Add(new SnapshotEntry(e.RelativePath, false, pf.Size, pf.Mtime, pf.Mode, pf.Hash));
            // 否则省略(不加入 finalized)

            if (reason == SkipReason.Unreadable)
            {
                unreadablePaths.Add(e.RelativePath);
                if (!isBound) // 去重边界:兄弟路径补一条警告
                    warnings.Add(new SkipWarning(e.RelativePath, SkipReason.Unreadable, "dedup sibling of unreadable content"));
            }
            else missingPaths.Add(e.RelativePath);
        }
        return finalized;
    }
```

在 `string snapshotId = NewSnapshotId();` 之前调用它,计数取 HashSet 的唯一路径数(**不做任何算术**):

```csharp
        entries = FinalizeEntries(entries, failedContent, failedPath, prior, missingPaths, unreadablePaths, warnings);
        int skippedMissing = missingPaths.Count;
        int skippedUnreadable = unreadablePaths.Count;
```

> 计数语义:`SkippedMissing`/`SkippedUnreadable` 即各自 HashSet 的唯一路径数。
> 一条路径若扫描期与终结期都触发,HashSet 自动去重;一次逻辑失败可能分别落入两个集合(符合 spec)。

- [ ] **Step 7: 实现 — 报告返回带新字段**

最终 `return new BackupReport(...)`(成功分支)末尾追加:

```csharp
        return new BackupReport(snapshotId, files, dirs, changed, unchanged, packsCreated, volumesUploaded,
            uploadedBytes, snapsDeleted, packsDeleted, eligibleCompaction, DryRun: false,
            Skipped: false, SkippedMissing: skippedMissing, SkippedUnreadable: skippedUnreadable,
            Warnings: warnings.Count > 0 ? warnings : null);
```

DryRun 分支(113-114)在打包前 return,**只能反映扫描期**统计(下界),直接用 HashSet 计数
(此时终结后的 `skippedMissing`/`skippedUnreadable` 局部变量尚未声明,不可引用):

```csharp
        if (o.DryRun)
            return new BackupReport(null, files, dirs, changed, unchanged, 0, 0, 0, 0, 0, 0, DryRun: true,
                Skipped: false, SkippedMissing: missingPaths.Count, SkippedUnreadable: unreadablePaths.Count,
                Warnings: warnings.Count > 0 ? warnings : null);
```

- [ ] **Step 8: 构建并运行新测试**

Run: `dotnet test tests/AzureBackup.Core.Tests/AzureBackup.Core.Tests.csproj --filter "Unreadable_changed_file|Unreadable_new_file"`
Expected: 两个 PASS。

- [ ] **Step 9: 全测无回归**

Run: `dotnet test AzureBackup.slnx -c Debug`
Expected: 全绿。

- [ ] **Step 10: 提交**

```bash
git add src/AzureBackup.Core/Backup/BackupRunner.cs tests/AzureBackup.Core.Tests/BackupRunnerTests.cs
git commit -m "feat(backup): 跳过丢失/打不开文件,终结结构沿用或省略,报告带警告"
```

---

## Task 6: Program.cs — 汇总行与通知 body 加跳过/警告

**Files:**
- Modify: `src/AzureBackup.Backup/Program.cs:67-69`

- [ ] **Step 1: 扩展成功汇总行**

`Program.cs` 第 69 行的成功 `line` 追加跳过计数;并在有警告时附清单:

**先**把警告清单拼进 `line`,**再** `Console.WriteLine`,确保控制台、summary、通知三者一致:

```csharp
            line = r.Skipped
                ? $"[{name}] skipped (lock held)"
                : $"[{name}] snapshot={r.SnapshotId} new/mod={r.NewOrModified} packs={r.PacksCreated} vols={r.VolumesUploaded} bytes={r.UploadedBytes} delSnaps={r.SnapshotsDeleted} delPacks={r.PacksDeleted} compacted={r.PacksCompacted} skipMissing={r.SkippedMissing} skipUnreadable={r.SkippedUnreadable}";
            if (r.Warnings is { Count: > 0 })
                foreach (SkipWarning w in r.Warnings)
                    line += $"\n  ! unreadable: {w.Path}";
            Console.WriteLine(line);
```

> `Program.cs` 顶部加 `using AzureBackup.Core.Scan;`。`line` 含警告清单后会一并进 `summary` 与 per-job 通知 body。

- [ ] **Step 2: 构建确认**

Run: `dotnet build AzureBackup.slnx -c Debug`
Expected: 成功。

- [ ] **Step 3: 全测最终确认**

Run: `dotnet test AzureBackup.slnx -c Debug`
Expected: 全绿。

- [ ] **Step 4: 提交**

```bash
git add src/AzureBackup.Backup/Program.cs
git commit -m "feat(cli): 汇总行/通知体现跳过计数与 Unreadable 清单"
```

---

## Task 7: 文档与收尾

- [ ] **Step 1: 更新 STATUS.md / docs**(如有进度文档),记录新行为:丢失静默省略、打不开报警沿用、成功带警告语义。
- [ ] **Step 2: 重新编译 docker 文件**(见 `build/`、`docker/`、`dist/`)。按既有 NAS 导出脚本(amd64 + 传统 Docker 镜像格式)重建镜像。
- [ ] **Step 3: 提交**

```bash
git add -A
git commit -m "docs+build: 跳过语义文档更新 + 重建 docker 镜像"
```

---

## 验证清单(对照 spec 测试节)

- [x] 扫描删文件 → 不崩、省略、无警告(SkippedMissing)
- [x] 扫描删目录 → 子树静默跳过
- [x] 文件打不开 → 跳过 + 警告 + 沿用(有 prior)/省略(无 prior)
- [x] 目录打不开 → 子树跳过 + 警告
- [x] 去重边界 → 不悬空、兄弟路径报 Unreadable(`FinalizeEntriesTests` 纯函数单测)
- [x] 整 pack 全失败 → 不产卷不进索引
- [x] Compactor 不容错 → 成员异常上抛(由 PackBuilder 默认 `tolerateMemberFailures:false` 单测覆盖;Compactor 复用同一默认路径,成员为 MemoryStream 不会抛 FS 异常)
- [x] 退出码 0(成功带警告)
- [x] 其它异常继续上抛
- [x] DryRun 仅统计扫描期下界
