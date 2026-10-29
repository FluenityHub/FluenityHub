using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class GitAccountsDialog : ContentDialog
{
    private readonly CancellationTokenSource _cancellation = new();
    public GitAccountsDialog()
    {
        InitializeComponent();
        Closed += (_, _) => { _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    private string Provider => (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = SaveButton.IsEnabled = ProviderBox.IsEnabled = ScopeBox.IsEnabled = UserBox.IsEnabled = false;
        try
        {
            var account = await GitCredentialManagerService.GetAsync(Provider, ScopeBox.Text, true, _cancellation.Token, UserBox.Text);
            if (account is null) throw new IOException("Git sign-in was not completed. Check that Git Credential Manager is installed.");
            GitCredentialManagerService.SetAccount(Provider, ScopeBox.Text, account.UserName);
            UserBox.Text = account.UserName;
            ShowStatus($"Signed in as {account.UserName}.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally { SignInButton.IsEnabled = SaveButton.IsEnabled = ProviderBox.IsEnabled = ScopeBox.IsEnabled = UserBox.IsEnabled = true; }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try { GitCredentialManagerService.SetAccount(Provider, ScopeBox.Text, UserBox.Text); ShowStatus("Account choice saved.", InfoBarSeverity.Success); }
        catch (ArgumentException ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private void ShowStatus(string text, InfoBarSeverity severity)
    {
        StatusBar.Message = text; StatusBar.Severity = severity; StatusBar.IsOpen = true;
    }
}
