using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FluenityHub_WinUIHost.Services;

internal sealed record UnityCliCommandResult(bool Succeeded, JsonElement Data, string Message);

internal static class UnityCliCommandService
{
    public static async Task<UnityCliCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        string? workingDirectory = null)
    {
        var executable = await new UnityCliToolService().GetVerifiedExecutablePathAsync(cancellationToken);
        if (executable is null)
        {
            return new(false, default, "Install Unity CLI from Settings and try again.");
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
        };
        startInfo.Environment["UNITY_NO_CLI_INVOKED_TELEMETRY"] = "1";
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--non-interactive");
        startInfo.ArgumentList.Add("--no-log-proxy");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var outputSync = new object();
        var overflow = false;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputSync)
            {
                if (output.Length + e.Data.Length > 2 * 1024 * 1024) overflow = true;
                else output.AppendLine(e.Data);
            }
        };
        // Drain stderr without forwarding command arguments or credentials to logs.
        process.ErrorDataReceived += (_, _) => { };
        if (!process.Start()) return new(false, default, "Unity CLI could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            lock (outputSync)
            {
                if (overflow) return new(false, default, "Unity CLI returned too much data.");
                return Parse(process.ExitCode, output.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new(false, default, "Unity CLI timed out. Try again.");
        }
    }

    internal static UnityCliCommandResult Parse(int exitCode, string output)
    {
        if (TryParse(exitCode, output, out var result)) return result;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (TryParse(exitCode, line, out result)) return result;
        }
        return new(false, default, "Unity CLI returned an unreadable result. Check its version in Settings.");
    }

    private static bool TryParse(int exitCode, string json, out UnityCliCommandResult result)
    {
        result = new(false, default, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("success", out var success)) return false;
            var succeeded = exitCode == 0 && success.ValueKind == JsonValueKind.True;
            var data = root.TryGetProperty("data", out var value) ? value.Clone() : default;
            var message = succeeded ? string.Empty : "Unity could not complete the operation.";
            if (!succeeded && root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    if (error.ValueKind != JsonValueKind.Object) continue;
                    if (error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        message = text.GetString() ?? message;
                        break;
                    }
                }
            }
            result = new(succeeded, data, message);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
