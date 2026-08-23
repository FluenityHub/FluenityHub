using System.Collections.Concurrent;
using System.Diagnostics;

namespace FluenityHub_WinUIHost.Services;

internal sealed class UnityLicensingClientSession : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<int, UnityLicensingClientSession> ActiveSessions = new();

    private readonly Process _process;
    private readonly CancellationTokenSource _outputCancellation = new();
    private bool _ownsProcess;
    private bool _disposed;

    private UnityLicensingClientSession(Process process, string editorPipeName)
    {
        _process = process;
        _ownsProcess = true;
        EditorPipeName = editorPipeName;
    }

    public string EditorPipeName { get; }

    public static async Task<(UnityLicensingClientSession? Session, string ErrorMessage)> StartAsync(
        string editorExecutable,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var licensingClientPath = Path.Combine(
            Path.GetDirectoryName(editorExecutable) ?? string.Empty,
            "Data",
            "Resources",
            "Licensing",
            "Client",
            "Unity.Licensing.Client.exe");
        if (!File.Exists(licensingClientPath))
        {
            return (null, $"Unity Licensing Client was not found for this Editor: {licensingClientPath}");
        }

        // Unity's SDK tries the user pipe first and falls back to a unique pipe.
        // A dedicated pipe prevents another Hub/CLI client from owning our token
        // handoff while allowing multiple Editors to launch independently.
        var pipeSuffix = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var fullPipeName = $"Unity-LicenseClient-{pipeSuffix}";
        var editorPipeName = fullPipeName["Unity-".Length..];

        var startInfo = new ProcessStartInfo
        {
            FileName = licensingClientPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--namedPipe");
        startInfo.ArgumentList.Add(fullPipeName);
        startInfo.ArgumentList.Add("--cloudEnvironment");
        startInfo.ArgumentList.Add("production");
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            startInfo.ArgumentList.Add("--accessToken");
            startInfo.ArgumentList.Add(accessToken);
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                return (null, "Unity Licensing Client could not be started.");
            }

            var session = new UnityLicensingClientSession(process, editorPipeName);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputPump = PumpOutputAsync(process.StandardOutput, ready, session._outputCancellation.Token);
            var errorPump = PumpOutputAsync(process.StandardError, null, session._outputCancellation.Token);
            _ = ObservePumpAsync(outputPump);
            _ = ObservePumpAsync(errorPump);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            try
            {
                var exitTask = process.WaitForExitAsync(timeout.Token);
                var completed = await Task.WhenAny(ready.Task, exitTask).WaitAsync(timeout.Token);
                if (completed == exitTask)
                {
                    await exitTask;
                    session.Dispose();
                    return (null, $"Unity Licensing Client stopped during startup (exit code {process.ExitCode}).");
                }

                await ready.Task;
                return (session, string.Empty);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                session.Dispose();
                return (null, "Unity Licensing Client did not become ready in time.");
            }
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            process?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryTerminate(process);
            process?.Dispose();
            return (null, $"Unable to start Unity Licensing Client: {ex.Message}");
        }
    }

    public void AttachToEditor(Process editorProcess)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UnityLicensingClientSession));
        }

        _ownsProcess = false;
        ActiveSessions[editorProcess.Id] = this;
        editorProcess.EnableRaisingEvents = true;
        editorProcess.Exited += OnEditorExited;
        if (editorProcess.HasExited)
        {
            Release(editorProcess);
        }
    }

    private void OnEditorExited(object? sender, EventArgs args)
    {
        if (sender is Process editorProcess)
        {
            Release(editorProcess);
        }
    }

    private static void Release(Process editorProcess)
    {
        editorProcess.Exited -= ActiveSessions.TryGetValue(editorProcess.Id, out var session)
            ? session.OnEditorExited
            : null;

        if (ActiveSessions.TryRemove(editorProcess.Id, out session))
        {
            session._ownsProcess = true;
            session.Dispose();
        }
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        TaskCompletionSource<bool>? ready,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            if (line.Contains("Waiting for clients to connect on", StringComparison.OrdinalIgnoreCase))
            {
                ready?.TrySetResult(true);
            }
        }
    }

    private static async Task ObservePumpAsync(Task pump)
    {
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
            // Expected when a session is disposed.
        }
        catch
        {
            // Licensing output is diagnostic only and must not destabilize launch.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputCancellation.Cancel();
        if (_ownsProcess)
        {
            TryTerminate(_process);
        }

        _process.Dispose();
        _outputCancellation.Dispose();
    }

    private static void TryTerminate(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The licensing client may already have stopped automatically.
        }
    }
}
