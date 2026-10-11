using System.IO;
using Microsoft.Extensions.Logging;

namespace Retriva.Shared;

/// <summary>
/// Menulis log ke berkas.
///
/// ADA KARENA SELAMA INI LOG PANEL TIDAK PERNAH BISA DIBACA SIAPA PUN.
///
/// Retriva.Admin adalah WinExe. Windows tidak memberinya konsol, jadi setiap
/// baris yang ditulis ILogger maupun Console.WriteLine mengalir ke tempat yang
/// tidak ada. Agent punya berkas log dan karena itu setiap kali ada masalah,
/// yang tersedia untuk diperiksa hanyalah sisi agent — sisi yang paling jarang
/// menjadi penyebabnya.
///
/// Akibatnya nyata dan mahal: diagnosis berjam-jam berdasarkan tebakan, karena
/// separuh sistem berjalan tanpa satu pun jejak yang dapat dibaca. Sertifikat
/// tanpa kunci privat, relay yang menolak datagram, panel yang ditolak — semua
/// tercatat rapi, dan tidak satu pun sampai ke mata orang.
///
/// Sengaja sederhana: satu berkas, ditimpa tiap kali aplikasi dibuka, tanpa
/// rotasi. Log yang menumpuk lintas sesi membuat orang membaca kejadian kemarin
/// dan menyimpulkan hal yang salah tentang hari ini.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public string Path { get; }

    public FileLoggerProvider(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // FileShare.ReadWrite supaya berkasnya dapat dibuka Notepad selagi
        // aplikasi berjalan. Log yang hanya dapat dibaca setelah aplikasinya
        // ditutup tidak menolong saat yang ingin dilihat justru sedang terjadi.
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    private void Write(string line)
    {
        lock (_gate)
        {
            try { _writer.WriteLine(line); }
            catch (IOException) { /* Disk penuh tidak boleh menjatuhkan lab. */ }
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    private sealed class Writer(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;

            // Nama kategori dipangkas ke bagian terakhirnya. "Retriva.Server
            // .BroadcastRelay" menjadi "BroadcastRelay" — yang panjang membuat
            // pesannya terdorong ke kanan sampai sulit dipindai dengan mata.
            var shortName = category[(category.LastIndexOf('.') + 1)..];

            owner.Write($"{DateTime.Now:HH:mm:ss.fff} {Level(level)} {shortName,-18} "
                      + formatter(state, error));

            if (error is not null) owner.Write("    " + error);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Critical => "KRIT",
            LogLevel.Error => "GALAT",
            LogLevel.Warning => "PERINGATAN",
            _ => "info",
        };
    }
}
