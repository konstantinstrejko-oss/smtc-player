// Растровая часть: иконка трея с кольцом прогресса, миниатюры для истории
// и доминирующий цвет обложки, которым подкрашивается стекло.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class Thumbnail
{
    /// <summary>Уменьшенная копия обложки для журнала.</summary>
    public static void Save(string source, string target, int size)
    {
        try
        {
            using (var src = Load(source))
            {
                if (src == null) return;
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, new Rectangle(0, 0, size, size));
                    bmp.Save(target, ImageFormat.Png);
                }
            }
        }
        catch { }
    }

    /// <summary>Читает через поток: файл обложки перезаписывается мостом на ходу.</summary>
    public static Bitmap Load(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(bytes)) return new Bitmap(ms);
        }
        catch { return null; }
    }
}

public static class Palette
{
    /// <summary>
    /// Доминирующий цвет обложки: среднее с весом насыщенности, затем принудительно
    /// поднятая яркость. Без веса получается серо-бурая каша, а стеклу нужен оттенок.
    /// </summary>
    public static Color Dominant(string coverPath, Color fallback)
    {
        try
        {
            using (var src = Thumbnail.Load(coverPath))
            {
                if (src == null) return fallback;
                using (var small = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, new Rectangle(0, 0, 32, 32));

                    double rs = 0, gs = 0, bs = 0, ws = 0;
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            int max = Math.Max(c.R, Math.Max(c.G, c.B));
                            int min = Math.Min(c.R, Math.Min(c.G, c.B));
                            double sat = max == 0 ? 0 : (double)(max - min) / max;
                            double w = 0.15 + sat * sat * 3.0;      // насыщенные пиксели решают
                            rs += c.R * w; gs += c.G * w; bs += c.B * w; ws += w;
                        }

                    if (ws <= 0) return fallback;
                    return Lift(Color.FromArgb((int)(rs / ws), (int)(gs / ws), (int)(bs / ws)));
                }
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>Подтягивает цвет к читаемой яркости, сохраняя оттенок.</summary>
    static Color Lift(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max < 1) return Color.FromArgb(70, 70, 90);
        double k = 190.0 / max;
        if (k < 1) k = 1;
        if (k > 3.2) k = 3.2;
        return Color.FromArgb(
            (int)Math.Min(255, c.R * k),
            (int)Math.Min(255, c.G * k),
            (int)Math.Min(255, c.B * k));
    }
}

/// <summary>
/// Насколько светла панель задач под самой иконкой.
///
/// Реестровой темы для этого мало: панель задач полупрозрачна, и при «тёмной»
/// теме поверх светлых обоев она получается почти белой. Поэтому яркость
/// меряется по экрану — в углах прямоугольника иконки, где сам знак не рисуется
/// (он круглый) и виден чистый фон панели.
/// </summary>
public static class TrayBackdrop
{
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetPixel(IntPtr dc, int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    struct NotifyIconIdentifier { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    static bool _light;
    static DateTime _measured = DateTime.MinValue;

    /// <summary>Светлый ли фон под иконкой. Замер кэшируется на две секунды.</summary>
    public static bool IsLight(IntPtr ownerWindow)
    {
        if ((DateTime.UtcNow - _measured).TotalSeconds < 2) return _light;
        _measured = DateTime.UtcNow;

        try
        {
            var id = new NotifyIconIdentifier { hWnd = ownerWindow, uID = 1, guidItem = Guid.Empty };
            id.cbSize = Marshal.SizeOf(typeof(NotifyIconIdentifier));
            RECT r;
            if (Shell_NotifyIconGetRect(ref id, out r) != 0) return _light;
            if (r.Right - r.Left < 6 || r.Bottom - r.Top < 6) return _light;

            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return _light;
            try
            {
                int[] xs = { r.Left + 1, r.Right - 2, r.Left + 1, r.Right - 2 };
                int[] ys = { r.Top + 1, r.Top + 1, r.Bottom - 2, r.Bottom - 2 };
                double sum = 0;
                int n = 0;
                for (int i = 0; i < 4; i++)
                {
                    int c = GetPixel(dc, xs[i], ys[i]);
                    if (c == -1) continue;                       // CLR_INVALID
                    int rr = c & 0xFF, gg = (c >> 8) & 0xFF, bb = (c >> 16) & 0xFF;
                    sum += 0.299 * rr + 0.587 * gg + 0.114 * bb;
                    n++;
                }
                if (n == 0) return _light;

                double luma = sum / n;
                // гистерезис: на границе иконка иначе моргала бы туда-сюда
                _light = _light ? luma > 118 : luma > 145;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
        catch { }
        return _light;
    }
}

public static class TrayArt
{
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    const int SM_CXSMICON = 49;

    public static int IconSize
    {
        get
        {
            int s = GetSystemMetrics(SM_CXSMICON);
            return s >= 16 ? s : 16;
        }
    }

    // Логотип: ромб, расколотый на четыре стеклянных осколка. Левая пара
    // фиолетово-розовая, правая — сине-бирюзовая.
    static readonly Color[] LeftGlass = { Color.FromArgb(226, 170, 255), Color.FromArgb(150, 90, 210) };
    static readonly Color[] RightGlass = { Color.FromArgb(120, 220, 255), Color.FromArgb(20, 120, 220) };

    /// <summary>
    /// Рисует логотип целиком. Вынесено отдельно, потому что тем же кодом
    /// собирается и .ico приложения — двух версий одного знака быть не должно.
    /// </summary>
    public static void DrawShard(Graphics g, float cx, float cy, float r, int alpha)
    {
        DrawShard(g, cx, cy, r, alpha, false);
    }

    /// <summary>
    /// onLight — знак идёт на светлую панель задач. Стёкла там притемняются:
    /// светло-сиреневая пара на белом растворяется, и от знака остаётся одна
    /// синяя половина — он читается как съехавший вбок.
    /// </summary>
    public static void DrawShard(Graphics g, float cx, float cy, float r, int alpha, bool onLight)
    {
        float gap = r * 0.14f;

        var top = new PointF(cx, cy - r);
        var right = new PointF(cx + r, cy);
        var bottom = new PointF(cx, cy + r);
        var left = new PointF(cx - r, cy);
        var mid = new PointF(cx, cy);

        Color[] rightGlass = onLight ? Deepen(RightGlass) : RightGlass;
        Color[] leftGlass = onLight ? Deepen(LeftGlass) : LeftGlass;

        Piece(g, new[] { top, right, mid }, rightGlass, alpha, gap, cx, cy);
        Piece(g, new[] { right, bottom, mid }, rightGlass, alpha, gap, cx, cy);
        Piece(g, new[] { bottom, left, mid }, leftGlass, alpha, gap, cx, cy);
        Piece(g, new[] { left, top, mid }, leftGlass, alpha, gap, cx, cy);
    }

    /// <summary>Тот же оттенок, но с яркостью, читаемой на белом.</summary>
    static Color[] Deepen(Color[] glass)
    {
        var outp = new Color[glass.Length];
        for (int i = 0; i < glass.Length; i++)
        {
            Color c = glass[i];
            double max = Math.Max(c.R, Math.Max(c.G, c.B));
            double k = max < 1 ? 1 : Math.Min(1.0, 168.0 / max);
            outp[i] = Color.FromArgb((int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
        }
        return outp;
    }

    static void Piece(Graphics g, PointF[] tri, Color[] glass, int alpha, float gap, float cx, float cy)
    {
        // осколок отодвигается от центра — так между гранями остаётся разрез
        float mx = (tri[0].X + tri[1].X + tri[2].X) / 3f - cx;
        float my = (tri[0].Y + tri[1].Y + tri[2].Y) / 3f - cy;
        float len = (float)Math.Sqrt(mx * mx + my * my);
        float dx = 0, dy = 0;
        if (len > 0.001f)
        {
            float ux = mx / len, uy = my / len;
            // разлёт от центра плюс сдвиг по касательной: осколки встают
            // «вертушкой», а не ровным крестом
            dx = ux * gap - uy * gap * 0.62f;
            dy = uy * gap + ux * gap * 0.62f;
        }

        var moved = new PointF[3];
        for (int i = 0; i < 3; i++) moved[i] = new PointF(tri[i].X + dx, tri[i].Y + dy);

        var bounds = Bounds(moved);
        if (bounds.Width < 0.5f || bounds.Height < 0.5f) return;

        using (var brush = new LinearGradientBrush(bounds,
                   Color.FromArgb(alpha, glass[0]), Color.FromArgb(alpha, glass[1]), 55f))
            g.FillPolygon(brush, moved);

        // Светлая грань делает осколок стеклянным, но на иконке трея она шире
        // самого осколка и выбеливает его — там рисуем только заливку.
        if (bounds.Width < 14f) return;
        using (var edge = new Pen(Color.FromArgb(Math.Min(alpha, 175), 255, 255, 255), Math.Max(0.8f, gap * 0.22f)))
            g.DrawPolygon(edge, moved);
    }

    static RectangleF Bounds(PointF[] p)
    {
        float minX = p[0].X, maxX = p[0].X, minY = p[0].Y, maxY = p[0].Y;
        foreach (var q in p)
        {
            if (q.X < minX) minX = q.X; if (q.X > maxX) maxX = q.X;
            if (q.Y < minY) minY = q.Y; if (q.Y > maxY) maxY = q.Y;
        }
        return new RectangleF(minX, minY, Math.Max(1f, maxX - minX), Math.Max(1f, maxY - minY));
    }

    /// <summary>
    /// Иконка трея: логотип внутри кольца, которое и есть полоса прогресса —
    /// заполняется по часовой от двенадцати.
    /// </summary>
    public static IntPtr BuildIcon(double progress, int state, Color accent)
    {
        return BuildIcon(progress, state, accent, false);
    }

    /// <summary>
    /// onLight — иконка идёт на светлую панель задач. Тогда всё, что рисовалось
    /// белым, становится тёмным: белое кольцо на светлом фоне не видно вовсе, и
    /// от значка остаётся одна дуга прогресса — та самая «синяя полоска» сбоку.
    /// </summary>
    public static IntPtr BuildIcon(double progress, int state, Color accent, bool onLight)
    {
        using (var bmp = Render(IconSize, progress, state, accent, onLight))
            return bmp.GetHicon();
    }

    /// <summary>
    /// Та же иконка растром. Вынесено из BuildIcon, потому что HICON нельзя
    /// посмотреть глазами: обратная конвертация теряет полупрозрачность, и
    /// проверка «видно ли значок на светлой панели» врала.
    /// </summary>
    public static Bitmap Render(int s, double progress, int state, Color accent, bool onLight)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float thick = Math.Max(1.3f, s * 0.075f);
            var rect = new RectangleF(thick / 2, thick / 2, s - thick, s - thick);

            int alpha = state == 0 ? 110 : 255;

            Color trackColor = onLight
                ? Color.FromArgb(state == 0 ? 40 : 58, 0, 0, 0)
                : Color.FromArgb(state == 0 ? 45 : 60, 255, 255, 255);
            using (var track = new Pen(trackColor, thick))
                g.DrawEllipse(track, rect);

            if (state != 0 && progress > 0.001)
            {
                Color arc = onLight ? Deepen(accent) : Brighten(accent);
                using (var pen = new Pen(Color.FromArgb(alpha, arc), thick))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, rect, -90f, (float)(360.0 * Math.Max(0, Math.Min(1, progress))));
                }
            }

            DrawShard(g, s / 2f, s / 2f, s * 0.345f, alpha, onLight);

            // на паузе поверх осколков ложатся две полоски
            if (state == 2)
            {
                Color bars = onLight ? Color.FromArgb(235, 24, 24, 32) : Color.FromArgb(235, 255, 255, 255);
                using (var br = new SolidBrush(bars))
                {
                    float w = s * 0.07f, h = s * 0.2f, gap = s * 0.06f;
                    g.FillRectangle(br, s * 0.5f - gap / 2 - w, s * 0.5f - h / 2, w, h);
                    g.FillRectangle(br, s * 0.5f + gap / 2, s * 0.5f - h / 2, w, h);
                }
            }
        }
        return bmp;
    }

    public static void Release(IntPtr icon)
    {
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    static Color Brighten(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max < 40) return Color.White;
        double k = 235.0 / max;
        return Color.FromArgb(
            (int)Math.Min(255, c.R * k),
            (int)Math.Min(255, c.G * k),
            (int)Math.Min(255, c.B * k));
    }

    /// <summary>Тот же оттенок, доведённый до яркости, читаемой на белом.</summary>
    static Color Deepen(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max < 1) return Color.FromArgb(40, 40, 60);
        double k = Math.Min(1.0, 150.0 / max);
        return Color.FromArgb((int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
    }
}
