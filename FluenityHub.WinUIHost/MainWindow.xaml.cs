using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using FluenityHub_WinUIHost.Helpers;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using WinUIEx;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluenityHub_WinUIHost;

public sealed partial class MainWindow : Window
{

    private static readonly TimeSpan UnityAccountStatusFreshness = TimeSpan.FromSeconds(30);
    private static readonly Vector3 AccountButtonNormalScale = new(1f, 1f, 1f);
    private static readonly Vector3 AccountButtonHoverScale = new(0.94f, 0.94f, 1f);
    private static readonly Vector3 AccountButtonPressedScale = new(0.88f, 0.88f, 1f);

    public static MainWindow? Instance { get; private set; }
    private NativeTrayIcon? _trayIcon;
    private bool _isExitingFromTray;
    private readonly DispatcherTimer _priorityMonitorTimer = new();
    private readonly DispatcherTimer _taskbarErrorClearTimer = new();
    private readonly UnityCliAuthService _unityCliAuthService = new();
    private readonly UnityLogoutSecurityService _unityLogoutSecurityService = new();
    private readonly UnityModuleInstallationManager _moduleInstallationManager =
        UnityModuleInstallationManager.Instance;
    private CancellationTokenSource? _unityAccountCancellation;
    private UnityCliAuthState? _unityAccountState;
    private DateTimeOffset _unityAccountStatusCheckedAt;
    private bool _isUnityAccountBusy;
    private bool _wasWindowDeactivated;
    private bool _refreshUnityAccountWhenIdle;
    private Task? _sharedUnityAccountRefreshTask;
    private TaskbarProgressService? _taskbarProgressService;
    private int _activeEditorUninstallCount;
    private bool _restoreAfterUnityExit;
    private bool _launchedUnityWasObserved;
    private DateTimeOffset _unityLaunchRequestedAt;
    private bool _hasStartedUpdateCheck;

    public event EventHandler<UnityCliAuthState>? UnityAccountStateChanged;
    public UnityCliAuthState? UnityAccountState => _unityAccountState;

    public IntPtr WindowHandle => WindowNative.GetWindowHandle(this);

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        AccountButton.PointerEntered += OnAccountButtonPointerEntered;
        AccountButton.PointerExited += OnAccountButtonPointerExited;
        AccountButton.PointerCanceled += OnAccountButtonPointerCanceled;
        AccountButton.PointerCaptureLost += OnAccountButtonPointerCaptureLost;
        AccountButton.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnAccountButtonPointerPressed),
            handledEventsToo: true);
        AccountButton.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnAccountButtonPointerReleased),
            handledEventsToo: true);
        _moduleInstallationManager.AttachDispatcher(DispatcherQueue);
        _moduleInstallationManager.OperationChanged += OnModuleInstallationChanged;
        JumpListService.SetWindowAppUserModelId(WindowHandle);
        _taskbarProgressService = new TaskbarProgressService(WindowHandle);
        _taskbarErrorClearTimer.Interval = TimeSpan.FromSeconds(6);
        _taskbarErrorClearTimer.Tick += OnTaskbarErrorClearTimerTick;
        Activated += OnWindowActivatedForTaskbarProgress;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppRootGrid.ActualThemeChanged += OnAppRootActualThemeChanged;
        UpdateTitleBarColors();

        // Calculate initial window bounds (1240x700 DIPs) scaled for current monitor DPI
        try
        {
            var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var rawDpi = Windows.Win32.PInvoke.GetDpiForWindow(new Windows.Win32.Foundation.HWND(hwnd));
            var scale = (rawDpi == 0 ? 96.0 : rawDpi) / 96.0;

            var widthPx = (int)(1240 * scale);
            var heightPx = (int)(700 * scale);

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                var workArea = displayArea.WorkArea;
                var x = workArea.X + (workArea.Width - widthPx) / 2;
                var y = workArea.Y + (workArea.Height - heightPx) / 2;
                AppWindow.MoveAndResize(new RectInt32(x, y, widthPx, heightPx));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(widthPx, heightPx));
            }
        }
        catch
        {
            // Fallback if DPI scaling call fails
        }

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Ignore icon loading error if asset missing
        }

        // Apply saved user theme on startup
        try
        {
            var settingsStore = new AppSettingsStore();
            var settings = settingsStore.Load();
            SetAppTheme(settings.AppTheme);
            EvaluateProcessPriority();
        }
        catch
        {
            // Ignore settings load error on startup
        }

        _priorityMonitorTimer.Interval = TimeSpan.FromSeconds(3);
        _priorityMonitorTimer.Tick += OnPriorityMonitorTimerTick;
        _priorityMonitorTimer.Start();

        ShowInitialContent();
    }

    private void ShowInitialContent()
    {
        ShowMainContent();
    }

    private void ShowMainContent(string? destination = null)
    {
        AccountButton.Visibility = Visibility.Visible;
        if (RootFrame.Content is not MainPage)
        {
            RootFrame.Navigate(
                typeof(MainPage),
                null,
                new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }

        if (RootFrame.Content is not MainPage mainPage)
        {
            return;
        }

        mainPage.InstallUpdateRequested += OnInstallUpdateRequested;
        mainPage.SeeChangesRequested += OnSeeChangesRequested;

        if (!string.IsNullOrWhiteSpace(destination))
        {
            mainPage.HandleExternalAction(destination);
        }

        if (!_hasStartedUpdateCheck)
        {
            _hasStartedUpdateCheck = true;
            CheckAppUpdatesOnLaunch();
        }
    }

    public void EnsureMainContentForExternalActivation()
    {
        if (RootFrame.Content is not MainPage)
        {
            ShowMainContent();
        }
    }

    private AppUpdateInfo? _currentUpdateInfo;

    private async void CheckAppUpdatesOnLaunch()
    {
        try
        {
            var updateInfo = await AppUpdateService.CheckForUpdatesAsync();
            _currentUpdateInfo = updateInfo;

            if (updateInfo.HasUpdate)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MainPage.Instance?.ShowAppUpdate($"FluenityHub v{updateInfo.LatestVersion} is available", string.IsNullOrWhiteSpace(updateInfo.ReleaseTitle) ? "A new version of FluenityHub is available with new features and performance improvements." : updateInfo.ReleaseTitle);
                });
            }
        }
        catch
        {
            // Ignore update check error on launch
        }
    }

    private void OnInstallUpdateRequested(object? sender, EventArgs e)
    {
        var targetUrl = _currentUpdateInfo?.DownloadUrl
            ?? _currentUpdateInfo?.ReleaseUrl
            ?? "https://github.com/FluenityHub/FluenityHub/releases";
        OpenExternalUrl(targetUrl);
    }

    private void OnSeeChangesRequested(object? sender, EventArgs e)
    {
        var targetUrl = _currentUpdateInfo?.ReleaseUrl
            ?? "https://github.com/FluenityHub/FluenityHub/releases";
        OpenExternalUrl(targetUrl);
    }
    private static void OpenExternalUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser opening failure
        }
    }

    private void OnAccountButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AccountButton.Scale = AccountButtonHoverScale;
    }

    private void OnAccountButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        AccountButton.Scale = AccountButtonPressedScale;
    }

    private void OnAccountButtonPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        AccountButton.Scale = AccountButtonNormalScale;
    }

    private void OnAccountButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        AccountButton.Scale = AccountButtonNormalScale;
    }

    private void OnAccountButtonPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        AccountButton.Scale = AccountButtonNormalScale;
    }

    private void OnAccountButtonPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        AccountButton.Scale = AccountButtonNormalScale;
    }

    private async void OnAccountFlyoutOpening(object sender, object args)
    {
        if (_isUnityAccountBusy
            || (_unityAccountState is not null
                && DateTimeOffset.UtcNow - _unityAccountStatusCheckedAt < UnityAccountStatusFreshness))
        {
            return;
        }

        await RefreshUnityAccountStateAsync();
    }

    public void UpdateUnityAccountState(UnityCliAuthState state)
    {
        _unityAccountState = state;
        _unityAccountStatusCheckedAt = DateTimeOffset.UtcNow;

        var displayName = state.IsLoggedIn && !string.IsNullOrWhiteSpace(state.DisplayName)
            ? state.DisplayName
            : "Unity ID";
        var accountDescription = state.IsLoggedIn
            ? !string.IsNullOrWhiteSpace(state.Email)
                ? state.Email
                : !string.IsNullOrWhiteSpace(state.Mode)
                    ? state.Mode
                    : "Signed in"
            : (string.IsNullOrWhiteSpace(state.Message) || state.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
                ? "Not signed in"
                : state.Message;

        TitleBarPersonPicture.DisplayName = state.IsLoggedIn ? displayName : string.Empty;
        TitleBarPersonPicture.Initials = state.IsLoggedIn ? string.Empty : "\uE77B";
        TitleBarPersonPicture.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(
            state.IsLoggedIn ? "Segoe UI Variable" : "Segoe Fluent Icons");
        AccountMenuHeader.DisplayName = displayName;
        AccountMenuHeader.Description = string.IsNullOrWhiteSpace(accountDescription)
            ? (state.IsLoggedIn ? "Signed in" : "Not signed in")
            : accountDescription;
        AccountMenuHeader.Visibility = state.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountMenuHeaderSeparator.Visibility = state.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        SwitchUnityAccountMenuItem.Text = state.IsLoggedIn ? "Switch account" : "Sign in";
        SwitchUnityAccountMenuItem.IsEnabled = !_isUnityAccountBusy && state.IsCliAvailable;
        SignOutUnityMenuItem.IsEnabled = !_isUnityAccountBusy && state.IsCliAvailable && state.IsLoggedIn;
        SignOutUnityMenuItem.Visibility = state.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;

        var switchActionName = state.IsLoggedIn ? "Switch Unity account" : "Sign in to Unity";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AccountButton, $"Unity account, {displayName}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            AccountMenuHeader,
            $"Unity account, {displayName}, {AccountMenuHeader.Description}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SwitchUnityAccountMenuItem, switchActionName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(AccountButton, accountDescription);
        ToolTipService.SetToolTip(AccountButton, state.IsLoggedIn ? displayName : "Unity account");
        UnityAccountStateChanged?.Invoke(this, state);

        if (!state.IsCliAvailable)
        {
            ShowAccountStatus("Unity CLI is required", state.Message);
        }
    }

    private void SetUnityAccountBusy(bool isBusy)
    {
        _isUnityAccountBusy = isBusy;
        AccountMenuHeader.IsBusy = isBusy;
        SwitchUnityAccountMenuItem.IsEnabled = !isBusy && _unityAccountState?.IsCliAvailable == true;
        SignOutUnityMenuItem.IsEnabled = !isBusy
            && _unityAccountState?.IsCliAvailable == true
            && _unityAccountState.IsLoggedIn;

        if (!isBusy && _refreshUnityAccountWhenIdle)
        {
            _refreshUnityAccountWhenIdle = false;
            _ = RefreshUnityAccountStateAsync();
        }
    }

    private void ShowAccountStatus(string title, string message)
    {
        var status = string.IsNullOrWhiteSpace(message)
            ? title
            : $"{title}: {message}";
        ToolTipService.SetToolTip(AccountButton, status);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(AccountButton, status);
    }

    private void OnOpenUnityLicensesClick(object sender, RoutedEventArgs e)
    {
        AccountMenuFlyout.Hide();
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.NavigateToLicenses();
        }
    }

    private async void OnManageOrganizationClick(object sender, RoutedEventArgs e)
    {
        AccountMenuFlyout.Hide();
        await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://cloud.unity.com/home/organizations"));
    }

    private async void OnUnityDiscussionsClick(object sender, RoutedEventArgs e)
    {
        AccountMenuFlyout.Hide();
        await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://discussions.unity.com/"));
    }

    private async void OnSwitchUnityAccountClick(object sender, RoutedEventArgs e)
    {
        if (_isUnityAccountBusy || _unityAccountState?.IsCliAvailable != true)
        {
            return;
        }

        var switchExistingAccount = _unityAccountState.IsLoggedIn;
        AccountMenuFlyout.Hide();
        await RunUnityAccountOperationAsync(async cancellationToken =>
        {
            if (switchExistingAccount)
            {
                var signedOutState = await _unityCliAuthService.LogoutAsync(cancellationToken);
                _unityLogoutSecurityService.ApplyAfterLogout(signedOutState);
                UpdateUnityAccountState(signedOutState);
                if (signedOutState.IsLoggedIn)
                {
                    return signedOutState;
                }
            }

            return await _unityCliAuthService.LoginAsync(cancellationToken);
        },
        switchExistingAccount ? "Unity account switch failed" : "Unity sign-in failed",
        switchExistingAccount ? "Unity account switched" : "Signed in to Unity",
        expectLoggedIn: true);
    }

    private async void OnSignOutUnityClick(object sender, RoutedEventArgs e)
    {
        if (_isUnityAccountBusy
            || _unityAccountState?.IsCliAvailable != true
            || !_unityAccountState.IsLoggedIn)
        {
            return;
        }

        AccountMenuFlyout.Hide();
        var confirmation = new ContentDialog
        {
            XamlRoot = AppRootGrid.XamlRoot,
            Title = "Sign out of Unity?",
            Content = "Signing out removes the Unity CLI session from Windows Credential Manager. Unity Personal and named-user licenses may no longer remain active.",
            PrimaryButtonText = "Sign out",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = CurrentTheme
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunUnityAccountOperationAsync(
            async cancellationToken =>
            {
                var state = await _unityCliAuthService.LogoutAsync(cancellationToken);
                _unityLogoutSecurityService.ApplyAfterLogout(state);
                return state;
            },
            "Unity sign-out failed",
            "Signed out of Unity",
            expectLoggedIn: false);
    }

    private async Task RunUnityAccountOperationAsync(
        Func<CancellationToken, Task<UnityCliAuthState>> operation,
        string failureTitle,
        string successTitle,
        bool expectLoggedIn)
    {
        _unityAccountCancellation?.Cancel();
        _unityAccountCancellation?.Dispose();
        _unityAccountCancellation = new CancellationTokenSource();
        var cancellationToken = _unityAccountCancellation.Token;
        SetUnityAccountBusy(true);
        try
        {
            var state = await operation(cancellationToken);
            UpdateUnityAccountState(state);
            if (state.IsLoggedIn != expectLoggedIn)
            {
                ShowAccountStatus(failureTitle, state.Message);
                ShowUnityAccountNotification(failureTitle, state.Message);
            }
            else
            {
                ShowUnityAccountNotification(
                    successTitle,
                    state.IsLoggedIn
                        ? (!string.IsNullOrWhiteSpace(state.Email) ? state.Email : state.DisplayName)
                        : state.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed or another account action replaced this request.
        }
        catch (Exception ex)
        {
            ShowAccountStatus(failureTitle, ex.Message);
            ShowUnityAccountNotification(failureTitle, ex.Message);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetUnityAccountBusy(false);
            }
        }
    }

    private static void ShowUnityAccountNotification(string title, string message)
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            var builder = new AppNotificationBuilder().AddText(title);
            if (!string.IsNullOrWhiteSpace(message))
            {
                builder.AddText(message);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch
        {
            // The updated account state remains visible in the app.
        }
    }

    private async void LaunchProjectFromTray(UnityProjectInfo project)
    {
        try
        {
            var settingsStore = new AppSettingsStore();
            var settings = settingsStore.Load();
            var editorLocator = new UnityEditorLocator();
            var editors = editorLocator.GetInstalledEditors(settings.CustomEditorPaths);

            var exePath = editorLocator.FindEditorExecutable(project.Version, editors);

            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var result = await new UnityEditorLaunchService().LaunchProjectAsync(
                    exePath,
                    project.Path,
                    project.Version);
                if (result.Succeeded)
                {
                    NotifyEditorLaunched(result.EditorProcess, project.Path);
                }
                else
                {
                    RestoreWindow();
                    ShowAccountStatus("Unable to open project", result.Message);
                }
            }
            else
            {
                // Missing editor — restore and show the version picker dialog
                RestoreWindow();
                if (RootFrame.Content is MainPage mainPage)
                {
                    mainPage.NavigateToProjectsAndShowMissingEditor(project);
                }
            }
        }
        catch
        {
            RestoreWindow();
        }
    }

    private void OpenSettingsFromTray()
    {
        RestoreWindow();
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.NavigateToSettings();
        }
    }

    private void ExitAppFromTray()
    {
        if (_moduleInstallationManager.HasActiveOperations
            || _activeEditorUninstallCount > 0)
        {
            RestoreWindow();
            if (RootFrame.Content is MainPage mainPage)
            {
                mainPage.NavigateToEditors();
            }
            return;
        }

        _isExitingFromTray = true;
        CancelUnityAccountOperation();
        _moduleInstallationManager.OperationChanged -= OnModuleInstallationChanged;
        Activated -= OnWindowActivatedForTaskbarProgress;
        _priorityMonitorTimer.Stop();
        _taskbarErrorClearTimer.Stop();
        _taskbarProgressService?.Dispose();
        _taskbarProgressService = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Exit();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExitingFromTray) return; // Already shutting down via tray Exit

        if (_moduleInstallationManager.HasActiveOperations
            || _activeEditorUninstallCount > 0)
        {
            args.Cancel = true;
            MinimizeToTray();
            return;
        }

        try
        {
            var settings = new AppSettingsStore().Load();
            // 2 = when Closing the Hub, 3 = when Unity Editor opens or Closing the Hub
            if (settings.MinimizeBehavior is 2 or 3)
            {
                args.Cancel = true;
                MinimizeToTray();
                return;
            }
        }
        catch
        {
            // Fall through to real close
        }

        // Actual close — clean up window-level integrations.
        CancelUnityAccountOperation();
        _moduleInstallationManager.OperationChanged -= OnModuleInstallationChanged;
        Activated -= OnWindowActivatedForTaskbarProgress;
        _priorityMonitorTimer.Stop();
        _taskbarErrorClearTimer.Stop();
        _taskbarProgressService?.Dispose();
        _taskbarProgressService = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void CancelUnityAccountOperation()
    {
        _unityAccountCancellation?.Cancel();
        _unityAccountCancellation?.Dispose();
        _unityAccountCancellation = null;
    }

    private void OnModuleInstallationChanged(
        object? sender,
        UnityModuleInstallationChangedEventArgs args)
    {
        if (args.ChangedOperation?.IsTerminal == true)
        {
            ShowModuleInstallationNotification(args.ChangedOperation);
        }

        if (_activeEditorUninstallCount > 0)
        {
            _taskbarProgressService?.SetIndeterminate();
            return;
        }

        var operation = args.CurrentOperation;
        if (operation is null)
        {
            if (args.ChangedOperation?.State == UnityModuleInstallationState.Failed)
            {
                _taskbarProgressService?.SetError(args.ChangedOperation.Percentage);
                _taskbarErrorClearTimer.Stop();
                _taskbarErrorClearTimer.Start();
            }
            else if (_moduleInstallationManager.VisibleOperations.Any(item =>
                         item.State == UnityModuleInstallationState.Paused))
            {
                _taskbarProgressService?.SetPaused();
            }
            else if (!_taskbarErrorClearTimer.IsEnabled)
            {
                _taskbarProgressService?.Clear();
            }

            return;
        }

        _taskbarErrorClearTimer.Stop();
        switch (operation.State)
        {
            case UnityModuleInstallationState.Canceling:
            case UnityModuleInstallationState.Pausing:
            case UnityModuleInstallationState.Paused:
                _taskbarProgressService?.SetPaused();
                break;
            case UnityModuleInstallationState.Failed:
                _taskbarProgressService?.SetError(operation.Percentage);
                _taskbarErrorClearTimer.Start();
                break;
            case UnityModuleInstallationState.Succeeded:
            case UnityModuleInstallationState.Canceled:
                _taskbarProgressService?.Clear();
                break;
            default:
                if (operation.Percentage is not null)
                {
                    _taskbarProgressService?.SetProgress(operation.Percentage.Value);
                }
                else
                {
                    _taskbarProgressService?.SetIndeterminate();
                }
                break;
        }
    }

    private void OnTaskbarErrorClearTimerTick(object? sender, object e)
    {
        _taskbarErrorClearTimer.Stop();
        if (!_moduleInstallationManager.HasActiveOperations)
        {
            _taskbarProgressService?.Clear();
        }
    }

    private void ApplyCurrentTaskbarProgress()
    {
        if (_activeEditorUninstallCount > 0)
        {
            _taskbarProgressService?.SetIndeterminate();
            return;
        }

        var operation = _moduleInstallationManager.CurrentOperation;
        if (operation is null)
        {
            if (_moduleInstallationManager.VisibleOperations.Any(item =>
                    item.State == UnityModuleInstallationState.Paused))
            {
                _taskbarProgressService?.SetPaused();
            }
            else
            {
                _taskbarProgressService?.Reapply();
            }
            return;
        }

        switch (operation.State)
        {
            case UnityModuleInstallationState.Canceling:
            case UnityModuleInstallationState.Pausing:
            case UnityModuleInstallationState.Paused:
                _taskbarProgressService?.SetPaused();
                break;
            case UnityModuleInstallationState.Failed:
                _taskbarProgressService?.SetError(operation.Percentage);
                break;
            default:
                if (operation.Percentage is double percentage)
                {
                    _taskbarProgressService?.SetProgress(percentage);
                }
                else
                {
                    _taskbarProgressService?.SetIndeterminate();
                }
                break;
        }
    }

    private void OnWindowActivatedForTaskbarProgress(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _wasWindowDeactivated = true;
            return;
        }

        // Activation occurs after Explorer creates the window's taskbar
        // button, covering a TaskbarButtonCreated message delivered before
        // the WinUI window subclass was attached.
        ApplyCurrentTaskbarProgress();

        var returnedFromAnotherApp = _wasWindowDeactivated;
        _wasWindowDeactivated = false;
        if (returnedFromAnotherApp
            && RootFrame.Content is MainPage mainPage)
        {
            mainPage.CurrentProjectsPage?.RefreshExternalProjectMetadata();
        }

        var needsInitialAccountState = _unityAccountState is null;
        var cachedStateIsStale = DateTimeOffset.UtcNow - _unityAccountStatusCheckedAt
                                 >= UnityAccountStatusFreshness;
        if (!needsInitialAccountState && !returnedFromAnotherApp && !cachedStateIsStale)
        {
            return;
        }

        if (_isUnityAccountBusy)
        {
            // Browser sign-in owns the CLI while it is running. Refresh as soon
            // as that operation settles instead of competing with it.
            _refreshUnityAccountWhenIdle = true;
            return;
        }

        // Account identity is persisted in Unity's shared account database. Read
        // that lightweight source away from the UI thread instead of spawning
        // Unity CLI during first paint. Explicit sign-in/out operations still
        // use Unity CLI and publish their result immediately.
        _ = RefreshUnityAccountStateAsync();
    }

    public Task RefreshUnityAccountStateAsync()
    {
        if (_sharedUnityAccountRefreshTask is { IsCompleted: false })
        {
            return _sharedUnityAccountRefreshTask;
        }

        _sharedUnityAccountRefreshTask = RefreshSharedUnityAccountAsync();
        return _sharedUnityAccountRefreshTask;
    }

    private async Task RefreshSharedUnityAccountAsync()
    {
        try
        {
            var state = await Task.Run(ReadSharedUnityAccountState);
            UpdateUnityAccountState(state);

            if (state.IsLoggedIn && NetworkConnectivityService.Current.CanAttemptInternet)
            {
                if (UnitySharedAuthService.TryGetActiveAccessToken(out var token, out _)
                    && token is not null
                    && !UnitySharedAuthService.IsAccessTokenUsable(token)
                    && UnitySharedAuthService.HasUsableRefreshToken(token))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (refreshed, _) = await UnitySharedAuthService.RefreshOAuthTokenAsync();
                            if (refreshed is not null)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    var updatedState = ReadSharedUnityAccountState();
                                    UpdateUnityAccountState(updatedState);
                                });
                            }
                        }
                        catch
                        {
                            // Best-effort background refresh
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Preserve the last known identity when the shared store is briefly
            // unavailable instead of incorrectly switching the title bar to a
            // signed-out state.
            ShowAccountStatus("Unity account unavailable", ex.Message);
        }
    }

    private static UnityCliAuthState ReadSharedUnityAccountState()
    {
        var cliStatus = new UnityCliToolService().GetStatus();
        if (!UnitySharedAuthService.TryGetActiveAccount(out var account, out var errorMessage))
        {
            throw new IOException(errorMessage);
        }

        if (account is null)
        {
            return new UnityCliAuthState(
                cliStatus.IsInstalled,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                cliStatus.IsInstalled ? "Not signed in" : "Install Unity CLI from Settings before signing in.");
        }

        if (!UnitySharedAuthService.TryGetActiveAccessToken(out var token, out _)
            || token is null)
        {
            return new UnityCliAuthState(
                cliStatus.IsInstalled,
                false,
                account.DisplayName,
                account.Email,
                "oauth",
                "Unity sign-in expired. Sign in again to continue.")
            {
                SessionState = "expired"
            };
        }

        var hasValidAccess = UnitySharedAuthService.IsAccessTokenUsable(token);
        var hasValidRefresh = UnitySharedAuthService.HasUsableRefreshToken(token);

        if (!hasValidAccess && !hasValidRefresh)
        {
            return new UnityCliAuthState(
                cliStatus.IsInstalled,
                false,
                account.DisplayName,
                account.Email,
                "oauth",
                "Unity sign-in expired. Sign in again to continue.")
            {
                SessionState = "expired"
            };
        }

        return new UnityCliAuthState(
            cliStatus.IsInstalled,
            true,
            account.DisplayName,
            account.Email,
            "oauth",
            "Signed in securely through Unity CLI.");
    }

    private void OnTaskbarButtonCreated()
    {
        _taskbarProgressService?.NotifyTaskbarButtonCreated();
        ApplyCurrentTaskbarProgress();
    }

    private void ShowModuleInstallationNotification(UnityModuleInstallationSnapshot? operation)
    {
        if (operation?.IsTerminal != true)
        {
            return;
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            var title = operation.InstallsEditor
                ? operation.State switch
                {
                    UnityModuleInstallationState.Succeeded => "Unity Editor installed",
                    UnityModuleInstallationState.Canceled => "Unity Editor installation canceled",
                    _ => "Unity Editor installation failed"
                }
                : operation.Phase;
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(operation.Message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // The result remains available when the window is restored.
        }
    }

    public void NotifyEditorUninstallStarted()
    {
        _activeEditorUninstallCount++;
        _taskbarErrorClearTimer.Stop();
        _taskbarProgressService?.SetIndeterminate();
    }

    public void NotifyEditorUninstallCompleted(
        string editorDisplayName,
        bool succeeded,
        string message)
    {
        _activeEditorUninstallCount = Math.Max(0, _activeEditorUninstallCount - 1);
        if (_activeEditorUninstallCount > 0)
        {
            _taskbarProgressService?.SetIndeterminate();
        }
        else if (!succeeded)
        {
            _taskbarProgressService?.SetError(null);
            _taskbarErrorClearTimer.Stop();
            _taskbarErrorClearTimer.Start();
        }
        else if (_moduleInstallationManager.CurrentOperation is not null)
        {
            ApplyCurrentTaskbarProgress();
        }
        else
        {
            _taskbarProgressService?.Clear();
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            var notification = new AppNotificationBuilder()
                .AddText(succeeded ? "Unity Editor uninstalled" : "Unity Editor uninstall failed")
                .AddText(succeeded ? $"{editorDisplayName} was removed." : message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // The result remains visible on the Installs page.
        }
    }

    public void NotifyEditorLaunched(
        System.Diagnostics.Process? editorProcess = null,
        string? projectPath = null)
    {
        _ = RefreshUnityAccountStateAsync();

        try
        {
            var settings = new AppSettingsStore().Load();
            // 1 = when Unity Editor opens, 3 = when Unity Editor opens or Closing the Hub
            if (settings.MinimizeBehavior is 1 or 3)
            {
                _restoreAfterUnityExit = true;
                _launchedUnityWasObserved = false;
                _unityLaunchRequestedAt = DateTimeOffset.UtcNow;
                TrackLaunchedUnityProcess(editorProcess, projectPath);
                MinimizeToTray();
            }
            else
            {
                TrackLaunchedUnityProcess(editorProcess, projectPath);
            }
            EvaluateProcessPriority();
        }
        catch
        {
            // Ignore
        }
    }

    private void OnPriorityMonitorTimerTick(object? sender, object e)
    {
        EvaluateProcessPriority();

        if (!_restoreAfterUnityExit)
        {
            return;
        }

        if (IsAnyUnityEditorRunning())
        {
            _launchedUnityWasObserved = true;
            return;
        }

        if (_launchedUnityWasObserved)
        {
            RestoreAfterUnityExit();
        }
        else if (DateTimeOffset.UtcNow - _unityLaunchRequestedAt > TimeSpan.FromMinutes(2))
        {
            _restoreAfterUnityExit = false;
        }
    }

    private void TrackLaunchedUnityProcess(
        System.Diagnostics.Process? editorProcess,
        string? projectPath)
    {
        if (editorProcess is null)
        {
            return;
        }

        try
        {
            editorProcess.EnableRaisingEvents = true;
            editorProcess.Exited += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(projectPath))
                {
                    _ = SynchronizeProjectVersionAfterEditorExitAsync(projectPath);
                }

                try
                {
                    editorProcess.Dispose();
                }
                catch
                {
                    // The periodic process monitor remains the fallback.
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    _launchedUnityWasObserved = true;
                    if (!IsAnyUnityEditorRunning())
                    {
                        RestoreAfterUnityExit();
                    }
                });
            };

            if (!editorProcess.HasExited)
            {
                _launchedUnityWasObserved = true;
            }
        }
        catch
        {
            // The periodic process monitor covers races during subscription.
        }
    }

    private async Task SynchronizeProjectVersionAfterEditorExitAsync(string projectPath)
    {
        var version = await Task.Run(() =>
            new UnityHubProjectService().SynchronizeProjectVersionFromDisk(projectPath));
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (RootFrame.Content is MainPage mainPage
                && mainPage.CurrentProjectsPage is { } projectsPage)
            {
                projectsPage.RefreshProjectVersionFromDisk(projectPath, version);
            }
        });
    }

    private static bool IsAnyUnityEditorRunning()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("Unity");
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    return !process.HasExited;
                }
                catch
                {
                    return false;
                }
            });
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private void RestoreAfterUnityExit()
    {
        if (!_restoreAfterUnityExit)
        {
            return;
        }

        _restoreAfterUnityExit = false;
        _launchedUnityWasObserved = false;
        RestoreWindow();
    }

    public void EvaluateProcessPriority()
    {
        try
        {
            var settings = new AppSettingsStore().Load();
            if (settings.LowerPriorityWhenUnityOpens)
            {
                var isUnityRunning = System.Diagnostics.Process.GetProcessesByName("Unity").Length > 0;
                var currentPriority = System.Diagnostics.Process.GetCurrentProcess().PriorityClass;
                var targetPriority = isUnityRunning ? System.Diagnostics.ProcessPriorityClass.BelowNormal : System.Diagnostics.ProcessPriorityClass.Normal;

                if (currentPriority != targetPriority)
                {
                    System.Diagnostics.Process.GetCurrentProcess().PriorityClass = targetPriority;
                }
            }
            else
            {
                if (System.Diagnostics.Process.GetCurrentProcess().PriorityClass != System.Diagnostics.ProcessPriorityClass.Normal)
                {
                    System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                }
            }
        }
        catch
        {
            // Ignore process priority elevation/permission issues
        }
    }

    public void MinimizeToTray()
    {
        // 1. Show the tray icon (NativeTrayIcon guards against double-add)
        _trayIcon?.Show("FluenityHub");

        // 2. Hide the window
        AppWindow.Hide();

        // 3. Show a Windows App SDK toast notification
        try
        {
            if (AppNotificationManager.IsSupported())
            {
                var notification = new AppNotificationBuilder()
                    .AddText("FluenityHub is Minimized to Tray")
                    .AddText("FluenityHub is now running in the background. Click this notification or the icon on the tray to restore the window.")
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
        }
        catch
        {
            // Non-critical — tray icon is still there
        }
    }

    public void RestoreWindow()
    {
        // If the user restores the app manually, do not raise it again when
        // the Editor exits later.
        _restoreAfterUnityExit = false;
        _launchedUnityWasObserved = false;

        // 1. Remove the tray icon so there's only ever one
        _trayIcon?.Hide();

        // 2. Show and bring window to front
        AppWindow.Show();
        WinUIEx.HwndExtensions.SetForegroundWindow(WindowHandle);
    }

    public void OpenExternalProjectPath(string projectPath)
    {
        RestoreWindow();
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.OpenExternalProjectPath(projectPath);
        }
        else
        {
            Microsoft.UI.Xaml.Navigation.NavigatedEventHandler? handler = null;
            handler = (s, e) =>
            {
                RootFrame.Navigated -= handler;
                if (RootFrame.Content is MainPage page)
                {
                    page.OpenExternalProjectPath(projectPath);
                }
            };
            RootFrame.Navigated += handler;
        }
    }

    public void HandleExternalAction(string action)
    {
        RestoreWindow();
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.HandleExternalAction(action);
        }
        else
        {
            Microsoft.UI.Xaml.Navigation.NavigatedEventHandler? handler = null;
            handler = (s, e) =>
            {
                RootFrame.Navigated -= handler;
                if (RootFrame.Content is MainPage page)
                {
                    page.HandleExternalAction(action);
                }
            };
            RootFrame.Navigated += handler;
        }
    }

    public ElementTheme CurrentTheme { get; private set; } = ElementTheme.Default;

    public void SetAppTheme(string themeName)
    {
        CurrentTheme = themeName switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (AppRootGrid is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = CurrentTheme;
        }

        if (Content is FrameworkElement windowContent)
        {
            windowContent.RequestedTheme = CurrentTheme;
        }

        UpdateTitleBarColors();
    }

    private void OnAppRootActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarColors();
    }

    private void UpdateTitleBarColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var isDark = AppRootGrid.ActualTheme == ElementTheme.Dark;
        var foreground = isDark ? Colors.White : Colors.Black;
        var inactiveForeground = isDark
            ? ColorHelper.FromArgb(255, 128, 128, 128)
            : ColorHelper.FromArgb(255, 96, 96, 96);
        var titleBar = AppWindow.TitleBar;

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }
}
