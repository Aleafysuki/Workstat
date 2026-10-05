using WorkTimer.Core.Config;
using WorkTimer.Core.Timing;
using Xunit;

namespace WorkTimer.Tests;

/// <summary>
/// 计时状态机的行为测试。这些用例是整个项目最重要的回归保护 ——
/// 阈值语义、追溯作废、继承分类只要改错一处，工时数字就会悄悄不准。
/// </summary>
public class TimerEngineTests
{
    [Fact]
    public void 工作窗口正常累计有效工时()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        Assert.Equal(TimerState.Working, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.NetSeconds, 56L, 62L);
    }

    [Fact]
    public void 没有前台窗口时继承上一分类且不打断计时()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        var before = timer.Snapshot.NetSeconds;

        // 切回桌面：窗口类 Progman → 归 Inherit
        TestFactory.TickFor(timer, clock, TestFactory.Window("explorer", string.Empty, "Progman"), 60);

        Assert.Equal(TimerState.Working, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.NetSeconds - before, 56L, 62L);
    }

    [Fact]
    public void 完全没有前台窗口时也继承上一分类()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        var before = timer.Snapshot.NetSeconds;

        TestFactory.TickFor(timer, clock, WorkTimer.Core.Monitoring.WindowInfo.None(), 60);

        Assert.Equal(TimerState.Working, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.NetSeconds - before, 56L, 62L);
    }

    [Fact]
    public void 游戏超过阈值后整段作废并挂起()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        var workBefore = timer.Snapshot.NetSeconds;

        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 320);

        Assert.Equal(TimerState.SuspendedAuto, timer.Snapshot.State);
        // 游戏段一秒都不应该进有效工时（最多差一个采样周期的容差）
        Assert.InRange(timer.Snapshot.NetSeconds, workBefore, workBefore + 6L);
        Assert.InRange(timer.Snapshot.SuspendedSeconds, 300L, 330L);
    }

    [Fact]
    public void 娱乐段未超阈值时整段计入工时()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 60);       // 阈值 300s，远未到
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        Assert.Equal(TimerState.Working, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.NetSeconds, 172L, 186L);
    }

    [Fact]
    public void 跨娱乐类别连续累计不清零()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 30);

        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 60);
        TestFactory.TickFor(timer, clock, TestFactory.Window("PotPlayerMini64"), 60);

        // 游戏 60s + 视频 60s 连续累计 120s，还没到最小阈值 300s
        Assert.Equal(TimerState.PendingEntertainment, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.RunSeconds, 112.0, 128.0);

        TestFactory.TickFor(timer, clock, TestFactory.Window("PotPlayerMini64"), 200);

        Assert.Equal(TimerState.SuspendedAuto, timer.Snapshot.State);
    }

    [Fact]
    public void 阈值模式取最小时游戏阈值作用于整段()
    {
        var config = TestFactory.Config(mode: "min", game: 5, video: 10);
        var (timer, clock, _, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 100);
        TestFactory.TickFor(timer, clock, TestFactory.Window("PotPlayerMini64"), 250);

        // 取最小 = 5 分钟 = 300s，run 已到 350s
        Assert.Equal(TimerState.SuspendedAuto, timer.Snapshot.State);
    }

    [Fact]
    public void 阈值模式取当前类别时切换类别会抬高阈值()
    {
        var config = TestFactory.Config(mode: "current", game: 5, video: 10);
        var (timer, clock, _, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 200);
        TestFactory.TickFor(timer, clock, TestFactory.Window("PotPlayerMini64"), 240);

        // 切到视频后阈值变成 10 分钟 = 600s，run 才 440s
        Assert.NotEqual(TimerState.SuspendedAuto, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.RunSeconds, 430.0, 450.0);
    }

    [Fact]
    public void 待判定模式下主计时器不动且数字只增不减()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        // 先进入娱乐窗口把 run 建起来（创建 run 的那一拍会把之前那 2s 结算成工作）
        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 6);
        var before = timer.Snapshot.NetSeconds;

        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 120);

        Assert.Equal(before, timer.Snapshot.NetSeconds);
        Assert.InRange(timer.Snapshot.PendingSeconds, 108L, 130L);
    }

    [Fact]
    public void 实时计入模式下娱乐段先增长再因超阈值扣回()
    {
        var config = TestFactory.Config(live: true);
        var (timer, clock, _, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        var before = timer.Snapshot.NetSeconds;

        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 120);
        Assert.True(timer.Snapshot.NetSeconds >= before + 110,
            $"实时计入模式下娱乐段时间应进入主计时器，实际 {timer.Snapshot.NetSeconds}，期望 ≥ {before + 110}");

        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 200);

        Assert.Equal(TimerState.SuspendedAuto, timer.Snapshot.State);
        // 超阈值后扣回，回到进入娱乐前的水平
        Assert.InRange(timer.Snapshot.NetSeconds, before, before + 6L);
    }

    [Fact]
    public void 宽松模式不作废而是把前段计入()
    {
        var config = TestFactory.Config(retroactive: false);
        var (timer, clock, _, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 320);

        Assert.Equal(TimerState.SuspendedAuto, timer.Snapshot.State);
        // 宽松模式下进入娱乐前的那段（含阈值内的部分）仍然计入
        Assert.True(timer.Snapshot.NetSeconds >= 300,
            $"宽松模式应保留前段工时，实际 {timer.Snapshot.NetSeconds}");
    }

    [Fact]
    public void 无输入超过阈值后挂起且不再累计工时()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        var before = timer.Snapshot.NetSeconds;

        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD", idleSeconds: 1200), 120);

        Assert.Equal(TimerState.IdleAway, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.NetSeconds, before - 3L, before + 3L);
        Assert.InRange(timer.Snapshot.IdleSeconds, 108L, 132L);
    }

    [Fact]
    public void 会议类应用前台时抑制空闲挂起()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("wemeetapp", idleSeconds: 3600), 120);

        Assert.Equal(TimerState.Working, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.NetSeconds, 112L, 126L);
    }

    [Fact]
    public void 连续离开超过阈值后自动结束会话()
    {
        var config = TestFactory.Config(idleMinutes: 1, autoEndMinutes: 5);
        var (timer, clock, sink, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD", idleSeconds: 120), 400);

        Assert.Equal(TimerState.Stopped, timer.Snapshot.State);
        Assert.Contains(StopReasons.AutoEnd, sink.EndedReasons);
    }

    [Fact]
    public void 暂停期间不计入工时()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        timer.Pause();
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 120);
        timer.Resume();
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        Assert.InRange(timer.Snapshot.NetSeconds, 112L, 126L);
        Assert.InRange(timer.Snapshot.UserPausedSeconds, 112L, 126L);
    }

    [Fact]
    public void 相同窗口的连续采样只产生一个窗口段()
    {
        var (timer, clock, sink, _) = TestFactory.Build();

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);
        timer.Stop();

        Assert.Single(sink.Segments);
        Assert.InRange(sink.Segments[0].Seconds, 56, 62);
    }

    [Fact]
    public void 退出时可从落库的进度恢复并继续计时()
    {
        var config = TestFactory.Config();
        var (timer, clock, _, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 120);
        var net = timer.Snapshot.NetSeconds;
        var savedAt = clock.Now;
        timer.Stop(StopReasons.UserExit);

        // 新一次启动，从数据库读出的进度恢复到暂停态
        var (restored, clock2, _, _) = TestFactory.Build(config);
        restored.RestorePausedSession(200, 1, "测试项目", savedAt, new SessionProgress(net, 0, 0, 0, 0));

        Assert.Equal(TimerState.PausedByUser, restored.Snapshot.State);
        Assert.Equal(net, restored.Snapshot.NetSeconds);

        restored.Resume();
        TestFactory.TickFor(restored, clock2, TestFactory.Window("WINWORD"), 60);

        Assert.InRange(restored.Snapshot.NetSeconds, net + 56L, net + 62L);
    }

    [Fact]
    public void 停止后再开始不会把上一个会话的时间带过来()
    {
        var (timer, clock, _, _) = TestFactory.Build();

        timer.Start(1, "项目A");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 300);
        timer.Stop();

        timer.Start(2, "项目B");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 30);

        Assert.InRange(timer.Snapshot.NetSeconds, 26L, 32L);
        Assert.Equal(2L, timer.Snapshot.ProjectId);
    }

    [Fact]
    public void 短暂抢占前台的弹窗不会开启娱乐运行()
    {
        var config = TestFactory.Config();
        config.General.MinSegmentSeconds = 5;
        var (timer, clock, sink, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        // 微信弹窗抢了 2 秒前台（防抖时长 5 秒，应被完全吸收）
        TestFactory.TickFor(timer, clock, TestFactory.Window("WeChat"), 2);
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        Assert.Equal(TimerState.Working, timer.Snapshot.State);
        Assert.False(sink.HasEvent("RUN_BEGIN"), "2 秒的弹窗不应该开启娱乐运行");
        Assert.InRange(timer.Snapshot.NetSeconds, 116L, 126L);
    }

    [Fact]
    public void 持续占住前台的娱乐窗口仍会被判定()
    {
        var config = TestFactory.Config();
        config.General.MinSegmentSeconds = 5;
        var (timer, clock, sink, _) = TestFactory.Build(config);

        timer.Start(1, "测试项目");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        // 稳定占住前台超过防抖时长后就该正常开始累计娱乐连续时长
        TestFactory.TickFor(timer, clock, TestFactory.Window("steam"), 120);

        Assert.True(sink.HasEvent("RUN_BEGIN"), "稳定占住前台的游戏窗口应该开启娱乐运行");
        Assert.Equal(TimerState.PendingEntertainment, timer.Snapshot.State);
    }

    // ================================================================
    // 自动结束会话的回归测试。
    //
    // 这两个用例来自一次真实故障：会话自动结束之后，同一个 tick 的后半段
    // 又在"没有会话"的前提下继续推进娱乐状态机（未开始 → 待判定 → 离开挂起），
    // 于是空闲兜底每秒重复触发一次"自动结束会话"，日志刷了 3000 多条却什么都没结束；
    // 同时因为没有会话，界面上的「暂停 / 停止并保存」被全部禁用 ——
    // 用户看到的现象就是"无法暂停或停止"。
    // ================================================================

    [Fact]
    public void 自动结束会话后不会每秒重复触发自动结束()
    {
        // 空闲 1 分钟算离开；连续离开 2 分钟就自动结束会话
        var config = TestFactory.Config(idleMinutes: 1, autoEndMinutes: 2);
        var (timer, clock, sink, _) = TestFactory.Build(config);

        var autoEndNotices = 0;
        timer.Notice += (_, message) =>
        {
            if (message.Contains("自动结束会话"))
            {
                autoEndNotices++;
            }
        };

        timer.Start(1, "项目A");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 10);

        // 切到娱乐窗口 —— 故障现场前台正是 QQ，这一步是复现的关键：
        // 只有娱乐类别才会在"没有会话"的前提下被状态机重新推到「待判定」。
        var entertainment = TestFactory.Window("steam");
        TestFactory.TickFor(timer, clock, entertainment, 120);
        Assert.Equal(TimerState.PendingEntertainment, timer.Snapshot.State);

        // 人走了：空闲超过阈值 → 离开挂起；连续离开超过 2 分钟 → 自动结束会话
        var idleEntertainment = entertainment with { IdleSeconds = 120 };
        TestFactory.TickFor(timer, clock, idleEntertainment, 200);

        Assert.False(timer.Snapshot.HasSession);
        Assert.Equal(TimerState.Stopped, timer.Snapshot.State);
        Assert.Single(sink.EndedReasons);
        Assert.Equal(1, autoEndNotices);

        // 再采样 5 分钟。旧实现在这里会每秒重复触发一次"自动结束会话"，
        // 并把状态在「待判定」→「离开挂起」之间来回摆动，日志被刷爆。
        TestFactory.TickFor(timer, clock, idleEntertainment, 300);

        Assert.Equal(1, autoEndNotices);
        Assert.False(timer.Snapshot.HasSession);
        Assert.Equal(TimerState.Stopped, timer.Snapshot.State);
        Assert.Single(sink.EndedReasons);
    }

    [Fact]
    public void 自动结束之后再开新会话不会立刻又被自动结束()
    {
        var config = TestFactory.Config(idleMinutes: 1, autoEndMinutes: 2);
        var (timer, clock, sink, _) = TestFactory.Build(config);

        timer.Start(1, "项目A");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD", idleSeconds: 120), 200);
        Assert.False(timer.Snapshot.HasSession);
        Assert.Single(sink.EndedReasons);

        // 用户回来了，重新开始计时 —— 连续离开时长必须已经归零，
        // 否则新会话的第一个采样就会被判定成"离开太久"而立刻结束
        timer.Start(2, "项目B");
        TestFactory.TickFor(timer, clock, TestFactory.Window("WINWORD"), 60);

        Assert.True(timer.Snapshot.HasSession);
        Assert.Equal(TimerState.Working, timer.Snapshot.State);
        Assert.InRange(timer.Snapshot.NetSeconds, 56L, 62L);

        // 只有上一次的自动结束那一条，说明新会话没有被误结束
        Assert.Single(sink.EndedReasons);
    }
}
