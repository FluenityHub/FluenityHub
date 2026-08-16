using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class SaveProjectAsTemplateDialog : ContentDialog
{
    public CustomTemplateInfo? ResultTemplate { get; private set; }

    private readonly List<UnityProjectInfo> _availableProjects;
    private readonly IReadOnlyDictionary<string, string> _installedEditors;
    private readonly UnityEditorLocator _editorLocator = new();
    private readonly TemplateService _templateService = new();
    private readonly SourceControlService _sourceControlService = new();
    private readonly GitService _gitService = new();
    private readonly ObservableCollection<string> _selectedRootFiles = [];
    private readonly CustomTemplateInfo? _editingTemplate;
    private readonly bool _isEditMode;
    private readonly bool _isSourceControlEnabled;

    private bool _isStep1 = true;
    private bool _isAuthorized;
    private bool _isBusy;
    private bool _showValidationErrors;
    private string? _selectedSourceProjectPath;
    private string? _customImagePath;
    private string? _displayedImagePath;
    private bool _removeExistingImage;
    private DateTimeOffset _lastImageOpenTime;
    private int _authorizationRequestId;
    private bool _isApplyingSavedToken;
    private string? _appliedSavedToken;
    private string? _appliedSavedTokenProvider;

    public SaveProjectAsTemplateDialog(
        IEnumerable<UnityProjectInfo> projects,
        IReadOnlyDictionary<string, string> installedEditors,
        string? preselectedProjectPath = null)
    {
        InitializeComponent();
        _isSourceControlEnabled = new AppSettingsStore().Load().EnableSourceControl;

        if (MainWindow.Instance is not null)
        {
            RequestedTheme = MainWindow.Instance.CurrentTheme;
        }

        _availableProjects = SortProjectsLikeProjectsPage(
            projects.Where(project => Directory.Exists(project.Path)),
            LoadProjectSortSettings()).ToList();
        _installedEditors = installedEditors;

        SourceProjectComboBox.ItemsSource = _availableProjects.Select(p => p.Title).ToList();
        SelectedRootFilesListView.ItemsSource = _selectedRootFiles;

        if (!string.IsNullOrEmpty(preselectedProjectPath))
        {
            var idx = _availableProjects.FindIndex(p => string.Equals(p.Path, preselectedProjectPath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                SourceProjectComboBox.SelectedIndex = idx;
                SourceProjectPickerSection.Visibility = Visibility.Collapsed;
            }
        }

        UpdateWizardStepUI();
    }

    public SaveProjectAsTemplateDialog(CustomTemplateInfo template)
    {
        InitializeComponent();
        _isSourceControlEnabled = new AppSettingsStore().Load().EnableSourceControl;

        _editingTemplate = template ?? throw new ArgumentNullException(nameof(template));
        _isEditMode = true;
        _availableProjects = [];
        _installedEditors = new Dictionary<string, string>();

        if (MainWindow.Instance is not null)
        {
            RequestedTheme = MainWindow.Instance.CurrentTheme;
        }

        SelectedRootFilesListView.ItemsSource = _selectedRootFiles;
        ConfigureEditMode();
        UpdateWizardStepUI();
    }

    private void ConfigureEditMode()
    {
        Title = "Edit template";
        DialogSubtitleTextBlock.Visibility = Visibility.Collapsed;
        SourceProjectPickerSection.Visibility = Visibility.Collapsed;
        ProjectSettingsOptionPanel.Visibility = Visibility.Collapsed;
        AdvancedTemplateOptionsExpander.Visibility = Visibility.Collapsed;

        TemplateNameTextBox.Text = _editingTemplate!.Name;
        TemplateNameTextBox.IsReadOnly = true;
        TemplateDescriptionTextBox.Text = _editingTemplate.Description;
        TemplateVersionTextBox.Text = _editingTemplate.Version;
        EditorVersionTextBox.Text = _editingTemplate.EditorVersion;
        EditorVersionTextBox.IsEnabled = false;
        EditorMissingWarningPanel.Visibility = Visibility.Collapsed;

        RepositoryNameTextBox.Text = _editingTemplate.Id;
        RepositoryNameTextBox.Tag = "auto";
        RepositoryDescriptionTextBox.Text = _editingTemplate.Description;

        if (_editingTemplate.HasImage)
        {
            ShowCoverImage(_editingTemplate.ImagePath);
        }

        SavingTitleTextBlock.Text = "Updating template...";
        SavingStatusTextBlock.Text = "Updating template metadata, image, and archive.";
    }

    private static AppSettings LoadProjectSortSettings()
    {
        try
        {
            return new AppSettingsStore().Load();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not load project sort settings: {ex}");
            return new AppSettings();
        }
    }

    private static IEnumerable<UnityProjectInfo> SortProjectsLikeProjectsPage(
        IEnumerable<UnityProjectInfo> projects,
        AppSettings settings)
    {
        var sortAscending = settings.SortAscending;
        var isName = string.Equals(settings.SortCriteria, "Name", StringComparison.Ordinal);
        var isVersion = string.Equals(settings.SortCriteria, "EditorVersion", StringComparison.Ordinal);

        IOrderedEnumerable<UnityProjectInfo> sorted;
        if (isName)
        {
            sorted = sortAscending
                ? projects.OrderBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
                : projects.OrderByDescending(project => project.Title, StringComparer.CurrentCultureIgnoreCase);
        }
        else if (isVersion)
        {
            sorted = sortAscending
                ? projects.OrderBy(project => project.Version, StringComparer.OrdinalIgnoreCase)
                : projects.OrderByDescending(project => project.Version, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            sorted = sortAscending
                ? projects.OrderBy(project => project.LastModifiedUtc)
                : projects.OrderByDescending(project => project.LastModifiedUtc);
        }

        var keepStarredOnTop = settings.KeepStarredOnTop && settings.ShowFavoritesColumn;
        var keepSourceControlOnTop =
            !keepStarredOnTop &&
            settings.KeepSourceControlOnTop &&
            settings.EnableSourceControl &&
            settings.ShowSourceControlColumn;

        if (!keepStarredOnTop && !keepSourceControlOnTop)
        {
            return sorted;
        }

        var comparer = Comparer<UnityProjectInfo>.Create((left, right) =>
        {
            int comparison;
            if (isName)
            {
                comparison = string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
            }
            else if (isVersion)
            {
                comparison = string.Compare(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                comparison = left.LastModifiedUtc.CompareTo(right.LastModifiedUtc);
            }

            return sortAscending ? comparison : -comparison;
        });

        return keepStarredOnTop
            ? sorted.OrderByDescending(project => project.IsFavorite).ThenBy(project => project, comparer)
            : sorted.OrderByDescending(project => !string.IsNullOrEmpty(project.SourceControlProvider)).ThenBy(project => project, comparer);
    }

    private void OnSourceProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePlaceholderHelpToolTip();

        if (SourceProjectComboBox.SelectedIndex < 0 || SourceProjectComboBox.SelectedIndex >= _availableProjects.Count)
        {
            _selectedSourceProjectPath = null;
            EditorVersionTextBox.Text = string.Empty;
            EditorMissingWarningPanel.Visibility = Visibility.Collapsed;
            RootFilesComboBox.ItemsSource = null;
            _selectedRootFiles.Clear();
            UpdateSelectedRootFilesState();
            ValidateInput();
            return;
        }

        var project = _availableProjects[SourceProjectComboBox.SelectedIndex];
        EditorVersionTextBox.Text = project.Version;

        if (!string.Equals(_selectedSourceProjectPath, project.Path, StringComparison.OrdinalIgnoreCase))
        {
            _selectedSourceProjectPath = project.Path;
            TemplateNameTextBox.Text = project.Title;
            _selectedRootFiles.Clear();
            UpdateSelectedRootFilesState();
        }

        var isEditorInstalled = !string.IsNullOrWhiteSpace(_editorLocator.FindEditorExecutable(project.Version, _installedEditors));
        EditorMissingWarningPanel.Visibility = !isEditorInstalled ? Visibility.Visible : Visibility.Collapsed;

        // Populate root files list from source project
        try
        {
            if (Directory.Exists(project.Path))
            {
                var files = Directory.GetFiles(project.Path, "*.*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(f => !string.IsNullOrEmpty(f) && !f.StartsWith("."))
                    .ToList();

                RootFilesComboBox.ItemsSource = files;
            }
        }
        catch
        {
            RootFilesComboBox.ItemsSource = null;
        }

        ValidateInput();
    }

    private void UpdatePlaceholderHelpToolTip()
    {
        if (ReplaceNamePlaceholderHelpButton is null) return;

        string projName = "my project";
        if (SourceProjectComboBox?.SelectedIndex >= 0 && SourceProjectComboBox.SelectedIndex < _availableProjects.Count)
        {
            var selected = _availableProjects[SourceProjectComboBox.SelectedIndex];
            var sourceDirectoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(selected.Path));
            if (!string.IsNullOrWhiteSpace(sourceDirectoryName))
            {
                projName = sourceDirectoryName;
            }
        }

        ToolTipService.SetToolTip(
            ReplaceNamePlaceholderHelpButton,
            $"When the template is saved, occurrences of \"{projName}\" in the included root files are replaced with %PROJECT_NAME%. When a new project is created from this template, %PROJECT_NAME% is replaced with the new project's name.");
    }

    private void OnSourceProjectTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        var submittedText = args.Text?.Trim() ?? string.Empty;
        var matchingIndex = _availableProjects.FindIndex(project =>
            string.Equals(project.Title, submittedText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(project.Path, submittedText, StringComparison.OrdinalIgnoreCase));

        args.Handled = true;

        if (matchingIndex >= 0)
        {
            sender.SelectedIndex = matchingIndex;
            sender.Text = _availableProjects[matchingIndex].Title;
            return;
        }

        sender.SelectedIndex = -1;
        _selectedSourceProjectPath = null;
        EditorVersionTextBox.Text = string.Empty;
        EditorMissingWarningPanel.Visibility = Visibility.Collapsed;
        RootFilesComboBox.ItemsSource = null;
        _selectedRootFiles.Clear();
        UpdateSelectedRootFilesState();
        ValidateInput();
        ErrorBanner.Title = "Source project not found";
        ErrorBanner.Message = "Select a project from the list or enter its exact name or path.";
        ErrorBanner.IsOpen = true;
    }

    private void OnTemplateNameTextChanged(object sender, TextChangedEventArgs e)
    {
        var name = TemplateNameTextBox?.Text?.Trim() ?? string.Empty;
        if (RepositoryNameTextBox is not null && (string.IsNullOrWhiteSpace(RepositoryNameTextBox.Text) || RepositoryNameTextBox.Tag as string == "auto"))
        {
            RepositoryNameTextBox.Text = name;
            RepositoryNameTextBox.Tag = "auto";
        }
        UpdateSlugPreview();
        ValidateInput();
    }

    private void OnRepositoryNameChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.FocusState != FocusState.Unfocused)
        {
            tb.Tag = "custom";
        }
        UpdateSlugPreview();
        ValidateInput();
    }

    private void UpdateSlugPreview()
    {
        if (SlugPreviewTextBlock is null) return;
        var name = RepositoryNameTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = TemplateNameTextBox?.Text?.Trim() ?? "my-project";
        }

        var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9\-]", "-").Trim('-');
        SlugPreviewTextBlock.Text = $"Slug preview: {slug}";
    }

    private void OnRootFilesComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootFilesComboBox.SelectedItem is string selectedFile && !_selectedRootFiles.Contains(selectedFile))
        {
            var verticalOffset = DialogContentScrollViewer.VerticalOffset;
            _selectedRootFiles.Add(selectedFile);
            UpdateSelectedRootFilesState();
            RestoreDialogScrollOffsetAfterLayout(verticalOffset);
        }

        RootFilesComboBox.SelectedIndex = -1;
    }

    private void OnRemoveRootFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string fileName })
        {
            var verticalOffset = DialogContentScrollViewer.VerticalOffset;
            _selectedRootFiles.Remove(fileName);
            UpdateSelectedRootFilesState();
            RestoreDialogScrollOffsetAfterLayout(verticalOffset);
        }
    }


    private void OnAdvancedExpanderExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        var verticalOffset = DialogContentScrollViewer.VerticalOffset;
        RestoreDialogScrollOffsetAfterLayout(verticalOffset);
    }

    private void OnAdvancedExpanderCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        var verticalOffset = DialogContentScrollViewer.VerticalOffset;
        RestoreDialogScrollOffsetAfterLayout(verticalOffset);
    }

    private void RestoreDialogScrollOffsetAfterLayout(double verticalOffset)
    {
        _ = RestoreDialogScrollOffsetAfterLayoutAsync(verticalOffset);
    }

    private Task RestoreDialogScrollOffsetAfterLayoutAsync(double verticalOffset)
    {
        var completion = new TaskCompletionSource<bool>();

        if (!DispatcherQueue.TryEnqueue(() =>
        {
            ApplyDialogScrollOffset(verticalOffset);

            // Focus restoration and Expander layout can each enqueue a later
            // bring-into-view pass. Apply the saved offset after both turns.
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                ApplyDialogScrollOffset(verticalOffset);
                completion.TrySetResult(true);
            }))
            {
                completion.TrySetResult(false);
            }
        }))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private void ApplyDialogScrollOffset(double verticalOffset)
    {
        DialogContentScrollViewer.UpdateLayout();

        var maximumVerticalOffset = Math.Max(
            0,
            DialogContentScrollViewer.ExtentHeight - DialogContentScrollViewer.ViewportHeight);
        DialogContentScrollViewer.ChangeView(
            horizontalOffset: null,
            verticalOffset: Math.Min(verticalOffset, maximumVerticalOffset),
            zoomFactor: null,
            disableAnimation: true);
    }

    private void UpdateSelectedRootFilesState()
    {
        var hasSelectedFiles = _selectedRootFiles.Count > 0;
        SelectedRootFilesListView.Visibility = hasSelectedFiles ? Visibility.Visible : Visibility.Collapsed;
        ReplaceNamePlaceholderCheckBox.IsEnabled = hasSelectedFiles;
        if (!hasSelectedFiles)
        {
            ReplaceNamePlaceholderCheckBox.IsChecked = false;
        }

        PlaceholderHelpText.Text = hasSelectedFiles
            ? "Substitutes project name inside selected root files."
            : "Add at least one root file above to enable substitution.";
    }

    private void OnInputFieldsChanged(object sender, TextChangedEventArgs e)
    {
        ValidateInput();
    }

    private bool ValidateInput()
    {
        var name = TemplateNameTextBox?.Text?.Trim() ?? string.Empty;
        var hasDuplicateName =
            !_isEditMode &&
            !string.IsNullOrWhiteSpace(name) &&
            _templateService.TemplateNameExists(name);
        if (TemplateNameValidationTextBlock is not null)
        {
            TemplateNameValidationTextBlock.Visibility = hasDuplicateName
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var validationError = GetValidationError(hasDuplicateName);
        var isValid = validationError is null;
        IsPrimaryButtonEnabled = !_isBusy && isValid;

        if (ErrorBanner is not null)
        {
            if (_showValidationErrors && validationError is not null)
            {
                ErrorBanner.Title = "Check the required information";
                ErrorBanner.Message = validationError;
                ErrorBanner.Severity = InfoBarSeverity.Error;
                ErrorBanner.IsOpen = true;
            }
            else if (validationError is null)
            {
                ErrorBanner.IsOpen = false;
            }
        }

        return isValid;
    }

    private string? GetValidationError(bool hasDuplicateName)
    {
        if (!_isEditMode && SourceProjectComboBox?.SelectedIndex < 0)
        {
            return "Select a source project.";
        }

        if (string.IsNullOrWhiteSpace(TemplateNameTextBox?.Text))
        {
            return "Enter a template name.";
        }

        if (hasDuplicateName)
        {
            return "A template with this name already exists. Choose a different name or update your templates location in Settings.";
        }

        if (string.IsNullOrWhiteSpace(TemplateDescriptionTextBox?.Text))
        {
            return "Enter a template description.";
        }

        var version = TemplateVersionTextBox?.Text?.Trim() ?? string.Empty;
        if (!Regex.IsMatch(
                version,
                @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"))
        {
            return "Enter a valid semantic version, such as 1.0.0 or 2.1.3-alpha.1.";
        }

        if (_isStep1)
        {
            return null;
        }

        var provider = GetSelectedSourceControlProvider();
        if (provider == "none")
        {
            return null;
        }

        if ((provider == "github" || provider == "gitlab")
            && string.IsNullOrWhiteSpace(RepositoryNameTextBox?.Text))
        {
            return "Enter a repository name.";
        }

        if (string.IsNullOrWhiteSpace(DefaultBranchTextBox?.Text))
        {
            return "Enter a default branch name.";
        }

        var customRemoteUrl = CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty;
        if (provider == "git"
            && !string.IsNullOrWhiteSpace(customRemoteUrl)
            && !GitService.IsValidRemoteUrl(customRemoteUrl))
        {
            return "Enter a valid HTTPS or SSH Git remote URL, or leave it blank for local Git.";
        }

        if ((provider == "github" || provider == "gitlab") &&
            (!_isAuthorized || string.IsNullOrWhiteSpace(TokenPasswordBox?.Password)))
        {
            return _isEditMode
                ? "Authorize a personal access token before updating the template."
                : "Authorize a personal access token before creating the template.";
        }

        return null;
    }

    private void UpdateWizardStepUI()
    {
        if (_isStep1)
        {
            Step1Panel.Visibility = Visibility.Visible;
            Step2Panel.Visibility = Visibility.Collapsed;

            PrimaryButtonText = _isSourceControlEnabled
                ? "Next"
                : _isEditMode ? "Update" : "Create";
            SecondaryButtonText = "Back";
            IsSecondaryButtonEnabled = false;
        }
        else
        {
            Step1Panel.Visibility = Visibility.Collapsed;
            Step2Panel.Visibility = Visibility.Visible;

            PrimaryButtonText = _isEditMode ? "Update" : "Create";
            SecondaryButtonText = "Back";
            IsSecondaryButtonEnabled = true;
        }

        ValidateInput();
    }

    private void OnSourceControlProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceControlProviderComboBox is null || TokenSectionStackPanel is null || RepositoryDetailsStackPanel is null) return;
        _authorizationRequestId++;
        SetAuthorizationVisualState(false);
        var tag = (SourceControlProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
        _isAuthorized = false;

        if (tag == "github" || tag == "gitlab")
        {
            TokenSectionStackPanel.Visibility = Visibility.Visible;
            CustomGitRemoteStackPanel.Visibility = Visibility.Collapsed;
            RepositoryDetailsStackPanel.Visibility = Visibility.Visible;
            HostedRepositoryNameStackPanel.Visibility = Visibility.Visible;
            HostedOwnerStackPanel.Visibility = Visibility.Visible;
            HostedVisibilityStackPanel.Visibility = Visibility.Visible;
            HostedDescriptionStackPanel.Visibility = Visibility.Visible;
            SourceControlConfigurationExpander.Header = "Additional configuration";
            AutoFillSavedToken(tag);
        }
        else if (tag == "git")
        {
            TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            CustomGitRemoteStackPanel.Visibility = Visibility.Visible;
            RepositoryDetailsStackPanel.Visibility = Visibility.Visible;
            HostedRepositoryNameStackPanel.Visibility = Visibility.Collapsed;
            HostedOwnerStackPanel.Visibility = Visibility.Collapsed;
            HostedVisibilityStackPanel.Visibility = Visibility.Collapsed;
            HostedDescriptionStackPanel.Visibility = Visibility.Collapsed;
            SourceControlConfigurationExpander.Header = "Git configuration";
            _isAuthorized = true;
        }
        else
        {
            TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            CustomGitRemoteStackPanel.Visibility = Visibility.Collapsed;
            RepositoryDetailsStackPanel.Visibility = Visibility.Collapsed;
        }

        ValidateInput();
    }

    private void AutoFillSavedToken(string provider)
    {
        _isAuthorized = false;
        ShowTokenUnlinkedState();
        OwnerComboBox.ItemsSource = null;

        var token = provider == "gitlab"
            ? CredentialService.GetGitLabToken()
            : CredentialService.GetGitHubToken();

        _appliedSavedToken = token;
        _appliedSavedTokenProvider = provider;
        _isApplyingSavedToken = true;
        try
        {
            TokenPasswordBox.Password = token;
        }
        finally
        {
            _isApplyingSavedToken = false;
        }
        AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(token);
        if (!string.IsNullOrWhiteSpace(token))
        {
            OnAuthorizeTokenClick(this, new RoutedEventArgs());
        }
    }

    private void OnTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        var selectedProvider =
            (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        var currentToken = TokenPasswordBox?.Password ?? string.Empty;
        var isSavedTokenEvent =
            string.Equals(selectedProvider, _appliedSavedTokenProvider, StringComparison.Ordinal) &&
            string.Equals(currentToken, _appliedSavedToken, StringComparison.Ordinal);
        if (!_isApplyingSavedToken && !isSavedTokenEvent)
        {
            _appliedSavedToken = null;
            _appliedSavedTokenProvider = null;
            _authorizationRequestId++;
            SetAuthorizationVisualState(false);
        }
        _isAuthorized = false;
        if (AuthorizeButton is not null)
        {
            AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(currentToken.Trim());
        }

        ValidateInput();
    }

    private async void OnAuthorizeTokenClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        var token = TokenPasswordBox?.Password?.Trim() ?? string.Empty;
        await ValidateAndApplyTokenAsync(tag, token);
    }

    private async Task ValidateAndApplyTokenAsync(string provider, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var requestId = ++_authorizationRequestId;
        _isAuthorized = false;
        ShowTokenUnlinkedState();
        try
        {
            if (AuthorizeButton is not null) AuthorizeButton.IsEnabled = false;
            SetAuthorizationVisualState(true);

            var (ok, primaryUser, orgs, error) =
                await _sourceControlService.AuthorizeTokenAsync(provider, token);
            if (requestId != _authorizationRequestId) return;

            if (ok)
            {
                _isAuthorized = true;
                if (provider == "gitlab") CredentialService.SaveGitLabToken(token);
                else CredentialService.SaveGitHubToken(token);

                OwnerComboBox.ItemsSource = new List<string> { primaryUser }.Concat(orgs).ToList();
                OwnerComboBox.SelectedIndex = 0;

                ShowTokenLinkedState(primaryUser);
            }
            else
            {
                ShowTokenUnlinkedState();
                ErrorBanner.Title = "Authorization failed";
                ErrorBanner.Message = error;
                ErrorBanner.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            if (requestId != _authorizationRequestId) return;
            Debug.WriteLine($"Token authorization failed: {ex}");
            ShowTokenUnlinkedState();
            ErrorBanner.Title = "Authorization failed";
            ErrorBanner.Message = ex.Message;
            ErrorBanner.IsOpen = true;
        }
        finally
        {
            if (requestId == _authorizationRequestId)
            {
                if (AuthorizeButton is not null)
                    AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(TokenPasswordBox?.Password);
                SetAuthorizationVisualState(false);
                ValidateInput();
            }
        }
    }

    private void SetAuthorizationVisualState(bool isAuthorizing)
    {
        if (AuthorizeButton is not null)
            AuthorizeButton.Visibility = isAuthorizing ? Visibility.Collapsed : Visibility.Visible;
        if (AuthorizeLoadingPanel is not null)
            AuthorizeLoadingPanel.Visibility = isAuthorizing ? Visibility.Visible : Visibility.Collapsed;
        if (AuthorizeProgressRing is not null)
            AuthorizeProgressRing.IsActive = isAuthorizing;
    }

    private void ShowTokenLinkedState(string username)
    {
        _isAuthorized = true;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Collapsed;
        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Visible;
        if (TokenUserTextBlock is not null) TokenUserTextBlock.Text = $"Connected as @{username}";
        ValidateInput();
    }

    private void ShowTokenUnlinkedState()
    {
        _isAuthorized = false;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;
        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
        ValidateInput();
    }

    private void OnDisconnectTokenClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        if (tag == "gitlab") CredentialService.SaveGitLabToken(string.Empty);
        else CredentialService.SaveGitHubToken(string.Empty);

        TokenPasswordBox.Password = string.Empty;
        ShowTokenUnlinkedState();
    }

    private void OnGetTokenClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        var url = tag == "gitlab"
            ? "https://gitlab.com/-/user_settings/personal_access_tokens"
            : "https://github.com/settings/tokens/new";

        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private async void OnBrowseCoverImageClick(object sender, RoutedEventArgs e)
    {
        var originalVerticalOffset = DialogContentScrollViewer.VerticalOffset;

        try
        {
            if (MainWindow.Instance is null) return;

            BrowseCoverImageButton.Focus(FocusState.Programmatic);
            await RestoreDialogScrollOffsetAfterLayoutAsync(originalVerticalOffset);

            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(
                MainWindow.Instance.AppWindow.Id)
            {
                Title = "Choose a template image",
                CommitButtonText = "Choose",
                SuggestedStartLocation =
                    Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                SetCoverImage(file.Path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Browse image failed: {ex}");
        }
        finally
        {
            // Re-establish the picker launch control as the focus anchor before
            // restoring the viewport. ContentDialog re-measures when its native
            // child window closes and can otherwise restore an earlier field.
            BrowseCoverImageButton.Focus(FocusState.Programmatic);
            await RestoreDialogScrollOffsetAfterLayoutAsync(originalVerticalOffset);
        }
    }

    private void OnCoverImageDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Use as template image";
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void OnCoverImageDrop(object sender, DragEventArgs e)
    {
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is not null)
            {
                SetCoverImage(file.Path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Drop image failed: {ex}");
        }
    }

    private void OnRemoveCoverImageClick(object sender, RoutedEventArgs e)
    {
        var shouldTransferFocus =
            RemoveCoverImageButton.FocusState != FocusState.Unfocused;

        _customImagePath = null;
        _displayedImagePath = null;
        _removeExistingImage = _isEditMode;
        CoverImagePreview.Source = null;
        CoverImagePreviewContainer.Visibility = Visibility.Collapsed;
        CoverImagePlaceholderIcon.Visibility = Visibility.Visible;
        BrowseCoverImageButton.Visibility = Visibility.Visible;
        if (shouldTransferFocus)
        {
            BrowseCoverImageButton.Focus(FocusState.Programmatic);
        }
        RemoveCoverImageButton.Visibility = Visibility.Collapsed;
        ImageDropText.Text = "or drop files here";
    }

    private void SetCoverImage(string path)
    {
        var extension = Path.GetExtension(path);
        var supportedExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp" };
        if (!File.Exists(path) || !supportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            ErrorBanner.Title = "Unsupported image";
            ErrorBanner.Message = "Choose a PNG, JPG, JPEG, or WebP image.";
            ErrorBanner.IsOpen = true;
            return;
        }

        _customImagePath = path;
        _removeExistingImage = false;
        ShowCoverImage(path);
        ErrorBanner.IsOpen = false;
    }

    private void ShowCoverImage(string path)
    {
        var shouldTransferFocus =
            BrowseCoverImageButton.FocusState != FocusState.Unfocused;

        _displayedImagePath = path;
        CoverImagePreview.Source = CustomTemplateInfo.CreateCoverImageSource(path);
        CoverImagePreviewContainer.Visibility = Visibility.Visible;
        CoverImagePlaceholderIcon.Visibility = Visibility.Collapsed;
        RemoveCoverImageButton.Visibility = Visibility.Visible;
        if (shouldTransferFocus)
        {
            CoverImagePreviewContainer.Focus(FocusState.Programmatic);
        }
        BrowseCoverImageButton.Visibility = Visibility.Collapsed;
        ImageDropText.Text = Path.GetFileName(path);
    }

    private void OnOpenCoverImageClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_displayedImagePath) || !File.Exists(_displayedImagePath))
        {
            return;
        }

        // A Windows double-click raises two Button.Click events. Suppress the
        // second event so the default image application opens only once.
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastImageOpenTime).TotalMilliseconds < 600)
        {
            return;
        }
        _lastImageOpenTime = now;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _displayedImagePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open template image failed: {ex}");
            ErrorBanner.Title = "Image could not be opened";
            ErrorBanner.Message = ex.Message;
            ErrorBanner.IsOpen = true;
        }
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_isStep1)
        {
            args.Cancel = true;
            _isStep1 = true;
            UpdateWizardStepUI();
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_isStep1)
        {
            if (!ValidateInput())
            {
                args.Cancel = true;
                _showValidationErrors = true;
                var duplicateName =
                    !_isEditMode &&
                    _templateService.TemplateNameExists(TemplateNameTextBox.Text.Trim());
                ErrorBanner.Title = duplicateName ? "Template name already exists" : "Missing required fields";
                ErrorBanner.Message = duplicateName
                    ? "A template with this name already exists. Choose a different name or update your templates location in Settings."
                    : _isEditMode
                        ? "Enter a template Name, Description, and Version."
                        : "Please select a source project and enter a template Name, Description, and Version.";
                ErrorBanner.IsOpen = true;
                return;
            }

            if (_isSourceControlEnabled)
            {
                args.Cancel = true;
                _isStep1 = false;
                UpdateWizardStepUI();
                return;
            }
        }

        if (!ValidateInput())
        {
            args.Cancel = true;
            _showValidationErrors = true;
            ValidateInput();
            return;
        }

        // Step 2 Create template processing
        var deferral = args.GetDeferral();
        try
        {
            UnityProjectInfo? sourceProject = null;
            if (!_isEditMode)
            {
                var selectedIdx = SourceProjectComboBox.SelectedIndex;
                if (selectedIdx < 0 || selectedIdx >= _availableProjects.Count)
                {
                    args.Cancel = true;
                    return;
                }

                sourceProject = _availableProjects[selectedIdx];
            }

            var name = TemplateNameTextBox.Text.Trim();
            var description = TemplateDescriptionTextBox.Text.Trim();
            var version = TemplateVersionTextBox.Text.Trim();
            var keepSettings = KeepProjectSettingsCheckBox.IsChecked == true;

            var includedRootFiles = _selectedRootFiles.ToList();
            var replaceProjectName = ReplaceNamePlaceholderCheckBox.IsChecked == true;

            var providerTag = GetSelectedSourceControlProvider();
            bool isPrivate = PrivateVisibilityRadioButton?.IsChecked == true;
            string repoName = RepositoryNameTextBox?.Text?.Trim() ?? name;
            string defaultBranch = DefaultBranchTextBox?.Text?.Trim() ?? "main";
            if (string.IsNullOrWhiteSpace(defaultBranch)) defaultBranch = "main";
            string repoDescription = RepositoryDescriptionTextBox?.Text?.Trim() ?? description;
            bool enableLfs = EnableGitLfsCheckBox?.IsChecked == true;
            string token = TokenPasswordBox?.Password?.Trim() ?? string.Empty;
            string customGitRemoteUrl = CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty;
            string selectedOwner = OwnerComboBox?.SelectedItem as string ?? string.Empty;

            SetCreationState(true);
            ErrorBanner.IsOpen = false;
            await Task.Yield();

            await Task.Run(async () =>
            {
                if (_isEditMode)
                {
                    ResultTemplate = await _templateService.UpdateCustomTemplateAsync(
                        _editingTemplate!,
                        description,
                        version,
                        _customImagePath,
                        _removeExistingImage);
                }
                else
                {
                    ResultTemplate = await _templateService.SaveAsCustomTemplateAsync(
                        sourceProject!,
                        name,
                        description,
                        version,
                        _customImagePath,
                        keepSettings,
                        includedRootFiles,
                        replaceProjectName);
                }

                if (ResultTemplate is not null && providerTag != "none")
                {
                    var remoteUrl = providerTag == "git" ? customGitRemoteUrl : string.Empty;
                    string? credentialUser = null;
                    string? credentialPassword = null;

                    if (providerTag == "github" || providerTag == "gitlab")
                    {
                        var (authOk, primaryUser, _, authError) =
                            await _sourceControlService.AuthorizeTokenAsync(providerTag, token);
                        if (!authOk)
                        {
                            throw new InvalidOperationException(authError);
                        }

                        var owner = string.IsNullOrWhiteSpace(selectedOwner) ? primaryUser : selectedOwner;
                        var (createOk, createdRemoteUrl, createError) =
                            await _sourceControlService.CreateRemoteRepositoryAsync(
                                providerTag,
                                token,
                                primaryUser,
                                owner,
                                repoName,
                                isPrivate,
                                repoDescription);
                        if (!createOk || string.IsNullOrWhiteSpace(createdRemoteUrl))
                        {
                            throw new InvalidOperationException(createError);
                        }

                        remoteUrl = createdRemoteUrl;
                        credentialUser = providerTag == "gitlab" ? "oauth2" : "x-access-token";
                        credentialPassword = token;
                    }

                    var (gitOk, gitMessage) = await _gitService.InitAndSetupUnityGitAsync(
                        ResultTemplate.TemplateFolderPath,
                        remoteUrl,
                        defaultBranch,
                        enableLfs,
                        pushAllChanges: true,
                        credentialUser,
                        credentialPassword);
                    if (!gitOk)
                    {
                        throw new InvalidOperationException(gitMessage);
                    }
                }
            });

            if (ResultTemplate is null)
            {
                args.Cancel = true;
                ErrorBanner.Title = _isEditMode ? "Update failed" : "Creation failed";
                ErrorBanner.Message = _isEditMode
                    ? "Could not update the custom template. Verify the template files and try again."
                    : "Could not save custom template. Please verify project files and try again.";
                ErrorBanner.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ErrorBanner.Title = _isEditMode ? "Template update failed" : "Template creation failed";
            ErrorBanner.Message = ex.Message;
            ErrorBanner.IsOpen = true;
        }
        finally
        {
            SetCreationState(false);
            deferral.Complete();
        }
    }

    private string GetSelectedSourceControlProvider()
        => _isSourceControlEnabled
            ? (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "none"
            : "none";

    private void SetCreationState(bool isBusy)
    {
        _isBusy = isBusy;
        SavingProgressPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        SavingProgressRing.IsActive = isBusy;
        if (isBusy)
        {
            PrimaryButtonText = _isEditMode ? "Updating..." : "Creating...";
        }
        else
        {
            PrimaryButtonText = _isEditMode ? "Update" : "Create";
        }
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = !isBusy && !_isStep1;
        if (!isBusy)
        {
            ValidateInput();
        }
    }
}
