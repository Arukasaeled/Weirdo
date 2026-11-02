using System.Text.Json;
using System.Text.Json.Serialization;
using Weirdo.Core;

namespace Weirdo.BrokerHost;

internal sealed record SavedSession(SessionInfo Info, SessionRequest Request);
internal sealed record ProtectedSessionState(string Encrypted, string? Protection);
internal sealed record BootstrapResult(
    JsonElement? State,
    List<AgentProfile> Agents,
    SessionInfo[] Sessions,
    string AppVersion,
    string HomePath,
    int BrokerPid,
    int WindowsBuild,
    string[] Warnings);

internal sealed record ProcessMeasurement(int Pid, string Name, string Group, long WorkingSetBytes, long PrivateBytes, double CpuPercent);
internal sealed record ResourceGroup(string Name, int[] Pids, long WorkingSetBytes, long PrivateBytes, double CpuPercent);
internal sealed record ResourceMeasurements(DateTimeOffset At, double IntervalMs, int LogicalProcessors, string CpuBasis, ResourceGroup[] Groups, ProcessMeasurement[] Processes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BootstrapResult))]
[JsonSerializable(typeof(SavedSession[]))]
[JsonSerializable(typeof(ProtectedSessionState))]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionInfo[]))]
[JsonSerializable(typeof(TerminalChunk))]
[JsonSerializable(typeof(TerminalReplay))]
[JsonSerializable(typeof(List<AgentProfile>))]
[JsonSerializable(typeof(List<NativeSession>))]
[JsonSerializable(typeof(WorkspaceInfo))]
[JsonSerializable(typeof(List<FileEntry>))]
[JsonSerializable(typeof(ResourceMeasurements))]
internal partial class BrokerJsonContext : JsonSerializerContext;
