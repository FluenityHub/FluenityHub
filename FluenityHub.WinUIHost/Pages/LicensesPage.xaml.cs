using System.Diagnostics;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Pages;

public sealed partial class LicensesPage : Page
{
    private readonly UnityLicensingService _licensingService = new();
    private readonly UnityCliAuthService _unityCliAuthService = new();
    private readonly UnityLogoutSecurityService _unityLogoutSecurityService = new();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _accountCancellation;
    private bool _isUnityCliAvailable;
    private bool _isPublishingAccountState;

    public LicensesPage()
    {
        InitializeComponent();
        SettingsBreadcrumbBar.ItemsSource = new[] { "Settings", "Unity licenses" };
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnSettingsBreadcrumbItemClicked(
        BreadcrumbBar sender,
        BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0 && Frame?.CanGoBack == true)
        {
            Frame.GoBack();
        }
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is { } mainWindow)
        {
            mainWindow.UnityAccountStateChanged -= OnUnityAccountStateChanged;
            mainWindow.UnityAccountStateChanged += OnUnityAccountStateChanged;
            if (mainWindow.UnityAccountState is { } cachedState)
            {
                ApplyUnityAccountState(cachedState, showResult: false, publishToMainWindow: false);
            }
        }

        await Task.WhenAll(
            LoadUnityAccountAsync(showResult: false),
            LoadLicensesAsync(synchronize: false));
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is { } mainWindow)
        {
            mainWindow.UnityAccountStateChanged -= OnUnityAccountStateChanged;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _accountCancellation?.Cancel();
        _accountCancellation?.Dispose();
        _accountCancellation = null;
    }

    private async void OnAddLicenseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetXamlRoot = Content?.XamlRoot ?? XamlRoot;
            if (targetXamlRoot is null) return;

            var dialog = new Dialogs.AddLicenseDialog
            {
                XamlRoot = targetXamlRoot,
                RequestedTheme = (targetXamlRoot.Content as FrameworkElement)?.RequestedTheme
                    ?? MainWindow.Instance?.CurrentTheme
                    ?? ElementTheme.Default
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ShowUnityAccountResult("Could not show license dialog", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await Task.WhenAll(
            LoadUnityAccountAsync(showResult: true),
            LoadLicensesAsync(synchronize: true));
    }

    private async Task LoadUnityAccountAsync(bool showResult)
    {
        _accountCancellation?.Cancel();
        _accountCancellation?.Dispose();
        _accountCancellation = new CancellationTokenSource();
        SetUnityAccountBusy(true, "Checking");
        try
        {
            if (MainWindow.Instance is not { } mainWindow)
            {
                throw new InvalidOperationException("The Unity account host is unavailable.");
            }

            _isPublishingAccountState = true;
            try
            {
                await mainWindow.RefreshUnityAccountStateAsync();
            }
            finally
            {
                _isPublishingAccountState = false;
            }

            _accountCancellation.Token.ThrowIfCancellationRequested();
            if (mainWindow.UnityAccountState is { } state)
            {
                ApplyUnityAccountState(state, showResult, publishToMainWindow: false);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed or a newer account request replaced this one.
        }
        catch (Exception ex)
        {
            ShowUnityAccountResult("Unity account unavailable", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetUnityAccountBusy(false, "Sign in");
        }
    }

    private async void OnUnitySignInClick(object sender, RoutedEventArgs e)
    {
        _accountCancellation?.Cancel();
        _accountCancellation?.Dispose();
        _accountCancellation = new CancellationTokenSource();
        SetUnityAccountBusy(true, "Waiting for browser");
        try
        {
            var state = await _unityCliAuthService.LoginAsync(_accountCancellation.Token);
            ApplyUnityAccountState(state, showResult: true);
            if (state.IsLoggedIn)
            {
                await LoadLicensesAsync(synchronize: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Page navigation canceled sign-in.
        }
        catch (Exception ex)
        {
            ShowUnityAccountResult("Unity sign-in failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetUnityAccountBusy(false, "Sign in");
        }
    }

    private async void OnUnitySignOutClick(object sender, RoutedEventArgs e)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Sign out of Unity?",
            Content = "Signing out removes the Unity CLI session from Windows Credential Manager. Unity Personal and named-user licenses may no longer remain active.",
            PrimaryButtonText = "Sign out",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _accountCancellation?.Cancel();
        _accountCancellation?.Dispose();
        _accountCancellation = new CancellationTokenSource();
        SetUnityAccountBusy(true, "Signing out");
        try
        {
            var state = await _unityCliAuthService.LogoutAsync(_accountCancellation.Token);
            _unityLogoutSecurityService.ApplyAfterLogout(state);
            ApplyUnityAccountState(state, showResult: true);
            await LoadLicensesAsync(synchronize: false);
        }
        catch (OperationCanceledException)
        {
            // Page navigation canceled sign-out.
        }
        catch (Exception ex)
        {
            ShowUnityAccountResult("Unity sign-out failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetUnityAccountBusy(false, "Sign in");
        }
    }

    private void ApplyUnityAccountState(
        UnityCliAuthState state,
        bool showResult,
        bool publishToMainWindow = true)
    {
        _isUnityCliAvailable = state.IsCliAvailable;
        var identity = !string.IsNullOrWhiteSpace(state.DisplayName)
            ? state.DisplayName
            : state.Email;
        UnityAccountPersonPicture.DisplayName = state.IsLoggedIn ? identity : string.Empty;
        UnityAccountNameTextBlock.Text = state.IsLoggedIn
            ? (!string.IsNullOrWhiteSpace(state.DisplayName) ? state.DisplayName : "Unity ID")
            : "Unity ID";
        UnityAccountEmailTextBlock.Text = state.IsLoggedIn
            ? (!string.IsNullOrWhiteSpace(state.Email)
                ? state.Email
                : !string.IsNullOrWhiteSpace(state.DisplayName)
                    ? state.DisplayName
                    : "Signed in")
            : state.Message;
        UnitySignInButton.Visibility = state.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        UnitySignInButton.IsEnabled = state.IsCliAvailable;
        UnitySignOutButton.Visibility = state.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        if (showResult || !state.IsCliAvailable)
        {
            ShowUnityAccountResult(
                state.IsLoggedIn ? "Signed in to Unity" : "Unity account",
                state.Message,
                state.IsLoggedIn
                    ? InfoBarSeverity.Success
                    : state.IsCliAvailable
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Warning);
        }

        if (publishToMainWindow && MainWindow.Instance is { } mainWindow)
        {
            _isPublishingAccountState = true;
            try
            {
                mainWindow.UpdateUnityAccountState(state);
            }
            finally
            {
                _isPublishingAccountState = false;
            }
        }
    }

    private async void OnUnityAccountStateChanged(object? sender, UnityCliAuthState state)
    {
        if (_isPublishingAccountState)
        {
            return;
        }

        ApplyUnityAccountState(state, showResult: true, publishToMainWindow: false);
        await LoadLicensesAsync(synchronize: state.IsLoggedIn);
    }

    private void SetUnityAccountBusy(bool isBusy, string text)
    {
        UnityAccountProgressRing.IsActive = isBusy;
        UnityAccountProgressRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        UnitySignInButtonText.Text = isBusy ? text : "Sign in";
        UnitySignInButton.IsEnabled = !isBusy && _isUnityCliAvailable;
        UnitySignOutButton.IsEnabled = !isBusy;
    }

    private CancellationTokenSource? _accountInfoBarCts;
    private CancellationTokenSource? _statusInfoBarCts;

    private async void ShowUnityAccountResult(string title, string message, InfoBarSeverity severity)
    {
        _accountInfoBarCts?.Cancel();
        _accountInfoBarCts?.Dispose();
        _accountInfoBarCts = new CancellationTokenSource();
        var token = _accountInfoBarCts.Token;

        UnityAccountInfoBar.Title = title;
        UnityAccountInfoBar.Message = message;
        UnityAccountInfoBar.Severity = severity;
        UnityAccountInfoBar.Visibility = Visibility.Visible;
        UnityAccountInfoBar.IsOpen = true;

        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
        {
            try
            {
                await Task.Delay(4000, token);
                if (!token.IsCancellationRequested)
                {
                    UnityAccountInfoBar.IsOpen = false;
                    UnityAccountInfoBar.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async void ShowStatusInfoBar(string title, string message, InfoBarSeverity severity, bool isOpen)
    {
        _statusInfoBarCts?.Cancel();
        _statusInfoBarCts?.Dispose();
        _statusInfoBarCts = new CancellationTokenSource();
        var token = _statusInfoBarCts.Token;

        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        StatusInfoBar.IsOpen = isOpen;

        if (isOpen && severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
        {
            try
            {
                await Task.Delay(4000, token);
                if (!token.IsCancellationRequested)
                {
                    StatusInfoBar.IsOpen = false;
                    StatusInfoBar.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void OnInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
    }

    private async Task LoadLicensesAsync(bool synchronize)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        SetLoadingState(true, synchronize);
        try
        {
            var snapshot = await _licensingService.GetSnapshotAsync(
                synchronize,
                _loadCancellation.Token);

            // Licensing client sub-item
            LicensingClientPathTextBlock.Text = snapshot.IsClientAvailable
                ? snapshot.ClientPath
                : "Not installed";
            LicensingClientStatusTextBlock.Text = snapshot.IsClientAvailable
                ? snapshot.ClientVersion
                : "Unavailable";

            // Primary license info
            var primary = snapshot.Licenses.Count > 0 ? snapshot.Licenses[0] : null;

            LicenseExpander.Header = primary is not null
                ? primary.Name
                : "License status";
            LicenseExpander.Description = primary is not null
                ? primary.Description
                : snapshot.IsClientAvailable
                    ? "No active licenses found"
                    : "Unity Licensing Client is unavailable";
            LicenseStatusTextBlock.Text = primary is not null
                ? "Active"
                : "Not activated";
            LicenseStatusTextBlock.Foreground = primary is not null
                ? (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["SystemFillColorSuccessBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["TextFillColorSecondaryBrush"];

            if (primary is not null && !string.IsNullOrWhiteSpace(primary.Description))
            {
                LicenseTypeCard.Visibility = Visibility.Visible;
                LicenseTypeTextBlock.Text = primary.Description;
            }
            else
            {
                LicenseTypeCard.Visibility = Visibility.Collapsed;
            }

            if (primary is not null && !string.IsNullOrWhiteSpace(primary.Details))
            {
                LicenseDetailsCard.Visibility = Visibility.Visible;
                LicenseDetailsTextBlock.Text = primary.Details;
            }
            else
            {
                LicenseDetailsCard.Visibility = Visibility.Collapsed;
            }

            EmptyStatePanel.Visibility = primary is null
                ? Visibility.Visible
                : Visibility.Collapsed;

            var title = snapshot.IsClientAvailable
                ? primary is not null ? "Licenses refreshed" : "No active licenses"
                : "Licensing client unavailable";
            var severity = !snapshot.IsClientAvailable
                ? InfoBarSeverity.Warning
                : primary is not null
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Success;
            var open = synchronize || !snapshot.IsClientAvailable;

            ShowStatusInfoBar(title, snapshot.StatusMessage, severity, open);
        }
        catch (OperationCanceledException)
        {
            // The page was closed or a newer refresh replaced this request.
        }
        catch (Exception ex)
        {
            ShowStatusInfoBar("Licenses could not be loaded", ex.Message, InfoBarSeverity.Error, true);
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        finally
        {
            SetLoadingState(false, synchronize);
        }
    }

    private void SetLoadingState(bool isLoading, bool synchronizing)
    {
        RefreshButton.IsEnabled = !isLoading;
        RefreshProgressRing.IsActive = isLoading;
        RefreshProgressRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        RefreshIcon.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
        RefreshButtonText.Text = isLoading && synchronizing ? "Refreshing" : "Refresh";
        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        if (isLoading)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnManageUnityIdClick(object sender, RoutedEventArgs e)
        => OpenUrl("https://id.unity.com/");

    private void OnManageOrganizationClick(object sender, RoutedEventArgs e)
        => OpenUrl("https://id.unity.com/en/organizations");

    private void OnLearnAboutLicensesClick(object sender, RoutedEventArgs e)
        => OpenUrl("https://docs.unity.com/en-us/hub/manage-license");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // The system may not have a registered browser.
        }
    }
}
