namespace FluenityHub_WinUIHost.Services;

internal static class UnityAccountConnectionState
{
    private static readonly object SyncRoot = new();

    private static string MarkerFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluenityHub.WinUIHost",
        "unity-account-disconnected");

    public static bool IsDisconnected
    {
        get
        {
            lock (SyncRoot)
            {
                return File.Exists(MarkerFilePath);
            }
        }
    }

    public static void SetDisconnected(bool isDisconnected)
    {
        lock (SyncRoot)
        {
            if (isDisconnected)
            {
                var directory = Path.GetDirectoryName(MarkerFilePath)
                    ?? throw new InvalidOperationException("The Unity account state directory is unavailable.");
                Directory.CreateDirectory(directory);
                File.WriteAllText(MarkerFilePath, string.Empty);
            }
            else if (File.Exists(MarkerFilePath))
            {
                File.Delete(MarkerFilePath);
            }
        }
    }
}