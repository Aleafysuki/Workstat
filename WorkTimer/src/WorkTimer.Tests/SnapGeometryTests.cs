using WorkTimer.Core.Geometry;
using Xunit;

namespace WorkTimer.Tests;

/// <summary>
/// 悬浮窗贴边几何的回归测试。
/// 用户报告过两个具体问题，这里各有一条对应用例：
///  1. 贴右边展开时窗口右半截跑到屏幕外（展开只改尺寸没改坐标）；
///  2. 除右边外其它方向完全不吸附（默认只允许右边）。
/// </summary>
public class SnapGeometryTests
{
    private static readonly RectD Work = new(0, 0, 2880, 1710);

    [Fact]
    public void 贴右边展开时必须向左生长而不是顶出屏幕()
    {
        // 收起状态贴右边缘：宽 118
        var collapsed = new RectD(Work.Right - 118, 200, 118, 34);

        var expanded = SnapGeometry.Resolve(collapsed, SnapEdge.Right, 246, 66, Work);

        Assert.Equal(Work.Right, expanded.Right);          // 右边缘仍然贴边
        Assert.Equal(Work.Right - 246, expanded.Left);     // 左边缘往左移动
        Assert.True(expanded.Right <= Work.Right, "展开后不能超出屏幕右边界");
        Assert.True(expanded.Left >= Work.Left, "展开后不能超出屏幕左边界");
    }

    [Fact]
    public void 贴左边展开时必须向右生长()
    {
        var collapsed = new RectD(Work.Left, 200, 118, 34);

        var expanded = SnapGeometry.Resolve(collapsed, SnapEdge.Left, 246, 66, Work);

        Assert.Equal(Work.Left, expanded.Left);
        Assert.Equal(Work.Left + 246, expanded.Right);
        Assert.True(expanded.Right <= Work.Right);
    }

    [Fact]
    public void 贴下边展开时必须向上生长()
    {
        var collapsed = new RectD(500, Work.Bottom - 34, 118, 34);

        var expanded = SnapGeometry.Resolve(collapsed, SnapEdge.Bottom, 246, 66, Work);

        Assert.Equal(Work.Bottom, expanded.Bottom);
        Assert.Equal(Work.Bottom - 66, expanded.Top);
        Assert.True(expanded.Bottom <= Work.Bottom);
    }

    [Fact]
    public void 贴上边展开时必须向下生长()
    {
        var collapsed = new RectD(500, Work.Top, 118, 34);

        var expanded = SnapGeometry.Resolve(collapsed, SnapEdge.Top, 246, 66, Work);

        Assert.Equal(Work.Top, expanded.Top);
        Assert.True(expanded.Bottom <= Work.Bottom);
    }

    [Fact]
    public void 默认配置下四个方向都能吸附()
    {
        var size = new RectD(0, 0, 200, 60);

        Assert.Equal(SnapEdge.Left, SnapGeometry.Detect(new RectD(Work.Left + 5, 500, 200, 60), Work, "auto", 26));
        Assert.Equal(SnapEdge.Right, SnapGeometry.Detect(new RectD(Work.Right - 205, 500, 200, 60), Work, "auto", 26));
        Assert.Equal(SnapEdge.Top, SnapGeometry.Detect(new RectD(500, Work.Top + 3, 200, 60), Work, "auto", 26));
        Assert.Equal(SnapEdge.Bottom, SnapGeometry.Detect(new RectD(500, Work.Bottom - 63, 200, 60), Work, "auto", 26));

        // 离得远就不吸附
        Assert.Equal(SnapEdge.None, SnapGeometry.Detect(new RectD(1200, 700, 200, 60), Work, "auto", 26));
        _ = size;
    }

    [Fact]
    public void 限定单边时其它方向不吸附()
    {
        Assert.Equal(SnapEdge.None, SnapGeometry.Detect(new RectD(Work.Left + 5, 500, 200, 60), Work, "right", 26));
        Assert.Equal(SnapEdge.Right, SnapGeometry.Detect(new RectD(Work.Right - 205, 500, 200, 60), Work, "right", 26));
    }

    [Fact]
    public void 关闭吸附时任何位置都不吸附()
    {
        Assert.Equal(SnapEdge.None, SnapGeometry.Detect(new RectD(Work.Left, 500, 200, 60), Work, "none", 26));
    }

    [Fact]
    public void 拖到屏幕外时会被钳回工作区()
    {
        var result = SnapGeometry.Resolve(new RectD(-500, -300, 118, 34), SnapEdge.None, 246, 66, Work);

        Assert.Equal(Work.Left, result.Left);
        Assert.Equal(Work.Top, result.Top);

        var farRight = SnapGeometry.Resolve(new RectD(9999, 9999, 118, 34), SnapEdge.None, 246, 66, Work);
        Assert.True(farRight.Right <= Work.Right);
        Assert.True(farRight.Bottom <= Work.Bottom);
    }

    [Fact]
    public void 可以判断窗口是否还停留在某块屏幕内()
    {
        var screens = new[] { Work, new RectD(2880, 0, 1920, 1080) };

        Assert.True(SnapGeometry.IsVisibleOnAny(screens, new RectD(2800, 100, 200, 60)));
        Assert.True(SnapGeometry.IsVisibleOnAny(screens, new RectD(3000, 100, 200, 60)));
        Assert.False(SnapGeometry.IsVisibleOnAny(screens, new RectD(9000, 9000, 200, 60)));
    }

    [Fact]
    public void 窗口比工作区宽时贴左边而不是算出负坐标()
    {
        // 工作区宽 400，窗口宽 800：横向放不下 → 贴左边；纵向放得下 → 保持原位
        var tiny = new RectD(0, 0, 400, 300);
        var result = SnapGeometry.ClampInto(tiny, new RectD(50, 50, 800, 200));

        Assert.Equal(0, result.Left);
        Assert.Equal(50, result.Top);
    }
}
