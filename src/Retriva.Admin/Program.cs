using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Retriva.Shared;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;
using Retriva.Shared.Security;
using Retriva.Server.Files;
using Retriva.Server.Security;
using Retriva.Server.Data;
using Retriva.Server;

namespace Retriva.Admin;

public static class Program
{
    /// <summary>
    /// Memberi tahu Windows bahwa proses ini memahami penskalaan layar.
    ///
    /// Tanpa ini Windows memvirtualkan koordinat: pada penskalaan 125%, layar
    /// 1920x1080 dilaporkan 1536x864 dan persegi jendela ikut dikecilkan. Untuk
    /// aplikasi biasa itu tidak terasa, tetapi kita menangkap piksel — angka
    /// yang salah berarti area tangkapan yang salah dan siaran yang buram.
    /// Harus dipanggil sebelum jendela pertama dibuat.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary>
    /// Berkas log panel, di tempat yang sama dengan log agent.
    ///
    /// Keduanya berdampingan supaya ketika ada yang salah, yang diperiksa
    /// adalah kedua sisi — bukan hanya sisi yang kebetulan punya jendela.
    /// </summary>
    private static string LogPath => RetrivaPaths.LogFile("koordinator");

    /// <summary>
    /// Menangkap pengecualian yang tidak tertangani, dan MENAMPILKANNYA.
    ///
    /// APLIKASI YANG MENUTUP DIRI TANPA PESAN ADALAH BENTUK KEGAGALAN TERBURUK
    /// DARI SUDUT PANDANG ORANG DI DEPANNYA.
    ///
    /// Retriva.Admin adalah WinExe: tidak punya konsol, jadi pengecualian yang
    /// lolos sampai puncak hanya mematikan prosesnya. Tidak ada jendela, tidak
    /// ada pesan, tidak ada berkas yang bertambah. Yang terlihat hanyalah ikon
    /// yang diklik dan tidak terjadi apa-apa — dan tidak ada satu pun petunjuk
    /// yang dapat dilaporkan ke siapa pun.
    ///
    /// Tiga jalur dipasang karena ketiganya berbeda: pengecualian di utas UI,
    /// di utas lain, dan di Task yang tidak pernah diamati. Memasang satu saja
    /// menyisakan dua jalan keluar yang tetap senyap.
    /// </summary>
    private static void CatchEverything(Application app)
    {
        void Report(string where, Exception? ex)
        {
            var detail = ex?.ToString() ?? "(tanpa keterangan)";
            var line = $"=== {where} @ {DateTime.Now:O} ==={Environment.NewLine}{detail}";

            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch (IOException) { /* log terkunci; kotak pesan tetap muncul */ }

            MessageBox.Show(
                $"Retriva berhenti karena kesalahan yang tidak tertangani.{Environment.NewLine}{Environment.NewLine}"
              + $"{ex?.Message}{Environment.NewLine}{Environment.NewLine}"
              + $"Rinciannya ditulis ke:{Environment.NewLine}{LogPath}{Environment.NewLine}{Environment.NewLine}"

                // Cara mengirimkannya disebut DI SINI, bukan diserahkan kepada
                // orang yang baru saja melihat aplikasinya mati. Jalur berkas
                // saja tidak cukup: ia berada di folder yang tidak akan dibuka
                // siapa pun tanpa diberi tahu, dan log panel jarang bercerita
                // lengkap tanpa log agent di sebelahnya.
              + "Untuk melaporkannya: buka Start, ketik \"Retriva\", "
              + "lalu jalankan \"Retriva — Kumpulkan log\". Berkas zip akan muncul "
              + "di Desktop; kirimkan berkas itu.",
                "Retriva", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        app.DispatcherUnhandledException += (_, e) =>
        {
            Report("DispatcherUnhandledException", e.Exception);

            // Ditandai tertangani supaya jendelanya tetap hidup bila memungkinkan.
            // Kegagalan di satu penanganan peristiwa tidak selalu berarti seluruh
            // aplikasi rusak, dan menutupnya menghilangkan kesempatan menyimpan
            // apa pun yang sedang berjalan.
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    [STAThread]
    public static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);

        RetrivaPaths.EnsureDataDir();

        var config = ConfigStore.LoadOrCreate<ConsoleConfig>(
            RetrivaPaths.ConsoleConfigFile,
            problem => MessageBox.Show(problem, "Retriva — konfigurasi",
                MessageBoxButton.OK, MessageBoxImage.Warning));

        var panel = PanelIdentity.LoadOrCreate();

        /*
            Dicetak sebelum apa pun bercabang.

            Peran diambil dari panel.json, dan letak panel.json ditentukan
            RETRIVA_DATA_DIR yang diwariskan diam-diam dari jendela induk. Dua
            hal tak terlihat yang menentukan seluruh sisa hidup proses ini.

            Ketika dua jendela terbuka dan keduanya tampak sama, pertanyaan
            pertama selalu "yang ini yang mana" — dan tanpa baris ini
            jawabannya hanya bisa ditebak.
        */
        Debug.WriteLine($"[retriva] data: {RetrivaPaths.DataDir}");
        Debug.WriteLine($"[retriva] peran: {(panel.IsSuperadmin ? "Superadmin" : "Panel admin")}"
                      + $" · {panel.DisplayName}");
        Console.WriteLine($"Retriva — {(panel.IsSuperadmin ? "SUPERADMIN" : "PANEL ADMIN")}"
                        + $" · data: {RetrivaPaths.DataDir}");

        /*
            SATU PERTANYAAN, DITANYAKAN SEKALI.

            Hanya "apakah mesin ini yang menjalankan lab". Itu pertanyaan
            tentang apa yang dilakukan perangkat lunaknya — membuka port 8443,
            memegang basis data dan sertifikat — dan harus dijawab sebelum
            apa pun dinyalakan.

            Sisanya, termasuk ke lab mana panel ini menyambung dan dengan kode
            apa, TIDAK ditanyakan di sini. Kode pendaftaran berumur sepuluh
            menit; menanyakannya di dialog yang memblokir berarti dosen yang
            membuka aplikasi sebelum asisten sempat membuka pendaftaran berada
            di jalan buntu, dan salah ketik satu karakter hanya bisa diperbaiki
            dengan menutup lalu membuka aplikasinya lagi.

            Penautan pindah ke dalam aplikasi, tempat ia dapat diulang.
        */
        if (!panel.Configured)
        {
            var setup = new SetupWindow(panel);
            if (setup.ShowDialog() != true) return;

            panel.Configured = true;
            ConfigStore.Save(RetrivaPaths.PanelIdentityFile, panel);
        }

        if (!panel.IsSuperadmin)
        {
            RunPanel(panel, config);
            return;
        }

        // Kode pendaftaran agent dibuat sekali lalu dipakai seterusnya oleh
        // installer. Nilai dari console.json versi lama dipindahkan lebih dulu,
        // sebelum diperiksa kosong — kalau tidak, penggantian nama properti akan
        // menerbitkan kode baru dan menolak seluruh agent yang sudah terdaftar.
        var adopted = config.AdoptLegacySecret();

        // Rumus penomoran lama diubah menjadi tabel oktet → nama. Dilakukan di
        // sini, bukan saat layar Konfigurasi dibuka, supaya lab yang tidak
        // pernah membuka layar itu tetap menamai unitnya dengan benar.
        if (config.AdoptLegacyRange()) adopted = true;

        if (string.IsNullOrWhiteSpace(config.AgentEnrollmentSecret))
        {
            config.AgentEnrollmentSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            adopted = true;
        }

        if (adopted) ConfigStore.Save(RetrivaPaths.ConsoleConfigFile, config);

        var certificate = CertificateManager.LoadOrCreate();

        /*
            Kunci privat diperiksa, bukan diandaikan ada.

            Sertifikat tanpa kunci privat yang dapat dipakai SChannel tetap
            terlihat sehat dari segala sisi: sidik jarinya benar, masa
            berlakunya benar, berkasnya ada. Yang gagal hanya jabat tangan TLS,
            diam-diam, dan gejalanya muncul di mesin lain sebagai galat yang
            tidak menyebut sertifikat sama sekali.

            Satu baris di sini memindahkan penemuan kegagalan itu dari "beberapa
            jam menelusuri di sisi klien" menjadi "baris pertama di layar".
        */
        var certLine = $"Sertifikat {CertificatePin.Thumbprint(certificate)} · kunci privat "
                     + (certificate.HasPrivateKey ? "ada" : "TIDAK ADA — TLS akan gagal");

        if (!certificate.HasPrivateKey)
        {
            MessageBox.Show(
                "Sertifikat konsol tidak memiliki kunci privat yang dapat dipakai.\n\n"
              + "Hapus console.pfx di folder data lalu jalankan ulang Retriva.",
                "Retriva — sertifikat", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var builder = WebApplication.CreateBuilder(args);

        /*
            Log ditulis ke BERKAS, bukan ke konsol.

            Retriva.Admin adalah WinExe dan tidak punya konsol sama sekali. Tanpa
            ini, seluruh log koordinator — sertifikat, pendaftaran panel, relay
            siaran — mengalir ke tempat yang tidak ada, dan satu-satunya jejak
            yang tersedia saat menelusuri masalah adalah sisi agent.
        */
        var fileLog = new FileLoggerProvider(LogPath);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(fileLog);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSignalR(options =>
        {
            /*
                BATAS BAWAAN SIGNALR ADALAH 32 KB, DAN ITU TERLALU KECIL DI SINI.

                Agent mengirim pratinjau layar sebagai data URL base64. Pratinjau
                480x270 biasanya 15–25 KB — di bawah batas, jadi selama berbulan-
                bulan tidak ada yang salah. Tetapi ukuran JPEG bergantung pada
                ISI layar: layar yang ramai menghasilkan berkas yang jauh lebih
                besar daripada layar yang polos.

                Saat siaran berjalan, layar mahasiswa penuh gambar bergerak.
                Pratinjaunya melewati 32 KB, server menolak pesannya, dan
                SignalR MEMUTUS SELURUH SAMBUNGAN agent itu — bukan sekadar
                mengabaikan satu pesan.

                Gejalanya persis yang terlihat di log: agent terputus berulang
                dengan "The server closed the connection with an error", hanya
                ketika siaran berjalan, dan tidak pernah menyebut pratinjau
                maupun ukuran pesan.

                Satu megabyte memberi ruang lebih dari cukup untuk pratinjau
                terburuk sekalipun, dan tetap jauh di bawah ukuran yang dapat
                dipakai membanjiri koordinator.
            */
            options.MaximumReceiveMessageSize = 1024 * 1024;
        });
        builder.Services.AddSingleton<UnitRegistry>();
        builder.Services.AddSingleton<Database>();
        builder.Services.AddSingleton<UnitStore>();
        builder.Services.AddSingleton<ExamStore>();
        builder.Services.AddSingleton<ActivityLog>();
        builder.Services.AddSingleton<ChatStore>();
        builder.Services.AddSingleton<FileVault>();
        builder.Services.AddSingleton<BatchTracker>();
        builder.Services.AddSingleton<PanelStore>();
        builder.Services.AddSingleton<PanelRegistry>();
        builder.Services.AddSingleton<PanelEnrollment>();
        builder.Services.AddSingleton<LabNetwork>();
        builder.Services.AddSingleton<BroadcastRelay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BroadcastRelay>());
        builder.Services.AddSingleton(new CoordinatorIdentity(CertificatePin.Thumbprint(certificate)));
        builder.Services.AddSingleton(config);
        builder.Services.AddHostedService<UnitWatchdog>();
        builder.Services.AddHostedService<PanelBroadcaster>();
        builder.Services.AddHostedService<LabBeaconService>();
        builder.Services.AddHostedService<LatencyProbe>();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(config.ListenPort, listen => listen.UseHttps(certificate));
        });

        var web = builder.Build();
        web.MapHub<AgentHub>(RetrivaProtocol.HubPath);

        // Panel admin memakai hub-nya sendiri, di port dan sertifikat yang sama.
        //
        // Terpisah dari AgentHub karena arah kuasanya berlawanan: agent adalah
        // mesin yang dikendalikan, panel adalah orang yang mengendalikan. Satu
        // hub untuk keduanya berarti setiap metode harus memeriksa lebih dulu
        // siapa yang memanggil, dan satu pemeriksaan yang terlewat membuka
        // seluruh lab kepada siapa pun yang bisa menyamar sebagai agent.
        web.MapHub<PanelHub>(PanelProtocol.HubPath);

        // Titik unduh berkas berbagi Kestrel dan sertifikat yang sama dengan
        // hub, jadi tidak ada port baru maupun kepercayaan baru yang perlu
        // dibangun di sisi agent.
        web.MapRetrivaFiles();
        _ = web.RunAsync();

        var log = web.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Retriva.Admin");
        var activity = web.Services.GetRequiredService<ActivityLog>();

        log.LogInformation("=== SUPERADMIN · data {Dir}", RetrivaPaths.DataDir);
        log.LogInformation("{Cert}", certLine);

        // Menyentuh Database lebih awal supaya migrasi skema selesai sebelum
        // jendela terbuka, bukan saat agent pertama menyambung.
        var unitStore = web.Services.GetRequiredService<UnitStore>();

        // MAC yang berubah tidak lagi menolak pendaftaran, jadi ia harus terlihat
        // di tempat lain. Tanpa baris ini perubahannya benar-benar senyap — dan
        // MAC yang berpindah memang kadang layak dicurigai.
        unitStore.MacChanged += (unitId, before, after) =>
        {
            log.LogWarning("MAC {Unit} berubah: {Lama} -> {Baru}", unitId, before, after);
            activity.Write("system", "unit.mac.changed", unitId, $"{before} -> {after}");
        };

        activity.Write("system", "console.started", detail: $"peran {config.Role}");

        var thumbprint = CertificatePin.Thumbprint(certificate);

        /*
            Panel menyambung ke koordinatornya sendiri LEWAT JARINGAN, ke
            127.0.0.1.

            Terasa berputar — proses ini memanggil dirinya sendiri, dan objek
            yang dituju ada beberapa baris di atas. Tetapi jalan pintas
            dalam-proses akan menjadi jalur yang paling sering dilalui saat
            mengembangkan, sekaligus yang paling jarang dijalankan di lab: panel
            dosen di laptop terpisah memakai cabang yang hampir tidak pernah
            dicoba, dan kesalahannya baru muncul di depan satu kelas.

            Sidik jarinya diisi langsung, bukan lewat pendaftaran: mesin ini
            memang pemilik sertifikat itu, jadi tidak ada yang perlu dibuktikan.
        */
        // Labnya sendiri dicatat sebagai lab yang dikenal, supaya jalur
        // penyambungannya sama persis dengan panel mana pun.
        panel.Remember(thumbprint, config.LabName,
                       PanelProtocol.LoopbackHost, config.ListenPort, token: null);
        ConfigStore.Save(RetrivaPaths.PanelIdentityFile, panel);

        var link = new SuperadminLink(panel, log);

        Show(new MainWindow(link, web.Services.GetRequiredService<FileVault>(),
                            config, thumbprint, log),
             link, log, autoConnect: true);
    }

    /// <summary>
    /// Menjalankan panel yang BUKAN Superadmin.
    ///
    /// Tidak ada Kestrel, tidak ada AgentHub, tidak ada basis data. Ini bukan
    /// penghematan melainkan syarat kebenaran: dua koordinator di satu subnet
    /// berarti agent menyambung ke salah satunya secara acak, dan setengah lab
    /// berhenti menjawab perintah tanpa satu pun galat yang menjelaskannya.
    ///
    /// Panel ini tidak menyimpan apa-apa selain identitasnya sendiri. Seluruh
    /// keadaan lab ada di Superadmin, dan itu memang tempatnya.
    /// </summary>
    private static void RunPanel(PanelConfig panel, ConsoleConfig config)
    {
        // Panel juga menulis ke berkas, dengan nama berbeda supaya dua panel
        // di satu mesin pengembangan tidak saling menimpa.
        using var panelLog = new FileLoggerProvider(RetrivaPaths.LogFile("panel"));

        using var factory = LoggerFactory.Create(b =>
            b.AddProvider(panelLog).SetMinimumLevel(LogLevel.Information));

        var log = factory.CreateLogger("Retriva.Panel");
        log.LogInformation("=== PANEL ADMIN · data {Dir}", RetrivaPaths.DataDir);

        var link = new SuperadminLink(panel, log);

        // FileVault tetap dibuat supaya jendela punya bentuk yang sama, tetapi
        // panel ini menolak berbagi berkas di depan — lemarinya ada di mesin
        // Superadmin, dan berkas yang dititipkan di sini tidak akan pernah
        // ditemukan agent mana pun.
        var vault = new FileVault();

        var window = new MainWindow(link, vault, config,
                                    panel.MostRecentLab?.Thumbprint ?? "", log);

        // Penemuan lab hanya ada di panel yang BUKAN penyelenggara. Superadmin
        // adalah labnya sendiri; mendengarkan beacon di sana hanya akan
        // menampilkan dirinya sendiri sebagai kandidat untuk ditautkan.
        window.AttachDiscovery(new LabDiscovery(log));

        // Menyambung otomatis hanya bila sudah pernah tertaut. Yang belum akan
        // membuka jendela dalam keadaan "belum tertaut", dan penautannya
        // dilakukan dari layar Panel admin — tempat ia dapat diulang.
        Show(window, link, log, autoConnect: link.Linked);
    }

    private static void Show(MainWindow window, SuperadminLink link, ILogger log, bool autoConnect)
    {
        // Disambungkan setelah jendela dibuat, bukan sebelumnya.
        //
        // ConnectAsync memicu StatusChanged, dan penangannya menggambar ke
        // WebView. Dijalankan lebih dulu, peristiwa pertama tiba sebelum ada
        // yang menyimaknya — panel terbuka menampilkan "belum tersambung"
        // padahal sambungannya sudah jadi, dan tidak ada apa pun yang
        // memperbaikinya sampai keadaan berikutnya kebetulan berubah.
        window.Loaded += async (_, _) =>
        {
            if (!autoConnect) return;

            try
            {
                await link.ConnectAsync(link.CoordinatorHost, link.CoordinatorPort);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Gagal menyambung ke koordinator di {Host}:{Port}",
                    link.CoordinatorHost, link.CoordinatorPort);
                window.ReportLinkFailure(
                    $"Tidak dapat menyambung ke {link.CoordinatorHost}:{link.CoordinatorPort} — {ex.Message}");
            }
        };

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        CatchEverything(app);
        app.Run(window);
    }
}
