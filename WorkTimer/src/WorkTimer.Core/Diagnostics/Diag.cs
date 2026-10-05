using System.Diagnostics;

namespace WorkTimer.Core.Diagnostics;

/// <summary>
/// 运行时诊断计数器。界面的"诊断"页直接读这里，用来回答
/// "程序还在正常轮询吗 / 一次轮询要多久 / 有没有在频繁抛异常"这三类问题。
/// 全部用 Interlocked，可从采样线程安全更新。
/// </summary>
public static class Diag
{
    private static long _tickCount;
    private static long _tickErrors;
    private static long _lastTickMs;      // 放大 100 倍存的整数，避免用 double 跨线程
    private static long _maxTickMs;
    private static long _segmentsClosed;
    private static long _dbWrites;
    private static long _dbErrors;

    public static long TickCount => Interlocked.Read(ref _tickCount);
    public static long TickErrors => Interlocked.Read(ref _tickErrors);
    public static long SegmentsClosed => Interlocked.Read(ref _segmentsClosed);
    public static long DbWrites => Interlocked.Read(ref _dbWrites);
    public static long DbErrors => Interlocked.Read(ref _dbErrors);

    public static double LastTickMs => Interlocked.Read(ref _lastTickMs) / 100.0;
    public static double MaxTickMs => Interlocked.Read(ref _maxTickMs) / 100.0;

    public static DateTimeOffset? LastTickAt { get; private set; }
    public static string LastTickError { get; private set; } = string.Empty;

    public static void RecordTick(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _tickCount);
        LastTickAt = DateTimeOffset.Now;

        var ms = (long)(elapsed.TotalMilliseconds * 100);
        Interlocked.Exchange(ref _lastTickMs, ms);

        // 只在变大时更新最大值
        long current;
        while (ms > (current = Interlocked.Read(ref _maxTickMs)))
        {
            if (Interlocked.CompareExchange(ref _maxTickMs, ms, current) == current)
            {
                break;
            }
        }
    }

    public static void RecordTickError(Exception ex)
    {
        Interlocked.Increment(ref _tickErrors);
        LastTickError = $"{DateTimeOffset.Now:HH:mm:ss} {ex.GetType().Name}: {ex.Message}";
    }

    public static void RecordSegment() => Interlocked.Increment(ref _segmentsClosed);
    public static void RecordDbWrite() => Interlocked.Increment(ref _dbWrites);
    public static void RecordDbError() => Interlocked.Increment(ref _dbErrors);

    public static void Reset()
    {
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _tickErrors, 0);
        Interlocked.Exchange(ref _lastTickMs, 0);
        Interlocked.Exchange(ref _maxTickMs, 0);
        Interlocked.Exchange(ref _segmentsClosed, 0);
        Interlocked.Exchange(ref _dbWrites, 0);
        Interlocked.Exchange(ref _dbErrors, 0);
        LastTickError = string.Empty;
    }
}
