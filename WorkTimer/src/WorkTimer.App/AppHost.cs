using System.Diagnostics;
using System.IO;
using WorkTimer.Core.Classification;
using WorkTimer.Core.Config;
using WorkTimer.Core.Data;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Monitoring;
using WorkTimer.Core.Stats;
using WorkTimer.Core.Timing;

namespace WorkTimer.App;

/// <summary>
/// 组合根：把 Core 的各层装起来，管住采样定时器，并把结果向外抛给界面。
///
/// 所有对外事件都在采样线程触发，界面订阅方必须用 BeginInvoke 回 UI 线程。
/// </summary>
public sealed class AppHost : IDisposable
{
    private System.Threading.Timer? _tickTimer;
    private int _ticking;
    private bool _disposed;

    public DataLocation Location { get; private set; } = null!;
    public AppConfig Config { get; private set; } = new();
    public ConfigStore ConfigStore { get; private set; } = null!;
    public RuleStore RuleStore { get; private set; } = null!;
    public SiteStore SiteStore { get; private set; } = null!;
    public Database Database { get; private set; } = null!;
    public Repository Repository { get; private set; } = null!;
    public StatsService Stats { get; private set; } = null!;
    public SiteResolver SiteResolver { get; private set; } = null!;
    public RuleEngine Rules { get; private set; } = null!;
    public ForegroundWindowWatcher Watcher { get; private set; } = null!;
    public TimerEngine Timer { get; private set; } = null!;

    public WindowInfo LastWindow { get; private set; } = WindowInfo.None();

    /// <summary>托盘控制器。界面需要用它弹气泡提示，所以在这里挂一个引用。</summary>
    public Services.TrayController? Tray { get; set; }

    /// <summary>启动时发现的"上次退出时暂停"的会话，由界面询问是否继续。</summary>
    public SessionRecord? PendingResumeSession { get; private set; }

    /// <summary>数据目录发生了回退时的原因，界面需要明确告诉用户。</summary>
    public string? DataDirectoryFallbackReason => Location.FallbackReason;

    public event Action<TimerSnapshot>? SnapshotUpdated;
    public event Action<string, string>? Notice;

    /// <summary>悬浮窗/托盘请求把主窗口拉到前台。</summary>
    public event Action? ShowMainWindowRequested;

    public void RaiseShowMainWindow() => ShowMainWindowRequested?.Invoke();
    public event Action? ProjectsChanged;

    public void Initialize()
    {
        Location = AppPaths.Resolve(ReadDataDirOverride());

        Log.Initialize(Location.LogDirectory, ParseLogLevel("debug"));
        Log.Info("App", $"程序启动，版本 {typeof(AppHost).Assembly.GetName().Version}");
        Log.Info("App", $"数据目录：{Location.Root}（便携模式={Location.IsPortable}）");

        if (Location.FallbackReason is { Length: > 0 } reason)
        {
            Log.Warn("App", reason);
        }

        ConfigStore = new ConfigStore(Location);
        Config = ConfigStore.Load();
        Log.MinLevel = ParseLogLevel(Config.General.LogLevel);

        RuleStore = new RuleStore(Location);
        RuleStore.Load();

        SiteStore = new SiteStore(Location);
        SiteStore.Load();

        Database = new Database(Location.DatabasePath);
        Database.Initialize();

        Repository = new Repository(Database);
        Stats = new StatsService(Repository);

        SiteResolver = new SiteResolver();
        Watcher = new ForegroundWindowWatcher();
        Rules = new RuleEngine(SiteResolver, Watcher.SelfPid);
        Rules.Update(RuleStore.Rules, SiteStore.File, Config);

        Timer = new TimerEngine(new SystemClock(), Rules, Config, Repository);
        Timer.Notice += (area, message) => Notice?.Invoke(area, message);

        RecoverSessions();
    }

    /// <summary>数据目录覆盖值需要在日志/配置就绪之前读出来，所以直接读一次原始 JSON。</summary>
    private static string? ReadDataDirOverride()
    {
        try
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "data", "config.json");
            if (!File.Exists(portable))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(portable));
            if (document.RootElement.TryGetProperty("general", out var general)
                && general.TryGetProperty("dataDirOverride", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch
        {
            // 配置有问题时忽略覆盖值，走默认目录
        }

        return null;
    }

    public static LogLevel ParseLogLevel(string? text) => text?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    /// <summary>处理上次遗留的会话：崩溃的自动补记，正常退出的交给用户决定是否继续。</summary>
    private void RecoverSessions()
    {
        try
        {
            var crashed = Repository.FindCrashedSession();
            if (crashed is not null)
            {
                var heartbeat = crashed.LastHeartbeatAt ?? crashed.StartedAt;
                Repository.CloseCrashedSession(crashed, heartbeat);
                Notice?.Invoke("App",
                    $"上次程序未正常结束，会话已按心跳时刻（{heartbeat:HH:mm}）补记，有效 {TimeFormat.Hms(crashed.NetSeconds)}");
            }

            PendingResumeSession = Repository.FindPausedSession();
            if (PendingResumeSession is not null)
            {
                Log.Info("App",
                    $"发现上次退出时暂停的会话 #{PendingResumeSession.Id}（项目 {PendingResumeSession.ProjectName}，" +
                    $"已计 {TimeFormat.Hms(PendingResumeSession.NetSeconds)}）");
            }
        }
        catch (Exception ex)
        {
            Log.Error("App", "会话恢复检查失败", ex);
        }
    }

    public void StartTicking()
    {
        var interval = Math.Max(250, Config.General.PollIntervalMs);
        _tickTimer?.Dispose();
        _tickTimer = new System.Threading.Timer(_ => SafeTick(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(interval));
        Log.Info("App", $"采样定时器已启动，间隔 {interval}ms");
    }

    private void SafeTick()
    {
        // System.Threading.Timer 的回调可能重入（尤其采样变慢时），加个闸门防止叠加
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
        {
            return;
        }

        try
        {
            TickOnce();
        }
        catch (Exception ex)
        {
            Diag.RecordTickError(ex);
            Log.Error("App", "采样循环异常（已吞掉，定时器继续）", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    public void TickOnce()
    {
        var stopwatch = Stopwatch.StartNew();

        LastWindow = Watcher.Capture();
        Timer.Tick(LastWindow);

        Diag.RecordTick(stopwatch.Elapsed);
        SnapshotUpdated?.Invoke(Timer.Snapshot);
    }

    public void ReconfigureTicking()
    {
        if (_tickTimer is not null)
        {
            StartTicking();
        }
    }

    /// <summary>重新载入规则与站点表（设置界面改完调用）。</summary>
    public void ReloadRules()
    {
        Rules.Update(RuleStore.Rules, SiteStore.File, Config);
        Log.Info("App", "规则与站点表已重新载入");
    }

    public List<ProjectRecord> GetProjects() => Repository.GetProjects();

    public ProjectRecord EnsureDefaultProject() => Repository.EnsureDefaultProject();

    public bool SaveConfig()
    {
        var ok = ConfigStore.Save();
        if (ok)
        {
            Log.MinLevel = ParseLogLevel(Config.General.LogLevel);
        }
        return ok;
    }

    public void NotifyProjectsChanged() => ProjectsChanged?.Invoke();

    /// <summary>界面处理完"是否继续上次会话"之后调用，避免重复询问。</summary>
    public void ClearPendingResumeSession() => PendingResumeSession = null;

    /// <summary>退出前的收尾：保存会话为暂停、停定时器。</summary>
    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _tickTimer?.Dispose();
            _tickTimer = null;

            Timer.SaveAndSuspendForExit();

            Log.Info("App", "程序退出");
            Log.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("App", "退出收尾失败", ex);
        }
    }

    public void Dispose() => Shutdown();

    /// <summary>悬浮窗引用，设置页改完参数后让它立刻重建外观。</summary>
    public Views.WidgetWindow? Widget { get; set; }

    /// <summary>设置页改了日志级别后立即生效。</summary>
    public void RefreshLogLevel()
    {
        Log.MinLevel = ParseLogLevel(Config.General.LogLevel);
        Log.Info("Config", $"日志级别已切换为 {Config.General.LogLevel}");
    }

    /// <summary>诊断页需要的一屏信息。</summary>
    public (string Key, string Value)[] BuildDiagnostics()
    {
        var snapshot = Timer.Snapshot;
        return new (string, string)[]
        {
            ("数据目录", Location.Root),
            ("便携模式", Location.IsPortable ? "是" : $"否（{Location.FallbackReason}）"),
            ("数据库", Location.DatabasePath),
            ("日志文件", Log.CurrentFilePath ?? "(未初始化)"),
            ("数据库大小", FormatSize(Location.DatabasePath)),
            ("窗口段总数", Repository.CountSegments().ToString()),
            ("采样次数", Diag.TickCount.ToString()),
            ("采样异常", Diag.TickErrors.ToString()),
            ("最近采样耗时", $"{Diag.LastTickMs:F1} ms"),
            ("最大采样耗时", $"{Diag.MaxTickMs:F1} ms"),
            ("采样间隔配置", $"{Config.General.PollIntervalMs} ms"),
            ("防抖时长", $"{Config.General.MinSegmentSeconds} s"),
            ("上次采样时刻", Diag.LastTickAt?.ToString("HH:mm:ss") ?? "-"),
            ("最后采样错误", string.IsNullOrEmpty(Diag.LastTickError) ? "无" : Diag.LastTickError),
            ("", ""),
            ("计时状态", $"{snapshot.State.ToDisplay()}（{snapshot.State}）"),
            ("会话 Id", Timer.SessionId.ToString()),
            ("有效 / 待判定", $"{TimeFormat.Hms(snapshot.NetSeconds)} / {TimeFormat.Hms(snapshot.PendingSeconds)}"),
            ("挂起 / 暂停 / 空闲", $"{snapshot.SuspendedSeconds}s / {snapshot.UserPausedSeconds}s / {snapshot.IdleSeconds}s"),
            ("系统空闲", $"{snapshot.IdleSecondsNow} s（阈值 {Config.Idle.ThresholdMinutes} 分钟）"),
            ("娱乐连续", $"{snapshot.RunSeconds:F0}s / 阈值 {snapshot.RunThresholdSeconds:F0}s（{ThresholdPolicy.DescribeMode(snapshot.ThresholdMode)}）"),
            ("", ""),
            ("当前前台", LastWindow.ToString()),
            ("判定结果", $"{snapshot.CurrentCategoryDisplay} · {snapshot.DisplayCurrent}"),
            ("判定依据", snapshot.CurrentReason),
            ("当前窗口段", snapshot.CurrentSegment),
            ("", ""),
            ("用户规则数", RuleStore.Rules.Count.ToString()),
            ("站点表条目", SiteStore.File.Sites.Count.ToString()),
            ("引擎内部状态", Timer.DebugSummary()),
        };
    }

    private static string FormatSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length / 1024.0:F1} KB" : "(不存在)";
        }
        catch
        {
            return "(读取失败)";
        }
    }
}
