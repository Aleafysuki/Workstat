using Microsoft.Data.Sqlite;
using WorkTimer.Core.Classification;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Timing;

namespace WorkTimer.Core.Data;

/// <summary>
/// 数据访问层，同时实现 <see cref="ITimerSink"/> 让计时引擎直接落库。
///
/// 统计口径（设计文档 §6，必须严格遵守）：
///  - 会话不切割时间，跨零点仍是一条；
///  - <b>归属日 = sessions.started_at 的本地日期</b>，跨天会话整段计入开始那天；
///  - 总工时一律从 sessions 聚合，窗口段只用于明细展示。
/// </summary>
public sealed class Repository : ITimerSink
{
    private readonly Database _db;
    private readonly object _writeGate = new();

    public Repository(Database db)
    {
        _db = db;
    }

    public Database Database => _db;

    private static string Iso(DateTimeOffset value) => value.ToString("O");

    private static DateTimeOffset ParseDate(object? value)
        => value is null or DBNull
            ? default
            : DateTimeOffset.TryParse(value.ToString(), out var parsed) ? parsed : default;

    private static DateTimeOffset? ParseDateOrNull(object? value)
        => value is null or DBNull ? null : DateTimeOffset.TryParse(value.ToString(), out var parsed) ? parsed : null;

    private static long LongOf(object? value)
        => value is null or DBNull ? 0 : Convert.ToInt64(value);

    // ================================================================ 项目

    /// <summary>保证至少有一个可用项目。首次运行时建 "项目yyyyMMdd"。</summary>
    public ProjectRecord EnsureDefaultProject()
    {
        var existing = GetProjects(includeDeleted: false);
        if (existing.Count > 0)
        {
            return existing[0];
        }

        var name = $"项目{DateTimeOffset.Now:yyyyMMdd}";
        var created = CreateProject(name);
        Log.Info("Db", $"首次运行，已创建默认项目「{name}」");
        return created;
    }

    public List<ProjectRecord> GetProjects(bool includeDeleted = false)
    {
        var list = new List<ProjectRecord>();
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT id, name, color, is_archived, deleted_at, sort_order, created_at FROM projects " +
                (includeDeleted ? string.Empty : "WHERE deleted_at IS NULL ") +
                "ORDER BY sort_order, id;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ProjectRecord
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Color = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsArchived = reader.GetInt64(3) != 0,
                    DeletedAt = ParseDateOrNull(reader.IsDBNull(4) ? null : reader.GetString(4)),
                    SortOrder = (int)reader.GetInt64(5),
                    CreatedAt = ParseDate(reader.GetString(6)),
                });
            }
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "读取项目列表失败", ex);
        }

        return list;
    }

    public ProjectRecord CreateProject(string name)
    {
        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO projects (name, color, sort_order, created_at) VALUES ($n, NULL, " +
                "(SELECT COALESCE(MAX(sort_order), 0) + 1 FROM projects), $c); " +
                "SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$c", Iso(DateTimeOffset.Now));

            var id = Convert.ToInt64(cmd.ExecuteScalar());
            Diag.RecordDbWrite();
            Log.Info("Db", $"创建项目 id={id} name={name}");

            return new ProjectRecord { Id = id, Name = name, CreatedAt = DateTimeOffset.Now };
        }
    }

    public bool RenameProject(long id, string newName)
    {
        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE projects SET name = $n WHERE id = $id;";
            cmd.Parameters.AddWithValue("$n", newName);
            cmd.Parameters.AddWithValue("$id", id);
            var rows = cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
            Log.Info("Db", $"重命名项目 id={id} → {newName}（影响 {rows} 行）");
            return rows > 0;
        }
    }

    /// <summary>
    /// 项目名是否已被占用（只算未删除的项目 —— projects 表上有「未删除项目名唯一」的条件索引）。
    ///
    /// 新建 / 重命名前先问一次，目的是把 SQLite 的唯一约束异常翻译成一句人话，
    /// 而不是把 "UNIQUE constraint failed: projects.name" 直接甩给用户。
    /// 比较用 NOCASE：让「项目A」和「项目a」也算重名，避免自己看混。
    /// </summary>
    public bool ProjectNameExists(string name, long? excludeId = null)
    {
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT EXISTS(SELECT 1 FROM projects " +
                "WHERE deleted_at IS NULL AND name = $n COLLATE NOCASE" +
                (excludeId.HasValue ? " AND id <> $id" : string.Empty) + ");";
            cmd.Parameters.AddWithValue("$n", name);
            if (excludeId.HasValue)
            {
                cmd.Parameters.AddWithValue("$id", excludeId.Value);
            }

            return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", $"检查项目名是否重复失败：{name}", ex);

            // 查不了就当不重名放行，让后面真正的 INSERT 去暴露问题 —— 总比拦着用户什么都不让做好
            return false;
        }
    }

    /// <summary>软删除：进回收站，数据保留，可找回。</summary>
    public bool SoftDeleteProject(long id)
    {
        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE projects SET deleted_at = $t WHERE id = $id;";
            cmd.Parameters.AddWithValue("$t", Iso(DateTimeOffset.Now));
            cmd.Parameters.AddWithValue("$id", id);
            var rows = cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
            Log.Warn("Db", $"软删除项目 id={id}（可在回收站找回）");
            return rows > 0;
        }
    }

    public bool RestoreProject(long id)
    {
        lock (_writeGate)
        {
            using var connection = _db.Open();
            var name = QueryProjectName(connection, id);
            if (name is null)
            {
                return false;
            }

            // 同名的活动项目可能已存在，恢复时加后缀避免撞唯一索引
            var target = name;
            var suffix = 1;
            while (ProjectNameExists(connection, target, id))
            {
                target = $"{name} (恢复{suffix++})";
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE projects SET deleted_at = NULL, name = $n WHERE id = $id;";
            cmd.Parameters.AddWithValue("$n", target);
            cmd.Parameters.AddWithValue("$id", id);
            var rows = cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
            Log.Info("Db", $"找回项目 id={id} → {target}");
            return rows > 0;
        }
    }

    /// <summary>彻底删除回收站里超过保留期的项目及其数据。返回删除的项目数。</summary>
    public int PurgeDeletedProjects(int retentionDays)
    {
        var cutoff = Iso(DateTimeOffset.Now.AddDays(-retentionDays));
        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                var ids = new List<long>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "SELECT id FROM projects WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff;";
                    cmd.Parameters.AddWithValue("$cutoff", cutoff);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        ids.Add(reader.GetInt64(0));
                    }
                }

                foreach (var id in ids)
                {
                    Exec(connection, transaction, "DELETE FROM activity_segments WHERE session_id IN (SELECT id FROM sessions WHERE project_id = $id);", ("$id", id));
                    Exec(connection, transaction, "DELETE FROM events WHERE session_id IN (SELECT id FROM sessions WHERE project_id = $id);", ("$id", id));
                    Exec(connection, transaction, "DELETE FROM entertainment_runs WHERE session_id IN (SELECT id FROM sessions WHERE project_id = $id);", ("$id", id));
                    Exec(connection, transaction, "DELETE FROM sessions WHERE project_id = $id;", ("$id", id));
                    Exec(connection, transaction, "DELETE FROM projects WHERE id = $id;", ("$id", id));
                }

                transaction.Commit();
                Diag.RecordDbWrite();

                if (ids.Count > 0)
                {
                    Log.Warn("Db", $"已彻底清理 {ids.Count} 个超期回收站项目（保留期 {retentionDays} 天）");
                }

                return ids.Count;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Diag.RecordDbError();
                Log.Error("Db", "清理回收站失败，已回滚", ex);
                return 0;
            }
        }
    }

    private static string? QueryProjectName(SqliteConnection connection, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM projects WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar()?.ToString();
    }

    private static bool ProjectNameExists(SqliteConnection connection, string name, long excludeId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM projects WHERE name = $n AND deleted_at IS NULL AND id <> $id;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$id", excludeId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        cmd.ExecuteNonQuery();
    }

    // ================================================================ ITimerSink

    public long OnSessionStarted(DateTimeOffset startedAt, long projectId, SessionProgress progress)
    {
        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO sessions (project_id, started_at, state, net_seconds, suspended_seconds,
                                      user_paused_seconds, idle_seconds, last_heartbeat_at)
                VALUES ($p, $s, 'active', $net, $susp, $pause, $idle, $hb);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$s", Iso(startedAt));
            cmd.Parameters.AddWithValue("$net", progress.NetSeconds);
            cmd.Parameters.AddWithValue("$susp", progress.SuspendedSeconds);
            cmd.Parameters.AddWithValue("$pause", progress.UserPausedSeconds);
            cmd.Parameters.AddWithValue("$idle", progress.IdleSeconds);
            cmd.Parameters.AddWithValue("$hb", Iso(startedAt));

            var id = Convert.ToInt64(cmd.ExecuteScalar());
            Diag.RecordDbWrite();
            return id;
        }
    }

    public void OnSessionProgress(long sessionId, SessionProgress progress)
    {
        if (sessionId <= 0)
        {
            return;
        }

        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE sessions SET net_seconds = $net, suspended_seconds = $susp,
                                    user_paused_seconds = $pause, idle_seconds = $idle,
                                    last_heartbeat_at = $hb
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$net", progress.NetSeconds);
            cmd.Parameters.AddWithValue("$susp", progress.SuspendedSeconds);
            cmd.Parameters.AddWithValue("$pause", progress.UserPausedSeconds);
            cmd.Parameters.AddWithValue("$idle", progress.IdleSeconds);
            cmd.Parameters.AddWithValue("$hb", Iso(DateTimeOffset.Now));
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
        }
    }

    public void OnSessionEnded(long sessionId, DateTimeOffset endedAt, SessionProgress progress, string stopReason)
    {
        if (sessionId <= 0)
        {
            return;
        }

        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE sessions SET ended_at = $e, state = 'ended', stop_reason = $r,
                                    net_seconds = $net, suspended_seconds = $susp,
                                    user_paused_seconds = $pause, idle_seconds = $idle,
                                    last_heartbeat_at = $e
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$e", Iso(endedAt));
            cmd.Parameters.AddWithValue("$r", stopReason);
            cmd.Parameters.AddWithValue("$net", progress.NetSeconds);
            cmd.Parameters.AddWithValue("$susp", progress.SuspendedSeconds);
            cmd.Parameters.AddWithValue("$pause", progress.UserPausedSeconds);
            cmd.Parameters.AddWithValue("$idle", progress.IdleSeconds);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
        }
    }

    public void OnSessionSavedForExit(long sessionId, SessionProgress progress)
        => SaveSessionPaused(sessionId, progress);

    /// <summary>退出程序时把会话置为 paused（ended_at 留空），下次启动可恢复继续。</summary>
    public void SaveSessionPaused(long sessionId, SessionProgress progress)
    {
        if (sessionId <= 0)
        {
            return;
        }

        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE sessions SET state = 'paused', stop_reason = $r,
                                    net_seconds = $net, suspended_seconds = $susp,
                                    user_paused_seconds = $pause, idle_seconds = $idle,
                                    last_heartbeat_at = $hb
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$r", StopReasons.UserExit);
            cmd.Parameters.AddWithValue("$net", progress.NetSeconds);
            cmd.Parameters.AddWithValue("$susp", progress.SuspendedSeconds);
            cmd.Parameters.AddWithValue("$pause", progress.UserPausedSeconds);
            cmd.Parameters.AddWithValue("$idle", progress.IdleSeconds);
            cmd.Parameters.AddWithValue("$hb", Iso(DateTimeOffset.Now));
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
            Log.Info("Db", $"会话 {sessionId} 已保存为暂停状态（退出），下次启动可继续");
        }
    }

    public void OnEvent(TimerEventRecord record)
    {
        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO events (session_id, at, type, category, process_name, site_platform,
                                    window_title, window_class, reason)
                VALUES ($s, $at, $t, $c, $p, $sp, $wt, $wc, $r);
                """;
            cmd.Parameters.AddWithValue("$s", (object?)record.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", Iso(record.At));
            cmd.Parameters.AddWithValue("$t", record.Type);
            cmd.Parameters.AddWithValue("$c", (object?)record.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object?)record.ProcessName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sp", (object?)record.SitePlatform ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wt", (object?)record.WindowTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wc", (object?)record.WindowClass ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", (object?)record.Reason ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
        }
    }

    public void OnSegments(IReadOnlyList<ActivitySegment> segments)
    {
        if (segments.Count == 0)
        {
            return;
        }

        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText =
                    """
                    INSERT INTO activity_segments (session_id, project_id, started_at, ended_at, seconds,
                                                   category, counted, run_id, app_name, site_platform,
                                                   title_detail, process_name)
                    VALUES ($s, $p, $st, $en, $sec, $cat, $cnt, $run, $app, $site, $title, $proc);
                    """;

                var pSession = cmd.Parameters.Add("$s", SqliteType.Integer);
                var pProject = cmd.Parameters.Add("$p", SqliteType.Integer);
                var pStart = cmd.Parameters.Add("$st", SqliteType.Text);
                var pEnd = cmd.Parameters.Add("$en", SqliteType.Text);
                var pSeconds = cmd.Parameters.Add("$sec", SqliteType.Integer);
                var pCategory = cmd.Parameters.Add("$cat", SqliteType.Text);
                var pCounted = cmd.Parameters.Add("$cnt", SqliteType.Integer);
                var pRun = cmd.Parameters.Add("$run", SqliteType.Integer);
                var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
                var pSite = cmd.Parameters.Add("$site", SqliteType.Text);
                var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
                var pProc = cmd.Parameters.Add("$proc", SqliteType.Text);

                foreach (var segment in segments)
                {
                    pSession.Value = segment.SessionId;
                    pProject.Value = (object?)segment.ProjectId ?? DBNull.Value;
                    pStart.Value = Iso(segment.StartedAt);
                    pEnd.Value = Iso(segment.EndedAt);
                    pSeconds.Value = segment.Seconds;
                    pCategory.Value = CategoryExtensions.ToKey(segment.Category);
                    pCounted.Value = segment.Counted ? 1 : 0;
                    pRun.Value = (object?)segment.RunId ?? DBNull.Value;
                    pApp.Value = segment.AppName;
                    pSite.Value = (object?)segment.SitePlatform ?? DBNull.Value;
                    pTitle.Value = (object?)segment.TitleDetail ?? DBNull.Value;
                    pProc.Value = segment.ProcessName;
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                Diag.RecordDbWrite();
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Diag.RecordDbError();
                Log.Error("Db", $"写入窗口段失败（{segments.Count} 条），已回滚", ex);
                throw;
            }
        }
    }

    // ================================================================ 恢复

    /// <summary>找出"退出时暂停"的会话，用于启动时提示继续。</summary>
    public SessionRecord? FindPausedSession()
    {
        return QuerySingleSession(
            "SELECT s.id, s.project_id, COALESCE(p.name,''), s.started_at, s.ended_at, s.state, s.stop_reason, " +
            "s.net_seconds, s.suspended_seconds, s.user_paused_seconds, s.idle_seconds, s.last_heartbeat_at " +
            "FROM sessions s LEFT JOIN projects p ON p.id = s.project_id " +
            "WHERE s.state = 'paused' AND s.ended_at IS NULL ORDER BY s.id DESC LIMIT 1;");
    }

    /// <summary>找出异常的未闭合会话（崩溃遗留），用于按心跳补记。</summary>
    public SessionRecord? FindCrashedSession()
    {
        return QuerySingleSession(
            "SELECT s.id, s.project_id, COALESCE(p.name,''), s.started_at, s.ended_at, s.state, s.stop_reason, " +
            "s.net_seconds, s.suspended_seconds, s.user_paused_seconds, s.idle_seconds, s.last_heartbeat_at " +
            "FROM sessions s LEFT JOIN projects p ON p.id = s.project_id " +
            "WHERE s.state = 'active' AND s.ended_at IS NULL ORDER BY s.id DESC LIMIT 1;");
    }

    /// <summary>把崩溃遗留的会话按心跳时刻补记为已结束。</summary>
    public void CloseCrashedSession(SessionRecord session, DateTimeOffset endedAt)
    {
        lock (_writeGate)
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "UPDATE sessions SET ended_at = $e, state = 'ended', stop_reason = $r WHERE id = $id;";
            cmd.Parameters.AddWithValue("$e", Iso(endedAt));
            cmd.Parameters.AddWithValue("$r", StopReasons.Crash);
            cmd.Parameters.AddWithValue("$id", session.Id);
            cmd.ExecuteNonQuery();
            Diag.RecordDbWrite();
            Log.Warn("Db", $"崩溃会话 {session.Id} 已按心跳时刻 {endedAt:yyyy-MM-dd HH:mm:ss} 补记结束");
        }
    }

    private SessionRecord? QuerySingleSession(string sql)
    {
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new SessionRecord
            {
                Id = reader.GetInt64(0),
                ProjectId = reader.GetInt64(1),
                ProjectName = reader.GetString(2),
                StartedAt = ParseDate(reader.GetString(3)),
                EndedAt = ParseDateOrNull(reader.IsDBNull(4) ? null : reader.GetString(4)),
                State = reader.GetString(5),
                StopReason = reader.IsDBNull(6) ? null : reader.GetString(6),
                NetSeconds = reader.GetInt64(7),
                SuspendedSeconds = reader.GetInt64(8),
                UserPausedSeconds = reader.GetInt64(9),
                IdleSeconds = reader.GetInt64(10),
                LastHeartbeatAt = ParseDateOrNull(reader.IsDBNull(11) ? null : reader.GetString(11)),
            };
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "查询会话失败", ex);
            return null;
        }
    }

    // ================================================================ 统计与明细

    /// <summary>
    /// 指定日期区间的总工时。含"进行中/暂停中"的会话（按当前累计值），
    /// 所以正在计时的时候界面上就能看到实时数字。
    /// </summary>
    public (long NetSeconds, long SuspendedSeconds) GetRangeTotals(DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT COALESCE(SUM(net_seconds), 0), COALESCE(SUM(suspended_seconds), 0)
                FROM sessions
                WHERE substr(started_at, 1, 10) >= $from AND substr(started_at, 1, 10) <= $to;
                """;
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "统计区间总工时失败", ex);
            return (0, 0);
        }
    }

    /// <summary>区间内的会话条数（按归属日）。</summary>
    public int GetSessionCount(DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT COUNT(1) FROM sessions
                WHERE substr(started_at, 1, 10) >= $from AND substr(started_at, 1, 10) <= $to;
                """;
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "统计会话数失败", ex);
            return 0;
        }
    }

    public List<ProjectTotal> GetProjectTotals(DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<ProjectTotal>();
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT s.project_id, COALESCE(p.name, '(已删除)'), p.color,
                       COALESCE(SUM(s.net_seconds), 0), COALESCE(SUM(s.suspended_seconds), 0)
                FROM sessions s
                LEFT JOIN projects p ON p.id = s.project_id
                WHERE substr(s.started_at, 1, 10) >= $from AND substr(s.started_at, 1, 10) <= $to
                GROUP BY s.project_id
                ORDER BY SUM(s.net_seconds) DESC;
                """;
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ProjectTotal
                {
                    ProjectId = reader.GetInt64(0),
                    ProjectName = reader.GetString(1),
                    Color = reader.IsDBNull(2) ? null : reader.GetString(2),
                    NetSeconds = reader.GetInt64(3),
                    SuspendedSeconds = reader.GetInt64(4),
                });
            }
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "统计项目工时失败", ex);
        }

        return list;
    }

    public List<DayTotal> GetDailyTotals(DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<DayTotal>();
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT substr(started_at, 1, 10) AS day,
                       COALESCE(SUM(net_seconds), 0), COALESCE(SUM(suspended_seconds), 0)
                FROM sessions
                WHERE substr(started_at, 1, 10) >= $from AND substr(started_at, 1, 10) <= $to
                GROUP BY day
                ORDER BY day;
                """;
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DayTotal
                {
                    Day = reader.GetString(0),
                    NetSeconds = reader.GetInt64(1),
                    SuspendedSeconds = reader.GetInt64(2),
                });
            }
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "统计每日工时失败", ex);
        }

        return list;
    }

    // ---------------------------------------------------------------- 数据锚点
    //
    // 存在的理由：界面打开时默认看「今天」。如果今天还没开始记录，概览和明细全是 0，
    // 用起来非常像「历史数据丢了」—— 其实一条都没丢，只是视图没落在有数据的日期上。
    // 所以这里给界面提供两个锚点查询：最近有数据的那一天、以及某一天到底有没有数据。

    /// <summary>最近一天有<b>窗口段明细</b>的日期（<c>yyyy-MM-dd</c>）。没有则返回 null。</summary>
    public string? GetLatestSegmentDay() => QueryLatestDay("activity_segments");

    /// <summary>最近一天有<b>会话</b>的日期（<c>yyyy-MM-dd</c>、按会话开始日期算）。没有则返回 null。</summary>
    public string? GetLatestSessionDay() => QueryLatestDay("sessions");

    private string? QueryLatestDay(string table)
    {
        // table 只由上面两个方法用字面量传入，不存在注入面
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT MAX(substr(started_at, 1, 10)) FROM {table};";
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", $"查询 {table} 最近日期失败", ex);
            return null;
        }
    }

    /// <summary>指定日期（<c>yyyy-MM-dd</c>）有没有窗口段明细。</summary>
    public bool HasSegmentsOnDay(string day) => HasRowsOnDay("activity_segments", day);

    /// <summary>指定日期（<c>yyyy-MM-dd</c>）有没有会话。</summary>
    public bool HasSessionsOnDay(string day) => HasRowsOnDay("sessions", day);

    private bool HasRowsOnDay(string table, string day)
    {
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE substr(started_at, 1, 10) = $d);";
            cmd.Parameters.AddWithValue("$d", day);
            return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", $"查询 {table} 在 {day} 是否有数据失败", ex);
            return false;
        }
    }

    /// <summary>窗口段明细（整合后的，不是每次采样一条）。</summary>
    public List<ActivitySegment> GetSegments(DateTimeOffset from, DateTimeOffset to, long? projectId = null, int limit = 500)
    {
        var list = new List<ActivitySegment>();
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"""
                 SELECT id, session_id, project_id, started_at, ended_at, seconds, category, counted,
                        run_id, app_name, site_platform, title_detail, process_name
                 FROM activity_segments
                 WHERE substr(started_at, 1, 10) >= $from AND substr(started_at, 1, 10) <= $to
                 {(projectId.HasValue ? "AND project_id = $pid" : string.Empty)}
                 ORDER BY started_at DESC
                 LIMIT {limit};
                 """;
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
            if (projectId.HasValue)
            {
                cmd.Parameters.AddWithValue("$pid", projectId.Value);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ActivitySegment
                {
                    Id = reader.GetInt64(0),
                    SessionId = reader.GetInt64(1),
                    ProjectId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    StartedAt = ParseDate(reader.GetString(3)),
                    EndedAt = ParseDate(reader.GetString(4)),
                    Seconds = (int)reader.GetInt64(5),
                    Category = CategoryExtensions.Parse(reader.GetString(6)),
                    Counted = reader.GetInt64(7) != 0,
                    RunId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    AppName = reader.GetString(9),
                    SitePlatform = reader.IsDBNull(10) ? null : reader.GetString(10),
                    TitleDetail = reader.IsDBNull(11) ? null : reader.GetString(11),
                    ProcessName = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                });
            }
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "读取窗口段明细失败", ex);
        }

        return list;
    }

    public int CountSegments()
    {
        try
        {
            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM activity_segments;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 找出被判成"中性"的应用（最近 N 天），按累计时长倒序。
    /// 设置页据此提供一键归类，用户不用为了一个没收录的软件去手写 JSON。
    /// </summary>
    public List<UnknownProcessRecord> GetNeutralProcesses(int days = 30, int limit = 100)
    {
        var list = new List<UnknownProcessRecord>();
        try
        {
            var from = DateTimeOffset.Now.AddDays(-days).ToString("yyyy-MM-dd");

            using var connection = _db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"""
                 SELECT process_name,
                        MAX(app_name)             AS app_name,
                        MAX(title_detail)         AS sample_title,
                        COALESCE(SUM(seconds), 0) AS total_seconds,
                        COUNT(1)                  AS seg_count,
                        MAX(started_at)           AS last_seen
                 FROM activity_segments
                 WHERE category = 'neutral' AND substr(started_at, 1, 10) >= $from
                 GROUP BY process_name
                 ORDER BY total_seconds DESC
                 LIMIT {limit};
                 """;
            cmd.Parameters.AddWithValue("$from", from);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new UnknownProcessRecord
                {
                    ProcessName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    AppName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    SampleTitle = reader.IsDBNull(2) ? null : reader.GetString(2),
                    TotalSeconds = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    SegmentCount = reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4),
                    LastSeen = ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5)),
                });
            }
        }
        catch (Exception ex)
        {
            Diag.RecordDbError();
            Log.Error("Db", "查询未识别应用失败", ex);
        }

        return list;
    }

    /// <summary>一键归类后，把该进程历史上被判为"中性"的窗口段一起改判。</summary>
    public int ReclassifySegmentsByProcess(string processName, Category category)
    {
        lock (_writeGate)
        {
            try
            {
                using var connection = _db.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE activity_segments
                    SET category = $cat, counted = $counted
                    WHERE process_name = $proc AND category = 'neutral';
                    """;
                cmd.Parameters.AddWithValue("$cat", CategoryExtensions.ToKey(category));
                cmd.Parameters.AddWithValue("$counted", category.IsCountable() ? 1 : 0);
                cmd.Parameters.AddWithValue("$proc", processName);

                var rows = cmd.ExecuteNonQuery();
                Diag.RecordDbWrite();
                Log.Info("Db", $"一键归类：{processName} → {CategoryExtensions.ToKey(category)}，回填 {rows} 条窗口段");
                return rows;
            }
            catch (Exception ex)
            {
                Diag.RecordDbError();
                Log.Error("Db", $"一键归类失败 process={processName}", ex);
                return 0;
            }
        }
    }
}
