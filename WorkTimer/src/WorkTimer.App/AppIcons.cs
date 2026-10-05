using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WorkTimer.Core.Timing;

namespace WorkTimer.App;

/// <summary>
/// 托盘图标在运行时用 GDI+ 画出来，不依赖 .ico 资源文件 ——
/// 五个状态五种颜色，改配色不用重新导资源。
/// </summary>
internal static class AppIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<TimerState, Icon> Cache = new();
    private static readonly Dictionary<TimerState, System.Windows.Media.SolidColorBrush> WpfBrushCache = new();

    /// <summary>状态 → 颜色。绿=计时中，橙=待判定，深橙=娱乐挂起，灰=暂停，浅灰=未开始。</summary>
    public static Color ColorOf(TimerState state) => state switch
    {
        TimerState.Working => Color.FromArgb(0x63, 0x99, 0x22),
        TimerState.PendingEntertainment => Color.FromArgb(0xEF, 0x9F, 0x27),
        TimerState.SuspendedAuto => Color.FromArgb(0xD8, 0x5A, 0x30),
        TimerState.PausedByUser => Color.FromArgb(0x88, 0x87, 0x80),
        _ => Color.FromArgb(0xB4, 0xB2, 0xA9),
    };

    /// <summary>
    /// 同一个状态色的 WPF 画刷版本（界面用 GDI+ 的 Color 画不出来）。
    /// 做了缓存：界面每秒都会用到它，每次 new 一个画刷是没必要的分配。
    /// </summary>
    public static System.Windows.Media.SolidColorBrush WpfBrush(TimerState state)
    {
        if (WpfBrushCache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var c = ColorOf(state);
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        WpfBrushCache[state] = brush;
        return brush;
    }

    public static Icon For(TimerState state)
    {
        if (Cache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var icon = Build(ColorOf(state));
        Cache[state] = icon;
        return icon;
    }

    private static Icon Build(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var outer = new SolidBrush(color);
            g.FillEllipse(outer, 2, 2, 28, 28);

            // 中间画一个"表盘"，让图标在托盘里能看出是计时器
            using var inner = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            g.FillEllipse(inner, 8, 8, 16, 16);

            using var hand = new Pen(color, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(hand, 16, 16, 16, 10);   // 指针
            g.DrawLine(hand, 16, 16, 21, 18);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static Bitmap CreateDot(Color color, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 0, 0, size - 1, size - 1);
        return bitmap;
    }
}
