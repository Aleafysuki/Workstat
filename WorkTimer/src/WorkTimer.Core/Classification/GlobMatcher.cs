using System.Text.RegularExpressions;

namespace WorkTimer.Core.Classification;

/// <summary>
/// 通配符匹配（* 与 ?）。规则里的进程名 / 标题 / 类名模式都走这里。
///
/// 两种语义，别混用：
///  - <see cref="Match"/>：<b>包含</b>语义。没通配符就是子串包含；有通配符时正则在输入里任意位置命中即可。
///    窗口标题这类场景用它（标题是"【教程】xxx_哔哩哔哩"，模式是 "哔哩哔哩"）。
///  - <see cref="MatchWhole"/>：<b>整串</b>语义。进程名用它（进程名是 "Code"，模式是 "Code"，
///    但模式 "Code" 不应该匹配到 "Code - Insiders"）。
///
/// 编译后的正则做缓存，避免每 2 秒采样都重复编译。
/// </summary>
public static class GlobMatcher
{
    private const string ContainsPrefix = "c:";
    private const string WholePrefix = "w:";

    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static bool Match(string? pattern, string? input)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input))
        {
            return false;
        }

        if (!HasWildcard(pattern))
        {
            return input.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        return GetCached(ContainsPrefix + pattern, anchored: false).IsMatch(input);
    }

    public static bool MatchWhole(string? pattern, string? input)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input))
        {
            return false;
        }

        if (!HasWildcard(pattern))
        {
            return string.Equals(pattern, input, StringComparison.OrdinalIgnoreCase);
        }

        return GetCached(WholePrefix + pattern, anchored: true).IsMatch(input);
    }

    public static bool MatchRegex(string? pattern, string? input)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
        }
        catch
        {
            // 用户写坏的正则不能把采样循环拖垮
            return false;
        }
    }

    public static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    private static Regex GetCached(string key, bool anchored)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var pattern = key[2..];
            var body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            var expression = anchored ? "^" + body + "$" : body;
            var regex = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            Cache[key] = regex;
            return regex;
        }
    }
}
