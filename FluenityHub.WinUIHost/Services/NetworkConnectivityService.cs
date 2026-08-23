using System.Net.Http;
using System.Net.Sockets;
using Windows.Networking.Connectivity;

namespace FluenityHub_WinUIHost.Services;

public enum AppNetworkState
{
    Unknown,
    Offline,
    Limited,
    Online
}

public sealed class NetworkStateChangedEventArgs(
    AppNetworkState previousState,
    AppNetworkState currentState) : EventArgs
{
    public AppNetworkState PreviousState { get; } = previousState;
    public AppNetworkState CurrentState { get; } = currentState;
}

public sealed class OfflineException(string message) : InvalidOperationException(message);

/// <summary>
/// Tracks the Windows connection profile and provides consistent network errors.
/// The Windows-reported state is a hint: limited connections are allowed to try
/// the actual service, while a definite offline state fails immediately.
/// </summary>
public sealed class NetworkConnectivityService
{
    public const string OfflineMessage =
        "You're offline. Connect to the internet and try again.";

    private static readonly Lazy<NetworkConnectivityService> LazyCurrent =
        new(() => new NetworkConnectivityService());

    private readonly object _stateLock = new();
    private AppNetworkState _state;

    private NetworkConnectivityService()
    {
        _state = AppNetworkState.Unknown;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _ = Task.Run(Refresh);
    }

    public static NetworkConnectivityService Current => LazyCurrent.Value;

    public AppNetworkState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool CanAttemptInternet => Refresh() != AppNetworkState.Offline;

    public event EventHandler<NetworkStateChangedEventArgs>? StateChanged;

    public AppNetworkState Refresh()
    {
        var current = ReadCurrentState();
        AppNetworkState previous;
        lock (_stateLock)
        {
            previous = _state;
            _state = current;
        }

        if (previous != current)
        {
            StateChanged?.Invoke(this, new(previous, current));
        }

        return current;
    }

    public void EnsureCanAttemptInternet()
    {
        if (Refresh() == AppNetworkState.Offline)
        {
            throw new OfflineException(OfflineMessage);
        }
    }

    public string GetUserMessage(Exception exception, string serviceName)
    {
        var root = Unwrap(exception);
        if (State == AppNetworkState.Offline || root is OfflineException)
        {
            return OfflineMessage;
        }

        if (root is OperationCanceledException or TimeoutException)
        {
            return $"{serviceName} did not respond in time. Check your connection and try again.";
        }

        if (root is HttpRequestException or SocketException)
        {
            return $"Could not reach {serviceName}. Check your connection and try again.";
        }

        return root.Message;
    }

    private void OnNetworkStatusChanged(object sender)
        => Refresh();

    private static AppNetworkState ReadCurrentState()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.GetNetworkConnectivityLevel() switch
            {
                NetworkConnectivityLevel.None or null => AppNetworkState.Offline,
                NetworkConnectivityLevel.LocalAccess or
                    NetworkConnectivityLevel.ConstrainedInternetAccess => AppNetworkState.Limited,
                NetworkConnectivityLevel.InternetAccess => AppNetworkState.Online,
                _ => AppNetworkState.Unknown
            };
        }
        catch
        {
            // Failure to query Windows must not incorrectly disable online actions.
            return AppNetworkState.Unknown;
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }
}
