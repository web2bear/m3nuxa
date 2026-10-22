using System.Runtime.InteropServices;

namespace ContexWin.Editor;

internal sealed class IconFilePicker(string baseDirectory)
{
    private string SettingsPath => Path.Combine(baseDirectory, "icon-picker-folder.txt");
    internal string StartFolder
    {
        get
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var saved = Path.GetFullPath(File.ReadAllText(SettingsPath).Trim(), baseDirectory);
                    if (Directory.Exists(saved)) return saved;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { ContexWin.App.DevLog.Write("icons", "Could not read last picker folder", ex); }
            return Path.Combine(baseDirectory, "icon-packs", "Fluent");
        }
    }

    internal void RememberSelection(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var relative = Path.GetRelativePath(baseDirectory, folder);
        File.WriteAllText(SettingsPath, relative.StartsWith("..") || Path.IsPathRooted(relative) ? folder : relative);
    }

    internal string? Pick(IntPtr owner)
    {
        var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"))!)!;
        IShellItem? folder = null;
        IShellItem? result = null;
        try
        {
            dialog.SetOptions(0x40 | 0x800 | 0x1000 | 0x8); // File system, existing path/file, preserve working directory.
            dialog.SetFileTypes(1, [new Filter { Name = "Иконки и изображения", Pattern = "*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.exe;*.dll" }]);
            dialog.SetTitle("Выбор иконки");
            var iid = typeof(IShellItem).GUID;
            if (Directory.Exists(StartFolder))
            {
                SHCreateItemFromParsingName(StartFolder, IntPtr.Zero, ref iid, out folder);
                dialog.SetFolder(folder);
            }
            var status = dialog.Show(owner);
            if (status == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(status);
            dialog.GetResult(out result);
            result.GetDisplayName(0x80058000, out var name);
            string path;
            try { path = Marshal.PtrToStringUni(name)!; }
            finally { Marshal.FreeCoTaskMem(name); }
            RememberSelection(path);
            return path;
        }
        finally
        {
            if (result is not null) Marshal.FinalReleaseComObject(result);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Filter
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern;
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Filter[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
}

