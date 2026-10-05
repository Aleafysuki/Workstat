using WorkTimer.Core.Classification;
using WorkTimer.Core.Config;
using WorkTimer.Core.Monitoring;
using WorkTimer.Core.Timing;

namespace WorkTimer.Tests;

/// <summary>
/// 假时钟。计时逻辑全部基于单调时钟，所以用一个可任意快进的时钟，
/// 就能在几毫秒内验证"游戏 5 分钟后作废"这种真实世界要等 5 分钟的用例。
/// </summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(8));

    public TimeSpan Elapsed { get; set; }

    public void Advance(TimeSpan delta)
    {
        Elapsed += delta;
        Now = Now.Add(delta);
    }
}

/// <summary>记录所有落库调用的假 sink，用来断言"窗口段整合"与"事件"。</summary>
internal sealed class RecordingSink : ITimerSink
{
    public List<ActivitySegment> Segments { get; } = new();
    public List<TimerEventRecord> Events { get; } = new();
    public List<SessionProgress> Progress { get; } = new();
    public List<string> EndedReasons { get; } = new();
    public long NextSessionId { get; set; } = 100;

    public long OnSessionStarted(DateTimeOffset startedAt, long projectId, SessionProgress progress)
    {
        var id = NextSessionId++;
        StartedAt = startedAt;
        StartedProjectId = projectId;
        return id;
    }

    public DateTimeOffset StartedAt { get; private set; }
    public long StartedProjectId { get; private set; }

    public void OnSessionProgress(long sessionId, SessionProgress progress) => Progress.Add(progress);

    public void OnSessionEnded(long sessionId, DateTimeOffset endedAt, SessionProgress progress, string stopReason)
        => EndedReasons.Add(stopReason);

    public List<SessionProgress> ExitSaved { get; } = new();

    public void OnSessionSavedForExit(long sessionId, SessionProgress progress) => ExitSaved.Add(progress);

    public void OnEvent(TimerEventRecord record) => Events.Add(record);

    public void OnSegments(IReadOnlyList<ActivitySegment> segments) => Segments.AddRange(segments);

    public bool HasEvent(string type) => Events.Any(e => e.Type == type);
}

internal static class TestFactory
{
    public const int SelfPid = -1;

    public static AppConfig Config(
        int game = 5,
        int video = 10,
        int chat = 10,
        string mode = "min",
        bool retroactive = true,
        bool live = false,
        int idleMinutes = 20,
        bool suppressMeetingIdle = true,
        int autoEndMinutes = 60)
    {
        var config = new AppConfig();
        config.General.MinSegmentSeconds = 0;
        config.Timing.Thresholds.Game = game;
        config.Timing.Thresholds.Video = video;
        config.Timing.Thresholds.Chat = chat;
        config.Timing.CrossCategoryThresholdMode = mode;
        config.Timing.RetroactiveDeduct = retroactive;
        config.Timing.CountEntertainmentLive = live;
        config.Idle.ThresholdMinutes = idleMinutes;
        config.Idle.SuppressForMeetingApps = suppressMeetingIdle;
        config.Idle.AutoEndSessionAfterMinutes = autoEndMinutes;
        return config;
    }

    public static RuleEngine Rules(AppConfig config)
    {
        var engine = new RuleEngine(new SiteResolver(), SelfPid);
        engine.Update(Array.Empty<Rule>(), DefaultSites.Create(), config);
        return engine;
    }

    public static (TimerEngine Timer, FakeClock Clock, RecordingSink Sink, AppConfig Config) Build(
        AppConfig? config = null)
    {
        config ??= Config();
        var clock = new FakeClock();
        var rules = Rules(config);
        var sink = new RecordingSink();
        var timer = new TimerEngine(clock, rules, config, sink);
        return (timer, clock, sink, config);
    }

    public static WindowInfo Window(
        string process,
        string title = "",
        string className = "TestWindowClass",
        int idleSeconds = 0,
        bool fullscreen = false)
        => new(new IntPtr(0x1234), 4242, process, $@"C:\fake\{process}.exe", title, className, fullscreen, idleSeconds);

    /// <summary>按固定步长推进时钟并采样，模拟真实轮询。</summary>
    public static void TickFor(
        TimerEngine timer,
        FakeClock clock,
        WindowInfo window,
        TimeSpan duration,
        TimeSpan? step = null)
    {
        var s = step ?? TimeSpan.FromSeconds(2);
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            var d = remaining < s ? remaining : s;
            clock.Advance(d);
            timer.Tick(window);
            remaining -= d;
        }
    }

    public static void TickFor(TimerEngine timer, FakeClock clock, WindowInfo window, int seconds)
        => TickFor(timer, clock, window, TimeSpan.FromSeconds(seconds));
}
