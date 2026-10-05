using WorkTimer.Core.Classification;
using WorkTimer.Core.Config;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Monitoring;

namespace WorkTimer.Core.Timing;

/// <summary>会话与窗口段的落库出口。抽成接口是为了让 Core 不依赖数据库，单测可用假实现。</summary>
public interface ITimerSink
{
    long OnSessionStarted(DateTimeOffset startedAt, long projectId, SessionProgress progress);
    void OnSessionProgress(long sessionId, SessionProgress progress);
    void OnSessionEnded(long sessionId, DateTimeOffset endedAt, SessionProgress progress, string stopReason);

    /// <summary>退出程序时把会话保存为"暂停"（不写 ended_at），下次启动可以接着记。</summary>
    void OnSessionSavedForExit(long sessionId, SessionProgress progress);

    void OnEvent(TimerEventRecord record);
    void OnSegments(IReadOnlyList<ActivitySegment> segments);
}

/// <summary>不做任何事的 sink，用于单测与"临时禁用持久化"。</summary>
public sealed class NullTimerSink : ITimerSink
{
    public long OnSessionStarted(DateTimeOffset startedAt, long projectId, SessionProgress progress) => 0;
    public void OnSessionProgress(long sessionId, SessionProgress progress) { }
    public void OnSessionEnded(long sessionId, DateTimeOffset endedAt, SessionProgress progress, string stopReason) { }
    public void OnSessionSavedForExit(long sessionId, SessionProgress progress) { }
    public void OnEvent(TimerEventRecord record) { }
    public void OnSegments(IReadOnlyList<ActivitySegment> segments) { }
}

/// <summary>界面渲染用的不可变快照。UI 线程直接读它，避免和采样线程抢状态。</summary>
public sealed record TimerSnapshot
{
    public TimerState State { get; init; } = TimerState.Stopped;
    public bool HasSession { get; init; }
    public long? ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public DateTimeOffset? SessionStartedAt { get; init; }

    public long NetSeconds { get; init; }
    public long PendingSeconds { get; init; }
    public long SuspendedSeconds { get; init; }
    public long UserPausedSeconds { get; init; }
    public long IdleSeconds { get; init; }

    public string CurrentAppName { get; init; } = string.Empty;
    public string? CurrentSitePlatform { get; init; }
    public string CurrentCategoryKey { get; init; } = "neutral";
    public string CurrentCategoryDisplay { get; init; } = string.Empty;
    public string CurrentReason { get; init; } = string.Empty;
    public string CurrentWindowTitle { get; init; } = string.Empty;

    public double RunSeconds { get; init; }
    public double RunThresholdSeconds { get; init; }
    public string ThresholdMode { get; init; } = "min";

    public int IdleSecondsNow { get; init; }
    public string CurrentSegment { get; init; } = string.Empty;

    public static readonly TimerSnapshot Empty = new();

    public string DisplayCurrent => string.IsNullOrEmpty(CurrentSitePlatform)
        ? CurrentAppName
        : $"{CurrentAppName} · {CurrentSitePlatform}";
}

/// <summary>
/// 计时状态机 —— 整个工具的心脏。对应设计文档 §4.5。
///
/// 关键设计：
///  1. <b>EntertainmentRun</b>：娱乐类别之间连续累计（游戏→视频不清零），只有切回工作才结束；
///  2. <b>待判定模式</b>（默认）：娱乐段的时间先不进主计时器，run 结束才一次性结算 ——
///     数字只增不减；打开 <c>CountEntertainmentLive</c> 则实时计入、超阈值扣回（数字会回跳）；
///  3. <b>追溯作废</b>：run 超阈值 → 整段作废。两种显示模式下最终工时数据完全一致；
///  4. <b>Inherit</b>：无前台窗口 / 桌面 / 开始菜单 / 本程序窗口 → 完全不动状态，run 与计时都继续；
///  5. 一切时长基于单调时钟，用户改系统时间不影响工时。
/// </summary>
public sealed class TimerEngine
{
    private sealed class RunState
    {
        public long Id;
        public TimeSpan StartElapsed;
        public DateTimeOffset StartedAt;
        public readonly List<Category> Visited = new();
        public int ThresholdMinutes;
    }

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly RuleEngine _rules;
    private readonly SegmentBuilder _segments;
    private readonly AppConfig _config;

    private ITimerSink _sink;

    private double _workSeconds;
    private double _suspendedSeconds;
    private double _userPausedSeconds;
    private double _idleSeconds;

    /// <summary>连续"离开/挂起"的时长。切回工作清零，用来判断"是不是忘了停止计时"。</summary>
    private double _awayStreakSeconds;

    private RunState? _run;
    private bool _suspendedUntilWork;
    private long _nextRunId = 1;

    private TimeSpan _lastTick;
    private bool _hasLastTick;

    private ClassificationResult? _lastEffective;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    // 防抖：窗口必须连续占据前台 minSegmentSeconds 才真正被采纳。
    // 用"进程 + 窗口类"做身份，浏览器内切换标签页（同进程同类）不受防抖延迟影响，
    // 而微信弹窗、通知横幅这种跨进程的短暂抢占会被吸收掉。
    private WindowInfo _committedWindow = WindowInfo.None();
    private string _committedKey = string.Empty;
    private string _candidateKey = string.Empty;
    private TimeSpan _candidateSince;

    private long _sessionId;
    private bool _hasSession;
    private string _projectName = string.Empty;
    private DateTimeOffset _sessionStartedAt;

    public TimerEngine(IClock clock, RuleEngine rules, AppConfig config, ITimerSink? sink = null)
    {
        _clock = clock;
        _rules = rules;
        _config = config;
        _sink = sink ?? new NullTimerSink();
        _segments = new SegmentBuilder(config.General.MinSegmentSeconds);
    }

    /// <summary>状态变化通知。⚠️ 在采样线程上、引擎锁内触发，订阅方必须用 BeginInvoke 而不能阻塞等待 UI 线程。</summary>
    public event Action<TimerState, TimerState>? StateChanged;

    /// <summary>面向用户的事件提示（挂起、作废、自动结束等）。同样在采样线程触发。</summary>
    public event Action<string, string>? Notice;

    public TimerState State { get; private set; } = TimerState.Stopped;

    public TimerSnapshot Snapshot { get; private set; } = TimerSnapshot.Empty;

    public SegmentBuilder Segments => _segments;

    public long SessionId => _sessionId;

    public void SetSink(ITimerSink sink) => _sink = sink ?? new NullTimerSink();

    public void UpdateMinSegmentSeconds()
    {
        // SegmentBuilder 的碎片阈值在构造时固定；配置改动后重建。
        // 这里只做日志，实际重建由 App 层重新构造引擎完成（避免运行中丢状态）。
        Log.Info("Timer", $"防抖时长配置为 {_config.General.MinSegmentSeconds}s（下次启动完全生效）");
    }

    // ---------------------------------------------------------------- 生命周期

    public void Start(long projectId, string projectName)
    {
        lock (_gate)
        {
            if (_hasSession)
            {
                Log.Warn("Timer", "已有会话在运行，Start 被忽略");
                return;
            }

            ResetCounters();

            _sessionStartedAt = _clock.Now;
            _projectName = projectName;
            _sessionId = _sink.OnSessionStarted(_sessionStartedAt, projectId, BuildProgress());

            _hasSession = true;
            _segments.Reset(_sessionId, projectId);
            _lastPersist = _clock.Now;

            SetState(TimerState.Working);
            Emit("START", reason: $"项目 {projectName}");

            Log.Info("Timer", $"会话开始 session={_sessionId} project={projectName}({projectId})");
            UpdateSnapshot();
        }
    }

    /// <summary>恢复一条"退出时暂停"的会话，让用户点继续时接着记。</summary>
    public void RestorePausedSession(
        long sessionId,
        long projectId,
        string projectName,
        DateTimeOffset startedAt,
        SessionProgress progress)
    {
        lock (_gate)
        {
            if (_hasSession)
            {
                return;
            }

            _sessionId = sessionId;
            _hasSession = true;
            _projectName = projectName;
            _sessionStartedAt = startedAt;

            _workSeconds = progress.NetSeconds;
            _suspendedSeconds = progress.SuspendedSeconds;
            _userPausedSeconds = progress.UserPausedSeconds;
            _idleSeconds = progress.IdleSeconds;

            _segments.Reset(sessionId, projectId);
            _lastTick = _clock.Elapsed;
            _hasLastTick = true;

            SetState(TimerState.PausedByUser);
            Emit("RESUME_FROM_EXIT", reason: $"恢复上次会话（项目 {projectName}，开始于 {startedAt:yyyy-MM-dd HH:mm}）");

            Log.Info("Timer", $"已恢复暂停会话 session={sessionId} project={projectName} 已计 {progress.NetSeconds}s");
            UpdateSnapshot();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_hasSession || State is TimerState.Stopped or TimerState.PausedByUser)
            {
                return;
            }

            SettleRun("手动暂停");
            _segments.CloseCurrent(_clock.Elapsed, _clock.Now);
            FlushSegments();

            SetState(TimerState.PausedByUser);
            Emit("PAUSE");
            PersistProgress(force: true);

            Log.Info("Timer", "会话已暂停");
            UpdateSnapshot();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_hasSession || State != TimerState.PausedByUser)
            {
                return;
            }

            _lastTick = _clock.Elapsed;
            _hasLastTick = true;
            SetState(TimerState.Working);
            Emit("UNPAUSE");
            PersistProgress(force: true);

            Log.Info("Timer", "会话已继续");
            UpdateSnapshot();
        }
    }

    public void Stop(string stopReason = StopReasons.UserStop)
    {
        lock (_gate)
        {
            if (!_hasSession)
            {
                return;
            }

            SettleRun("停止");
            _segments.CloseCurrent(_clock.Elapsed, _clock.Now);

            var progress = BuildProgress();
            _sink.OnSessionProgress(_sessionId, progress);
            _sink.OnSessionEnded(_sessionId, _clock.Now, progress, stopReason);
            FlushSegments();
            Emit("STOP", reason: stopReason);

            Log.Info("Timer", $"会话结束 session={_sessionId} 原因={stopReason} 有效={progress.NetSeconds}s " +
                              $"挂起={progress.SuspendedSeconds}s 暂停={progress.UserPausedSeconds}s 空闲={progress.IdleSeconds}s");

            _hasSession = false;
            _sessionId = 0;

            // 会话结束了，连续离开时长必须一起归零 ——
            // 留着它的话，下一次会话的第一个采样就会立刻满足"连续离开超阈值"而被误结束。
            _awayStreakSeconds = 0;

            SetState(TimerState.Stopped);
            UpdateSnapshot();
        }
    }

    public void SwitchProject(long projectId, string projectName)
    {
        lock (_gate)
        {
            if (_hasSession)
            {
                Stop(StopReasons.SwitchProject);
            }

            Start(projectId, projectName);
        }
    }

    /// <summary>
    /// 退出程序时调用：把当前会话保存为"暂停"状态（<c>ended_at</c> 留空），
    /// 下次启动可以恢复继续。这和崩溃恢复是两条独立路径 —— 这里是干净的收尾。
    /// </summary>
    public void SaveAndSuspendForExit()
    {
        lock (_gate)
        {
            if (!_hasSession)
            {
                return;
            }

            SettleRun("退出程序");
            _segments.CloseCurrent(_clock.Elapsed, _clock.Now);
            FlushSegments();

            var progress = BuildProgress();
            _sink.OnSessionSavedForExit(_sessionId, progress);
            Emit("EXIT_SAVE", reason: "退出程序，会话保存为暂停状态，下次启动可继续");

            Log.Info("Timer", $"退出保存 session={_sessionId} 有效={progress.NetSeconds}s（下次启动可继续）");

            _hasSession = false;
            _sessionId = 0;
            SetState(TimerState.Stopped);
            UpdateSnapshot();
        }
    }

    // ---------------------------------------------------------------- 采样主循环

    public void Tick(WindowInfo window)
    {
        try
        {
            TickCore(window);
        }
        catch (Exception ex)
        {
            Diag.RecordTickError(ex);
            Log.Error("Timer", "采样处理异常（已吞掉，避免定时器停摆）", ex);
        }
    }

    private void TickCore(WindowInfo window)
    {
        lock (_gate)
        {
            var now = _clock.Elapsed;
            var wallNow = _clock.Now;

            var delta = _hasLastTick ? (now - _lastTick).TotalSeconds : 0;
            _lastTick = now;
            _hasLastTick = true;

            if (delta < 0)
            {
                delta = 0;
            }
            else if (delta > 300)
            {
                Log.Warn("Timer", $"采样间隔异常（{delta:F0}s），已截断为 300s。可能是系统休眠唤醒。");
                delta = 300;
            }

            // ── 没有会话就什么都不做 ─────────────────────────────────────────
            // 只判断 State == Stopped 是不够的：空闲路径里的「自动结束会话」会在<b>同一个 tick 内</b>
            // 把会话停掉，State 变成「未开始」之后，这个 tick 的后半段原本还会继续跑娱乐状态机 ——
            // 凭空把状态推到「待判定」，下一轮又被空闲兜底重复自动结束，
            // 于是变成每秒写一条日志、永不停歇的死循环。
            //
            // 更直接的后果是：没有会话时界面把「暂停 / 停止并保存」两个按钮都禁用了，
            // 用户看到的就是"无法暂停或停止"。（这是线上日志里刷了 3000+ 条的真实故障。）
            if (!_hasSession)
            {
                // 自愈：没有会话时状态机不该停在别处。
                // 一旦停在别处（例如旧版本卡死留下的状态），把它拉回「未开始」，
                // 否则界面会显示一个永远不会变化的状态，按钮也一直是灰的。
                if (State != TimerState.Stopped)
                {
                    Log.Warn("Timer", $"没有会话但状态停在「{State.ToDisplay()}」，已复位为「未开始」");
                    _run = null;
                    _suspendedUntilWork = false;
                    _awayStreakSeconds = 0;
                    SetState(TimerState.Stopped);
                    UpdateSnapshot();
                }

                return;
            }

            if (State == TimerState.Stopped)
            {
                return;
            }

            var effectiveWindow = ApplyDebounce(window, now);
            var effective = ResolveEffectiveCategory(effectiveWindow);
            var idleNow = ResolveIdleSeconds(window, effective);

            if (State == TimerState.PausedByUser)
            {
                _userPausedSeconds += delta;
                PersistProgress(force: false, wallNow);
                UpdateSnapshot(effective, effectiveWindow, idleNow);
                return;
            }

            HandleIdleTransitions(wallNow, delta, idleNow, effective, effectiveWindow);

            // 空闲路径里可能刚刚触发了「自动结束会话」，必须在这里重新确认会话还在。
            // 少了这一步，下面的 Accrue 会在没有会话的前提下继续推进娱乐状态机，
            // 就是那个每秒重复自动结束的死循环的成因。
            if (!_hasSession)
            {
                UpdateSnapshot(effective, effectiveWindow, idleNow);
                return;
            }

            if (State == TimerState.IdleAway)
            {
                PersistProgress(force: false, wallNow);
                UpdateSnapshot(effective, effectiveWindow, idleNow);
                return;
            }

            Accrue(now, wallNow, delta, effective, effectiveWindow);
            CheckAutoEnd();
            PersistProgress(force: false, wallNow);
            UpdateSnapshot(effective, effectiveWindow, idleNow);
        }
    }

    /// <summary>
    /// 防抖：只有连续占据前台达到 <c>minSegmentSeconds</c> 的窗口才会被采纳用于判定。
    /// 这样微信新消息弹窗、系统通知、输入法候选这些抢一兩秒前台的窗口不会打断计时，
    /// 也不会凭空开启一次"娱乐运行"。
    /// 空闲秒数始终取原始采样值，不受防抖影响。
    /// </summary>
    private WindowInfo ApplyDebounce(WindowInfo window, TimeSpan now)
    {
        var debounceSeconds = _config.General.MinSegmentSeconds;

        var key = window.HasForeground
            ? $"{window.ProcessName}\u0001{window.ClassName}"
            : "<no-foreground>";

        // 首次采样直接采纳，避免刚点开始的前几秒被算成"外壳窗口"
        if (_committedKey.Length == 0 || debounceSeconds <= 0)
        {
            _committedKey = key;
            _candidateKey = key;
            _candidateSince = now;
            _committedWindow = window;
            return window;
        }

        if (key == _committedKey)
        {
            _candidateKey = key;
            _candidateSince = now;
            _committedWindow = window;   // 标题会变（浏览器切标签），保持最新
            return window;
        }

        if (key != _candidateKey)
        {
            _candidateKey = key;
            _candidateSince = now;
        }

        if ((now - _candidateSince).TotalSeconds >= debounceSeconds)
        {
            _committedKey = key;
            _committedWindow = window;
            Log.Debug("Timer", $"防抖确认窗口切换 → {window}");
            return window;
        }

        // 还没稳定下来：沿用上一个已确认的窗口，但空闲秒数要用当前值
        return _committedWindow with { IdleSeconds = window.IdleSeconds };
    }

    /// <summary>
    /// 决定本次采样实际生效的分类。
    /// Inherit（无前台窗口 / 桌面 / 开始菜单 / 本程序窗口）时<b>沿用上一次的有效分类</b>，
    /// 这样打开开始菜单或切回桌面不会打断计时，娱乐 run 也继续累计。
    /// </summary>
    private ClassificationResult ResolveEffectiveCategory(WindowInfo window)
    {
        var cls = _rules.Classify(window);

        if (cls.Category != Category.Inherit)
        {
            _lastEffective = cls;
            return cls;
        }

        if (_lastEffective is null)
        {
            _lastEffective = new ClassificationResult
            {
                Category = Category.Work,
                AppName = "（桌面 / 系统外壳）",
                Reason = cls.Reason + "，尚无有效分类，按工作处理",
            };
        }

        return _lastEffective with { Reason = cls.Reason + " → 继承上一分类" };
    }

    private int ResolveIdleSeconds(WindowInfo window, ClassificationResult effective)
    {
        if (!_config.Idle.Enabled || _config.Idle.Action == "count")
        {
            return 0;
        }

        // 会议类应用必须抑制空闲检测：开会真的不动键鼠，不抑制必然被误判成"离开"
        if (_config.Idle.SuppressForMeetingApps && effective.SuppressIdle)
        {
            return 0;
        }

        // 可选的更保守策略：只有前台是娱乐才因空闲挂起（读代码看资料不受影响）
        if (_config.Idle.SuspendOnlyWhenEntertainment && !effective.Category.IsEntertainment())
        {
            return 0;
        }

        return window.IdleSeconds;
    }

    private void HandleIdleTransitions(
        DateTimeOffset wallNow,
        double delta,
        int idleNow,
        ClassificationResult effective,
        WindowInfo window)
    {
        var wasIdle = State == TimerState.IdleAway;
        var isIdle = idleNow >= _config.Idle.ThresholdMinutes * 60;

        if (isIdle && !wasIdle)
        {
            if (_config.Idle.Action == "stop")
            {
                Log.Warn("Timer", "空闲达到阈值且动作配置为 stop，结束会话");
                Notice?.Invoke("Timer", $"已停止计时：离开电脑超过 {_config.Idle.ThresholdMinutes} 分钟");
                Stop(StopReasons.AutoEnd);
                return;
            }

            EnterIdleAway(effective, window);
        }
        else if (!isIdle && wasIdle)
        {
            LeaveIdleAway(effective);
        }

        if (State == TimerState.IdleAway)
        {
            _idleSeconds += delta;
            _awayStreakSeconds += delta;
            ObserveSegment(_clock.Elapsed, wallNow, effective, counted: false, window);

            // 空闲路径必须也检查自动结束，否则"人走了忘记停止"永远不会被兜底关掉
            CheckAutoEnd();
        }
    }

    private void Accrue(TimeSpan now, DateTimeOffset wallNow, double delta, ClassificationResult effective, WindowInfo window)
    {
        // 双保险：没有会话就不累计时间，也不推进状态机。
        // 调用方（TickCore）已经挡了一道，这里再挡一道，避免以后新增调用点又踩同一个坑。
        if (!_hasSession)
        {
            return;
        }

        if (effective.Category.IsEntertainment())
        {
            AccrueEntertainment(now, wallNow, delta, effective, window);
        }
        else
        {
            AccrueWork(now, wallNow, delta, effective, window);
        }
    }

    private void AccrueEntertainment(TimeSpan now, DateTimeOffset wallNow, double delta, ClassificationResult effective, WindowInfo window)
    {
        if (_suspendedUntilWork)
        {
            // 已判定摸鱼：继续挂起，时间进挂起桶，直到切回工作
            _suspendedSeconds += delta;
            _awayStreakSeconds += delta;
            ObserveSegment(now, wallNow, effective, counted: false, window);
            return;
        }

        if (_run is null)
        {
            // 进入娱乐：先把"上一个 tick 到现在"这一小段仍属工作的时间结算掉，再开启 run
            _workSeconds += delta;
            _run = new RunState
            {
                Id = _nextRunId++,
                StartElapsed = now,
                StartedAt = wallNow,
            };
            _segments.RunId = _run.Id;
            Emit("RUN_BEGIN", effective, window, "进入娱乐窗口，开始累计娱乐连续时长");
        }

        if (!_run.Visited.Contains(effective.Category))
        {
            _run.Visited.Add(effective.Category);
        }

        _run.ThresholdMinutes = ComputeThresholdMinutes(effective, _run);

        var runSeconds = (now - _run.StartElapsed).TotalSeconds;
        if (runSeconds >= _run.ThresholdMinutes * 60.0)
        {
            DiscardRun(effective, window);
            _suspendedSeconds += delta;
            _awayStreakSeconds += delta;
            ObserveSegment(now, wallNow, effective, counted: false, window);
            return;
        }

        SetState(TimerState.PendingEntertainment);
        ObserveSegment(now, wallNow, effective, counted: false, window);
    }

    private void AccrueWork(TimeSpan now, DateTimeOffset wallNow, double delta, ClassificationResult effective, WindowInfo window)
    {
        if (_suspendedUntilWork)
        {
            // 挂起期的尾巴仍算挂起，然后恢复计时
            _suspendedSeconds += delta;
            _suspendedUntilWork = false;
            SetState(TimerState.Working);
            Emit("RESUME", effective, window, "切回工作窗口，恢复计时");
            Log.Info("Timer", "切回工作窗口，恢复计时");
        }
        else if (_run is not null)
        {
            // run 在阈值内结束 → 整段补入有效工时（数字只增不减）
            var runSeconds = (now - _run.StartElapsed).TotalSeconds;
            _workSeconds += runSeconds;
            var minutes = _run.ThresholdMinutes;

            Emit("RUN_END", effective, window,
                $"娱乐段 {runSeconds:F0}s 未超阈值（{minutes} 分钟），整段计入工时");
            Log.Info("Timer", $"娱乐段结束未超阈值：{runSeconds:F0}s，计入工时");

            _run = null;
            _segments.RunId = null;
            SetState(TimerState.Working);
        }
        else
        {
            _workSeconds += delta;
        }

        _awayStreakSeconds = 0;
        ObserveSegment(now, wallNow, effective, counted: true, window);
    }

    private void ObserveSegment(
        TimeSpan now,
        DateTimeOffset wallNow,
        ClassificationResult effective,
        bool counted,
        WindowInfo window)
    {
        string? title = effective.IsBrowser
            ? (_config.Privacy.BrowserDetailLevel == "full" ? window.Title : null)
            : (_config.Privacy.RecordAppWindowTitle ? window.Title : null);

        _segments.Observe(
            now, wallNow,
            string.IsNullOrEmpty(effective.AppName) ? window.ProcessName : effective.AppName,
            effective.SitePlatform,
            window.ProcessName,
            effective.Category,
            counted,
            title);

        FlushSegments();
    }

    // ---------------------------------------------------------------- run 结算

    private void DiscardRun(ClassificationResult effective, WindowInfo window)
    {
        if (_run is null)
        {
            return;
        }

        var runSeconds = (_clock.Elapsed - _run.StartElapsed).TotalSeconds;
        var minutes = _run.ThresholdMinutes;

        if (_config.Timing.RetroactiveDeduct)
        {
            _suspendedSeconds += runSeconds;
            Emit("RUN_DISCARDED", effective, window,
                $"娱乐连续 {runSeconds:F0}s 超过阈值 {minutes} 分钟，整段作废并挂起");
            Notice?.Invoke("Timer",
                $"已挂起：娱乐连续 {Humanize(runSeconds)}，超过 {minutes} 分钟阈值，该段全部作废");
        }
        else
        {
            _workSeconds += runSeconds;
            Emit("RUN_DISCARDED", effective, window,
                $"娱乐连续 {runSeconds:F0}s 超过阈值 {minutes} 分钟，宽松模式下前段仍计入");
            Notice?.Invoke("Timer", $"已挂起：娱乐连续超过 {minutes} 分钟阈值");
        }

        Log.Info("Timer", $"娱乐段超阈值：{runSeconds:F0}s ≥ {minutes} 分钟，追溯作废={_config.Timing.RetroactiveDeduct}");

        _run = null;
        _segments.RunId = null;
        _suspendedUntilWork = true;
        SetState(TimerState.SuspendedAuto);
    }

    /// <summary>把当前 run 收尾（暂停 / 停止 / 进入空闲时调用）。</summary>
    private void SettleRun(string closeReason)
    {
        if (_run is null)
        {
            return;
        }

        var runSeconds = (_clock.Elapsed - _run.StartElapsed).TotalSeconds;
        _workSeconds += runSeconds;

        Emit("RUN_END", reason:
            $"{closeReason}，娱乐段 {runSeconds:F0}s 未超阈值（{_run.ThresholdMinutes} 分钟）计入工时");
        Log.Info("Timer", $"娱乐段因「{closeReason}」收尾：{runSeconds:F0}s，计入工时");

        _run = null;
        _segments.RunId = null;
    }

    private int ComputeThresholdMinutes(ClassificationResult current, RunState run)
    {
        var table = ThresholdPolicy.BuildThresholdTable(_config.Timing.Thresholds);

        // 当前窗口命中规则的阈值覆盖，优先作用于当前类别
        if (current.ThresholdMinutesOverride is int over && over >= 0)
        {
            table[current.Category] = over;
        }

        return ThresholdPolicy.ResolveMinutes(_config.Timing.CrossCategoryThresholdMode, run.Visited, table);
    }

    // ---------------------------------------------------------------- 状态切换

    private void EnterIdleAway(ClassificationResult effective, WindowInfo window)
    {
        SettleRun("进入空闲");
        _suspendedUntilWork = false;
        _segments.RunId = null;

        SetState(TimerState.IdleAway);
        Emit("IDLE", effective, window,
            $"无输入已达 {window.IdleSeconds / 60} 分钟（阈值 {_config.Idle.ThresholdMinutes} 分钟）");

        Log.Info("Timer", $"进入空闲状态：idle={window.IdleSeconds}s 阈值={_config.Idle.ThresholdMinutes}min");
        Notice?.Invoke("Timer", $"已挂起：离开电脑超过 {_config.Idle.ThresholdMinutes} 分钟");
    }

    private void LeaveIdleAway(ClassificationResult effective)
    {
        SetState(TimerState.Working);
        _awayStreakSeconds = 0;
        Emit("IDLE_END", effective, null, "检测到输入，恢复计时");
        Log.Info("Timer", "检测到输入，退出空闲状态");
    }

    /// <summary>
    /// "忘了停止计时"兜底。
    /// 用<b>连续</b>离开/挂起时长而不是会话累计值：需求场景是人走了没关计时，
    /// 连续离开才算；否则正常工作一天里断续的摸鱼累加起来会被误判，
    /// 把还在上班的会话结束掉。
    /// </summary>
    private void CheckAutoEnd()
    {
        // 没有会话就没有"自动结束"可言。
        // 这个前置判断很关键：原来的顺序是"先写日志和提示、再调 Stop"，
        // 而 Stop 在没有会话时直接返回 —— 结果就是每秒重复写一条"自动结束会话"的日志
        // 却什么都不做（线上刷了 3000+ 条，日志文件被撑爆，也掩盖了真正的问题）。
        if (!_hasSession)
        {
            return;
        }

        var limitSeconds = _config.Idle.AutoEndSessionAfterMinutes * 60.0;
        if (_awayStreakSeconds < limitSeconds)
        {
            return;
        }

        Log.Warn("Timer", $"连续离开/挂起 {_awayStreakSeconds:F0}s 超过 {_config.Idle.AutoEndSessionAfterMinutes} 分钟，自动结束会话");
        Notice?.Invoke("Timer", $"已自动结束会话：连续离开或挂起超过 {_config.Idle.AutoEndSessionAfterMinutes} 分钟");
        Stop(StopReasons.AutoEnd);
    }

    private void SetState(TimerState next)
    {
        if (State == next)
        {
            return;
        }

        var previous = State;
        State = next;
        StateChanged?.Invoke(previous, next);
        Log.Debug("Timer", $"状态 {previous.ToDisplay()} → {next.ToDisplay()}");
    }

    // ---------------------------------------------------------------- 输出

    private void Emit(string type, ClassificationResult? cls = null, WindowInfo? window = null, string? reason = null)
    {
        try
        {
            _sink.OnEvent(new TimerEventRecord(
                _hasSession ? _sessionId : null,
                _clock.Now,
                type,
                cls is null ? null : CategoryExtensions.ToKey(cls.Category),
                window?.ProcessName,
                cls?.SitePlatform,
                cls is { IsBrowser: true } ? window?.Title : null,
                window?.ClassName,
                reason));
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Timer", $"写事件失败 type={type}", ex);
        }
    }

    private void FlushSegments()
    {
        var batch = _segments.Drain();
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            _sink.OnSegments(batch);
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Timer", $"写窗口段失败（{batch.Count} 条）", ex);
        }
    }

    private void PersistProgress(bool force, DateTimeOffset? wallNow = null)
    {
        if (!_hasSession)
        {
            return;
        }

        var now = wallNow ?? _clock.Now;
        if (!force && (now - _lastPersist).TotalSeconds < 15)
        {
            return;
        }

        _lastPersist = now;

        try
        {
            _sink.OnSessionProgress(_sessionId, BuildProgress());
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Timer", "写会话心跳失败", ex);
        }
    }

    private SessionProgress BuildProgress() => new(
        (long)Math.Round(_workSeconds),
        (long)Math.Round(_run is null ? 0 : (_clock.Elapsed - _run.StartElapsed).TotalSeconds),
        (long)Math.Round(_suspendedSeconds),
        (long)Math.Round(_userPausedSeconds),
        (long)Math.Round(_idleSeconds));

    private void ResetCounters()
    {
        _workSeconds = 0;
        _suspendedSeconds = 0;
        _userPausedSeconds = 0;
        _idleSeconds = 0;
        _awayStreakSeconds = 0;
        _run = null;
        _suspendedUntilWork = false;
        _nextRunId = 1;
        _lastEffective = null;
        _lastTick = _clock.Elapsed;
        _hasLastTick = true;
        _committedKey = string.Empty;
        _candidateKey = string.Empty;
        _candidateSince = TimeSpan.Zero;
        _committedWindow = WindowInfo.None();
    }

    private void UpdateSnapshot(ClassificationResult? cls = null, WindowInfo? window = null, int idleNow = 0)
    {
        var live = _config.Timing.CountEntertainmentLive;
        var runSeconds = _run is null ? 0 : Math.Max(0, (_clock.Elapsed - _run.StartElapsed).TotalSeconds);
        var previous = Snapshot;

        Snapshot = new TimerSnapshot
        {
            State = State,
            HasSession = _hasSession,
            ProjectId = _segments.ProjectId,
            ProjectName = _projectName,
            SessionStartedAt = _hasSession ? _sessionStartedAt : null,
            NetSeconds = (long)Math.Round(_workSeconds + (live ? runSeconds : 0)),
            PendingSeconds = (long)Math.Round(live ? 0 : runSeconds),
            SuspendedSeconds = (long)Math.Round(_suspendedSeconds),
            UserPausedSeconds = (long)Math.Round(_userPausedSeconds),
            IdleSeconds = (long)Math.Round(_idleSeconds),
            CurrentAppName = cls?.AppName ?? previous.CurrentAppName,
            CurrentSitePlatform = cls?.SitePlatform ?? previous.CurrentSitePlatform,
            CurrentCategoryKey = cls is null ? previous.CurrentCategoryKey : CategoryExtensions.ToKey(cls.Category),
            CurrentCategoryDisplay = cls is null ? previous.CurrentCategoryDisplay : CategoryExtensions.ToDisplay(cls.Category),
            CurrentReason = cls?.Reason ?? previous.CurrentReason,
            CurrentWindowTitle = window?.Title ?? previous.CurrentWindowTitle,
            RunSeconds = runSeconds,
            RunThresholdSeconds = (_run?.ThresholdMinutes ?? 0) * 60.0,
            ThresholdMode = _config.Timing.CrossCategoryThresholdMode,
            IdleSecondsNow = idleNow,
            CurrentSegment = _segments.CurrentDescription,
        };
    }

    /// <summary>项目名与开始时间由外部设置（引擎不关心项目表），只影响展示。</summary>
    public void SetSessionDisplay(string projectName, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            _projectName = projectName;
            if (_hasSession)
            {
                _sessionStartedAt = startedAt;
            }

            UpdateSnapshot();
        }
    }

    /// <summary>诊断页用的一行内部状态摘要。</summary>
    public string DebugSummary()
    {
        lock (_gate)
        {
            return $"state={State.ToDisplay()} hasSession={_hasSession} session={_sessionId} " +
                   $"work={_workSeconds:F0}s susp={_suspendedSeconds:F0}s pause={_userPausedSeconds:F0}s idle={_idleSeconds:F0}s " +
                   $"awayStreak={_awayStreakSeconds:F0}s run={( _run is null ? "-" : $"{_run.Id}/{_run.ThresholdMinutes}min/{string.Join("+", _run.Visited)}")} " +
                   $"suspendedUntilWork={_suspendedUntilWork} | seg={_segments.CurrentDescription}";
        }
    }

    public static string Humanize(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分"
            : $"{(int)ts.TotalMinutes} 分 {ts.Seconds} 秒";
    }
}
