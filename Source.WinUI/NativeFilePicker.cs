using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AbioticFactorModManager;

internal static class NativeFilePicker
{
    private const int OfnExplorer = 0x00080000;
    private const int OfnAllowMultiSelect = 0x00000200;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnHideReadOnly = 0x00000004;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnEnableSizing = 0x00800000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);

    [DllImport("comdlg32.dll", EntryPoint = "CommDlgExtendedError")]
    private static extern int CommDlgExtendedError();

    public static string[] PickMultipleZipFiles(IntPtr owner, string initialDirectory)
        => PickFiles(owner, initialDirectory, "ZIP archives (*.zip)\0*.zip\0All files (*.*)\0*.*\0\0", "Select ZIP archives", true, "zip");

    public static string? PickSingleExecutable(IntPtr owner, string initialDirectory)
    {
        var files = PickFiles(owner, initialDirectory, "Executable files (*.exe)\0*.exe\0All files (*.*)\0*.*\0\0", "Select Abiotic Factor executable", false, "exe");
        return files.FirstOrDefault();
    }

    private static string[] PickFiles(IntPtr owner, string initialDirectory, string filter, string title, bool allowMultiple, string defaultExtension)
    {
        const int capacity = 65536;
        IntPtr filterPtr = IntPtr.Zero;
        IntPtr bufferPtr = IntPtr.Zero;
        IntPtr directoryPtr = IntPtr.Zero;
        IntPtr titlePtr = IntPtr.Zero;
        IntPtr extensionPtr = IntPtr.Zero;

        try
        {
            filterPtr = Marshal.StringToHGlobalUni(filter);
            bufferPtr = Marshal.AllocHGlobal(capacity * 2);
            Marshal.Copy(new byte[capacity * 2], 0, bufferPtr, capacity * 2);
            directoryPtr = Directory.Exists(initialDirectory) ? Marshal.StringToHGlobalUni(initialDirectory) : IntPtr.Zero;
            titlePtr = Marshal.StringToHGlobalUni(title);
            extensionPtr = Marshal.StringToHGlobalUni(defaultExtension);

            var dialog = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = bufferPtr,
                nMaxFile = capacity,
                lpstrInitialDir = directoryPtr,
                lpstrTitle = titlePtr,
                lpstrDefExt = extensionPtr,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnHideReadOnly | OfnNoChangeDir | OfnEnableSizing |
                    (allowMultiple ? OfnAllowMultiSelect : 0)
            };

            if (!GetOpenFileName(ref dialog))
            {
                var error = CommDlgExtendedError();
                if (error != 0)
                    throw new InvalidOperationException("The Windows file dialog could not be opened (error " + error + ").");
                return Array.Empty<string>();
            }

            var raw = Marshal.PtrToStringUni(bufferPtr, capacity) ?? "";
            var parts = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<string>();
            if (parts.Length == 1) return new[] { Path.GetFullPath(parts[0]) };

            var directory = parts[0];
            return parts.Skip(1)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Path.GetFullPath(Path.Combine(directory, name)))
                .ToArray();
        }
        finally
        {
            if (filterPtr != IntPtr.Zero) Marshal.FreeHGlobal(filterPtr);
            if (bufferPtr != IntPtr.Zero) Marshal.FreeHGlobal(bufferPtr);
            if (directoryPtr != IntPtr.Zero) Marshal.FreeHGlobal(directoryPtr);
            if (titlePtr != IntPtr.Zero) Marshal.FreeHGlobal(titlePtr);
            if (extensionPtr != IntPtr.Zero) Marshal.FreeHGlobal(extensionPtr);
        }
    }
}