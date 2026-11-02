using System.Text.Json;

namespace Weirdo.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
public sealed record AgentProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Kind { get; init; } = "custom";
    public string? Model { get; init; }
    public Dictionary<string,string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ResumeArguments { get; init; }
    public bool BuiltIn { get; init; }
    public bool Available { get; set; }
}
public sealed record LaunchSpec(string CommandLine, string Cwd, Dictionary<string,string> Environment, string? NativeSessionId);
public sealed record SessionRequest
{
    public string TrackingId { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string AgentId { get; init; } = "shell";
    public string? Title { get; init; }
    public string? Command { get; init; }
    public Dictionary<string,string>? Env { get; init; }
    public string? NativeSessionId { get; init; }
    public int Cols { get; init; } = 100;
    public int Rows { get; init; } = 30;
}
public sealed record SessionInfo
{
    public string Id { get; init; } = "";
    public string TrackingId { get; init; } = "";
    public string WorkspaceId { get; set; } = "";
    public string Title { get; set; } = "";
    public string AgentId { get; init; } = "shell";
    public string Cwd { get; set; } = "";
    public int ProcessId { get; set; }
    public bool Running { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? LastOutputAt { get; set; }
    public string? NativeSessionId { get; set; }
    public int Cols { get; set; } = 100;
    public int Rows { get; set; } = 30;
    public long BytesRead { get; set; }
}
public sealed record TerminalChunk(string SessionId, long Sequence, string Data);
public sealed record TerminalReplay(string SessionId, long Sequence, string Data, bool Truncated);
public sealed record GitInfo(bool IsRepository, string? Branch, int ChangedFiles, int Added, int Deleted, string? Error = null);
public sealed record WorkspaceInfo(string Path, GitInfo Git, string? Node, string? Python, string? Environment);
public sealed record WorkspaceLocation(string Path, string Name);
public sealed record FileEntry(string Name, string Path, bool IsDirectory, long? Size);
public sealed record NativeSession(string Id, string AgentId, string Cwd, DateTimeOffset ModifiedAt, string? Title);
public sealed record AgentModelChoices(string[] Suggestions, string? InheritedModel);
public sealed record AgentModelSyncResult(string[] Suggestions, bool NativePickerUpdated, string? Note);
public sealed record StoredAgentProfile(string Id, string Name, string Command, string Kind, string? ResumeArguments, bool BuiltIn, string? ProtectedEnvironment, string? Model = null);
public sealed record DataMigrationResult(bool Completed, int CopiedFiles, int SkippedFiles, string[] Warnings);
