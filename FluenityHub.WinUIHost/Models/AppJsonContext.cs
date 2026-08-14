using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using FluenityHub_WinUIHost.Services;

namespace FluenityHub_WinUIHost.Models;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ProjectBackupRecord>))]
[JsonSerializable(typeof(List<PersistedUnityInstallation>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
