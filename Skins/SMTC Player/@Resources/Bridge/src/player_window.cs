// Окно плеера: стекло, две раскладки, перемотка и громкость.
//
// Прозрачность берётся от DWM, а не от WPF: AllowsTransparency=true переводит
// WPF в программный рендеринг, где пиксельные шейдеры не рисуются вообще.
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using D = System.Drawing;
// System.Windows.Shapes.Path — это фигура, а не путь к файлу; развести их явно
using Path = System.Windows.Shapes.Path;
using IOPath = System.IO.Path;

// ---------------------------------------------------------------- шейдер

public class GlassShader : ShaderEffect
{
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(GlassShader), 0);
    public static readonly DependencyProperty NoiseProperty =
        RegisterPixelShaderSamplerProperty("Noise", typeof(GlassShader), 1);
    public static readonly DependencyProperty AmountProperty =
        DependencyProperty.Register("Amount", typeof(double), typeof(GlassShader),
            new UIPropertyMetadata(0.02, PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register("Phase", typeof(double), typeof(GlassShader),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));

    public GlassShader(string shaderPath, string noisePath)
    {
        PixelShader = new PixelShader { UriSource = new Uri(shaderPath) };
        Noise = new ImageBrush(new BitmapImage(new Uri(noisePath)))
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1)
        };
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(NoiseProperty);
        UpdateShaderValue(AmountProperty);
        UpdateShaderValue(PhaseProperty);
    }

    public Brush Input { get { return (Brush)GetValue(InputProperty); } set { SetValue(InputProperty, value); } }
    public Brush Noise { get { return (Brush)GetValue(NoiseProperty); } set { SetValue(NoiseProperty, value); } }
    public double Amount { get { return (double)GetValue(AmountProperty); } set { SetValue(AmountProperty, value); } }
    public double Phase { get { return (double)GetValue(PhaseProperty); } set { SetValue(PhaseProperty, value); } }
}

// ---------------------------------------------------------------- окно

public class PlayerWindow : Window
{
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WCD d);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    [StructLayout(LayoutKind.Sequential)] struct ACCENTPOLICY { public int State, Flags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WCD { public int Attribute; public IntPtr Data; public int SizeOfData; }

    const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    const int WCA_ACCENT_POLICY = 19;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public static string ResourceDir = "";

    // слои и элементы
    Border _glassHost, _tint;
    Image _glassImage;
    GlassShader _shader;
    Image _cover;
    Border _coverFrame;
    TextBlock _title, _artist, _posText, _durText;
    Canvas _titleClip;
    Grid _root, _content;
    ProgressBarLite _progress, _volume;
    Path _playIcon, _volWave;
    StackPanel _volumeBox;
    bool _muted;

    string _coverShown = "";
    bool _vertical;
    bool _draggingSeek;
    bool _fallbackAcrylic;
    D.Color _accent = D.Color.FromArgb(255, 90, 110, 170);

    static FontFamily _display, _text;

    public PlayerWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        LoadFonts();
        Build();
        ApplyLayout(Settings.Get("Layout", "horizontal") == "vertical");

        PlayerState.Changed += OnStateChanged;
        Deactivated += delegate { if (!_draggingSeek) HideSmooth(); };
        KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Escape) HideSmooth(); };
        Closed += delegate { PlayerState.Changed -= OnStateChanged; };
    }

    // ------------------------------------------------------------ шрифты

    static void LoadFonts()
    {
        if (_display != null) return;
        try
        {
            string dir = IOPath.Combine(ResourceDir, "Fonts");
            if (Directory.Exists(dir))
            {
                string uri = "file:///" + dir.Replace('\\', '/').TrimEnd('/') + "/";
                _display = new FontFamily(new Uri(uri), "./#Aquatico");
                _text = new FontFamily(new Uri(uri), "./#Quicksand");
            }
        }
        catch { }
        if (_display == null) _display = new FontFamily("Segoe UI");
        if (_text == null) _text = new FontFamily("Segoe UI");
    }

    // ------------------------------------------------------------ сборка

    void Build()
    {
        _root = new Grid();

        // 1. стекло: настоящий фон из-под окна, размытый и искажённый шейдером
        _glassImage = new Image
        {
            Stretch = Stretch.Fill,
            Effect = new BlurEffect { Radius = 26, KernelType = KernelType.Gaussian }
        };
        _glassHost = new Border { Child = _glassImage, ClipToBounds = true };

        string shaderPath = IOPath.Combine(ResourceDir, "Glass", "glass.ps");
        string noisePath = IOPath.Combine(ResourceDir, "Glass", "noise.png");
        if (File.Exists(shaderPath) && File.Exists(noisePath))
        {
            try
            {
                _shader = new GlassShader(new Uri(shaderPath).AbsoluteUri, new Uri(noisePath).AbsoluteUri)
                {
                    Amount = 0.032
                };
                _glassHost.Effect = _shader;
            }
            catch { _shader = null; }   // без искажения, но со стеклом
        }
        _root.Children.Add(_glassHost);

        // 2. тинт от обложки — стекло меняет оттенок вместе с треком
        _tint = new Border { Background = new SolidColorBrush(Color.FromArgb(0x38, 90, 110, 170)) };
        _root.Children.Add(_tint);

        // 3. затемнение — иначе текст на светлом фоне не читается
        _root.Children.Add(new Border
        {
            Background = new LinearGradientBrush(
                Color.FromArgb(105, 10, 10, 14), Color.FromArgb(175, 6, 6, 10), 90)
        });

        // 3. содержимое
        _content = new Grid { Margin = new Thickness(16) };
        BuildContent();
        _root.Children.Add(_content);

        // 4. фаска — как inset-блик в CSS
        _root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.2),
            IsHitTestVisible = false,
            BorderBrush = new LinearGradientBrush(
                Color.FromArgb(120, 255, 255, 255), Color.FromArgb(25, 255, 255, 255), 90)
        });

        Content = _root;
    }

    void BuildContent()
    {
        _cover = new Image { Stretch = Stretch.UniformToFill };
        _coverFrame = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _cover,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.55,
                Color = Colors.Black
            }
        };

        _title = new TextBlock
        {
            FontFamily = _text,
            FontSize = 15,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.NoWrap
        };
        _titleClip = new Canvas { ClipToBounds = true, Height = 21 };
        _titleClip.Children.Add(_title);

        // Quicksand.otf содержит только жирное начертание, поэтому второстепенный
        // текст набираем Aquatico — у него есть обычное.
        _artist = new TextBlock
        {
            FontFamily = _display,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromArgb(175, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _posText = Mono();
        _durText = Mono();
        _durText.HorizontalAlignment = HorizontalAlignment.Right;

        _progress = new ProgressBarLite(3.5);
        _progress.Seek += OnSeek;
        _progress.DragState += delegate (bool on) { _draggingSeek = on; };

        _volume = new ProgressBarLite(3.0) { Width = 78 };
        _volume.Seek += delegate (double v) { AppVolume.Set(PlayerState.AppId, (float)v); UpdateVolume(); };

        // Корпус динамика и «волны» разведены: волны рисуются обводкой, и при
        // нуле подменяются перечёркиванием — как принято во всех плеерах.
        var speaker = new Grid { Width = 17, Height = 12, Margin = new Thickness(0, 0, 7, 0),
                                 VerticalAlignment = VerticalAlignment.Center };
        speaker.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,4 L3,4 L7,0 L7,12 L3,8 L0,8 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
        });
        _volWave = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        speaker.Children.Add(_volWave);
        SetMuted(false);

        _volumeBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _volumeBox.Children.Add(speaker);
        _volumeBox.Children.Add(_volume);
    }

    TextBlock Mono()
    {
        return new TextBlock
        {
            FontFamily = _display,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255))
        };
    }

    // ------------------------------------------------------------ кнопки

    UIElement Buttons(double scale)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(IconButton("M9,0 L9,12 L1,6 Z M0,0 L1.6,0 L1.6,12 L0,12 Z", 0.82 * scale,
                                      delegate { PlayerState.Send("prev"); }));
        _playIcon = new Path
        {
            Fill = Brushes.White,
            Data = Geometry.Parse("M0,0 L13,7 L0,14 Z")
        };
        panel.Children.Add(Wrap(_playIcon, 1.0 * scale, delegate { PlayerState.Send("playpause"); }));
        panel.Children.Add(IconButton("M0,0 L8,6 L0,12 Z M8.4,0 L10,0 L10,12 L8.4,12 Z", 0.82 * scale,
                                      delegate { PlayerState.Send("next"); }));
        return panel;
    }

    UIElement IconButton(string geometry, double scale, Action click)
    {
        return Wrap(new Path { Fill = Brushes.White, Data = Geometry.Parse(geometry) }, scale, click);
    }

    UIElement Wrap(Path icon, double scale, Action click)
    {
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        icon.RenderTransform = new ScaleTransform(scale, scale);

        var host = new Border
        {
            Background = Brushes.Transparent,       // иначе не ловятся клики по пустоте
            Padding = new Thickness(11, 7, 11, 7),
            Cursor = Cursors.Hand,
            Child = icon,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var grow = new ScaleTransform(1, 1);
        host.RenderTransform = grow;

        host.MouseEnter += delegate { Spring(grow, 1.16); };
        host.MouseLeave += delegate { Spring(grow, 1.0); };
        host.MouseLeftButtonDown += delegate { Spring(grow, 0.9); };
        host.MouseLeftButtonUp += delegate { Spring(grow, 1.16); click(); };
        return host;
    }

    // Упругий перелёт: WPF KeySpline за единицу не выходит, поэтому BackEase.
    static void Spring(ScaleTransform t, double to)
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ------------------------------------------------------------ раскладки

    public void ApplyLayout(bool vertical)
    {
        _vertical = vertical;
        _content.Children.Clear();
        _content.ColumnDefinitions.Clear();
        _content.RowDefinitions.Clear();

        if (vertical) LayoutVertical(); else LayoutHorizontal();

        Width = vertical ? 300 : 440;
        Height = vertical ? 400 : 150;
        Place();
    }

    void LayoutHorizontal()
    {
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _coverFrame.Width = _coverFrame.Height = 102;
        _coverFrame.VerticalAlignment = VerticalAlignment.Center;
        _coverFrame.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(_coverFrame, 0);
        _content.Children.Add(_coverFrame);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_titleClip, 0);
        right.Children.Add(_titleClip);

        _artist.Margin = new Thickness(0, 1, 0, 0);
        Grid.SetRow(_artist, 1);
        right.Children.Add(_artist);

        var mid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        mid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _progress.Margin = new Thickness(0, 0, 0, 4);
        Grid.SetRow(_progress, 0);
        mid.Children.Add(_progress);

        var times = new Grid();
        Grid.SetRow(times, 1);
        times.Children.Add(_posText);
        times.Children.Add(_durText);
        mid.Children.Add(times);
        Grid.SetRow(mid, 2);
        right.Children.Add(mid);

        var bottom = new Grid();
        _volumeBox.HorizontalAlignment = HorizontalAlignment.Left;
        bottom.Children.Add(_volumeBox);
        var buttons = Buttons(1.0);
        ((FrameworkElement)buttons).HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 3);
        right.Children.Add(bottom);

        Grid.SetColumn(right, 1);
        _content.Children.Add(right);
    }

    void LayoutVertical()
    {
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _coverFrame.Width = _coverFrame.Height = 210;
        _coverFrame.HorizontalAlignment = HorizontalAlignment.Center;
        _coverFrame.VerticalAlignment = VerticalAlignment.Top;
        _coverFrame.Margin = new Thickness(0, 4, 0, 18);
        Grid.SetRow(_coverFrame, 0);
        _content.Children.Add(_coverFrame);

        _title.TextAlignment = TextAlignment.Center;
        Grid.SetRow(_titleClip, 1);
        _content.Children.Add(_titleClip);

        _artist.TextAlignment = TextAlignment.Center;
        _artist.HorizontalAlignment = HorizontalAlignment.Center;
        _artist.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetRow(_artist, 2);
        _content.Children.Add(_artist);

        var mid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        mid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _progress.Margin = new Thickness(0, 0, 0, 4);
        Grid.SetRow(_progress, 0);
        mid.Children.Add(_progress);
        var times = new Grid();
        Grid.SetRow(times, 1);
        times.Children.Add(_posText);
        times.Children.Add(_durText);
        mid.Children.Add(times);
        Grid.SetRow(mid, 3);
        _content.Children.Add(mid);

        var buttons = Buttons(1.15);
        Grid.SetRow(buttons, 4);
        _content.Children.Add(buttons);

        _volumeBox.HorizontalAlignment = HorizontalAlignment.Center;
        _volumeBox.Margin = new Thickness(0, 12, 0, 2);
        _volume.Width = 150;
        Grid.SetRow(_volumeBox, 5);
        _content.Children.Add(_volumeBox);
    }

    // ------------------------------------------------------------ стекло

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr h = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;

        int dark = 1; DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        int round = 2; DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        // Акрил включается только как запасной путь: обычно фон рисуем сами
        // снимком, и системное размытие под ним всё равно не видно.
    }

    /// <summary>Тинт стекла — доминирующий цвет обложки. Стекло «дышит» с треком.</summary>
    void ApplyAccent()
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;

        // порядок байтов политики: AABBGGRR
        int tint = (0x50 << 24) | (_accent.B << 16) | (_accent.G << 8) | _accent.R;
        var policy = new ACCENTPOLICY
        {
            State = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            Flags = 2,
            GradientColor = tint,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(policy);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, p, false);
            var data = new WCD { Attribute = WCA_ACCENT_POLICY, Data = p, SizeOfData = size };
            if (SetWindowCompositionAttribute(h, ref data) == 0)
            {
                // API не сработал — рисуем сплошной тёмный фон, всё остальное живо
                _root.Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 24));
            }
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ------------------------------------------------------------ данные

    void OnStateChanged()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(Apply));
    }

    public void Apply()
    {
        if (!IsVisible) return;

        string title = PlayerState.Title;
        if (string.IsNullOrEmpty(title) || title == "—") title = "Ничего не играет";
        if (_title.Text != title) { _title.Text = title; StartMarquee(); }

        _artist.Text = PlayerState.Artist;
        _posText.Text = Time(PlayerState.PosSec);
        _durText.Text = Time(PlayerState.DurSec);

        if (!_draggingSeek) _progress.Value = PlayerState.Progress;
        _progress.Enabled = PlayerState.CanSeek;

        _playIcon.Data = PlayerState.State == 1
            ? Geometry.Parse("M0,0 L4,0 L4,14 L0,14 Z M7,0 L11,0 L11,14 L7,14 Z")
            : Geometry.Parse("M0,0 L13,7 L0,14 Z");

        if (PlayerState.CoverPath != _coverShown)
        {
            _coverShown = PlayerState.CoverPath;
            LoadCover(_coverShown);
        }

        UpdateVolume();
    }

    void UpdateVolume()
    {
        float v = AppVolume.Get(PlayerState.AppId);
        if (v < 0) { _volumeBox.Visibility = Visibility.Collapsed; return; }
        _volumeBox.Visibility = Visibility.Visible;
        _volume.Value = v;
        SetMuted(v < 0.005f);
    }

    void SetMuted(bool muted)
    {
        if (_volWave == null) return;
        if (muted == _muted && _volWave.Data != null) return;
        _muted = muted;
        _volWave.Data = Geometry.Parse(muted
            ? "M10,3.5 L15.5,8.5 M15.5,3.5 L10,8.5"          // перечёркнуто
            : "M9.5,3.6 A4.4,4.4 0 0 1 9.5,8.4 M12,1.8 A7.6,7.6 0 0 1 12,10.2");
    }

    void LoadCover(string path)
    {
        BitmapImage img = null;
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                img = new BitmapImage();
                img.BeginInit();
                // мост перезаписывает файл на ходу — читаем копию в память
                img.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
            }
        }
        catch { img = null; }

        _cover.Source = img;
        _coverFrame.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;

        var fresh = Palette.Dominant(path, D.Color.FromArgb(255, 90, 110, 170));
        if (fresh.ToArgb() != _accent.ToArgb())
        {
            _accent = fresh;
            _tint.Background = new SolidColorBrush(Color.FromArgb(0x38, _accent.R, _accent.G, _accent.B));
            var glow = Color.FromArgb(150, _accent.R, _accent.G, _accent.B);
            _progress.Accent = glow;
            _volume.Accent = glow;
        }
    }

    static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(Math.Floor(seconds));
        if (ts.TotalHours >= 1)
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:d2}:{2:d2}",
                                 (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:d2}", (int)ts.TotalMinutes, ts.Seconds);
    }

    void OnSeek(double fraction)
    {
        PlayerState.Send("seek:" + (fraction * 100).ToString("F1", CultureInfo.InvariantCulture));
    }

    // Длинное название не обрезаем, а плавно катаем туда-обратно.
    void StartMarquee()
    {
        _title.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(_title, 0);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(delegate
        {
            double overflow = _title.ActualWidth - _titleClip.ActualWidth;
            if (overflow <= 4 || _titleClip.ActualWidth <= 0) return;

            var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow - 6,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 + overflow / 26)), ease));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow - 6,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4 + overflow / 26))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4 + 2 * overflow / 26)), ease));
            _title.BeginAnimation(Canvas.LeftProperty, a);
        }));
    }

    // ------------------------------------------------------------ показ

    /// <summary>
    /// Снимок того, что лежит под будущим окном. Это единственный способ получить
    /// настоящий фон для искажения: размытие, которое делает DWM, принадлежит
    /// системе, и WPF до его пикселей не дотягивается.
    ///
    /// Берётся один раз, пока окно ещё скрыто: BitBlt области 440×150 — это
    /// пара миллисекунд, в цикле ничего не крутится.
    /// </summary>
    BitmapSource CaptureBehind()
    {
        try
        {
            double scale = 1.0;
            using (var probe = D.Graphics.FromHwnd(IntPtr.Zero)) scale = probe.DpiX / 96.0;

            int x = (int)Math.Round(Left * scale);
            int y = (int)Math.Round(Top * scale);
            int w = (int)Math.Round(Width * scale);
            int h = (int)Math.Round(Height * scale);
            if (w < 2 || h < 2) return null;

            using (var bmp = new D.Bitmap(w, h, D.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = D.Graphics.FromImage(bmp))
                    g.CopyFromScreen(x, y, 0, 0, new D.Size(w, h), D.CopyPixelOperation.SourceCopy);

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, D.Imaging.ImageFormat.Bmp);
                    ms.Position = 0;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.StreamSource = ms;
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
        }
        catch { return null; }
    }

    /// <summary>Ставит окно в угол рабочей области — там же, где трей.</summary>
    void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 14;
        Top = wa.Bottom - Height - 14;
    }

    public void ShowSmooth()
    {
        Place();

        // строго до Show(), иначе окно снимет само себя
        var behind = CaptureBehind();
        _glassImage.Source = behind;
        _fallbackAcrylic = behind == null;

        Show();
        Activate();
        if (_fallbackAcrylic) ApplyAccent();   // не сняли фон — пусть размывает DWM
        Apply();

        _content.Opacity = 0;
        var slide = new TranslateTransform(0, 14);
        _root.RenderTransform = slide;

        _content.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(190)));
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(340))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 }
            });

        if (_shader != null)
            _shader.BeginAnimation(GlassShader.PhaseProperty,
                new DoubleAnimation(0, 24, TimeSpan.FromSeconds(30)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    public void HideSmooth()
    {
        if (!IsVisible) return;
        if (_shader != null) _shader.BeginAnimation(GlassShader.PhaseProperty, null);

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += delegate { Hide(); };
        _content.BeginAnimation(OpacityProperty, fade);
    }
}

// ---------------------------------------------------------------- полоса

/// <summary>
/// Полоса прогресса и громкости: тонкая линия, наполнение акцентным цветом,
/// клик и перетаскивание по всей ширине.
/// </summary>
public class ProgressBarLite : Grid
{
    readonly Border _track, _fill;
    readonly Ellipse _knob;
    double _value;
    bool _drag;

    public event Action<double> Seek;
    public event Action<bool> DragState;

    public bool Enabled = true;

    public ProgressBarLite(double thickness)
    {
        Height = 14;
        Background = Brushes.Transparent;   // ловим клик по всей высоте, не только по линии
        Cursor = Cursors.Hand;

        _track = new Border
        {
            Height = thickness,
            CornerRadius = new CornerRadius(thickness / 2),
            Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _fill = new Border
        {
            Height = thickness,
            CornerRadius = new CornerRadius(thickness / 2),
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 0
        };
        _knob = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false
        };

        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_knob);

        MouseEnter += delegate { _knob.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))); };
        MouseLeave += delegate { if (!_drag) _knob.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160))); };

        MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
        {
            if (!Enabled) return;
            _drag = true;
            CaptureMouse();
            if (DragState != null) DragState(true);
            Commit(e.GetPosition(this).X);
        };
        MouseMove += delegate (object s, MouseEventArgs e)
        {
            if (!_drag) return;
            Preview(e.GetPosition(this).X);
        };
        MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
        {
            if (!_drag) return;
            _drag = false;
            ReleaseMouseCapture();
            Commit(e.GetPosition(this).X);
            if (DragState != null) DragState(false);
        };
        SizeChanged += delegate { Redraw(); };
    }

    public Color Accent
    {
        set { _fill.Background = new SolidColorBrush(Blend(value)); }
    }

    static Color Blend(Color c)
    {
        // подмешиваем белый: чистый цвет обложки на тёмном фоне часто теряется
        return Color.FromArgb(235,
            (byte)Math.Min(255, c.R * 0.55 + 255 * 0.45),
            (byte)Math.Min(255, c.G * 0.55 + 255 * 0.45),
            (byte)Math.Min(255, c.B * 0.55 + 255 * 0.45));
    }

    public double Value
    {
        get { return _value; }
        set { _value = Math.Max(0, Math.Min(1, value)); Redraw(); }
    }

    void Preview(double x)
    {
        if (ActualWidth <= 0) return;
        _value = Math.Max(0, Math.Min(1, x / ActualWidth));
        Redraw();
    }

    void Commit(double x)
    {
        Preview(x);
        if (Seek != null) Seek(_value);
    }

    void Redraw()
    {
        if (ActualWidth <= 0) return;
        double w = ActualWidth * _value;
        _fill.Width = w;
        _knob.Margin = new Thickness(Math.Max(0, w - 4), 0, 0, 0);
    }
}
