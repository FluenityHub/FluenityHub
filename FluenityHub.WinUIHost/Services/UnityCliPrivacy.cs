using System.Diagnostics;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityCliPrivacy
{
    public static void Apply(ProcessStartInfo startInfo)
    {
        startInfo.Environment["UNITY_NO_CLI_INVOKED_TELEMETRY"] = "1";
        startInfo.Environment["UNITY_NO_CRASH_REPORT"] = "1";
    }
}
