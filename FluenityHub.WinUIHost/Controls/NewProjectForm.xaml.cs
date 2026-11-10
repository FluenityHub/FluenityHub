using System.Diagnostics;
using System.Text.RegularExpressions;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Controls;

public sealed partial class NewProjectForm : UserControl
{
    public string CreatedProjectPath { get; private set; } = string.Empty;
    public string CreatedProjectTitle { get; private set; } = string.Empty;
    public string SelectedVersion { get; private set; } = string.Empty;

    private readonly SourceControlService _sourceControlService = new();
    private readonly GitService _gitService = new();
    private readonly TemplateService _templateService = new();
    private CustomTemplateInfo? _selectedTemplate;
    private IReadOnlyDictionary<string, string> _installedEditors = new Dictionary<string, string>();
    private IReadOnlyList<UnityProjectTemplate> _templateOptions = [UnityTemplateCatalogService.Blank];
    private CancellationTokenSource? _templateCatalogCancellation;
    private CancellationTokenSource? _officialTemplateCreation;
    private UnityProjectTemplate? _selectedTemplateOption;
    private bool _showGallery;
    private bool _isSourceControlEnabled;
    private bool _isAuthorized;
    private bool _isBusy;
    private bool _isInitialized;
    private int _authorizationRequestId;
    private bool _isApplyingSavedToken;
    private string? _appliedSavedToken;
    private string? _appliedSavedTokenProvider;
    private readonly CancellationTokenSource _browserCancellation = new();

    internal event EventHandler? StateChanged;
    internal event EventHandler? TemplatesChanged;
    internal bool CanCreate { get; private set; }
    internal bool IsCreating => _isBusy;
    internal bool IsLoadingTemplates => _templateCatalogCancellation is not null;
    internal string EditorVersion => EditorVersionComboBox.SelectedItem as string ?? "Not specified";
    internal bool CanCancelCreation => _officialTemplateCreation is { IsCancellationRequested: false };
    internal bool IsCanceling => _officialTemplateCreation?.IsCancellationRequested == true;
    internal IReadOnlyList<UnityProjectTemplate> TemplateOptions => _templateOptions;
    internal UnityProjectTemplate? SelectedTemplateOption => _selectedTemplateOption;

    public NewProjectForm()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_isInitialized && _selectedTemplate is null)
                await LoadTemplateOptionsAsync();
        };
    }

    internal void Configure(
        IReadOnlyDictionary<string, string> installedEditors,
        CustomTemplateInfo? selectedTemplate = null,
        string? initialVersion = null,
        bool showGallery = false)
    {
        _selectedTemplate = selectedTemplate;
        _installedEditors = installedEditors;
        _showGallery = showGallery;
        _isSourceControlEnabled = new AppSettingsStore().Load().EnableSourceControl;
        if (!showGallery)
        {
            MinWidth = 480;
            ProjectOptionsScrollViewer.MaxHeight = 580;
        }
        if (showGallery || selectedTemplate is not null)
        {
            TemplateComboBox.Visibility = Visibility.Collapsed;
            SelectedTemplateNameTextBlock.Visibility = Visibility.Visible;
        }
        if (showGallery)
        {
            ProjectOptionsPanel.Children.Remove(TemplateDetailsSection);
            ProjectOptionsPanel.Children.Insert(0, TemplateDetailsSection);
        }

        SourceControlExpander.Visibility = _isSourceControlEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_isSourceControlEnabled)
        {
            SourceControlProviderComboBox.SelectedIndex = 0;
        }

        LocationTextBox.Text =
            new UnityHubProjectSettingsService().GetProjectLocation();

        var versionsList = installedEditors.Keys.ToList();
        EditorVersionComboBox.ItemsSource = versionsList;

        if (_selectedTemplate is not null)
        {
            ProjectNameTextBox.Text = $"{_selectedTemplate.Name} Project";
            var matchingVersionIdx = versionsList.FindIndex(v => string.Equals(v, _selectedTemplate.EditorVersion, StringComparison.OrdinalIgnoreCase));
            if (matchingVersionIdx >= 0)
            {
                EditorVersionComboBox.SelectedIndex = matchingVersionIdx;
            }
            else if (versionsList.Count > 0)
            {
                EditorVersionComboBox.SelectedIndex = 0;
            }
        }
        else if (!string.IsNullOrWhiteSpace(initialVersion))
        {
            var matchingVersionIdx = versionsList.FindIndex(v => string.Equals(v, initialVersion, StringComparison.OrdinalIgnoreCase));
            if (matchingVersionIdx >= 0)
            {
                EditorVersionComboBox.SelectedIndex = matchingVersionIdx;
            }
            else if (versionsList.Count > 0)
            {
                EditorVersionComboBox.SelectedIndex = 0;
            }
        }
        else if (versionsList.Count > 0)
        {
            EditorVersionComboBox.SelectedIndex = 0;
        }

        UpdateSlugPreview();
        _isInitialized = true;
        if (_selectedTemplate is null)
        {
            SetTemplateOptions(_templateOptions);
        }
        else
        {
            SelectedTemplateNameTextBlock.Text = _selectedTemplate.Name;
            TemplateDescriptionTextBlock.Text = _selectedTemplate.Description;
        }
        ValidateInputs(showMessage: false);
        if (IsLoaded && _selectedTemplate is null) _ = LoadTemplateOptionsAsync();
    }

    internal void Close()
    {
        _authorizationRequestId++;
        _templateCatalogCancellation?.Cancel();
        _browserCancellation.Cancel();
        _browserCancellation.Dispose();
    }

    private void OnProjectNameTextChanged(object sender, TextChangedEventArgs e)
    {
        var projectName = ProjectNameTextBox.Text?.Trim() ?? string.Empty;
        if (RepositoryNameTextBox is not null && (string.IsNullOrWhiteSpace(RepositoryNameTextBox.Text) || RepositoryNameTextBox.Tag as string == "auto"))
        {
            RepositoryNameTextBox.Text = projectName;
            RepositoryNameTextBox.Tag = "auto";
        }
        UpdateSlugPreview();
        ValidateInputs(showMessage: true);
    }

    private void OnRepositoryNameChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.FocusState != FocusState.Unfocused)
        {
            tb.Tag = "custom";
        }
        UpdateSlugPreview();
        ValidateInputs(showMessage: true);
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        ValidateInputs(showMessage: true);
    }

    private async void OnInputSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ValidateInputs(showMessage: true);
        if (_isInitialized && _selectedTemplate is null && ReferenceEquals(sender, EditorVersionComboBox))
            await LoadTemplateOptionsAsync();
    }

    private void SetTemplateOptions(IReadOnlyList<UnityProjectTemplate> options, UnityProjectTemplate? previous = null)
    {
        _templateOptions = options;
        var selectedIndex = previous is null ? 0 : options.ToList().FindIndex(option =>
            previous.CustomTemplate is { } custom
                ? string.Equals(option.CustomTemplate?.TemplateFolderPath, custom.TemplateFolderPath, StringComparison.OrdinalIgnoreCase)
                : option.CustomTemplate is null && option.PackageName == previous.PackageName && option.Version == previous.Version);
        if (selectedIndex < 0) selectedIndex = 0;
        if (!_showGallery)
        {
            TemplateComboBox.ItemsSource = options.Select(option => option.DisplayName).ToList();
            TemplateComboBox.SelectedIndex = selectedIndex;
        }
        SelectTemplateOption(options[selectedIndex]);
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    internal Task RefreshTemplatesAsync() => LoadTemplateOptionsAsync(preserveSelection: true);

    private async Task LoadTemplateOptionsAsync(bool preserveSelection = false)
    {
        var previous = preserveSelection || _selectedTemplateOption?.CustomTemplate is not null ? _selectedTemplateOption : null;
        _templateCatalogCancellation?.Cancel();
        SetTemplateOptions([UnityTemplateCatalogService.Blank]);
        if (EditorVersionComboBox.SelectedItem is not string version || !_installedEditors.TryGetValue(version, out var editor))
        {
            TemplateCatalogStatusTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        using var request = new CancellationTokenSource();
        _templateCatalogCancellation = request;
        StateChanged?.Invoke(this, EventArgs.Empty);
        TemplateCatalogStatusTextBlock.Text = "Loading Unity templates...";
        TemplateCatalogStatusTextBlock.Visibility = _showGallery ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            var officialTask = UnityTemplateCatalogService.LoadAsync(version, editor, request.Token);
            var customTask = _showGallery
                ? Task.Run(() => _templateService.GetCustomTemplates(repairMetadata: false), request.Token)
                : Task.FromResult(new List<CustomTemplateInfo>());
            await Task.WhenAll(officialTask, customTask);
            var (templates, available) = await officialTask;
            var custom = await customTask;
            if (request.IsCancellationRequested) return;
            SetTemplateOptions(custom.Count == 0 ? templates : templates.Concat(custom.Select(template =>
                new UnityProjectTemplate(string.Empty, template.Name, "Custom", template.DisplayDescription, template.DisplayVersion)
                { CustomTemplate = template })).ToList(), previous);
            TemplateCatalogStatusTextBlock.Text = available ? string.Empty : "Online templates aren’t available. You can still use installed templates.";
            TemplateCatalogStatusTextBlock.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Debug.WriteLine(ex);
            if (!request.IsCancellationRequested)
            {
                TemplateCatalogStatusTextBlock.Text = "Unity templates couldn’t be loaded. You can still create a blank project.";
                TemplateCatalogStatusTextBlock.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (ReferenceEquals(_templateCatalogCancellation, request))
            {
                _templateCatalogCancellation = null;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _selectedTemplate is not null) return;
        var index = TemplateComboBox.SelectedIndex;
        SelectTemplateOption(index >= 0 && index < _templateOptions.Count ? _templateOptions[index] : null);
    }

    internal void SelectTemplateOption(UnityProjectTemplate? template)
    {
        _selectedTemplateOption = template;
        SelectedTemplateNameTextBlock.Text = template?.Name ?? "Select a template";
        TemplateDescriptionTextBlock.Text = template is { DownloadSize: > 0 }
            ? $"Download size: {template.DownloadSize / (1024d * 1024):0.#} MB\n{template.Description}"
            : template?.Description ?? string.Empty;
        ValidateInputs(showMessage: false);
    }

    private void UpdateSlugPreview()
    {
        if (SlugPreviewTextBlock is null) return;
        var name = RepositoryNameTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ProjectNameTextBox?.Text?.Trim() ?? "my-project";
        }

        var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9\-]", "-").Trim('-');
        SlugPreviewTextBlock.Text = $"Slug preview: {slug}";
    }

    private void OnSourceControlProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceControlProviderComboBox is null || TokenSectionStackPanel is null || RepositoryDetailsStackPanel is null) return;
        _authorizationRequestId++;
        SetAuthorizationVisualState(false);
        var tag = (SourceControlProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
        _isAuthorized = false;

        if (TokenInfoButton is not null)
        {
            ToolTipService.SetToolTip(TokenInfoButton, tag == "gitlab"
                ? "Your token is stored securely in your OS keychain.\n\nRequired Scopes:\n. api\n. read_user\n\nThis allows FluenityHub to create the repository and sync your initial commit via HTTPS."
                : "Your token is stored securely in your OS keychain.\n\nFor Classic Tokens:\n. Select repo scope\n\nFor Fine-grained Tokens:\n. Add the permissions Administration and Contents and set access to Read and Write.\n\nThese allow FluenityHub to create the repository and sync your initial commit via HTTPS.");
        }

        if (tag == "github" || tag == "gitlab")
        {
            TokenSectionStackPanel.Visibility = Visibility.Visible;
            CustomGitRemoteStackPanel.Visibility = Visibility.Collapsed;
            RepositoryDetailsStackPanel.Visibility = Visibility.Visible;
            HostedRepositoryNameStackPanel.Visibility = Visibility.Visible;
            HostedVisibilityStackPanel.Visibility = Visibility.Visible;
            HostedDescriptionStackPanel.Visibility = Visibility.Visible;
            SourceControlConfigurationExpander.Header = "Additional configuration";
            CreateInitialCommitCheckBox.Content = "Create and push initial commit";
            AutoFillSavedToken(tag);
        }
        else if (tag == "git")
        {
            TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            CustomGitRemoteStackPanel.Visibility = Visibility.Visible;
            RepositoryDetailsStackPanel.Visibility = Visibility.Visible;
            HostedRepositoryNameStackPanel.Visibility = Visibility.Collapsed;
            HostedVisibilityStackPanel.Visibility = Visibility.Collapsed;
            HostedDescriptionStackPanel.Visibility = Visibility.Collapsed;
            SourceControlConfigurationExpander.Header = "Git configuration";
            CreateInitialCommitCheckBox.Content = "Create initial commit";
            _isAuthorized = true;
        }
        else
        {
            TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            CustomGitRemoteStackPanel.Visibility = Visibility.Collapsed;
            RepositoryDetailsStackPanel.Visibility = Visibility.Collapsed;
        }

        ValidateInputs(showMessage: false);
    }

    private void AutoFillSavedToken(string provider)
    {
        _isAuthorized = false;
        ShowUnlinkedState();

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
            OnAuthorizeClick(this, new RoutedEventArgs());
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
        var token = currentToken.Trim();
        _isAuthorized = false;
        if (AuthorizeButton is not null)
        {
            AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(token);
        }

        // Token changes also occur when a saved Credential Locker token is applied while
        // switching providers. Recompute validity without surfacing unrelated
        // form errors such as a missing repository name.
        ValidateInputs(showMessage: false);
    }

    private void OnDisconnectTokenClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        if (tag == "gitlab") CredentialService.RemoveGitLabToken();
        else CredentialService.RemoveGitHubToken();

        if (TokenPasswordBox is not null) TokenPasswordBox.Password = string.Empty;
        ShowUnlinkedState();
    }

    private void OnGetTokenClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        var url = tag switch
        {
            "gitlab" => "https://gitlab.com/-/user_settings/personal_access_tokens?name=FluenityHub&scopes=api,read_user",
            _ => "https://github.com/settings/tokens/new?description=FluenityHub&scopes=repo"
        };

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private async void OnAuthorizeClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        var token = TokenPasswordBox?.Password?.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            ShowAuthStatus("Please enter a personal access token.", isError: true);
            return;
        }

        await ValidateAndApplyTokenAsync(tag, token);
    }

    private async Task ValidateAndApplyTokenAsync(string tag, string token)
    {
        var requestId = ++_authorizationRequestId;
        _isAuthorized = false;
        ShowUnlinkedState();
        if (AuthorizeButton is not null) AuthorizeButton.IsEnabled = false;
        SetAuthorizationVisualState(true);
        ValidateInputs(showMessage: false);

        try
        {
            var (success, primaryOwner, _, error) =
                await _sourceControlService.AuthorizeTokenAsync(tag, token);
            if (requestId != _authorizationRequestId) return;

            if (success)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    if (tag == "gitlab") CredentialService.SaveGitLabToken(token);
                    else CredentialService.SaveGitHubToken(token);
                }

                ShowLinkedState(primaryOwner);
            }
            else
            {
                ShowUnlinkedState();
                ShowAuthStatus(error, isError: true);
            }
        }
        catch (Exception ex)
        {
            if (requestId != _authorizationRequestId) return;
            ShowUnlinkedState();
            ShowAuthStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            if (requestId == _authorizationRequestId)
            {
                if (AuthorizeButton is not null)
                    AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(TokenPasswordBox?.Password);
                SetAuthorizationVisualState(false);
                ValidateInputs(showMessage: false);
            }
        }
    }

    private async void OnBrowserSignInClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        var provider = (SourceControlProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        var requestId = ++_authorizationRequestId;
        try
        {
            var credential = await GitCredentialManagerService.GetAsync(provider, null, true, _browserCancellation.Token);
            if (requestId != _authorizationRequestId) return;
            if (credential is null) { ShowAuthStatus("Git sign-in was not completed.", true); return; }
            GitCredentialManagerService.SetAccount(provider, null, credential.UserName);
            TokenPasswordBox.Password = string.Empty;
            await ValidateAndApplyTokenAsync(provider, string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (requestId == _authorizationRequestId) ShowAuthStatus(ex.Message, true); }
        finally { button.IsEnabled = true; }
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

    private void ShowLinkedState(string username)
    {
        _isAuthorized = true;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Collapsed;
        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Visible;
        if (TokenUserTextBlock is not null) TokenUserTextBlock.Text = $"Connected as @{username}";
        if (AuthStatusTextBlock is not null) AuthStatusTextBlock.Visibility = Visibility.Collapsed;
        ValidateInputs(showMessage: false);
    }

    private void ShowUnlinkedState()
    {
        _isAuthorized = false;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;
        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
        ValidateInputs(showMessage: false);
    }

    private void ShowAuthStatus(string message, bool isError)
    {
        if (AuthStatusTextBlock is null) return;
        AuthStatusTextBlock.Text = message;
        AuthStatusTextBlock.Foreground = isError
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        AuthStatusTextBlock.Visibility = Visibility.Visible;
    }

    private async void OnBrowseLocationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MainWindow.Instance is null) return;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(MainWindow.Instance.AppWindow.Id)
            {
                Title = "Choose a project location",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                LocationTextBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnBrowseLocationClick failed: {ex}");
        }
    }

    internal async Task<bool> CreateAsync()
    {
        if (_isBusy) return false;
        var validationError = GetValidationError(out _);
        if (validationError is not null)
        {
            ValidateInputs(showMessage: true);
            return false;
        }

        try
        {
            var projectName = ProjectNameTextBox.Text?.Trim() ?? string.Empty;
            var parentFolder = LocationTextBox.Text?.Trim() ?? string.Empty;
            var selectedVersion = (EditorVersionComboBox.SelectedItem as string)!;

            var providerTag = GetSelectedSourceControlProvider();
            bool isPrivate = PrivateVisibilityRadioButton?.IsChecked == true;
            string repoName = RepositoryNameTextBox?.Text?.Trim() ?? projectName;
            string defaultBranch = DefaultBranchTextBox?.Text?.Trim() ?? "main";
            if (string.IsNullOrWhiteSpace(defaultBranch)) defaultBranch = "main";
            string description = RepositoryDescriptionTextBox?.Text?.Trim() ?? string.Empty;
            bool enableLfs = EnableGitLfsCheckBox?.IsChecked == true;
            bool doInitialCommit = CreateInitialCommitCheckBox?.IsChecked == true;
            string token = TokenPasswordBox?.Password?.Trim() ?? string.Empty;
            string customGitRemoteUrl = CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty;

            var projectPath = Path.Combine(parentFolder, projectName);
            var custom = _selectedTemplate ?? SelectedTemplateOption?.CustomTemplate;
            var official = custom is null && SelectedTemplateOption is { PackageName.Length: > 0 } preset ? preset : null;

            SetCreationState(true);
            await Task.Yield();
            try
            {
                if (official is not null)
                {
                    using var creation = new CancellationTokenSource();
                    _officialTemplateCreation = creation;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    CreationProgressTextBlock.Text = $"Preparing {official.Name}...";
                    var progress = new Progress<int>(percent =>
                    {
                        if (ReferenceEquals(_officialTemplateCreation, creation) && !creation.IsCancellationRequested)
                            CreationProgressTextBlock.Text = $"Downloading {official.Name} ({percent}%)...";
                    });
                    try
                    {
                        var archive = await UnityTemplateCatalogService.GetArchiveAsync(official,
                            new UnityHubLocationSettingsService().GetDownloadLocation(), progress, creation.Token);
                        CreationProgressTextBlock.Text = $"Extracting {official.Name}...";
                        await Task.Run(() => TemplateService.CreateProjectFromOfficialTemplate(
                            archive, projectPath, selectedVersion, creation.Token), creation.Token);
                    }
                    catch (OperationCanceledException) when (creation.IsCancellationRequested)
                    {
                        return false;
                    }
                    finally
                    {
                        _officialTemplateCreation = null;
                        StateChanged?.Invoke(this, EventArgs.Empty);
                        CreationProgressTextBlock.Text = "Creating project files and applying the selected options.";
                    }
                }

                await Task.Run(async () =>
                {
                    if (custom is not null)
                    {
                        var created = await _templateService.CreateProjectFromTemplateAsync(custom, projectPath, selectedVersion);
                        if (!created)
                        {
                            throw new InvalidOperationException("The selected template could not be extracted.");
                        }
                    }
                    else if (official is null)
                    {
                        if (!Directory.Exists(parentFolder))
                        {
                            Directory.CreateDirectory(parentFolder);
                        }

                        Directory.CreateDirectory(projectPath);
                        Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
                        Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));

                        var versionFile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
                        File.WriteAllText(versionFile, $"m_EditorVersion: {selectedVersion}\r\nm_EditorVersionWithRevision: {selectedVersion}\r\n");
                    }

                    // Configure the selected source-control provider.
                    if (providerTag != "none")
                    {
                        var remoteUrl = providerTag == "git"
                            ? customGitRemoteUrl
                            : string.Empty;
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

                            var (createOk, createdRemoteUrl, createError) =
                                await _sourceControlService.CreateRemoteRepositoryAsync(
                                    providerTag,
                                    token,
                                    primaryUser,
                                    primaryUser,
                                    repoName,
                                    isPrivate,
                                    description);
                            if (!createOk || string.IsNullOrWhiteSpace(createdRemoteUrl))
                            {
                                throw new InvalidOperationException(createError);
                            }

                            remoteUrl = createdRemoteUrl;
                            credentialUser = providerTag == "gitlab" ? "oauth2" : "x-access-token";
                            credentialPassword = token;
                            if (providerTag == "gitlab") CredentialService.SaveGitLabToken(token);
                            else CredentialService.SaveGitHubToken(token);
                        }

                        var (gitOk, gitMessage) = await _gitService.InitAndSetupUnityGitAsync(
                            projectPath,
                            remoteUrl,
                            defaultBranch,
                            enableLfs,
                            doInitialCommit,
                            credentialUser,
                            credentialPassword);
                        if (!gitOk)
                        {
                            throw new InvalidOperationException(gitMessage);
                        }
                    }
                });

                CreatedProjectPath = projectPath;
                CreatedProjectTitle = projectName;
                SelectedVersion = selectedVersion;
                return true;
            }
            catch (Exception ex)
            {
                ShowError($"Failed to create project files: {ex.Message}");
                return false;
            }
            finally
            {
                SetCreationState(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Create project failed: {ex}");
            ShowError($"Failed to create project: {ex.Message}");
            return false;
        }
    }

    private void ValidateInputs(bool showMessage)
    {
        if (!_isInitialized)
        {
            return;
        }

        var error = GetValidationError(out var nameInUse);
        CanCreate = !_isBusy && error is null;
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (ValidationInfoBar is null || _isBusy)
        {
            return;
        }

        ProjectNameWarning.Visibility = nameInUse ? Visibility.Visible : Visibility.Collapsed;
        ValidationInfoBar.Severity = InfoBarSeverity.Error;
        ValidationInfoBar.IsClosable = true;
        ValidationInfoBar.Message = error ?? string.Empty;
        ValidationInfoBar.IsOpen = !nameInUse && error is not null && showMessage;
    }

    private string? GetValidationError(out bool nameInUse)
    {
        nameInUse = false;
        var projectName = ProjectNameTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return "Enter a project name.";
        }

        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            projectName.EndsWith(' ') ||
            projectName.EndsWith('.'))
        {
            return "The project name contains characters that Windows cannot use in a folder name.";
        }

        var parentFolder = LocationTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            return "Choose a project location.";
        }

        string projectPath;
        try
        {
            projectPath = Path.GetFullPath(Path.Combine(parentFolder, projectName));
        }
        catch
        {
            return "Enter a valid project location.";
        }

        if (Directory.Exists(projectPath) || File.Exists(projectPath))
        {
            nameInUse = true;
            return "Choose a different name or project location.";
        }

        if (EditorVersionComboBox?.SelectedItem is not string)
        {
            return "Select an installed Unity Editor version.";
        }

        if (_selectedTemplate is null && _selectedTemplateOption is null)
            return "Select a template.";

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
            !_isAuthorized)
        {
            return "Sign in with your browser or authorize a personal access token before creating the project.";
        }

        return null;
    }

    private string GetSelectedSourceControlProvider()
        => _isSourceControlEnabled
            ? (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "none"
            : "none";

    private void SetCreationState(bool isBusy)
    {
        _isBusy = isBusy;
        ProjectOptionsScrollViewer.IsEnabled = !isBusy;
        CreationProgressTextBlock.Text = "Creating project files and applying the selected options.";
        CreationProgressInfoBar.IsOpen = isBusy;
        CanCreate = !isBusy && GetValidationError(out _) is null;
        if (isBusy) ValidationInfoBar.IsOpen = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void CancelCreation()
    {
        if (_officialTemplateCreation is not { IsCancellationRequested: false } creation) return;
        CreationProgressTextBlock.Text = "Canceling template creation...";
        creation.Cancel();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ShowError(string message)
    {
        ValidationInfoBar.Message = message;
        ValidationInfoBar.Severity = InfoBarSeverity.Error;
        ValidationInfoBar.Title = string.Empty;
        ValidationInfoBar.IsClosable = true;
        ValidationInfoBar.IsOpen = true;
        ValidationInfoBar.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }
}
