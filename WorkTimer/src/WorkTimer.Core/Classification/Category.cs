namespace WorkTimer.Core.Classification;

/// <summary>
/// 窗口分类。字符串形式用于配置文件与数据库，<see cref="CategoryExtensions.Parse"/> 负责解析。
/// </summary>
public enum Category
{
    /// <summary>工作软件、文件资源管理器、会议协作类。</summary>
    Work = 0,

    /// <summary>游戏。</summary>
    Game = 1,

    /// <summary>视频。</summary>
    Video = 2,

    /// <summary>纯社交聊天。</summary>
    Chat = 3,

    /// <summary>无法判定，按兜底策略处理。</summary>
    Neutral = 4,

    /// <summary>
    /// 继承：没有前台窗口 / 系统外壳 / 本程序自身窗口。
    /// 这类情况必须保持上一个有效分类并<b>不改变计时状态</b>，
    /// 否则打开开始菜单、切回桌面就会打断计时。
    /// </summary>
    Inherit = 5,
}

public static class CategoryExtensions
{
    public static string ToKey(this Category c) => c switch
    {
        Category.Work => "work",
        Category.Game => "game",
        Category.Video => "video",
        Category.Chat => "chat",
        Category.Neutral => "neutral",
        _ => "inherit",
    };

    public static string ToDisplay(this Category c) => c switch
    {
        Category.Work => "工作",
        Category.Game => "游戏",
        Category.Video => "视频",
        Category.Chat => "聊天",
        Category.Neutral => "中性",
        _ => "继承",
    };

    public static Category Parse(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "work" => Category.Work,
        "game" => Category.Game,
        "video" => Category.Video,
        "chat" => Category.Chat,
        "neutral" => Category.Neutral,
        "inherit" => Category.Inherit,
        _ => Category.Neutral,
    };

    /// <summary>是否属于会导致挂起的娱乐类别。</summary>
    public static bool IsEntertainment(this Category c)
        => c is Category.Game or Category.Video or Category.Chat;

    /// <summary>是否算作有产出、应当计时。</summary>
    public static bool IsCountable(this Category c)
        => c is Category.Work or Category.Neutral;
}
