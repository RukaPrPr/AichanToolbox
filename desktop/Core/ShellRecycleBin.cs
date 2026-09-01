using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AichanToolbox.Core;

internal sealed record RecycleFileResult(string Path, bool Recycled, Exception? Error);

internal static class ShellRecycleBin
{
    private sealed record RecycleRequest(
        string[] Paths,
        TaskCompletionSource<IReadOnlyList<RecycleFileResult>> Completion);

    private const uint Silent = 0x0004;
    private const uint NoConfirmation = 0x0010;
    private const uint AllowUndo = 0x0040;
    private const uint NoErrorUi = 0x0400;
    private const uint RecycleOnDelete = 0x00080000;
    private const uint NoCopyHooks = 0x00800000;
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
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var result = DeleteFiles([path]).Single();
        if (!result.Recycled)
            throw new IOException($"无法将文件移入回收站：{result.Error?.Message}", result.Error);
    }

    internal static IReadOnlyList<RecycleFileResult> DeleteFiles(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0) return [];

        var completion = new TaskCompletionSource<IReadOnlyList<RecycleFileResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add(new RecycleRequest(normalized, completion));
        return completion.Task.GetAwaiter().GetResult();
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
                request.Completion.TrySetResult(DeleteFilesWithRetry(request.Paths, DeleteFilesOnce));
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    internal static void DeleteFileWithRetry(string path, Action<string> deleteFile)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var result = DeleteFilesWithRetry([path], pending => deleteFile(pending[0])).Single();
        if (!result.Recycled)
            throw new IOException($"无法将文件移入回收站：{result.Error?.Message}", result.Error);
    }

    internal static IReadOnlyList<RecycleFileResult> DeleteFilesWithRetry(
        IReadOnlyList<string> paths,
        Action<IReadOnlyList<string>> deleteFiles)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lastErrors = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            var pending = normalized.Where(File.Exists).ToArray();
            if (pending.Length == 0) break;

            Exception? batchError = null;
            try
            {
                deleteFiles(pending);
            }
            catch (Exception exception)
            {
                batchError = exception;
            }

            foreach (var path in pending)
            {
                if (!File.Exists(path))
                {
                    lastErrors.Remove(path);
                    continue;
                }

                lastErrors[path] = batchError
                    ?? new IOException("Windows 回收站接口返回成功，但原文件仍然存在。");
            }

            if (attempt + 1 < RetryCount && normalized.Any(File.Exists))
                Thread.Sleep(100 * (attempt + 1));
        }

        return normalized.Select(path =>
        {
            var recycled = !File.Exists(path);
            lastErrors.TryGetValue(path, out var error);
            return new RecycleFileResult(path, recycled, recycled ? null : error);
        }).ToList();
    }

    private static void DeleteFilesOnce(IReadOnlyList<string> paths)
    {
        IFileOperation? operation = null;
        var shellItems = new List<IShellItem>(paths.Count);
        try
        {
            operation = (IFileOperation)new FileOperationComObject();
            ThrowIfFailed(
                operation.SetOperationFlags(Silent | NoConfirmation | AllowUndo | NoErrorUi | RecycleOnDelete | NoCopyHooks),
                "设置回收站操作");

            foreach (var path in paths)
            {
                var shellItemId = ShellItemId;
                ThrowIfFailed(
                    SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out var shellItem),
                    "解析待回收文件路径");
                shellItems.Add(shellItem);
                ThrowIfFailed(operation.DeleteItem(shellItem, IntPtr.Zero), "登记回收文件");
            }

            ThrowIfFailed(operation.PerformOperations(), "执行批量回收文件");
            ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted), "读取批量回收结果");
            if (aborted) throw new IOException("Windows 未能完成全部文件的批量回收操作。");
        }
        finally
        {
            for (var index = shellItems.Count - 1; index >= 0; index--)
                if (Marshal.IsComObject(shellItems[index])) Marshal.FinalReleaseComObject(shellItems[index]);
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
