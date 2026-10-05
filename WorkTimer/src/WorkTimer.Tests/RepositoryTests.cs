using WorkTimer.Core.Data;
using WorkTimer.Core.Timing;
using Xunit;

namespace WorkTimer.Tests;

/// <summary>
/// 数据层测试。重点验证两件容易出错的事：
///  1. 跨天会话的归属日（必须整段计入开始那天）；
///  2. "退出保存"的会话能被下次启动找回。
/// </summary>
public class RepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly Repository _repository;

    public RepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"worktimer-test-{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.Initialize();
        _repository = new Repository(_db);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                var file = _dbPath + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 测试清理失败不影响结论
            }
        }
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 首次运行会创建带日期的默认项目()
    {
        var project = _repository.EnsureDefaultProject();

        Assert.True(project.Id > 0);
        Assert.StartsWith("项目", project.Name);
        Assert.Single(_repository.GetProjects());
    }

    [Fact]
    public void 跨天会话整段计入开始那天()
    {
        var project = _repository.EnsureDefaultProject();
        var started = At(2026, 3, 5, 23, 30);

        var id = _repository.OnSessionStarted(started, project.Id, SessionProgress.Empty);
        _repository.OnSessionEnded(id, started.AddHours(1.5), new SessionProgress(5400, 0, 0, 0, 0), StopReasons.UserStop);

        var day5 = _repository.GetDailyTotals(At(2026, 3, 5, 0, 0), At(2026, 3, 5, 23, 59));
        var day6 = _repository.GetDailyTotals(At(2026, 3, 6, 0, 0), At(2026, 3, 6, 23, 59));

        var record = Assert.Single(day5);
        Assert.Equal(5400L, record.NetSeconds);
        Assert.Empty(day6);
    }

    [Fact]
    public void 区间总工时按会话归属日聚合()
    {
        var project = _repository.EnsureDefaultProject();

        var a = _repository.OnSessionStarted(At(2026, 3, 5, 9, 0), project.Id, SessionProgress.Empty);
        _repository.OnSessionEnded(a, At(2026, 3, 5, 11, 0), new SessionProgress(7200, 0, 600, 120, 300), StopReasons.UserStop);

        var b = _repository.OnSessionStarted(At(2026, 3, 6, 9, 0), project.Id, SessionProgress.Empty);
        _repository.OnSessionEnded(b, At(2026, 3, 6, 10, 0), new SessionProgress(3600, 0, 0, 0, 0), StopReasons.UserStop);

        var totals = _repository.GetRangeTotals(At(2026, 3, 5, 0, 0), At(2026, 3, 6, 23, 59));

        Assert.Equal(10800L, totals.NetSeconds);
        Assert.Equal(600L, totals.SuspendedSeconds);
    }

    [Fact]
    public void 退出保存的暂停会话能被找回并继续()
    {
        var project = _repository.EnsureDefaultProject();
        var started = At(2026, 3, 7, 9, 0);
        var id = _repository.OnSessionStarted(started, project.Id, SessionProgress.Empty);

        var progress = new SessionProgress(1800, 0, 0, 0, 0);
        _repository.SaveSessionPaused(id, progress);

        var found = _repository.FindPausedSession();

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
        Assert.Equal(1800L, found.NetSeconds);
        Assert.Equal("paused", found.State);
        Assert.Null(found.EndedAt);

        // 崩溃遗留（state=active）应该走另一条路，不会和退出保存混淆
        Assert.Null(_repository.FindCrashedSession());
    }

    [Fact]
    public void 崩溃遗留的会话能被识别并按心跳补记()
    {
        var project = _repository.EnsureDefaultProject();
        var started = At(2026, 3, 7, 9, 0);
        var id = _repository.OnSessionStarted(started, project.Id, SessionProgress.Empty);
        _repository.OnSessionProgress(id, new SessionProgress(900, 0, 0, 0, 0));

        var crashed = _repository.FindCrashedSession();
        Assert.NotNull(crashed);

        var heartbeat = At(2026, 3, 7, 9, 20);
        _repository.CloseCrashedSession(crashed!, heartbeat);

        Assert.Null(_repository.FindCrashedSession());
        var totals = _repository.GetRangeTotals(At(2026, 3, 7, 0, 0), At(2026, 3, 7, 23, 59));
        Assert.Equal(900L, totals.NetSeconds);
    }

    [Fact]
    public void 窗口段能按明细读回且保留站点平台()
    {
        var project = _repository.EnsureDefaultProject();
        var started = At(2026, 3, 8, 9, 0);
        var id = _repository.OnSessionStarted(started, project.Id, SessionProgress.Empty);

        _repository.OnSegments(new[]
        {
            new ActivitySegment
            {
                SessionId = id,
                ProjectId = project.Id,
                StartedAt = started,
                EndedAt = started.AddMinutes(45),
                Seconds = 2700,
                Category = WorkTimer.Core.Classification.Category.Work,
                Counted = true,
                AppName = "Visual Studio Code",
                ProcessName = "Code",
            },
            new ActivitySegment
            {
                SessionId = id,
                ProjectId = project.Id,
                StartedAt = started.AddMinutes(45),
                EndedAt = started.AddMinutes(60),
                Seconds = 900,
                Category = WorkTimer.Core.Classification.Category.Video,
                Counted = false,
                AppName = "Microsoft Edge",
                SitePlatform = "哔哩哔哩",
                ProcessName = "msedge",
            },
        });

        var segments = _repository.GetSegments(At(2026, 3, 8, 0, 0), At(2026, 3, 8, 23, 59));

        Assert.Equal(2, segments.Count);

        var video = segments.First(s => s.SitePlatform == "哔哩哔哩");
        Assert.Equal("Microsoft Edge · 哔哩哔哩", video.DisplayName);
        Assert.False(video.Counted);
        Assert.Equal(900, video.Seconds);
    }

    [Fact]
    public void 项目软删除后可找回且数据不丢()
    {
        var project = _repository.EnsureDefaultProject();
        var id = _repository.OnSessionStarted(At(2026, 3, 9, 9, 0), project.Id, SessionProgress.Empty);
        _repository.OnSessionEnded(id, At(2026, 3, 9, 10, 0), new SessionProgress(3600, 0, 0, 0, 0), StopReasons.UserStop);

        Assert.True(_repository.SoftDeleteProject(project.Id));
        Assert.Empty(_repository.GetProjects());                       // 活动列表里没有了
        Assert.Single(_repository.GetProjects(includeDeleted: true));  // 回收站里还在

        var totals = _repository.GetRangeTotals(At(2026, 3, 9, 0, 0), At(2026, 3, 9, 23, 59));
        Assert.Equal(3600L, totals.NetSeconds);                        // 数据没丢

        Assert.True(_repository.RestoreProject(project.Id));
        Assert.Single(_repository.GetProjects());
    }

    [Fact]
    public void 超期回收站项目会被彻底清理()
    {
        var project = _repository.EnsureDefaultProject();
        var id = _repository.OnSessionStarted(At(2026, 3, 9, 9, 0), project.Id, SessionProgress.Empty);
        _repository.OnSessionEnded(id, At(2026, 3, 9, 10, 0), new SessionProgress(3600, 0, 0, 0, 0), StopReasons.UserStop);
        _repository.SoftDeleteProject(project.Id);

        // 保留期 0 天 → 立刻算超期
        var purged = _repository.PurgeDeletedProjects(0);

        Assert.Equal(1, purged);
        Assert.Empty(_repository.GetProjects(includeDeleted: true));
        Assert.Equal(0L, _repository.GetRangeTotals(At(2026, 3, 1, 0, 0), At(2026, 3, 31, 23, 59)).NetSeconds);
    }

    [Fact]
    public void 重命名项目生效()
    {
        var project = _repository.EnsureDefaultProject();

        Assert.True(_repository.RenameProject(project.Id, "支付网关重构"));

        var reloaded = _repository.GetProjects().Single();
        Assert.Equal("支付网关重构", reloaded.Name);
    }

    [Fact]
    public void 未识别应用能被列出并一键归类回填历史()
    {
        var project = _repository.EnsureDefaultProject();
        var started = DateTimeOffset.Now.AddMinutes(-30);
        var id = _repository.OnSessionStarted(started, project.Id, SessionProgress.Empty);

        _repository.OnSegments(new[]
        {
            new ActivitySegment
            {
                SessionId = id,
                ProjectId = project.Id,
                StartedAt = started,
                EndedAt = started.AddMinutes(10),
                Seconds = 600,
                Category = WorkTimer.Core.Classification.Category.Neutral,
                Counted = true,
                AppName = "SomeNewApp",
                ProcessName = "SomeNewApp",
                TitleDetail = "某个未知软件的主界面",
            },
            new ActivitySegment
            {
                SessionId = id,
                ProjectId = project.Id,
                StartedAt = started.AddMinutes(10),
                EndedAt = started.AddMinutes(20),
                Seconds = 600,
                Category = WorkTimer.Core.Classification.Category.Neutral,
                Counted = true,
                AppName = "SomeNewApp",
                ProcessName = "SomeNewApp",
                TitleDetail = "某个未知软件的主界面",
            },
        });

        var unknowns = _repository.GetNeutralProcesses(30);

        var target = Assert.Single(unknowns);
        Assert.Equal("SomeNewApp", target.ProcessName);
        Assert.Equal(1200L, target.TotalSeconds);
        Assert.Equal(2, target.SegmentCount);

        // 一键归类为游戏，并把历史中性段一起回填
        var updated = _repository.ReclassifySegmentsByProcess("SomeNewApp", WorkTimer.Core.Classification.Category.Game);
        Assert.Equal(2, updated);
        Assert.Empty(_repository.GetNeutralProcesses(30));

        var segments = _repository.GetSegments(started.AddHours(-1), DateTimeOffset.Now.AddHours(1));
        Assert.All(segments, s =>
        {
            Assert.Equal(WorkTimer.Core.Classification.Category.Game, s.Category);
            Assert.False(s.Counted);
        });
    }

    // ================================================================
    // 数据锚点。这几个查询是「打开程序看不到历史记录」那个错觉的修复基础 ——
    // 一旦它们算错，界面就会又跳回空白的一天，所以单独覆盖。
    // ================================================================

    [Fact]
    public void 能查到最近有数据的日期()
    {
        var project = _repository.EnsureDefaultProject();

        var early = _repository.OnSessionStarted(At(2026, 3, 5, 9, 0), project.Id, SessionProgress.Empty);
        var late = _repository.OnSessionStarted(At(2026, 3, 9, 9, 0), project.Id, SessionProgress.Empty);

        _repository.OnSegments(new[]
        {
            new ActivitySegment
            {
                SessionId = early,
                ProjectId = project.Id,
                StartedAt = At(2026, 3, 5, 9, 0),
                EndedAt = At(2026, 3, 5, 9, 30),
                Seconds = 1800,
                Category = WorkTimer.Core.Classification.Category.Work,
                Counted = true,
                AppName = "Visual Studio Code",
                ProcessName = "Code",
            },
            new ActivitySegment
            {
                SessionId = late,
                ProjectId = project.Id,
                StartedAt = At(2026, 3, 9, 9, 0),
                EndedAt = At(2026, 3, 9, 9, 25),
                Seconds = 1500,
                Category = WorkTimer.Core.Classification.Category.Work,
                Counted = true,
                AppName = "Visual Studio Code",
                ProcessName = "Code",
            },
        });

        Assert.Equal("2026-03-09", _repository.GetLatestSessionDay());
        Assert.Equal("2026-03-09", _repository.GetLatestSegmentDay());

        Assert.True(_repository.HasSessionsOnDay("2026-03-09"));
        Assert.True(_repository.HasSegmentsOnDay("2026-03-05"));

        // 中间那些既没会话也没明细的日子必须报 false，
        // 否则界面会把视图锚到一个同样空白的日期上，等于没修
        Assert.False(_repository.HasSessionsOnDay("2026-03-07"));
        Assert.False(_repository.HasSegmentsOnDay("2026-03-07"));
    }

    [Fact]
    public void 没有任何数据时锚点查询返回空而不是抛异常()
    {
        Assert.Null(_repository.GetLatestSessionDay());
        Assert.Null(_repository.GetLatestSegmentDay());
        Assert.False(_repository.HasSessionsOnDay("2026-03-05"));
        Assert.False(_repository.HasSegmentsOnDay("2026-03-05"));
    }

    [Fact]
    public void 项目重名检查会排除自己和回收站里的项目()
    {
        _repository.EnsureDefaultProject();
        var second = _repository.CreateProject("支付网关重构");

        Assert.True(_repository.ProjectNameExists("支付网关重构"));
        Assert.False(_repository.ProjectNameExists("支付网关重构", excludeId: second.Id));
        Assert.False(_repository.ProjectNameExists("一个还没建过的名字"));

        // 大小写不敏感：避免「Project Alpha」和「project alpha」两个看起来一样的项目并存
        var ascii = _repository.CreateProject("Project Alpha");
        Assert.True(_repository.ProjectNameExists("project alpha"));
        Assert.False(_repository.ProjectNameExists("project alpha", excludeId: ascii.Id));

        // 进了回收站的项目不再占用名字，否则删掉的项目会永久霸占它
        Assert.True(_repository.SoftDeleteProject(second.Id));
        Assert.False(_repository.ProjectNameExists("支付网关重构"));
    }
}
