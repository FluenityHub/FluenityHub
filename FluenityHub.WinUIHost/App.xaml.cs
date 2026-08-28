using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace FluenityHub_WinUIHost;

public partial class App : Application
{
    private static Mutex? _appMutex;
    private const string ActivationPipeName = "FluenityHub_Activation_987A";
    private readonly object _activationSync = new();
    private readonly Queue<string[]> _pendingExternalActivations = new();
    private Window? _window;
    private readonly bool _isElevatedUnityCliHelper;
    private readonly string? _elevatedUnityCliRequestPath;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public App()
    {
        Services.JumpListService.InitializeAppUserModelId();

        var arguments = Environment.GetCommandLineArgs();
        var helperArgumentIndex = Array.FindIndex(
            arguments,
            argument => argument.Equals(
                Services.ElevatedUnityCliRunner.HelperArgument,
                StringComparison.OrdinalIgnoreCase));
        _isElevatedUnityCliHelper = helperArgumentIndex >= 0;
        _elevatedUnityCliRequestPath = helperArgumentIndex >= 0 && helperArgumentIndex + 1 < arguments.Length
            ? arguments[helperArgumentIndex + 1]
            : null;
        if (_isElevatedUnityCliHelper)
        {
            InitializeComponent();
            return;
        }

        _appMutex = new Mutex(true, "FluenityHub_SingleInstance_Mutex_987A", out bool createdNew);
        if (!createdNew)
        {
            ForwardActivationToRunningInstance();
            var existingHwnd = FindWindow(null, "FluenityHub");
            if (existingHwnd != IntPtr.Zero)
            {
                ShowWindow(existingHwnd, 9 /* SW_RESTORE */);
                SetForegroundWindow(existingHwnd);
            }
            Environment.Exit(0);
            return;
        }

        UnhandledException += (sender, e) =>
        {
            TryWriteCrashLog(e.Exception);
        };

        InitializeComponent();
        _ = ListenForExternalActivationsAsync();

        try
        {
            if (AppNotificationManager.IsSupported())
            {
                AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
                AppNotificationManager.Default.Register();
            }
        }
        catch
        {
            // Ignore notification registration fallback
        }
    }

    private static void TryWriteCrashLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluenityHub",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            var details = $"[CRASH] {DateTimeOffset.Now:O}{Environment.NewLine}{exception}";
            File.WriteAllText(
                Path.Combine(logDirectory, "crash-latest.log"),
                Helpers.SensitiveDataRedactor.Redact(details));
        }
        catch
        {
            // Crash reporting must not replace the original unhandled exception.
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow.Instance?.RestoreWindow();
        });
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (_isElevatedUnityCliHelper)
        {
            _ = RunElevatedUnityCliHelperAsync();
            return;
        }

        var window = new MainWindow();
        lock (_activationSync)
        {
            _window = window;
        }

        window.Activate();
        HandleExternalArguments(Environment.GetCommandLineArgs().Skip(1).ToArray());
        DrainPendingExternalActivations();

        try
        {
            var settings = new Services.AppSettingsStore().Load();
            (MainWindow.Instance as MainWindow)?.SetAppTheme(settings.AppTheme);
        }
        catch
        {
            // Ignore settings load fallback on initial launch
        }

        // Initialize Windows Taskbar / Start Menu Jump List with tasks & recent projects
        _ = Services.JumpListService.RefreshAsync();
    }

    private async Task RunElevatedUnityCliHelperAsync()
    {
        var exitCode = string.IsNullOrWhiteSpace(_elevatedUnityCliRequestPath)
            ? -1
            : await Services.ElevatedUnityCliRunner.RunHelperAsync(_elevatedUnityCliRequestPath);
        Environment.Exit(exitCode);
    }

    private static void ForwardActivationToRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(1500);

            var payload = JsonSerializer.Serialize(
                Environment.GetCommandLineArgs().Skip(1).ToArray(),
                Models.AppJsonContext.Default.StringArray);
            using var writer = new StreamWriter(client);
            writer.Write(payload);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activation forwarding failed: {ex.Message}");
        }
    }

    private async Task ListenForExternalActivationsAsync()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var payload = await reader.ReadToEndAsync();
                var arguments = string.IsNullOrWhiteSpace(payload)
                    ? []
                    : JsonSerializer.Deserialize(payload, Models.AppJsonContext.Default.StringArray) ?? [];

                DispatchExternalActivation(arguments);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Activation receiver failed: {ex.Message}");
                await Task.Delay(250);
            }
        }
    }

    private void DispatchExternalActivation(string[] arguments)
    {
        Window? window;
        lock (_activationSync)
        {
            window = _window;
            if (window is null)
            {
                _pendingExternalActivations.Enqueue(arguments);
                return;
            }
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow.Instance?.RestoreWindow();
            HandleExternalArguments(arguments);
        });
    }

    private void DrainPendingExternalActivations()
    {
        string[][] pending;
        lock (_activationSync)
        {
            pending = _pendingExternalActivations.ToArray();
            _pendingExternalActivations.Clear();
        }

        foreach (var arguments in pending)
        {
            HandleExternalArguments(arguments);
        }
    }

    private static void HandleExternalArguments(IReadOnlyList<string> arguments)
    {
        string? projectPath = null;
        string? action = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if ((string.Equals(argument, "--project", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(argument, "--projectPath", StringComparison.OrdinalIgnoreCase)) &&
                index + 1 < arguments.Count)
            {
                projectPath = arguments[index + 1];
                index++;
            }
            else if ((string.Equals(argument, "--action", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(argument, "--openPage", StringComparison.OrdinalIgnoreCase)) &&
                     index + 1 < arguments.Count)
            {
                action = arguments[index + 1];
                index++;
            }
            else if (string.Equals(argument, "--new-project", StringComparison.OrdinalIgnoreCase))
            {
                action = "new-project";
            }
            else if (string.Equals(argument, "--install-editor", StringComparison.OrdinalIgnoreCase))
            {
                action = "install-editor";
            }
            else if (Directory.Exists(argument))
            {
                projectPath ??= argument;
            }
        }

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            MainWindow.Instance?.OpenExternalProjectPath(projectPath);
        }
        else if (!string.IsNullOrWhiteSpace(action))
        {
            MainWindow.Instance?.HandleExternalAction(action);
        }
    }
}
