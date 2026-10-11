using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Retriva.Agent.Session;

/// <summary>
/// Peluncur RetrivaClient dari layanan Windows (SYSTEM / Session 0) ke desktop interaktif pengguna.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClientLauncher
{
    private static DateTime _lastLaunchAttempt = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Mencari letak berkas eksekusi RetrivaClient.exe.
    /// </summary>
    public static string? FindClientExe()
    {
        var custom = Environment.GetEnvironmentVariable("RETRIVA_CLIENT_EXE");
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

        var baseDir = AppContext.BaseDirectory;

        // Struktur produksi: Program Files\Retriva\Client\RetrivaClient.exe
        var inSubdir = Path.Combine(baseDir, "Client", "RetrivaClient.exe");
        if (File.Exists(inSubdir)) return inSubdir;

        // Bersebelahan dengan exe agent
        var adjacent = Path.Combine(baseDir, "RetrivaClient.exe");
        if (File.Exists(adjacent)) return adjacent;

        // Dari registry Run key mesin
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            if (key?.GetValue("RetrivaClient") is string runCmd)
            {
                var clean = runCmd.Trim().Trim('"');
                if (File.Exists(clean)) return clean;
            }
        }
        catch { /* abaikan kegagalan baca registry */ }

        // Struktur pengembangan
        var devPath = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Retriva.Client\bin\Debug\net10.0-windows10.0.19041.0\RetrivaClient.exe"));
        if (File.Exists(devPath)) return devPath;

        return null;
    }

    /// <summary>
    /// Apakah proses RetrivaClient sedang berjalan di sistem.
    /// </summary>
    public static bool IsClientProcessRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("RetrivaClient");
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Meluncurkan RetrivaClient ke sesi pengguna aktif. Memiliki cooldown agar tidak loop-spam.
    /// </summary>
    public static bool EnsureRunning(ILogger log, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastLaunchAttempt < Cooldown)) return false;
        _lastLaunchAttempt = now;

        if (IsClientProcessRunning()) return true;

        var exePath = FindClientExe();
        if (exePath is null)
        {
            log.LogWarning("[watchdog] RetrivaClient.exe tidak ditemukan di jalur instalasi");
            return false;
        }

        try
        {
            // Jika berjalan di mode interaktif / pengembangan konsol
            if (Environment.UserInteractive)
            {
                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                });
                log.LogInformation("[watchdog] RetrivaClient dijalankan di sesi pengembangan: {Path}", exePath);
                return true;
            }

            // Jika berjalan sebagai layanan Windows (SYSTEM)
            var activeSessionId = WTSGetActiveConsoleSessionId();
            if (activeSessionId == 0xFFFFFFFF || activeSessionId == 0)
            {
                log.LogDebug("[watchdog] Belum ada sesi pengguna aktif di konsol (sesi: {Session})", activeSessionId);
                return false;
            }

            if (!WTSQueryUserToken(activeSessionId, out var userToken))
            {
                log.LogDebug("[watchdog] Gagal mengambil token pengguna sesi {Session} (Win32 {Err})",
                    activeSessionId, Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                if (!DuplicateTokenEx(userToken, 0x10000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero,
                    2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out var primaryToken))
                {
                    log.LogWarning("[watchdog] Gagal duplikasi token pengguna (Win32 {Err})", Marshal.GetLastWin32Error());
                    return false;
                }

                try
                {
                    IntPtr envBlock = IntPtr.Zero;
                    _ = CreateEnvironmentBlock(out envBlock, userToken, false);

                    var si = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        lpDesktop = @"winsta0\default",
                    };

                    const uint creationFlags = 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */ | 0x00000010 /* CREATE_NEW_CONSOLE */;

                    var ok = CreateProcessAsUser(
                        primaryToken,
                        exePath,
                        null,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        creationFlags,
                        envBlock,
                        Path.GetDirectoryName(exePath),
                        ref si,
                        out var pi);

                    if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);

                    if (ok)
                    {
                        CloseHandle(pi.hProcess);
                        CloseHandle(pi.hThread);
                        log.LogInformation("[watchdog] RetrivaClient berhasil diluncurkan ke sesi {Session} (PID: {Pid})",
                            activeSessionId, pi.dwProcessId);
                        return true;
                    }
                    else
                    {
                        log.LogWarning("[watchdog] CreateProcessAsUser gagal (Win32 {Err})", Marshal.GetLastWin32Error());
                        return false;
                    }
                }
                finally
                {
                    CloseHandle(primaryToken);
                }
            }
            finally
            {
                CloseHandle(userToken);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("[watchdog] Pengecualian saat meluncurkan RetrivaClient: {Message}", ex.Message);
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
