using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Weirdo.Core;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AgentProfile))]
[JsonSerializable(typeof(AgentProfile[]))]
[JsonSerializable(typeof(List<AgentProfile>))]
[JsonSerializable(typeof(StoredAgentProfile))]
[JsonSerializable(typeof(List<StoredAgentProfile>))]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionInfo[]))]
[JsonSerializable(typeof(List<SessionInfo>))]
[JsonSerializable(typeof(TerminalChunk))]
[JsonSerializable(typeof(TerminalReplay))]
[JsonSerializable(typeof(GitInfo))]
[JsonSerializable(typeof(WorkspaceInfo))]
[JsonSerializable(typeof(WorkspaceLocation))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(List<FileEntry>))]
[JsonSerializable(typeof(NativeSession))]
[JsonSerializable(typeof(List<NativeSession>))]
[JsonSerializable(typeof(AgentModelChoices))]
[JsonSerializable(typeof(AgentModelSyncResult))]
[JsonSerializable(typeof(DataMigrationResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
public partial class CoreJsonContext : JsonSerializerContext;
