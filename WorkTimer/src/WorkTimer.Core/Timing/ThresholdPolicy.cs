using WorkTimer.Core.Classification;
using WorkTimer.Core.Config;

namespace WorkTimer.Core.Timing;

/// <summary>
/// 跨类别娱乐运行时的阈值取值策略。
///
/// 场景：一次连续娱乐里可能先玩游戏（阈值 5 分钟）再看视频（阈值 10 分钟）。
/// 整段该按多少分钟算？交给用户选，默认取最小（最严格）。
/// </summary>
public static class ThresholdPolicy
{
    public static int ResolveMinutes(
        string? mode,
        IReadOnlyList<Category> visited,
        IReadOnlyDictionary<Category, int> minutesByCategory)
    {
        if (visited.Count == 0)
        {
            return 0;
        }

        var values = new List<int>(visited.Count);
        foreach (var c in visited)
        {
            values.Add(minutesByCategory.TryGetValue(c, out var m) ? m : 10);
        }

        return (mode?.ToLowerInvariant()) switch
        {
            "max" => values.Max(),
            "sum" => values.Sum(),
            "avg" => (int)Math.Round(values.Average()),
            "current" => values[^1],
            _ => values.Min(),   // min 为默认
        };
    }

    public static Dictionary<Category, int> BuildThresholdTable(ThresholdsConfig t) => new()
    {
        [Category.Game] = t.Game,
        [Category.Video] = t.Video,
        [Category.Chat] = t.Chat,
    };

    public static string DescribeMode(string? mode) => (mode?.ToLowerInvariant()) switch
    {
        "max" => "取最大",
        "sum" => "累加",
        "avg" => "取均值",
        "current" => "取当前类别",
        _ => "取最小",
    };
}
