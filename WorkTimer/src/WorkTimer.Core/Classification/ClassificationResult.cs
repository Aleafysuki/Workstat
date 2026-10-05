namespace WorkTimer.Core.Classification;

/// <summary>一次分类的完整结果。Reason 与 MatchedRuleId 是给"诊断"页和判定纠错用的。</summary>
public sealed record ClassificationResult
{
    public Category Category { get; init; } = Category.Neutral;

    /// <summary>应用友好名（优先用规则备注，否则用进程名）。</summary>
    public string AppName { get; init; } = string.Empty;

    /// <summary>浏览器站点平台名；非浏览器为 null。</summary>
    public string? SitePlatform { get; init; }

    public bool IsBrowser { get; init; }

    /// <summary>命中的规则是否要求抑制空闲检测（会议类）。</summary>
    public bool SuppressIdle { get; init; }

    /// <summary>规则级阈值覆盖（分钟）。</summary>
    public int? ThresholdMinutesOverride { get; init; }

    /// <summary>判定依据的人话描述，直接显示在界面上，方便用户判断是不是误判。</summary>
    public string Reason { get; init; } = string.Empty;

    public string? MatchedRuleId { get; init; }

    /// <summary>明细表里显示的"应用/站点"名称。</summary>
    public string DisplayName => string.IsNullOrEmpty(SitePlatform) ? AppName : $"{AppName} · {SitePlatform}";

    public override string ToString()
        => $"{CategoryExtensions.ToKey(Category)}({CategoryExtensions.ToDisplay(Category)}) {DisplayName} <- {Reason}";
}
