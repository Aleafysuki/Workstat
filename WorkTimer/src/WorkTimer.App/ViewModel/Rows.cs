namespace WorkTimer.App.ViewModel;

/// <summary>概览页"按项目分布"表格的一行。</summary>
public sealed class ProjectRow
{
    public string ProjectName { get; init; } = string.Empty;
    public string NetText { get; init; } = string.Empty;
    public string RatioText { get; init; } = string.Empty;
    public string SuspendedText { get; init; } = string.Empty;
}

/// <summary>概览页"按天明细"表格的一行。</summary>
public sealed class DayRow
{
    public string Day { get; init; } = string.Empty;
    public string NetText { get; init; } = string.Empty;
    public string SuspendedText { get; init; } = string.Empty;
}

/// <summary>记录页的一行（一个窗口段）。</summary>
public sealed class SegmentRow
{
    public string Range { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Counted { get; init; } = string.Empty;
}

/// <summary>诊断页的一行键值。</summary>
public sealed class KvRow
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>未识别应用列表的一行。</summary>
public sealed class UnknownRow
{
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string TotalText { get; init; } = string.Empty;
    public string SampleTitle { get; init; } = string.Empty;
}
