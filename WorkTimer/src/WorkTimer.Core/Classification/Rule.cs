using System.Text.Json.Serialization;

namespace WorkTimer.Core.Classification;

/// <summary>
/// 一条分类规则。匹配顺序是 first-match-wins：用户规则 → 内置规则 → 兜底。
/// </summary>
public sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public bool Enabled { get; set; } = true;

    /// <summary>排序号，小的先判。用户规则与内置规则合并后统一排序。</summary>
    public int Order { get; set; } = 100;

    /// <summary>命中后归入的类别：work | game | video | chat | neutral。</summary>
    public string Category { get; set; } = "work";

    /// <summary>进程名模式（不含 .exe），支持 * 与 ? 通配，不区分大小写。</summary>
    public string? ProcessPattern { get; set; }

    /// <summary>窗口标题模式。</summary>
    public string? TitlePattern { get; set; }

    public bool TitleIsRegex { get; set; }

    /// <summary>窗口类名模式。</summary>
    public string? ClassPattern { get; set; }

    /// <summary>匹配方式：process | title | class | any | all。</summary>
    public string MatchMode { get; set; } = "process";

    /// <summary>覆盖所属类别的默认阈值（分钟）。null = 用类别默认值。</summary>
    public int? ThresholdMinutes { get; set; }

    /// <summary>
    /// 前台是这条规则命中的应用时，抑制空闲检测。
    /// 会议类必须打开，否则"开会 20 分钟没碰键鼠"会被误判成离开。
    /// </summary>
    public bool SuppressIdle { get; set; }

    /// <summary>备注，显示在设置界面的规则表里。</summary>
    public string? Note { get; set; }

    /// <summary>来源：user | builtin。运行时写入，不参与持久化比较。</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public Rule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Order = Order,
        Category = Category,
        ProcessPattern = ProcessPattern,
        TitlePattern = TitlePattern,
        TitleIsRegex = TitleIsRegex,
        ClassPattern = ClassPattern,
        MatchMode = MatchMode,
        ThresholdMinutes = ThresholdMinutes,
        SuppressIdle = SuppressIdle,
        Note = Note,
        IsBuiltIn = IsBuiltIn,
    };
}

/// <summary>
/// 站点定义。一次匹配同时产出"展示名"（platform）和"分类"（category），
/// 所以明细表能直接写"Microsoft Edge · 哔哩哔哩 15 分钟"，而不必存裸标题。
/// </summary>
public sealed class SiteDefinition
{
    /// <summary>展示名，例如 "哔哩哔哩"、"GitHub"。</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>归类：work | game | video | chat | neutral。</summary>
    public string Category { get; set; } = "work";

    /// <summary>标题关键词，命中任意一个即算匹配。</summary>
    public List<string> Patterns { get; set; } = new();

    /// <summary>备注。</summary>
    public string? Note { get; set; }

    /// <summary>来源：user | builtin。</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }
}

/// <summary>rules.user.json 的根对象。</summary>
public sealed class RuleFile
{
    public List<Rule> Rules { get; set; } = new();
}

/// <summary>sites.json 的根对象。</summary>
public sealed class SiteFile
{
    public List<SiteDefinition> Sites { get; set; } = new();

    /// <summary>站点都匹配不上时的兜底展示名。</summary>
    public string FallbackPlatform { get; set; } = "网页浏览";

    /// <summary>站点都匹配不上时的兜底类别（还会被 config.classification.browserFallback 覆盖）。</summary>
    public string FallbackCategory { get; set; } = "work";
}
