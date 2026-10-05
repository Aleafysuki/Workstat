using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkTimer.Core.Classification;
using WorkTimer.Core.Config;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Timing;

namespace WorkTimer.App.Services;

/// <summary>设置分组。UI 由元数据生成，分组与顺序集中定义在这里。</summary>
public sealed record SettingsGroup(string Title, string Hint, string Key);

/// <summary>
/// 设置页构建器。
///
/// 设计要点（对齐"分类直观、设置项简洁、必要时有说明"）：
///  - 每个设置项一行：左侧标题 + 灰色说明，右侧控件；
///  - 分组与说明集中在这里，调顺序/改文案不用动界面代码；
///  - 修改先落在内存里的 AppConfig，点"保存设置"才写盘并热生效。
/// </summary>
public static class SettingsPageBuilder
{
    public static readonly IReadOnlyList<SettingsGroup> Groups = new[]
    {
        new SettingsGroup("计时与判定", "娱乐阈值、跨类别策略、追溯作废与采样节奏。改完点右下角「保存设置」。", "timing"),
        new SettingsGroup("空闲与自动结束", "多久没碰键鼠算离开；人走了多久自动结束会话。", "idle"),
        new SettingsGroup("应用分类", "哪些软件算工作、算娱乐；以及你自己的分类规则。", "classify"),
        new SettingsGroup("浏览器与站点", "浏览器里按站点判定；明细记录到什么粒度。", "sites"),
        new SettingsGroup("未识别应用", "被判定为「中性」的软件。一键归类会同时回填历史记录。", "unknown"),
        new SettingsGroup("悬浮窗", "贴边自动收起的迷你计时窗。", "widget"),
        new SettingsGroup("托盘与热键", "启动行为、桌面快捷方式与全局快捷键。", "tray"),
        new SettingsGroup("隐私与数据", "数据目录、归档、回收站清理与导出。", "data"),
        new SettingsGroup("关于", "版本信息与内置数据恢复。", "about"),
    };

    // ================================================================ 入口

    public static UIElement Build(SettingsGroup group, AppHost host, Action onDirty, Action onDataChanged)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        switch (group.Key)
        {
            case "timing":
                BuildTiming(panel, host, onDirty);
                break;
            case "idle":
                BuildIdle(panel, host, onDirty);
                break;
            case "classify":
                BuildClassify(panel, host, onDirty);
                break;
            case "sites":
                BuildSites(panel, host, onDirty);
                break;
            case "unknown":
                BuildUnknown(panel, host, onDataChanged);
                break;
            case "widget":
                BuildWidget(panel, host, onDirty);
                break;
            case "tray":
                BuildTray(panel, host, onDirty);
                break;
            case "data":
                BuildData(panel, host, onDirty, onDataChanged);
                break;
            case "about":
                BuildAbout(panel, host);
                break;
        }

        return panel;
    }

    // ================================================================ 各分组

    private static void BuildTiming(StackPanel panel, AppHost host, Action onDirty)
    {
        var config = host.Config;

        Section(panel, "娱乐阈值（分钟）", "前台连续停留在娱乐窗口超过该时长就暂停计时。填 0 表示一进入就暂停。");

        panel.Children.Add(IntRow("游戏阈值", "默认 5 分钟。填 0 表示进入游戏立即暂停。",
            0, 600, "分钟", () => config.Timing.Thresholds.Game, v => { config.Timing.Thresholds.Game = v; onDirty(); }));

        panel.Children.Add(IntRow("视频阈值", "默认 10 分钟。看教程视频常见，所以给得比游戏宽。",
            0, 600, "分钟", () => config.Timing.Thresholds.Video, v => { config.Timing.Thresholds.Video = v; onDirty(); }));

        panel.Children.Add(IntRow("聊天阈值", "默认 10 分钟。短暂沟通不会打断计时，长时间泡在里面才会。",
            0, 600, "分钟", () => config.Timing.Thresholds.Chat, v => { config.Timing.Thresholds.Chat = v; onDirty(); }));

        Divider(panel);
        Section(panel, "娱乐连续运行", "游戏→视频这类跨类别切换不会把连续时长清零，只有切回工作才结束。");

        panel.Children.Add(ChoiceRow("跨类别阈值取值", "一段连续娱乐里出现多个类别时，用哪个阈值判定。",
            new[]
            {
                ("min", "取最小（最严格，推荐）"),
                ("avg", "取均值"),
                ("max", "取最大"),
                ("sum", "累加各类别阈值"),
                ("current", "实时取当前类别"),
            },
            () => config.Timing.CrossCategoryThresholdMode,
            v => { config.Timing.CrossCategoryThresholdMode = v; onDirty(); }));

        panel.Children.Add(BoolRow("娱乐段超阈值后整段作废", "开启后：超过阈值的整段娱乐都不计入工时（追溯扣除）。关闭后：阈值内的那段仍算工时。",
            () => config.Timing.RetroactiveDeduct,
            v => { config.Timing.RetroactiveDeduct = v; onDirty(); }));

        panel.Children.Add(BoolRow("允许计时器数字回退", "关闭（推荐）：娱乐期间显示「待判定」，结算后再定，数字只增不减。开启：娱乐时间实时计入，超阈值时扣回，数字会往回跳。两种模式最终工时数据完全一致。",
            () => config.Timing.CountEntertainmentLive,
            v => { config.Timing.CountEntertainmentLive = v; onDirty(); }));

        Divider(panel);
        Section(panel, "采样与防抖", "影响判定灵敏度与 CPU 占用。");

        panel.Children.Add(IntRow("采样间隔", "每多少毫秒读一次前台窗口。1000 表示每秒一次。",
            250, 10000, "毫秒", () => config.General.PollIntervalMs, v => { config.General.PollIntervalMs = v; onDirty(); }));

        panel.Children.Add(IntRow("防抖时长", "窗口要连续占据前台这么久才算「切换」。用来忽略微信弹窗、通知横幅这类短暂抢占。",
            0, 600, "秒", () => config.General.MinSegmentSeconds, v => { config.General.MinSegmentSeconds = v; onDirty(); }));

        Section(panel, "当前生效摘要", null);
        panel.Children.Add(InfoBox(
            $"游戏 {config.Timing.Thresholds.Game} 分钟 / 视频 {config.Timing.Thresholds.Video} 分钟 / 聊天 {config.Timing.Thresholds.Chat} 分钟；" +
            $"跨类别取 {ThresholdPolicy.DescribeMode(config.Timing.CrossCategoryThresholdMode)}；" +
            $"追溯作废 {(config.Timing.RetroactiveDeduct ? "开" : "关")}；" +
            $"数字回退 {(config.Timing.CountEntertainmentLive ? "开" : "关")}；" +
            $"采样 {config.General.PollIntervalMs}ms；防抖 {config.General.MinSegmentSeconds}s。"));
    }

    private static void BuildIdle(StackPanel panel, AppHost host, Action onDirty)
    {
        var config = host.Config;

        Section(panel, "空闲检测", "鼠标、键盘、触摸都没有操作即视为空闲。");

        panel.Children.Add(BoolRow("启用空闲检测", "关闭后不会因为没操作而挂起。",
            () => config.Idle.Enabled, v => { config.Idle.Enabled = v; onDirty(); }));

        panel.Children.Add(IntRow("空闲阈值", "无输入超过该时长视为离开电脑。默认 20 分钟。",
            1, 600, "分钟", () => config.Idle.ThresholdMinutes, v => { config.Idle.ThresholdMinutes = v; onDirty(); }));

        panel.Children.Add(ChoiceRow("空闲时的动作", "检测到离开后做什么。",
            new[]
            {
                ("suspend", "挂起计时（推荐）"),
                ("count", "照常计时"),
                ("stop", "直接停止计时"),
            },
            () => config.Idle.Action, v => { config.Idle.Action = v; onDirty(); }));

        panel.Children.Add(BoolRow("会议应用抑制空闲检测", "前台是腾讯会议 / 飞书 / 钉钉 / 企微 / Zoom / Teams 时不因空闲挂起 —— 开会确实不动键鼠，不抑制必然误判。",
            () => config.Idle.SuppressForMeetingApps, v => { config.Idle.SuppressForMeetingApps = v; onDirty(); }));

        panel.Children.Add(BoolRow("仅当前台是娱乐时才因空闲挂起", "默认关闭。如果你经常长时间读代码、看资料不动键鼠，可以打开这个开关避免被误挂起。",
            () => config.Idle.SuspendOnlyWhenEntertainment, v => { config.Idle.SuspendOnlyWhenEntertainment = v; onDirty(); }));

        Divider(panel);
        Section(panel, "防止「忘了停止计时」", null);

        panel.Children.Add(IntRow("连续离开多久自动结束会话", "连续空闲或挂起超过该时长就自动结束并保存，避免人走了计时跑一整晚。默认 60 分钟。",
            5, 1440, "分钟", () => config.Idle.AutoEndSessionAfterMinutes,
            v => { config.Idle.AutoEndSessionAfterMinutes = v; onDirty(); }));

        panel.Children.Add(InfoBox(
            "这里用的是「连续」离开时长，不是会话累计值。" +
            "如果用累计值，正常工作一天里断续的摸鱼累加到 60 分钟就会把还在上班的会话结束掉。"));
    }

    private static void BuildClassify(StackPanel panel, AppHost host, Action onDirty)
    {
        var config = host.Config;

        Section(panel, "工作场景聊天工具", "默认微信 / QQ / TIM 算聊天类（10 分钟阈值）。哪天要用它们对接工作，勾上即整体归工作。");
        panel.Children.Add(BoolRow("微信算工作", null, () => config.Timing.TreatImAsWork.Wechat,
            v => { config.Timing.TreatImAsWork.Wechat = v; onDirty(); }));
        panel.Children.Add(BoolRow("QQ 算工作", null, () => config.Timing.TreatImAsWork.Qq,
            v => { config.Timing.TreatImAsWork.Qq = v; onDirty(); }));
        panel.Children.Add(BoolRow("TIM 算工作", null, () => config.Timing.TreatImAsWork.Tim,
            v => { config.Timing.TreatImAsWork.Tim = v; onDirty(); }));

        Divider(panel);
        Section(panel, "其它判定开关", null);

        panel.Children.Add(BoolRow("标题含「教程 / 课程」的视频算工作", "默认关闭 —— 你说过看教程视频只想要宽容期而不是算工时。如果发现看教程老被挂起，打开它。",
            () => config.Classification.TreatTutorialVideoAsWork,
            v => { config.Classification.TreatTutorialVideoAsWork = v; onDirty(); }));

        panel.Children.Add(BoolRow("独占全屏且未命中规则时视为疑似游戏", "默认关闭。能捞住没收录的新游戏，但全屏看文档 / 看代码时会被误杀。",
            () => config.Classification.FullscreenGameFallback,
            v => { config.Classification.FullscreenGameFallback = v; onDirty(); }));

        Divider(panel);
        Section(panel, "我的规则", "规则优先于内置数据与站点表，所以可以用来纠正任何判定。");

        var rulePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(rulePanel);

        void RebuildRules()
        {
            rulePanel.Children.Clear();

            if (host.RuleStore.Rules.Count == 0)
            {
                rulePanel.Children.Add(InfoBox("还没有自定义规则。在主界面看到误判时点「改为工作 / 改为娱乐」会自动生成一条。"));
            }
            else
            {
                foreach (var rule in host.RuleStore.Rules.ToList())
                {
                    rulePanel.Children.Add(RuleRow(rule, host, RebuildRules));
                }
            }

            panel.Children.Remove(rulePanel);
            panel.Children.Add(rulePanel);
        }

        RebuildRules();

        panel.Children.Add(AddRuleRow(host, RebuildRules));

        var resetRules = new Button
        {
            Content = "清空全部自定义规则",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
        };
        resetRules.Click += (_, _) =>
        {
            if (MessageBox.Show("确定清空全部自定义规则吗？清空后将只使用内置数据与站点表。", "清空规则",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            host.RuleStore.ResetToBuiltIn();
            host.ReloadRules();
            RebuildRules();
        };
        panel.Children.Add(resetRules);

        Divider(panel);
        panel.Children.Add(InfoBox(
            $"内置规则 {BuiltInRules.Create().Count} 条、内置站点 {DefaultSites.Create().Sites.Count} 个。" +
            $"规则文件：{host.RuleStore.Path}"));
    }

    private static void BuildSites(StackPanel panel, AppHost host, Action onDirty)
    {
        var config = host.Config;

        Section(panel, "浏览器整体策略", null);

        panel.Children.Add(ChoiceRow("标题都识别不出站点时算哪类",
            "站点表没命中时的兜底类别。",
            new[] { ("work", "算工作（推荐）"), ("neutral", "算中性") },
            () => config.Classification.BrowserFallback,
            v => { config.Classification.BrowserFallback = v; onDirty(); }));

        panel.Children.Add(ChoiceRow("明细记录粒度",
            "浏览器页面记录到什么程度。默认只记平台名，不记完整网页标题。",
            new[] { ("platform", "只记平台名（如「哔哩哔哩」）"), ("full", "记完整网页标题") },
            () => config.Privacy.BrowserDetailLevel,
            v => { config.Privacy.BrowserDetailLevel = v; onDirty(); }));

        panel.Children.Add(BoolRow("记录非浏览器应用的窗口标题",
            "关闭后明细里只保留应用名（如「Visual Studio Code」），不记具体文档名。",
            () => config.Privacy.RecordAppWindowTitle,
            v => { config.Privacy.RecordAppWindowTitle = v; onDirty(); }));

        Divider(panel);
        Section(panel, "站点表", "窗口标题里包含关键词即命中。命中后同时决定「显示名」和「类别」。");

        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var listPanel = new StackPanel();
        var caption = new TextBlock { Style = Res("CaptionText") as Style, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(caption);

        void RebuildSites()
        {
            listPanel.Children.Clear();
            var keyword = filter.Text?.Trim() ?? string.Empty;
            var all = host.SiteStore.File.Sites;
            var matched = string.IsNullOrEmpty(keyword)
                ? all
                : all.Where(s => s.Platform.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                 || s.Patterns.Any(p => p.Contains(keyword, StringComparison.OrdinalIgnoreCase))).ToList();

            caption.Text = $"共 {all.Count} 个站点，当前显示 {matched.Count} 个。删掉的站点会从 sites.json 里移除。";

            foreach (var site in matched.Take(200))
            {
                listPanel.Children.Add(SiteRow(site, host, RebuildSites));
            }

            if (matched.Count > 200)
            {
                listPanel.Children.Add(InfoBox($"（仅显示前 200 条，请用上方筛选缩小范围）"));
            }
        }

        filter.TextChanged += (_, _) => RebuildSites();
        panel.Children.Add(filter);

        var scroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listPanel,
            Margin = new Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(scroll);

        RebuildSites();

        panel.Children.Add(AddSiteRow(host, RebuildSites));

        var reset = new Button
        {
            Content = "恢复内置站点表",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
        };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show("恢复后你对站点表的所有修改都会丢失。继续吗？", "恢复内置站点表",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            host.SiteStore.ResetToBuiltIn();
            host.ReloadRules();
            RebuildSites();
        };
        panel.Children.Add(reset);
    }

    private static void BuildUnknown(StackPanel panel, AppHost host, Action onDataChanged)
    {
        Section(panel, "被判成「中性」的应用", "这些是没有命中任何规则、也没落进站点表的软件。挑一个归类，历史记录会同步回填。");

        var listPanel = new StackPanel();
        var caption = new TextBlock { Style = Res("CaptionText") as Style, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(caption);

        void Rebuild()
        {
            listPanel.Children.Clear();
            List<WorkTimer.Core.Data.UnknownProcessRecord> items;
            try
            {
                items = host.Repository.GetNeutralProcesses(30);
            }
            catch (Exception ex)
            {
                caption.Text = "读取失败：" + ex.Message;
                return;
            }

            if (items.Count == 0)
            {
                caption.Text = "最近 30 天没有被判成中性的应用。";
                listPanel.Children.Add(InfoBox("很好 —— 说明常用软件都已经能识别了。"));
                return;
            }

            caption.Text = $"最近 30 天共 {items.Count} 个未识别应用，按累计时长排序。";

            foreach (var item in items)
            {
                listPanel.Children.Add(UnknownRow(item, host, () =>
                {
                    Rebuild();
                    onDataChanged();
                }));
            }
        }

        Rebuild();

        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listPanel,
        };
        panel.Children.Add(scroll);

        var refresh = new Button
        {
            Content = "重新扫描",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
        };
        refresh.Click += (_, _) => Rebuild();
        panel.Children.Add(refresh);
    }

    private static void BuildWidget(StackPanel panel, AppHost host, Action onDirty)
    {
        var config = host.Config;

        Section(panel, "悬浮窗（加速球）", "拖到屏幕边缘会自动吸附并收成一个胶囊，鼠标移入再滑出。");

        panel.Children.Add(BoolRow("启用悬浮窗", "关闭后主窗口与托盘仍然可用。",
            () => config.FloatingWidget.Enabled, v => { config.FloatingWidget.Enabled = v; onDirty(); host.Widget?.ApplyConfig(); }));

        panel.Children.Add(BoolRow("贴边自动收起", "拖到屏幕边缘附近自动吸附。",
            () => config.FloatingWidget.SnapToEdge, v => { config.FloatingWidget.SnapToEdge = v; onDirty(); }));

        panel.Children.Add(ChoiceRow("吸附到哪些边", "四个方向都支持。",
            new[] { ("auto", "左右上下都吸附（推荐）"), ("left", "只吸附左边"), ("right", "只吸附右边"), ("none", "不吸附") },
            () => config.FloatingWidget.SnapSide,
            v => { config.FloatingWidget.SnapSide = v; onDirty(); }));

        panel.Children.Add(ChoiceRow("收起后显示内容", null,
            new[] { ("timeOnly", "只显示累计时间"), ("projectAndTime", "项目名 + 时间") },
            () => config.FloatingWidget.CollapsedContent,
            v => { config.FloatingWidget.CollapsedContent = v; onDirty(); host.Widget?.ApplyConfig(); }));

        panel.Children.Add(DoubleRow("不透明度", "0.3 ~ 1.0。半透明窗体需要系统额外合成，越低越吃性能。",
            0.3, 1.0, () => config.FloatingWidget.Opacity,
            v => { config.FloatingWidget.Opacity = v; onDirty(); host.Widget?.ApplyConfig(); }));

        panel.Children.Add(BoolRow("始终置顶", null,
            () => config.FloatingWidget.AlwaysOnTop, v => { config.FloatingWidget.AlwaysOnTop = v; onDirty(); host.Widget?.ApplyConfig(); }));

        var reset = new Button
        {
            Content = "把悬浮窗移回默认位置",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        reset.Click += (_, _) =>
        {
            config.FloatingWidget.PositionX = null;
            config.FloatingWidget.PositionY = null;
            onDirty();
            host.Widget?.ResetPosition();
        };
        panel.Children.Add(reset);
    }

    private static void BuildTray(StackPanel panel, AppHost host, Action onDirty)
    {
        var config = host.Config;

        Section(panel, "启动与关闭", null);

        panel.Children.Add(BoolRow("启动时最小化到托盘", "关闭则启动后直接显示主窗口。",
            () => config.General.StartMinimizedToTray, v => { config.General.StartMinimizedToTray = v; onDirty(); }));

        panel.Children.Add(BoolRow("点关闭按钮时最小化到托盘", "关闭则点 X 直接退出程序（退出时会自动暂停并保存当前会话）。",
            () => config.General.CloseToTray, v => { config.General.CloseToTray = v; onDirty(); }));

        panel.Children.Add(BoolRow("挂起 / 自动结束会话时弹气泡提示", null,
            () => config.Notification.NotifyOnSuspend, v => { config.Notification.NotifyOnSuspend = v; onDirty(); }));

        Divider(panel);
        Section(panel, "桌面快捷方式", "在桌面放一个入口，不用每次去安装目录里翻 exe。");

        panel.Children.Add(InfoBox(
            $"将指向：{Environment.ProcessPath ?? "(未知)"}\n" +
            "如果以后把程序换到别的目录，这个快捷方式会失效，重新点一次创建即可。"));

        var createShortcut = new Button
        {
            Content = ShortcutService.Exists() ? "重新创建桌面快捷方式" : "创建桌面快捷方式",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        createShortcut.Click += (_, _) =>
        {
            try
            {
                var path = ShortcutService.CreateDesktopShortcut();
                MessageBox.Show($"已在桌面创建快捷方式：\n\n{path}",
                    "桌面快捷方式", MessageBoxButton.OK, MessageBoxImage.Information);
                createShortcut.Content = "重新创建桌面快捷方式";
            }
            catch (Exception ex)
            {
                Log.Error("App", "创建桌面快捷方式失败", ex);
                MessageBox.Show($"创建失败：{ex.Message}\n\n详细信息已写入日志：\n{Log.CurrentFilePath}",
                    "桌面快捷方式", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        panel.Children.Add(createShortcut);

        Divider(panel);
        Section(panel, "高级", null);

        panel.Children.Add(ChoiceRow("日志级别", "排查问题时用 debug；平时想省点写盘可以调成 info。",
            new[] { ("debug", "debug（最详细）"), ("info", "info"), ("warn", "warn"), ("error", "error") },
            () => config.General.LogLevel,
            v => { config.General.LogLevel = v; onDirty(); host.RefreshLogLevel(); }));

        panel.Children.Add(InfoBox("全局热键、开机自启尚未实现，会在后续版本补上。"));
    }

    private static void BuildData(StackPanel panel, AppHost host, Action onDirty, Action onDataChanged)
    {
        var config = host.Config;

        Section(panel, "数据位置", null);
        panel.Children.Add(InfoBox(
            $"当前数据目录：{host.Location.Root}\n" +
            $"便携模式：{(host.Location.IsPortable ? "是（与 exe 同目录）" : "否")}\n" +
            (string.IsNullOrEmpty(host.Location.FallbackReason) ? string.Empty : $"回退原因：{host.Location.FallbackReason}\n") +
            $"数据库：{host.Location.DatabasePath}\n" +
            $"日志：{WorkTimer.Core.Diagnostics.Log.LogDirectory}"));

        var openData = new Button
        {
            Content = "打开数据目录",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        openData.Click += (_, _) => OpenPath(host.Location.Root);
        panel.Children.Add(openData);

        Divider(panel);
        Section(panel, "归档与回收站", null);

        panel.Children.Add(IntRow("明细归档天数", "超过该天数的窗口段会聚合成日汇总后清理明细，避免数据库无限膨胀。",
            3, 3650, "天", () => config.Storage.ArchiveSegmentsAfterDays,
            v => { config.Storage.ArchiveSegmentsAfterDays = v; onDirty(); }));

        panel.Children.Add(IntRow("回收站保留天数", "已删除的项目在回收站里保留多久。",
            1, 3650, "天", () => config.Storage.TrashRetentionDays,
            v => { config.Storage.TrashRetentionDays = v; onDirty(); }));

        var purge = new Button
        {
            Content = "清理超期的回收站项目",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        purge.Click += (_, _) =>
        {
            var days = config.Storage.TrashRetentionDays;
            if (MessageBox.Show(
                    $"将永久删除回收站里超过 {days} 天的项目及其全部工时记录，此操作不可撤销。\n\n确定继续吗？",
                    "清理回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            var count = host.Repository.PurgeDeletedProjects(days);
            MessageBox.Show($"已清理 {count} 个项目。", "清理回收站", MessageBoxButton.OK, MessageBoxImage.Information);
            onDataChanged();
        };
        panel.Children.Add(purge);

        Divider(panel);
        Section(panel, "运行统计", null);
        panel.Children.Add(InfoBox(
            $"窗口段总数：{host.Repository.CountSegments()}\n" +
            $"用户规则：{host.RuleStore.Rules.Count} 条\n" +
            $"站点表：{host.SiteStore.File.Sites.Count} 个"));
    }

    private static void BuildAbout(StackPanel panel, AppHost host)
    {
        var version = typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "-";

        Section(panel, "工作计时", null);
        panel.Children.Add(InfoBox(
            $"版本：{version}\n" +
            $"运行环境：.NET {Environment.Version}\n" +
            $"界面框架：WPF（PerMonitorV2 高 DPI）\n" +
            $"数据目录：{host.Location.Root}"));

        Divider(panel);
        Section(panel, "恢复内置数据", "内置规则与站点表随程序升级更新。你的自定义规则不会被影响。");

        var resetRules = new Button
        {
            Content = "重新载入内置规则与站点表",
            Style = Res("FlatButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        resetRules.Click += (_, _) =>
        {
            host.SiteStore.ResetToBuiltIn();
            host.ReloadRules();
            MessageBox.Show($"已重新载入内置规则 {BuiltInRules.Create().Count} 条、站点 {DefaultSites.Create().Sites.Count} 个。",
                "恢复内置数据", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        panel.Children.Add(resetRules);

        Divider(panel);
        panel.Children.Add(InfoBox(
            "统计口径：跨天会话整段计入「开始计时」那一天（归属日 = 会话开始日期）。\n" +
            "本工具统计的是「前台焦点时间」，不是 CPU 忙碌时间 —— 后台跑编译时前台在浏览网页，计的是浏览网页那段。"));
    }

    // ================================================================ 行构建器

    private static Style? Res(string key) => Application.Current?.TryFindResource(key) as Style;

    private static Brush ResBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private static void Section(StackPanel panel, string title, string? hint)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = Res("SectionTitle") as Style,
            Margin = new Thickness(0, 0, 0, hint is null ? 10 : 2),
        });

        if (!string.IsNullOrEmpty(hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                Style = Res("CaptionText") as Style,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }
    }

    private static void Divider(StackPanel panel)
    {
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = ResBrush("BorderBrushSoft"),
            Margin = new Thickness(0, 14, 0, 14),
        });
    }

    private static Border InfoBox(string text) => new()
    {
        Background = ResBrush("BgAltBrush"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 2, 0, 6),
        Child = new TextBlock
        {
            Text = text,
            Style = Res("CaptionText") as Style,
            FontSize = 12,
        },
    };

    private static Grid Row(string title, string? description, FrameworkElement control, double controlWidth = double.NaN)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, Style = Res("SettingTitle") as Style });

        if (!string.IsNullOrEmpty(description))
        {
            left.Children.Add(new TextBlock
            {
                Text = description,
                Style = Res("CaptionText") as Style,
                Margin = new Thickness(0, 3, 0, 0),
                MaxWidth = 620,
            });
        }

        if (!double.IsNaN(controlWidth))
        {
            control.Width = controlWidth;
        }

        control.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(left, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(left);
        grid.Children.Add(control);
        return grid;
    }

    private static FrameworkElement BoolRow(string title, string? description, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return Row(title, description, box);
    }

    private static FrameworkElement IntRow(string title, string? description, int min, int max, string unit,
        Func<int> get, Action<int> set)
    {
        var box = new TextBox { Text = get().ToString(), Width = 90, TextAlignment = TextAlignment.Right };
        var label = new TextBlock
        {
            Text = unit,
            Style = Res("CaptionText") as Style,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        void Commit()
        {
            if (int.TryParse(box.Text.Trim(), out var value))
            {
                value = Math.Clamp(value, min, max);
                box.Text = value.ToString();
                set(value);
            }
            else
            {
                box.Text = get().ToString();
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Commit();
            }
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(box);
        stack.Children.Add(label);
        return Row(title, description, stack);
    }

    private static FrameworkElement DoubleRow(string title, string? description, double min, double max,
        Func<double> get, Action<double> set)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = get(),
            Width = 220,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Width = 44,
            TextAlignment = TextAlignment.Right,
            Style = Res("CaptionText") as Style,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Text = get().ToString("F2"),
        };

        slider.ValueChanged += (_, e) =>
        {
            label.Text = e.NewValue.ToString("F2");
            set(e.NewValue);
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(slider);
        stack.Children.Add(label);
        return Row(title, description, stack);
    }

    private static FrameworkElement ChoiceRow(string title, string? description,
        (string Value, string Label)[] options, Func<string> get, Action<string> set)
    {
        var combo = new ComboBox { Width = 260 };
        var current = get();

        foreach (var (value, label) in options)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (string?)item.Tag == current)
            {
                combo.SelectedIndex = i;
                break;
            }
        }

        if (combo.SelectedIndex < 0 && combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string value)
            {
                set(value);
            }
        };

        return Row(title, description, combo);
    }

    private static FrameworkElement RuleRow(Rule rule, AppHost host, Action refresh)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"{CategoryExtensions.ToDisplay(CategoryExtensions.Parse(rule.Category))} · " +
                   (rule.MatchMode switch
                   {
                       "all" => $"进程 {rule.ProcessPattern} 且标题含 {rule.TitlePattern}",
                       "any" => $"进程 {rule.ProcessPattern} 或标题含 {rule.TitlePattern}",
                       "title" => $"标题含 {rule.TitlePattern}",
                       "class" => $"窗口类 {rule.ClassPattern}",
                       _ => $"进程 {rule.ProcessPattern}",
                   }),
            Style = Res("SettingTitle") as Style,
        });
        text.Children.Add(new TextBlock
        {
            Text = rule.Note ?? rule.Id,
            Style = Res("CaptionText") as Style,
        });

        var remove = new Button
        {
            Content = "删除",
            Style = Res("FlatButton") as Style,
            Padding = new Thickness(10, 4, 10, 4),
        };
        remove.Click += (_, _) =>
        {
            host.RuleStore.Remove(rule.Id);
            host.ReloadRules();
            refresh();
        };

        Grid.SetColumn(text, 0);
        Grid.SetColumn(remove, 1);
        grid.Children.Add(text);
        grid.Children.Add(remove);

        return new Border
        {
            Background = ResBrush("BgAltBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
    }

    private static FrameworkElement AddRuleRow(AppHost host, Action refresh)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var process = new TextBox { Margin = new Thickness(0, 0, 8, 0) };
        process.ToolTip = "进程名模式，如 steam 或 chrome*";

        var category = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (value, label) in new[] { ("work", "工作"), ("game", "游戏"), ("video", "视频"), ("chat", "聊天") })
        {
            category.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        category.SelectedIndex = 0;

        var add = new Button { Content = "添加规则", Style = Res("FlatButton") as Style, Padding = new Thickness(12, 5, 12, 5) };
        add.Click += (_, _) =>
        {
            var pattern = process.Text.Trim();
            if (string.IsNullOrWhiteSpace(pattern))
            {
                MessageBox.Show("请填写进程名模式。", "添加规则", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var value = (category.SelectedItem as ComboBoxItem)?.Tag as string ?? "work";
            host.RuleStore.Upsert(new Rule
            {
                Id = "u-" + Guid.NewGuid().ToString("N")[..6],
                Enabled = true,
                Category = value,
                MatchMode = "process",
                ProcessPattern = pattern,
                Note = "手动添加",
            });
            host.ReloadRules();
            process.Clear();
            refresh();
        };

        Grid.SetColumn(process, 0);
        Grid.SetColumn(category, 1);
        Grid.SetColumn(add, 2);
        grid.Children.Add(process);
        grid.Children.Add(category);
        grid.Children.Add(add);
        return grid;
    }

    private static FrameworkElement SiteRow(SiteDefinition site, AppHost host, Action refresh)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"{site.Platform}　·　{CategoryExtensions.ToDisplay(CategoryExtensions.Parse(site.Category))}",
            Style = Res("SettingTitle") as Style,
        });
        text.Children.Add(new TextBlock
        {
            Text = string.Join("、", site.Patterns.Take(6)),
            Style = Res("CaptionText") as Style,
        });

        var category = new ComboBox { Width = 96, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (value, label) in new[] { ("work", "工作"), ("game", "游戏"), ("video", "视频"), ("chat", "聊天") })
        {
            category.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        for (var i = 0; i < category.Items.Count; i++)
        {
            if (category.Items[i] is ComboBoxItem item && (string?)item.Tag == site.Category)
            {
                category.SelectedIndex = i;
                break;
            }
        }
        if (category.SelectedIndex < 0)
        {
            category.SelectedIndex = 0;
        }

        category.SelectionChanged += (_, _) =>
        {
            if (category.SelectedItem is ComboBoxItem item && item.Tag is string value && value != site.Category)
            {
                site.Category = value;
                host.SiteStore.Save();
                host.ReloadRules();
            }
        };

        var remove = new Button { Content = "删除", Style = Res("FlatButton") as Style, Padding = new Thickness(10, 4, 10, 4) };
        remove.Click += (_, _) =>
        {
            host.SiteStore.Remove(site.Platform);
            host.ReloadRules();
            refresh();
        };

        Grid.SetColumn(text, 0);
        Grid.SetColumn(category, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(text);
        grid.Children.Add(category);
        grid.Children.Add(remove);
        return grid;
    }

    private static FrameworkElement AddSiteRow(AppHost host, Action refresh)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var platform = new TextBox { Margin = new Thickness(0, 0, 8, 0) };
        var patterns = new TextBox { Margin = new Thickness(0, 0, 8, 0) };
        var category = new ComboBox { Width = 96, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (value, label) in new[] { ("work", "工作"), ("game", "游戏"), ("video", "视频"), ("chat", "聊天") })
        {
            category.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        category.SelectedIndex = 0;

        var add = new Button { Content = "添加站点", Style = Res("FlatButton") as Style, Padding = new Thickness(12, 5, 12, 5) };
        add.Click += (_, _) =>
        {
            var name = platform.Text.Trim();
            var keys = patterns.Text.Split(new[] { ',', '，', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

            if (string.IsNullOrWhiteSpace(name) || keys.Count == 0)
            {
                MessageBox.Show("请填写平台名和至少一个标题关键词（多个用逗号分隔）。", "添加站点",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            host.SiteStore.AddOrUpdate(new SiteDefinition
            {
                Platform = name,
                Category = (category.SelectedItem as ComboBoxItem)?.Tag as string ?? "work",
                Patterns = keys,
                Note = "手动添加",
            });
            host.ReloadRules();
            platform.Clear();
            patterns.Clear();
            refresh();
        };

        Grid.SetColumn(platform, 0);
        Grid.SetColumn(patterns, 1);
        Grid.SetColumn(category, 2);
        Grid.SetColumn(add, 3);
        grid.Children.Add(platform);
        grid.Children.Add(patterns);
        grid.Children.Add(category);
        grid.Children.Add(add);
        return grid;
    }

    private static FrameworkElement UnknownRow(WorkTimer.Core.Data.UnknownProcessRecord item, AppHost host, Action refresh)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"{item.DisplayName}（{item.ProcessName}）　累计 {TimeFormat.Hms(item.TotalSeconds)}　{item.SegmentCount} 段",
            Style = Res("SettingTitle") as Style,
        });

        if (!string.IsNullOrWhiteSpace(item.SampleTitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = "示例标题：" + item.SampleTitle,
                Style = Res("CaptionText") as Style,
            });
        }

        var category = new ComboBox { Width = 96, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (value, label) in new[] { ("work", "工作"), ("game", "游戏"), ("video", "视频"), ("chat", "聊天") })
        {
            category.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        category.SelectedIndex = 0;

        var apply = new Button { Content = "归类", Style = Res("FlatButton") as Style, Padding = new Thickness(12, 4, 12, 4) };
        apply.Click += (_, _) =>
        {
            var value = (category.SelectedItem as ComboBoxItem)?.Tag as string ?? "work";
            var parsed = CategoryExtensions.Parse(value);

            host.RuleStore.Upsert(new Rule
            {
                Id = "u-" + Guid.NewGuid().ToString("N")[..6],
                Enabled = true,
                Category = value,
                MatchMode = "process",
                ProcessPattern = item.ProcessName,
                Note = $"{item.DisplayName}（未识别归类）",
            });
            host.ReloadRules();

            var backfilled = host.Repository.ReclassifySegmentsByProcess(item.ProcessName, parsed);
            MessageBox.Show($"已把「{item.DisplayName}」归为{CategoryExtensions.ToDisplay(parsed)}，并回填历史 {backfilled} 条窗口段。",
                "归类完成", MessageBoxButton.OK, MessageBoxImage.Information);
            refresh();
        };

        Grid.SetColumn(text, 0);
        Grid.SetColumn(category, 1);
        Grid.SetColumn(apply, 2);
        grid.Children.Add(text);
        grid.Children.Add(category);
        grid.Children.Add(apply);
        return grid;
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开目录就算了
        }
    }
}
