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
        float gap = r * 0.14f;

        var top = new PointF(cx, cy - r);
        var right = new PointF(cx + r, cy);
        var bottom = new PointF(cx, cy + r);
        var left = new PointF(cx - r, cy);
        var mid = new PointF(cx, cy);

        Piece(g, new[] { top, right, mid }, RightGlass, alpha, gap, cx, cy);
        Piece(g, new[] { right, bottom, mid }, RightGlass, alpha, gap, cx, cy);
        Piece(g, new[] { bottom, left, mid }, LeftGlass, alpha, gap, cx, cy);
        Piece(g, new[] { left, top, mid }, LeftGlass, alpha, gap, cx, cy);
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
        int s = IconSize;
        using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float thick = Math.Max(1.3f, s * 0.075f);
            var rect = new RectangleF(thick / 2, thick / 2, s - thick, s - thick);

            int alpha = state == 0 ? 110 : 255;

            using (var track = new Pen(Color.FromArgb(state == 0 ? 45 : 60, 255, 255, 255), thick))
                g.DrawEllipse(track, rect);

            if (state != 0 && progress > 0.001)
            {
                using (var pen = new Pen(Color.FromArgb(alpha, Brighten(accent)), thick))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, rect, -90f, (float)(360.0 * Math.Max(0, Math.Min(1, progress))));
                }
            }

            DrawShard(g, s / 2f, s / 2f, s * 0.345f, alpha);

            // на паузе поверх осколков ложатся две полоски
            if (state == 2)
            {
                using (var br = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                {
                    float w = s * 0.07f, h = s * 0.2f, gap = s * 0.06f;
                    g.FillRectangle(br, s * 0.5f - gap / 2 - w, s * 0.5f - h / 2, w, h);
                    g.FillRectangle(br, s * 0.5f + gap / 2, s * 0.5f - h / 2, w, h);
                }
            }

            return bmp.GetHicon();
        }
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
}
