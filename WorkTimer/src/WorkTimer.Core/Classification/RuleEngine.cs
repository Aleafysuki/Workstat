using WorkTimer.Core.Config;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Monitoring;

namespace WorkTimer.Core.Classification;

/// <summary>
/// 判定引擎。把一份 <see cref="WindowInfo"/> 翻译成 <see cref="ClassificationResult"/>。
///
/// 判定顺序（first-match-wins）：
///   1. 没有前台窗口 / 本程序自身窗口 / 系统外壳窗口  → Inherit（继承上一分类，不改变计时状态）
///   2. <b>用户规则</b>（rules.user.json，按 Order）—— 放在浏览器判定之前，
///      这样"一键纠错"才能覆盖站点表把某个页面判成工作或娱乐
///   3. 浏览器  → 站点识别（sites.json），按站点类别
///   4. 内置规则 → 兜底
///   5. 后处理：IM 归工作开关、教程视频视为工作
/// </summary>
public sealed class RuleEngine
{
    private readonly SiteResolver _sites;
    private readonly int _selfPid;

    private List<Rule> _userRules = new();
    private List<Rule> _builtInRules = new();
    private AppConfig _config = new();

    public RuleEngine(SiteResolver sites, int selfPid)
    {
        _sites = sites;
        _selfPid = selfPid;
        Rebuild();
    }

    /// <summary>生效的规则全集（用户规则在前），供设置界面展示。</summary>
    public IReadOnlyList<Rule> EffectiveRules => _userRules.Concat(_builtInRules).ToList();

    public AppConfig Config => _config;

    public void Update(IEnumerable<Rule>? userRules, SiteFile? sites, AppConfig config)
    {
        _userRules = (userRules ?? Enumerable.Empty<Rule>())
            .Where(r => r is not null && r.Enabled)
            .OrderBy(r => r.Order)
            .ToList();

        if (sites is not null)
        {
            _sites.Update(sites);
        }

        _config = config;
        Rebuild();

        Log.Info("Classify", $"规则已刷新：用户 {_userRules.Count} 条 + 内置 {_builtInRules.Count} 条，" +
                             $"站点 {_sites.File.Sites.Count} 个");
    }

    private void Rebuild()
    {
        _builtInRules = BuiltInRules.Create().OrderBy(r => r.Order).ToList();
    }

    public ClassificationResult Classify(in WindowInfo w)
    {
        // 1. 没有前台窗口
        if (!w.HasForeground)
        {
            return new ClassificationResult
            {
                Category = Category.Inherit,
                AppName = string.Empty,
                Reason = "无前台窗口",
            };
        }

        // 2. 本程序自身窗口
        if (w.Pid == _selfPid)
        {
            return new ClassificationResult
            {
                Category = Category.Inherit,
                AppName = string.IsNullOrEmpty(w.ProcessName) ? "WorkTimer" : w.ProcessName,
                Reason = "本程序自身窗口",
            };
        }

        // 3. 系统外壳窗口类（桌面、任务栏、开始菜单、通知中心、任务视图、锁屏）
        if (!string.IsNullOrEmpty(w.ClassName) && IsShellClass(w.ClassName))
        {
            return new ClassificationResult
            {
                Category = Category.Inherit,
                AppName = w.ProcessName,
                Reason = $"系统外壳窗口类 {w.ClassName}",
            };
        }

        if (IsShellProcess(w.ProcessName))
        {
            return new ClassificationResult
            {
                Category = Category.Inherit,
                AppName = w.ProcessName,
                Reason = $"系统外壳进程 {w.ProcessName}",
            };
        }

        // 4. 用户规则优先（必须在浏览器判定之前，否则纠错无法覆盖站点表）
        var userHit = MatchRule(_userRules, w);
        if (userHit is not null)
        {
            return FromRule(userHit, w);
        }

        // 5. 浏览器 → 站点识别
        if (IsBrowser(w.ProcessName))
        {
            return ClassifyBrowser(w);
        }

        // 6. 内置规则
        var builtInHit = MatchRule(_builtInRules, w);
        if (builtInHit is not null)
        {
            return FromRule(builtInHit, w);
        }

        // 7. 独占全屏兜底
        if (_config.Classification.FullscreenGameFallback && w.IsFullscreen)
        {
            return new ClassificationResult
            {
                Category = Category.Game,
                AppName = w.ProcessName,
                Reason = "独占全屏且未命中任何规则（疑似游戏，可在设置中关闭）",
            };
        }

        // 8. 兜底
        return new ClassificationResult
        {
            Category = Category.Neutral,
            AppName = w.ProcessName,
            Reason = "未命中任何规则",
        };
    }

    private ClassificationResult FromRule(Rule rule, in WindowInfo w)
    {
        var category = CategoryExtensions.Parse(rule.Category);
        var appName = string.IsNullOrWhiteSpace(rule.Note) ? w.ProcessName : rule.Note!;

        return ApplyPostProcessing(new ClassificationResult
        {
            Category = category,
            AppName = appName,
            IsBrowser = IsBrowser(w.ProcessName),
            SitePlatform = IsBrowser(w.ProcessName) ? _sites.Resolve(w.Title)?.Platform : null,
            SuppressIdle = rule.SuppressIdle,
            ThresholdMinutesOverride = rule.ThresholdMinutes,
            MatchedRuleId = rule.Id,
            Reason = $"{(rule.IsBuiltIn ? "内置" : "用户")}规则 {rule.Id}" +
                     (string.IsNullOrWhiteSpace(rule.ProcessPattern) ? string.Empty : $"（进程 {rule.ProcessPattern}）") +
                     (string.IsNullOrWhiteSpace(rule.TitlePattern) ? string.Empty : $"（标题 {rule.TitlePattern}）"),
        }, w);
    }

    private ClassificationResult ClassifyBrowser(in WindowInfo w)
    {
        var match = _sites.Resolve(w.Title);
        if (match is not null)
        {
            return ApplyPostProcessing(new ClassificationResult
            {
                Category = match.Category,
                AppName = FriendlyBrowserName(w.ProcessName),
                SitePlatform = match.Platform,
                IsBrowser = true,
                Reason = $"站点表命中「{match.MatchedPattern}」→ {match.Platform}",
            }, w);
        }

        var fallback = CategoryExtensions.Parse(_config.Classification.BrowserFallback);

        return ApplyPostProcessing(new ClassificationResult
        {
            Category = fallback,
            AppName = FriendlyBrowserName(w.ProcessName),
            SitePlatform = _sites.FallbackPlatform,
            IsBrowser = true,
            Reason = "浏览器标题未命中站点表，按兜底策略处理",
        }, w);
    }

    /// <summary>IM 归工作开关 + 教程视频视为工作。</summary>
    private ClassificationResult ApplyPostProcessing(ClassificationResult result, in WindowInfo w)
    {
        // 有些日子要用 QQ/微信 对接工作，给个开关把它们整体归工作
        if (result.Category == Category.Chat && IsTreatImAsWork(w.ProcessName))
        {
            return result with
            {
                Category = Category.Work,
                Reason = result.Reason + " → 被「工作场景聊天工具」开关改为工作",
            };
        }

        // "看教程视频"只想要宽容期而不是算工时，所以默认关闭
        if (result.Category == Category.Video && _config.Classification.TreatTutorialVideoAsWork)
        {
            foreach (var keyword in _config.Classification.TutorialKeywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword)
                    && w.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return result with
                    {
                        Category = Category.Work,
                        Reason = result.Reason + $" → 标题含「{keyword}」，按教程视频算工作",
                    };
                }
            }
        }

        return result;
    }

    private bool IsTreatImAsWork(string processName)
    {
        var flags = _config.Timing.TreatImAsWork;
        if (flags.Wechat && (GlobMatcher.MatchWhole("WeChat", processName) || GlobMatcher.MatchWhole("Weixin", processName)))
        {
            return true;
        }

        if (flags.Qq && GlobMatcher.MatchWhole("QQ", processName))
        {
            return true;
        }

        if (flags.Tim && GlobMatcher.MatchWhole("TIM", processName))
        {
            return true;
        }

        return false;
    }

    private Rule? MatchRule(IEnumerable<Rule> rules, in WindowInfo w)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (MatchesRule(rule, w))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool MatchesRule(Rule rule, in WindowInfo w)
    {
        var mode = rule.MatchMode?.ToLowerInvariant() ?? "process";

        var processHit = string.IsNullOrEmpty(rule.ProcessPattern)
            ? (bool?)null
            : GlobMatcher.MatchWhole(rule.ProcessPattern, w.ProcessName);

        var titleHit = string.IsNullOrEmpty(rule.TitlePattern)
            ? (bool?)null
            : rule.TitleIsRegex
                ? GlobMatcher.MatchRegex(rule.TitlePattern, w.Title)
                : GlobMatcher.Match(rule.TitlePattern, w.Title);

        var classHit = string.IsNullOrEmpty(rule.ClassPattern)
            ? (bool?)null
            : GlobMatcher.Match(rule.ClassPattern, w.ClassName);

        return mode switch
        {
            "title" => titleHit ?? false,
            "class" => classHit ?? false,
            "any" => (processHit ?? false) || (titleHit ?? false) || (classHit ?? false),
            "all" => (processHit ?? true) && (titleHit ?? true) && (classHit ?? true)
                     && (processHit.HasValue || titleHit.HasValue || classHit.HasValue),
            _ => processHit ?? false,
        };
    }

    private static bool IsShellClass(string className)
        => BuiltInRules.ShellWindowClasses.Any(c => string.Equals(c, className, StringComparison.OrdinalIgnoreCase));

    private static bool IsShellProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        // explorer.exe 是特例：窗口类判定已经拦掉了任务栏与桌面，
        // 走到这里的 explorer 就是文件资源管理器，属于工作。
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return BuiltInRules.ShellProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsBrowser(string processName)
        => !string.IsNullOrEmpty(processName)
           && BuiltInRules.BrowserProcesses.Any(b => string.Equals(b, processName, StringComparison.OrdinalIgnoreCase));

    private static string FriendlyBrowserName(string processName) => processName.ToLowerInvariant() switch
    {
        "msedge" => "Microsoft Edge",
        "chrome" => "Google Chrome",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        "360se" or "360chrome" => "360 浏览器",
        "qqbrowser" => "QQ 浏览器",
        "sogouexplorer" => "搜狗浏览器",
        _ => string.IsNullOrEmpty(processName) ? "浏览器" : processName,
    };
}
