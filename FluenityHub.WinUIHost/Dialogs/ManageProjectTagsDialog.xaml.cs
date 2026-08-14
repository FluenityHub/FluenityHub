using System.Collections.ObjectModel;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed class TagChipItem
{
    public string Name { get; }
    public SolidColorBrush ColorBrush { get; }
    public SolidColorBrush TintBrush { get; }

    public TagChipItem(string name)
    {
        Name = name;
        ColorBrush = Helpers.TagColorHelper.GetSolidBrushForTag(name);
        TintBrush = Helpers.TagColorHelper.GetTintBrushForTag(name);
    }

    public override bool Equals(object? obj) => obj is TagChipItem other && Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => Name.ToLowerInvariant().GetHashCode();
}

public sealed partial class ManageProjectTagsDialog : ContentDialog
{
    public ObservableCollection<TagChipItem> AvailableTags { get; } = [];
    public ObservableCollection<TagChipItem> SelectedTags { get; } = [];

    public ManageProjectTagsDialog(UnityProjectInfo project, IEnumerable<string>? existingGlobalTags = null)
    {
        InitializeComponent();

        SubtitleTextBlock.Text = $"Manage tags for {project.Title}";

        // Seed available categories with default Unity presets + any existing tags from app/projects
        var seedTags = new List<string> { "Game", "Client Project", "Prototype", "Personal", "Simulation", "Archived", "Visualization", "Work in Progress", "2D", "3D" };
        if (existingGlobalTags is not null)
        {
            seedTags.AddRange(existingGlobalTags);
        }
        seedTags.AddRange(project.Tags);

        foreach (var tag in seedTags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AvailableTags.Add(new TagChipItem(tag));
        }

        foreach (var tag in project.Tags)
        {
            if (!SelectedTagsAny(tag))
            {
                SelectedTags.Add(new TagChipItem(tag));
            }
        }

        ActiveTagsItemsControl.ItemsSource = SelectedTags;
        AvailableTagsItemsControl.ItemsSource = AvailableTags;

        UpdateUIState();
        UpdateColorSelectionVisuals();

        var customFlyout = new MenuFlyout();
        var removeItem = new MenuFlyoutItem
        {
            Text = "Remove custom color",
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        removeItem.Click += OnRemoveCustomColorClick;
        customFlyout.Items.Add(removeItem);
        CustomColorPreviewBtn.ContextFlyout = customFlyout;

    }

    private bool SelectedTagsAny(string tagName) =>
        SelectedTags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase));

    private void OnAddTagClick(object sender, RoutedEventArgs e)
    {
        AddTagFromInput();
    }

    private void OnNewTagKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddTagFromInput();
            e.Handled = true;
        }
    }

    private string _selectedColorKey = "Blue";
    private string? _editingCategoryName;

    private void OnColorDotClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string colorKey)
        {
            _selectedColorKey = colorKey;
            UpdateColorSelectionVisuals();
        }
    }

    private void OnApplyCustomColorClick(object sender, RoutedEventArgs e)
    {
        var c = TagColorPicker.Color;
        var hexColor = c.A < byte.MaxValue
            ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        if (!string.IsNullOrWhiteSpace(_editingCategoryName))
        {
            var categoryName = _editingCategoryName;
            Helpers.TagColorHelper.SetTagColor(categoryName, hexColor);
            RefreshTagChip(AvailableTags, categoryName);
            RefreshTagChip(SelectedTags, categoryName);
            _editingCategoryName = null;
            CustomColorFlyout.Hide();
            return;
        }

        _selectedColorKey = hexColor;

        var solidBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
        CustomColorPreviewDot.Fill = solidBrush;
        CustomColorPreviewBtn.Tag = hexColor;
        CustomColorPreviewBtn.Visibility = Visibility.Visible;

        UpdateColorSelectionVisuals();
        CustomColorFlyout.Hide();
    }

    private void OnCloseCustomColorClick(object sender, RoutedEventArgs e)
    {
        _editingCategoryName = null;
        CustomColorFlyout.Hide();
    }

    private void OnOpenNewTagColorPickerClick(object sender, RoutedEventArgs e)
    {
        _editingCategoryName = null;
        TagColorPicker.Color = Helpers.TagColorHelper.ParseColor(_selectedColorKey);
    }

    private void OnEditAvailableTagColorClick(object sender, RoutedEventArgs e)
    {
        string categoryName;
        FrameworkElement targetElement = CustomColorButton;

        if (sender is MenuFlyoutItem { Tag: ToggleButton toggleBtn } && toggleBtn.Tag is string nameFromBtn)
        {
            categoryName = nameFromBtn;
            targetElement = toggleBtn;
        }
        else if (sender is MenuFlyoutItem { Tag: string name })
        {
            categoryName = name;
        }
        else
        {
            return;
        }

        _editingCategoryName = categoryName;
        TagColorPicker.Color = Helpers.TagColorHelper.GetColorForTag(categoryName);
        DispatcherQueue.TryEnqueue(() => CustomColorFlyout.ShowAt(targetElement));
    }

    private static void RefreshTagChip(ObservableCollection<TagChipItem> items, string categoryName)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
            {
                items[index] = new TagChipItem(categoryName);
                return;
            }
        }
    }

    private void OnRemoveCustomColorClick(object sender, RoutedEventArgs e)
    {
        var removedColorKey = CustomColorPreviewBtn.Tag as string;
        CustomColorPreviewBtn.Visibility = Visibility.Collapsed;
        CustomColorPreviewBtn.Tag = string.Empty;

        if (!string.IsNullOrEmpty(removedColorKey)
            && removedColorKey.Equals(_selectedColorKey, StringComparison.OrdinalIgnoreCase))
        {
            _selectedColorKey = "Blue";
        }

        UpdateColorSelectionVisuals();
    }

    private void UpdateColorSelectionVisuals()
    {
        var transparentBrush = Application.Current.Resources.TryGetValue("ControlFillColorTransparentBrush", out var transparentObj) && transparentObj is Brush transparent
            ? transparent
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Brush? accentBrush = null;
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accentObj)
            && accentObj is Brush accent)
        {
            accentBrush = accent;
        }
        else if (Application.Current.Resources.TryGetValue("ControlStrongStrokeColorDefaultBrush", out var strokeObj)
                 && strokeObj is Brush stroke)
        {
            accentBrush = stroke;
        }

        if (PresetColorDotsPanel != null)
        {
            foreach (var child in PresetColorDotsPanel.Children)
            {
                if (child is Button btn && btn.Tag is string tagKey)
                {
                    bool isSelected = tagKey.Equals(_selectedColorKey, StringComparison.OrdinalIgnoreCase);
                    ApplyColorButtonSelectionState(btn, isSelected, accentBrush, transparentBrush);
                }
            }
        }

        if (CustomColorPreviewBtn != null && CustomColorPreviewBtn.Visibility == Visibility.Visible)
        {
            bool isCustomSelected = CustomColorPreviewBtn.Tag is string customTag && customTag.Equals(_selectedColorKey, StringComparison.OrdinalIgnoreCase);
            ApplyColorButtonSelectionState(CustomColorPreviewBtn, isCustomSelected, accentBrush, transparentBrush);
        }
    }

    private static void ApplyColorButtonSelectionState(Button button, bool isSelected, Brush? accentBrush, Brush transparentBrush)
    {
        button.BorderBrush = isSelected && accentBrush is not null ? accentBrush : transparentBrush;

        if (isSelected && accentBrush is not null)
        {
            // Keep the selection outline through the standard Button pointer-over and pressed states.
            button.Resources["ButtonBorderBrushPointerOver"] = accentBrush;
            button.Resources["ButtonBorderBrushPressed"] = accentBrush;
        }
        else
        {
            button.Resources.Remove("ButtonBorderBrushPointerOver");
            button.Resources.Remove("ButtonBorderBrushPressed");
        }
    }

    private void AddTagFromInput()
    {
        var text = UnityHubProjectService.NormalizeProjectTags([NewTagTextBox.Text ?? string.Empty])
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Save color preference for tag
            Helpers.TagColorHelper.SetTagColor(text, _selectedColorKey);

            // 1. Add to AvailableTags if missing
            var existingAvailable = AvailableTags.FirstOrDefault(t => t.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (existingAvailable is null)
            {
                AvailableTags.Add(new TagChipItem(text));
            }

            // 2. Select tag for current project
            AddTagToSelected(text);
            NewTagTextBox.Text = string.Empty;
        }
    }

    private void AddTagToSelected(string tag)
    {
        var normalizedTag = UnityHubProjectService.NormalizeProjectTags([tag]).FirstOrDefault();
        if (!string.IsNullOrEmpty(normalizedTag) && !SelectedTagsAny(normalizedTag))
        {
            SelectedTags.Add(new TagChipItem(normalizedTag));
            UpdateUIState();
        }
    }

    private void RemoveTagFromSelected(string tag)
    {
        var existing = SelectedTags.FirstOrDefault(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedTags.Remove(existing);
            UpdateUIState();
        }
    }

    private void OnRemoveTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            RemoveTagFromSelected(tag);
        }
    }

    private void OnClearAllTagsClick(object sender, RoutedEventArgs e)
    {
        SelectedTags.Clear();
        UpdateUIState();
    }

    private void OnTagChipToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleBtn && toggleBtn.Tag is string tag)
        {
            if (toggleBtn.IsChecked == true)
            {
                AddTagToSelected(tag);
            }
            else
            {
                RemoveTagFromSelected(tag);
            }
            UpdateChipIconTheme(toggleBtn);
        }
    }

    private void OnDeleteAvailableTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (SelectedTagsAny(tag))
            {
                ShowDialogStatus($"'{tag}' is currently assigned to this project. Deselect it under Active Tags before removing this category.", InfoBarSeverity.Warning);
                return;
            }

            var existingAvailable = AvailableTags.FirstOrDefault(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (existingAvailable is not null)
            {
                AvailableTags.Remove(existingAvailable);
            }
        }
    }

    private void OnResetCategoriesClick(object sender, RoutedEventArgs e)
    {
        var defaultPresets = new[] { "Game", "Client Project", "Prototype", "Personal", "Simulation", "Archived", "Visualization", "Work in Progress", "2D", "3D" };
        var customActiveTags = SelectedTags.Where(t => !defaultPresets.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        AvailableTags.Clear();
        var selectedNames = SelectedTags.Select(t => t.Name);
        var tagsToRestore = defaultPresets.Concat(selectedNames).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tagsToRestore)
        {
            AvailableTags.Add(new TagChipItem(tag));
        }

        if (customActiveTags.Count > 0)
        {
            ShowDialogStatus($"Categories reset to defaults. Preserved {customActiveTags.Count} active tag(s). Deselect them under Active Tags to fully remove.", InfoBarSeverity.Warning);
        }
        else
        {
            ShowDialogStatus("Categories reset to default presets.", InfoBarSeverity.Success);
        }

        UpdateUIState();
    }

    private DispatcherTimer? _infoBarTimer;

    private void ShowDialogStatus(string message, InfoBarSeverity severity, int autoHideSeconds = 4)
    {
        if (DialogInfoBar is not null)
        {
            _infoBarTimer?.Stop();
            DialogInfoBar.Message = message;
            DialogInfoBar.Severity = severity;
            DialogInfoBar.IsOpen = true;

            if (autoHideSeconds > 0)
            {
                _infoBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoHideSeconds) };
                _infoBarTimer.Tick += (s, e) =>
                {
                    _infoBarTimer?.Stop();
                    if (DialogInfoBar is not null)
                    {
                        DialogInfoBar.IsOpen = false;
                    }
                };
                _infoBarTimer.Start();
            }
        }
    }

    private string? _targetTagNameToRename;

    private void OnRenameTagMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ToggleButton toggleBtn } || toggleBtn.Tag is not string tagName)
        {
            return;
        }

        _targetTagNameToRename = tagName;
        RenameTagTextBox.Text = tagName;

        DispatcherQueue.TryEnqueue(() => RenameTagFlyout.ShowAt(toggleBtn));
    }

    private void OnRenameTagFlyoutOpened(object sender, object e)
    {
        RenameTagTextBox.Focus(FocusState.Programmatic);
        RenameTagTextBox.SelectAll();
    }

    private void OnRenameTagKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SaveTagRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _targetTagNameToRename = null;
            RenameTagFlyout.Hide();
            e.Handled = true;
        }
    }

    private void OnSaveRenameTagClick(object sender, RoutedEventArgs e)
    {
        SaveTagRename();
    }

    private void OnCancelRenameTagClick(object sender, RoutedEventArgs e)
    {
        _targetTagNameToRename = null;
        RenameTagFlyout.Hide();
    }

    private void SaveTagRename()
    {
        if (string.IsNullOrEmpty(_targetTagNameToRename))
        {
            RenameTagFlyout.Hide();
            return;
        }

        var oldName = _targetTagNameToRename;
        var newName = UnityHubProjectService.NormalizeProjectTags([RenameTagTextBox.Text ?? string.Empty])
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            _targetTagNameToRename = null;
            RenameTagFlyout.Hide();
            return;
        }

        // Migrate tag color setting from oldName to newName
        var existingColor = Helpers.TagColorHelper.GetColorForTag(oldName);
        var hexColor = $"#{existingColor.R:X2}{existingColor.G:X2}{existingColor.B:X2}";
        Helpers.TagColorHelper.SetTagColor(newName, hexColor);

        // Update AvailableTags
        for (int i = 0; i < AvailableTags.Count; i++)
        {
            if (AvailableTags[i].Name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                AvailableTags[i] = new TagChipItem(newName);
                break;
            }
        }

        // Update SelectedTags
        for (int i = 0; i < SelectedTags.Count; i++)
        {
            if (SelectedTags[i].Name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                SelectedTags[i] = new TagChipItem(newName);
                break;
            }
        }

        _targetTagNameToRename = null;
        RenameTagFlyout.Hide();
        UpdateUIState();
        ShowDialogStatus($"Renamed tag '{oldName}' to '{newName}'.", InfoBarSeverity.Success);
    }

    private void OnTagToggleButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleBtn && toggleBtn.Tag is string tag)
        {
            toggleBtn.IsChecked = SelectedTagsAny(tag);
            UpdateChipIconTheme(toggleBtn);

            var flyout = new MenuFlyout();

            var renameItem = new MenuFlyoutItem
            {
                Text = "Rename tag",
                Icon = new FontIcon { Glyph = "\uE70F" },
                Tag = toggleBtn
            };
            renameItem.Click += OnRenameTagMenuClick;

            var editColorItem = new MenuFlyoutItem
            {
                Text = "Edit color",
                Icon = new FontIcon { Glyph = "\uE790" },
                Tag = toggleBtn
            };
            editColorItem.Click += OnEditAvailableTagColorClick;

            var deleteItem = new MenuFlyoutItem
            {
                Text = "Delete category",
                Icon = new FontIcon { Glyph = "\uE74D" },
                Tag = tag
            };
            deleteItem.Click += (s, args) => OnDeleteAvailableTagClick(s!, args);

            flyout.Items.Add(renameItem);
            flyout.Items.Add(editColorItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deleteItem);

            toggleBtn.ContextFlyout = flyout;
        }
    }

    private void OnActiveTagBorderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string tag)
        {
            var flyout = new MenuFlyout();

            var removeItem = new MenuFlyoutItem
            {
                Text = "Clear tag",
                Icon = new FontIcon { Glyph = "\uE711" },
                Tag = tag
            };
            removeItem.Click += (s, args) => RemoveTagFromSelected(tag);

            var removeAllItem = new MenuFlyoutItem
            {
                Text = "Clear all",
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            removeAllItem.Click += OnClearAllTagsClick;

            flyout.Items.Add(removeItem);
            flyout.Items.Add(removeAllItem);

            element.ContextFlyout = flyout;
        }
    }

    private void UpdateUIState()
    {
        bool hasTags = SelectedTags.Count > 0;
        EmptyTagsTextBlock.Visibility = hasTags ? Visibility.Collapsed : Visibility.Visible;
        ClearAllTagsButton.IsEnabled = hasTags;

        // Synchronize realized containers; newly created ones are handled by Loaded.
        for (int i = 0; i < AvailableTagsItemsControl.Items.Count; i++)
        {
            if (AvailableTagsItemsControl.ContainerFromIndex(i) is ContentPresenter container)
            {
                SyncToggleInContainer(container);
            }
        }
    }

    private void SyncToggleInContainer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ToggleButton tb && tb.Tag is string tag)
            {
                tb.IsChecked = SelectedTagsAny(tag);
                UpdateChipIconTheme(tb);
                return;
            }
            SyncToggleInContainer(child);
        }
    }

    private static void UpdateChipIconTheme(ToggleButton toggleBtn)
    {
        bool isChecked = toggleBtn.IsChecked == true;
        var brushKey = isChecked ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(brushKey, out var brushObj) && brushObj is Brush brush)
        {
            if (FindVisualChild<FontIcon>(toggleBtn) is FontIcon icon)
            {
                icon.Foreground = brush;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var childOfChild = FindVisualChild<T>(child);
            if (childOfChild is not null)
                return childOfChild;
        }
        return null;
    }

}
