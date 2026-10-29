using System.Diagnostics;

namespace FluenityHub_WinUIHost.Services;

internal sealed record GitAccountCredential(string UserName, string Password);

internal static class GitCredentialManagerService
{
    public static async Task<GitAccountCredential?> GetAsync(string provider, string? repository,
        bool interactive, CancellationToken cancellationToken = default, string? preferredUser = null)
    {
        var host = provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase) ? "gitlab.com" : "github.com";
        var scope = NormalizeScope(repository);
        var settings = new AppSettingsStore().Load();
        var user = ResolveAccount(settings.GitAccounts, host, scope);
        if (!string.IsNullOrWhiteSpace(preferredUser)) user = preferredUser.Trim();
        if (user?.Any(char.IsControl) == true) throw new ArgumentException("Enter a valid Git account name.");
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("credential-manager");
        startInfo.ArgumentList.Add("get");
        startInfo.Environment["GCM_INTERACTIVE"] = interactive ? "always" : "never";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(startInfo) ?? throw new IOException("Git Credential Manager could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync($"protocol=https\nhost={host}\npath={scope}\n" +
            (string.IsNullOrEmpty(user) ? string.Empty : $"username={user}\n") + "\n");
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var result = await output;
            await error;
            if (process.ExitCode != 0) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in result.Split('\n'))
            {
                var pair = line.TrimEnd('\r').Split('=', 2);
                if (pair.Length == 2) fields[pair[0]] = pair[1];
            }
            return fields.TryGetValue("username", out var account) && fields.TryGetValue("password", out var password)
                ? new(account, password) : null;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Git sign-in timed out. Try again.");
        }
    }

    public static void SetAccount(string provider, string? repository, string userName)
    {
        if (userName.Any(char.IsControl)) throw new ArgumentException("Enter a valid Git account name.");
        var host = provider.Equals("gitlab", StringComparison.OrdinalIgnoreCase) ? "gitlab.com" : "github.com";
        var store = new AppSettingsStore();
        var settings = store.Load();
        var key = host + "/" + NormalizeScope(repository);
        if (string.IsNullOrWhiteSpace(userName)) settings.GitAccounts.Remove(key);
        else settings.GitAccounts[key] = userName.Trim();
        store.Save(settings, updateGitAccounts: true);
    }

    internal static string NormalizeScope(string? scope)
    {
        var value = (scope ?? string.Empty).Trim().Trim('/');
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '?' or '#' or '=')
            || value.Split('/').Any(s => s is "." or "..") || value.Contains("//", StringComparison.Ordinal) || value.Length > 512)
            throw new ArgumentException("Use an organization name or organization/repository.");
        return value;
    }

    internal static string? ResolveAccount(IReadOnlyDictionary<string, string> accounts, string host, string scope)
    {
        while (true)
        {
            var key = host + "/" + scope;
            var account = accounts.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(account)) return account;
            if (scope.Length == 0) return null;
            var separator = scope.LastIndexOf('/');
            scope = separator < 0 ? string.Empty : scope[..separator];
        }
    }

    public static string? ConfigureGitProcess(ProcessStartInfo startInfo, string remote)
    {
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || (uri.Host != "github.com" && uri.Host != "gitlab.com")) return null;
        var settings = new AppSettingsStore().Load();
        var scope = uri.AbsolutePath.Trim('/');
        if (scope.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) scope = scope[..^4];
        var account = ResolveAccount(settings.GitAccounts, uri.Host, scope);
        if (string.IsNullOrWhiteSpace(account)) return null;
        startInfo.Environment["GIT_CONFIG_COUNT"] = "3";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "credential.helper";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = string.Empty;
        startInfo.Environment["GIT_CONFIG_KEY_1"] = "credential.helper";
        startInfo.Environment["GIT_CONFIG_VALUE_1"] = "manager";
        startInfo.Environment["GIT_CONFIG_KEY_2"] = "credential.username";
        startInfo.Environment["GIT_CONFIG_VALUE_2"] = account;
        return account;
    }
}
