namespace FluenityHub_WinUIHost.Services;

public sealed class UnityEditorUninstallChangedEventArgs(string version, bool isUninstalling) : EventArgs
{
    public string Version { get; } = version;
    public bool IsUninstalling { get; } = isUninstalling;
}

public static class UnityEditorUninstallTracker
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> ActiveVersions = new(StringComparer.OrdinalIgnoreCase);

    public static event EventHandler<UnityEditorUninstallChangedEventArgs>? Changed;

    public static bool IsUninstalling(string version)
    {
        lock (Gate) return ActiveVersions.Contains(version);
    }

    public static bool TryBegin(string version)
    {
        lock (Gate)
        {
            if (!ActiveVersions.Add(version)) return false;
        }
        Changed?.Invoke(null, new(version, true));
        return true;
    }

    public static void Complete(string version)
    {
        lock (Gate)
        {
            if (!ActiveVersions.Remove(version)) return;
        }
        Changed?.Invoke(null, new(version, false));
    }
}
