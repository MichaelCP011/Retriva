using System.IO;
using System.Runtime.InteropServices;

namespace Retriva.Setup;

/// <summary>
/// Pintasan Start Menu, dibuat lewat COM IShellLink.
///
/// TANPA PUSTAKA PIHAK KETIGA, dan itu disengaja.
///
/// Cara yang lebih pendek adalah menumpang Windows Script Host lewat
/// COM "WScript.Shell". Ia bekerja di hampir semua mesin, dan gagal di mesin
/// yang justru paling mungkin ada di lab: Windows Script Host dapat dimatikan
/// lewat Group Policy sebagai langkah pengerasan, dan banyak kebijakan sekolah
/// memang mematikannya. Kegagalannya berupa pengecualian COM yang tidak
/// menyebut Group Policy sama sekali.
///
/// IShellLink adalah antarmuka yang dipakai Explorer sendiri. Ia tidak dapat
/// dimatikan tanpa merusak Windows.
/// </summary>
public static class Shortcut
{
    public static void Create(string linkPath, string targetPath, string workingDir,
                              string description, string? arguments = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetPath);
        link.SetWorkingDirectory(workingDir);
        link.SetDescription(description);
        link.SetIconLocation(targetPath, 0);

        if (!string.IsNullOrWhiteSpace(arguments)) link.SetArguments(arguments);

        ((IPersistFile)link).Save(linkPath, fRemember: true);
        Marshal.FinalReleaseComObject(link);
    }

    public static void Delete(string linkPath)
    {
        if (File.Exists(linkPath)) File.Delete(linkPath);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
                     int maxPath, nint findData, int flags);
        void GetIDList(out nint pidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon,
                             int maxPath, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);
        void Resolve(nint hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder fileName);
    }
}

/// <summary>
/// Menjadwalkan penghapusan berkas saat PC dinyalakan ulang.
///
/// Dipakai untuk satu hal saja: installer yang menghapus dirinya sendiri.
/// Windows tidak mengizinkan exe yang sedang berjalan dihapus, jadi uninstall
/// selalu menyisakan satu berkas — dan folder yang tersisa berisi satu exe
/// terlihat seperti uninstall yang gagal.
/// </summary>
public static class PendingDelete
{
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string? destination, int flags);

    /// <summary>Destination null berarti hapus, bukan pindahkan.</summary>
    public static void Schedule(string path) =>
        MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
}
