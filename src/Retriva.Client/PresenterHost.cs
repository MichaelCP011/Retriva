using System.Windows;
using System.Windows.Media;
using Retriva.Media;
using Retriva.Shared.Broadcast;
using Retriva.Shared.Local;

namespace Retriva.Client;

/// <summary>
/// Menyiarkan layar mahasiswa ini ke seluruh kelas.
///
/// MEMAKAI JALUR YANG SAMA DENGAN SIARAN DOSEN, HANYA PANGKALNYA BERBEDA.
///
/// Aliran dikirim unicast ke relay Superadmin, dan Superadmin yang memancarkan
/// multicast ke unit lain. Persis seperti panel dosen. Yang berubah hanya siapa
/// yang menangkap layar — dan karena jalurnya sama, tidak ada cabang jaringan
/// baru yang hanya teruji sekali.
///
/// Berbeda dari RemoteSessionHost dalam satu hal yang menentukan: TIDAK ADA
/// penyuntikan input dan TIDAK ADA pemblokiran. Presentasi bukan pengambilalihan
/// — mahasiswa tetap mengemudikan mesinnya sendiri, dan memang itu maksudnya:
/// ia sedang menjelaskan pekerjaannya.
/// </summary>
public sealed class PresenterHost : IDisposable
{
    private BroadcastSession? _stream;
    private PresentingIndicator? _indicator;

    public bool Active => _stream is not null;

    public bool Start(BeginPresenting message)
    {
        Stop();

        try
        {
            /*
                Tangkapan LAYAR PENUH, bukan jendela tertentu.

                Mahasiswa yang mempresentasikan kode hampir selalu berpindah
                antara editor, terminal, dan browser. Mengunci ke satu jendela
                membuat kelas melihat bidang beku setiap kali ia berpindah — dan
                ia tidak akan tahu, karena layarnya sendiri terlihat normal.
            */
            _stream = new BroadcastSession(new BroadcastSettings(
                TargetWidth: Math.Clamp(message.Width, 640, 1920),
                Quality: Math.Clamp(message.Quality, 40, 95),
                Fps: Math.Clamp(message.FrameRate, 5, 30),
                Port: message.RelayPort,
                UnicastHost: message.RelayHost));

            _stream.Start();
        }
        catch (Exception)
        {
            _stream = null;
            return false;
        }

        /*
            PENANDA WAJIB ADA, dan sengaja tidak dapat ditutup.

            Layar yang disiarkan ke seluruh kelas tanpa pemiliknya menyadarinya
            adalah hal terburuk yang dapat dilakukan aplikasi ini. Mahasiswa
            membuka pesan pribadi, tab lain, apa pun — dan tiga puluh sembilan
            orang menontonnya.

            Penandanya tembus klik dan tidak pernah menerima fokus, jadi ia tidak
            menghalangi pekerjaan; tetapi ia selalu terlihat selama siarannya
            berjalan.
        */
        _indicator = new PresentingIndicator(message.RequestedBy);
        _indicator.Show();

        return true;
    }

    public void Stop()
    {
        // Penanda ditutup lebih dulu. Kalau aliran yang dihentikan lebih dulu
        // dan penutupan penandanya gagal, mahasiswa melihat papan "sedang
        // disiarkan" pada layar yang sudah tidak disiarkan — dan menutup
        // hal-hal yang sebenarnya sudah aman.
        _indicator?.Close();
        _indicator = null;

        var stream = _stream;
        _stream = null;
        _ = stream?.DisposeAsync();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Papan kecil di puncak layar selama siaran berjalan.
///
/// Sengaja hijau, bukan merah: mahasiswa ini tidak sedang diawasi maupun
/// diambil alih — ia sedang menampilkan pekerjaannya, dan itu hal yang baik.
/// Merah akan membuatnya terasa seperti hukuman.
/// </summary>
internal sealed class PresentingIndicator : Window
{
    public PresentingIndicator(string requestedBy)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        var text = new System.Windows.Controls.TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };

        text.Inlines.Add(new System.Windows.Documents.Run("Layar Anda sedang ditampilkan ke kelas"));
        text.Inlines.Add(new System.Windows.Documents.Run($"   ·   diminta {requestedBy}")
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        });

        Content = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x6E, 0x50)),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Padding = new Thickness(18, 9, 18, 9),
            Child = text,
        };

        Loaded += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = 0;
        };

        /*
            Dikecualikan dari tangkapan layar.

            Tanpa ini, penandanya ikut tersiarkan dan muncul di layar seluruh
            kelas — termasuk di PC proyektor, yang lalu menampilkan papan
            "Layar Anda sedang ditampilkan ke kelas" kepada orang yang layarnya
            sama sekali tidak sedang ditampilkan.
        */
        SourceInitialized += (_, _) => CaptureExclusion.Apply(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }
}
