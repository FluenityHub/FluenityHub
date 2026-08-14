using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.UI.Text;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class AddModulesDialog : ContentDialog
{
    private enum DialogStage
    {
        ModuleSelection,
        AgreementReview,
        ToolSetup
    }

    private static readonly string[] CategoryOrder =
    [
        "Dev tools",
        "Platforms",
        "Language packs (preview)",
        "Documentation"
    ];

    private readonly UnityEditorInfo _editor;
    private readonly UnityEditorRelease? _release;
    private readonly bool _installsEditor;
    private readonly UnityCliToolService _cliToolService = new();
    private readonly UnityModuleService _moduleService = new();
    private readonly List<UnityEditorModuleInfo> _modules = [];
    private readonly List<UnityEditorModuleInfo> _pendingModules = [];
    private readonly List<UnityLicenseTerm> _pendingAgreements = [];
    private CancellationTokenSource? _agreementLoadCancellation;
    private CancellationTokenSource? _toolSetupCancellation;
    private UnityCliReleaseInfo? _cliRelease;
    private DialogStage _stage = DialogStage.ModuleSelection;
    private bool _isLoadingAgreement;
    private bool _isNormalizingSelection;
    private int _agreementIndex;
    private int _agreementLoadVersion;
    private string _selectionTitle = string.Empty;
    private long _editorDownloadSizeBytes;
    private long _editorInstalledSizeBytes;

    public UnityModuleInstallationRequest? InstallationRequest { get; private set; }

    public bool RequiresRemovalConfirmation =>
        InstallationRequest?.OperationKind == UnityModuleOperationKind.Remove;

    public AddModulesDialog(UnityEditorInfo editor, int projectCount)
    {
        InitializeComponent();
        _editor = editor;

        try
        {
            var catalog = _moduleService.LoadCatalog(editor);
            _selectionTitle = projectCount > 0
                ? $"Add modules for {catalog.ProductName} ({editor.Version}) · {projectCount} project{(projectCount == 1 ? string.Empty : "s")}"
                : $"Add modules for {catalog.ProductName} ({editor.Version})";
            InitializeModuleSelection(catalog);
        }
        catch (Exception ex)
        {
            _selectionTitle = $"Add modules for Unity {editor.Version}";
            Title = _selectionTitle;
            ShowStatus("Modules unavailable", ex.Message, InfoBarSeverity.Warning);
            IsPrimaryButtonEnabled = false;
        }
    }

    public AddModulesDialog(UnityEditorRelease release, string installRoot)
    {
        InitializeComponent();
        _release = release;
        _installsEditor = true;
        _editorDownloadSizeBytes = release.DownloadSizeBytes;
        _editorInstalledSizeBytes = release.InstalledSizeBytes;

        var normalizedRoot = Path.GetFullPath(installRoot);
        var installDirectory = Path.Combine(normalizedRoot, release.Version);
        _editor = new UnityEditorInfo
        {
            Version = release.Version,
            InstallDirectory = installDirectory,
            ExecutablePath = Path.Combine(installDirectory, "Editor", "Unity.exe")
        };

        _selectionTitle = $"Install Unity {release.Version}";
        InitializeModuleSelection(
            new UnityModuleCatalog($"Unity {release.Version}", release.Modules));
    }

    private void InitializeModuleSelection(UnityModuleCatalog catalog)
    {
        Title = _selectionTitle;
        _modules.AddRange(catalog.Modules);
        ModulesTreeView.ItemsSource = BuildModuleTree(_modules);
        AvailableSizeRun.Text = FormatBytes(
            _moduleService.GetAvailableDiskSpace(_editor.InstallDirectory));
        UpdateSelectionState();
    }

    public static Visibility BoolToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    public static string ModuleActionsAutomationName(string moduleName)
        => $"Manage {moduleName}";

    public static FontWeight ModuleFontWeight(bool isCategory)
        => isCategory ? FontWeights.SemiBold : FontWeights.Normal;

    private static IReadOnlyList<UnityModuleTreeItem> BuildModuleTree(
        IReadOnlyCollection<UnityEditorModuleInfo> modules)
    {
        var orderedCategories = modules
            .Select(module => module.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category =>
            {
                var index = Array.FindIndex(
                    CategoryOrder,
                    candidate => candidate.Equals(category, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(category => category, StringComparer.OrdinalIgnoreCase);

        var roots = new List<UnityModuleTreeItem>();
        foreach (var category in orderedCategories)
        {
            var categoryModules = modules
                .Where(module => module.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (categoryModules.Count == 0)
            {
                continue;
            }

            var categoryItem = new UnityModuleTreeItem
            {
                Name = category,
                IsCategory = true,
                IsExpanded = true
            };

            foreach (var module in categoryModules.Where(module => !module.IsChild))
            {
                var moduleItem = new UnityModuleTreeItem
                {
                    Name = module.Name,
                    Module = module,
                    IsExpanded = true
                };

                foreach (var child in categoryModules.Where(child =>
                    child.ParentId.Equals(module.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    moduleItem.Children.Add(new UnityModuleTreeItem
                    {
                        Name = child.Name,
                        Module = child
                    });
                }

                categoryItem.Children.Add(moduleItem);
            }

            roots.Add(categoryItem);
        }

        return roots;
    }

    private void OnModuleSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_isNormalizingSelection || _stage != DialogStage.ModuleSelection)
        {
            return;
        }

        _isNormalizingSelection = true;
        try
        {
            foreach (var item in args.AddedItems
                         .OfType<UnityModuleTreeItem>()
                         .Where(item => !item.CanSelect)
                         .ToArray())
            {
                sender.SelectedItems.Remove(item);
            }
        }
        finally
        {
            _isNormalizingSelection = false;
        }

        UpdateSelectionState();
    }

    private void OnTreeDragItemsStarting(
        TreeView sender,
        TreeViewDragItemsStartingEventArgs args)
        => args.Cancel = true;

    private void OnAgreementAcceptanceChanged(object sender, RoutedEventArgs e)
    {
        if (_stage == DialogStage.AgreementReview)
        {
            IsPrimaryButtonEnabled = !_isLoadingAgreement && AgreementCheckBox.IsChecked == true;
        }
    }

    private void UpdateSelectionState()
    {
        if (_stage != DialogStage.ModuleSelection)
        {
            return;
        }

        var selectedModules = GetSelectedModules();
        var requiredBytes = _editorInstalledSizeBytes
            + selectedModules.Sum(module => module.InstalledSizeBytes);
        var availableBytes = _moduleService.GetAvailableDiskSpace(_editor.InstallDirectory);
        var agreements = BuildLicenseTerms(selectedModules);

        RequiredSizeRun.Text = FormatBytes(requiredBytes);
        PrimaryButtonText = agreements.Count > 0 ? "Next" : "Install";
        IsPrimaryButtonEnabled =
            (_installsEditor || selectedModules.Count > 0)
            && requiredBytes <= availableBytes;

        if (requiredBytes > availableBytes)
        {
            ShowStatus(
                "Not enough disk space",
                "Free additional space or select fewer modules.",
                InfoBarSeverity.Warning);
        }
        else if (StatusInfoBar.Title == "Not enough disk space")
        {
            StatusInfoBar.IsOpen = false;
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            if (_stage == DialogStage.ModuleSelection)
            {
                var selectedModules = GetSelectedModules();
                if (!_installsEditor && selectedModules.Count == 0)
                {
                    ShowStatus("Nothing selected", "Select at least one module to install.", InfoBarSeverity.Warning);
                    return;
                }

                _pendingModules.Clear();
                _pendingModules.AddRange(selectedModules);
                _pendingAgreements.Clear();
                _pendingAgreements.AddRange(BuildLicenseTerms(selectedModules));

                if (_pendingAgreements.Count > 0)
                {
                    await ShowAgreementAsync(0);
                }
                else
                {
                    await ContinueToInstallationAsync();
                }
            }
            else if (_stage == DialogStage.AgreementReview
                     && !_isLoadingAgreement
                     && AgreementCheckBox.IsChecked == true)
            {
                if (_agreementIndex < _pendingAgreements.Count - 1)
                {
                    await ShowAgreementAsync(_agreementIndex + 1);
                }
                else
                {
                    await ContinueToInstallationAsync();
                }
            }
            else if (_stage == DialogStage.ToolSetup)
            {
                if (_cliRelease is null)
                {
                    await ShowToolSetupAsync();
                }
                else
                {
                    CompleteInstallationRequest(_cliRelease);
                }
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_stage is not (DialogStage.AgreementReview or DialogStage.ToolSetup))
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            if (_stage == DialogStage.ToolSetup)
            {
                if (_pendingAgreements.Count > 0)
                {
                    await ShowAgreementAsync(_pendingAgreements.Count - 1);
                }
                else
                {
                    ReturnToModuleSelection();
                }
            }
            else if (_agreementIndex == 0)
            {
                ReturnToModuleSelection();
            }
            else
            {
                await ShowAgreementAsync(_agreementIndex - 1);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnRetryLicenseClick(object sender, RoutedEventArgs e)
    {
        if (_stage == DialogStage.AgreementReview)
        {
            await ShowAgreementAsync(_agreementIndex);
        }
    }

    private async Task ShowAgreementAsync(int index)
    {
        CancelAgreementLoad();
        CancelToolSetupRequest();
        if (index < 0 || index >= _pendingAgreements.Count)
        {
            ReturnToModuleSelection();
            return;
        }

        _stage = DialogStage.AgreementReview;
        _agreementIndex = index;
        var term = _pendingAgreements[index];
        var loadVersion = ++_agreementLoadVersion;

        ModuleSelectionPanel.Visibility = Visibility.Collapsed;
        ToolSetupPanel.Visibility = Visibility.Collapsed;
        AgreementPanel.Visibility = Visibility.Visible;
        Title = term.Label;
        AgreementPositionTextBlock.Text = $"Agreement {index + 1} of {_pendingAgreements.Count}";
        SecondaryButtonText = "Back";
        PrimaryButtonText = index == _pendingAgreements.Count - 1 ? "Install" : "Continue";
        DefaultButton = ContentDialogButton.Primary;

        AgreementInfoBar.IsOpen = false;
        AgreementCheckBox.IsChecked = false;
        AgreementCheckBox.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        AgreementLoadingPanel.Visibility = Visibility.Visible;
        AgreementContentPanel.Visibility = Visibility.Collapsed;
        AgreementBodyTextBlock.Text = string.Empty;
        AgreementLinkButton.Content = term.NavigateUri?.AbsoluteUri ?? string.Empty;
        AgreementLinkButton.NavigateUri = term.NavigateUri;
        AgreementLinkButton.Visibility = term.NavigateUri is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        AgreementBodyScrollViewer.ChangeView(null, 0, null, true);

        _isLoadingAgreement = true;
        var cancellation = new CancellationTokenSource();
        _agreementLoadCancellation = cancellation;
        try
        {
            var content = await _moduleService.LoadLicenseContentAsync(term, cancellation.Token);
            if (loadVersion != _agreementLoadVersion)
            {
                return;
            }

            ShowAgreementContent(term, content);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A different agreement or the module page replaced this request.
        }
        catch (Exception ex)
        {
            if (loadVersion != _agreementLoadVersion)
            {
                return;
            }

            ShowAgreementContent(term, null);
            AgreementInfoBar.Message = $"The full license text could not be loaded. {ex.Message}";
            AgreementInfoBar.IsOpen = true;
        }
        finally
        {
            if (loadVersion == _agreementLoadVersion)
            {
                _isLoadingAgreement = false;
                AgreementCheckBox.IsEnabled = true;
                IsPrimaryButtonEnabled = AgreementCheckBox.IsChecked == true;
                AgreementBodyScrollViewer.Focus(FocusState.Programmatic);
                _agreementLoadCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ShowAgreementContent(UnityLicenseTerm term, string? loadedContent)
    {
        var fallback = string.IsNullOrWhiteSpace(term.Message)
            ? "Review the license terms using the link below before continuing."
            : term.Message;
        AgreementBodyTextBlock.Text = string.IsNullOrWhiteSpace(loadedContent)
            ? fallback
            : loadedContent;
        AgreementLoadingPanel.Visibility = Visibility.Collapsed;
        AgreementContentPanel.Visibility = Visibility.Visible;
    }

    private IReadOnlyList<UnityLicenseTerm> BuildLicenseTerms(
        IReadOnlyCollection<UnityEditorModuleInfo> selectedModules)
    {
        var selectedIds = selectedModules
            .Select(module => module.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _modules
            .Where(module => selectedIds.Contains(module.Id))
            .SelectMany(module => module.LicenseTerms)
            .DistinctBy(
                term => $"{term.Label}\u001f{term.NavigateUri}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private List<UnityEditorModuleInfo> GetSelectedModules()
    {
        var selectedItems = ModulesTreeView?.SelectedItems;
        if (selectedItems is null)
        {
            return [];
        }

        var selectedIds = selectedItems
            .OfType<UnityModuleTreeItem>()
            .Select(item => item.Module)
            .Where(module => module?.IsSelectable == true)
            .Cast<UnityEditorModuleInfo>()
            .Select(module => module.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _modules
            .Where(module => selectedIds.Contains(module.Id))
            .ToList();
    }

    private Task ContinueToInstallationAsync()
    {
        if (_cliToolService.GetStatus().IsInstalled)
        {
            CompleteInstallationRequest(cliRelease: null);
            return Task.CompletedTask;
        }

        return ShowToolSetupAsync();
    }

    private async Task ShowToolSetupAsync()
    {
        CancelAgreementLoad();
        CancelToolSetupRequest();
        _stage = DialogStage.ToolSetup;
        _cliRelease = null;

        Title = "Install Unity CLI";
        ModuleSelectionPanel.Visibility = Visibility.Collapsed;
        AgreementPanel.Visibility = Visibility.Collapsed;
        ToolSetupPanel.Visibility = Visibility.Visible;
        ToolSetupInfoBar.IsOpen = false;
        ToolSetupLocationTextBlock.Text = UnityCliToolService.ToolRootPath;
        ToolSetupVersionTextBlock.Text = "Checking…";
        ToolSetupSizeTextBlock.Text = "Checking…";
        ToolSetupProgressPanel.Visibility = Visibility.Visible;
        ToolSetupProgressBar.IsIndeterminate = true;
        ToolSetupProgressBar.ShowError = false;
        ToolSetupProgressTextBlock.Text = "Checking the latest release…";
        SecondaryButtonText = "Back";
        PrimaryButtonText = "Install";
        IsPrimaryButtonEnabled = false;
        DefaultButton = ContentDialogButton.Primary;

        var cancellation = new CancellationTokenSource();
        _toolSetupCancellation = cancellation;
        try
        {
            var release = await _cliToolService.GetLatestReleaseAsync(cancellation.Token);
            if (!ReferenceEquals(_toolSetupCancellation, cancellation)
                || _stage != DialogStage.ToolSetup)
            {
                return;
            }

            _cliRelease = release;
            ToolSetupVersionTextBlock.Text = release.Version;
            ToolSetupSizeTextBlock.Text = release.DownloadSizeBytes is > 0
                ? FormatBytes(release.DownloadSizeBytes.Value)
                : "Size provided when download starts";
            ToolSetupProgressPanel.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The dialog moved to a different state or closed.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_toolSetupCancellation, cancellation))
            {
                return;
            }

            ToolSetupProgressPanel.Visibility = Visibility.Collapsed;
            ToolSetupInfoBar.Message = ex.Message;
            ToolSetupInfoBar.IsOpen = true;
            PrimaryButtonText = "Retry";
            IsPrimaryButtonEnabled = true;
        }
        finally
        {
            if (ReferenceEquals(_toolSetupCancellation, cancellation))
            {
                _toolSetupCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CompleteInstallationRequest(UnityCliReleaseInfo? cliRelease)
    {
        var modules = _pendingModules
            .Select(module => new UnityModuleInstallationTarget(
                module.Id,
                module.Name,
                module.DownloadSizeBytes))
            .ToList();
        if (_installsEditor)
        {
            modules.Insert(
                0,
                new UnityModuleInstallationTarget(
                    "unity-editor",
                    $"Unity Editor {_editor.Version}",
                    _editorDownloadSizeBytes));
        }

        if (modules.Count == 0)
        {
            ReturnToModuleSelection();
            ShowStatus("Nothing selected", "Select at least one module to install.", InfoBarSeverity.Warning);
            return;
        }

        InstallationRequest = new UnityModuleInstallationRequest(
            _editor.Version,
            _editor.InstallDirectory,
            modules,
            cliRelease)
        {
            InstallsEditor = _installsEditor,
            EditorRevision = _release?.Revision
        };
        Hide();
    }

    private void OnModuleRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UnityModuleTreeItem { ShowMaintenanceActions: true } } element)
        {
            if (element.FindName("ModuleActionsButton") is Button button && button.Flyout is FlyoutBase flyout)
            {
                flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
                e.Handled = true;
            }
        }
    }

    private void OnRepairModuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UnityEditorModuleInfo { IsInstalled: true } module })
        {
            CompleteMaintenanceRequest(module, UnityModuleOperationKind.Repair);
        }
    }

    private void OnRemoveModuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UnityEditorModuleInfo { CanRemove: true } module })
        {
            CompleteMaintenanceRequest(module, UnityModuleOperationKind.Remove);
        }
    }

    private void CompleteMaintenanceRequest(
        UnityEditorModuleInfo module,
        UnityModuleOperationKind operationKind)
    {
        InstallationRequest = new UnityModuleInstallationRequest(
            _editor.Version,
            _editor.InstallDirectory,
            [new UnityModuleInstallationTarget(module.Id, module.Name, module.DownloadSizeBytes)],
            CliRelease: null)
        {
            OperationKind = operationKind
        };
        Hide();
    }

    private void ReturnToModuleSelection()
    {
        CancelAgreementLoad();
        CancelToolSetupRequest();
        _stage = DialogStage.ModuleSelection;
        _pendingModules.Clear();
        _pendingAgreements.Clear();
        _agreementIndex = 0;
        _isLoadingAgreement = false;

        Title = _selectionTitle;
        AgreementInfoBar.IsOpen = false;
        AgreementPanel.Visibility = Visibility.Collapsed;
        ToolSetupPanel.Visibility = Visibility.Collapsed;
        ModuleSelectionPanel.Visibility = Visibility.Visible;
        SecondaryButtonText = string.Empty;
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        UpdateSelectionState();
        ModulesTreeView.Focus(FocusState.Programmatic);
    }

    private void CancelAgreementLoad()
    {
        _agreementLoadVersion++;
        _agreementLoadCancellation?.Cancel();
        _agreementLoadCancellation = null;
    }

    private void CancelToolSetupRequest()
    {
        _toolSetupCancellation?.Cancel();
        _toolSetupCancellation = null;
    }

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        CancelAgreementLoad();
        CancelToolSetupRequest();
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 bytes";
        }

        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }
}
