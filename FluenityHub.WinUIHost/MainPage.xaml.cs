using FluenityHub_WinUIHost.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace FluenityHub_WinUIHost;

public sealed partial class MainPage : Page
{
    public static MainPage? Instance { get; private set; }
    private bool _isSynchronizingNavigation;

    public ProjectsPage? CurrentProjectsPage => ContentFrame.Content as ProjectsPage;

    public MainPage()
    {
        InitializeComponent();
        Instance = this;
        AppNavigationView.SelectedItem = FindNavigationItem("projects");
    }

    private void OnNavigationViewSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSynchronizingNavigation)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            NavigateTopLevel(typeof(SettingsPage), args.RecommendedNavigationTransitionInfo);
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "projects" => typeof(ProjectsPage),
            "templates" => typeof(TemplatesPage),
            "editors" => typeof(EditorsPage),
            _ => null
        };

        if (pageType is not null)
        {
            NavigateTopLevel(pageType, args.RecommendedNavigationTransitionInfo);
        }
    }

    private void OnNavigationViewBackRequested(
        NavigationView sender,
        NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack(new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromLeft
            });
        }
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        UpdateBackButton();

        _isSynchronizingNavigation = true;
        try
        {
            AppNavigationView.SelectedItem = e.SourcePageType switch
            {
                var pageType when pageType == typeof(SettingsPage) || pageType == typeof(LicensesPage)
                    => AppNavigationView.SettingsItem,
                var pageType when pageType == typeof(ProjectsPage)
                    => FindNavigationItem("projects"),
                var pageType when pageType == typeof(TemplatesPage)
                    => FindNavigationItem("templates"),
                var pageType when pageType == typeof(EditorsPage)
                    => FindNavigationItem("editors"),
                _ => AppNavigationView.SelectedItem
            };
        }
        finally
        {
            _isSynchronizingNavigation = false;
        }
    }

    private void NavigateTopLevel(Type pageType, NavigationTransitionInfo? transitionInfo = null)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, transitionInfo);
        }

        ContentFrame.BackStack.Clear();
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        AppNavigationView.IsBackEnabled = ContentFrame.CanGoBack;
        AppNavigationView.IsBackButtonVisible = ContentFrame.CanGoBack
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
    }

    private NavigationViewItem? FindNavigationItem(string tag)
        => AppNavigationView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag as string == tag);

    public void NavigateToSettings()
    {
        AppNavigationView.SelectedItem = AppNavigationView.SettingsItem;
        NavigateTopLevel(typeof(SettingsPage), new EntranceNavigationTransitionInfo());
    }

    public void NavigateToLicenses()
    {
        AppNavigationView.SelectedItem = AppNavigationView.SettingsItem;
        NavigateTopLevel(typeof(SettingsPage), new EntranceNavigationTransitionInfo());
        ContentFrame.Navigate(
            typeof(LicensesPage),
            null,
            new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });
    }

    public void NavigateToEditors()
    {
        AppNavigationView.SelectedItem = FindNavigationItem("editors");
        NavigateTopLevel(typeof(EditorsPage), new EntranceNavigationTransitionInfo());
    }

    public void NavigateToProjectsAndShowMissingEditor(Models.UnityProjectInfo project)
    {
        AppNavigationView.SelectedItem = FindNavigationItem("projects");
        NavigateTopLevel(typeof(ProjectsPage), new EntranceNavigationTransitionInfo());
        if (ContentFrame.Content is ProjectsPage projectsPage)
        {
            _ = projectsPage.ShowVersionPickerDialog(project);
        }
    }

    public void OpenExternalProjectPath(string projectPath)
    {
        AppNavigationView.SelectedItem = FindNavigationItem("projects");
        if (ContentFrame.Content is ProjectsPage activeProjectsPage)
        {
            activeProjectsPage.OpenExternalProjectPath(projectPath);
            return;
        }

        NavigatedEventHandler? handler = null;
        handler = (s, e) =>
        {
            ContentFrame.Navigated -= handler;
            if (ContentFrame.Content is ProjectsPage page)
            {
                page.OpenExternalProjectPath(projectPath);
            }
        };
        ContentFrame.Navigated += handler;
        NavigateTopLevel(typeof(ProjectsPage), new EntranceNavigationTransitionInfo());
    }

    public void HandleExternalAction(string action)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "new-project":
            case "newproject":
            case "new":
                NavigateToNewProject();
                break;

            case "install-editor":
            case "installs":
            case "install":
            case "editors":
                NavigateToInstallEditor();
                break;
        }
    }

    public void NavigateToNewProject()
    {
        AppNavigationView.SelectedItem = FindNavigationItem("projects");
        if (ContentFrame.Content is ProjectsPage activeProjectsPage)
        {
            activeProjectsPage.TriggerNewProjectFlow();
            return;
        }

        NavigatedEventHandler? handler = null;
        handler = (s, e) =>
        {
            ContentFrame.Navigated -= handler;
            if (ContentFrame.Content is ProjectsPage page)
            {
                page.TriggerNewProjectFlow();
            }
        };
        ContentFrame.Navigated += handler;
        NavigateTopLevel(typeof(ProjectsPage), new EntranceNavigationTransitionInfo());
    }

    public void NavigateToInstallEditor()
    {
        AppNavigationView.SelectedItem = FindNavigationItem("editors");
        if (ContentFrame.Content is EditorsPage activeEditorsPage)
        {
            activeEditorsPage.TriggerInstallEditorFlow();
            return;
        }

        NavigatedEventHandler? handler = null;
        handler = (s, e) =>
        {
            ContentFrame.Navigated -= handler;
            if (ContentFrame.Content is EditorsPage page)
            {
                page.TriggerInstallEditorFlow();
            }
        };
        ContentFrame.Navigated += handler;
        NavigateTopLevel(typeof(EditorsPage), new EntranceNavigationTransitionInfo());
    }

    public void NavigateToProjectsFilteredByEditor(string editorVersion)
    {
        AppNavigationView.SelectedItem = FindNavigationItem("projects");
        NavigateTopLevel(typeof(ProjectsPage), new EntranceNavigationTransitionInfo());
        if (ContentFrame.Content is ProjectsPage projectsPage)
        {
            projectsPage.FilterByEditorVersion(editorVersion);
        }
    }
}
