namespace Retriva.Shared.Protocol;

/// <summary>Peran sebuah komputer di dalam laboratorium.</summary>
public enum UnitRole
{
    /// <summary>PC Asisten — pemegang basis data dan koordinator.</summary>
    Primary,
    /// <summary>PC Dosen — konsol kedua, siaran dikirim langsung ke klien.</summary>
    Secondary,
    /// <summary>PC Mahasiswa.</summary>
    Client,
}

/// <summary>Status sebuah unit sebagaimana terlihat dari konsol.</summary>
public enum UnitStatus
{
    Offline,
    Online,
    Locked,
    /// <summary>Ada pelanggaran test mode yang belum ditangani.</summary>
    Alert,
}

/// <summary>Sumber tampilan fisik yang di-capture untuk siaran.</summary>
public enum DisplaySource
{
    AdminScreen,
    HdmiInput,
    SecondMonitor,
}

/// <summary>Cakupan yang di-capture dari sumber terpilih.</summary>
public enum CaptureScope
{
    FullScreen,
    SingleWindow,
    Application,
}

/// <summary>Hasil eksekusi sebuah perintah di sisi agent.</summary>
public enum CommandOutcome
{
    Success,
    Failed,
    Unsupported,
    Denied,
}
