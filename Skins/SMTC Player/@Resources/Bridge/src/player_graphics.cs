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

    /// <summary>
    /// Логотип, у которого кольцо и есть полоса прогресса: заполняется по часовой
    /// от двенадцати. Внутри play или пауза — по состоянию.
    /// </summary>
    public static IntPtr BuildIcon(double progress, int state, Color accent)
    {
        int s = IconSize;
        using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float pad = s * 0.09f;
            float thick = Math.Max(1.5f, s * 0.085f);
            var rect = new RectangleF(pad + thick / 2, pad + thick / 2,
                                      s - 2 * pad - thick, s - 2 * pad - thick);

            int alpha = state == 0 ? 90 : 255;

            using (var track = new Pen(Color.FromArgb(state == 0 ? 60 : 70, 255, 255, 255), thick))
                g.DrawEllipse(track, rect);

            if (state != 0 && progress > 0.001)
            {
                Color arc = Brighten(accent);
                using (var pen = new Pen(Color.FromArgb(alpha, arc), thick))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, rect, -90f, (float)(360.0 * Math.Max(0, Math.Min(1, progress))));
                }
            }

            using (var br = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            {
                // Иконка показывает состояние, а не действие: играет — треугольник,
                // на паузе — две полоски. Наоборот читалось бы как «остановлено».
                if (state == 2)
                {
                    // пауза: две полоски
                    float w = s * 0.085f, h = s * 0.34f, gap = s * 0.075f;
                    float cx = s * 0.5f, cy = s * 0.5f;
                    g.FillRectangle(br, cx - gap / 2 - w, cy - h / 2, w, h);
                    g.FillRectangle(br, cx + gap / 2, cy - h / 2, w, h);
                }
                else
                {
                    float cx = s * 0.53f, cy = s * 0.5f, r = s * 0.19f;
                    g.FillPolygon(br, new PointF[] {
                        new PointF(cx + r, cy),
                        new PointF(cx - r * 0.72f, cy - r * 0.92f),
                        new PointF(cx - r * 0.72f, cy + r * 0.92f)
                    });
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
