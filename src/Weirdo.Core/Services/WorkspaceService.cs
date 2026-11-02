using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Weirdo.Core.Services;

public sealed class WorkspaceService
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Task<WorkspaceInfo> Task)> inspections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object inspectionGate = new();
    private readonly SemaphoreSlim workers = new(3);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Value)> versions = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WorkspaceLocation> ResolvePathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请输入文件夹路径。", nameof(path));
        path = path.Trim();
        if (path[0] is '"' or '\'')
        {
            if (path.Length < 2 || path[^1] != path[0]) throw new ArgumentException("目录路径的引号不匹配。", nameof(path));
            path = path[1..^1].Trim();
        }
        path = Environment.ExpandEnvironmentVariables(path);
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length == 1 ? "" : path[2..]);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请输入完整的文件夹路径，例如 D:\\Projects。", nameof(path));
        try
        {
            return await Task.Run(() =>
            {
                var normalized = Path.TrimEndingDirectorySeparator(ExistingDirectory(path, explainFile: true));
                var name = Path.GetFileName(normalized);
                return new WorkspaceLocation(normalized, name.Length == 0 ? normalized : name);
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new TimeoutException("访问文件夹超过 5 秒，请检查驱动器或网络路径后重试。"); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("目录路径格式无效。", nameof(path)); }
    }

    public Task<WorkspaceInfo> InspectAsync(string path)
    {
        path = ExistingDirectory(path);
        lock (inspectionGate)
        {
            if (inspections.TryGetValue(path, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(1)) return cached.Task;
            var task = InspectCoreAsync(path);
            inspections[path] = (DateTimeOffset.UtcNow, task);
            if (inspections.Count > 128)
                foreach (var stale in inspections.Where(x => DateTimeOffset.UtcNow - x.Value.At > TimeSpan.FromMinutes(5)).Select(x => x.Key).ToArray()) inspections.TryRemove(stale, out _);
            return task;
        }
    }

    private static string ExistingDirectory(string path, bool explainFile = false)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            if (explainFile && File.Exists(path)) throw new IOException("所选路径是文件，请选择文件夹。");
            throw new DirectoryNotFoundException($"Workspace folder does not exist: {path}");
        }
        return path;
    }

    private async Task<WorkspaceInfo> InspectCoreAsync(string path)
    {
        await workers.WaitAsync().ConfigureAwait(false);
        try
        {
            var gitTask = GitAsync(path);
            var nodeTask = VersionAsync("node.exe", path);
            var pythonTask = VersionAsync("python.exe", path);
            await Task.WhenAll(gitTask, nodeTask, pythonTask).ConfigureAwait(false);
            var environment = System.Environment.GetEnvironmentVariable("VIRTUAL_ENV") is { Length: > 0 } venv ? Path.GetFileName(venv) : System.Environment.GetEnvironmentVariable("CONDA_DEFAULT_ENV");
            if (string.IsNullOrEmpty(environment) && Directory.Exists(Path.Combine(path, ".venv"))) environment = ".venv available";
            return new(path, await gitTask, await nodeTask, await pythonTask, environment);
        }
        finally { workers.Release(); }
    }

    public Task<List<FileEntry>> FilesAsync(string root, string path) => Task.Run(() =>
    {
        var absoluteRoot = Path.GetFullPath(root);
        var absolutePath = Path.GetFullPath(path);
        if (!Directory.Exists(absoluteRoot) || !Directory.Exists(absolutePath)) throw new DirectoryNotFoundException("The selected folder no longer exists.");
        EnsureWithinRoot(absoluteRoot, absolutePath);
        var entries = new List<FileEntry>();
        foreach (var entry in new DirectoryInfo(absolutePath).EnumerateFileSystemInfos())
        {
            if (entry.Name == ".git") continue;
            if (entries.Count >= 10000) throw new IOException("This folder contains more than 10,000 entries. Open a smaller subfolder.");
            var directory = (entry.Attributes & FileAttributes.Directory) != 0;
            long? size = entry is FileInfo file ? file.Length : null;
            entries.Add(new(entry.Name, entry.FullName, directory, size));
        }
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

    public static void EnsureWithinRoot(string root, string path)
    {
        var lexicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var lexicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!IsWithin(lexicalRoot, lexicalPath)) throw new UnauthorizedAccessException("The path is outside the selected workspace.");
        var actualRoot = ResolveLinks(lexicalRoot);
        var actualPath = ResolveLinks(lexicalPath);
        if (!IsWithin(actualRoot, actualPath)) throw new UnauthorizedAccessException("This link points outside the selected workspace.");
    }

    private static bool IsWithin(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ResolveLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new IOException("The selected symbolic link cannot be resolved.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private async Task<string?> VersionAsync(string executable, string cwd)
    {
        var path = CommandResolver.FindExecutable(executable);
        if (path is null) return null;
        // Do not activate Windows Store aliases while inspecting a project.
        if (path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length == 0) return null;
        if (versions.TryGetValue(path, out var value) && DateTimeOffset.UtcNow - value.At < TimeSpan.FromMinutes(2)) return value.Value;
        var probe = await RunAsync(path, ["--version"], cwd, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        var version = probe.ExitCode == 0 ? (probe.Output + probe.Error).Trim().Split('\n')[0].Trim() : null;
        if (version?.Length > 100) version = null;
        versions[path] = (DateTimeOffset.UtcNow, version);
        return version;
    }

    private async Task<GitInfo> GitAsync(string cwd)
    {
        var executable = CommandResolver.FindExecutable("git.exe");
        if (executable is null) return new(false, null, 0, 0, 0, "Git is not installed or is not on PATH.");
        Task<ProcessResult> Git(params string[] arguments) => RunAsync(executable, new[] { "--no-optional-locks", "-C", cwd }.Concat(arguments).ToArray(), cwd, TimeSpan.FromSeconds(8));
        var rootResult = await Git("rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (rootResult.ExitCode != 0)
        {
            var ordinary = rootResult.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
            return new(false, null, 0, 0, 0, ordinary ? null : ShortError(rootResult));
        }
        var root = Path.GetFullPath(rootResult.Output.Trim());
        var branchTask = Git("symbolic-ref", "--short", "-q", "HEAD");
        var statusTask = Git("status", "--porcelain=v1", "-z", "--untracked-files=all");
        var headTask = Git("rev-parse", "--verify", "HEAD");
        await Task.WhenAll(branchTask, statusTask, headTask).ConfigureAwait(false);
        var status = await statusTask;
        var branchResult = await branchTask;
        var branch = branchResult.ExitCode == 0 ? branchResult.Output.Trim() : (await Git("rev-parse", "--short", "HEAD").ConfigureAwait(false)).Output.Trim() + " (detached)";
        if (status.ExitCode != 0) return new(true, branch, 0, 0, 0, ShortError(status));
        var records = ParseStatus(status.Output);
        var changedFiles = records.Count;
        var added = 0;
        var deleted = 0;
        string? warning = null;
        var head = await headTask;
        var toCount = records.Where(x => x.Code == "??").Select(x => x.Path).ToList();
        if (head.ExitCode == 0)
        {
            var diff = await Git("diff", "--no-ext-diff", "--no-textconv", "--numstat", "-z", "HEAD", "--").ConfigureAwait(false);
            if (diff.ExitCode != 0) return new(true, branch, changedFiles, 0, 0, ShortError(diff));
            foreach (var row in ParseNumstat(diff.Output)) { added += row.Added; deleted += row.Deleted; }
        }
        else
        {
            // In an unborn repository, all present tracked and untracked text is added relative to an empty tree.
            var tracked = await Git("ls-files", "-z").ConfigureAwait(false);
            if (tracked.ExitCode != 0) return new(true, branch, changedFiles, 0, 0, ShortError(tracked));
            toCount.AddRange(tracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        using var countTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        foreach (var relative in toCount.Distinct(StringComparer.Ordinal))
        {
            try
            {
                countTimeout.Token.ThrowIfCancellationRequested();
                var file = Path.GetFullPath(Path.Combine(root, relative));
                if (!IsWithin(Path.TrimEndingDirectorySeparator(root), file)) continue;
                if (!File.Exists(file)) continue;
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { added++; continue; }
                if (info.Length > 64 * 1024 * 1024) { warning = "Line totals exclude untracked text files larger than 64 MB."; continue; }
                added += await CountTextLinesAsync(file, countTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { warning = "Line totals are partial: untracked-file counting exceeded 6 seconds."; break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = "Line totals are partial: one or more changed files could not be read."; }
        }
        return new(true, branch, changedFiles, added, deleted, warning);
    }

    internal static List<(string Code, string Path)> ParseStatus(string output)
    {
        var records = output.Split('\0');
        var result = new List<(string, string)>();
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 4) continue;
            var code = record[..2];
            result.Add((code, record[3..]));
            if (code.Contains('R') || code.Contains('C')) i++; // -z: destination first, source in next NUL field.
        }
        return result;
    }

    // Ported from Kooky GitStatusFetcher.parseNumstat (MIT, copyright 2026 kooky contributors).
    // Windows modification: explicit tuple records and checked invariant-culture numeric parsing.
    internal static IEnumerable<(int Added, int Deleted, string Path)> ParseNumstat(string raw)
    {
        var fields = raw.Split('\0');
        for (var index = 0; index < fields.Length; index++)
        {
            var parts = fields[index].Split('\t', 3);
            if (parts.Length != 3) continue;
            var path = parts[2];
            if (path.Length == 0)
            {
                if (index + 2 >= fields.Length) continue;
                path = fields[index + 2];
                index += 2;
            }
            if (path.Length == 0) continue;
            _ = int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var added);
            _ = int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var deleted);
            yield return (added, deleted, path);
        }
    }

    private static async Task<int> CountTextLinesAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[16384];
        var lines = 0;
        var last = -1;
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == 0) return 0; // Git's binary-file convention.
                if (buffer[i] == '\n') lines++;
            }
            last = buffer[count - 1];
        }
        return lines + (last >= 0 && last != '\n' ? 1 : 0);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            process.Start();
            var stdout = ReadBoundedAsync(process.StandardOutput, cancellation.Token);
            var stderr = ReadBoundedAsync(process.StandardError, cancellation.Token);
            await Task.WhenAll(process.WaitForExitAsync(cancellation.Token), stdout, stderr).ConfigureAwait(false);
            return new(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return new(-1, "", ex is OperationCanceledException ? "Command timed out." : ex.Message);
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int length;
        while ((length = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + length > 8 * 1024 * 1024) throw new IOException("Command output exceeds the 8 MB limit.");
            result.Append(buffer, 0, length);
        }
        return result.ToString();
    }
    private static string ShortError(ProcessResult result) => string.IsNullOrWhiteSpace(result.Error) ? $"Git exited with code {result.ExitCode}." : result.Error.Trim()[..Math.Min(result.Error.Trim().Length, 300)];
}
