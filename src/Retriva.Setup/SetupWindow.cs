using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Retriva.Ui;

namespace Retriva.Setup;

/// <summary>
/// Wizard pemasangan: peran, prasyarat, konfigurasi, pemasangan.
///
/// Memakai ChromeWindow yang sama dengan panel admin dan klien, supaya installer
/// terbaca sebagai bagian dari Retriva dan bukan alat terpisah yang kebetulan
/// dipakai. Bilah judul, warna, dan tombol jendelanya sama persis.
/// </summary>
public sealed class SetupWindow : ChromeWindow
{
    private readonly SetupProfile _profile = new();
    private readonly List<string> _log = [];

    private readonly StackPanel _rail = new();
    private readonly Border _pane = new();
    private readonly Button _back = Theme.Button("Kembali");
    private readonly Button _next = Theme.Button("Lanjut", primary: true);
    private readonly TextBlock _footNote = Theme.Text("", 11.5, Theme.InkDim);

    private int _step;
    private bool _installing;
    private bool _installed;

    /// <summary>Prasyarat yang menghalangi, dihitung ulang tiap layar prasyarat dibuka.</summary>
    private int _blocking;

    /*
        KONFIGURASI SEBELUM PRASYARAT, dan urutan itu diperbaiki setelah gagal.

        Versi pertama memeriksa prasyarat di langkah kedua, sebelum alamat
        koordinator diisi. Pemeriksaan koneksi lalu selalu menguji nilai
        BAWAAN — 192.168.1.252 — dan tidak pernah diulang setelah orangnya
        mengetik alamat yang sebenarnya.

        Akibatnya dua arah sekaligus, keduanya buruk. Alamat bawaan yang
        kebetulan benar meloloskan pemasangan tanpa menguji apa pun yang
        dimasukkan orangnya. Alamat bawaan yang salah — yaitu hampir selalu —
        menghalangi pemasangan dengan galat tentang alamat yang bahkan belum
        sempat diisi.

        Sekarang alamatnya ditanya lebih dulu, lalu diuji.
    */
    private static readonly string[] StepNames =
        ["Peran", "Konfigurasi", "Prasyarat", "Pemasangan"];

    public SetupWindow()
        : base("Retriva", "Pemasangan", "admin")
    {
        Width = 760;
        Height = 560;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        SetBody(BuildShell());
        Render();
    }

    private UIElement BuildShell()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        /* ---------------- rail langkah ---------------- */

        var railHost = new Border
        {
            Background = Theme.Brush(Theme.RailBg),
            Padding = new Thickness(18, 20, 18, 18),
        };

        var railStack = new StackPanel();
        railStack.Children.Add(Theme.Text("Retriva", 15, Theme.Ink, FontWeights.SemiBold));
        railStack.Children.Add(Theme.Text($"versi {SetupProfile.Version}", 11, Theme.InkDim));

        _rail.Margin = new Thickness(0, 22, 0, 0);
        railStack.Children.Add(_rail);

        var spacer = new Border { Height = 1 };
        railStack.Children.Add(spacer);

        railHost.Child = railStack;
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);

        /* ---------------- isi + kaki ---------------- */

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _pane.Padding = new Thickness(26, 22, 26, 12);
        Grid.SetRow(_pane, 0);
        right.Children.Add(_pane);

        var foot = new Border
        {
            BorderBrush = Theme.Brush(Theme.BorderSoft),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(26, 12, 26, 16),
        };

        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _footNote.VerticalAlignment = VerticalAlignment.Center;
        _footNote.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_footNote, 0);
        footGrid.Children.Add(_footNote);

        _back.Click += (_, _) => { _step--; Render(); };
        Grid.SetColumn(_back, 1);
        footGrid.Children.Add(_back);

        _next.Margin = new Thickness(8, 0, 0, 0);
        _next.Click += (_, _) => Advance();
        Grid.SetColumn(_next, 2);
        footGrid.Children.Add(_next);

        foot.Child = footGrid;
        Grid.SetRow(foot, 1);
        right.Children.Add(foot);

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return grid;
    }

    /* ================= navigasi ================= */

    private void Advance()
    {
        if (_installed) { Close(); return; }

        if (_step < 3) { _step++; Render(); return; }

        /*
            Task pemasangan DIAMATI, tidak dibuang.

            "_ = InstallAsync()" membuang Task-nya beserta setiap pengecualian di
            dalamnya. Pemasangan yang gagal karena hal yang tidak diduga —
            sebuah bug di installer sendiri, bukan di mesin penggunanya — lalu
            terlihat sebagai tombol yang ditekan tanpa hasil apa pun.

            ContinueWith menangkapnya dan menampilkannya di layar yang sama
            dengan kegagalan lain, sehingga tidak ada jalur kegagalan yang
            berakhir dalam diam.
        */
        _ = InstallAsync().ContinueWith(t =>
        {
            if (t.Exception is null) return;

            var inner = t.Exception.InnerException ?? t.Exception;
            _installing = false;
            _progressText = $"Installer berhenti: {inner.Message}";
            _progressBad = true;
            Log($"GAGAL: {inner}");
            Render();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Render()
    {
        RenderRail();

        _pane.Child = _step switch
        {
            0 => RoleStep(),
            1 => ConfigStep(),
            2 => PrerequisiteStep(),
            _ => InstallStep(),
        };

        _back.Visibility = _step == 0 || _installing || _installed
            ? Visibility.Collapsed
            : Visibility.Visible;

        _next.Content = _installed ? "Selesai" : _step == 3 ? "Pasang sekarang" : "Lanjut";
        _next.IsEnabled = !_installing && (_step != 2 || _blocking == 0);

        _footNote.Text = _step == 2 && _blocking > 0
            ? $"{_blocking} prasyarat belum terpenuhi."
            : "";
        _footNote.Foreground = Theme.Brush(Theme.Bad);
    }

    private void RenderRail()
    {
        _rail.Children.Clear();

        for (var i = 0; i < StepNames.Length; i++)
        {
            var done = i < _step;
            var here = i == _step;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Theme.Brush(here ? Theme.Accent : done ? Theme.Ok : Theme.Border),
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var label = Theme.Text(StepNames[i], 12.5,
                here ? Theme.Ink : done ? Theme.InkMuted : Theme.InkDim,
                here ? FontWeights.SemiBold : FontWeights.Normal);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            _rail.Children.Add(row);
        }
    }

    /* ================= langkah 1: peran ================= */

    private UIElement RoleStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Text("Mesin ini akan dipakai sebagai apa?", 19, Theme.Ink, FontWeights.SemiBold));
        panel.Children.Add(Theme.Text(
            "Pilihan ini menentukan seluruh sisanya. Tidak ada setelan lain yang perlu dicocokkan dengannya.",
            12.5, Theme.InkMuted));

        var cards = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        cards.Children.Add(RoleCard(SetupRole.Lab, "PC Mahasiswa",
            "Agent sebagai layanan Windows, dan klien yang berjalan di sesi pengguna. "
          + "Keduanya dipasang bersama — klien adalah tangan agent di desktop, dan agent "
          + "tanpa klien tidak dapat mengunci layar, menampilkan siaran, maupun memantau ujian."));

        cards.Children.Add(RoleCard(SetupRole.Panel, "PC Asisten atau laptop dosen",
            "Panel admin. Pada PC Asisten, panel ini sekaligus menjalankan koordinator lab — "
          + "menyimpan basis data, sertifikat, dan melayani seluruh unit."));

        panel.Children.Add(cards);

        var hasExisting = SystemActions.ServiceExists(SetupProfile.ServiceName)
            || Directory.Exists(Path.Combine(SetupProfile.ProgramFiles, "Retriva", "Lab"))
            || Directory.Exists(Path.Combine(SetupProfile.ProgramFiles, "Retriva", "Panel"))
            || Directory.Exists(SetupProfile.DataDir);

        if (hasExisting)
        {
            var cleanCard = new Border
            {
                Background = Theme.Brush(Theme.Surface1),
                BorderBrush = Theme.Brush(Color.FromArgb(90, 224, 185, 92)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 14, 0, 0),
            };

            var cleanGrid = new Grid();
            cleanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cleanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cleanText = new StackPanel();
            cleanText.Children.Add(Theme.Text("Terdeteksi instalasi atau sisa data/konfigurasi Retriva di PC ini", 12, Theme.Warn, FontWeights.SemiBold));
            cleanText.Children.Add(Theme.Text("Ingin membersihkan total sisa pengaturan lama sebelum memasang ulang?", 11, Theme.InkMuted));
            Grid.SetColumn(cleanText, 0);
            cleanGrid.Children.Add(cleanText);

            var unBtn = Theme.Button("Copot / Bersihkan");
            unBtn.Padding = new Thickness(12, 5, 12, 5);
            unBtn.Click += (_, _) =>
            {
                var unWin = new UninstallWindow();
                unWin.ShowDialog();
                Render();
            };
            Grid.SetColumn(unBtn, 1);
            cleanGrid.Children.Add(unBtn);

            cleanCard.Child = cleanGrid;
            panel.Children.Add(cleanCard);
        }

        return panel;
    }

    private UIElement RoleCard(SetupRole role, string title, string detail)
    {
        var chosen = _profile.Role == role;
        var available = Payload.Has(role);

        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(chosen ? Theme.Surface2 : Theme.Surface1),
            BorderBrush = Theme.Brush(chosen ? Theme.Accent : Theme.BorderSoft),
            BorderThickness = new Thickness(chosen ? 1.5 : 1),
            Cursor = available ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.No,
            Opacity = available ? 1.0 : 0.5,
        };

        var stack = new StackPanel();
        stack.Children.Add(Theme.Text(title, 13.5, Theme.Ink, FontWeights.SemiBold));
        stack.Children.Add(Theme.Text(detail, 11.5, Theme.InkMuted));

        /*
            Peran yang muatannya tidak ikut terbungkus DINONAKTIFKAN, bukan
            disembunyikan.

            Menyembunyikannya membuat orangnya mengira installer ini memang
            hanya untuk satu peran. Menampilkannya lalu gagal di tengah
            pemasangan menyalahkan mesinnya. Yang benar adalah menyebutkan
            bahwa yang kurang ada di installer-nya, bukan di mesin ini.
        */
        if (!available)
        {
            stack.Children.Add(Theme.Text(
                "Tidak tersedia — muatan untuk peran ini tidak ikut dibungkus ke dalam installer.",
                11, Theme.Warn));
        }
        else
        {
            var size = Payload.CompressedSize(role) / 1024.0 / 1024.0;
            stack.Children.Add(Theme.Text($"{size:N0} MB", 11, Theme.InkDim));
        }

        border.Child = stack;

        if (available)
        {
            border.MouseLeftButtonUp += (_, _) => { _profile.Role = role; Render(); };
        }

        return border;
    }

    /* ================= langkah 2: prasyarat ================= */

    private UIElement PrerequisiteStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Text("Pemeriksaan sebelum pemasangan", 19, Theme.Ink, FontWeights.SemiBold));
        panel.Children.Add(Theme.Text(
            "Setiap baris di bawah benar-benar diukur di mesin ini, bukan diandaikan.",
            12.5, Theme.InkMuted));

        var checks = Prerequisites.RunAll(
            _profile.Role == SetupRole.Lab ? _profile.Host : "",
            _profile.Port);

        _blocking = checks.Count(c => c.State == CheckState.Fail);

        var list = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var c in checks) list.Children.Add(CheckRow(c));

        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 320,
            Content = list,
        });

        return panel;
    }

    private static UIElement CheckRow(Check check)
    {
        var (color, mark) = check.State switch
        {
            CheckState.Pass => (Theme.Ok, "OK"),
            CheckState.Warn => (Theme.Warn, "!"),
            CheckState.Fail => (Theme.Bad, "X"),
            _ => (Theme.InkDim, "?"),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = Theme.Text(mark, 11.5, color, FontWeights.SemiBold, wrap: false);
        badge.FontFamily = new FontFamily("Consolas");
        badge.VerticalAlignment = VerticalAlignment.Top;
        badge.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var text = new StackPanel();
        text.Children.Add(Theme.Text(check.Title, 13, Theme.Ink));
        text.Children.Add(Theme.Text(check.Detail, 11.5,
            check.State == CheckState.Fail ? Theme.Bad : Theme.InkDim));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Padding = new Thickness(0, 11, 0, 11),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Theme.Brush(Theme.BorderSoft),
            Child = grid,
        };
    }

    /* ================= langkah 3: konfigurasi ================= */

    private UIElement ConfigStep()
    {
        var panel = new StackPanel();

        if (_profile.Role == SetupRole.Lab)
        {
            panel.Children.Add(Theme.Text("Pengaturan PC Mahasiswa", 19, Theme.Ink, FontWeights.SemiBold));
            panel.Children.Add(Theme.Text(
                "Retriva Client terdesentralisasi: unit akan otomatis mendeteksi dan terhubung ke Superadmin "
              + "di jaringan lokal lab yang sedang ditempati tanpa perlu pengaturan manual.",
                12.5, Theme.InkMuted));

            var body = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

            var name = Theme.Field(_profile.DisplayName);
            name.TextChanged += (_, _) => _profile.DisplayName = name.Text.Trim();
            body.Children.Add(Theme.Labeled("Nama PC Mahasiswa", name));
            body.Children.Add(Theme.Text(
                "Nama atau nomor unit ini yang akan tampil di panel lab yang sedang tersambung (misal: PC-01 atau nama mahasiswa).",
                11, Theme.InkDim));

            body.Children.Add(FolderRow(
                "Folder aplikasi Retriva",
                () => _profile.InstallDirOverride,
                v => _profile.InstallDirOverride = v,
                _profile.DefaultInstallDir,
                "Bawaannya di Program Files. Windows sendiri memastikan pengguna biasa dapat menjalankan isinya "
              + "tanpa dapat menggantinya. Diubah hanya bila drive sistemnya sempit."));

            body.Children.Add(FolderRow(
                "Folder berkas yang diterima",
                () => _profile.FileInboxDir,
                v => _profile.FileInboxDir = v,
                SetupProfile.DefaultFileInbox,
                "Tempat berkas praktikum atau kiriman dari asisten/dosen mendarat. Bawaannya di Documents mahasiswa."));

            panel.Children.Add(body);
        }
        else
        {
            panel.Children.Add(Theme.Text("Peran panel ini", 19, Theme.Ink, FontWeights.SemiBold));
            panel.Children.Add(Theme.Text(
                "Pilih apakah mesin ini menjadi Superadmin penyelenggara lab atau Admin/Dosen yang bergabung.",
                12.5, Theme.InkMuted));

            var body = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

            body.Children.Add(HostChoice(true, "Mesin ini menjalankan lab (Superadmin)",
                "PC Asisten utama. Menjalankan koordinator lab, menyimpan basis data dan sertifikat, "
              + "membuka porta jaringan, serta melayani seluruh unit mahasiswa dan panel admin lain."));

            body.Children.Add(HostChoice(false, "Bergabung ke lab yang sudah ada (Admin / Dosen)",
                "Untuk laptop dosen atau PC asisten pendamping. Tidak membuka porta; aplikasi menemukan lab "
              + "lewat jaringan lokal dan pendaftarannya cukup dilakukan langsung di aplikasi."));

            if (_profile.HostsLab)
            {
                var labName = Theme.Field(_profile.LabName);
                labName.TextChanged += (_, _) => _profile.LabName = labName.Text.Trim();
                body.Children.Add(Theme.Labeled("Nama lab superadmin", labName));
                body.Children.Add(Theme.Text(
                    "Nama laboratorium yang dipancarkan ke jaringan dan ditampilkan di kepala panel (misal: LAB Jaringan, LAB Basis Data).",
                    11, Theme.InkDim));

                var name = Theme.Field(_profile.DisplayName);
                name.TextChanged += (_, _) => _profile.DisplayName = name.Text.Trim();
                body.Children.Add(Theme.Labeled("Nama device / asisten", name));
            }
            else
            {
                var name = Theme.Field(_profile.DisplayName);
                name.TextChanged += (_, _) => _profile.DisplayName = name.Text.Trim();
                body.Children.Add(Theme.Labeled("Nama device admin (laptop dosen / PC asisten)", name));
                body.Children.Add(Theme.Text(
                    "Nama yang akan dilihat oleh Superadmin saat menyetujui pendaftaran panel ini.",
                    11, Theme.InkDim));
            }

            body.Children.Add(FolderRow(
                "Folder aplikasi Retriva",
                () => _profile.InstallDirOverride,
                v => _profile.InstallDirOverride = v,
                _profile.DefaultInstallDir,
                "Folder program panel admin. Data dan basis data lab selalu berada di ProgramData\\Retriva."));

            panel.Children.Add(body);
        }

        return panel;
    }

    /// <summary>
    /// Satu baris pemilih folder: kotak isian, tombol telusuri, dan keterangan.
    ///
    /// KOTAKNYA MENAMPILKAN JALUR BAWAAN SEBAGAI PLACEHOLDER, bukan sebagai isi.
    ///
    /// Mengisinya dengan jalur bawaan akan membuat setiap pemasangan menuliskan
    /// jalur itu secara eksplisit ke konfigurasi, seolah-olah orangnya memang
    /// memilihnya. Bawaan yang berubah di versi berikutnya lalu tidak berlaku
    /// untuk mesin yang sebenarnya tidak pernah memilih apa pun — dan tidak ada
    /// cara membedakannya lagi setelah tertulis.
    ///
    /// Kosong berarti "pakai bawaan", dan itu tetap kosong sampai ada yang
    /// benar-benar mengetik atau menelusuri.
    /// </summary>
    private UIElement FolderRow(string label, Func<string?> get, Action<string?> set,
                                string placeholder, string note)
    {
        var box = Theme.Field(get() ?? "");
        box.TextChanged += (_, _) =>
        {
            var value = box.Text.Trim();
            set(value.Length == 0 ? null : value);
        };

        var browse = Theme.Button("Telusuri");
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = label,
                InitialDirectory = string.IsNullOrWhiteSpace(box.Text) ? placeholder : box.Text.Trim(),
            };

            if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(box, 0);
        row.Children.Add(box);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);

        var stack = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        stack.Children.Add(Theme.Text(label, 11.5, Theme.InkMuted, FontWeights.SemiBold));
        stack.Children.Add(row);
        // Keterangannya SELALU tampil, dan jalur bawaannya disebut di depan.
        // Menyembunyikannya saat kotaknya kosong berarti menghilangkannya
        // justru pada keadaan yang paling butuh penjelasan: kotak kosong yang
        // tidak menyatakan ke mana sebenarnya berkasnya akan pergi.
        stack.Children.Add(Theme.Text($"Kosong berarti {placeholder}", 11, Theme.InkDim));
        stack.Children.Add(Theme.Text(note, 11, Theme.InkDim));

        return stack;
    }

    private UIElement HostChoice(bool value, string title, string detail)
    {
        var chosen = _profile.HostsLab == value;

        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(chosen ? Theme.Surface2 : Theme.Surface1),
            BorderBrush = Theme.Brush(chosen ? Theme.Accent : Theme.BorderSoft),
            BorderThickness = new Thickness(chosen ? 1.5 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var stack = new StackPanel();
        stack.Children.Add(Theme.Text(title, 13, Theme.Ink, FontWeights.SemiBold));
        stack.Children.Add(Theme.Text(detail, 11.5, Theme.InkMuted));
        border.Child = stack;

        border.MouseLeftButtonUp += (_, _) => { _profile.HostsLab = value; Render(); };
        return border;
    }

    /* ================= langkah 4: pemasangan ================= */

    /*
        KEADAAN DISIMPAN SEBAGAI DATA, BUKAN SEBAGAI ELEMEN UI.

        Versi pertama menyimpan StackPanel log dan TextBlock kemajuan sebagai
        field, lalu memasangnya kembali ke ScrollViewer baru setiap kali layar
        digambar ulang. WPF melarang itu: sebuah elemen hanya boleh punya satu
        induk logis, dan memasangnya ke induk kedua melempar
        InvalidOperationException.

        Kegagalannya tidak bersuara sama sekali. Render() dipanggil dari
        InstallAsync, yang dijalankan sebagai "_ = InstallAsync()" — Task-nya
        dibuang, jadi pengecualiannya tidak pernah diamati siapa pun. Yang
        terlihat hanya tombol "Pasang sekarang" yang ditekan dan tidak
        menghasilkan apa-apa. Tidak ada dialog, tidak ada log, tidak ada
        perubahan layar.

        Sekarang log dan teks kemajuan hanyalah string; elemennya dibuat ulang
        dari data itu pada tiap penggambaran. Satu-satunya cara elemen dapat
        punya dua induk adalah dengan menyimpannya, dan tidak ada lagi yang
        disimpan.
    */
    private string _progressText = "";
    private bool _progressBad;

    private UIElement InstallStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Text(
            _installed ? "Pemasangan selesai" : "Siap dipasang",
            19, Theme.Ink, FontWeights.SemiBold));

        panel.Children.Add(Theme.Text(
            _installed
                ? _profile.Role == SetupRole.Lab
                    ? "Unit ini akan muncul di dashboard begitu layanannya menyambung ke koordinator."
                    : "Panel dapat dibuka dari Start Menu."
                : $"{_profile.ProductName} akan dipasang ke {_profile.InstallDir}.",
            12.5, Theme.InkMuted));

        if (_progressText.Length > 0)
        {
            var progress = Theme.Text(_progressText, 12.5,
                _progressBad ? Theme.Bad : _installed ? Theme.Ok : Theme.InkMuted);
            progress.Margin = new Thickness(0, 14, 0, 6);
            panel.Children.Add(progress);
        }

        if (_log.Count > 0)
        {
            var lines = new StackPanel();
            foreach (var line in _log)
            {
                var text = Theme.Text(line, 11.5,
                    line.StartsWith("GAGAL", StringComparison.Ordinal) ? Theme.Bad : Theme.InkDim);
                text.FontFamily = new FontFamily("Consolas");
                text.Margin = new Thickness(0, 0, 0, 2);
                lines.Children.Add(text);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 240,
                Content = lines,
            };

            // Digulung ke bawah setelah tata letaknya selesai. Log yang berhenti
            // di baris pertama membuat pemasangan terlihat macet padahal berjalan.
            scroll.Loaded += (_, _) => scroll.ScrollToEnd();
            panel.Children.Add(scroll);
        }

        return panel;
    }

    private void Log(string line)
    {
        _log.Add(line);
        Dispatcher.Invoke(Render);
    }

    private async Task InstallAsync()
    {
        _installing = true;
        _log.Clear();
        _progressText = "Menyiapkan...";
        _progressBad = false;
        Render();

        var plan = Installer.Build(_profile, Log);

        /*
            Pemasangan dijalankan di utas latar, bukan di utas UI.

            Menjalankannya di utas UI membekukan jendela selama seluruh proses —
            dan jendela yang membeku adalah yang paling sering ditutup paksa oleh
            orang yang mengira aplikasinya hang. Ditutup di tengah, mesin
            tertinggal separuh terpasang tanpa pembatalan sempat berjalan.
        */
        var failure = await Task.Run(() => plan.Run((i, total, title) =>
            Dispatcher.Invoke(() =>
            {
                _progressText = $"Langkah {i} dari {total} — {title}";
                Render();
            })));

        _installing = false;

        if (failure is null)
        {
            _installed = true;
            _progressText = "Selesai.";
            _progressBad = false;
        }
        else
        {
            _progressText = $"Pemasangan dibatalkan: {failure}";
            _progressBad = true;
        }

        Render();
    }
}
