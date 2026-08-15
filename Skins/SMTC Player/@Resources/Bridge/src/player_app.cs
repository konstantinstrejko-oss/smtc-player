// Shard Player — оболочка режима трея: иконка, окно плеера, журнал.
//
// Ассеты (шрифты, шейдер, шум) вшиты в exe и распаковываются рядом с ним: так
// один и тот же файл работает и внутри пакета Rainmeter, и сам по себе из zip.
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class Assets
{
    /// <summary>Куда распакованы ассеты. Пусто, если распаковать не удалось.</summary>
    public static string Dir = "";

    static readonly string[] Files = {
        "Fonts/Aquatico.otf",
        "Fonts/Quicksand.otf",
        "Glass/glass.ps",
        "Glass/noise.png"
    };

    public static void Unpack()
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RainmeterSMTC", "res");

            var asm = Assembly.GetExecutingAssembly();
            foreach (string rel in Files)
            {
                string target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));

                using (Stream src = asm.GetManifestResourceStream(rel.Replace('/', '.')))
                {
                    if (src == null) continue;
                    // перезаписываем только при несовпадении длины: файл может быть занят
                    if (File.Exists(target) && new FileInfo(target).Length == src.Length) continue;
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write))
                    {
                        var buf = new byte[16384];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                    }
                }
            }
            Dir = root;
        }
        catch { Dir = ""; }
    }
}

public class ShardApp : Application
{
    TrayIcon _tray;
    PlayerWindow _window;
    HistoryWindow _history;
    DispatcherTimer _unload;

    public ShardApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Assets.Unpack();
        PlayerWindow.ResourceDir = Assets.Dir;

        _tray = new TrayIcon();
        _tray.ToggleWindow += Toggle;
        _tray.HistoryRequested += ShowHistory;
        _tray.LayoutChanged += Relayout;
        _tray.ExitRequested += delegate
        {
            _tray.Dispose();
            Shutdown();
        };

        // -Show и -History открывают окна сразу: иначе их никак не проверить,
        // кроме как руками через иконку в трее.
        foreach (string a in Environment.GetCommandLineArgs())
        {
            if (a.Equals("-Show", StringComparison.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Toggle));
            else if (a.Equals("-History", StringComparison.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowHistory));
        }

        // Окно тяжелее фонового процесса впятеро, поэтому живёт только пока нужно.
        _unload = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(5, Settings.GetInt("UnloadSeconds", 60)))
        };
        _unload.Tick += delegate
        {
            _unload.Stop();
            if (_window == null || _window.IsVisible) return;

            _window.Close();
            _window = null;

            // WPF отдаёт немало памяти только после сборки; без этого процесс
            // так и висит со ~140 МБ, хотя окна уже нет
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Trim();
        };
    }

    void Toggle()
    {
        if (_window != null && _window.IsVisible) { _window.HideSmooth(); _unload.Start(); return; }

        _unload.Stop();
        if (_window == null)
        {
            _window = new PlayerWindow();
            // окно закрывается ещё по Esc и по потере фокуса — без этой подписки
            // отсчёт выгрузки в тех случаях просто не начинался
            _window.IsVisibleChanged += delegate
            {
                if (_window == null) return;
                if (_window.IsVisible) _unload.Stop(); else _unload.Start();
            };
        }
        _window.ShowSmooth();
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    static extern int EmptyWorkingSet(IntPtr process);

    static void Trim()
    {
        try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); }
        catch { }
    }

    void Relayout()
    {
        if (_window == null) return;
        bool vertical = Settings.Get("Layout", "horizontal") == "vertical";
        _window.ApplyLayout(vertical);
        // размер изменился — фон под окном надо снять заново
        if (_window.IsVisible) { _window.Hide(); _window.ShowSmooth(); }
    }

    void ShowHistory()
    {
        if (_history == null)
        {
            _history = new HistoryWindow();
            _history.Closed += delegate { _history = null; };
        }
        _history.Reload();
        _history.Show();
        _history.Activate();
    }
}

/// <summary>
/// Журнал прослушанного. Только просмотр: переключиться на прошлый трек нельзя —
/// SMTC такого не умеет, и притворяться незачем.
/// </summary>
public class HistoryWindow : Window
{
    readonly ListBox _list;

    public HistoryWindow()
    {
        Title = "Shard Player — история";
        Width = 420;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromArgb(255, 22, 22, 27));
        ResizeMode = ResizeMode.CanResize;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Content = _list;

        // иначе системная рамка остаётся светлой поверх тёмного списка
        SourceInitialized += delegate
        {
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(h, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, 4);
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    public void Reload()
    {
        _list.Items.Clear();
        var entries = History.Read(200);
        if (entries.Count == 0)
        {
            _list.Items.Add(new TextBlock
            {
                Text = "Пока пусто. Трек попадает сюда после 30 секунд звучания.",
                Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                Margin = new Thickness(10)
            });
            return;
        }

        foreach (var e in entries) _list.Items.Add(Row(e));
    }

    static UIElement Row(History.Entry e)
    {
        var grid = new Grid { Margin = new Thickness(4, 5, 4, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var frame = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Margin = new Thickness(0, 0, 10, 0)
        };
        if (!string.IsNullOrEmpty(e.Cover) && File.Exists(e.Cover))
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(File.ReadAllBytes(e.Cover));
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                frame.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
            }
            catch { }
        }
        Grid.SetColumn(frame, 0);
        grid.Children.Add(frame);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = e.Title,
            Foreground = Brushes.White,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = e.Artist,
            Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var when = new TextBlock
        {
            Text = e.When.Length >= 16 ? e.When.Substring(5, 11) : e.When,
            Foreground = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0)
        };
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        return grid;
    }
}
