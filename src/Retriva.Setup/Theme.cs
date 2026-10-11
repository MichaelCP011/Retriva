using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

/*
    Alias untuk kontrol Border WPF.

    Kelas ini punya field warna bernama Border — mengikuti nama token di
    ui/src/styles.css — dan nama itu MENUTUPI nama tipe System.Windows.Controls.Border
    di seluruh berkas ini. Akibatnya "Border.CornerRadiusProperty" dibaca sebagai
    properti pada sebuah Color, dan kompilernya mengeluh tentang Color yang tidak
    punya CornerRadiusProperty — galat yang tidak menyebut penyebabnya sama sekali.

    Alias dipilih alih-alih mengganti nama field, karena kesamaan nama dengan
    token CSS-nya justru yang membuat keduanya dapat ditemukan bersama.

    Field Canvas menyimpan jebakan yang sama terhadap System.Windows.Controls.Canvas.
    Kontrol itu belum dipakai di berkas ini, jadi belum ada aliasnya — yang
    memakainya nanti akan bertemu galat yang sama persis, dan catatan ini yang
    menjelaskannya.
*/
using WpfBorder = System.Windows.Controls.Border;

namespace Retriva.Setup;

/// <summary>
/// Token warna dan kontrol dasar, disalin dari sistem desain Retriva.
///
/// DISALIN, BUKAN DIRUJUK — dan itu perlu dijelaskan.
///
/// Warna aslinya tinggal di ui/src/styles.css sebagai variabel CSS, dan dibaca
/// oleh WebView2. Installer tidak memuat WebView2 sama sekali: menariknya masuk
/// hanya untuk empat layar berarti menambah seratus megabyte ke berkas yang
/// harus disalin ke empat puluh PC, dan menambah satu lagi hal yang bisa gagal
/// pada mesin yang runtime-nya belum lengkap.
///
/// Harganya nyata: warna di sini tidak ikut berubah ketika tema di CSS diubah.
/// Karena itu nilainya ditulis dengan nama yang sama persis seperti di CSS,
/// supaya yang mengubah salah satunya dapat menemukan pasangannya.
/// </summary>
public static class Theme
{
    // Sama dengan blok [data-theme="dark"] di ui/src/styles.css.
    public static readonly Color Canvas = Rgb(0x1B, 0x1B, 0x1B);
    public static readonly Color Surface1 = Rgb(0x25, 0x25, 0x25);
    public static readonly Color Surface2 = Rgb(0x30, 0x30, 0x30);
    public static readonly Color Border = Rgb(0x40, 0x40, 0x40);
    public static readonly Color BorderSoft = Rgb(0x33, 0x33, 0x33);
    public static readonly Color Ink = Rgb(0xEB, 0xEB, 0xEB);
    public static readonly Color InkMuted = Rgb(0xA8, 0xA8, 0xA8);
    public static readonly Color InkDim = Rgb(0x7E, 0x7E, 0x7E);
    public static readonly Color Accent = Rgb(0xE0, 0x0D, 0x00);
    public static readonly Color AccentHover = Rgb(0xFA, 0x0F, 0x00);
    public static readonly Color Ok = Rgb(0x3F, 0xBF, 0x95);
    public static readonly Color Warn = Rgb(0xE0, 0xB9, 0x5C);
    public static readonly Color Bad = Rgb(0xFF, 0x3B, 0x2E);
    public static readonly Color RailBg = Rgb(0x14, 0x14, 0x14);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    public static SolidColorBrush Brush(Color c) => new(c);

    public static TextBlock Text(string content, double size = 12.5, Color? color = null,
                                FontWeight? weight = null, bool wrap = true)
        => new()
        {
            Text = content,
            FontSize = size,
            Foreground = Brush(color ?? Ink),
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };

    public static Button Button(string label, bool primary = false)
    {
        var b = new Button
        {
            Content = label,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(16, 7, 16, 7),
            BorderThickness = new Thickness(1),
            Foreground = Brush(primary ? Colors.White : Ink),
            Background = Brush(primary ? Accent : Surface1),
            BorderBrush = Brush(primary ? Accent : Border),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        // Template diganti supaya sudutnya mengikuti radius 4px sistem desain,
        // dan supaya latar tombol tidak dikembalikan ke abu bawaan WPF saat
        // kursor lewat — perilaku yang membuat tombol merah berkedip putih.
        b.Template = RoundedButtonTemplate(primary);
        return b;
    }

    private static ControlTemplate RoundedButtonTemplate(bool primary)
    {
        var border = new FrameworkElementFactory(typeof(WpfBorder));
        border.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(WpfBorder.BackgroundProperty,
            new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(WpfBorder.BorderBrushProperty,
            new System.Windows.Data.Binding("BorderBrush")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(WpfBorder.BorderThicknessProperty,
            new System.Windows.Data.Binding("BorderThickness")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(WpfBorder.PaddingProperty, new Thickness(0));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 7, 16, 7));
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,
            Brush(primary ? AccentHover : Surface2)));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        return template;
    }

    public static TextBox Field(string value = "")
        => new()
        {
            Text = value,
            FontSize = 12.5,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brush(Canvas),
            Foreground = Brush(Ink),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(1),
            CaretBrush = Brush(Ink),
        };

    public static StackPanel Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(Text(label, 11.5, InkDim));
        ((FrameworkElement)control).Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(control);
        return panel;
    }
}
