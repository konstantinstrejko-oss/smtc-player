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
using System.Windows.Threading;
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
    // Тот же фон, но без размытия. В фаске стекло сжимает картинку в узкую
    // полосу и показывает её резкой — из одного размытого входа получалось мыло.
    public static readonly DependencyProperty SharpProperty =
        RegisterPixelShaderSamplerProperty("Sharp", typeof(GlassShader), 2);

    public static readonly DependencyProperty AmountProperty =
        DependencyProperty.Register("Amount", typeof(double), typeof(GlassShader),
            new UIPropertyMetadata(0.02, PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register("Phase", typeof(double), typeof(GlassShader),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
    // Вся область эффекта в точках — карточка вместе с запасом снимка. Сузить её
    // до карточки нельзя: ни ClipToBounds, ни явный Clip на элементе с эффектом
    // области не ограничивают (проверено стендом). Поэтому геометрия карточки
    // передаётся отдельным параметром.
    public static readonly DependencyProperty AreaProperty =
        DependencyProperty.Register("Area", typeof(Point), typeof(GlassShader),
            new UIPropertyMetadata(new Point(488, 198), PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register("Radius", typeof(double), typeof(GlassShader),
            new UIPropertyMetadata(20.0, PixelShaderConstantCallback(3)));
    /// <summary>Ширина фаски в точках — вся оптика живёт в ней.</summary>
    public static readonly DependencyProperty BevelProperty =
        DependencyProperty.Register("Bevel", typeof(double), typeof(GlassShader),
            new UIPropertyMetadata(32.0, PixelShaderConstantCallback(4)));
    /// <summary>Курсор в точках от центра карточки.</summary>
    public static readonly DependencyProperty PointerProperty =
        DependencyProperty.Register("Pointer", typeof(Point), typeof(GlassShader),
            new UIPropertyMetadata(new Point(-9999, -9999), PixelShaderConstantCallback(5)));
    /// <summary>Сила выпуклости под курсором, 0..1.</summary>
    public static readonly DependencyProperty TouchProperty =
        DependencyProperty.Register("Touch", typeof(double), typeof(GlassShader),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(6)));
    public static readonly DependencyProperty CardProperty =
        DependencyProperty.Register("Card", typeof(Point), typeof(GlassShader),
            new UIPropertyMetadata(new Point(440, 150), PixelShaderConstantCallback(7)));

    public GlassShader(string shaderPath, string noisePath)
    {
        PixelShader = new PixelShader { UriSource = new Uri(shaderPath) };
        // Viewport в АБСОЛЮТНЫХ единицах ужал бы текстуру до одного пикселя, и
        // шум превратился бы в сетку с периодом 1 px — на экране это полосы, а
        // дисперсия каналов делает их радужными. Нужны относительные единицы:
        // текстура на весь элемент, тайлинг — чтобы дрейф не упирался в край.
        Noise = new ImageBrush(new BitmapImage(new Uri(noisePath)))
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1)
        };
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(NoiseProperty);
        UpdateShaderValue(SharpProperty);
        UpdateShaderValue(AmountProperty);
        UpdateShaderValue(PhaseProperty);
        UpdateShaderValue(AreaProperty);
        UpdateShaderValue(RadiusProperty);
        UpdateShaderValue(BevelProperty);
        UpdateShaderValue(PointerProperty);
        UpdateShaderValue(TouchProperty);
        UpdateShaderValue(CardProperty);
    }

    public Brush Input { get { return (Brush)GetValue(InputProperty); } set { SetValue(InputProperty, value); } }
    public Brush Noise { get { return (Brush)GetValue(NoiseProperty); } set { SetValue(NoiseProperty, value); } }
    public Brush Sharp { get { return (Brush)GetValue(SharpProperty); } set { SetValue(SharpProperty, value); } }
    public double Amount { get { return (double)GetValue(AmountProperty); } set { SetValue(AmountProperty, value); } }
    public double Phase { get { return (double)GetValue(PhaseProperty); } set { SetValue(PhaseProperty, value); } }
    public Point Area { get { return (Point)GetValue(AreaProperty); } set { SetValue(AreaProperty, value); } }
    public double Radius { get { return (double)GetValue(RadiusProperty); } set { SetValue(RadiusProperty, value); } }
    public double Bevel { get { return (double)GetValue(BevelProperty); } set { SetValue(BevelProperty, value); } }
    public Point Pointer { get { return (Point)GetValue(PointerProperty); } set { SetValue(PointerProperty, value); } }
    public double Touch { get { return (double)GetValue(TouchProperty); } set { SetValue(TouchProperty, value); } }
    public Point Card { get { return (Point)GetValue(CardProperty); } set { SetValue(CardProperty, value); } }
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

    /// <summary>Радиус скругления окна.</summary>
    const double Radius = 20;

    /// <summary>Плотность оттенка обложки поверх стекла.</summary>
    const byte TintAlpha = 0x26;

    /// <summary>Запас снимка фона по краям, чтобы преломлению было что брать.</summary>
    const double GlassPad = 24;

    /// <summary>
    /// Поле вокруг карточки под тень. Окно на столько же больше самой карточки:
    /// без тени панель выглядит наклейкой, а рисовать её некуда — за границами
    /// окна композитор ничего не покажет.
    /// </summary>
    const double ShadowPad = 20;

    // слои и элементы
    Border _glassHost, _tint, _card, _shadow;
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

    // Перекраска содержимого под яркость фона. Каждый элемент при постройке
    // кладёт сюда свою функцию — иначе при добавлении новой кнопки её легко
    // забыть, и она останется белой на белом.
    readonly System.Collections.Generic.List<Action<bool>> _skin =
        new System.Collections.Generic.List<Action<bool>>();
    // из этих регистраций живут только до следующей раскладки — кнопки
    readonly System.Collections.Generic.List<Action<bool>> _skinLayout =
        new System.Collections.Generic.List<Action<bool>>();
    Border _veil;
    bool _onLight;

    string _coverShown = "";
    bool _vertical;
    bool _draggingSeek;
    bool _draggingVolume;
    DateTime _volumeTouched = DateTime.MinValue;
    bool _closing;
    bool _warmed;
    bool _wantVisible;
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

        // Жидкость: под курсором стекло вспучивается мягким куполом и оптика
        // едет вместе с ним. Купол включается плавно — резкое появление читается
        // как подёргивание, а не как отклик материала.
        MouseMove += delegate (object s, MouseEventArgs e)
        {
            if (_shader == null) return;
            var pt = e.GetPosition(_glassHost);
            _shader.Pointer = new Point(pt.X - _glassHost.ActualWidth / 2,
                                        pt.Y - _glassHost.ActualHeight / 2);
        };
        MouseEnter += delegate { AnimateTouch(1.0, 220); };
        MouseLeave += delegate { AnimateTouch(0.0, 320); };

        // CloseOnBlur=0 оставляет окно висеть, пока его не закроют явно
        Deactivated += delegate
        {
            if (_draggingSeek || _draggingVolume) return;
            if (!Settings.GetBool("CloseOnBlur", true)) return;
            HideSmooth();
        };
        KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Escape) HideSmooth(); };
        Closed += delegate { PlayerState.Changed -= OnStateChanged; };
    }

    // ------------------------------------------------- контраст содержимого

    /// <summary>
    /// Стекло прозрачное, поэтому белый текст на светлом фоне не читается — как
    /// не читается тёмный на тёмном. Содержимое переключается по фактической
    /// яркости того, что лежит под окном: светлый фон — тёмная типографика и
    /// светлая вуаль, тёмный — как было.
    /// </summary>
    void ApplyContrast(bool light)
    {
        _onLight = light;
        foreach (var apply in _skin) apply(light);

        if (_veil != null)
            _veil.Background = light
                ? new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(30, 255, 255, 255), 90)
                : new LinearGradientBrush(Color.FromArgb(20, 10, 10, 14), Color.FromArgb(48, 6, 6, 10), 90);

        var shadow = _content != null ? _content.Effect as DropShadowEffect : null;
        if (shadow != null)
        {
            shadow.Color = light ? Colors.White : Colors.Black;
            shadow.Opacity = light ? 0.8 : 0.95;
        }
    }

    Color Ink(bool light, byte alpha)
    {
        return light ? Color.FromArgb(alpha, 14, 14, 18) : Color.FromArgb(alpha, 255, 255, 255);
    }

    void ToneText(TextBlock t, byte alpha)
    {
        _skin.Add(delegate (bool light) { t.Foreground = new SolidColorBrush(Ink(light, alpha)); });
    }

    void ToneFill(System.Windows.Shapes.Shape s, byte alpha) { ToneFill(s, alpha, false); }

    void ToneFill(System.Windows.Shapes.Shape s, byte alpha, bool perLayout)
    {
        Action<bool> apply = delegate (bool light) { s.Fill = new SolidColorBrush(Ink(light, alpha)); };
        _skin.Add(apply);
        if (perLayout) _skinLayout.Add(apply);
        apply(_onLight);
    }

    void ToneStroke(System.Windows.Shapes.Shape s, byte alpha)
    {
        _skin.Add(delegate (bool light) { s.Stroke = new SolidColorBrush(Ink(light, alpha)); });
    }

    void ToneBorder(Border b, byte alpha)
    {
        _skin.Add(delegate (bool light) { b.Background = new SolidColorBrush(Ink(light, alpha)); });
    }

    /// <summary>Ширина и высота самой карточки: окно шире на поле под тень.</summary>
    double CardW { get { return Math.Max(1, Width - ShadowPad * 2); } }
    double CardH { get { return Math.Max(1, Height - ShadowPad * 2); } }

    /// <summary>Плавно набирает и отпускает выпуклость стекла под курсором.</summary>
    void AnimateTouch(double to, int ms)
    {
        if (_shader == null) return;
        if (!Settings.GetBool("GlassTouch", true)) { _shader.Touch = 0; return; }
        _shader.BeginAnimation(GlassShader.TouchProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
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
        // Окно больше карточки на ShadowPad с каждой стороны: в этом поле лежит
        // тень. Всё остальное — стекло, содержимое, клип, захват фона — считает
        // координаты от карточки, а не от окна.
        _root = new Grid();

        // 1. стекло: настоящий фон из-под окна, размытый и искажённый шейдером
        _glassImage = new Image
        {
            Stretch = Stretch.Fill,
            Effect = new BlurEffect
            {
                // Чем сильнее размытие, тем меньше от фона остаётся: на большом
                // радиусе стекло превращается в ровное пятно.
                Radius = Math.Max(2, Math.Min(60, Settings.GetInt("GlassBlur", 14))),
                KernelType = KernelType.Gaussian
            }
        };
        // Снимок выпускается за границы окна на GlassPad — размытию нужно, что
        // размывать по краям. Поля постоянные: недостающее у края экрана
        // дорисовывает сам захват.
        _glassImage.Margin = new Thickness(-GlassPad);
        _glassHost = new Border { Child = _glassImage, ClipToBounds = true };

        string shaderPath = IOPath.Combine(ResourceDir, "Glass", "glass.ps");
        string noisePath = IOPath.Combine(ResourceDir, "Glass", "noise.png");
        if (File.Exists(shaderPath) && File.Exists(noisePath))
        {
            try
            {
                _shader = new GlassShader(new Uri(shaderPath).AbsoluteUri, new Uri(noisePath).AbsoluteUri)
                {
                    Amount = Math.Max(0.0, Math.Min(0.2, Settings.GetInt("GlassAmount", 90) / 1000.0)),
                    Radius = Radius,
                    Bevel = Math.Max(8, Math.Min(60, Settings.GetInt("GlassBevel", 32)))
                };
                _glassHost.Effect = _shader;
            }
            catch { _shader = null; }   // без искажения, но со стеклом
        }
        // геометрию карточки шейдер должен знать: вся оптика задана в точках
        _glassHost.SizeChanged += delegate
        {
            if (_shader == null) return;
            double w = Math.Max(1, _glassHost.ActualWidth), h = Math.Max(1, _glassHost.ActualHeight);
            _shader.Card = new Point(w, h);
            _shader.Area = new Point(w + GlassPad * 2, h + GlassPad * 2);

            // Вьюпорт резкого входа считается от размеров карточки — при смене
            // раскладки он обязан пересчитаться, иначе резкая копия ляжет со
            // сдвигом относительно размытой.
            var brush = _shader.Sharp as ImageBrush;
            if (brush != null)
                brush.Viewport = new Rect(-GlassPad / w, -GlassPad / h,
                                          (w + GlassPad * 2) / w, (h + GlassPad * 2) / h);
        };
        _root.Children.Add(_glassHost);

        // 2. тинт от обложки — стекло меняет оттенок вместе с треком
        _tint = new Border { Background = new SolidColorBrush(Color.FromArgb(TintAlpha, 90, 110, 170)) };
        _root.Children.Add(_tint);

        // 3. вуаль. Держится минимальной: плотные слои поверх съедают всю оптику
        // фаски, а читаемость текста обеспечивает его собственная тень.
        _veil = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromArgb(20, 10, 10, 14), Color.FromArgb(48, 6, 6, 10), 90)
        };
        _root.Children.Add(_veil);

        // 3. содержимое. Тень под ним заменяет плотное затемнение всей карточки:
        // текст читается на любом фоне, а сам фон остаётся видимым.
        _content = new Grid
        {
            Margin = new Thickness(16),
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.95,
                Color = Colors.Black
            }
        };
        BuildContent();
        _root.Children.Add(_content);
        ApplyContrast(false);   // стартуем в тёмном исполнении, замер поправит

        // Нарисованной фаски здесь больше нет: грань, блик и световую нить по
        // кромке считает шейдер из настоящей нормали поверхности. Рамка поверх
        // спорила с ними и выдавала карточку за наклейку. Если шейдер не
        // загрузился, рамку возвращаем — иначе край вовсе ничем не обозначен.
        if (_shader == null)
            _root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(Radius),
                BorderThickness = new Thickness(1.2),
                IsHitTestVisible = false,
                BorderBrush = new LinearGradientBrush(
                    Color.FromArgb(120, 255, 255, 255), Color.FromArgb(25, 255, 255, 255), 90)
            });

        // Системное скругление DWM даёт всего ~8 px и радиус не настраивается,
        // поэтому режем содержимое сами. Углы при этом остаются прозрачными:
        // фон окна композитор не рисует, а WPF за пределы Clip не заходит.
        _root.SizeChanged += delegate
        {
            // во время показа Clip анимируется — перебивать его нельзя
            if (IsVisible) return;
            _root.Clip = new RectangleGeometry(
                new Rect(0, 0, _root.ActualWidth, _root.ActualHeight), Radius, Radius);
        };

        // Тень отдельным слоем под карточкой, а не эффектом на ней: содержимое
        // карточки меняется каждый кадр вместе с фоном, и тень пересчитывалась бы
        // столько же раз. Здесь она статична и уходит в кэш растром.
        _shadow = new Border
        {
            Margin = new Thickness(ShadowPad, ShadowPad + 5, ShadowPad, ShadowPad - 5),
            CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(Color.FromArgb(96, 0, 0, 0)),
            Effect = new BlurEffect { Radius = 16, KernelType = KernelType.Gaussian },
            CacheMode = new BitmapCache(),
            IsHitTestVisible = false,
            Opacity = 0
        };

        _card = new Border { Margin = new Thickness(ShadowPad), Child = _root };

        var shell = new Grid();
        shell.Children.Add(_shadow);
        shell.Children.Add(_card);
        Content = shell;
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
        ToneText(_title, 255);
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
        ToneText(_artist, 190);

        _posText = Mono();
        _durText = Mono();
        _durText.HorizontalAlignment = HorizontalAlignment.Right;

        _progress = new ProgressBarLite(3.5);
        _skin.Add(_progress.SetLight);
        _progress.Seek += OnSeek;
        _progress.DragState += delegate (bool on) { _draggingSeek = on; };

        _volume = new ProgressBarLite(3.0) { Width = 78, Live = true };
        _skin.Add(_volume.SetLight);
        _volume.Seek += delegate (double v)
        {
            AppVolume.Set(PlayerState.AppId, (float)v);
            // система применяет громкость не мгновенно; пока она догоняет,
            // читать её обратно нельзя — ползунок отскакивал бы назад
            _volumeTouched = DateTime.UtcNow;
        };
        _volume.DragState += delegate (bool on)
        {
            _draggingVolume = on;
            _volumeTouched = DateTime.UtcNow;
        };

        // Корпус динамика и «волны» разведены: волны рисуются обводкой, и при
        // нуле подменяются перечёркиванием — как принято во всех плеерах.
        var speaker = new Grid { Width = 17, Height = 12, Margin = new Thickness(0, 0, 7, 0),
                                 VerticalAlignment = VerticalAlignment.Center };
        var speakerBody = new Path
        {
            Data = Geometry.Parse("M0,4 L3,4 L7,0 L7,12 L3,8 L0,8 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
        };
        ToneFill(speakerBody, 210);
        speaker.Children.Add(speakerBody);
        _volWave = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        ToneStroke(_volWave, 210);
        speaker.Children.Add(_volWave);
        SetMuted(false);

        _volumeBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _volumeBox.Children.Add(speaker);
        _volumeBox.Children.Add(_volume);
    }

    TextBlock Mono()
    {
        var t = new TextBlock
        {
            FontFamily = _display,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255))
        };
        ToneText(t, 165);
        return t;
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
        ToneFill(_playIcon, 255, true);
        panel.Children.Add(Wrap(_playIcon, 1.0 * scale, delegate { PlayerState.Send("playpause"); }));
        panel.Children.Add(IconButton("M0,0 L8,6 L0,12 Z M8.4,0 L10,0 L10,12 L8.4,12 Z", 0.82 * scale,
                                      delegate { PlayerState.Send("next"); }));
        return panel;
    }

    UIElement IconButton(string geometry, double scale, Action click)
    {
        var icon = new Path { Fill = Brushes.White, Data = Geometry.Parse(geometry) };
        ToneFill(icon, 235, true);
        return Wrap(icon, scale, click);
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

        // Очистки _content мало: обложка, ползунки и время лежат не прямо в нём,
        // а во вложенных панелях, и после Clear остаются логическими детьми этих
        // панелей. Повторная раскладка тогда падает с «элемент уже является
        // логическим дочерним для другого» и уносит процесс целиком — до этой
        // строки переключение вида на живом окне работало ровно один раз.
        Detach(_coverFrame);
        Detach(_titleClip);
        Detach(_artist);
        Detach(_progress);
        Detach(_posText);
        Detach(_durText);
        Detach(_volumeBox);

        // Кнопки собираются заново на каждую раскладку, поэтому их перекраску
        // надо снимать с учёта: иначе список растёт, а вместе с ним и мусор из
        // элементов, которых давно нет в дереве.
        foreach (var stale in _skinLayout) _skin.Remove(stale);
        _skinLayout.Clear();

        if (vertical) LayoutVertical(); else LayoutHorizontal();

        Width = (vertical ? 300 : 440) + ShadowPad * 2;
        Height = (vertical ? 400 : 150) + ShadowPad * 2;
        Place();
    }

    /// <summary>Снимает элемент с текущего родителя, каким бы тот ни был.</summary>
    static void Detach(FrameworkElement el)
    {
        if (el == null) return;
        var parent = el.Parent;

        var panel = parent as Panel;
        if (panel != null) { panel.Children.Remove(el); return; }

        var decorator = parent as Decorator;
        if (decorator != null && decorator.Child == el) { decorator.Child = null; return; }

        var holder = parent as ContentControl;
        if (holder != null && holder.Content == el) holder.Content = null;
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
        // своё скругление больше системного, так что системное только мешало бы
        int round = 1 /* DONOTROUND */; DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);

        // Без этого всё, что лежит вне Clip, композитор заливает чёрным — и
        // раскрытие карточки идёт из чёрного прямоугольника. С прозрачным
        // градиентом непрорисованные области становятся по-настоящему сквозными.
        SetAccent(h, 2 /* ACCENT_ENABLE_TRANSPARENTGRADIENT */, 0);
    }

    static bool SetAccent(IntPtr hwnd, int state, int gradientColor)
    {
        var policy = new ACCENTPOLICY { State = state, Flags = 2, GradientColor = gradientColor, AnimationId = 0 };
        int size = Marshal.SizeOf(policy);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, p, false);
            var data = new WCD { Attribute = WCA_ACCENT_POLICY, Data = p, SizeOfData = size };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Тинт стекла — доминирующий цвет обложки. Стекло «дышит» с треком.</summary>
    void ApplyAccent()
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;

        // порядок байтов политики: AABBGGRR
        int tint = (0x50 << 24) | (_accent.B << 16) | (_accent.G << 8) | _accent.R;
        if (!SetAccent(h, ACCENT_ENABLE_ACRYLICBLURBEHIND, tint))
        {
            // API не сработал — рисуем сплошной тёмный фон, всё остальное живо
            _root.Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 24));
        }
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
        // пока тащат ползунок и ещё полсекунды после — значение не трогаем
        if (_draggingVolume || (DateTime.UtcNow - _volumeTouched).TotalMilliseconds < 500)
        {
            SetMuted(_volume.Value < 0.005);
            return;
        }

        float v = AppVolume.Get(PlayerState.AppId);
        if (v < 0) { _volumeBox.Visibility = Visibility.Collapsed; return; }
        _volumeBox.Visibility = Visibility.Visible;

        // мелкую разницу игнорируем: система округляет громкость по-своему,
        // и от этих долей процента ползунок дёргался бы на каждом такте
        if (Math.Abs(_volume.Value - v) > 0.004) _volume.Value = v;
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
            _tint.Background = new SolidColorBrush(Color.FromArgb(TintAlpha, _accent.R, _accent.G, _accent.B));
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    /// <summary>
    /// Гасит всплывающую панель трея. Окно открывается кликом по иконке, и эта
    /// панель в этот момент ещё на экране — без неё снимок фона забирал бы её
    /// внутрь стекла, и трей просвечивал бы сквозь окно.
    /// </summary>
    static void DismissTrayFlyout()
    {
        string[] classes = {
            "NotifyIconOverflowWindow",              // Windows 10
            "TopLevelWindowForOverflowXamlIsland",   // Windows 11
            "Xaml_WindowedPopupClass"
        };
        bool hidden = false;
        foreach (string cls in classes)
        {
            IntPtr h = FindWindow(cls, null);
            if (h != IntPtr.Zero && IsWindowVisible(h)) { ShowWindow(h, 0 /* SW_HIDE */); hidden = true; }
        }
        // дать композитору перерисовать экран, иначе снимок ещё со старым кадром
        if (hidden) System.Threading.Thread.Sleep(90);
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")]
    static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd,
                                  IntPtr src, int xs, int ys, int ws, int hs, uint rop);

    const int STRETCH_COLORONCOLOR = 3;
    const uint SRCCOPY = 0x00CC0020;

    const uint WDA_NONE = 0;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    D.Bitmap _shot;
    WriteableBitmap _live;
    int _shotW, _shotH;

    double DpiScale
    {
        get
        {
            try { using (var probe = D.Graphics.FromHwnd(IntPtr.Zero)) return probe.DpiX / 96.0; }
            catch { return 1.0; }
        }
    }

    /// <summary>
    /// Снимок того, что лежит под окном. Пиксели пишутся в одну и ту же
    /// WriteableBitmap: пересоздавать картинку на каждом кадре слишком дорого.
    /// </summary>
    void CaptureBehind()
    {
        try
        {
            double scale = DpiScale;
            // Снимаем с запасом по краям: размытию нужно, что размывать за
            // границей карточки, иначе по периметру стекло редеет и сквозь него
            // проступает тинт полосой.
            int pad = (int)Math.Round(GlassPad * scale);
            int wx = (int)Math.Round((Left + ShadowPad) * scale);
            int wy = (int)Math.Round((Top + ShadowPad) * scale);
            int ww = (int)Math.Round(CardW * scale);
            int wh = (int)Math.Round(CardH * scale);

            // Размер снимка постоянный — окно плюс запас с четырёх сторон. Раньше
            // он обрезался краем экрана, и картинка ложилась в окно со сдвигом.
            int fullW = ww + pad * 2;
            int fullH = wh + pad * 2;

            // Читать за пределами экрана нельзя: оттуда приходит мусор. Чего не
            // хватило, дорисуем растяжкой крайнего ряда пикселей.
            var bounds = D.Rectangle.FromLTRB(
                (int)SystemParameters.VirtualScreenLeft, (int)SystemParameters.VirtualScreenTop,
                (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth),
                (int)(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight));

            int x = Math.Max(bounds.Left, wx - pad);
            int y = Math.Max(bounds.Top, wy - pad);
            int right = Math.Min(bounds.Right, wx + ww + pad);
            int bottom = Math.Min(bounds.Bottom, wy + wh + pad);
            int w = right - x;
            int h = bottom - y;
            if (w < 2 || h < 2) return;

            // куда снятое ложится внутри снимка
            int inX = x - (wx - pad);
            int inY = y - (wy - pad);

            // Фон уходит под сильное размытие, поэтому снимать его можно
            // уменьшенным: вчетверо меньше пикселей копировать и отдавать в WPF,
            // а разницы не видно. Масштаб обратно делает GPU при отрисовке.
            // Уменьшение экономит копирование, но не чтение экрана — замер
            // показал, что выигрыша по процессору почти нет, поэтому по
            // умолчанию берём полное качество.
            int div = Math.Max(1, Math.Min(4, Settings.GetInt("GlassScale", 1)));
            int cw = Math.Max(2, fullW / div);
            int ch = Math.Max(2, fullH / div);

            // те же прямоугольники в масштабе снимка
            double kx = (double)cw / fullW, ky = (double)ch / fullH;
            int dx = (int)Math.Round(inX * kx), dy = (int)Math.Round(inY * ky);
            int dw = Math.Max(1, (int)Math.Round(w * kx)), dh = Math.Max(1, (int)Math.Round(h * ky));
            if (dx + dw > cw) dw = cw - dx;
            if (dy + dh > ch) dh = ch - dy;

            if (_shot == null || _shotW != cw || _shotH != ch)
            {
                if (_shot != null) _shot.Dispose();
                _shot = new D.Bitmap(cw, ch, D.Imaging.PixelFormat.Format32bppPArgb);
                _live = new WriteableBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32, null);
                _shotW = cw; _shotH = ch;
                _glassImage.Source = _live;

                // Второй вход шейдера — тот же снимок без размытия. Вьюпорт
                // выносит его за границы карточки ровно на запас, чтобы резкая
                // и размытая версии лежали в одних координатах.
                if (_shader != null)
                {
                    double gw = Math.Max(1, _glassHost.ActualWidth > 0 ? _glassHost.ActualWidth : Width);
                    double gh = Math.Max(1, _glassHost.ActualHeight > 0 ? _glassHost.ActualHeight : Height);
                    _shader.Sharp = new ImageBrush(_live)
                    {
                        Stretch = Stretch.Fill,
                        TileMode = TileMode.None,
                        ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                        Viewport = new Rect(-GlassPad / gw, -GlassPad / gh,
                                            (gw + GlassPad * 2) / gw, (gh + GlassPad * 2) / gh)
                    };
                }
            }

            using (var g = D.Graphics.FromImage(_shot))
            {
                IntPtr dst = g.GetHdc();
                IntPtr screen = GetDC(IntPtr.Zero);
                try
                {
                    SetStretchBltMode(dst, STRETCH_COLORONCOLOR);
                    StretchBlt(dst, dx, dy, dw, dh, screen, x, y, w, h, SRCCOPY);

                    // Поля, которых не хватило до края экрана, заполняются
                    // растянутым крайним рядом пикселей. Пустыми их оставлять
                    // нельзя: размытие затянет в стекло прозрачность, и у
                    // прижатой к краю карточки кромка светлеет полосой.
                    int gapL = dx, gapT = dy;
                    int gapR = cw - dx - dw, gapB = ch - dy - dh;
                    if (gapL > 0) StretchBlt(dst, 0, dy, gapL, dh, screen, x, y, 1, h, SRCCOPY);
                    if (gapR > 0) StretchBlt(dst, dx + dw, dy, gapR, dh, screen, right - 1, y, 1, h, SRCCOPY);
                    if (gapT > 0) StretchBlt(dst, dx, 0, dw, gapT, screen, x, y, w, 1, SRCCOPY);
                    if (gapB > 0) StretchBlt(dst, dx, dy + dh, dw, gapB, screen, x, bottom - 1, w, 1, SRCCOPY);
                    // углы — от одного пикселя
                    if (gapL > 0 && gapT > 0) StretchBlt(dst, 0, 0, gapL, gapT, screen, x, y, 1, 1, SRCCOPY);
                    if (gapR > 0 && gapT > 0) StretchBlt(dst, dx + dw, 0, gapR, gapT, screen, right - 1, y, 1, 1, SRCCOPY);
                    if (gapL > 0 && gapB > 0) StretchBlt(dst, 0, dy + dh, gapL, gapB, screen, x, bottom - 1, 1, 1, SRCCOPY);
                    if (gapR > 0 && gapB > 0) StretchBlt(dst, dx + dw, dy + dh, gapR, gapB, screen, right - 1, bottom - 1, 1, 1, SRCCOPY);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screen);
                    g.ReleaseHdc(dst);
                }
            }

            int w2 = cw, h2 = ch;
            if (Settings.GetBool("DumpGlass", false))
            {
                Settings.Set("DumpGlass", false);   // один раз
                try
                {
                    string dump = IOPath.Combine(Settings.Dir, "glass_dump.png");
                    _shot.Save(dump, D.Imaging.ImageFormat.Png);
                    Diag.Log("снимок фона сохранён: " + dump);
                }
                catch (Exception ex) { Diag.Log("снимок фона не сохранён: " + ex.Message); }
            }

            var rect = new D.Rectangle(0, 0, w2, h2);
            var data = _shot.LockBits(rect, D.Imaging.ImageLockMode.ReadOnly, D.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                _live.WritePixels(new Int32Rect(0, 0, w2, h2), data.Scan0, data.Stride * h2, data.Stride);
                TrackBrightness(data, w2, h2);
                if (Settings.GetBool("Debug", false)) LogFingerprint(data, w2, h2);
            }
            finally { _shot.UnlockBits(data); }
        }
        catch { }
    }

    DateTime _brightnessAt = DateTime.MinValue;

    /// <summary>
    /// Средняя яркость того, что лежит под окном. Считается по разреженной сетке
    /// пикселей уже снятого кадра — отдельный проход по экрану ради этого не
    /// нужен. Переключение с гистерезисом: на границе содержимое иначе моргало
    /// бы между тёмным и светлым исполнением.
    /// </summary>
    void TrackBrightness(D.Imaging.BitmapData data, int w, int h)
    {
        if ((DateTime.UtcNow - _brightnessAt).TotalMilliseconds < 350) return;
        _brightnessAt = DateTime.UtcNow;

        long sum = 0;
        int n = 0;
        unchecked
        {
            for (int y = 6; y < h; y += 9)
            {
                IntPtr row = (IntPtr)(data.Scan0.ToInt64() + (long)y * data.Stride);
                for (int x = 6; x < w; x += 9)
                {
                    int c = Marshal.ReadInt32(row, x * 4);
                    int b = c & 0xFF, g = (c >> 8) & 0xFF, r = (c >> 16) & 0xFF;
                    sum += (r * 77 + g * 151 + b * 28) >> 8;
                    n++;
                }
            }
        }
        if (n == 0) return;

        double luma = (double)sum / n;
        bool light = _onLight ? luma > 118 : luma > 148;
        if (light != _onLight) ApplyContrast(light);
    }

    DateTime _fingerprintAt = DateTime.MinValue;
    int _capturedFrames;

    /// <summary>
    /// Отпечаток снимка — доказательство, что фон действительно обновляется:
    /// само окно исключено из захвата, поэтому увидеть его скриншотом нельзя.
    /// </summary>
    void LogFingerprint(D.Imaging.BitmapData data, int w, int h)
    {
        _capturedFrames++;
        if ((DateTime.UtcNow - _fingerprintAt).TotalMilliseconds < 1000) return;
        _fingerprintAt = DateTime.UtcNow;

        unchecked
        {
            long sum = 0;
            for (int y = 4; y < h; y += 17)
            {
                IntPtr row = (IntPtr)(data.Scan0.ToInt64() + (long)y * data.Stride);
                for (int x = 4; x < w; x += 17)
                    sum += Marshal.ReadInt32(row, x * 4) & 0x00FFFFFF;
            }
            Diag.Log(string.Format("стекло: кадров {0}, отпечаток фона {1}", _capturedFrames, sum));
        }
    }

    /// <summary>
    /// Живое стекло: фон перечитывается по таймеру, пока окно открыто. Чтобы
    /// захват не снимал сам себя (иначе получается бесконечный «туннель»), окно
    /// исключается из захвата средствами системы.
    /// </summary>
    void StartLiveGlass()
    {
        bool live = Settings.GetBool("GlassLive", true);
        IntPtr h = new WindowInteropHelper(this).Handle;

        if (!live)
        {
            SetWindowDisplayAffinity(h, WDA_NONE);
            return;
        }

        // Требует Windows 10 2004+. Если не поддерживается — остаёмся на
        // единственном снимке, иначе окно поймает само себя.
        if (!SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE))
        {
            Diag.Log("живое стекло недоступно: окно не исключается из захвата");
            return;
        }

        // Таймер даёт рывки: он не совпадает с кадрами композитора, и снимок то
        // отстаёт, то дублируется. CompositionTarget.Rendering срабатывает перед
        // каждым кадром WPF, поэтому фон обновляется ровно тогда, когда его
        // собираются рисовать.
        _glassFps = Math.Max(0, Math.Min(240, Settings.GetInt("GlassFps", 0)));
        if (!_renderHooked)
        {
            CompositionTarget.Rendering += OnRenderFrame;
            _renderHooked = true;
        }
    }

    int _glassFps;
    bool _renderHooked;
    DateTime _lastCapture = DateTime.MinValue;

    void OnRenderFrame(object sender, EventArgs e)
    {
        if (!IsVisible) return;

        // GlassFps=0 — каждый кадр композитора; иначе не чаще заданного
        if (_glassFps > 0)
        {
            double minGap = 1000.0 / _glassFps;
            if ((DateTime.UtcNow - _lastCapture).TotalMilliseconds < minGap) return;
        }
        _lastCapture = DateTime.UtcNow;
        CaptureBehind();
    }

    void StopLiveGlass()
    {
        if (!_renderHooked) return;
        CompositionTarget.Rendering -= OnRenderFrame;
        _renderHooked = false;
    }

    /// <summary>Ставит окно в угол рабочей области — там же, где трей.</summary>
    void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width + ShadowPad - 14;
        Top = wa.Bottom - Height + ShadowPad - 14;
    }

    /// <summary>
    /// Первый показ окна давал вспышку чёрным: HWND уже на экране, а WPF ещё не
    /// нарисовал ни одного кадра. Поэтому окно один раз поднимается далеко за
    /// пределами экрана и прячется сразу после первой отрисовки.
    /// </summary>
    public void Prewarm()
    {
        if (_warmed) return;
        _warmed = true;

        Left = -30000;
        Top = -30000;
        Show();

        ContentRendered += delegate
        {
            // если за время прогрева окно уже попросили показать — не мешаем
            if (_wantVisible) return;
            Hide();
            Place();
        };
    }

    public void ShowSmooth()
    {
        _wantVisible = true;
        Place();

        // первый кадр — строго до Show(), иначе окно снимет само себя
        DismissTrayFlyout();
        CaptureBehind();
        _fallbackAcrylic = _live == null;

        Show();
        Activate();
        StartLiveGlass();
        if (_fallbackAcrylic) ApplyAccent();   // не сняли фон — пусть размывает DWM
        Apply();

        // Прозрачность у корня анимировать нельзя: под ним непрозрачный фон
        // окна, и полупрозрачная карточка проявлялась бы из черноты. Прозрачно
        // только то, чего WPF не рисует вовсе, — поэтому раскрываем сам Clip.
        AnimateReveal(true);

        // содержимое подтягивается чуть позже карточки: под ним уже лежит
        // непрозрачное стекло, так что его прозрачность безопасна
        _content.Opacity = 0;
        _content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240))
        {
            BeginTime = TimeSpan.FromMilliseconds(110),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        // Дрейф фазы по умолчанию выключен: стекло не должно само по себе течь,
        // оно преломляет то, что за ним. Постоянное движение картинки читается
        // как дефект, а не как эффект.
        int drift = Math.Max(0, Math.Min(600, Settings.GetInt("GlassDrift", 0)));
        if (_shader != null && drift > 0)
            _shader.BeginAnimation(GlassShader.PhaseProperty,
                new DoubleAnimation(0, 24, TimeSpan.FromSeconds(drift)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>
    /// Раскрытие и схлопывание карточки: анимируется прямоугольник Clip, растущий
    /// из угла у трея. Одновременно содержимое чуть подрастает — от этого движение
    /// читается как «карточка вылетает», а не «прямоугольник расширяется».
    /// </summary>
    void AnimateReveal(bool opening)
    {
        // размеры карточки, а не окна: окно шире на поле под тень
        double w = _root.ActualWidth > 0 ? _root.ActualWidth : CardW;
        double h = _root.ActualHeight > 0 ? _root.ActualHeight : CardH;

        // тень проявляется вместе с карточкой и уходит чуть раньше неё
        if (_shadow != null)
            _shadow.BeginAnimation(OpacityProperty,
                new DoubleAnimation(opening ? 1.0 : 0.0,
                    TimeSpan.FromMilliseconds(opening ? 300 : 170)));

        var clip = _root.Clip as RectangleGeometry;
        if (clip == null)
        {
            clip = new RectangleGeometry(new Rect(0, 0, w, h), Radius, Radius);
            _root.Clip = clip;
        }

        // сжатое состояние: узкая полоса у правого нижнего угла — там трей
        var small = new Rect(w * 0.30, h * 0.42, w * 0.70, h * 0.58);
        var full = new Rect(0, 0, w, h);

        var span = TimeSpan.FromMilliseconds(opening ? 380 : 210);
        IEasingFunction ease = opening
            ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            : new CubicEase { EasingMode = EasingMode.EaseIn };

        if (opening) clip.Rect = small;
        var rect = new RectAnimation(opening ? small : full, opening ? full : small, span)
        {
            EasingFunction = ease
        };

        var scale = _root.RenderTransform as ScaleTransform;
        if (scale == null)
        {
            scale = new ScaleTransform(1, 1);
            _root.RenderTransformOrigin = new Point(0.85, 1.0);
            _root.RenderTransform = scale;
        }
        var grow = new DoubleAnimation(opening ? 0.97 : 1.0, opening ? 1.0 : 0.98, span) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        if (!opening)
        {
            rect.Completed += delegate
            {
                _closing = false;
                _wantVisible = false;
                Hide();
                clip.Rect = new Rect(0, 0, w, h);   // вернуть к полному на будущее
            };
        }
        clip.BeginAnimation(RectangleGeometry.RectProperty, rect);
    }

    public void HideSmooth()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        StopLiveGlass();
        if (_shader != null) _shader.BeginAnimation(GlassShader.PhaseProperty, null);

        _content.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
        AnimateReveal(false);
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

    /// <summary>Применять значение прямо во время перетаскивания.</summary>
    public bool Live;

    DateTime _lastLive = DateTime.MinValue;

    /// <summary>Перекраска под светлый фон — см. ApplyContrast у окна.</summary>
    public void SetLight(bool light)
    {
        _track.Background = new SolidColorBrush(light
            ? Color.FromArgb(60, 14, 14, 18) : Color.FromArgb(55, 255, 255, 255));
        _fill.Background = new SolidColorBrush(light
            ? Color.FromArgb(225, 14, 14, 18) : Color.FromArgb(230, 255, 255, 255));
        _knob.Fill = new SolidColorBrush(light
            ? Color.FromArgb(255, 14, 14, 18) : Colors.White);
    }

    public ProgressBarLite(double thickness)
    {
        Height = 18;   // полоса тонкая, но хватать её нужно уверенно
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
            Width = 10,
            Height = 10,
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

            // громкость меняется сразу, а не по отпусканию — иначе тащишь
            // вслепую; чаще 20 раз в секунду системе слать незачем
            if (!Live || (DateTime.UtcNow - _lastLive).TotalMilliseconds < 50) return;
            _lastLive = DateTime.UtcNow;
            if (Seek != null) Seek(_value);
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
        // кружок держим целиком внутри полосы: на максимуме он иначе наполовину
        // уезжает за край и выглядит обрезанным
        double knob = _knob.Width;
        double x = Math.Max(0, Math.Min(ActualWidth - knob, w - knob / 2));
        _knob.Margin = new Thickness(x, 0, 0, 0);
    }
}
