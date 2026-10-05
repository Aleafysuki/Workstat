namespace WorkTimer.Core.Data;

/// <summary>项目记录。<c>DeletedAt</c> 非空表示在回收站里（软删除，30 天内可找回）。</summary>
public sealed class ProjectRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsDeleted => DeletedAt.HasValue;

    public override string ToString() => Name;
}

/// <summary>会话记录。</summary>
public sealed class SessionRecord
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>active（进行中）| paused（退出时暂停保存）| ended（已结束）。</summary>
    public string State { get; set; } = "active";

    public string? StopReason { get; set; }
    public long NetSeconds { get; set; }
    public long SuspendedSeconds { get; set; }
    public long UserPausedSeconds { get; set; }
    public long IdleSeconds { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>归属日 = StartedAt 的本地日期（跨天会话整段计入开始那天）。</summary>
    public string AttributionDay => StartedAt.ToString("yyyy-MM-dd");
}

/// <summary>按项目聚合的工时。</summary>
public sealed class ProjectTotal
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string? Color { get; set; }
    public long NetSeconds { get; set; }
    public long SuspendedSeconds { get; set; }
}

/// <summary>按日聚合的工时。</summary>
public sealed class DayTotal
{
    public string Day { get; set; } = string.Empty;
    public long NetSeconds { get; set; }
    public long SuspendedSeconds { get; set; }
}

/// <summary>
/// 被判定为"中性"（没命中任何规则）的应用汇总。
/// 设置页用它做"未识别应用一键归类"，这样新出现的软件不用手写 JSON。
/// </summary>
public sealed class UnknownProcessRecord
{
    public string ProcessName { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? SampleTitle { get; set; }
    public long TotalSeconds { get; set; }
    public int SegmentCount { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public string DisplayName => string.IsNullOrEmpty(AppName) ? ProcessName : AppName;
}
