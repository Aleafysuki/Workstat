using WorkTimer.Core.Classification;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.Core.Timing;

/// <summary>
/// 窗口段整合器。
///
/// 2 秒一次采样，如果每次都落库，一次"写代码 47 分钟"就是 1410 条记录，
/// 一天上千条，明细表没法看、归档也没法做。
/// 这里把连续相同的（应用 + 站点 + 类别 + 是否计入）采样合并成一个"窗口段"，
/// 只在状态真正变化时闭合上一条。
///
/// 另外处理"碎片"：短于 <c>minSeconds</c> 的段不会单独成条，而是并入前一条，
/// 避免 Alt+Tab 路过某个窗口留下一条 2 秒的噪音记录。
/// </summary>
public sealed class SegmentBuilder
{
    private sealed class Pending
    {
        public string Key = string.Empty;
        public TimeSpan StartElapsed;
        public DateTimeOffset StartedAt;
        public string AppName = string.Empty;
        public string? SitePlatform;
        public string ProcessName = string.Empty;
        public string? TitleDetail;
        public Category Category;
        public bool Counted;
        public long? RunId;
    }

    private readonly int _minSeconds;
    private readonly List<ActivitySegment> _closed = new();
    private Pending? _current;
    private ActivitySegment? _lastEmitted;

    public SegmentBuilder(int minSeconds)
    {
        _minSeconds = Math.Max(0, minSeconds);
    }

    public event Action<ActivitySegment>? SegmentClosed;

    /// <summary>当前会话 Id，写入每条段。</summary>
    public long SessionId { get; set; }

    /// <summary>当前项目 Id，写入每条段。</summary>
    public long? ProjectId { get; set; }

    /// <summary>当前所属娱乐运行的 Id，用于事后复盘"整段摸鱼"。</summary>
    public long? RunId { get; set; }

    /// <summary>已闭合但还没被取走的段。</summary>
    public IReadOnlyList<ActivitySegment> Closed => _closed;

    private static string BuildKey(string appName, string? site, string process, Category c, bool counted)
        => $"{appName}\u0001{site}\u0001{process}\u0001{(int)c}\u0001{counted}";

    public void Observe(
        TimeSpan now,
        DateTimeOffset wallNow,
        string appName,
        string? sitePlatform,
        string processName,
        Category category,
        bool counted,
        string? titleDetail)
    {
        var key = BuildKey(appName, sitePlatform, processName, category, counted);

        if (_current is not null && _current.Key == key)
        {
            return; // 同一个窗口段，什么都不用做；时长在闭合时按时间差算
        }

        CloseCurrent(now, wallNow);

        _current = new Pending
        {
            Key = key,
            StartElapsed = now,
            StartedAt = wallNow,
            AppName = appName,
            SitePlatform = sitePlatform,
            ProcessName = processName,
            Category = category,
            Counted = counted,
            TitleDetail = titleDetail,
            RunId = RunId,
        };
    }

    /// <summary>闭合当前段并返回它（可能因为太短被并入前一段而返回 null）。</summary>
    public ActivitySegment? CloseCurrent(TimeSpan now, DateTimeOffset wallNow)
    {
        if (_current is null)
        {
            return null;
        }

        var p = _current;
        _current = null;

        var seconds = (int)Math.Round((now - p.StartElapsed).TotalSeconds);
        if (seconds <= 0)
        {
            return null;
        }

        var segment = new ActivitySegment
        {
            SessionId = SessionId,
            ProjectId = ProjectId,
            RunId = p.RunId,
            StartedAt = p.StartedAt,
            EndedAt = p.StartedAt + TimeSpan.FromSeconds(seconds),
            Seconds = seconds,
            Category = p.Category,
            Counted = p.Counted,
            AppName = p.AppName,
            SitePlatform = p.SitePlatform,
            ProcessName = p.ProcessName,
            TitleDetail = p.TitleDetail,
        };

        if (segment.Seconds < _minSeconds && _lastEmitted is not null)
        {
            // 碎片并入相邻段：延长上一段，避免留下 2 秒的噪音记录
            _lastEmitted.EndedAt = segment.EndedAt;
            _lastEmitted.Seconds += segment.Seconds;
            Log.Debug("Timer", $"碎片段并入前一段：{segment.Seconds}s {segment.DisplayName}");
            return null;
        }

        _lastEmitted = segment;
        _closed.Add(segment);
        Diag.RecordSegment();
        SegmentClosed?.Invoke(segment);
        Log.Debug("Timer", $"窗口段闭合：{segment}");
        return segment;
    }

    /// <summary>取出并清空已闭合的段（写库用）。</summary>
    public List<ActivitySegment> Drain()
    {
        var list = new List<ActivitySegment>(_closed);
        _closed.Clear();
        return list;
    }

    /// <summary>切换会话/项目时重置，避免把上一个会话的最后一截串到新会话里。</summary>
    public void Reset(long sessionId, long? projectId)
    {
        SessionId = sessionId;
        ProjectId = projectId;
        _current = null;
        _lastEmitted = null;
        _closed.Clear();
        RunId = null;
    }

    /// <summary>当前是否处于某个未闭合的窗口段中（诊断用）。</summary>
    public string CurrentDescription => _current is null
        ? "<无>"
        : $"{_current.AppName}{(string.IsNullOrEmpty(_current.SitePlatform) ? "" : " · " + _current.SitePlatform)} " +
          $"{CategoryExtensions.ToKey(_current.Category)} counted={_current.Counted} " +
          $"自 {_current.StartedAt:HH:mm:ss}";
}
