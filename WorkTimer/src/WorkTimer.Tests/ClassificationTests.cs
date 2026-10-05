using WorkTimer.Core.Classification;
using WorkTimer.Core.Monitoring;
using Xunit;

namespace WorkTimer.Tests;

/// <summary>判定引擎的行为测试：分类、继承、站点识别、开关。</summary>
public class ClassificationTests
{
    private static RuleEngine Engine() => TestFactory.Rules(TestFactory.Config());

    [Fact]
    public void 无前台窗口归继承()
    {
        var result = Engine().Classify(WindowInfo.None());

        Assert.Equal(Category.Inherit, result.Category);
    }

    [Fact]
    public void 本程序自身窗口归继承()
    {
        var window = new WindowInfo(
            new IntPtr(1), TestFactory.SelfPid, "WorkTimer", @"C:\x\WorkTimer.exe",
            "工作计时", "TestWindowClass", false, 0);

        var result = Engine().Classify(window);

        Assert.Equal(Category.Inherit, result.Category);
    }

    [Fact]
    public void 桌面窗口归继承()
    {
        var result = Engine().Classify(TestFactory.Window("explorer", "Program Manager", "Progman"));

        Assert.Equal(Category.Inherit, result.Category);
    }

    [Fact]
    public void 开始菜单与任务栏归继承()
    {
        var engine = Engine();

        Assert.Equal(Category.Inherit, engine.Classify(TestFactory.Window("explorer", "", "Shell_TrayWnd")).Category);
        Assert.Equal(Category.Inherit, engine.Classify(TestFactory.Window("StartMenuExperienceHost", "", "XamlExplorerHostIslandWindow")).Category);
        Assert.Equal(Category.Inherit, engine.Classify(TestFactory.Window("ShellExperienceHost", "", "Windows.UI.Core.CoreWindow")).Category);
    }

    [Fact]
    public void 文件资源管理器仍算工作()
    {
        var result = Engine().Classify(TestFactory.Window("explorer", "下载", "CabinetWClass"));

        Assert.Equal(Category.Work, result.Category);
    }

    [Fact]
    public void 内置规则能识别常见开发工具()
    {
        var engine = Engine();

        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("devenv")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("Code")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("WindowsTerminal")).Category);
    }

    [Fact]
    public void 会议类应用归工作且抑制空闲检测()
    {
        var engine = Engine();

        foreach (var process in new[] { "wemeetapp", "Zoom", "DingTalk", "Feishu", "Teams" })
        {
            var result = engine.Classify(TestFactory.Window(process));
            Assert.Equal(Category.Work, result.Category);
            Assert.True(result.SuppressIdle, $"{process} 应标记为抑制空闲检测");
        }
    }

    [Fact]
    public void 聊天工具默认归聊天且可用开关改为工作()
    {
        var config = TestFactory.Config();
        var engine = new RuleEngine(new SiteResolver(), TestFactory.SelfPid);
        engine.Update(Array.Empty<Rule>(), DefaultSites.Create(), config);

        Assert.Equal(Category.Chat, engine.Classify(TestFactory.Window("WeChat")).Category);
        Assert.Equal(Category.Chat, engine.Classify(TestFactory.Window("QQ")).Category);

        config.Timing.TreatImAsWork.Wechat = true;
        config.Timing.TreatImAsWork.Qq = true;
        engine.Update(Array.Empty<Rule>(), DefaultSites.Create(), config);

        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("WeChat")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("QQ")).Category);
    }

    [Fact]
    public void 游戏与视频客户端按类别识别()
    {
        var engine = Engine();

        Assert.Equal(Category.Game, engine.Classify(TestFactory.Window("steam")).Category);
        Assert.Equal(Category.Game, engine.Classify(TestFactory.Window("GenshinImpact")).Category);
        Assert.Equal(Category.Video, engine.Classify(TestFactory.Window("PotPlayerMini64")).Category);
    }

    [Fact]
    public void 浏览器娱乐站点识别出平台名与类别()
    {
        var result = Engine().Classify(TestFactory.Window("chrome", "【教程】从零写一个编译器_哔哩哔哩_bilibili"));

        Assert.Equal(Category.Video, result.Category);
        Assert.Equal("哔哩哔哩", result.SitePlatform);
        Assert.True(result.IsBrowser);
        Assert.Equal("Google Chrome · 哔哩哔哩", result.DisplayName);
    }

    [Fact]
    public void 浏览器工作站点识别为工作()
    {
        var engine = Engine();

        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("msedge", "torvalds/linux: Linux kernel source tree")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("chrome", "ChatGPT")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("chrome", "腾讯文档 - 在线文档")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("msedge", "DeepSeek - 深度求索")).Category);
    }

    [Fact]
    public void 浏览器未识别站点时走兜底类别()
    {
        var result = Engine().Classify(TestFactory.Window("chrome", "某个没有关键词的页面"));

        Assert.Equal(Category.Work, result.Category);
        Assert.Equal("网页浏览", result.SitePlatform);
    }

    [Fact]
    public void 用户规则优先于内置规则()
    {
        var config = TestFactory.Config();
        var engine = new RuleEngine(new SiteResolver(), TestFactory.SelfPid);
        engine.Update(
            new[] { new Rule { Id = "u1", Order = 1, Category = "work", ProcessPattern = "steam", MatchMode = "process" } },
            DefaultSites.Create(),
            config);

        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("steam")).Category);
    }

    [Fact]
    public void 关闭的规则不生效()
    {
        var config = TestFactory.Config();
        var engine = new RuleEngine(new SiteResolver(), TestFactory.SelfPid);
        engine.Update(
            new[] { new Rule { Id = "u1", Order = 1, Enabled = false, Category = "work", ProcessPattern = "steam" } },
            DefaultSites.Create(),
            config);

        Assert.Equal(Category.Game, engine.Classify(TestFactory.Window("steam")).Category);
    }

    [Fact]
    public void 通配符规则可以匹配进程名()
    {
        Assert.True(GlobMatcher.MatchWhole("chrome*", "chrome"));
        Assert.True(GlobMatcher.MatchWhole("*player*", "PotPlayerMini64"));
        Assert.False(GlobMatcher.MatchWhole("Code", "Code - Insiders"));
    }

    [Fact]
    public void 标题匹配是包含语义()
    {
        Assert.True(GlobMatcher.Match("哔哩哔哩", "【教程】xxx_哔哩哔哩_bilibili"));
        Assert.False(GlobMatcher.Match("哔哩哔哩", "GitHub"));
    }

    [Fact]
    public void 教程视频开关默认关闭()
    {
        var config = TestFactory.Config();
        var engine = new RuleEngine(new SiteResolver(), TestFactory.SelfPid);
        engine.Update(Array.Empty<Rule>(), DefaultSites.Create(), config);

        // 默认情况下教程视频仍然算视频（只给宽容期，不算工时）
        var bySite = engine.Classify(TestFactory.Window("chrome", "Python 入门教程_哔哩哔哩"));
        Assert.Equal(Category.Video, bySite.Category);

        // 打开开关后按教程关键词归工作
        config.Classification.TreatTutorialVideoAsWork = true;
        engine.Update(Array.Empty<Rule>(), DefaultSites.Create(), config);

        var byTutorial = engine.Classify(TestFactory.Window("chrome", "Python 入门教程_哔哩哔哩"));
        Assert.Equal(Category.Work, byTutorial.Category);
    }

    [Fact]
    public void 用户反馈过的具体案例能被正确归类()
    {
        var engine = Engine();

        // 用户报告过 WorkBuddy 被判成中性 —— 现在应当直接命中工作规则
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("WorkBuddy", "WorkBuddy")).Category);

        // 用户机器上真实在跑的明日方舟
        Assert.Equal(Category.Game, engine.Classify(TestFactory.Window("Arknights")).Category);

        // 以前可能落空的常见软件
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("Cursor")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("Docker Desktop")).Category);
        Assert.Equal(Category.Work, engine.Classify(TestFactory.Window("Everything")).Category);
        Assert.Equal(Category.Video, engine.Classify(TestFactory.Window("QQMusic")).Category);
        Assert.Equal(Category.Video, engine.Classify(TestFactory.Window("WeRead")).Category);
    }

    [Fact]
    public void 游戏引擎窗口类作为强信号被识别()
    {
        var engine = Engine();

        Assert.Equal(Category.Game, engine.Classify(TestFactory.Window("SomeUnknownGame", className: "UnityWndClass")).Category);
        Assert.Equal(Category.Game, engine.Classify(TestFactory.Window("SomeUnknownGame", className: "UnrealWindow")).Category);
        Assert.Equal(Category.Game, engine.Classify(TestFactory.Window("SomeUnknownGame", className: "Valve001")).Category);
    }

    [Fact]
    public void 内置数据量达到可用规模()
    {
        var rules = BuiltInRules.Create();
        var sites = DefaultSites.Create();

        Assert.True(rules.Count >= 300, $"内置规则只有 {rules.Count} 条，偏少");
        Assert.True(sites.Sites.Count >= 150, $"内置站点只有 {sites.Sites.Count} 个，偏少");

        // 内置规则必须都设了类别，避免"匹配上了但没归类"的空规则
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Category)));

        // 进程规则与窗口类规则不能同时为空，否则规则永不命中
        Assert.All(rules, r => Assert.True(
            !string.IsNullOrWhiteSpace(r.ProcessPattern) || !string.IsNullOrWhiteSpace(r.ClassPattern),
            $"{r.Id} 没有任何匹配条件"));
    }

    [Fact]
    public void 新增站点覆盖了常见工作与娱乐网站()
    {
        var engine = Engine();

        Assert.Equal("腾讯文档", engine.Classify(TestFactory.Window("chrome", "季度排期 - 腾讯文档")).SitePlatform);
        Assert.Equal("Notion", engine.Classify(TestFactory.Window("chrome", "Roadmap – Notion")).SitePlatform);
        Assert.Equal("小红书", engine.Classify(TestFactory.Window("msedge", "发现好生活 - 小红书")).SitePlatform);
        Assert.Equal("知乎", engine.Classify(TestFactory.Window("chrome", "如何评价 xx - 知乎")).SitePlatform);
        Assert.Equal("AWS", engine.Classify(TestFactory.Window("chrome", "Amazon Web Services")).SitePlatform);
    }
}
