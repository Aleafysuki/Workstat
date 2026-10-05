using System.Text;

namespace WorkTimer.Core.Diagnostics;

/// <summary>日志级别。数值越大越严重。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 一条日志。Area 是稳定的短标签（App/Config/Db/Monitor/Classify/Timer/Tray/Widget），
/// 用来在日志文件里按模块快速过滤定位问题。
/// </summary>
public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Area, string Message, string? Detail = null)
{
    public string LevelTag => Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    public override string ToString()
    {
        var sb = new StringBuilder()
            .Append(At.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(LevelTag).Append(']')
            .Append(" [").Append(Area).Append("] ")
            .Append(Message);
        if (!string.IsNullOrWhiteSpace(Detail))
        {
            sb.AppendLine().Append(Detail);
        }
        return sb.ToString();
    }
}

/// <summary>
/// 极简全局日志。
/// 设计目标（对齐"易于定位问题"）：
///  1. 每条日志带时间戳 + 级别 + 模块标签，日志文件可直接 grep 模块；
///  2. 内存环形缓冲保留最近 N 条，界面"诊断"页可实时查看，不用去翻文件；
///  3. 所有异常记录都带完整 ToString()（含堆栈）；
///  4. 任何写盘失败都不会抛出，避免"日志把自己搞崩"。
/// </summary>
public static class Log
{
    private const int RingCapacity = 400;

    private static readonly object Gate = new();
    private static readonly LinkedList<LogEntry> Ring = new();

    private static string? _directory;
    private static string _currentFileDate = string.Empty;
    private static StreamWriter? _writer;
    private static LogLevel _minLevel = LogLevel.Debug;

    /// <summary>新日志产生时触发。可能在后台线程触发，订阅方需自行 marshal 到 UI 线程。</summary>
    public static event Action<LogEntry>? EntryAdded;

    public static string? LogDirectory => _directory;

    public static LogLevel MinLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    public static void Initialize(string logDirectory, LogLevel minLevel = LogLevel.Debug)
    {
        lock (Gate)
        {
            _directory = logDirectory;
            _minLevel = minLevel;
            TryRollFile_NoLock(force: true);
        }

        Info("Log", $"日志已启动，目录 = {logDirectory}");
    }

    public static void Debug(string area, string message) => Write(LogLevel.Debug, area, message, null);
    public static void Info(string area, string message) => Write(LogLevel.Info, area, message, null);
    public static void Warn(string area, string message) => Write(LogLevel.Warn, area, message, null);
    public static void Error(string area, string message) => Write(LogLevel.Error, area, message, null);

    public static void Error(string area, string message, Exception ex)
        => Write(LogLevel.Error, area, message, Describe(ex));

    public static void Warn(string area, string message, Exception ex)
        => Write(LogLevel.Warn, area, message, Describe(ex));

    /// <summary>异常的统一描述格式，保证堆栈一定被记下来。</summary>
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var depth = 0;
        for (Exception? e = ex; e is not null && depth < 6; e = e.InnerException, depth++)
        {
            sb.Append(depth == 0 ? "异常：" : "  内部异常：")
              .Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
            sb.AppendLine(e.StackTrace);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>最近 N 条日志（新的在后）。</summary>
    public static IReadOnlyList<LogEntry> Recent(int count = 300)
    {
        lock (Gate)
        {
            var n = Math.Min(count, Ring.Count);
            var result = new List<LogEntry>(n);
            var node = Ring.Last;
            for (var i = 0; i < n && node is not null; i++, node = node.Previous)
            {
                result.Add(node.Value);
            }
            result.Reverse();
            return result;
        }
    }

    public static string? CurrentFilePath
    {
        get
        {
            lock (Gate)
            {
                return _directory is null ? null : Path.Combine(_directory, FileNameFor(DateTimeOffset.Now));
            }
        }
    }

    private static string FileNameFor(DateTimeOffset at) => $"app-{at:yyyyMMdd}.log";

    private static void Write(LogLevel level, string area, string message, string? detail)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, area, message, detail);

        lock (Gate)
        {
            Ring.AddLast(entry);
            while (Ring.Count > RingCapacity)
            {
                Ring.RemoveFirst();
            }

            if ((int)level >= (int)_minLevel)
            {
                SafeWriteToFile_NoLock(entry);
            }
        }

        try
        {
            EntryAdded?.Invoke(entry);
        }
        catch
        {
            // 订阅方（UI）抛异常不能影响日志本身。
        }
    }

    private static void SafeWriteToFile_NoLock(LogEntry entry)
    {
        try
        {
            TryRollFile_NoLock(force: false);
            _writer?.WriteLine(entry.ToString());
            _writer?.Flush();
        }
        catch
        {
            // 磁盘满了 / 文件被占用 / 目录被删 —— 都不能让日志成为新的故障源。
            _writer = null;
        }
    }

    private static void TryRollFile_NoLock(bool force)
    {
        if (_directory is null)
        {
            return;
        }

        var today = DateTimeOffset.Now.ToString("yyyyMMdd");
        if (!force && _writer is not null && _currentFileDate == today)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            _writer?.Dispose();
            var path = Path.Combine(_directory, FileNameFor(DateTimeOffset.Now));
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
            _currentFileDate = today;
        }
        catch
        {
            _writer = null;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // ignore
            }
            _writer = null;
        }
    }
}
