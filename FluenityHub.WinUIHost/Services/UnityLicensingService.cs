using System.Diagnostics;
using System.Text;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityLicensingService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMinutes(2);

    public bool IsClientAvailable => FindLicensingClient() is not null;

    public async Task<(bool Succeeded, string Message)> ActivateUlfAsync(
        string? serial,
        CancellationToken cancellationToken = default)
    {
        var clientPath = FindLicensingClient();
        if (clientPath is null)
        {
            return (false, "Unity Licensing Client is not installed.");
        }

        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return (false, NetworkConnectivityService.OfflineMessage);
        }

        var arguments = new List<string> { "--activate-ulf" };
        if (!string.IsNullOrWhiteSpace(serial))
        {
            arguments.Add("--serial");
            arguments.Add(serial.Trim());
        }

        var result = await RunClientAsync(
            clientPath,
            arguments,
            cancellationToken,
            ActivationTimeout);
        if (result.Succeeded)
        {
            return (true, string.IsNullOrWhiteSpace(serial)
                ? "Unity Personal license activated."
                : "Unity license activated.");
        }

        return (false, BuildSafeFailureMessage(
            result.Output,
            "Unity could not activate this license. Check your Unity account, serial number, and network connection."));
    }

    public async Task<(bool Succeeded, string Message)> CreateManualLicenseRequestAsync(
        string editorPath,
        string outputFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(editorPath))
        {
            return (false, "The selected Unity Editor could not be found.");
        }

        var outputDirectory = Path.GetDirectoryName(outputFilePath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return (false, "Choose a valid location for the license request file.");
        }

        Directory.CreateDirectory(outputDirectory);
        var startedAt = DateTime.UtcNow;
        var result = await RunEditorAsync(
            editorPath,
            ["-batchmode", "-createManualActivationFile", "-logFile", Path.Combine(outputDirectory, "FluenityHub-license-request.log")],
            outputDirectory,
            cancellationToken,
            ActivationTimeout);
        if (!result.Succeeded)
        {
            return (false, BuildSafeFailureMessage(
                result.Output,
                "Unity could not create the license request file."));
        }

        var generatedRequest = Directory
            .EnumerateFiles(outputDirectory, "*.alf", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTimeUtc >= startedAt.AddSeconds(-5))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (generatedRequest is null)
        {
            return (false, "Unity completed without creating a license request file.");
        }

        if (!string.Equals(generatedRequest.FullName, outputFilePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(generatedRequest.FullName, outputFilePath, overwrite: true);
        }

        return (true, "License request created.");
    }

    public async Task<(bool Succeeded, string Message)> ActivateManualLicenseFileAsync(
        string editorPath,
        string licenseFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(editorPath))
        {
            return (false, "The Unity Editor used for manual activation could not be found.");
        }

        if (!File.Exists(licenseFilePath)
            || !Path.GetExtension(licenseFilePath).Equals(".ulf", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Choose a valid Unity license file (.ulf).");
        }

        var result = await RunEditorAsync(
            editorPath,
            ["-batchmode", "-manualLicenseFile", licenseFilePath, "-logFile", Path.Combine(Path.GetTempPath(), "FluenityHub-license-activation.log")],
            Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
            cancellationToken,
            ActivationTimeout);
        return result.Succeeded
            ? (true, "Unity license file activated.")
            : (false, BuildSafeFailureMessage(
                result.Output,
                "Unity could not activate the selected license file."));
    }

    public async Task<UnityLicenseSnapshot> GetSnapshotAsync(
        bool synchronize,
        CancellationToken cancellationToken = default)
    {
        var clientPath = FindLicensingClient();
        if (clientPath is null)
        {
            return new UnityLicenseSnapshot(
                false,
                string.Empty,
                string.Empty,
                [],
                "Unity Licensing Client was not found. Install Unity Hub or a Unity Editor to manage licenses.");
        }

        var synchronizationSkippedOffline =
            synchronize && !NetworkConnectivityService.Current.CanAttemptInternet;
        if (synchronize && !synchronizationSkippedOffline)
        {
            // Best-effort sync entitlements call.
            // Ignore non-critical warnings (e.g. Floating license server not configured)
            // so active local licenses on disk are still read and displayed.
            await RunClientAsync(
                clientPath,
                ["--syncEntitlements"],
                cancellationToken);
        }

        var entitlementResult = await RunClientAsync(
            clientPath,
            ["--showEntitlements"],
            cancellationToken);
        var clientVersion = await ReadVersionAsync(clientPath, cancellationToken);
        if (!entitlementResult.Succeeded)
        {
            return new UnityLicenseSnapshot(
                true,
                clientPath,
                clientVersion,
                [],
                BuildSafeFailureMessage(
                    entitlementResult.Output,
                    "Unity license information could not be read."));
        }

        var licenses = ParseEntitlements(entitlementResult.Output);
        return new UnityLicenseSnapshot(
            true,
            clientPath,
            clientVersion,
            licenses,
            synchronizationSkippedOffline
                ? licenses.Count == 0
                    ? "You're offline. No locally stored Unity licenses were found."
                    : $"You're offline. Showing {licenses.Count} locally stored Unity license{(licenses.Count == 1 ? string.Empty : "s")}."
                : licenses.Count == 0
                ? "No active Unity licenses were found for this Windows user."
                : $"{licenses.Count} active Unity license{(licenses.Count == 1 ? string.Empty : "s")} found.");
    }

    private static string? FindLicensingClient()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Unity Hub",
                "UnityLicensingClient_V1",
                "Unity.Licensing.Client.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Unity Hub",
                "UnityLicensingClient_V1",
                "Unity.Licensing.Client.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<string> ReadVersionAsync(
        string clientPath,
        CancellationToken cancellationToken)
    {
        var result = await RunClientAsync(clientPath, ["--version"], cancellationToken);
        if (!result.Succeeded)
        {
            return "Unknown version";
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains("Unity.Licensing.Client", StringComparison.OrdinalIgnoreCase))
            ?? "Unknown version";
    }

    private static IReadOnlyList<UnityLicenseInfo> ParseEntitlements(string output)
    {
        if (string.IsNullOrWhiteSpace(output)
            || output.Contains("No licenses were found", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var rawList = new List<UnityLicenseInfo>();
        var current = new List<KeyValuePair<string, string>>();
        foreach (var rawLine in output.Split(['\r', '\n']))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                AddEntitlement(rawList, current);
                current.Clear();
                continue;
            }

            if (IsDiagnosticLine(line))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                separator = line.IndexOf('\t');
            }

            if (separator > 0)
            {
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(key)
                    && !string.IsNullOrWhiteSpace(value)
                    && !IsSensitiveField(key))
                {
                    current.Add(new KeyValuePair<string, string>(key, value));
                }
            }
        }

        AddEntitlement(rawList, current);

        // Filter out internal sub-entitlements (e.g. com.unity.xxx, feature flags)
        // Keep primary licenses (Personal, Pro, Plus, Enterprise, Student)
        var primaryLicenses = rawList
            .Where(l => IsPrimaryLicense(l.Name, l.Description))
            .DistinctBy(l => l.Name)
            .ToList();

        if (primaryLicenses.Count > 0)
        {
            return primaryLicenses;
        }

        // Fallback: If no explicit 'Personal/Pro' keyword match, return distinct non-technical entries
        var cleanFallback = rawList
            .Where(l => !l.Name.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(l => l.Name)
            .Take(3)
            .ToList();

        return cleanFallback.Count > 0 ? cleanFallback : rawList.Take(1).ToList();
    }

    private static bool IsPrimaryLicense(string name, string type)
    {
        var combined = $"{name} {type}".ToLowerInvariant();
        return combined.Contains("personal")
            || combined.Contains("pro")
            || combined.Contains("plus")
            || combined.Contains("enterprise")
            || combined.Contains("student")
            || combined.Contains("indie");
    }

    private static void AddEntitlement(
        ICollection<UnityLicenseInfo> target,
        IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        if (fields.Count == 0)
        {
            return;
        }

        var rawName = FindValue(fields, "Product Name", "ProductName", "Name", "EntitlementGroupId")
            ?? "Unity license";
        var name = CleanLicenseName(rawName);
        var description = FindValue(fields, "License Type", "LicenseType", "Entitlement Type", "Type")
            ?? "Active on this device";
        var details = string.Join(
            Environment.NewLine,
            fields
                .Where(field => !field.Value.Equals(rawName, StringComparison.OrdinalIgnoreCase) && !field.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select(field => $"{field.Key}: {field.Value}"));
        target.Add(new UnityLicenseInfo(name, description, details));
    }

    private static string CleanLicenseName(string rawName)
    {
        var lower = rawName.ToLowerInvariant();
        if (lower.Contains("personal")) return "Unity Personal";
        if (lower.Contains("pro")) return "Unity Pro";
        if (lower.Contains("plus")) return "Unity Plus";
        if (lower.Contains("enterprise")) return "Unity Enterprise";
        if (lower.Contains("student")) return "Unity Student";
        return rawName;
    }

    private static string? FindValue(
        IEnumerable<KeyValuePair<string, string>> fields,
        params string[] keys)
        => fields.FirstOrDefault(field => keys.Any(key =>
                field.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .Value;

    private static bool IsSensitiveField(string key)
        => key.Contains("token", StringComparison.OrdinalIgnoreCase)
           || key.Contains("password", StringComparison.OrdinalIgnoreCase)
           || key.Contains("serial", StringComparison.OrdinalIgnoreCase)
           || key.Contains("machinebinding", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiagnosticLine(string line)
        => line.StartsWith("Unable to retrieve", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("An error occurred when retrieving", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("at System.", StringComparison.OrdinalIgnoreCase);

    private static string BuildSafeFailureMessage(string output, string fallback)
    {
        var firstUsefulLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !IsDiagnosticLine(line));
        return string.IsNullOrWhiteSpace(firstUsefulLine)
            ? fallback
            : $"{fallback} {firstUsefulLine}";
    }

    private static async Task<(bool Succeeded, string Output)> RunClientAsync(
        string clientPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? commandTimeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = clientPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return (false, "Unity Licensing Client could not be started.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(commandTimeout ?? CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return (false, "Unity Licensing Client stopped responding.");
        }

        var output = new StringBuilder()
            .AppendLine(await standardOutput)
            .AppendLine(await standardError)
            .ToString()
            .Trim();
        return (process.ExitCode == 0, output);
    }

    private static async Task<(bool Succeeded, string Output)> RunEditorAsync(
        string editorPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan commandTimeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = editorPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return (false, "Unity Editor could not be started.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(commandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return (false, "Unity Editor stopped responding during manual activation.");
        }

        var output = new StringBuilder()
            .AppendLine(await standardOutput)
            .AppendLine(await standardError)
            .ToString()
            .Trim();
        return (process.ExitCode == 0, output);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already have exited.
        }
    }
}
