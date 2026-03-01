using System.Runtime.InteropServices;

namespace AvoTelemetryAgent.UI;

/// <summary>
/// Native COM folder picker using IFileOpenDialog with FOS_PICKFOLDERS.
/// Runs on a dedicated STA thread; waits until the dialog closes (no timeout).
/// </summary>
internal static class NativeFolderPicker
{
    /// <summary>
    /// Opens a native folder-picker dialog and returns the selected path,
    /// or <c>null</c> if the user cancelled. Throws on COM/display errors.
    /// </summary>
    public static string? PickFolder(string title)
    {
        string?    result = null;
        Exception? fault  = null;

        var thread = new Thread(() =>
        {
            try   { result = ShowDialog(title); }
            catch (Exception ex) { fault = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(); // no timeout — wait until dialog is closed

        if (fault is not null) throw fault;
        return result;
    }

    // ── Internal dialog logic ─────────────────────────────────────────────────

    private static string? ShowDialog(string title)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogClass();
        try
        {
            dialog.SetOptions(Fos.PickFolders | Fos.ForceFileSystem);
            dialog.SetTitle(title);

            int hr = dialog.Show(IntPtr.Zero);
            if (hr == HResultCancelled) return null; // user cancelled

            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(Sigdn.FileSystemPath, out string path);
            Marshal.ReleaseComObject(item);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    // ── COM definitions ───────────────────────────────────────────────────────

    // HRESULT_FROM_WIN32(ERROR_CANCELLED) — returned by IFileOpenDialog.Show()
    // when the user dismisses the dialog without selecting a folder.
    private const int HResultCancelled = unchecked((int)0x800704C7);

    [Flags]
    private enum Fos : uint
    {
        PickFolders     = 0x00000020,
        ForceFileSystem = 0x00000040,
    }

    private enum Sigdn : uint
    {
        FileSystemPath = 0x80058000,
    }

    // CLSID_FileOpenDialog
    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass { }

    // IID_IFileOpenDialog — vtable order: IUnknown(0-2) → IModalWindow(3) →
    // IFileDialog(4-26) → IFileOpenDialog(27-28)
    [ComImport,
     Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr hwnd);                               // IModalWindow
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);           // IFileDialog
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(Fos fos);
        void GetOptions(out Fos pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszExt);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);                                // IFileOpenDialog
        void GetSelectedItems(out IntPtr ppsai);
    }

    // IID_IShellItem
    [ComImport,
     Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(Sigdn sigdnName,
                            [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
