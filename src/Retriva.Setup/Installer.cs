using System.IO;

namespace Retriva.Setup;

/// <summary>
/// Menyusun rencana pemasangan untuk sebuah peran, lalu menjalankannya.
///
/// URUTAN LANGKAHNYA BUKAN SELERA.
///
/// Layanan lama dihentikan sebelum berkas disentuh, karena exe yang sedang
/// berjalan tidak dapat ditimpa. Konfigurasi ditulis sebelum layanan dimulai,
/// karena agent membacanya saat start. Entri Add/Remove Programs ditulis
/// sebelum layanan dijalankan, supaya pemasangan yang gagal di langkah terakhir
/// tetap dapat dicopot lewat cara yang wajar.
/// </summary>
public static class Installer
{
    public const string FirewallOut = "Retriva Agent keluar";
    public const string FirewallBroadcast = "Retriva siaran masuk";
    public const string FirewallPanelIn = "Retriva koordinator masuk";
    public const string ClientRunValue = "RetrivaClient";

    public static InstallPlan Build(SetupProfile profile, Action<string> log)
    {
        var plan = new InstallPlan(log);
        var dir = profile.InstallDir;
        var setupExe = Path.Combine(dir, "RetrivaSetup.exe");
        var uninstallExe = Path.Combine(dir, "Uninstall.exe");

        /*
            Berkas yang sudah ada TIDAK dihapus sebagai langkah tersendiri.

            Ekstraksi menimpa dengan overwrite, dan itu memang yang diinginkan
            pada pemasangan ulang: berkas lama tergantikan, folder data di
            ProgramData tidak tersentuh. Menghapus foldernya lebih dulu berarti
            pemasangan yang gagal di tengah meninggalkan mesin tanpa versi lama
            MAUPUN versi baru — lebih buruk daripada keadaan sebelum dimulai.
        */

        if (profile.Role == SetupRole.Lab)
        {
            plan.Add("Menghentikan layanan yang sedang berjalan",
                apply: () => SystemActions.StopService(SetupProfile.ServiceName, TimeSpan.FromSeconds(20)),

                // Tidak dihidupkan lagi saat dibatalkan: layanan lama menunjuk
                // ke berkas yang mungkin sudah separuh tertimpa. Menyalakannya
                // kembali berarti menjalankan campuran dua versi.
                undo: null);
        }

        plan.Add("Menyalin berkas program",
            apply: () => Payload.Extract(profile.Role, dir, log),
            undo: () =>
            {
                // Hanya folder programnya. ProgramData tidak pernah disentuh
                // pembatalan — di dalamnya ada token perangkat dan basis data.
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            });

        plan.Add("Menyalin installer dan uninstaller",
            apply: () =>
            {
                /*
                    Installer menyalin DIRINYA SENDIRI ke folder pemasangan sebagai
                    RetrivaSetup.exe dan Uninstall.exe.

                    Uninstall dijalankan dari Add/Remove Programs, kapan saja,
                    berbulan-bulan kemudian. Berkas installer yang dipakai hari
                    ini besar kemungkinan sudah tidak ada di Downloads pada saat
                    itu — dan UninstallString yang menunjuk ke berkas yang hilang
                    membuat entrinya tidak dapat dihapus sama sekali, bahkan
                    tidak dari Apps & features.
                */
                var source = Environment.ProcessPath
                    ?? throw new InvalidOperationException("jalur installer tidak diketahui");

                if (!string.Equals(source, setupExe, StringComparison.OrdinalIgnoreCase))
                    File.Copy(source, setupExe, overwrite: true);

                SystemActions.CreateHardLinkOrCopy(setupExe, uninstallExe);
                SystemActions.RegisterAppPath("Uninstall.exe", uninstallExe);
            },
            undo: () =>
            {
                if (File.Exists(setupExe)) File.Delete(setupExe);
                if (File.Exists(uninstallExe)) File.Delete(uninstallExe);
                SystemActions.UnregisterAppPath("Uninstall.exe");
            });

        if (profile.Role == SetupRole.Lab)
        {
            BuildLabSteps(plan, profile, dir, log);
        }
        else
        {
            BuildPanelSteps(plan, profile, dir, log);
        }

        /*
            Pintasan "Kumpulkan log", DI KEDUA PERAN.

            Ini bukan kenyamanan. Log Retriva ada di ProgramData\Retriva\logs —
            folder yang tidak akan ditebak siapa pun, dan yang sebelumnya bahkan
            tidak sama untuk tiap komponen. Setiap kali ada masalah di lab,
            putaran pertamanya habis untuk menjelaskan letak berkasnya, dan
            putaran itu berjarak satu hari kerja.

            Pintasan ini mengubah "kirimkan lognya" dari instruksi bercabang tiga
            menjadi satu kalimat yang sama untuk PC mahasiswa, laptop dosen, dan
            PC Asisten: buka Start, ketik Retriva, klik Kumpulkan log, kirim
            berkas zip yang muncul di Desktop.
        */
        var logsLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "Retriva — Kumpulkan log.lnk");

        plan.Add("Membuat pintasan kumpulkan log",
            apply: () => Shortcut.Create(
                logsLink, setupExe, dir,
                "Mengumpulkan log dan keadaan mesin ini menjadi satu berkas zip di Desktop",
                arguments: "--logs"),
            undo: () => Shortcut.Delete(logsLink));

        var uninstallLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            $"Copot {profile.ProductName}.lnk");

        plan.Add("Membuat pintasan copot pemasangan",
            apply: () => Shortcut.Create(
                uninstallLink, uninstallExe, dir,
                $"Mencopot {profile.ProductName} dan seluruh pengaturannya"),
            undo: () => Shortcut.Delete(uninstallLink));

        plan.Add("Mendaftarkan di Add/Remove Programs",
            apply: () => SystemActions.RegisterUninstall(
                profile.UninstallKey, profile.ProductName, SetupProfile.Version,
                SetupProfile.Publisher, dir, uninstallExe, SystemActions.DirectorySize(dir)),
            undo: () => SystemActions.UnregisterUninstall(profile.UninstallKey));

        return plan;
    }

    private static void BuildLabSteps(InstallPlan plan, SetupProfile profile, string dir, Action<string> log)
    {
        var agentExe = Path.Combine(dir, "RetrivaAgent.exe");
        var clientExe = Path.Combine(dir, "Client", "RetrivaClient.exe");

        plan.Add("Menulis konfigurasi agent",
            apply: () =>
            {
                profile.WriteAgentConfig(log);
                SystemActions.GrantUsersModify(SetupProfile.DataDir);
            },

            // Konfigurasi TIDAK dihapus saat pembatalan. Di dalamnya token
            // perangkat yang mungkin sudah ada sejak pemasangan sebelumnya, dan
            // menghapusnya adalah kerusakan yang tidak dapat dipulihkan.
            undo: null);

        plan.Add("Mengizinkan agent menjangkau koordinator",
            apply: () => SystemActions.AddFirewallRule(FirewallOut,
                $"dir=out action=allow program=\"{agentExe}\" enable=yes"),
            undo: () => SystemActions.RemoveFirewallRule(FirewallOut));

        plan.Add("Mengizinkan siaran multicast masuk",
            apply: () => SystemActions.AddFirewallRule(FirewallBroadcast,
                "dir=in action=allow protocol=UDP localport=9100-9103 enable=yes"),
            undo: () => SystemActions.RemoveFirewallRule(FirewallBroadcast));

        plan.Add("Mendaftarkan layanan Windows",
            apply: () =>
            {
                // Layanan lama dihapus lebih dulu bila ada. sc create menolak
                // nama yang sudah terpakai, dan penolakannya berbunyi tentang
                // "specified service already exists" pada pemasangan ulang yang
                // sebenarnya wajar.
                SystemActions.DeleteService(SetupProfile.ServiceName);
                SystemActions.CreateService(
                    SetupProfile.ServiceName, "Retriva Agent", Path.Combine(dir, "RetrivaAgent.exe"),
                    "Menghubungkan PC ini ke koordinator Retriva di laboratorium.");
            },
            undo: () => SystemActions.DeleteService(SetupProfile.ServiceName));

        plan.Add("Menjalankan klien saat pengguna login",
            apply: () => SystemActions.SetClientAutostart(ClientRunValue, $"\"{clientExe}\""),
            undo: () => SystemActions.ClearClientAutostart(ClientRunValue));

        plan.Add("Menjalankan layanan",
            apply: () => SystemActions.StartService(SetupProfile.ServiceName),
            undo: () => SystemActions.StopService(SetupProfile.ServiceName, TimeSpan.FromSeconds(15)));

        var clientMenuLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "Retriva Client.lnk");

        var clientDesktopLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            "Retriva Client.lnk");

        var clientDir = Path.GetDirectoryName(clientExe) ?? dir;

        plan.Add("Membuat pintasan Retriva Client di Start Menu",
            apply: () => Shortcut.Create(clientMenuLink, clientExe, clientDir, "Retriva Client (PC Mahasiswa)"),
            undo: () => Shortcut.Delete(clientMenuLink));

        plan.Add("Membuat pintasan Retriva Client di Desktop",
            apply: () => Shortcut.Create(clientDesktopLink, clientExe, clientDir, "Retriva Client (PC Mahasiswa)"),
            undo: () => Shortcut.Delete(clientDesktopLink));

        plan.Add("Mendaftarkan Retriva Client di pencarian Windows",
            apply: () =>
            {
                SystemActions.RegisterAppPath("RetrivaClient.exe", clientExe);
                SystemActions.RegisterAppPath("Retriva.exe", clientExe);
            },
            undo: () =>
            {
                SystemActions.UnregisterAppPath("RetrivaClient.exe");
                SystemActions.UnregisterAppPath("Retriva.exe");
            });

        /*
            Pintasan "Pindah lab", supaya fiturnya dapat DITEMUKAN.
        */
        var reconfigureLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "Retriva — Pindah lab.lnk");

        plan.Add("Membuat pintasan pindah lab",
            apply: () => Shortcut.Create(
                reconfigureLink, Path.Combine(dir, "RetrivaSetup.exe"), dir,
                "Mengarahkan unit ini ke laboratorium lain tanpa pemasangan ulang",
                arguments: "--reconfigure"),
            undo: () => Shortcut.Delete(reconfigureLink));
    }

    private static void BuildPanelSteps(InstallPlan plan, SetupProfile profile, string dir, Action<string> log)
    {
        var panelExe = Path.Combine(dir, "Retriva.exe");

        plan.Add("Menulis identitas panel",
            apply: () =>
            {
                profile.WritePanelConfig(log);

                /*
                    Hak ubah diberikan ke pengguna biasa untuk seluruh folder data ProgramData\Retriva.

                    Installer berjalan terelevasi (Administrator), sedangkan panel admin
                    dijalankan oleh pengguna biasa (dosen/asisten) tanpa UAC. Seluruh berkas
                    seperti panel.json, console.json, sertifikat, dan basis data harus dapat
                    ditulis dan diubah oleh proses panel admin biasa.
                */
                SystemActions.GrantUsersModify(SetupProfile.DataDir);
            },

            // Sama seperti agent.json: di dalamnya daftar lab yang pernah
            // menyetujui panel ini. Tidak dihapus oleh pembatalan.
            undo: null);

        /*
            SELURUH PORTA UDP RETRIVA DIBUKA, BUKAN HANYA PENEMUAN LAB.

            Versi pertama hanya mengizinkan UDP 9102 — dan hanya di mesin
            koordinator. Dua kekurangan, keduanya sunyi:

              9101  frame kendali jarak jauh, dikirim KLIEN ke panel. Tanpa
                    aturan ini, panel meminta kendali, klien mulai mengirim
                    layarnya, dan tidak satu frame pun melewati firewall panel.
                    Yang terlihat: "menunggu frame di port 9101" selamanya,
                    lalu "remote.view tidak dijawab".

              9103  frame siaran dari panel ke relay. Di PC Asisten keduanya
                    satu mesin, jadi lewat loopback dan tidak terlihat salah.
                    Di laptop dosen yang menyiarkan, frame-nya harus melewati
                    jaringan — dan tanpa aturan ini tidak akan pernah sampai.

            Kekurangan kedua adalah cakupannya: aturan UDP dulu hanya dipasang
            bila mesin ini menjalankan lab. Panel dosen justru MENERIMA beacon
            penemuan lab di 9102, jadi tanpa aturan itu ia tidak pernah
            menemukan lab mana pun — dan satu-satunya jalan tersisa adalah
            mengetik alamat dengan tangan.

            Satu rentang untuk semuanya. Empat aturan terpisah berarti empat
            tempat yang bisa lupa diperbarui saat porta bertambah.
        */
        plan.Add("Mengizinkan lalu lintas siaran, kendali, dan penemuan lab",
            apply: () => SystemActions.AddFirewallRule("Retriva UDP masuk",
                "dir=in action=allow protocol=UDP localport=9100-9103 enable=yes"),
            undo: () => SystemActions.RemoveFirewallRule("Retriva UDP masuk"));

        if (profile.HostsLab)
        {
            /*
                Hanya koordinator yang membuka porta TCP.

                Panel dosen tidak melayani siapa pun lewat TCP — ia hanya
                menyambung keluar. Membuka 8443 di laptopnya berarti melubangi
                firewall untuk layanan yang tidak pernah berjalan di sana.
            */
            plan.Add("Mengizinkan panel lain dan agent menyambung",
                apply: () => SystemActions.AddFirewallRule(FirewallPanelIn,
                    $"dir=in action=allow protocol=TCP localport={profile.Port} enable=yes"),
                undo: () => SystemActions.RemoveFirewallRule(FirewallPanelIn));
        }

        var panelMenuLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "Retriva Panel Admin.lnk");

        var panelDesktopLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            "Retriva Panel Admin.lnk");

        plan.Add("Membuat pintasan di Start Menu",
            apply: () => Shortcut.Create(panelMenuLink, panelExe, dir, "Panel admin Retriva"),
            undo: () => Shortcut.Delete(panelMenuLink));

        plan.Add("Membuat pintasan di Desktop",
            apply: () => Shortcut.Create(panelDesktopLink, panelExe, dir, "Panel admin Retriva"),
            undo: () => Shortcut.Delete(panelDesktopLink));

        plan.Add("Mendaftarkan Panel Admin di pencarian Windows",
            apply: () => SystemActions.RegisterAppPath("Retriva.exe", panelExe),
            undo: () => SystemActions.UnregisterAppPath("Retriva.exe"));
    }

    /// <summary>
    /// Mencopot pemasangan.
    ///
    /// Tidak memakai InstallPlan: pembatalan berurutan berguna saat memasang,
    /// tetapi uninstall harus MENERUSKAN meski satu langkah gagal. Aturan
    /// firewall yang sudah hilang tidak boleh menghentikan penghapusan layanan.
    /// </summary>
    public static void Uninstall(SetupRole role, bool cleanData, Action<string> log)
    {
        var profile = new SetupProfile { Role = role };
        var dir = profile.InstallDir;

        void Try(string what, Action action)
        {
            try { action(); log($"  {what}"); }
            catch (Exception ex) { log($"  GAGAL {what}: {ex.Message}"); }
        }

        log($"Mencopot {profile.ProductName}...");

        if (role == SetupRole.Lab)
        {
            Try("layanan dihentikan", () =>
                SystemActions.StopService(SetupProfile.ServiceName, TimeSpan.FromSeconds(20)));
            Try("layanan dihapus", () => SystemActions.DeleteService(SetupProfile.ServiceName));
            Try("autostart klien dilepas", () => SystemActions.ClearClientAutostart(ClientRunValue));
            Try("aturan firewall keluar dihapus", () => SystemActions.RemoveFirewallRule(FirewallOut));
            Try("aturan siaran dihapus", () => SystemActions.RemoveFirewallRule(FirewallBroadcast));

            Try("pintasan klien di Start Menu dihapus", () => Shortcut.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "Retriva Client.lnk")));
            Try("pintasan klien di Desktop dihapus", () => Shortcut.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                "Retriva Client.lnk")));
            Try("registrasi pencarian klien dihapus", () =>
            {
                SystemActions.UnregisterAppPath("RetrivaClient.exe");
                SystemActions.UnregisterAppPath("Retriva.exe");
            });

            Try("pintasan pindah lab dihapus", () => Shortcut.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "Retriva — Pindah lab.lnk")));

            // Klien berjalan di sesi pengguna dan memegang berkasnya sendiri.
            // Tanpa menghentikannya, penghapusan folder gagal separuh jalan.
            Try("klien dihentikan", () => StopProcess("RetrivaClient", log));
        }
        else
        {
            Try("aturan koordinator dihapus", () => SystemActions.RemoveFirewallRule(FirewallPanelIn));
            Try("aturan UDP dihapus", () => SystemActions.RemoveFirewallRule("Retriva UDP masuk"));

            // Nama lama dari versi sebelumnya. Dihapus juga supaya pemasangan
            // yang pernah memakainya tidak meninggalkan aturan yatim di firewall.
            Try("aturan penemuan lab lama dihapus",
                () => SystemActions.RemoveFirewallRule("Retriva penemuan lab"));
            Try("pintasan panel di Start Menu dihapus", () => Shortcut.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "Retriva Panel Admin.lnk")));
            Try("pintasan panel di Desktop dihapus", () => Shortcut.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                "Retriva Panel Admin.lnk")));
            Try("registrasi pencarian panel dihapus", () => SystemActions.UnregisterAppPath("Retriva.exe"));
            Try("panel dihentikan", () => StopProcess("Retriva", log));
        }

        // Dipasang oleh kedua peran, jadi dihapus di luar percabangan peran.
        Try("pintasan kumpulkan log dihapus", () => Shortcut.Delete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "Retriva — Kumpulkan log.lnk")));

        Try("pintasan copot pemasangan dihapus", () =>
        {
            Shortcut.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), $"Copot {profile.ProductName}.lnk"));
            Shortcut.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Copot Retriva Client.lnk"));
            Shortcut.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Copot Retriva Lab (PC Mahasiswa).lnk"));
            Shortcut.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Copot Retriva Panel Admin.lnk"));
            SystemActions.UnregisterAppPath("Uninstall.exe");
        });

        Try("entri Add/Remove Programs dihapus",
            () => SystemActions.UnregisterUninstall(profile.UninstallKey));

        Try("berkas program dihapus", () => DeleteInstallDir(dir, log));

        /*
            Data di ProgramData DIPERTAHANKAN kecuali diminta sebaliknya.

            Di dalamnya token perangkat, basis data, dan sertifikat. Orang yang
            melakukan uninstall biasanya sedang memperbaiki sesuatu — memasang
            versi baru, memindahkan koordinator — bukan sedang ingin menghapus
            riwayat kelas satu semester.
        */
        if (cleanData)
        {
            Try("data di ProgramData dihapus", () =>
            {
                if (Directory.Exists(SetupProfile.DataDir))
                    Directory.Delete(SetupProfile.DataDir, recursive: true);
            });
        }
        else
        {
            log($"  data di {SetupProfile.DataDir} dipertahankan (pakai --clean-data untuk menghapusnya)");
        }

        log("Selesai.");
    }

    private static void StopProcess(string processName, Action<string> log)
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            try { p.Kill(); p.WaitForExit(5000); }
            catch (Exception ex) { log($"  {processName} tidak dapat dihentikan: {ex.Message}"); }
            finally { p.Dispose(); }
        }
    }

    /// <summary>
    /// Menghapus folder pemasangan, KECUALI installer yang sedang berjalan.
    ///
    /// Uninstall dijalankan dari RetrivaSetup.exe di dalam folder itu sendiri;
    /// Windows tidak mengizinkan berkas exe yang sedang berjalan dihapus. Yang
    /// tersisa satu berkas, dan ia dijadwalkan hilang saat PC dinyalakan ulang.
    /// </summary>
    private static void DeleteInstallDir(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) return;

        var self = Environment.ProcessPath;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); }
            catch (Exception ex) { log($"  {Path.GetFileName(file)} tidak dapat dihapus: {ex.Message}"); }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            try { Directory.Delete(sub, recursive: true); } catch (Exception) { /* ada yang terkunci */ }
        }

        if (self is not null && self.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
        {
            PendingDelete.Schedule(self);
            PendingDelete.Schedule(dir);
            log("  installer menghapus dirinya sendiri saat PC dinyalakan ulang");
        }
        else
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* sisa terkunci */ }
        }
    }
}
