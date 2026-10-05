using Microsoft.Data.Sqlite;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.Core.Data;

/// <summary>
/// 建表脚本与版本迁移。
/// 迁移是"只往前"的：每条迁移只跑一次，版本号记在 schema_version 里。
/// 新增字段一律走新的迁移条目，不要改历史条目 —— 否则老用户的库升不上来。
/// </summary>
internal static class Migrations
{
    private static readonly (int Version, string[] Statements)[] Steps =
    {
        (1, new[]
        {
            """
            CREATE TABLE IF NOT EXISTS projects (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              name         TEXT    NOT NULL,
              color        TEXT,
              is_archived  INTEGER NOT NULL DEFAULT 0,
              deleted_at   TEXT,
              sort_order   INTEGER NOT NULL DEFAULT 0,
              created_at   TEXT    NOT NULL
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_active_name
              ON projects(name) WHERE deleted_at IS NULL;
            """,
            """
            CREATE TABLE IF NOT EXISTS sessions (
              id                  INTEGER PRIMARY KEY AUTOINCREMENT,
              project_id          INTEGER NOT NULL REFERENCES projects(id),
              started_at          TEXT    NOT NULL,
              ended_at            TEXT,
              state               TEXT    NOT NULL DEFAULT 'active',
              stop_reason         TEXT,
              net_seconds         INTEGER NOT NULL DEFAULT 0,
              suspended_seconds   INTEGER NOT NULL DEFAULT 0,
              user_paused_seconds INTEGER NOT NULL DEFAULT 0,
              idle_seconds        INTEGER NOT NULL DEFAULT 0,
              note                TEXT,
              last_heartbeat_at   TEXT
            );
            """,
            "CREATE INDEX IF NOT EXISTS idx_sessions_started ON sessions(started_at);",
            "CREATE INDEX IF NOT EXISTS idx_sessions_state ON sessions(state);",
            """
            CREATE TABLE IF NOT EXISTS events (
              id            INTEGER PRIMARY KEY AUTOINCREMENT,
              session_id    INTEGER REFERENCES sessions(id),
              at            TEXT    NOT NULL,
              type          TEXT    NOT NULL,
              category      TEXT,
              process_name  TEXT,
              site_platform TEXT,
              window_title  TEXT,
              window_class  TEXT,
              reason        TEXT
            );
            """,
            "CREATE INDEX IF NOT EXISTS idx_events_at ON events(at);",
            "CREATE INDEX IF NOT EXISTS idx_events_session ON events(session_id);",
            """
            CREATE TABLE IF NOT EXISTS activity_segments (
              id            INTEGER PRIMARY KEY AUTOINCREMENT,
              session_id    INTEGER NOT NULL REFERENCES sessions(id),
              project_id    INTEGER,
              started_at    TEXT    NOT NULL,
              ended_at      TEXT    NOT NULL,
              seconds       INTEGER NOT NULL,
              category      TEXT    NOT NULL,
              counted       INTEGER NOT NULL,
              run_id        INTEGER,
              app_name      TEXT    NOT NULL,
              site_platform TEXT,
              title_detail  TEXT,
              process_name  TEXT
            );
            """,
            "CREATE INDEX IF NOT EXISTS idx_seg_started ON activity_segments(started_at);",
            "CREATE INDEX IF NOT EXISTS idx_seg_session ON activity_segments(session_id);",
            "CREATE INDEX IF NOT EXISTS idx_seg_project ON activity_segments(project_id, started_at);",
            """
            CREATE TABLE IF NOT EXISTS entertainment_runs (
              id             INTEGER PRIMARY KEY AUTOINCREMENT,
              session_id     INTEGER NOT NULL REFERENCES sessions(id),
              started_at     TEXT    NOT NULL,
              ended_at       TEXT,
              seconds        INTEGER NOT NULL DEFAULT 0,
              threshold_used INTEGER,
              discarded      INTEGER NOT NULL DEFAULT 0,
              categories     TEXT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS daily_rollup (
              day               TEXT    NOT NULL,
              project_id        INTEGER NOT NULL,
              net_seconds       INTEGER NOT NULL,
              suspended_seconds INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (day, project_id)
            );
            """,
            "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);",
        }),
    };

    public static void Apply(SqliteConnection connection, int targetVersion)
    {
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        var current = GetVersion(connection);

        foreach (var (version, statements) in Steps)
        {
            if (version <= current)
            {
                continue;
            }

            Log.Info("Db", $"执行数据库迁移 v{current} → v{version}（{statements.Length} 条语句）");

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var sql in statements)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }

                using (var versionCmd = connection.CreateCommand())
                {
                    versionCmd.Transaction = transaction;
                    versionCmd.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
                    versionCmd.Parameters.AddWithValue("$v", version);
                    versionCmd.ExecuteNonQuery();
                }

                transaction.Commit();
                current = version;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Log.Error("Db", $"迁移 v{version} 失败，已回滚。数据库仍停留在 v{current}", ex);
                throw;
            }
        }
    }

    private static int GetVersion(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
