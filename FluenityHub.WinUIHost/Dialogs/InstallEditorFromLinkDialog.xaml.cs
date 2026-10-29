using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class InstallEditorFromLinkDialog : ContentDialog
{
    private readonly CancellationTokenSource _cancellation = new();
    public UnityEditorRelease? SelectedRelease { get; private set; }

    public InstallEditorFromLinkDialog()
    {
        InitializeComponent();
        Closing += (_, _) => _cancellation.Cancel();
        Closed += (_, _) => { _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    private async void OnContinue(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = LinkBox.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsActive = true;
        StatusBar.IsOpen = false;
        try
        {
            SelectedRelease = await UnityEditorInstallLinkService.ResolveAsync(LinkBox.Text, _cancellation.Token);
            _cancellation.Token.ThrowIfCancellationRequested();
            args.Cancel = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusBar.Message = ex.Message; StatusBar.IsOpen = true; }
        finally
        {
            Progress.IsActive = false;
            Progress.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = LinkBox.IsEnabled = true;
            deferral.Complete();
        }
    }
}
