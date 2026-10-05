namespace WorkTimer.Core.Classification;

/// <summary>
/// 内置规则库（随程序升级覆盖，用户改动请写在 rules.user.json）。
///
/// 组织方式：整体按"先具体后宽泛"排列，因为引擎是 first-match-wins。
///  1. 具体软件（进程名精确/通配）
///  2. 强信号窗口类（UnityWndClass / UnrealWindow / Valve001 这类只可能是游戏）
///  3. 兜底关键词
/// 越靠后越宽泛 —— 宽泛规则放前面会把具体软件的错误归类。
/// </summary>
public static class BuiltInRules
{
    /// <summary>浏览器进程名。命中后走站点识别而不是直接判类别。</summary>
    public static readonly string[] BrowserProcesses =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
        "360se", "360chrome", "360ChromeX", "QQBrowser", "sogouexplorer",
        "Maxthon", "TheWorld", "UCBrowser", "2345Explorer", "LBBROWSER",
        "liebao", "QuarkPC", "AlipayBrowser", "browser",
    };

    /// <summary>
    /// 系统外壳窗口类名 —— 这些一律归 Inherit：
    /// 没有前台窗口、切回桌面、打开开始菜单 / 通知中心 / 任务视图、锁屏。
    /// 注意：文件资源管理器的真实窗口类名是 CabinetWClass，<b>不在</b>此列，它是工作。
    /// </summary>
    public static readonly string[] ShellWindowClasses =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "LockScreenControllerProxyWindow",
        "SysShadow",
        "ForegroundStaging",
        "TaskListThumbnailWnd",
        "NarratorHelperWindow",
        "Windows.Internal.Shell.TabProxyWindow",
    };

    /// <summary>进程名属于系统外壳的，也归 Inherit。</summary>
    public static readonly string[] ShellProcesses =
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "LockApp", "LogonUI", "TextInputHost", "ApplicationFrameHost",
        "explorer",           // 窗口类判定已拦掉任务栏与桌面，走到这里的是文件资源管理器
    };

    public static List<Rule> Create()
    {
        var rules = new List<Rule>();
        var order = 10;

        void Add(string category, string? process, string note)
        {
            rules.Add(new Rule
            {
                Id = "bi-" + (rules.Count + 1).ToString("D3"),
                Enabled = true,
                Order = order,
                Category = category,
                ProcessPattern = process,
                MatchMode = "process",
                Note = note,
                IsBuiltIn = true,
            });
            order += 10;
        }

        void AddClass(string category, string classPattern, string note, bool suppressIdle = false)
        {
            rules.Add(new Rule
            {
                Id = "bi-" + (rules.Count + 1).ToString("D3"),
                Enabled = true,
                Order = order,
                Category = category,
                ClassPattern = classPattern,
                MatchMode = "class",
                SuppressIdle = suppressIdle,
                Note = note,
                IsBuiltIn = true,
            });
            order += 10;
        }

        void AddMany(string category, string note, bool suppressIdle, params string[] processes)
        {
            foreach (var process in processes)
            {
                rules.Add(new Rule
                {
                    Id = "bi-" + (rules.Count + 1).ToString("D3"),
                    Enabled = true,
                    Order = order,
                    Category = category,
                    ProcessPattern = process,
                    MatchMode = "process",
                    SuppressIdle = suppressIdle,
                    Note = note,
                    IsBuiltIn = true,
                });
                order += 10;
            }
        }

        // ============================================================ 工作 · 系统与办公

        Add("work", "explorer", "文件资源管理器");
        AddMany("work", "系统工具", false,
            "Everything", "SnippingTool", "Snipaste", "PixPin", "FastStoneCapture",
            "ScreenToGif", "ShareX", "Greenshot", "obs64", "OBS64", "Bandicam",
            "mspaint", "calc", "notepad", "Notepad++", "Notepad3", "notepad++",
            "mstsc", "Taskmgr", "regedit", "services", "eventvwr", "diskmgmt",
            "devmgmt", "control", "mmc", "resmon", "perfmon", "SystemSettings",
            "ApplicationFrameHost", "CompMgmtLauncher", "charmap", "osk",
            "WinMerge", "BeyondCompare", "BCompare", "7zFM", "WinRAR", "Bandizip",
            "NanaZip", "PeaZip", "BulkRenameUtility", "EverythingToolbar");

        AddMany("work", "Office", false,
            "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS",
            "MSPUB", "VISIO", "wps", "et", "wpp", "wpspdf", "ksolaunch",
            "wpscloudsvr", "wpscenter");

        AddMany("work", "PDF / 阅读", false,
            "Acrobat", "AcroRd32", "AcrobatDC", "FoxitPDFReader", "FoxitReader",
            "SumatraPDF", "PDFXCview", "PDFReader", "okular", "calibre",
            "Calibre", "Kindle", "kindle", "Xodo", "EdgeView");

        AddMany("work", "笔记与知识库", false,
            "Obsidian", "Notion", "Typora", "MarkText", "Zettlr", "Logseq",
            "Joplin", "Evernote", "印象笔记", "YoudaoNote", "有道云笔记",
            "WizNote", "为知笔记", "Roam", "RemNote", "Anki", "xmind", "XMind",
            "MindManager", "mubu", "幕布", "ProcessOn", "drawio", "spacedesk",
            "Anytype", "思源笔记", "SiYuan", "flowus", "FlowUs");

        // ============================================================ 工作 · 开发

        AddMany("work", "代码编辑器 / IDE", false,
            "devenv", "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf",
            "Trae", "Zed", "atom", "Brackets", "sublime_text", "SublimeText",
            "rider64", "idea64", "pycharm64", "webstorm64", "phpstorm64",
            "clion64", "goland64", "datagrip64", "rubymine64", "appcode",
            "AndroidStudio", "studio64", "eclipse", "netbeans", "Xcode",
            "HBuilderX", "hbuilder", "SublimeMerge", "vim", "gvim", "nvim",
            "emacs", "helix", "TextMate", "UltraEdit", "uedit64", "EditPlus",
            "SlickEdit", "CodeBlocks", "QtCreator", "qtcreator", "SharpDevelop",
            "Lazarus", "Dev-Cpp", "devcpp", "Keil", "IAR*", "Arduino",
            "Arduino IDE", "mplab_ide", "STM32CubeIDE", "Vivado", "quartus",
            "MATLAB", "matlab", "RStudio", "rstudio", "spyder", "jupyter",
            "Jupyter", "Spyder", "thonny", "Processing", "godot", "Godot",
            "Unity", "Unity Hub", "UnrealEditor", "Blender", "BlenderLauncher");

        AddMany("work", "终端 / Shell", false,
            "WindowsTerminal", "wt", "powershell", "pwsh", "cmd", "conhost",
            "alacritty", "kitty", "wezterm", "wezterm-gui", "ConEmu64", "ConEmu",
            "cmder", "MobaXterm", "mobaxterm", "Xshell*", "Xftp*", "putty",
            "puttytel", "SecureCRT", "tabby", "Terminus", "Termius",
            "Hyper", "FluentTerminal", "OpenConsole", "nushell", "nu", "wsl");

        AddMany("work", "版本控制", false,
            "GitExtensions", "TortoiseGitProc", "TortoiseGitMerge", "TortoiseProc",
            "SourceTree", "Fork", "GitHubDesktop", "GitKraken", "SmartGit",
            "git-bash", "git", "gitk", "Tower", "Sourcetree");

        AddMany("work", "数据库", false,
            "DBeaver", "dbeaver", "Navicat*", "navicat*", "ssms", "MySQLWorkbench",
            "mysqlworkbench", "workbench", "redis-cli", "RedisInsight",
            "Another Redis Desktop Manager", "MongoDBCompass", "mongosh",
            "pgAdmin4", "HeidiSQL", "SQLyog", "ToRedis", "DataGrip",
            "SQLiteStudio", "sqlitestudio", "DB Browser for SQLite", "db browser*",
            "RoboMongo", "Robo 3T", "TablePlus", "Beekeeper Studio", "redisplusplus",
            "MiniZinc", "DbGate", "Dbgate");

        AddMany("work", "接口 / 抓包 / 调试", false,
            "postman", "Postman", "Apifox", "Insomnia", "Hoppscotch", "RapidAPI",
            "Fiddler*", "fiddler", "Wireshark", "charles", "Charles", "mitmproxy",
            "mitmweb", "HttpCanary", "BurpSuite*", "swagger", "SoapUI",
            "Paw", "Thunder Client", "Yaak", "Bruno", "curl");

        AddMany("work", "容器 / 云 / 虚拟化", false,
            "docker*", "Docker Desktop", "com.docker.backend", "kubectl", "Lens",
            "Rancher Desktop", "minikube", "kind", "Podman", "VirtualBoxVM",
            "VirtualBox", "vmware-vmx", "vmplayer", "vmware", "VBoxSVC",
            "VBoxHeadless", "qemu*", "Hyper-V*", "vmconnect", "AnyDesk",
            "ToDesk", "SunloginClient", "SunloginRemote", "向日葵", "TeamViewer",
            "RustDesk", "Parsec", "nomachine", "RealVNC", "vncviewer",
            "wslservice", "WSL", "TerminalService", "tailscale", "clash*",
            "Clash*", "v2ray*", "sing-box", "mihomo");

        AddMany("work", "设计 / 建模", false,
            "Photoshop", "Illustrator", "InDesign", "AfterFX", "Premiere",
            "Premiere Pro", "Audition", "Lightroom", "CorelDRW", "CorelDRAW",
            "Figma", "figma_agent", "sketch", "Sketch", "Adobe XD", "AdobeXD",
            "AxureRP", "Axure", "墨刀", "modao", "MasterGo", "mastergo",
            "即时设计", "jsDesign", "Canva", "canva", "创客贴", "chuangkit",
            "Blender", "Maya", "3dsmax", "3ds Max", "ZBrush", "SketchUp",
            "Rhino", "Cinema 4D", "CINEMA 4D", "Houdini", "Substance*",
            "AutoCAD", "acad", "SolidWorks", "SLDWORKS", "Inventor", "CATIA",
            "UGII", "Creo", "Alias", "KeyShot", "Lumion", "ENSCAPE", "Twinmotion");

        // ============================================================ 工作 · 会议与协作（抑制空闲）

        AddMany("work", "会议（抑制空闲检测）", true,
            "wemeetapp", "TencentMeeting", "TencentMeetingApp", "Zoom", "zoom",
            "Teams", "ms-teams", "MicrosoftTeams", "Feishu", "Lark", "飞书",
            "DingTalk", "钉钉", "WXWork", "企业微信", "Slack", "Webex*",
            "webexmta", "wemeet", "VooV Meeting", "voov", "Workspace",
            "Gotomeeting", "BlueJeans", "Lifesize", "Polycom", "小鱼易连",
            "好视通", "瞩目", "Zhumu", "随锐", "Welink", "WeLink");

        // ============================================================ 工作 · AI 工具

        AddMany("work", "AI 桌面客户端", false,
            "WorkBuddy", "workbuddy", "chatgpt", "ChatGPT", "Claude", "claude",
            "Gemini", "Copilot", "cursor", "windsurf", "Trae", "Ollama",
            "ollama", "LM Studio", "lmstudio", "Jan", "Msty", "CherryStudio",
            "Cherry Studio", "Chatbox", "chatbox", "NextChat", "LobeChat",
            "豆包", "Doubao", "doubao", "通义", "Tongyi", "文小言", "元宝",
            "Yuanbao", "Kimi", "kimi", "moonshot", "DeepSeek", "deepseek",
            "智谱清言", "ChatGLM", "Xinghuo", "讯飞星火", "星火", "Metaso",
            "秘塔", "Perplexity", "Perplexica", "AnythingLLM", "open-webui",
            "Text Generation WebUI", "ComfyUI", "A1111", "Stable Diffusion*",
            "DiffusionBee", "Fooocus", "InvokeAI", "koboldcpp", "llama.cpp",
            "GPT4All", "Witsy", "BoltAI", "TypingMind", "Msty");

        // ============================================================ 电脑端 AI/办公 的其他常见 Electron 应用

        AddMany("work", "常用效率工具", false,
            "Snipaste", "Quicker", "utools", "uTools", "Listary", "Ditto",
            "ClipboardFusion", "Clipy", "Twinkle Tray", "EarTrumpet",
            "PowerToys*", "Rainmeter", "AutoHotkey*", "AutoHotkeyUX",
            "Keypirinha", "Wox", "Fluent Search", "Flow Launcher",
            "Capslock+", "StrokeIt", "MouseInc", "Wgestures", "Rime*",
            "WeaselDeployer", "WeaselServer", "小狼毫");

        // ============================================================ 娱乐 · 视频 / 音频 / 阅读

        AddMany("video", "视频播放器", false,
            "PotPlayerMini64", "PotPlayerMini", "PotPlayer64", "vlc", "VLC",
            "mpv", "mpvnet", "mpc-hc64", "mpc-be64", "mpc-be", "MPC-BE",
            "KMPlayer64", "KMPlayer", "GOM64", "GOM", "QQPlayer", "QQLive",
            "bilibili", "哔哩哔哩", "iqiyi", "QYClient", "爱奇艺", "YoukuClient",
            "YoukuDesktop", "优酷", "MgtvClient", "芒果TV", "PPTV", "PPTVHD",
            "sohuvideo", "搜狐视频", "letv", "乐视视频", "douyin", "抖音",
            "TikTok", "kuaishou", "快手", "huya", "虎牙", "douyu", "斗鱼",
            "Twitch", "MiguVideo", "咪咕视频", "1905", "YinheVideo");

        AddMany("video", "音乐 / 音频", false,
            "cloudmusic", "网易云音乐", "QQMusic", "QQ音乐", "KuGou", "酷狗音乐",
            "kwmusic", "酷我音乐", "Spotify", "foobar2000", "AIMP", "Audacity",
            "MusicBee", "Dopamine", "椒盐音乐", "洛雪音乐", "lx-music*",
            "喜马拉雅", "Ximalaya", "蜻蜓FM", "懒人听书", "企鹅FM",
            "Podcast", "Listening", "汽水音乐");

        AddMany("video", "小说 / 漫画 / 阅读器", false,
            "番茄小说", "FanqieNovel", "起点读书", "qidian", "晋江小说",
            "小说阅读器", "七猫免费小说", "微信读书", "WeRead", "ireader",
            "掌阅", "多看阅读", "DuokanReader", "kindle", "静读天下",
            "MoonReader", "NeatReader", "Komga", "Tachiyomi", "Mihon",
            "哔哩哔哩漫画", "快看漫画", "KuKan", "动漫之家");

        // ============================================================ 娱乐 · 聊天 / 社交

        AddMany("chat", "聊天 / 社交", false,
            "WeChat", "Weixin", "微信", "QQ", "QQMusicMini", "TIM", "Telegram",
            "Discord", "WhatsApp", "Line", "Messenger", "Facebook", "Instagram",
            "Skype", "Viber", "KakaoTalk", "Zalo", "Signal", "Element",
            "阿里旺旺", "旺旺", "千牛", "AliWorkbench", "千牛工作台",
            "钉钉个人版", "陌陌", "探探", "Soul", "soul", "即刻", "最右",
            "YY", "YY语音", "TT语音", "比心", "Uki", "Hago");

        // ============================================================ 娱乐 · 游戏

        // 平台 / 启动器
        AddMany("game", "游戏平台", false,
            "steam", "steamwebhelper", "steamservice", "wegame", "WeGame",
            "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "Battle.net Helper",
            "GalaxyClient", "GalaxyClientHelper", "uplay", "UbisoftConnect",
            "Ubisoft Game Launcher", "Origin", "EADesktop", "EA app",
            "RiotClientServices", "RiotClient", "Playnite", "GOGGalaxy",
            "txgameassistant", "腾讯手游助手", "wegameapp", "wegamelite",
            "NeteaseGame", "MuMuPlayer", "MuMuNxDevice", "mumuplayer",
            "LDPlayer", "dnplayer", "dnmultiplayer", "Nox", "NoxVMHandle",
            "BlueStacks*", "HD-Player", "MEmu*", "MEmuConsole",
            "smartgamelauncher", "GameCenter", "游戏中心", "游戏盒子",
            "云·星穹铁道", "云原神", "腾讯先锋", "START云游戏", "wegamecloud");

        // 具体游戏（进程名，覆盖常见的）
        AddMany("game", "游戏", false,
            "LeagueClient", "League of Legends", "LeagueClientUx", "RiotClientCrashHandler",
            "dota2", "cs2", "csgo", "valorant", "VALORANT", "VALORANT-Win64-Shipping",
            "GenshinImpact", "YuanShen", "StarRail", "崩坏3", "HonkaiImpact3",
            "BH3", "ZenlessZoneZero", "绝区零", "Arknights", "明日方舟",
            "wutheringwaves", "鸣潮", "Minecraft*", "javaw", "Roblox*",
            "RobloxPlayerBeta", "Terraria", "Stardew Valley", "Among Us",
            "PUBG", "TslGame", "NarakaBladepoint", "永劫无间", "APEX",
            "r5apex", "Overwatch", "守望先锋", "Diablo*", "Wow", "WowClassic",
            "World of Warcraft", "Bns", "剑灵", "jx3", "剑网3", "剑侠情缘",
            "MHOnline", "梦幻西游", "大话西游", "xyq", "dh2", "倩女幽魂",
            "逆水寒", "天涯明月刀", "天刀",
            "地下城与勇士", "DNF", "DungeonFighter", "穿越火线", "crossfire",
            "crossfireclient", "Counter-Strike", "qqfc", "QQ飞车",
            "QQSpeed", "QQ炫舞", "欢乐斗地主", "DDZ", "斗地主",
            "qqgame", "QQ游戏", "腾讯游戏平台", "英雄联盟", "王者荣耀",
            "和平精英", "PUBGM", "阴阳师", "Onmyoji", "第五人格", "IdentityV",
            "蛋仔派对", "EggyParty", "元梦之星", "dreamstar", "三国杀",
            "sanguosha", "魔兽世界", "炉石传说", "Hearthstone", "HearthstoneBeta",
            "星际争霸", "StarCraft", "SC2", "StormHero", "风暴英雄",
            "跑跑卡丁车", "KartRider", "劲舞团", "冒险岛", "MapleStory",
            "传奇", "Mir2", "热血传奇", "征途", "ztgame", "完美世界",
            "太极熊猫", "崩坏学园", "少女前线", "碧蓝航线", "azurlane",
            "明日方舟", "战双帕弥什", "深空之眼", "无期迷途", "重返未来",
            "卡拉彼丘", "strinova", "尘白禁区", "尘白", "崩坏：星穹铁道",
            "BlackMythWukong", "黑神话", "Wukong", "b1-Win64-Shipping",
            "GTA*", "Cyberpunk2077", "EldenRing", "BaldursGate3", "bg3",
            "hollowknight", "HollowKnight", "Terraria", "Titanfall2",
            "RainbowSix", "RainbowSixSiege", "warframe", "Warframe",
            "Destiny2", "TheFinals", "DeltaForce", "三角洲行动", "MarvelRivals",
            "Deadlock", "Palworld", "帕鲁", "鹅鸭杀", "GooseGooseDuck",
            "QQT", "全民k歌", "WeSing", "唱吧");

        // 游戏引擎强信号窗口类 —— 这三类窗口几乎只出现在游戏里
        AddClass("game", "UnityWndClass", "Unity 游戏引擎窗口");
        AddClass("game", "UnrealWindow", "Unreal 游戏引擎窗口");
        AddClass("game", "Valve001", "Source 引擎游戏窗口");
        AddClass("game", "RIOT_WINDOW_CLASS", "Riot 游戏窗口");
        AddClass("game", "Godot_win32", "Godot 游戏窗口");
        AddClass("game", "RPG Maker*", "RPG Maker 游戏窗口");
        AddClass("game", "SandboxieControlWndClass", "沙箱（常用于多开游戏）");

        return rules;
    }
}
