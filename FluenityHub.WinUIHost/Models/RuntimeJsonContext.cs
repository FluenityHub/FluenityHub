using System.Text.Json.Serialization;
using FluenityHub_WinUIHost.Services;

namespace FluenityHub_WinUIHost.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ElevatedUnityCliRunner.OperationRequest))]
[JsonSerializable(typeof(ElevatedUnityCliRunner.OperationResult))]
[JsonSerializable(typeof(UnityCliToolService.UnityCliManifest))]
[JsonSerializable(typeof(UnityCliToolService.UnityCliState))]
internal partial class RuntimeJsonContext : JsonSerializerContext
{
}
