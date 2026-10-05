namespace WorkTimer.Core.Geometry;

/// <summary>吸附在哪条边上。</summary>
public enum SnapEdge
{
    None,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>不依赖任何 UI 框架的矩形，方便把窗口几何放进 Core 做单元测试。</summary>
public readonly record struct RectD(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public bool IntersectsWith(RectD other)
        => other.Left < Right && Left < other.Right && other.Top < Bottom && Top < other.Bottom;

    public override string ToString() => $"({Left:F0},{Top:F0} {Width:F0}x{Height:F0})";
}

/// <summary>
/// 悬浮窗贴边几何计算。
///
/// 抽出来的原因：上一版这里的逻辑写错了 —— 展开时只改尺寸、不动坐标，
/// 贴右边时窗口右半截就跑到屏幕外了。这种"只在特定拖拽路径下才复现"的问题
/// 光靠手点很难覆盖，所以做成纯函数 + 单元测试。
/// </summary>
public static class SnapGeometry
{
    /// <summary>离哪条边最近（且在阈值内）就吸附哪条边。<paramref name="snapSide"/> 为 auto 时四边都允许。</summary>
    public static SnapEdge Detect(RectD window, RectD workArea, string? snapSide, double threshold)
    {
        var side = (snapSide ?? "auto").ToLowerInvariant();
        var allowAll = side == "auto";

        var best = SnapEdge.None;
        var bestGap = double.MaxValue;

        void Consider(SnapEdge candidate, double gap, bool allowed)
        {
            if (!allowed || gap > threshold || gap >= bestGap)
            {
                return;
            }

            best = candidate;
            bestGap = gap;
        }

        Consider(SnapEdge.Left, window.Left - workArea.Left, allowAll || side == "left");
        Consider(SnapEdge.Right, workArea.Right - window.Right, allowAll || side == "right");
        Consider(SnapEdge.Top, window.Top - workArea.Top, allowAll || side == "top");
        Consider(SnapEdge.Bottom, workArea.Bottom - window.Bottom, allowAll || side == "bottom");

        return best;
    }

    /// <summary>
    /// 以 <paramref name="edge"/> 为锚点，把窗口调整到目标尺寸。
    ///
    /// 关键在于<b>贴边那一侧固定、另一侧生长</b>：
    /// 贴右边时 x = 工作区右边界 - 新宽度（左边缘往左移），而不是固定左上角。
    /// 最后再统一钳制进工作区，保证任何情况下都不越界。
    /// </summary>
    public static RectD Resolve(RectD current, SnapEdge edge, double targetWidth, double targetHeight, RectD workArea)
    {
        var x = current.Left;
        var y = current.Top;

        switch (edge)
        {
            case SnapEdge.Left:
                x = workArea.Left;
                break;
            case SnapEdge.Right:
                x = workArea.Right - targetWidth;
                break;
            case SnapEdge.Top:
                y = workArea.Top;
                break;
            case SnapEdge.Bottom:
                y = workArea.Bottom - targetHeight;
                break;
        }

        x = Clamp(x, workArea.Left, workArea.Right - targetWidth);
        y = Clamp(y, workArea.Top, workArea.Bottom - targetHeight);

        return new RectD(x, y, targetWidth, targetHeight);
    }

    /// <summary>窗口是否至少和某块工作区有交叠（用来发现"位置被存到屏幕外了"）。</summary>
    public static bool IsVisibleOnAny(IEnumerable<RectD> workAreas, RectD window)
    {
        foreach (var area in workAreas)
        {
            if (area.Width <= 0 || area.Height <= 0)
            {
                continue;
            }

            if (area.IntersectsWith(window))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把窗口放进工作区。如果窗口比工作区还大，就贴左上角。</summary>
    public static RectD ClampInto(RectD workArea, RectD window)
    {
        var x = Clamp(window.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width));
        var y = Clamp(window.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height));
        return new RectD(x, y, window.Width, window.Height);
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? Math.Max(min, max) : value;
}
