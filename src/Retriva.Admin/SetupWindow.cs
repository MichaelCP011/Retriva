using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Retriva.Shared.Config;
using Retriva.Ui;

namespace Retriva.Admin;

/// <summary>
/// Ditampilkan sekali, saat panel belum tahu ia bagian dari lab yang mana.
///
/// SATU PERTANYAAN, BUKAN SEBUAH FORMULIR PENGATURAN.
///
/// Yang perlu dijawab hanya "apakah mesin ini yang menjalankan koordinator".
/// Semua yang lain diturunkan darinya: mesin Superadmin membuka port dan
/// menyimpan basis data, mesin lain tidak membuka port apa pun. Menjadikannya
/// beberapa saklar terpisah membuat kombinasi mustahil dapat tersetel — panel
/// yang membuka port tetapi tidak punya data, misalnya — dan orang yang
/// menyetelnya tidak punya cara mengetahui bahwa ia baru saja melakukannya.
///
/// Kalau jawabannya "bukan", barulah alamat dan kode pendaftaran ditanyakan,
/// karena baru saat itulah keduanya berarti.
/// </summary>
public sealed class SetupWindow : ChromeWindow
{
    private static readonly Color Ink = Color.FromRgb(0xE8, 0xE8, 0xE8);
    private static readonly Color InkDim = Color.FromRgb(0x9A, 0x9A, 0x9A);
    private static readonly Color Line = Color.FromRgb(0x2E, 0x2E, 0x2E);
    private static readonly Color FieldBg = Color.FromRgb(0x16, 0x16, 0x16);
    private static readonly Color Accent = Color.FromRgb(0x3F, 0xBF, 0x95);

    private readonly RadioButton _asSuperadmin;
    private readonly TextBox _name;
    private readonly TextBlock _problem;

    public SetupWindow(PanelConfig panel) : base("Retriva", "Penyiapan", "admin")
    {
        // Dialog penyiapan tidak dimaksimalkan: isinya satu pertanyaan, dan
        // jendela selebar layar untuk satu pertanyaan terasa seperti aplikasi
        // yang gagal memuat isinya.
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        Width = 560;
        Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _asSuperadmin = Radio("Mesin ini yang menjalankan lab",
            "PC Asisten. Membuka jaringan lab, menyimpan data dan sertifikat, "
          + "serta menyetujui panel lain.");

        var asPanel = Radio("Mesin ini bergabung ke lab yang sudah ada",
            "Laptop dosen atau asisten. Penautannya dilakukan nanti dari dalam "
          + "aplikasi, di layar Panel admin.");

        var (nameWrap, nameBox) = Field("Nama panel ini", panel.DisplayName,
            "Nama inilah yang dilihat asisten saat menyetujui, dan yang muncul "
          + "di layar mahasiswa.");

        _name = nameBox;
        _asSuperadmin.IsChecked = true;

        _problem = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xB9, 0x5C)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var confirm = new Button
        {
            Content = "Lanjutkan",
            Height = 34,
            Margin = new Thickness(0, 18, 0, 0),
            Background = new SolidColorBrush(Accent),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E)),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        confirm.Click += (_, _) => Confirm(panel);

        var body = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        body.Children.Add(Heading("Retriva belum tahu lab mana ini"));
        body.Children.Add(Caption(
            "Jawaban ini menentukan apakah mesin ini membuka jaringan untuk lab, "
          + "atau menyambung ke mesin yang sudah melakukannya."));
        body.Children.Add(_asSuperadmin);
        body.Children.Add(asPanel);
        body.Children.Add(nameWrap);
        body.Children.Add(_problem);
        body.Children.Add(confirm);

        SetBody(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
    }

    private void Confirm(PanelConfig panel)
    {
        var displayName = _name.Text.Trim();
        if (displayName.Length == 0)
        {
            Fail("Nama panel tidak boleh kosong — nama inilah yang muncul di layar mahasiswa.");
            return;
        }

        panel.DisplayName = displayName;
        panel.IsSuperadmin = _asSuperadmin.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void Fail(string message)
    {
        _problem.Text = message;
        _problem.Visibility = Visibility.Visible;
    }

    /* ================= potongan tampilan ================= */

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Ink),
        FontSize = 19,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(InkDim),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 19,
        Margin = new Thickness(0, 0, 0, 18),
    };

    private static RadioButton Radio(string title, string detail)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Ink),
            FontSize = 13.5,
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = new SolidColorBrush(InkDim),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });

        return new RadioButton
        {
            Content = stack,
            Foreground = new SolidColorBrush(Ink),
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    /// <summary>
    /// Membuat satu isian beserta labelnya.
    ///
    /// Mengembalikan KEDUANYA, bukan hanya kotaknya. Pemanggil perlu kotaknya
    /// untuk membaca isi, tetapi yang boleh dipasang ke tata letak adalah
    /// pembungkusnya — satu elemen WPF hanya boleh punya satu induk, dan
    /// memasang kotak yang sudah berada di dalam pembungkus akan melempar
    /// pengecualian saat jendela dibuka.
    /// </summary>
    private static (StackPanel Wrap, TextBox Box) Field(string label, string value, string? hint = null)
    {
        var box = new TextBox
        {
            Text = value,
            Background = new SolidColorBrush(FieldBg),
            Foreground = new SolidColorBrush(Ink),
            BorderBrush = new SolidColorBrush(Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 7, 9, 7),
            FontSize = 13,
            CaretBrush = new SolidColorBrush(Ink),
        };

        // Label dan kotak dibungkus bersama supaya keduanya ikut disembunyikan
        // sekaligus. Disembunyikan terpisah, label akan tertinggal menggantung
        // di atas kotak yang sudah tidak ada.
        var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        wrap.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(InkDim),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 5),
        });
        wrap.Children.Add(box);

        if (hint is not null)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = hint,
                Foreground = new SolidColorBrush(InkDim),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return (wrap, box);
    }
}
