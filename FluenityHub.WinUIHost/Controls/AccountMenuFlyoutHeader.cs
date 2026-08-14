using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Controls;

/// <summary>
/// Non-command identity content hosted by a native MenuFlyout presenter.
/// </summary>
public sealed partial class AccountMenuFlyoutHeader : MenuFlyoutItem
{
    public static readonly DependencyProperty DisplayNameProperty =
        DependencyProperty.Register(
            nameof(DisplayName),
            typeof(string),
            typeof(AccountMenuFlyoutHeader),
            new PropertyMetadata("Unity ID"));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(AccountMenuFlyoutHeader),
            new PropertyMetadata("Not signed in"));

    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(
            nameof(IsBusy),
            typeof(bool),
            typeof(AccountMenuFlyoutHeader),
            new PropertyMetadata(false));

    public AccountMenuFlyoutHeader()
    {
        IsTabStop = false;
        AllowFocusOnInteraction = false;
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

}
