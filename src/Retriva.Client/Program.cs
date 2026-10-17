using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Retriva.Media;
using Retriva.Shared;
using Retriva.Shared.Local;

namespace Retriva.Client;

/// <summary>
/// Klien mahasiswa.
///
/// Berkas ini sengaja tipis: ia hanya menyalakan komponen dan menyambungkan
/// peristiwa di antaranya. Antarmukanya ada di ClientShell, layar terkunci di
/// LockOverlay, kendali jarak jauh di RemoteSessionHost, dan jalur ke agent di
/// AgentLink. Versi sebelumnya menaruh seluruh antarmuka di sini sebagai
/// tumpukan kontrol WPF — berguna untuk membuktikan jembatannya hidup, tetapi
/// bukan sesuatu yang bisa disodorkan ke mahasiswa.
/// </summary>
public static class Program
{
    /// <summary>
    /// Kesadaran DPI per-monitor, dipasang sebelum jendela pertama dibuat.
    ///
    /// Kritis di sisi klien karena dua hal sekaligus: layar yang dikirim ke
    /// admin harus punya resolusi fisik yang benar, dan koordinat yang
    /// disuntikkan kembali harus mengacu pada ruang koordinat yang sama. Kalau
    /// Windows memvirtualkan angkanya, kursor asisten mendarat bergeser — makin
    /// jauh dari kiri-atas, makin besar simpangannya.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [STAThread]
    public static void Main()
    {
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);

        // Application dibuat lebih dulu, sebelum Window mana pun. Jendela yang
        // dibuat sebelum Application ada tidak terdaftar di Application.Windows
        // dan berperilaku tidak menentu.
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        var link = new AgentLink();
        var overlay = new LockOverlay();
        var blocker = new RemoteInputBlocker();
        var remote = new RemoteSessionHost();
        var shell = new ClientShell();

        /*
            Pemantau ujian dibuat sekali, dinyalakan dan dimatikan oleh perintah.

            Objeknya hidup sepanjang proses; yang berumur pendek adalah hook dan
            pendengar papan klip di dalamnya. Membuat ulang objeknya tiap sesi
            akan menyisakan instance lama yang masih memegang hook bila ada satu
            jalur saja yang lupa memanggil Stop — dan hook yang tertinggal adalah
            kegagalan yang tidak muncul di layar mana pun.
        */
        ExamWatcher? exam = null;

        // Log tetap ditulis ke berkas meski kotak lognya sudah tidak ada di
        // layar. Mahasiswa tidak perlu melihatnya, tetapi asisten yang menelusuri
        // masalah tetap butuh, dan berkas jauh lebih mudah disalin utuh daripada
        // tangkapan layar.
        var logFile = RetrivaPaths.LogFile("klien");
        try { File.WriteAllText(logFile, $"=== sesi baru {DateTime.Now:s} ==={Environment.NewLine}"); }
        catch (IOException) { /* diabaikan */ }

        void Append(string line)
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}"); }
            catch (IOException) { /* diabaikan */ }
        }

        link.Log += Append;
        overlay.Diagnostic += Append;
        shell.Diagnostic += Append;

        // Laporan penerimaan siaran diteruskan ke agent, yang menyertakannya di
        // denyut berikutnya. Tiga lapis, dan memang harus: hanya klien yang tahu
        // frame tiba, hanya agent yang punya sambungan ke koordinator, dan hanya
        // koordinator yang tahu panel mana yang perlu diberi tahu.
        shell.ReportBroadcast = (frames, packets) => link.SendAsync(new BroadcastStats
        {
            FramesComplete = frames,
            PacketsReceived = packets,
        });

        string? nim = null;
        string? displayName = null;
        var helpRaised = false;
        var isGuestSession = false;

        /*
            Aturannya dari koordinator, bukan dipatok di sini.

            Baris ini sebelumnya berbunyi `requireLogin: true` beserta komentar
            yang menjelaskan mengapa nilainya selalu benar. Penjelasannya masuk
            akal dan salah: saklar "Wajib login mahasiswa" di layar Konfigurasi
            memang ada, tersimpan dengan benar, dan tidak pernah sampai ke sini.
        */
        void PushState() => shell.PushState(
            connected: link.Connected,
            unitId: link.UnitId,
            requireLogin: link.Policy.RequireStudentLogin,
            allowGuest: link.Policy.AllowGuestAccess,

            // Dipakai sebagai nama saat login tidak diwajibkan. Mahasiswa tidak
            // dimintai apa pun, tetapi panel tetap perlu menyebut seseorang —
            // "PC-07" saja tidak membantu asisten yang mencari orangnya.
            windowsUser: Environment.UserName);

        link.PolicyChanged += _ => PushState();

        /* ---------------- agent -> klien ---------------- */

        link.ConnectionChanged += connected =>
        {
            remote.OnLinkChanged(connected);
            PushState();
            Append(connected ? "[jalur] tersambung ke agent" : "[jalur] terputus");

            /*
                Identitas diumumkan ulang setiap jalur tersambung kembali.

                Agent dapat dimulai ulang tanpa mahasiswa menyentuh apa pun —
                pembaruan layanan, atau layanan yang dipulihkan Windows setelah
                gagal. Klien tetap menampilkan orang yang sama karena memang
                tidak ada yang berubah di layarnya, sedangkan agent yang baru
                tidak tahu siapa pun sedang duduk di sini.

                Panel lalu menampilkan unit tanpa nama sementara mahasiswanya
                ada di depan layar dan merasa sudah masuk. Tidak ada galat, tidak
                ada tanda apa pun — kedua sisi yakin dirinya benar.
            */
            if (!connected || displayName is null) return;

            _ = link.SendAsync(new StudentSignedIn
            {
                Nim = nim ?? "",
                DisplayName = displayName,
                IsGuest = isGuestSession,
            });
            Append($"[masuk] identitas diumumkan ulang · {displayName}");
        };

        // Dua puluh detik. Cukup untuk menoleh ke layar dan membaca, tidak
        // cukup untuk membuat admin menyerah dan menelepon asisten.
        link.RemoteConsentRequested = begin => shell.AskRemoteConsent(
            begin.IssuedByName, begin.AllowInput, TimeSpan.FromSeconds(20));

        link.LockRequested = show => shell.Dispatcher.Invoke(() =>
        {
            Append($"[kunci] diminta oleh {show.IssuedBy}");
            var shown = overlay.ShowLocked(show.Message, show.IssuedBy);
            if (shown) blocker.Install(blocking: true);
            return shown;
        });

        link.UnlockRequested = () => shell.Dispatcher.Invoke(() =>
        {
            blocker.Uninstall();
            overlay.HideLocked();
            return true;
        });

        /*
            Presentasi layar sendiri.

            Dijalankan KLIEN, bukan agent. Agent adalah layanan tanpa sesi
            desktop; ia tidak dapat melihat satu piksel pun dari layar mahasiswa.
            Pembagian yang sama berlaku untuk kunci layar dan kendali jarak jauh.
        */
        using var presenter = new PresenterHost();

        link.PresentRequested = begin => shell.Dispatcher.Invoke(() =>
        {
            var ok = presenter.Start(begin);
            Append(ok
                ? $"[presentasi] layar disiarkan ke {begin.RelayHost}:{begin.RelayPort}"
                : "[presentasi] gagal memulai tangkapan layar");
            return ok;
        });

        link.PresentEndRequested = () => shell.Dispatcher.Invoke(() =>
        {
            presenter.Stop();
            Append("[presentasi] dihentikan");
        });

        // Siaran tampil DI DALAM jendela klien, bukan jendela terpisah.
        link.BroadcastRequested = show => shell.Dispatcher.Invoke(() =>
        {
            try
            {
                shell.ShowBroadcast(show.MulticastGroup, show.Port, show.SourceLabel, show.Fullscreen);
                return true;
            }
            catch (Exception ex)
            {
                Append($"[siaran] gagal: {ex.GetType().Name} — {ex.Message}");
                return false;
            }
        });

        link.BroadcastStopRequested = () => shell.Dispatcher.Invoke(() =>
        {
            shell.HideBroadcast();
            return true;
        });

        link.ChatArrived = chat =>
            shell.PushChat(chat.FromDisplayName, chat.Text, fromAdmin: true, chat.ToEveryone);

        link.ChatCleared = () =>
        {
            helpRaised = false;
            shell.PushChatCleared();
            shell.PushHelp(false);
        };

        link.ExamWatchChanged = title => shell.Dispatcher.Invoke(() =>
        {
            exam ??= new ExamWatcher(
                observe: (kind, description) =>
                {
                    Append($"[ujian] {description}");
                    _ = link.SendAsync(new ExamObservation
                    {
                        Kind = kind,
                        Description = description,
                    });
                },
                log: Append);

            if (string.IsNullOrWhiteSpace(title))
            {
                exam.Stop();
                shell.PushExam(null);
                return;
            }

            // Papan ditampilkan LEBIH DULU, pemantauan menyala setelahnya.
            //
            // Urutan sebaliknya membuat ada celah — sekecil apa pun — ketika
            // aktivitas sudah dicatat sementara mahasiswa belum diberi tahu.
            // Celah itu tidak akan pernah terlihat dalam pengujian, dan tetap
            // merupakan pengawasan tanpa pemberitahuan.
            shell.PushExam(title);
            exam.Start(shell, title);
        });

        link.HelpAcknowledgedByAdmin = by =>
        {
            helpRaised = false;
            shell.PushHelp(false);
            shell.PushChat("Sistem", $"{by} sudah melihat permintaan bantuan Anda.", fromAdmin: true);
        };

        // Berkas hanya diumumkan setelah sidiknya terverifikasi utuh, jadi
        // mahasiswa tidak pernah diarahkan membuka unduhan yang belum lengkap.
        link.FileArrivedAtClient = file =>
            shell.PushFile(file.FileName, file.SavedPath, file.SizeBytes);

        /* ---------------- kendali jarak jauh ---------------- */

        link.RemoteBeginRequested = begin => shell.Dispatcher.Invoke(() =>
        {
            Append($"[kendali] {begin.IssuedByName} · "
                 + (begin.AllowInput ? "mengambil alih input" : "hanya melihat"));
            try { return remote.Start(begin); }
            catch (Exception ex)
            {
                Append($"[kendali] gagal: {ex.GetType().Name} — {ex.Message}");
                return null;
            }
        });

        link.RemoteEndRequested = () => shell.Dispatcher.Invoke(() =>
        {
            if (!remote.Active) return false;
            remote.Stop("diakhiri admin");
            return true;
        });

        /*
            Kegagalan suntikan input DICATAT — sebelumnya tidak oleh siapa pun.

            InputInjector sudah lama menyimpan alasan penolakan di LastFault, dan
            RemoteSessionHost meneruskannya sebagai LastInjectionFault. Tidak ada
            satu baris pun yang membacanya. Jadi ketika kendali jarak jauh
            berhenti menuruti mouse admin, satu-satunya keterangan yang ada —
            "suntikan MouseMove ditolak (error 5), kemungkinan jendela berjalan
            sebagai administrator" — tersimpan di memori dan dibuang bersama
            prosesnya.

            Dibatasi sekali per dua detik. Gerakan mouse datang lima puluh kali
            per detik, dan kegagalan yang berlangsung terus akan menghasilkan
            seratus baris identik tiap dua detik — yang berikutnya adalah log
            yang tidak dapat dibaca, kebalikan dari gunanya.
        */
        var lastFaultAt = DateTime.MinValue;
        string? lastFaultText = null;

        link.InputRequested = inject => shell.Dispatcher.Invoke(() =>
        {
            remote.Apply(inject);

            var fault = remote.LastInjectionFault;
            if (fault is null) { lastFaultText = null; return; }

            var repeated = fault == lastFaultText;
            if (repeated && DateTime.UtcNow - lastFaultAt < TimeSpan.FromSeconds(2)) return;

            lastFaultAt = DateTime.UtcNow;
            lastFaultText = fault;
            Append($"[kendali] {fault}");
        });

        remote.Ended += reason =>
        {
            Append($"[kendali] sesi berakhir — {reason}");
            _ = link.SendAsync(new RemoteSessionEnded { Reason = reason });
        };

        /* ---------------- klien -> agent ---------------- */

        shell.Ready += () =>
        {
            PushState();
            // Dipindai ulang tiap halaman siap, bukan sekali saat proses mulai.
            // Berkas dari sesi sebelumnya tetap ada di disk dan harus terlihat.
            shell.PushInbox();
        };

        shell.SignInRequested = (enteredNim, enteredName, isGuest) =>
        {
            nim = isGuest ? null : enteredNim;
            displayName = enteredName;
            isGuestSession = isGuest;
            Append($"[masuk] {enteredName} ({(isGuest ? "tamu" : enteredNim)})");

            shell.PushStudent(nim, displayName, isGuest);

            // IsGuest AKHIRNYA BISA BERNILAI TRUE.
            //
            // Medan ini ada di protokol sejak lama, diteruskan agent dengan
            // benar, ditampilkan panel dengan benar — dan tidak pernah dikirim
            // bernilai true oleh siapa pun, karena tidak ada tombol tamu.
            // Seluruh rantainya bekerja sempurna untuk keadaan yang tidak
            // pernah terjadi.
            _ = link.SendAsync(new StudentSignedIn
            {
                Nim = isGuest ? "" : enteredNim,
                DisplayName = enteredName,
                IsGuest = isGuest,
            });
            return true;
        };

        shell.SignOutRequested = () =>
        {
            Append($"[keluar] {displayName ?? "?"}");
            nim = null;
            displayName = null;
            isGuestSession = false;
            shell.PushStudent(null, null);
            _ = link.SendAsync(new StudentSignedOut());
        };

        shell.ChatSendRequested = text =>
        {
            // Pesan sendiri ditampilkan langsung, tidak menunggu balasan agent.
            // Menunggu berarti jeda yang terlihat seperti pesan tidak terkirim,
            // dan mahasiswa akan mengetiknya ulang.
            shell.PushChat(displayName ?? "Saya", text, fromAdmin: false);
            _ = link.SendAsync(new StudentChat { Text = text });
        };

        shell.HelpToggleRequested = raised =>
        {
            helpRaised = raised;
            Append(raised ? "[bantuan] diminta" : "[bantuan] dibatalkan");
            _ = link.SendAsync(new StudentHelp { Raised = raised });
        };

        link.SelfName = ClientShell.ReadSelfName;
        link.FileInboxChanged = shell.SetFileInbox;

        shell.UnitNameRequested = name =>
        {
            /*
                Disimpan JUGA di sisi klien, bukan hanya dikirim.

                Nama ini diketik sekali dan diharapkan bertahan. Kalau ia hanya
                dikirim, mahasiswa yang mengetiknya saat agent sedang terputus —
                keadaan yang justru paling mungkin di lab baru — akan melihatnya
                tersimpan lalu hilang begitu aplikasinya dibuka lagi, tanpa
                penjelasan apa pun.

                Berkasnya juga yang membuat agent dapat mengirim ulang nama itu
                setiap kali ia berhasil menyambung, sehingga koordinator yang
                basis datanya diganti tetap mendapatkannya kembali.
            */
            try
            {
                var file = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Retriva", "unit-name.txt");

                Directory.CreateDirectory(Path.GetDirectoryName(file)!);

                if (string.IsNullOrWhiteSpace(name)) File.Delete(file);
                else File.WriteAllText(file, name.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tidak fatal: namanya tetap dikirim, hanya tidak diingat
                // setelah aplikasi ditutup. Kegagalannya dicatat supaya
                // "namanya hilang lagi" punya jejak yang bisa dibaca.
                Append($"[unit] nama tidak dapat disimpan: {ex.Message}");
            }

            Append(string.IsNullOrWhiteSpace(name)
                ? "[unit] nama dihapus"
                : $"[unit] dinamai \"{name.Trim()}\"");

            _ = link.SendAsync(new UnitSelfName { Name = name.Trim() });
        };

        overlay.RescueRequested += () =>
        {
            Append("[kunci] dilepas lewat pintu darurat pengembangan");
            blocker.Uninstall();
            overlay.HideLocked();
        };

        shell.Loaded += (_, _) => link.Start();

        /*
            Pengiriman pratinjau layar (Thumbnail) berkala ke agent setiap 2 detik.
            Karena agent adalah Windows Service di Session 0, penangkapan layar
            interaktif desktop HANYA bisa dilakukan oleh klien yang berjalan di sesi pengguna (Session 1).
        */
        using var thumbCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(thumbCts.Token))
                {
                    if (!link.Connected) continue;

                    try
                    {
                        var jpeg = ScreenThumbnail.Capture();
                        if (jpeg.Length > 0)
                        {
                            await link.SendAsync(new ScreenThumbnailReport
                            {
                                JpegData = jpeg,
                                Width = ScreenThumbnail.Width,
                                Height = ScreenThumbnail.Height,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Append($"[pratinjau] gagal: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, thumbCts.Token);

        shell.Closed += async (_, _) =>
        {
            thumbCts.Cancel();
            blocker.Dispose();
            // Pemblokir input dilepas sebelum proses berakhir, kalau tidak mouse
            // dan keyboard mahasiswa tetap tertahan sampai Windows menyadari
            // hook-nya sudah mati.
            remote.Dispose();

            /*
                Pemantau ujian dilepas di sini juga, bukan diserahkan ke matinya
                proses.

                Windows memang membersihkan hook milik proses yang berakhir, jadi
                baris ini tidak menutup kebocoran nyata. Ia ada untuk alasan yang
                lain: satu-satunya tempat sumber daya pemantauan dilepas harus
                tetap satu, yaitu Stop(). Mengandalkan matinya proses berarti ada
                jalur keluar yang tidak melewatinya — dan jalur semacam itu akan
                dijadikan contoh oleh perubahan berikutnya.
            */
            exam?.Dispose();

            overlay.AllowClose();
            await link.DisposeAsync();
        };

        /*
            Pengecualian yang tidak tertangani DICATAT, tidak mematikan klien
            diam-diam.

            Klien berjalan di PC mahasiswa tanpa siapa pun yang mengawasinya.
            Proses yang mati tanpa jejak berarti unit itu berhenti dipantau,
            berhenti menerima siaran, dan tidak dapat dikunci — sementara agent
            tetap melaporkannya online. Panel menampilkan unit yang sehat.

            Tidak ada kotak pesan di sini, berbeda dari panel admin: mahasiswa
            tidak dapat menindaklanjutinya, dan dialog galat di tengah praktikum
            hanya mengganggu. Yang perlu adalah jejaknya, dan itu masuk ke berkas
            log yang sama dengan sisa diagnostik klien.
        */
        void Report(string where, Exception? ex)
        {
            try
            {
                File.AppendAllText(logFile,
                    $"=== {where} @ {DateTime.Now:O} ==={Environment.NewLine}{ex}{Environment.NewLine}");
            }
            catch (IOException) { /* berkas terkunci */ }
        }

        app.DispatcherUnhandledException += (_, e) =>
        {
            Report("DispatcherUnhandledException", e.Exception);

            // Tetap hidup bila memungkinkan. Klien yang mati adalah unit yang
            // tidak dapat dikunci maupun dipantau.
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        app.Run(shell);
    }
}
