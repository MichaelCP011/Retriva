using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Retriva.Ui;

namespace Retriva.Setup;

/// <summary>
/// Layar kecil untuk memindahkan unit yang sudah terpasang ke lab lain.
///
/// SATU LAYAR, BUKAN WIZARD. Tidak ada peran yang dipilih, tidak ada prasyarat
/// yang diperiksa, tidak ada berkas yang disalin. Yang berubah hanya ke mana
/// unit ini menyambung, dan pertanyaannya muat dalam satu halaman.
///
/// Dibuka lewat "RetrivaSetup.exe --reconfigure" dari folder pemasangan, atau
/// dari pintasan yang dipasang installer. Ia menuntut hak administrator karena
/// menghidupkan ulang layanan Windows.
/// </summary>
public sealed class ReconfigureWindow : ChromeWindow
{
    private readonly SetupProfile _profile = new() { Role = SetupRole.Lab };

    private readonly TextBox _host;
    private readonly TextBox _port;
    private readonly TextBox _secret;
    private readonly TextBox _thumb;

    private readonly TextBlock _findNote = Theme.Text("", 11.5, Theme.InkDim);
    private readonly TextBlock _result = Theme.Text("", 12, Theme.InkMuted);
    private readonly Button _apply = Theme.Button("Terapkan dan mulai ulang layanan", primary: true);
    private readonly Button _find = Theme.Button("Cari lab di jaringan");

    public ReconfigureWindow() : base("Retriva", "Pindah lab", "admin")
    {
        Width = 640;
        Height = 620;
        MinWidth = 600;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        /*
            Nilai yang sedang berlaku DIBACA dan ditampilkan lebih dulu.

            Layar konfigurasi ulang yang terbuka kosong memaksa orangnya mengetik
            ulang keempat medan, termasuk yang sebenarnya tidak berubah. Sidik
            jari sertifikat sepanjang enam puluh empat karakter yang diketik
            ulang tanpa alasan adalah salah ketik yang menunggu terjadi — dan
            akibatnya adalah unit yang menolak menyambung ke lab mana pun.
        */
        var current = ReadCurrentConfig();
        _profile.Host = current.host;
        _profile.Port = current.port;
        _profile.EnrollmentSecret = current.secret;
        _profile.Thumbprint = current.thumbprint;

        _host = Theme.Field(_profile.Host);
        _port = Theme.Field(_profile.Port.ToString());
        _secret = Theme.Field(_profile.EnrollmentSecret);
        _thumb = Theme.Field(_profile.Thumbprint);

        SetBody(BuildBody(current.found));
    }

    private UIElement BuildBody(bool found)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(26, 22, 26, 22),
        };

        var panel = new StackPanel();

        panel.Children.Add(Theme.Text("Pindahkan unit ini ke lab lain", 19, Theme.Ink, FontWeights.SemiBold));
        panel.Children.Add(Theme.Text(
            "Hanya konfigurasi yang berubah. Berkas program tidak disentuh, dan token perangkat "
          + "dipertahankan — lab asal tetap mengenali unit ini beserta seluruh riwayatnya bila "
          + "suatu saat ia kembali.",
            12.5, Theme.InkMuted));

        if (!found)
        {
            panel.Children.Add(Theme.Text(
                "agent.json belum ada di mesin ini. Kemungkinan Retriva Lab belum terpasang, "
              + "dan yang dibutuhkan adalah pemasangan biasa, bukan layar ini.",
                12, Theme.Warn));
        }

        var body = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        _host.TextChanged += (_, _) => _profile.Host = _host.Text.Trim();
        body.Children.Add(Theme.Labeled("Alamat koordinator lab tujuan", _host));

        _find.HorizontalAlignment = HorizontalAlignment.Left;
        _find.Margin = new Thickness(0, 0, 0, 6);
        _find.Click += OnFind;
        body.Children.Add(_find);

        _findNote.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(_findNote);

        _port.TextChanged += (_, _) =>
        {
            if (int.TryParse(_port.Text.Trim(), out var p) && p is > 0 and < 65536) _profile.Port = p;
        };
        body.Children.Add(Theme.Labeled("Port", _port));

        _secret.TextChanged += (_, _) => _profile.EnrollmentSecret = _secret.Text.Trim();
        body.Children.Add(Theme.Labeled("Kode pendaftaran lab tujuan", _secret));

        _thumb.TextChanged += (_, _) => _profile.Thumbprint = _thumb.Text.Trim();
        body.Children.Add(Theme.Labeled("Sidik jari sertifikat (SHA-256)", _thumb));

        body.Children.Add(Theme.Text(
            "Kode pendaftaran dan sidik jari milik lab TUJUAN, bukan lab asal. Keduanya ada di "
          + "layar Konfigurasi pada panel Superadmin lab itu. Diisi dengan nilai lab asal, "
          + "layanannya tetap berjalan dan setiap upaya mendaftar akan ditolak diam-diam.",
            11.5, Theme.Warn));

        _apply.Margin = new Thickness(0, 20, 0, 0);
        _apply.HorizontalAlignment = HorizontalAlignment.Left;
        _apply.Click += OnApply;
        body.Children.Add(_apply);

        _result.Margin = new Thickness(0, 12, 0, 0);
        _result.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_result);

        panel.Children.Add(body);
        scroll.Content = panel;
        return scroll;
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        _find.IsEnabled = false;
        _find.Content = "Mendengarkan...";
        _findNote.Text = "";

        var labs = await Task.Run(() => LabFinder.Listen(TimeSpan.FromSeconds(4)));

        _find.IsEnabled = true;
        _find.Content = "Cari lab di jaringan";

        if (labs.Count == 0)
        {
            _findNote.Text =
                "Tidak ada lab terdengar. Bisa berarti koordinatornya belum berjalan, berbeda "
              + "jaringan, atau multicast diblokir. Alamatnya tetap dapat diketik.";
            _findNote.Foreground = Theme.Brush(Theme.Warn);
            return;
        }

        var lab = labs[0];
        _host.Text = lab.Host;
        _port.Text = lab.Port.ToString();
        if (!string.IsNullOrWhiteSpace(lab.Thumbprint)) _thumb.Text = lab.Thumbprint;

        /*
            Kode pendaftaran TIDAK diisi dari beacon, dan tidak akan pernah.

            Beacon terbuka bagi siapa pun di jaringan yang sama. Kode pendaftaran
            adalah satu-satunya hal yang membuktikan bahwa orang yang memindahkan
            unit ini memang berwenang atas lab tujuan, dan menyiarkannya akan
            membuat pembuktian itu tidak berarti apa-apa.
        */
        _findNote.Text = labs.Count == 1
            ? $"Ditemukan: {lab.LabName} di {lab.Host}:{lab.Port}. Kode pendaftaran tetap diisi sendiri."
            : $"Ditemukan {labs.Count} lab; dipakai {lab.LabName} di {lab.Host}. "
              + $"Lainnya: {string.Join(", ", labs.Skip(1).Select(l => $"{l.LabName} ({l.Host})"))}";
        _findNote.Foreground = Theme.Brush(Theme.Ok);
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_profile.Host))
        {
            Say("Alamat koordinator belum diisi.", Theme.Bad);
            return;
        }

        if (string.IsNullOrWhiteSpace(_profile.Thumbprint))
        {
            Say("Sidik jari sertifikat belum diisi. Tanpa penyematan, agent menolak "
              + "menyambung ke koordinator mana pun.", Theme.Bad);
            return;
        }

        _apply.IsEnabled = false;
        _apply.Content = "Menerapkan...";

        var lines = new List<string>();
        var ok = await Task.Run(() => Program.ApplyReconfigure(_profile, lines.Add));

        _apply.IsEnabled = true;
        _apply.Content = "Terapkan dan mulai ulang layanan";

        Say(string.Join("\n", lines), ok ? Theme.Ok : Theme.Bad);
    }

    private void Say(string text, System.Windows.Media.Color color)
    {
        _result.Text = text;
        _result.Foreground = Theme.Brush(color);
    }

    /// <summary>
    /// Membaca agent.json yang berlaku. Gagal baca diperlakukan sebagai kosong.
    /// </summary>
    private static (bool found, string host, int port, string secret, string thumbprint)
        ReadCurrentConfig()
    {
        var file = Path.Combine(SetupProfile.DataDir, "agent.json");
        if (!File.Exists(file)) return (false, "", 8443, "", "");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;

            var host = root.TryGetProperty("consoleEndpoints", out var eps)
                       && eps.ValueKind == JsonValueKind.Array
                       && eps.GetArrayLength() > 0
                ? eps[0].GetString() ?? ""
                : "";

            // Endpoint dapat tersimpan sebagai URL lengkap setelah penemuan
            // otomatis. Yang ditampilkan di kotak adalah host telanjang, karena
            // itulah bentuk yang diketik orang.
            if (Uri.TryCreate(host, UriKind.Absolute, out var uri)) host = uri.Host;

            return (
                true,
                host,
                root.TryGetProperty("consolePort", out var p) && p.TryGetInt32(out var port)
                    ? port : 8443,
                root.TryGetProperty("enrollmentSecret", out var s) ? s.GetString() ?? "" : "",
                root.TryGetProperty("consoleCertificateThumbprint", out var t)
                    ? t.GetString() ?? "" : "");
        }
        catch (JsonException)
        {
            return (false, "", 8443, "", "");
        }
    }
}
