namespace WorkTimer.App;

/// <summary>时间显示格式统一放这里，保证主窗口、悬浮窗、托盘、明细表显示一致。</summary>
internal static class TimeFormat
{
    /// <summary>00:12:34（超过 24 小时继续累加小时数）。</summary>
    public static string Hms(long seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public static string Hms(double seconds) => Hms((long)Math.Round(seconds));

    /// <summary>6h12m / 12m 这种人读的短格式，用于统计卡片。</summary>
    public static string Short(long seconds)
    {
        if (seconds <= 0)
        {
            return "0m";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
        }

        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m" : $"{ts.Seconds}s";
    }

    /// <summary>用于明细表的时间段：14:03 - 14:47。</summary>
    public static string Range(DateTimeOffset from, DateTimeOffset to)
        => $"{from:HH:mm:ss} - {to:HH:mm:ss}";

    public static string DayLabel(string day) => day;
}
