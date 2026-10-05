using WorkTimer.Core.Classification;

namespace WorkTimer.Core.Timing;

/// <summary>计时状态机的状态。</summary>
public enum TimerState
{
    /// <summary>用户未开始。</summary>
    Stopped = 0,

    /// <summary>正常计时。</summary>
    Working = 1,

    /// <summary>已进入娱乐窗口但尚未超过阈值（"待判定"）。</summary>
    PendingEntertainment = 2,

    /// <summary>娱乐连续超阈值 → 自动挂起。</summary>
    SuspendedAuto = 3,

    /// <summary>用户手动暂停，或退出程序时保存。</summary>
    PausedByUser = 4,

    /// <summary>无输入超阈值。</summary>
    IdleAway = 5,
}

public static class TimerStateExtensions
{
    public static string ToDisplay(this TimerState s) => s switch
    {
        TimerState.Stopped => "未开始",
        TimerState.Working => "计时中",
        TimerState.PendingEntertainment => "待判定",
        TimerState.SuspendedAuto => "娱乐挂起",
        TimerState.PausedByUser => "已暂停",
        _ => "离开挂起",
    };

    /// <summary>该状态下主计时器是否会继续累加（用于界面配色与提示文案）。</summary>
    public static bool IsCounting(this TimerState s)
        => s is TimerState.Working or TimerState.PendingEntertainment;
}

/// <summary>会话进度快照，用于写库与界面展示。</summary>
public sealed record SessionProgress(
    long NetSeconds,
    long PendingSeconds,
    long SuspendedSeconds,
    long UserPausedSeconds,
    long IdleSeconds)
{
    public static readonly SessionProgress Empty = new(0, 0, 0, 0, 0);

    public long TotalSeconds => NetSeconds + PendingSeconds + SuspendedSeconds + UserPausedSeconds + IdleSeconds;
}

/// <summary>一个"窗口段"：连续相同（应用 + 站点 + 类别 + 是否计入）的采样被合并成一条。</summary>
public sealed class ActivitySegment
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public long? ProjectId { get; set; }
    public long? RunId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public int Seconds { get; set; }

    public Category Category { get; set; }
    public bool Counted { get; set; }

    /// <summary>展示用应用名，例如 "Visual Studio Code"。</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>浏览器站点平台名，非浏览器为 null。</summary>
    public string? SitePlatform { get; set; }

    /// <summary>完整标题。只有隐私设置允许时才写入。</summary>
    public string? TitleDetail { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrEmpty(SitePlatform) ? AppName : $"{AppName} · {SitePlatform}";

    public override string ToString()
        => $"[{StartedAt:HH:mm:ss}-{EndedAt:HH:mm:ss}] {Seconds,6}s {DisplayName} {CategoryExtensions.ToKey(Category)} counted={Counted}";
}

/// <summary>写入 events 表的一条事件。</summary>
public sealed record TimerEventRecord(
    long? SessionId,
    DateTimeOffset At,
    string Type,
    string? Category = null,
    string? ProcessName = null,
    string? SitePlatform = null,
    string? WindowTitle = null,
    string? WindowClass = null,
    string? Reason = null);

/// <summary>会话结束原因。</summary>
public static class StopReasons
{
    public const string UserStop = "USER_STOP";
    public const string UserExit = "USER_EXIT";
    public const string AutoEnd = "AUTO_END";
    public const string SwitchProject = "SWITCH_PROJECT";
    public const string Crash = "CRASH";
}
