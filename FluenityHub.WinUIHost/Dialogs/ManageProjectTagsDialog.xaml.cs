using System.Collections.ObjectModel;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;

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

    private ObservableCollection<TagChipItem>? _dragSource;
    private TagChipItem? _draggedTag;
    private FrameworkElement? _draggedElement;
    private SoftwareBitmap? _dragPreviewBitmap;

    public ManageProjectTagsDialog(
        UnityProjectInfo project,
        IEnumerable<string>? existingGlobalTags = null,
        IEnumerable<string>? preferredCategoryOrder = null)
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

        var distinctSeedTags = seedTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var orderedCategories = (preferredCategoryOrder ?? [])
            .Concat(distinctSeedTags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in orderedCategories)
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

    private readonly record struct TagDropPlacement(int Index, double X, double Y, double Height);

    private async void OnAvailableTagDragStarting(UIElement sender, DragStartingEventArgs args)
        => await BeginTagDragAsync((FrameworkElement)sender, args, AvailableTags);

    private async void OnActiveTagDragStarting(UIElement sender, DragStartingEventArgs args)
        => await BeginTagDragAsync((FrameworkElement)sender, args, SelectedTags);

    private void OnAvailableTagsDragOver(object sender, DragEventArgs args)
        => UpdateTagDropIndicator(
            AvailableTagsItemsControl,
            AvailableTagDropIndicator,
            AvailableTags,
            args);

    private void OnActiveTagsDragOver(object sender, DragEventArgs args)
        => UpdateTagDropIndicator(
            ActiveTagsItemsControl,
            ActiveTagDropIndicator,
            SelectedTags,
            args);

    private void OnAvailableTagsDrop(object sender, DragEventArgs args)
        => CompleteTagReorder(AvailableTagsItemsControl, AvailableTags, args);

    private void OnActiveTagsDrop(object sender, DragEventArgs args)
        => CompleteTagReorder(ActiveTagsItemsControl, SelectedTags, args);

    private void OnTagCollectionDragLeave(object sender, DragEventArgs args)
        => HideTagDropIndicators();

    private void OnTagDropCompleted(UIElement sender, DropCompletedEventArgs args)
        => ResetTagDragState();

    private async Task BeginTagDragAsync(
        FrameworkElement sender,
        DragStartingEventArgs args,
        ObservableCollection<TagChipItem> source)
    {
        if (sender.Tag is not string tagName)
        {
            args.Cancel = true;
            return;
        }

        var item = source.FirstOrDefault(tag =>
            tag.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            args.Cancel = true;
            return;
        }

        _dragSource = source;
        _draggedTag = item;
        _draggedElement = sender;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText(item.Name);

        var deferral = args.GetDeferral();
        try
        {
            var preview = new RenderTargetBitmap();
            await preview.RenderAsync(sender);
            var pixels = await preview.GetPixelsAsync();
            if (preview.PixelWidth > 0 && preview.PixelHeight > 0 && pixels.Length > 0)
            {
                _dragPreviewBitmap?.Dispose();
                _dragPreviewBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    pixels,
                    BitmapPixelFormat.Bgra8,
                    preview.PixelWidth,
                    preview.PixelHeight,
                    BitmapAlphaMode.Premultiplied);
                args.DragUI.SetContentFromSoftwareBitmap(
                    _dragPreviewBitmap,
                    new Point(preview.PixelWidth / 2d, preview.PixelHeight));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to create the custom tag drag preview: {ex}");
        }
        finally
        {
            _draggedElement.Opacity = 0.45;
            HideTagDropIndicators();
            deferral.Complete();
        }
    }

    private void UpdateTagDropIndicator(
        ItemsControl itemsControl,
        FrameworkElement indicator,
        ObservableCollection<TagChipItem> target,
        DragEventArgs args)
    {
        if (!ReferenceEquals(_dragSource, target) || _draggedTag is null)
        {
            return;
        }

        args.DragUIOverride.IsCaptionVisible = false;
        args.DragUIOverride.IsGlyphVisible = false;

        var placement = GetTagDropPlacement(itemsControl, args.GetPosition(itemsControl));
        Canvas.SetLeft(indicator, Math.Max(0, placement.X - (indicator.Width / 2)));
        Canvas.SetTop(indicator, placement.Y);
        indicator.Height = placement.Height;
        indicator.Visibility = Visibility.Visible;

        args.AcceptedOperation = DataPackageOperation.Move;
        args.Handled = true;
    }

    private void CompleteTagReorder(
        ItemsControl itemsControl,
        ObservableCollection<TagChipItem> target,
        DragEventArgs args)
    {
        try
        {
            if (!ReferenceEquals(_dragSource, target) || _draggedTag is null)
            {
                return;
            }

            var oldIndex = target.IndexOf(_draggedTag);
            if (oldIndex < 0)
            {
                return;
            }

            var placement = GetTagDropPlacement(itemsControl, args.GetPosition(itemsControl));
            var insertionIndex = placement.Index;
            if (insertionIndex > oldIndex)
            {
                insertionIndex--;
            }

            var newIndex = Math.Clamp(insertionIndex, 0, target.Count - 1);
            if (newIndex != oldIndex)
            {
                target.Move(oldIndex, newIndex);
                UpdateUIState();
            }

            args.AcceptedOperation = DataPackageOperation.Move;
            args.Handled = true;
        }
        finally
        {
            ResetTagDragState();
        }
    }

    private static TagDropPlacement GetTagDropPlacement(ItemsControl itemsControl, Point pointer)
    {
        FrameworkElement? lastContainer = null;
        Point lastOrigin = default;

        for (var index = 0; index < itemsControl.Items.Count; index++)
        {
            if (itemsControl.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            var origin = container.TransformToVisual(itemsControl).TransformPoint(new Point());
            var bottom = origin.Y + container.ActualHeight;
            var horizontalMidpoint = origin.X + (container.ActualWidth / 2);
            if (pointer.Y < origin.Y
                || (pointer.Y <= bottom && pointer.X < horizontalMidpoint))
            {
                return new TagDropPlacement(index, origin.X, origin.Y, container.ActualHeight);
            }

            lastContainer = container;
            lastOrigin = origin;
        }

        return lastContainer is null
            ? new TagDropPlacement(0, 0, 0, 28)
            : new TagDropPlacement(
                itemsControl.Items.Count,
                lastOrigin.X + lastContainer.ActualWidth + 3,
                lastOrigin.Y,
                lastContainer.ActualHeight);
    }

    private void ResetTagDragState()
    {
        HideTagDropIndicators();
        if (_draggedElement is not null)
        {
            _draggedElement.Opacity = 1;
        }

        _dragSource = null;
        _draggedTag = null;
        _draggedElement = null;
        _dragPreviewBitmap?.Dispose();
        _dragPreviewBitmap = null;
    }

    private void HideTagDropIndicators()
    {
        AvailableTagDropIndicator.Visibility = Visibility.Collapsed;
        ActiveTagDropIndicator.Visibility = Visibility.Collapsed;
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
