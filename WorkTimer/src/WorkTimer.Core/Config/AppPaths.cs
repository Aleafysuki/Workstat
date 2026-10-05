namespace WorkTimer.Core.Config;

/// <summary>最终确定的数据目录，以及它是不是便携模式、有没有发生回退。</summary>
public sealed record DataLocation(string Root, bool IsPortable, string? FallbackReason)
{
    public string DatabasePath => Path.Combine(Root, "worktimer.db");
    public string ConfigPath => Path.Combine(Root, "config.json");
    public string UserRulesPath => Path.Combine(Root, "rules.user.json");
    public string SitesPath => Path.Combine(Root, "sites.json");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string ExportDirectory => Path.Combine(Root, "export");
}

/// <summary>
/// 数据目录解析。优先放在 exe 同目录（便携），
/// 但 exe 可能被放在 C:\Program Files 这种没有写权限的地方 —— 那种情况下
/// 静默回退到 %APPDATA%\WorkTimer 并把原因带回去，由界面显式告诉用户实际路径。
/// 否则用户会遇到"设置改了没保存""数据莫名其妙消失"这类无从下手的故障。
/// </summary>
public static class AppPaths
{
    public const string DataFolderName = "data";

    public static DataLocation Resolve(string? overrideRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            try
            {
                Directory.CreateDirectory(overrideRoot);
                EnsureSubDirectories(overrideRoot);
                return new DataLocation(overrideRoot, false, null);
            }
            catch
            {
                // 用户手填的目录不可用 —— 继续走默认逻辑
            }
        }

        var portableRoot = Path.Combine(AppContext.BaseDirectory, DataFolderName);
        var probeError = TryPrepare(portableRoot);
        if (probeError is null)
        {
            return new DataLocation(portableRoot, IsPortable: true, FallbackReason: null);
        }

        var fallbackRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WorkTimer");

        try
        {
            EnsureSubDirectories(fallbackRoot);
        }
        catch
        {
            // 连 %APPDATA% 都写不了就只能用临时目录，至少别崩
            fallbackRoot = Path.Combine(Path.GetTempPath(), "WorkTimer");
            EnsureSubDirectories(fallbackRoot);
        }

        return new DataLocation(
            fallbackRoot,
            IsPortable: false,
            FallbackReason: $"exe 同目录不可写（{probeError}），已回退到 {fallbackRoot}");
    }

    /// <summary>返回 null 表示可用；否则返回不可用原因。</summary>
    private static string? TryPrepare(string root)
    {
        try
        {
            EnsureSubDirectories(root);

            var probe = Path.Combine(root, ".write-probe");
            File.WriteAllText(probe, DateTimeOffset.Now.ToString("O"));
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void EnsureSubDirectories(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "export"));
    }
}
