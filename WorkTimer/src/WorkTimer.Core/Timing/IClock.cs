using System.Diagnostics;

namespace WorkTimer.Core.Timing;

/// <summary>
/// 时钟抽象。存在的唯一理由是<b>可测</b>：
/// 计时逻辑全部基于 <see cref="Elapsed"/>（单调时钟），单元测试注入假时钟就能
/// 在几毫秒内跑完"游戏 5 分钟后作废"这种需要几十分钟才能验证的用例，
/// 而且不受用户修改系统时间影响。
/// </summary>
public interface IClock
{
    /// <summary>墙钟时间，只用于展示与"会话归属日"。</summary>
    DateTimeOffset Now { get; }

    /// <summary>自程序启动以来的单调时间，用于一切时长计算。</summary>
    TimeSpan Elapsed { get; }
}

public sealed class SystemClock : IClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public DateTimeOffset Now => DateTimeOffset.Now;

    public TimeSpan Elapsed => _stopwatch.Elapsed;
}
