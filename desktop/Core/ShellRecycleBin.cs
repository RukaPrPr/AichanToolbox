using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AichanToolbox.Core;

internal static class ShellRecycleBin
{
    private sealed record RecycleRequest(string Path, TaskCompletionSource<bool> Completion);

    private const uint Silent = 0x0004;
    private const uint NoConfirmation = 0x0010;
    private const uint AllowUndo = 0x0040;
    private const uint NoErrorUi = 0x0400;
    private const uint RecycleOnDelete = 0x00080000;
    private const int RetryCount = 3;
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly BlockingCollection<RecycleRequest> Requests = new();
    private static readonly Thread Worker = StartWorker();

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr propertyArray);
        [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr progressSink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr progressSink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destinationFolder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IntPtr progressSink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destinationFolder);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr progressSink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IntPtr progressSink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool anyOperationsAborted);
    }

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private class FileOperationComObject
    {
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    internal static ApartmentState WorkerApartmentState => Worker.GetApartmentState();

    public static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add(new RecycleRequest(Path.GetFullPath(path), completion));
        completion.Task.GetAwaiter().GetResult();
    }

    private static Thread StartWorker()
    {
        var worker = new Thread(ProcessRequests)
        {
            IsBackground = true,
            Name = "AichanToolbox.RecycleBin.STA"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return worker;
    }

    private static void ProcessRequests()
    {
        foreach (var request in Requests.GetConsumingEnumerable())
        {
            try
            {
                DeleteFileCore(request.Path);
                request.Completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    private static void DeleteFileCore(string path)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            if (!File.Exists(path)) return;

            try
            {
                DeleteFileOnce(path);
                if (!File.Exists(path)) return;
                lastError = new IOException("Windows 回收站接口返回成功，但原文件仍然存在。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }

            if (attempt + 1 < RetryCount) Thread.Sleep(100 * (attempt + 1));
        }

        throw new IOException($"无法将文件移入回收站：{lastError?.Message}", lastError);
    }

    private static void DeleteFileOnce(string path)
    {
        IFileOperation? operation = null;
        IShellItem? shellItem = null;
        try
        {
            operation = (IFileOperation)new FileOperationComObject();
            ThrowIfFailed(
                operation.SetOperationFlags(Silent | NoConfirmation | AllowUndo | NoErrorUi | RecycleOnDelete),
                "设置回收站操作");

            var shellItemId = ShellItemId;
            ThrowIfFailed(
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out shellItem),
                "解析待回收文件路径");
            ThrowIfFailed(operation.DeleteItem(shellItem, IntPtr.Zero), "登记回收文件");
            ThrowIfFailed(operation.PerformOperations(), "执行回收文件");
            ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted), "读取回收结果");
            if (aborted) throw new OperationCanceledException("文件回收操作已取消。");
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem)) Marshal.FinalReleaseComObject(shellItem);
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static void ThrowIfFailed(int result, string action)
    {
        if (result >= 0) return;
        var code = unchecked((uint)result);
        throw new IOException(
            $"{action}失败（HRESULT 0x{code:X8}）。",
            Marshal.GetExceptionForHR(result));
    }
}
