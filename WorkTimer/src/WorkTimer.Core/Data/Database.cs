using Microsoft.Data.Sqlite;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.Core.Data;

/// <summary>
/// SQLite 连接与建表迁移。
/// 每次操作开一个短连接（本工具的写入频率很低：2 秒一次采样但只在窗口段闭合时落库），
/// 不做连接池，避免长时间持有文件句柄导致云盘同步失败、数据库被锁等难查的问题。
/// </summary>
public sealed class Database
{
    private const int TargetSchemaVersion = 1;

    private readonly string _path;
    private readonly string _connectionString;

    public Database(string path)
    {
        _path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 数据库放在 exe 同目录时可能被云盘同步、杀软扫描短暂占用，给足重试时间
            DefaultTimeout = 15,
        }.ToString();
    }

    public string Path => _path;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>建库、建表、跑迁移、做一次完整性自检。</summary>
    public void Initialize()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();

        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");

        var quickCheck = ScalarString(connection, "PRAGMA quick_check;");
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Db", $"数据库完整性自检未通过：{quickCheck}（文件 {_path}）");
        }

        Migrations.Apply(connection, TargetSchemaVersion);
        Log.Info("Db", $"数据库就绪：{_path}（schema v{CurrentVersion(connection)}）");
    }

    public int CurrentVersion(SqliteConnection connection)
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

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn("Db", $"执行 {sql} 失败", ex);
            return string.Empty;
        }
    }
}
