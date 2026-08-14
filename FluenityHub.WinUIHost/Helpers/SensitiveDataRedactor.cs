using System.Text.RegularExpressions;

namespace FluenityHub_WinUIHost.Helpers;

/// <summary>
/// Defense-in-depth filtering for diagnostics that can contain output from
/// external tools. Credentials must still never be intentionally logged.
/// </summary>
internal static partial class SensitiveDataRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = AuthorizationValue().Replace(value, "$1 [REDACTED]");
        redacted = KnownPersonalAccessToken().Replace(redacted, "[REDACTED]");
        return SecretPropertyValue().Replace(redacted, "$1[REDACTED]$3");
    }

    [GeneratedRegex(
        @"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex(
        @"(?i)\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{10,})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownPersonalAccessToken();

    [GeneratedRegex(
        @"(?i)([\""']?(?:access_?token|unity_?token|refresh_?token|private-token|password|secret)[\""']?\s*[:=]\s*[\""']?)([^\""'\s,&}]+)([\""']?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPropertyValue();
}
