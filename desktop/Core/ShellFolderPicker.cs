using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AichanToolbox.Core;

internal static class ShellFolderPicker
{
    private const int CancelledHResult = unchecked((int)0x800704C7);

    public static string? Show(Window owner, string title, string? initialDirectory)
    {
        IFileOpenDialog? dialog = null;
        IShellItem? initialItem = null;
        IShellItem? result = null;
        try
        {
            dialog = (IFileOpenDialog)(object)new FileOpenDialogComObject();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options |
                FileOpenOptions.PickFolders |
                FileOpenOptions.ForceFileSystem |
                FileOpenOptions.PathMustExist |
                FileOpenOptions.NoChangeDirectory);
            dialog.SetTitle(title);

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                var itemId = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref itemId, out initialItem);
                dialog.SetFolder(initialItem);
            }

            var resultCode = dialog.Show(new WindowInteropHelper(owner).Handle);
            if (resultCode == CancelledHResult) return null;
            Marshal.ThrowExceptionForHR(resultCode);

            dialog.GetResult(out result);
            result.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var pathPointer);
            try
            {
                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
        finally
        {
            ReleaseComObject(result);
            ReleaseComObject(initialItem);
            ReleaseComObject(dialog);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private sealed class FileOpenDialogComObject
    {
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr filterSpecifications);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(FileOpenOptions options);
        void GetOptions(out FileOpenOptions options);
        void SetDefaultFolder(IShellItem shellItem);
        void SetFolder(IShellItem shellItem);
        void GetFolder(out IShellItem shellItem);
        void GetCurrentSelection(out IShellItem shellItem);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem shellItem);
        void AddPlace(IShellItem shellItem, int alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr shellItems);
        void GetSelectedItems(out IntPtr shellItems);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindingContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem shellItem, uint hint, out int order);
    }

    [Flags]
    private enum FileOpenOptions : uint
    {
        NoChangeDirectory = 0x00000008,
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800
    }

    private enum ShellItemDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }
}
