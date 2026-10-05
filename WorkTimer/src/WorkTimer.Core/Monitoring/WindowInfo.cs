namespace WorkTimer.Core.Monitoring;

/// <summary>
/// 一次前台窗口采样结果。<see cref="HasForeground"/> 为 false 表示
/// 当前没有任何窗口被激活（所有窗口失焦、切回桌面、开始菜单抢占等），
/// 这种情况下分类应落到 Inherit。
/// </summary>
public readonly record struct WindowInfo(
    IntPtr Hwnd,
    int Pid,
    string ProcessName,
    string ProcessPath,
    string Title,
    string ClassName,
    bool IsFullscreen,
    int IdleSeconds)
{
    public bool HasForeground => Hwnd != IntPtr.Zero;

    public static WindowInfo None(int idleSeconds = 0)
        => new(IntPtr.Zero, 0, string.Empty, string.Empty, string.Empty, string.Empty, false, idleSeconds);

    public override string ToString()
        => HasForeground
            ? $"{ProcessName}({Pid}) class={ClassName} fullscreen={IsFullscreen} title=\"{Title}\" idle={IdleSeconds}s"
            : $"<无前台窗口> idle={IdleSeconds}s";
}
