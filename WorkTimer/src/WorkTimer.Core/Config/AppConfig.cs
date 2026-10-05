namespace WorkTimer.Core.Config;

/// <summary>
/// 应用配置。字段与设计文档 §7 一一对应。
/// 所有属性都可读写，JSON 序列化用 camelCase。
/// 每个类都提供 <c>Normalize()</c> 做一次取值兜底，避免用户手改配置文件写出
/// 越界值（比如阈值填 -1、采样间隔填 0）导致运行时出现难查的怪现象。
/// </summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 3;
    public GeneralConfig General { get; set; } = new();
    public TimingConfig Timing { get; set; } = new();
    public IdleConfig Idle { get; set; } = new();
    public ClassificationConfig Classification { get; set; } = new();
    public PrivacyConfig Privacy { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public FloatingWidgetConfig FloatingWidget { get; set; } = new();
    public NotificationConfig Notification { get; set; } = new();
    public HotkeyConfig Hotkeys { get; set; } = new();
    public UiConfig Ui { get; set; } = new();

    public void Normalize()
    {
        General.Normalize();
        Timing.Normalize();
        Idle.Normalize();
        Classification.Normalize();
        Privacy.Normalize();
        Storage.Normalize();
        FloatingWidget.Normalize();
    }
}

public sealed class GeneralConfig
{
    /// <summary>
    /// 前台窗口采样间隔（毫秒）。默认 1000（每秒一次）。
    /// 采样越密判定越及时；界面上计时显示会自己插值，所以不必靠提高采样率让数字"看起来在动"。
    /// </summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>窗口需连续占据前台多少秒才认定为"切换"（防抖）。默认 5。</summary>
    public int MinSegmentSeconds { get; set; } = 5;

    public string DataDirMode { get; set; } = "portable";
    public string? DataDirOverride { get; set; }

    public bool AutoStartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "light";
    /// <summary>日志最低级别：debug / info / warn / error。</summary>
    public string LogLevel { get; set; } = "debug";

    public void Normalize()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 250, 60_000);
        MinSegmentSeconds = Math.Clamp(MinSegmentSeconds, 0, 600);
    }
}

public sealed class TimingConfig
{
    public ThresholdsConfig Thresholds { get; set; } = new();

    /// <summary>跨娱乐类别时如何取阈值：min | avg | max | sum | current。默认 min。</summary>
    public string CrossCategoryThresholdMode { get; set; } = "min";

    /// <summary>娱乐段超阈值后是否整段作废（追溯扣除）。默认 true。</summary>
    public bool RetroactiveDeduct { get; set; } = true;

    /// <summary>娱乐段是否实时计入主计时器（数字会回退）。默认 false = 待判定模式，数字只增不减。</summary>
    public bool CountEntertainmentLive { get; set; }

    /// <summary>把 IM 强制归工作（有些日子要用 QQ/微信对接工作）。</summary>
    public TreatImAsWorkConfig TreatImAsWork { get; set; } = new();

    public void Normalize()
    {
        Thresholds.Normalize();
        CrossCategoryThresholdMode = CrossCategoryThresholdMode?.ToLowerInvariant() switch
        {
            "avg" or "max" or "sum" or "current" => CrossCategoryThresholdMode!.ToLowerInvariant(),
            _ => "min",
        };
    }
}

public sealed class ThresholdsConfig
{
    public int Game { get; set; } = 5;
    public int Video { get; set; } = 10;
    public int Chat { get; set; } = 10;

    public void Normalize()
    {
        Game = Math.Clamp(Game, 0, 600);
        Video = Math.Clamp(Video, 0, 600);
        Chat = Math.Clamp(Chat, 0, 600);
    }
}

public sealed class TreatImAsWorkConfig
{
    public bool Wechat { get; set; }
    public bool Qq { get; set; }
    public bool Tim { get; set; }
}

public sealed class IdleConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>无输入多少分钟后视为离开。默认 20。</summary>
    public int ThresholdMinutes { get; set; } = 20;

    /// <summary>空闲时的动作：suspend | count | stop。默认 suspend。</summary>
    public string Action { get; set; } = "suspend";

    /// <summary>前台是会议类应用时抑制空闲检测（开会确实不动键鼠）。默认 true。</summary>
    public bool SuppressForMeetingApps { get; set; } = true;

    /// <summary>仅当前台是娱乐窗口时才因空闲挂起。默认 false。</summary>
    public bool SuspendOnlyWhenEntertainment { get; set; }

    /// <summary>会话内空闲 + 挂起累计超过该分钟数就自动结束会话。默认 60。</summary>
    public int AutoEndSessionAfterMinutes { get; set; } = 60;

    public void Normalize()
    {
        ThresholdMinutes = Math.Clamp(ThresholdMinutes, 1, 600);
        AutoEndSessionAfterMinutes = Math.Clamp(AutoEndSessionAfterMinutes, 5, 24 * 60);
        Action = Action?.ToLowerInvariant() switch
        {
            "count" or "stop" => Action!.ToLowerInvariant(),
            _ => "suspend",
        };
    }
}

public sealed class ClassificationConfig
{
    /// <summary>浏览器标题无法识别站点时的兜底类别：work | neutral。</summary>
    public string BrowserFallback { get; set; } = "work";

    /// <summary>独占全屏且未命中任何规则时，视为疑似游戏。默认 false。</summary>
    public bool FullscreenGameFallback { get; set; }

    /// <summary>标题含"教程/课程"等关键词的视频视为工作。默认 false。</summary>
    public bool TreatTutorialVideoAsWork { get; set; }

    /// <summary>教程类关键词。</summary>
    public List<string> TutorialKeywords { get; set; } = new()
    {
        "教程", "课程", "讲座", "教学", "入门", "实战", "从零", "官方文档", "公开课",
    };

    public void Normalize()
    {
        BrowserFallback = string.Equals(BrowserFallback, "neutral", StringComparison.OrdinalIgnoreCase)
            ? "neutral"
            : "work";
    }
}

public sealed class PrivacyConfig
{
    /// <summary>浏览器明细记录粒度：platform（只记平台名，默认）| full（记完整标题）。</summary>
    public string BrowserDetailLevel { get; set; } = "platform";

    /// <summary>是否记录非浏览器应用的窗口标题。默认 true。</summary>
    public bool RecordAppWindowTitle { get; set; } = true;

    public void Normalize()
    {
        BrowserDetailLevel = string.Equals(BrowserDetailLevel, "full", StringComparison.OrdinalIgnoreCase)
            ? "full"
            : "platform";
    }
}

public sealed class StorageConfig
{
    /// <summary>明细超过多少天归档。默认 30。</summary>
    public int ArchiveSegmentsAfterDays { get; set; } = 30;

    /// <summary>回收站里已删项目的保留天数。默认 30。</summary>
    public int TrashRetentionDays { get; set; } = 30;

    public void Normalize()
    {
        ArchiveSegmentsAfterDays = Math.Clamp(ArchiveSegmentsAfterDays, 3, 3650);
        TrashRetentionDays = Math.Clamp(TrashRetentionDays, 1, 3650);
    }
}

public sealed class FloatingWidgetConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>拖到屏幕边缘是否自动吸附并收起。</summary>
    public bool SnapToEdge { get; set; } = true;

    /// <summary>吸附方向：auto（四边都吸附，默认）| left | right | top | bottom | none。</summary>
    public string SnapSide { get; set; } = "auto";

    /// <summary>收起后显示内容：timeOnly | projectAndTime。</summary>
    public string CollapsedContent { get; set; } = "timeOnly";

    /// <summary>不透明度 0.3 ~ 1.0。</summary>
    public double Opacity { get; set; } = 0.88;

    public bool AlwaysOnTop { get; set; } = true;

    public int? PositionX { get; set; }
    public int? PositionY { get; set; }

    public void Normalize()
    {
        Opacity = Math.Clamp(Opacity, 0.3, 1.0);
        SnapSide = SnapSide?.ToLowerInvariant() switch
        {
            "left" or "right" or "top" or "bottom" or "none" => SnapSide!.ToLowerInvariant(),
            _ => "auto",
        };
        CollapsedContent = string.Equals(CollapsedContent, "projectAndTime", StringComparison.OrdinalIgnoreCase)
            ? "projectAndTime"
            : "timeOnly";
    }
}

public sealed class NotificationConfig
{
    public bool NotifyOnSuspend { get; set; } = true;
    public bool NotifyOnAutoEnd { get; set; } = true;
}

public sealed class HotkeyConfig
{
    public string ToggleTimer { get; set; } = "Ctrl+Alt+S";
    public string OpenMain { get; set; } = "Ctrl+Alt+W";
    public string ToggleWidget { get; set; } = "Ctrl+Alt+F";
}

public sealed class UiConfig
{
    public bool ShowSeconds { get; set; } = true;
    public string TrayTooltipFormat { get; set; } = "{project} · {time}";
}
