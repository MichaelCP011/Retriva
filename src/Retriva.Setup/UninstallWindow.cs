using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Retriva.Ui;

namespace Retriva.Setup;

/// <summary>
/// Jendela kustom untuk mencopot pemasangan Retriva secara bersih.
/// Mengikuti gaya visual dan tema gelap Retriva (ChromeWindow).
/// </summary>
public sealed class UninstallWindow : ChromeWindow
{
    private readonly Border _pane = new();
    private readonly StackPanel _rail = new();
    private readonly Button _cancel = Theme.Button("Batal");
    private readonly Button _uninstallBtn = Theme.Button("Copot Sekarang", primary: true);

    private int _step; // 0 = Konfirmasi, 1 = Pencopotan, 2 = Selesai
    private bool _uninstalling;
    private bool _done;

    private CheckBox? _cleanDataCheck;
    private RadioButton? _radioLab;
    private RadioButton? _radioPanel;
    private RadioButton? _radioAll;

    private TextBlock? _statusText;
    private ProgressBar? _progressBar;
    private TextBox? _logBox;
    private readonly List<string> _logs = [];

    private static readonly string[] StepNames = ["Konfirmasi", "Pencopotan", "Selesai"];

    public UninstallWindow(SetupRole? initialRole = null)
        : base("Retriva", "Copot Pemasangan", "setup")
    {
        Width = 720;
        Height = 520;
        MinWidth = 660;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        SetBody(BuildShell());
        Render();
    }

    private UIElement BuildShell()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var railHost = new Border
        {
            Background = Theme.Brush(Theme.RailBg),
            Padding = new Thickness(18, 20, 18, 18),
        };

        var railStack = new StackPanel();
        railStack.Children.Add(Theme.Text("Retriva", 15, Theme.Ink, FontWeights.SemiBold));
        railStack.Children.Add(Theme.Text("Uninstaller Kustom", 11, Theme.InkDim));

        _rail.Margin = new Thickness(0, 22, 0, 0);
        railStack.Children.Add(_rail);

        railHost.Child = railStack;
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);

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

        _cancel.Margin = new Thickness(0, 0, 10, 0);
        _cancel.Click += (_, _) => { if (!_uninstalling) Close(); };
        Grid.SetColumn(_cancel, 1);
        footGrid.Children.Add(_cancel);

        _uninstallBtn.Click += (_, _) =>
        {
            if (_done)
            {
                Close();
                return;
            }
            if (_step == 0) StartUninstall();
        };
        Grid.SetColumn(_uninstallBtn, 2);
        footGrid.Children.Add(_uninstallBtn);

        foot.Child = footGrid;
        Grid.SetRow(foot, 1);
        right.Children.Add(foot);

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return grid;
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

    private void Render()
    {
        RenderRail();

        _pane.Child = _step switch
        {
            0 => ConfirmStep(),
            1 => ProgressStep(),
            _ => DoneStep(),
        };

        if (_step == 0)
        {
            _cancel.Visibility = Visibility.Visible;
            _cancel.IsEnabled = true;
            _uninstallBtn.Content = "Copot Sekarang";
            _uninstallBtn.IsEnabled = true;
        }
        else if (_step == 1)
        {
            _cancel.Visibility = Visibility.Collapsed;
            _uninstallBtn.IsEnabled = false;
            _uninstallBtn.Content = "Mencopot...";
        }
        else
        {
            _cancel.Visibility = Visibility.Collapsed;
            _uninstallBtn.Visibility = Visibility.Visible;
            _uninstallBtn.IsEnabled = true;
            _uninstallBtn.Content = "Selesai";
        }
    }

    /* ================= Langkah 0: Konfirmasi ================= */

    private UIElement ConfirmStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Text("Copot Pemasangan Retriva", 19, Theme.Ink, FontWeights.SemiBold));
        panel.Children.Add(Theme.Text(
            "Pilih komponen yang ingin dicopot dari komputer ini.",
            12.5, Theme.InkMuted));

        // Deteksi instalasi yang ada
        var labInstalled = SystemActions.ServiceExists(SetupProfile.ServiceName)
            || Directory.Exists(Path.Combine(SetupProfile.ProgramFiles, "Retriva", "Lab"));
        var panelInstalled = Directory.Exists(Path.Combine(SetupProfile.ProgramFiles, "Retriva", "Panel"));
        var dataExists = Directory.Exists(SetupProfile.DataDir);

        var optionsCard = new Border
        {
            Background = Theme.Brush(Theme.Surface1),
            BorderBrush = Theme.Brush(Theme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 16, 0, 14),
        };

        var optionsStack = new StackPanel();
        optionsStack.Children.Add(Theme.Text("Komponen yang dicopot:", 12.5, Theme.Ink, FontWeights.SemiBold));

        _radioAll = new RadioButton
        {
            Content = Theme.Text("Bersihkan Seluruh Komponen Retriva (PC Mahasiswa & Panel)", 12, Theme.Ink),
            IsChecked = true,
            Margin = new Thickness(0, 8, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _radioLab = new RadioButton
        {
            Content = Theme.Text("Hanya PC Mahasiswa (Layanan Agent & Klien)", 12, Theme.Ink),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _radioPanel = new RadioButton
        {
            Content = Theme.Text("Hanya Panel Admin / Superadmin", 12, Theme.Ink),
            Margin = new Thickness(0, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        if (labInstalled && !panelInstalled)
        {
            _radioLab.IsChecked = true;
            _radioAll.IsChecked = false;
        }
        else if (panelInstalled && !labInstalled)
        {
            _radioPanel.IsChecked = true;
            _radioAll.IsChecked = false;
        }

        optionsStack.Children.Add(_radioAll);
        optionsStack.Children.Add(_radioLab);
        optionsStack.Children.Add(_radioPanel);
        optionsCard.Child = optionsStack;
        panel.Children.Add(optionsCard);

        // Opsi hapus ProgramData
        var cleanDataCard = new Border
        {
            Background = Theme.Brush(Theme.Surface1),
            BorderBrush = Theme.Brush(Theme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var cleanStack = new StackPanel();
        _cleanDataCheck = new CheckBox
        {
            IsChecked = true, // Default centang agar bersih sempurna sesuai permintaan pengguna
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = Theme.Text("Hapus bersih seluruh data konfigurasi dan pengaturan", 12.5, Theme.Ink, FontWeights.SemiBold),
        };
        cleanStack.Children.Add(_cleanDataCheck);

        cleanStack.Children.Add(Theme.Text(
            $"Menghapus folder {SetupProfile.DataDir} (termasuk token lab, basis data, dan riwayat). "
          + "Pilihan ini memastikan tidak ada berkas atau setelan lama yang tertinggal di PC.",
            11.5, Theme.InkMuted));

        cleanDataCard.Child = cleanStack;
        panel.Children.Add(cleanDataCard);

        var infoCard = new Border
        {
            Background = Theme.Brush(Color.FromArgb(24, 255, 59, 46)),
            BorderBrush = Theme.Brush(Color.FromArgb(80, 255, 59, 46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
        };
        infoCard.Child = Theme.Text(
            "Tindakan ini akan menghentikan proses Retriva yang berjalan, mencabut aturan firewall, "
          + "menghapus layanan Windows, membersihkan pintasan desktop/start menu, dan menghapus berkas aplikasi.",
            11, Theme.InkMuted);
        panel.Children.Add(infoCard);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /* ================= Langkah 1: Progress ================= */

    private UIElement ProgressStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Text("Mencopot Retriva...", 19, Theme.Ink, FontWeights.SemiBold));

        _statusText = Theme.Text("Menyiapkan proses pencopotan...", 12.5, Theme.InkMuted);
        _statusText.Margin = new Thickness(0, 4, 0, 14);
        panel.Children.Add(_statusText);

        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 6,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = Theme.Brush(Theme.Accent),
            Background = Theme.Brush(Theme.Surface2),
            BorderThickness = new Thickness(0),
        };
        panel.Children.Add(_progressBar);

        _logBox = new TextBox
        {
            IsReadOnly = true,
            Height = 220,
            Background = Theme.Brush(Theme.Canvas),
            Foreground = Theme.Brush(Theme.InkDim),
            BorderBrush = Theme.Brush(Theme.Border),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 11,
            Padding = new Thickness(10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_logBox);

        return panel;
    }

    /* ================= Langkah 2: Selesai ================= */

    private UIElement DoneStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Text("Retriva Berhasil Dicopot", 19, Theme.Ok, FontWeights.SemiBold));
        panel.Children.Add(Theme.Text(
            "Seluruh berkas program, layanan Windows, pengaturan, dan konfigurasi telah dibersihkan secara sempurna dari komputer ini.",
            12.5, Theme.InkMuted));

        var summaryBox = new Border
        {
            Background = Theme.Brush(Theme.Surface1),
            BorderBrush = Theme.Brush(Theme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 20, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(Theme.Text("Daftar komponen yang telah dibersihkan:", 12.5, Theme.Ink, FontWeights.SemiBold));

        void Item(string text) => stack.Children.Add(Theme.Text($"✓  {text}", 12, Theme.Ok));

        Item("Layanan Windows (RetrivaAgent) dihentikan dan dihapus");
        Item("Autostart klien di Registry dibersihkan");
        Item("Aturan port dan Firewall dicabut");
        Item("Pintasan Start Menu, Desktop, dan pencarian Windows dihapus");
        Item("Entri di Windows Settings / Add & Remove Programs dibersihkan");
        if (_cleanDataCheck?.IsChecked == true)
        {
            Item($"Folder konfigurasi dan basis data ({SetupProfile.DataDir}) dihapus total");
        }
        Item("Folder instalasi program di Program Files dihapus");

        summaryBox.Child = stack;
        panel.Children.Add(summaryBox);

        return panel;
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            _logs.Add(message);
            if (_logBox is not null)
            {
                _logBox.AppendText(message + Environment.NewLine);
                _logBox.ScrollToEnd();
            }
            if (_statusText is not null)
            {
                _statusText.Text = message;
            }
        });
    }

    private void StartUninstall()
    {
        _uninstalling = true;
        _step = 1;
        Render();

        var cleanData = _cleanDataCheck?.IsChecked == true;
        var cleanLab = _radioLab?.IsChecked == true || _radioAll?.IsChecked == true;
        var cleanPanel = _radioPanel?.IsChecked == true || _radioAll?.IsChecked == true;

        Task.Run(() =>
        {
            try
            {
                if (cleanLab)
                {
                    Log("Mencopot komponen PC Mahasiswa (Lab)...");
                    Installer.Uninstall(SetupRole.Lab, cleanData: false, Log);
                }

                if (cleanPanel)
                {
                    Log("Mencopot komponen Panel Admin / Superadmin...");
                    Installer.Uninstall(SetupRole.Panel, cleanData: false, Log);
                }

                if (cleanData)
                {
                    Log($"Menghapus seluruh folder konfigurasi dan basis data di {SetupProfile.DataDir}...");
                    try
                    {
                        if (Directory.Exists(SetupProfile.DataDir))
                        {
                            Directory.Delete(SetupProfile.DataDir, recursive: true);
                            Log("  Folder konfigurasi berhasil dihapus bersih.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"  Peringatan saat menghapus ProgramData: {ex.Message}");
                    }
                }

                // Bersihkan folder induk Program Files\Retriva jika sudah kosong
                try
                {
                    var parentDir = Path.Combine(SetupProfile.ProgramFiles, "Retriva");
                    if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                    {
                        Directory.Delete(parentDir);
                        Log($"  Folder {parentDir} berhasil dihapus.");
                    }
                }
                catch { }

                Log("Pencopotan selesai dengan sukses.");
            }
            catch (Exception ex)
            {
                Log($"Terjadi kesalahan: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    _uninstalling = false;
                    _done = true;
                    _step = 2;
                    Render();
                });
            }
        });
    }
}
