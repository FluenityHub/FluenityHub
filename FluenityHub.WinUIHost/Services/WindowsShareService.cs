using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace FluenityHub_WinUIHost.Services;

/// <summary>
/// Opens the Windows Share Sheet for the app's WinUI desktop window.
/// </summary>
public sealed class WindowsShareService
{
    private static readonly Guid DataTransferManagerId =
        new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    private IDataTransferManagerInterop? _interop;
    private DataTransferManager? _dataTransferManager;
    private IntPtr _windowHandle;
    private PendingShare? _pendingShare;

    public void ShowLink(IntPtr windowHandle, string title, string description, Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        EnsureInitialized(windowHandle);

        _pendingShare = new PendingShare(title, description, link);
        _interop!.ShowShareUIForWindow(windowHandle);
    }

    private void EnsureInitialized(IntPtr windowHandle)
    {
        if (_dataTransferManager is not null && _windowHandle == windowHandle)
        {
            return;
        }

        if (_dataTransferManager is not null)
        {
            _dataTransferManager.DataRequested -= OnDataRequested;
        }

        _interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var interfaceId = DataTransferManagerId;
        var managerPointer = _interop.GetForWindow(windowHandle, ref interfaceId);
        _dataTransferManager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(managerPointer);
        _dataTransferManager.DataRequested += OnDataRequested;
        _windowHandle = windowHandle;
    }

    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var pendingShare = _pendingShare;
        _pendingShare = null;
        if (pendingShare is null)
        {
            args.Request.FailWithDisplayText("The project link is no longer available.");
            return;
        }

        var data = args.Request.Data;
        data.Properties.Title = pendingShare.Title;
        data.Properties.Description = pendingShare.Description;
        data.SetWebLink(pendingShare.Link);
        data.SetText(pendingShare.Link.AbsoluteUri);
        data.RequestedOperation = DataPackageOperation.Copy;
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid interfaceId);

        void ShowShareUIForWindow([In] IntPtr appWindow);
    }

    private sealed record PendingShare(string Title, string Description, Uri Link);
}
