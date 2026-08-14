using System.Diagnostics;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class AddLicenseDialog : ContentDialog
{
    private readonly UnityLicensingService _licensingService = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private string? _manualActivationEditorPath;
    private LicenseDialogPage _currentPage = LicenseDialogPage.Root;
    private RequestOperation _requestOperation;
    private bool _isBusy;

    public AddLicenseDialog()
    {
        InitializeComponent();
        ShowPage(LicenseDialogPage.Root);
    }

    private void OnRootActionCardClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var action = (sender as FrameworkElement)?.Tag as string;

        switch (action)
        {
            case "serial":
                ShowPage(LicenseDialogPage.Serial);
                SerialNumberTextBox.Focus(FocusState.Programmatic);
                break;
            case "request":
                ShowPage(LicenseDialogPage.Request);
                break;
            case "personal":
                ShowPage(LicenseDialogPage.Personal);
                break;
            case "team":
                OpenUrl("https://store.unity.com/");
                break;
            case "student":
                OpenUrl("https://unity.com/products/unity-student");
                break;
        }
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_isBusy)
        {
            args.Cancel = true;
            return;
        }

        args.Cancel = true;
        ShowPage(LicenseDialogPage.Root);
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_currentPage is not (LicenseDialogPage.Serial or LicenseDialogPage.Personal or LicenseDialogPage.Request))
        {
            args.Cancel = true;
            return;
        }

        var serial = _currentPage == LicenseDialogPage.Serial
            ? SerialNumberTextBox.Text.Trim()
            : null;

        if (_currentPage == LicenseDialogPage.Request)
        {
            await ActivateManualLicenseFileAsync(args);
            return;
        }

        if (_currentPage == LicenseDialogPage.Serial && !IsValidSerial(serial))
        {
            args.Cancel = true;
            ShowStatus(SerialStatusInfoBar, "Enter a valid serial number", "Use the serial number assigned to your Unity account.", InfoBarSeverity.Warning);
            SerialNumberTextBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_currentPage == LicenseDialogPage.Personal && !PersonalTermsCheckBox.IsChecked.GetValueOrDefault())
        {
            args.Cancel = true;
            ShowStatus(PersonalStatusInfoBar, "Confirm eligibility", "Confirm that you are eligible for Unity Personal and agree to the terms before continuing.", InfoBarSeverity.Warning);
            PersonalTermsCheckBox.Focus(FocusState.Programmatic);
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            SetBusyState(true);

            var result = await _licensingService.ActivateUlfAsync(serial, _cancellationTokenSource.Token);
            if (!result.Succeeded)
            {
                args.Cancel = true;
                var target = _currentPage == LicenseDialogPage.Serial
                    ? SerialStatusInfoBar
                    : PersonalStatusInfoBar;
                ShowStatus(target, "License activation failed", result.Message, InfoBarSeverity.Error);
            }
        }
        catch (OperationCanceledException)
        {
            args.Cancel = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            var target = _currentPage == LicenseDialogPage.Serial
                ? SerialStatusInfoBar
                : PersonalStatusInfoBar;
            ShowStatus(target, "License activation failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusyState(false);
            deferral.Complete();
        }
    }

    private void OnSerialNumberTextChanged(object sender, TextChangedEventArgs e) => UpdatePrimaryButtonState();

    private void OnPersonalTermsChanged(object sender, RoutedEventArgs e) => UpdatePrimaryButtonState();

    private void OnOpenManualActivationClick(object sender, RoutedEventArgs e)
        => OpenUrl("https://license.unity3d.com/manual");

    private async void OnCreateLicenseRequestClick(object sender, RoutedEventArgs e)
    {
        var editor = ResolveManualActivationEditor();
        if (editor is null)
        {
            ShowStatus(RequestStatusInfoBar, "Unity Editor required", "Install or locate a Unity Editor before creating a manual license request.", InfoBarSeverity.Warning);
            return;
        }

        if (MainWindow.Instance is null)
        {
            ShowStatus(RequestStatusInfoBar, "Unable to create license request", "The application window is not available.", InfoBarSeverity.Error);
            return;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(MainWindow.Instance.AppWindow.Id)
        {
            Title = "Save Unity license request",
            CommitButtonText = "Save",
            SuggestedFileName = "Unity-license-request"
        };
        picker.FileTypeChoices.Add("Unity license request", [".alf"]);
        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        _manualActivationEditorPath = editor.Value.Path;
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        RequestStatusInfoBar.IsOpen = false;
        SetRequestBusyState(true, RequestOperation.CreatingRequest);
        try
        {
            var result = await _licensingService.CreateManualLicenseRequestAsync(
                _manualActivationEditorPath,
                outputFile.Path,
                _cancellationTokenSource.Token);
            if (!result.Succeeded)
            {
                ShowStatus(RequestStatusInfoBar, "License request failed", result.Message, InfoBarSeverity.Error);
                return;
            }

            LicenseRequestPathTextBlock.Text = $"Saved to {outputFile.Path} using Unity {editor.Value.Version}.";
            ShowStatus(RequestStatusInfoBar, "License request created", "Upload the .alf file in the next step to generate your license file.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowStatus(RequestStatusInfoBar, "License request failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetRequestBusyState(false, RequestOperation.None);
        }
    }

    private async void OnBrowseLicenseFileClick(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
        {
            ShowStatus(RequestStatusInfoBar, "Unable to choose a license file", "The application window is not available.", InfoBarSeverity.Error);
            return;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(MainWindow.Instance.AppWindow.Id)
        {
            Title = "Choose Unity license file",
            CommitButtonText = "Choose"
        };
        picker.FileTypeFilter.Add(".ulf");
        var licenseFile = await picker.PickSingleFileAsync();
        if (licenseFile is null)
        {
            return;
        }

        LicenseFileTextBox.Text = licenseFile.Path;
        RequestStatusInfoBar.IsOpen = false;
        UpdatePrimaryButtonState();
    }

    private void OnPersonalTermsLinkClick(object sender, RoutedEventArgs e)
        => OpenUrl("https://unity.com/legal/editor-terms-of-service/software");

    private void OnGetTeamPlanClick(object sender, RoutedEventArgs e) => OpenUrl("https://store.unity.com/");

    private void OnHelpClick(object sender, RoutedEventArgs e) => OpenUrl("https://docs.unity.com/en-us/hub/manage-license");

    private void OnFaqClick(object sender, RoutedEventArgs e) => OpenUrl("https://unity.com/pricing");

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    private void ShowPage(LicenseDialogPage page)
    {
        _currentPage = page;
        RootPanel.Visibility = page == LicenseDialogPage.Root ? Visibility.Visible : Visibility.Collapsed;
        SerialPanel.Visibility = page == LicenseDialogPage.Serial ? Visibility.Visible : Visibility.Collapsed;
        PersonalPanel.Visibility = page == LicenseDialogPage.Personal ? Visibility.Visible : Visibility.Collapsed;
        RequestPanel.Visibility = page == LicenseDialogPage.Request ? Visibility.Visible : Visibility.Collapsed;

        if (page == LicenseDialogPage.Request)
        {
            _manualActivationEditorPath ??= ResolveManualActivationEditor()?.Path;
        }

        Title = page switch
        {
            LicenseDialogPage.Serial => "Activate with serial number",
            LicenseDialogPage.Personal => "Get Unity Personal",
            LicenseDialogPage.Request => "Activate with license request",
            _ => "Add new license"
        };

        PrimaryButtonText = page switch
        {
            LicenseDialogPage.Serial => "Activate",
            LicenseDialogPage.Personal => "Get license",
            LicenseDialogPage.Request => "Activate",
            _ => string.Empty
        };
        SecondaryButtonText = page is LicenseDialogPage.Root or LicenseDialogPage.Personal ? string.Empty : "Back";
        DefaultButton = page is LicenseDialogPage.Serial or LicenseDialogPage.Personal
            ? ContentDialogButton.Primary
            : ContentDialogButton.None;

        SerialStatusInfoBar.IsOpen = false;
        PersonalStatusInfoBar.IsOpen = false;
        if (page != LicenseDialogPage.Request)
        {
            RequestStatusInfoBar.IsOpen = false;
        }
        UpdatePrimaryButtonState();
    }

    private void UpdatePrimaryButtonState()
    {
        IsPrimaryButtonEnabled = !_isBusy && _currentPage switch
        {
            LicenseDialogPage.Serial => IsValidSerial(SerialNumberTextBox.Text),
            LicenseDialogPage.Personal => PersonalTermsCheckBox.IsChecked.GetValueOrDefault(),
            LicenseDialogPage.Request => !string.IsNullOrWhiteSpace(_manualActivationEditorPath)
                                         && File.Exists(_manualActivationEditorPath)
                                         && File.Exists(LicenseFileTextBox.Text),
            _ => false
        };
    }

    private void SetBusyState(bool isBusy)
    {
        _isBusy = isBusy;
        SerialNumberTextBox.IsEnabled = !isBusy;
        PersonalTermsCheckBox.IsEnabled = !isBusy;
        SerialProgressPanel.Visibility = isBusy && _currentPage == LicenseDialogPage.Serial
            ? Visibility.Visible
            : Visibility.Collapsed;
        PersonalProgressPanel.Visibility = isBusy && _currentPage == LicenseDialogPage.Personal
            ? Visibility.Visible
            : Visibility.Collapsed;
        SerialProgressRing.IsActive = isBusy && _currentPage == LicenseDialogPage.Serial;
        PersonalProgressRing.IsActive = isBusy && _currentPage == LicenseDialogPage.Personal;
        UpdatePrimaryButtonState();
    }

    private async Task ActivateManualLicenseFileAsync(ContentDialogButtonClickEventArgs args)
    {
        var editor = _manualActivationEditorPath;
        var licenseFilePath = LicenseFileTextBox.Text;
        if (string.IsNullOrWhiteSpace(editor) || !File.Exists(editor))
        {
            args.Cancel = true;
            ShowStatus(RequestStatusInfoBar, "Unity Editor required", "Install or locate a Unity Editor before activating a manual license file.", InfoBarSeverity.Warning);
            return;
        }

        if (!File.Exists(licenseFilePath)
            || !Path.GetExtension(licenseFilePath).Equals(".ulf", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            ShowStatus(RequestStatusInfoBar, "Choose a valid license file", "Select the .ulf file returned by Unity's manual activation portal.", InfoBarSeverity.Warning);
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            RequestStatusInfoBar.IsOpen = false;
            SetRequestBusyState(true, RequestOperation.ActivatingLicense);
            var result = await _licensingService.ActivateManualLicenseFileAsync(editor, licenseFilePath, _cancellationTokenSource.Token);
            if (!result.Succeeded)
            {
                args.Cancel = true;
                ShowStatus(RequestStatusInfoBar, "License activation failed", result.Message, InfoBarSeverity.Error);
            }
        }
        catch (OperationCanceledException)
        {
            args.Cancel = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowStatus(RequestStatusInfoBar, "License activation failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetRequestBusyState(false, RequestOperation.None);
            deferral.Complete();
        }
    }

    private (string Version, string Path)? ResolveManualActivationEditor()
    {
        var settings = new AppSettingsStore().Load();
        var editor = new UnityEditorLocator()
            .GetInstalledEditors(settings.CustomEditorPaths)
            .OrderByDescending(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(editor.Value) ? null : (editor.Key, editor.Value);
    }

    private void SetRequestBusyState(bool isBusy, RequestOperation operation)
    {
        _isBusy = isBusy;
        _requestOperation = isBusy ? operation : RequestOperation.None;
        CreateLicenseRequestButton.IsEnabled = !isBusy;
        BrowseLicenseFileButton.IsEnabled = !isBusy;
        RequestCreationProgressPanel.Visibility = _requestOperation == RequestOperation.CreatingRequest
            ? Visibility.Visible
            : Visibility.Collapsed;
        RequestCreationProgressRing.IsActive = _requestOperation == RequestOperation.CreatingRequest;
        LicenseActivationProgressPanel.Visibility = _requestOperation == RequestOperation.ActivatingLicense
            ? Visibility.Visible
            : Visibility.Collapsed;
        LicenseActivationProgressRing.IsActive = _requestOperation == RequestOperation.ActivatingLicense;
        UpdatePrimaryButtonState();
    }

    private static void ShowStatus(InfoBar target, string title, string message, InfoBarSeverity severity)
    {
        target.Title = title;
        target.Message = message;
        target.Severity = severity;
        target.IsOpen = true;
    }

    private static bool IsValidSerial(string? value)
    {
        // Unity Hub enables activation for any non-empty entry and lets Unity's
        // licensing service return the authoritative serial-format error.
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum LicenseDialogPage
    {
        Root,
        Serial,
        Personal,
        Request
    }

    private enum RequestOperation
    {
        None,
        CreatingRequest,
        ActivatingLicense
    }
}
