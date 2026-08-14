using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

internal sealed record ElevatedUnityCliRunResult(
    int ExitCode,
    string Output,
    bool TimedOut = false,
    string? StartError = null);

internal static partial class ElevatedUnityCliRunner
{
    internal const string HelperArgument = "--elevated-unity-cli";
    internal sealed record OperationRequest(string CliPath, IReadOnlyList<string> Arguments);
    internal sealed record OperationResult(int ExitCode, string? Error = null);

    public static async Task<ElevatedUnityCliRunResult> RunAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        Action<string> lineObserver,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        var operationRoot = GetOperationRoot();
        Directory.CreateDirectory(operationRoot);
        var operationDirectory = Path.Combine(operationRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationDirectory);
        var requestPath = Path.Combine(operationDirectory, "request.json");
        var outputPath = Path.Combine(operationDirectory, "output.log");
        var resultPath = Path.Combine(operationDirectory, "result.json");
        var cancelPath = Path.Combine(operationDirectory, "cancel.requested");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(
                new OperationRequest(cliPath, arguments),
                RuntimeJsonContext.Default.OperationRequest),
            cancellationToken);
        // Create every handoff file under the unelevated user token so the helper cannot
        // leave administrator-owned artifacts that the main app is unable to clean up.
        await File.WriteAllTextAsync(outputPath, string.Empty, cancellationToken);
        await File.WriteAllTextAsync(resultPath, string.Empty, cancellationToken);

        Process? helper = null;
        var observedLength = 0;
        var pendingLine = string.Empty;
        var capturedOutput = new StringBuilder();
        var lastActivity = DateTimeOffset.UtcNow;
        var timedOut = false;

        void ReadNewOutput(bool flushPendingLine = false)
        {
            if (!File.Exists(outputPath))
            {
                return;
            }

            string content;
            try
            {
                using var stream = new FileStream(
                    outputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                content = reader.ReadToEnd();
            }
            catch (IOException)
            {
                return;
            }

            if (content.Length < observedLength)
            {
                observedLength = 0;
                pendingLine = string.Empty;
            }

            if (content.Length > observedLength)
            {
                var delta = content[observedLength..];
                observedLength = content.Length;
                capturedOutput.Append(delta);
                lastActivity = DateTimeOffset.UtcNow;
                var combined = pendingLine + delta;
                var lines = combined.Split('\n');
                pendingLine = lines[^1];
                foreach (var rawLine in lines[..^1])
                {
                    var line = rawLine.TrimEnd('\r');
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lineObserver(line);
                    }
                }
            }

            if (flushPendingLine && !string.IsNullOrWhiteSpace(pendingLine))
            {
                lineObserver(pendingLine.TrimEnd('\r'));
                pendingLine = string.Empty;
            }
        }

        try
        {
            var hostPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(hostPath) || !File.Exists(hostPath))
            {
                return new(-1, string.Empty, StartError: "FluenityHub could not locate its elevated helper executable.");
            }

            var helperStartInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            helperStartInfo.ArgumentList.Add(HelperArgument);
            helperStartInfo.ArgumentList.Add(requestPath);
            helper = Process.Start(helperStartInfo);
            if (helper is null)
            {
                return new(-1, string.Empty, StartError: "Windows could not start the elevated Unity installer helper.");
            }

            while (!helper.HasExited)
            {
                ReadNewOutput();
                if (cancellationToken.IsCancellationRequested)
                {
                    await File.WriteAllTextAsync(cancelPath, "cancel", CancellationToken.None);
                    await helper.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (DateTimeOffset.UtcNow - lastActivity >= inactivityTimeout)
                {
                    timedOut = true;
                    await File.WriteAllTextAsync(cancelPath, "timeout", CancellationToken.None);
                    await helper.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
                    break;
                }

                await Task.Delay(100, CancellationToken.None);
            }

            ReadNewOutput(flushPendingLine: true);
            if (timedOut)
            {
                return new(-1, capturedOutput.ToString(), TimedOut: true);
            }

            if (!File.Exists(resultPath))
            {
                return new(
                    helper.HasExited ? helper.ExitCode : -1,
                    capturedOutput.ToString(),
                    StartError: "The elevated Unity installer helper did not return a result.");
            }

            var result = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(resultPath, CancellationToken.None),
                RuntimeJsonContext.Default.OperationResult);
            return result is null
                ? new(-1, capturedOutput.ToString(), StartError: "The elevated Unity installer returned an invalid result.")
                : new(result.ExitCode, capturedOutput.ToString(), StartError: result.Error);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new(-1, capturedOutput.ToString(), StartError: "Windows administrator approval was canceled.");
        }
        catch (TimeoutException)
        {
            return new(-1, capturedOutput.ToString(), TimedOut: timedOut, StartError: "The elevated installer did not stop when requested.");
        }
        finally
        {
            helper?.Dispose();
            TryDeleteOperationDirectory(operationDirectory, operationRoot);
        }
    }

    public static async Task<int> RunHelperAsync(string requestPath)
    {
        var resultPath = string.Empty;
        try
        {
            var operationRoot = GetOperationRoot();
            var fullRequestPath = Path.GetFullPath(requestPath);
            var operationDirectory = Path.GetDirectoryName(fullRequestPath)
                ?? throw new InvalidDataException("The elevated operation path is invalid.");
            EnsureValidOperationDirectory(operationDirectory, operationRoot);
            if (!Path.GetFileName(fullRequestPath).Equals("request.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The elevated operation request is invalid.");
            }

            resultPath = Path.Combine(operationDirectory, "result.json");
            var outputPath = Path.Combine(operationDirectory, "output.log");
            var cancelPath = Path.Combine(operationDirectory, "cancel.requested");
            var request = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(fullRequestPath),
                RuntimeJsonContext.Default.OperationRequest)
                ?? throw new InvalidDataException("The elevated operation request is empty.");
            ValidateRequest(request);

            var verifiedCliPath = await new UnityCliToolService().GetVerifiedExecutablePathAsync();
            if (string.IsNullOrWhiteSpace(verifiedCliPath)
                || !Path.GetFullPath(verifiedCliPath).Equals(
                    Path.GetFullPath(request.CliPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The requested Unity CLI executable could not be verified.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = verifiedCliPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Unity CLI could not be started.");
            }
            process.StandardInput.Close();

            await using var outputStream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous);
            await using var writer = new StreamWriter(outputStream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            using var writeGate = new SemaphoreSlim(1, 1);

            async Task PumpAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    await writeGate.WaitAsync();
                    try
                    {
                        await writer.WriteLineAsync(line);
                    }
                    finally
                    {
                        writeGate.Release();
                    }
                }
            }

            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
            while (!process.HasExited)
            {
                if (File.Exists(cancelPath))
                {
                    process.Kill(entireProcessTree: true);
                    break;
                }

                await Task.Delay(100);
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            var exitCode = process.ExitCode;
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(
                    new OperationResult(exitCode),
                    RuntimeJsonContext.Default.OperationResult));
            return exitCode;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                try
                {
                    await File.WriteAllTextAsync(
                        resultPath,
                        JsonSerializer.Serialize(
                            new OperationResult(-1, exception.Message),
                            RuntimeJsonContext.Default.OperationResult));
                }
                catch
                {
                    // The helper exit code remains available when its result file cannot be written.
                }
            }

            return -1;
        }
    }

    private static void ValidateRequest(OperationRequest request)
    {
        if (request.Arguments.Count < 8
            || !request.Arguments[0].Equals("install-modules", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only Unity module installation is allowed in elevated mode.");
        }

        var hasEditorVersion = false;
        var hasModule = false;
        for (var index = 1; index < request.Arguments.Count; index++)
        {
            var argument = request.Arguments[index];
            switch (argument)
            {
                case "--editor-version":
                    if (++index >= request.Arguments.Count || !SafeIdentifier().IsMatch(request.Arguments[index]))
                    {
                        throw new InvalidDataException("The elevated Editor version is invalid.");
                    }
                    hasEditorVersion = true;
                    break;
                case "--module":
                    var moduleCount = 0;
                    while (index + 1 < request.Arguments.Count
                           && !request.Arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        index++;
                        if (!SafeIdentifier().IsMatch(request.Arguments[index]))
                        {
                            throw new InvalidDataException("An elevated module identifier is invalid.");
                        }
                        moduleCount++;
                    }
                    hasModule = moduleCount > 0;
                    break;
                case "--retries":
                    if (++index >= request.Arguments.Count
                        || !int.TryParse(request.Arguments[index], out var retries)
                        || retries is < 0 or > 10)
                    {
                        throw new InvalidDataException("The elevated retry count is invalid.");
                    }
                    break;
                case "--format":
                    if (++index >= request.Arguments.Count
                        || !request.Arguments[index].Equals("ndjson", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The elevated output format is invalid.");
                    }
                    break;
                case "--reinstall":
                case "--force":
                case "--no-childModules":
                case "--cm":
                case "--accept-eula":
                case "--yes":
                case "--non-interactive":
                case "--no-banner":
                case "--verbose":
                    break;
                default:
                    throw new InvalidDataException($"Elevated Unity CLI option '{argument}' is not allowed.");
            }
        }

        if (!hasEditorVersion || !hasModule)
        {
            throw new InvalidDataException("The elevated Unity module request is incomplete.");
        }
    }

    private static string GetOperationRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluenityHub",
            "ElevatedOperations");

    private static void EnsureValidOperationDirectory(string operationDirectory, string operationRoot)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(operationRoot));
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(operationDirectory));
        if (!Path.GetDirectoryName(fullDirectory)!.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(fullDirectory), "N", out _)
            || !Directory.Exists(fullDirectory)
            || File.GetAttributes(fullDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The elevated operation directory is not trusted.");
        }
    }

    private static void TryDeleteOperationDirectory(string operationDirectory, string operationRoot)
    {
        try
        {
            EnsureValidOperationDirectory(operationDirectory, operationRoot);
            Directory.Delete(operationDirectory, recursive: true);
        }
        catch
        {
            // A later maintenance pass can remove an operation directory still held by Windows.
        }
    }

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+_-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
