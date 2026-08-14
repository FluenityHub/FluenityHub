using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Controls;

/// <summary>
/// A TreeViewItem whose selection checkbox can be disabled independently from
/// the item itself, so the expand/collapse chevron remains available.
/// </summary>
public sealed partial class ModuleTreeViewItem : TreeViewItem
{
    public static readonly DependencyProperty IsSelectionEnabledProperty =
        DependencyProperty.Register(
            nameof(IsSelectionEnabled),
            typeof(bool),
            typeof(ModuleTreeViewItem),
            new PropertyMetadata(true, OnIsSelectionEnabledChanged));

    public bool IsSelectionEnabled
    {
        get => (bool)GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateSelectionCheckBox();
    }

    private static void OnIsSelectionEnabledChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is ModuleTreeViewItem item)
        {
            item.UpdateSelectionCheckBox();
        }
    }

    private void UpdateSelectionCheckBox()
    {
        if (GetTemplateChild("MultiSelectCheckBox") is not CheckBox selectionCheckBox)
        {
            return;
        }

        selectionCheckBox.IsEnabled = IsSelectionEnabled;
        selectionCheckBox.IsTabStop = IsSelectionEnabled;
        AutomationProperties.SetHelpText(
            selectionCheckBox,
            IsSelectionEnabled ? string.Empty : "This module is already installed.");
    }
}
