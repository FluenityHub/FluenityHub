using System.Text.Json;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityEditorPreferenceService
{
    public static async Task<string?> GetDefaultVersionAsync(CancellationToken cancellationToken)
    {
        if (!new UnityCliToolService().GetStatus().IsInstalled) return null;
        var result = await UnityCliCommandService.RunAsync(["editors", "default"], cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        return ReadDefaultVersion(result.Data);
    }

    internal static string? ReadDefaultVersion(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("default", out var value))
            throw new InvalidDataException("Unity CLI did not return the default Editor preference.");
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidDataException("Unity CLI returned an invalid default Editor preference.")
        };
    }

    public static async Task SetDefaultVersionAsync(string? version, CancellationToken cancellationToken)
    {
        var result = await UnityCliCommandService.RunAsync(
            ["editors", "default", version ?? "--unset"], cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
    }
}
