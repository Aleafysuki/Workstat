namespace WorkTimer.Core.Monitoring;

/// <summary>
/// 系统级空闲检测。GetLastInputInfo 统计的是"最后一次输入"的时间点，
/// 鼠标移动、点击、滚轮、键盘、触摸都会刷新它，因此不需要额外挂 RawInput。
/// </summary>
public static class IdleDetector
{
    public static int GetIdleSeconds()
    {
        try
        {
            var info = new LASTINPUTINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>(),
            };

            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                return 0;
            }

            // 用无符号减法，天然处理 TickCount 回绕
            var now = unchecked((uint)Environment.TickCount);
            var elapsed = unchecked(now - info.dwTime);
            return (int)(elapsed / 1000u);
        }
        catch
        {
            return 0;
        }
    }
}
