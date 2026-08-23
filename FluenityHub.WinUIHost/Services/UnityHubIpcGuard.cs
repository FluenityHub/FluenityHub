using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluenityHub_WinUIHost.Services;

/// <summary>
/// Supplies the subset of Unity Hub's documented Editor IPC contract needed by
/// an Editor launched directly by FluenityHub. This keeps account and service
/// state available without starting Unity Hub's Electron process.
/// </summary>
internal sealed class UnityHubIpcGuard : IDisposable
{
    private const string PipeName = "Unity-hubIPCService";
    private const char MessageDelimiter = '\f';
    private static readonly object SyncRoot = new();
    private static readonly ConcurrentDictionary<int, UnityHubIpcGuard> EditorGuards = new();
    private static PipeServer? _sharedServer;
    private static int _leaseCount;

    private bool _attached;
    private bool _disposed;

    private UnityHubIpcGuard()
    {
    }

    public static bool TryAcquire(
        UnitySharedAccessToken? token,
        out UnityHubIpcGuard? guard,
        out string errorMessage)
    {
        lock (SyncRoot)
        {
            try
            {
                _sharedServer ??= new PipeServer(token);
                _sharedServer.UpdateAccount(token);
                _leaseCount++;
                guard = new UnityHubIpcGuard();
                errorMessage = string.Empty;
                return true;
            }
            catch (IOException ex)
            {
                guard = null;
                errorMessage = $"Unable to start Unity Editor account IPC: {ex.Message}";
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                guard = null;
                errorMessage = $"Unable to start Unity Editor account IPC: {ex.Message}";
                return false;
            }
        }
    }

    public void AttachToEditor(Process editorProcess)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _attached = true;
        EditorGuards[editorProcess.Id] = this;
        editorProcess.EnableRaisingEvents = true;
        editorProcess.Exited += OnEditorExited;
        UnityEditorLaunchDiagnostics.Write("HubIPC", $"Lifetime attached to Editor pid={editorProcess.Id}.");
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
        if (EditorGuards.TryRemove(editorProcess.Id, out var guard))
        {
            var exitCode = editorProcess.HasExited ? editorProcess.ExitCode : -1;
            UnityEditorLaunchDiagnostics.Write(
                "HubIPC",
                $"Editor pid={editorProcess.Id} exited; code={exitCode}; releasing server lease.");
            editorProcess.Exited -= guard.OnEditorExited;
            guard._attached = false;
            guard.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed || _attached)
        {
            return;
        }

        _disposed = true;
        lock (SyncRoot)
        {
            _leaseCount = Math.Max(0, _leaseCount - 1);
            if (_leaseCount == 0)
            {
                _sharedServer?.Dispose();
                _sharedServer = null;
            }
        }
    }

    private sealed class PipeServer : IDisposable
    {
        private static readonly JsonObject DefaultProductionUrls = new()
            {
                ["core"] = "https://core.cloud.unity3d.com",
                ["webauth"] = "https://accounts.unity3d.com",
                ["identity"] = "https://api.unity.com",
                ["license"] = "https://license.unity3d.com",
                ["activation"] = "https://activation.unity3d.com",
                ["perf"] = "https://perf.cloud.unity3d.com",
                ["portal"] = "https://id.unity.com",
                ["analytics"] = "https://analytics.cloud.unity3d.com",
                ["cdp-analytics"] = "https://prd-lender.cdp.internal.unity3d.com",
                ["analyticsOptOut"] = "https://config.uca.cloud.unity3d.com/",
                ["livePlatform"] = "https://live-platform-api.prd.ld.unity3d.com/graphql",
                ["servicesGateway"] = "https://services.unity.com",
                ["profileDashboard"] = "https://cloud.unity.com",
                ["login"] = "https://login.unity.com",
                ["packages"] = "https://packages.unity.com",
                ["appLinking"] = "https://services.api.unity.com",
                ["genesis_api_url"] = "https://api.unity.com",
                ["genesis_service_url"] = "https://id.unity.com",
                ["services-gateway"] = "https://services.unity.com",
                ["muse"] = string.Empty,
                ["ai"] = string.Empty
            };

        private readonly CancellationTokenSource _cancellation = new();
        private readonly ConcurrentDictionary<int, Task> _clients = new();
        private UnitySharedAccessToken? _token;
        private NamedPipeServerStream? _waitingPipe;
        private int _nextClientId;
        private bool _disposed;

        public PipeServer(UnitySharedAccessToken? token)
        {
            _token = token;
            _waitingPipe = CreatePipe();
            UnityEditorLaunchDiagnostics.Write("HubIPC", $"Listening on {PipeName}.");
            _ = AcceptConnectionsAsync();
        }

        public void UpdateAccount(UnitySharedAccessToken? token)
            => Volatile.Write(ref _token, token);

        private async Task AcceptConnectionsAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = Interlocked.Exchange(ref _waitingPipe, null) ?? CreatePipe();
                    await pipe.WaitForConnectionAsync(_cancellation.Token);

                    var clientId = Interlocked.Increment(ref _nextClientId);
                    UnityEditorLaunchDiagnostics.Write("HubIPC", $"Client {clientId} connected.");
                    var clientTask = HandleClientAsync(clientId, pipe);
                    pipe = null;
                    _clients[clientId] = clientTask;
                    _ = clientTask.ContinueWith(
                        completedTask => _clients.TryRemove(clientId, out var _),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    if (!_cancellation.IsCancellationRequested)
                    {
                        _waitingPipe = CreatePipe();
                    }
                }
                catch (OperationCanceledException)
                {
                    pipe?.Dispose();
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    pipe?.Dispose();
                    UnityEditorLaunchDiagnostics.Write(
                        "HubIPC",
                        $"Accept failed but server remains active: {ex.GetType().Name}: {ex.Message}");
                    if (!_cancellation.IsCancellationRequested)
                    {
                        await Task.Delay(250);
                        _waitingPipe = CreatePipe();
                    }
                }
            }

            UnityEditorLaunchDiagnostics.Write("HubIPC", "Accept loop stopped.");
        }

        private async Task HandleClientAsync(int clientId, NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                var buffer = new byte[16 * 1024];
                var pending = new StringBuilder();

                try
                {
                    while (!_cancellation.IsCancellationRequested && pipe.IsConnected)
                    {
                        var bytesRead = await pipe.ReadAsync(buffer, _cancellation.Token);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        pending.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                        await ProcessMessagesAsync(pipe, pending, _cancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal server shutdown.
                }
                catch (IOException ex)
                {
                    UnityEditorLaunchDiagnostics.Write(
                        "HubIPC",
                        $"Client {clientId} pipe failure: {ex.GetType().Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    UnityEditorLaunchDiagnostics.Write(
                        "HubIPC",
                        $"Client {clientId} handler failure: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    UnityEditorLaunchDiagnostics.Write("HubIPC", $"Client {clientId} disconnected.");
                    _clients.TryRemove(clientId, out _);
                }
            }
        }

        private async Task ProcessMessagesAsync(
            NamedPipeServerStream pipe,
            StringBuilder pending,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var content = pending.ToString();
                var delimiterIndex = content.IndexOf(MessageDelimiter);
                if (delimiterIndex < 0)
                {
                    return;
                }

                var envelope = content[..delimiterIndex];
                pending.Remove(0, delimiterIndex + 1);
                if (string.IsNullOrWhiteSpace(envelope))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(envelope);
                    if (!document.RootElement.TryGetProperty("type", out var typeElement)
                        || typeElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var eventName = typeElement.GetString();
                    UnityEditorLaunchDiagnostics.Write(
                        "HubIPC",
                        $"Received {eventName ?? "<missing-type>"}.");
                    var response = CreateResponse(eventName);
                    if (response is not null)
                    {
                        await WriteEnvelopeAsync(pipe, response.Value.EventName, response.Value.Data, cancellationToken);
                        UnityEditorLaunchDiagnostics.Write(
                            "HubIPC",
                            $"Sent {response.Value.EventName}.");
                    }
                    else
                    {
                        UnityEditorLaunchDiagnostics.Write(
                            "HubIPC",
                            IsHandledWithoutResponse(eventName)
                                ? $"Handled {eventName} without a response."
                                : $"No local handler for {eventName ?? "<missing-type>"}.");
                    }
                }
                catch (JsonException)
                {
                    UnityEditorLaunchDiagnostics.Write("HubIPC", "Rejected malformed JSON envelope.");
                    // Ignore malformed IPC input without reflecting it or any
                    // potentially sensitive content into application logs.
                }
            }
        }

        private (string EventName, JsonNode Data)? CreateResponse(string? eventName)
        {
            var token = Volatile.Read(ref _token);
            var isLoggedIn = token is not null && !string.IsNullOrWhiteSpace(token.Value);
            return eventName switch
            {
                "health:check" => (eventName, new JsonObject { ["health"] = true }),
                "connectInfo:get" => ("connectInfo:changed", new JsonObject
                {
                    ["error"] = false,
                    ["initialized"] = true,
                    ["loggedIn"] = isLoggedIn,
                    ["maintenance"] = false,
                    ["online"] = isLoggedIn,
                    ["ready"] = true,
                    ["showLoginWindow"] = false,
                    ["workOffline"] = !isLoggedIn
                }),
                "userInfo:get" => ("userInfo:changed", isLoggedIn ? new JsonObject
                {
                    ["userId"] = token!.Account.ForeignKey,
                    ["displayName"] = token.Account.DisplayName,
                    ["name"] = token.Account.Email,
                    ["primaryOrg"] = token.Account.PrimaryOrganization,
                    ["accessToken"] = token.Value,
                    ["accessTokenExpiration"] = token.Expiration ?? 0,
                    ["valid"] = true,
                    ["whitelisted"] = true,
                    ["organizationForeignKeys"] = string.Empty,
                    ["cached"] = false
                } : new JsonObject
                {
                    ["valid"] = false,
                    ["loggedIn"] = false
                }),
                "orgInfo:get" => ("orgInfo:changed", new JsonArray()),
                "config:get" or "config:get-default" or "config:get-urls" =>
                    (eventName, LoadProductionUrls()),
                _ => null
            };
        }

        private static bool IsHandledWithoutResponse(string? eventName)
            => eventName is "project:open-in-editor-url";

        private static JsonObject LoadProductionUrls()
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnityHub",
                "cloudConfig.json");

            try
            {
                var file = new FileInfo(configPath);
                if (!file.Exists || file.Length is <= 0 or > 1024 * 1024)
                {
                    return (JsonObject)DefaultProductionUrls.DeepClone();
                }

                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return (JsonObject)DefaultProductionUrls.DeepClone();
                }

                var values = new JsonObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.Length is 0 or > 128
                        || !property.Name.All(character =>
                            char.IsAsciiLetterOrDigit(character)
                            || character is '-' or '_'))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind is JsonValueKind.String
                        or JsonValueKind.Number
                        or JsonValueKind.True
                        or JsonValueKind.False
                        or JsonValueKind.Null)
                    {
                        values[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    }
                }

                if (values.ContainsKey("genesis_api_url")
                    && values.ContainsKey("genesis_service_url")
                    && values.ContainsKey("core"))
                {
                    UnityEditorLaunchDiagnostics.Write(
                        "HubIPC",
                        $"Loaded {values.Count} service configuration values from Unity's shared cache.");
                    return values;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                UnityEditorLaunchDiagnostics.Write(
                    "HubIPC",
                    $"Unity service configuration cache unavailable; using defaults: {ex.GetType().Name}.");
            }

            return (JsonObject)DefaultProductionUrls.DeepClone();
        }

        private static async Task WriteEnvelopeAsync(
            NamedPipeServerStream pipe,
            string eventName,
            JsonNode data,
            CancellationToken cancellationToken)
        {
            var json = new JsonObject
            {
                ["type"] = eventName,
                ["data"] = data
            }.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(json + MessageDelimiter);
            UnityEditorLaunchDiagnostics.Write(
                "HubIPC",
                $"Writing {eventName}; bytes={bytes.Length}.");
            using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeCancellation.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await pipe.WriteAsync(bytes, writeCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"Timed out writing the {eventName} response to Unity Editor.");
            }
            // Do not flush a Windows named pipe here. FlushFileBuffers waits for
            // the peer to drain all buffered bytes, which can deadlock Unity's
            // synchronous IPC request path. Unity Hub's @unity/hub-ipc server
            // writes the framed message without an explicit flush.
            UnityEditorLaunchDiagnostics.Write(
                "HubIPC",
                $"Write completed for {eventName}.");
        }

        private static NamedPipeServerStream CreatePipe()
            => new(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 64 * 1024,
                outBufferSize: 64 * 1024);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnityEditorLaunchDiagnostics.Write("HubIPC", "Server shutdown requested.");
            _cancellation.Cancel();
            Interlocked.Exchange(ref _waitingPipe, null)?.Dispose();
            _cancellation.Dispose();
        }
    }
}
