using System.Runtime.InteropServices;
using Clip.Core;
using Windows.Storage;
using WinDataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using WinDataRequestedEventArgs = Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs;
using WinDataTransferManager = Windows.ApplicationModel.DataTransfer.DataTransferManager;

namespace Clip.Shell;

internal static class WindowsShareService
{
    private static readonly Guid DataTransferManagerId = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static bool IsSupported() => WinDataTransferManager.IsSupported();

    public static void ShowShareUI(
        IntPtr hwnd,
        ClipboardHistoryItem item,
        ClipboardSharePayload payload,
        string title,
        string description,
        Action<Exception> onDataFailed)
    {
        var interop = WinDataTransferManager.As<IDataTransferManagerInterop>();
        var result = interop.GetForWindow(hwnd, DataTransferManagerId);
        var manager = WinRT.MarshalInterface<WinDataTransferManager>.FromAbi(result);

        Windows.Foundation.TypedEventHandler<WinDataTransferManager, WinDataRequestedEventArgs>? handler = null;
        handler = async (_, args) =>
        {
            if (handler is not null)
            {
                manager.DataRequested -= handler;
            }

            var deferral = args.Request.GetDeferral();
            try
            {
                var data = args.Request.Data;
                data.Properties.Title = title;
                data.Properties.Description = description;
                data.RequestedOperation = WinDataPackageOperation.Copy;
                if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
                {
                    data.SetText(item.Text ?? item.Preview ?? string.Empty);
                }

                var files = new List<StorageFile>();
                foreach (var path in payload.FilePaths)
                {
                    files.Add(await StorageFile.GetFileFromPathAsync(path));
                }

                data.SetStorageItems(files);
                data.ShareCompleted += (_, _) => payload.Cleanup();
                data.ShareCanceled += (_, _) => payload.Cleanup();
            }
            catch (Exception ex)
            {
                payload.Cleanup();
                onDataFailed(ex);
                args.Request.FailWithDisplayText("Clip could not prepare this item for sharing.");
            }
            finally
            {
                deferral.Complete();
            }
        };

        manager.DataRequested += handler;
        interop.ShowShareUIForWindow(hwnd);
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
