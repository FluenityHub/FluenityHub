namespace FluenityHub_WinUIHost.Models;

public sealed record UnityExternalActivityInfo
{
    public string Title { get; }
    public string Status { get; }

    public UnityExternalActivityInfo(string title, string status)
    {
        Title = title;
        Status = status;
    }
}
