using WorkTimer.Core.Data;

namespace WorkTimer.Core.Stats;

/// <summary>界面"概览"页需要的全部统计结果。</summary>
public sealed class StatsSummary
{
    public long TodayNetSeconds { get; set; }
    public long TodaySuspendedSeconds { get; set; }
    public long WeekNetSeconds { get; set; }
    public long MonthNetSeconds { get; set; }

    public List<ProjectTotal> TodayByProject { get; set; } = new();
    public List<DayTotal> Last7Days { get; set; } = new();

    /// <summary>今天"摸鱼"占比：挂起时长 /（有效 + 挂起）。</summary>
    public double TodaySuspendedRatio
    {
        get
        {
            var total = TodayNetSeconds + TodaySuspendedSeconds;
            return total <= 0 ? 0 : (double)TodaySuspendedSeconds / total;
        }
    }
}

/// <summary>
/// 统计聚合。
/// 口径固定：归属日 = 会话 began 的日期（<c>sessions.started_at</c>），
/// 跨天会话整段计入开始那天。所以这里全部从 sessions 聚合，不碰窗口段。
/// </summary>
public sealed class StatsService
{
    private readonly Repository _repository;

    public StatsService(Repository repository)
    {
        _repository = repository;
    }

    public StatsSummary GetSummary(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        // 周一为一周起点
        var offsetToMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-offsetToMonday);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var end = today.AddDays(1).AddSeconds(-1);

        var summary = new StatsSummary();

        var todayTotals = _repository.GetRangeTotals(today, end);
        summary.TodayNetSeconds = todayTotals.NetSeconds;
        summary.TodaySuspendedSeconds = todayTotals.SuspendedSeconds;

        summary.WeekNetSeconds = _repository.GetRangeTotals(weekStart, end).NetSeconds;
        summary.MonthNetSeconds = _repository.GetRangeTotals(monthStart, end).NetSeconds;

        summary.TodayByProject = _repository.GetProjectTotals(today, end);
        summary.Last7Days = _repository.GetDailyTotals(today.AddDays(-6), today);

        return summary;
    }
}
