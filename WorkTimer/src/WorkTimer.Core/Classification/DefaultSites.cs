namespace WorkTimer.Core.Classification;

/// <summary>
/// 内置站点平台映射（浏览器窗口标题 → 平台名 + 类别）。用户改动写在 sites.json。
///
/// 匹配是"标题包含关键词"，所以关键词要挑标题里一定出现的片段。
/// 每条都同时产出展示名（platform）和类别（category），明细表因此显示
/// "Microsoft Edge · 哔哩哔哩 15 分钟"而不是裸标题（也顺便照顾了隐私）。
/// </summary>
public static class DefaultSites
{
    public const string FallbackPlatform = "网页浏览";

    public static SiteFile Create()
    {
        var file = new SiteFile
        {
            FallbackPlatform = FallbackPlatform,
            FallbackCategory = "work",
        };

        void Add(string platform, string category, params string[] patterns)
        {
            file.Sites.Add(new SiteDefinition
            {
                Platform = platform,
                Category = category,
                Patterns = patterns.ToList(),
                IsBuiltIn = true,
            });
        }

        // ============================================================ 娱乐 · 视频

        Add("哔哩哔哩", "video", "bilibili", "哔哩哔哩", "b23.tv");
        Add("YouTube", "video", "youtube");
        Add("Netflix", "video", "netflix");
        Add("腾讯视频", "video", "腾讯视频", "v.qq.com");
        Add("爱奇艺", "video", "爱奇艺", "iqiyi");
        Add("优酷", "video", "优酷", "youku");
        Add("芒果TV", "video", "芒果tv", "mgtv");
        Add("咪咕视频", "video", "咪咕视频", "miguvideo");
        Add("PPTV", "video", "pptv", "pp视频");
        Add("搜狐视频", "video", "搜狐视频", "tv.sohu");
        Add("乐视视频", "video", "乐视视频", "le.com");
        Add("抖音", "video", "抖音", "douyin");
        Add("快手", "video", "快手", "kuaishou");
        Add("西瓜视频", "video", "西瓜视频", "ixigua");
        Add("微视", "video", "微视", "weishi");
        Add("斗鱼", "video", "斗鱼", "douyu");
        Add("虎牙", "video", "虎牙", "huya");
        Add("Twitch", "video", "twitch");
        Add("TikTok", "video", "tiktok");
        Add("央视频", "video", "央视频", "yangshipin");
        Add("韩剧TV", "video", "韩剧tv", "hanjutv");
        Add("樱花动漫", "video", "樱花动漫", "yhdm");
        Add("AGE动漫", "video", "age动漫", "agedm");
        Add("低端影视", "video", "低端影视", "ddrk");
        Add("人人视频", "video", "人人视频", "rrmj");
        Add("泥视频", "video", "泥视频", "nivod");
        Add("漫猫动漫", "video", "漫猫", "manmiao");
        Add("ACG 视频站", "video", "acg", "动漫", "番剧", "新番");

        // ============================================================ 娱乐 · 音乐 / 音频 / 小说

        Add("网易云音乐", "video", "网易云音乐", "music.163");
        Add("QQ音乐", "video", "qq音乐", "y.qq.com");
        Add("酷狗音乐", "video", "酷狗音乐", "kugou");
        Add("酷我音乐", "video", "酷我音乐", "kuwo");
        Add("Spotify", "video", "spotify");
        Add("喜马拉雅", "video", "喜马拉雅", "ximalaya");
        Add("蜻蜓FM", "video", "蜻蜓fm", "qingting");
        Add("懒人听书", "video", "懒人听书", "lrts");
        Add("番茄小说", "video", "番茄小说", "fanqienovel");
        Add("起点中文网", "video", "起点中文网", "qidian", "起点读书");
        Add("晋江文学城", "video", "晋江文学城", "jjwxc");
        Add("七猫小说", "video", "七猫", "qimao");
        Add("笔趣阁", "video", "笔趣阁", "biquge");
        Add("微信读书", "video", "微信读书", "weread");
        Add("掌阅", "video", "掌阅", "zhangyue");
        Add("快看漫画", "video", "快看漫画", "kuaikanmanhua");
        Add("动漫之家", "video", "动漫之家", "dmzj");
        Add("哔哩哔哩漫画", "video", "哔哩哔哩漫画", "bilibilicomics");

        // ============================================================ 娱乐 · 社交 / 论坛

        Add("微博", "video", "微博", "weibo");
        Add("知乎", "video", "知乎", "zhihu.com");
        Add("小红书", "video", "小红书", "xiaohongshu");
        Add("百度贴吧", "video", "百度贴吧", "tieba.baidu", "- 贴吧");
        Add("豆瓣", "video", "豆瓣", "douban");
        Add("虎扑", "video", "虎扑", "hupu");
        Add("Reddit", "video", "reddit");
        Add("NGA", "video", "nga", "艾泽拉斯");
        Add("水木社区", "video", "水木社区", "newsmth");
        Add("天涯社区", "video", "天涯社区", "tianya");
        Add("脉脉", "video", "脉脉", "maimai");
        Add("即刻", "video", "即刻app", "okjike");
        Add("最右", "video", "最右", "izuiyou");
        Add("Soul", "video", "soul app", "- soul");

        // ============================================================ 娱乐 · 购物（摸鱼常见，可按需在 sites.json 删除）

        Add("淘宝", "video", "淘宝", "taobao.com", "天猫");
        Add("京东", "video", "京东", "jd.com", "- jd");
        Add("拼多多", "video", "拼多多", "pinduoduo", "yangkeduo");
        Add("闲鱼", "video", "闲鱼", "goofish", "2.taobao");
        Add("唯品会", "video", "唯品会", "vip.com");
        Add("苏宁", "video", "苏宁易购", "suning");
        Add("得物", "video", "得物", "dewu", "poizon");
        Add("1688", "video", "1688", "alibaba.com");

        // ============================================================ 工作 · 代码 / 技术

        Add("GitHub", "work", "github");
        Add("GitLab", "work", "gitlab");
        Add("Gitee 码云", "work", "gitee", "码云");
        Add("Bitbucket", "work", "bitbucket");
        Add("Stack Overflow", "work", "stackoverflow", "stack overflow");
        Add("Stack Exchange", "work", "stackexchange", "superuser", "serverfault");
        Add("SegmentFault", "work", "segmentfault");
        Add("CSDN", "work", "csdn");
        Add("掘金", "work", "掘金", "juejin");
        Add("博客园", "work", "博客园", "cnblogs");
        Add("InfoQ", "work", "infoq");
        Add("开源中国", "work", "oschina", "开源中国");
        Add("V2EX", "work", "v2ex");
        Add("MDN 文档", "work", "developer.mozilla", "mdn web docs", "mdn");
        Add("Microsoft Learn", "work", "learn.microsoft", "microsoft docs", "docs.microsoft");
        Add("W3School", "work", "w3school", "w3cschool");
        Add("菜鸟教程", "work", "菜鸟教程", "runoob");
        Add("DevDocs", "work", "devdocs");
        Add("Read the Docs", "work", "readthedocs", "read the docs");
        Add("GeeksforGeeks", "work", "geeksforgeeks");
        Add("Baeldung", "work", "baeldung");
        Add("TutorialsPoint", "work", "tutorialspoint");
        Add("CodePen", "work", "codepen");
        Add("CodeSandbox", "work", "codesandbox");
        Add("JSFiddle", "work", "jsfiddle");
        Add("StackBlitz", "work", "stackblitz");
        Add("Replit", "work", "replit");
        Add("LeetCode", "work", "leetcode", "力扣");
        Add("牛客网", "work", "nowcoder", "牛客");
        Add("洛谷", "work", "luogu", "洛谷");
        Add("Codeforces", "work", "codeforces");
        Add("AtCoder", "work", "atcoder");
        Add("HackerRank", "work", "hackerrank");
        Add("Codewars", "work", "codewars");
        Add("npm", "work", "npmjs", "npm -");
        Add("NuGet", "work", "nuget");
        Add("PyPI", "work", "pypi");
        Add("Crates.io", "work", "crates.io", "crates.io");
        Add("Maven", "work", "maven", "mvnrepository");
        Add("Packagist", "work", "packagist");
        Add("Docker Hub", "work", "docker hub", "hub.docker");
        Add("Kubernetes 文档", "work", "kubernetes", "k8s");
        Add("Terraform", "work", "terraform", "registry.terraform");
        Add("Ansible", "work", "ansible");
        Add("Redis 文档", "work", "redis.io", "redis 命令");
        Add("MySQL 文档", "work", "dev.mysql", "mysql 文档");
        Add("PostgreSQL 文档", "work", "postgresql", "postgres");
        Add("MongoDB 文档", "work", "mongodb.com", "mongodb 文档");
        Add("Elastic 文档", "work", "elastic.co", "elasticsearch");
        Add(".NET 文档", "work", "dotnet.microsoft", ".net api");
        Add("Java 文档", "work", "docs.oracle", "java se");
        Add("Python 文档", "work", "docs.python", "python 文档");
        Add("Rust 文档", "work", "doc.rust-lang", "docs.rs");
        Add("Go 文档", "work", "go.dev", "golang");

        // ============================================================ 工作 · 云 / DevOps / 运维

        Add("腾讯云", "work", "腾讯云", "cloud.tencent", "腾讯云控制台");
        Add("阿里云", "work", "阿里云", "aliyun", "阿里云控制台");
        Add("华为云", "work", "华为云", "huaweicloud");
        Add("百度智能云", "work", "百度智能云", "cloud.baidu");
        Add("AWS", "work", "aws.amazon", "amazon web services", "aws 管理控制台");
        Add("Azure", "work", "azure.microsoft", "portal.azure", "azure portal");
        Add("Google Cloud", "work", "console.cloud.google", "google cloud");
        Add("DigitalOcean", "work", "digitalocean");
        Add("Vercel", "work", "vercel");
        Add("Netlify", "work", "netlify");
        Add("Cloudflare", "work", "cloudflare", "dash.cloudflare");
        Add("Grafana", "work", "grafana");
        Add("Prometheus", "work", "prometheus");
        Add("Kibana", "work", "kibana");
        Add("Sentry", "work", "sentry.io", "sentry 错误");
        Add("Datadog", "work", "datadoghq", "datadog");
        Add("Jenkins", "work", "jenkins");
        Add("SonarQube", "work", "sonarqube", "sonar");
        Add("Gitea / 自建 Git", "work", "gitea");
        Add("运维后台", "work", "控制台", "管理后台", "运维平台", "监控大盘");

        // ============================================================ 工作 · AI 工具

        Add("ChatGPT", "work", "chatgpt", "openai", "- chatgpt");
        Add("Claude", "work", "claude.ai", "- claude", "anthropic");
        Add("Gemini", "work", "gemini", "bard");
        Add("GitHub Copilot", "work", "copilot", "github copilot");
        Add("Perplexity", "work", "perplexity");
        Add("DeepSeek", "work", "deepseek", "深度求索");
        Add("通义千问", "work", "通义", "千问", "qwen", "tongyi");
        Add("豆包", "work", "豆包", "doubao");
        Add("Kimi", "work", "kimi", "月之暗面", "moonshot");
        Add("文心一言", "work", "文心一言", "yiyan.baidu", "文心大模型");
        Add("智谱清言", "work", "智谱", "chatglm", "zhipu", "glm");
        Add("讯飞星火", "work", "星火认知", "xinghuo", "讯飞星火");
        Add("腾讯元宝", "work", "腾讯元宝", "yuanbao");
        Add("秘塔搜索", "work", "秘塔", "metaso");
        Add("天工 AI", "work", "天工", "tiangong");
        Add("360 智脑", "work", "360智脑", "智脑");
        Add("Hugging Face", "work", "huggingface");
        Add("ModelScope 魔搭", "work", "modelscope", "魔搭");
        Add("OpenRouter", "work", "openrouter");
        Add("Poe", "work", "poe.com", "quora poe");
        Add("能效/中转 AI 平台", "work", "api.openai", "aiproxy", "oneapi", "newapi");

        // ============================================================ 工作 · 在线文档 / 协作

        Add("腾讯文档", "work", "腾讯文档", "docs.qq.com");
        Add("金山文档 / WPS 云", "work", "金山文档", "kdocs", "wps文档", "wps云");
        Add("百度文库", "work", "百度文库", "wenku.baidu");
        Add("语雀", "work", "语雀", "yuque");
        Add("飞书文档", "work", "飞书文档", "feishu", "lark");
        Add("石墨文档", "work", "石墨文档", "shimo");
        Add("Notion", "work", "notion");
        Add("Confluence", "work", "confluence");
        Add("Jira", "work", "jira", "atlassian");
        Add("Trello", "work", "trello");
        Add("Asana", "work", "asana");
        Add("禅道", "work", "禅道", "zentao");
        Add("TAPD", "work", "tapd");
        Add("Teambition", "work", "teambition");
        Add("Worktile", "work", "worktile");
        Add("飞书多维表格", "work", "多维表格", "bitable");
        Add("Figma", "work", "figma");
        Add("蓝湖", "work", "蓝湖", "lanhu");
        Add("MasterGo", "work", "mastergo");
        Add("即时设计", "work", "即时设计", "js.design");
        Add("墨刀", "work", "墨刀", "modao");
        Add("ProcessOn", "work", "processon");
        Add("draw.io", "work", "draw.io", "diagrams.net");
        Add("Excalidraw", "work", "excalidraw");
        Add("Miro", "work", "miro.com", "mural");
        Add("ProcessOn 模板", "work", "流程图", "思维导图");
        Add("在线 Office", "work", "office.com", "office 365", "onedrive");
        Add("腾讯会议网页版", "work", "腾讯会议", "meeting.tencent");
        Add("邮箱", "work", "收件箱", "outlook", "163邮箱", "mail.qq", "企业邮箱", "gmail");

        // ============================================================ 工作 · 学术 / 学习

        Add("中国知网", "work", "知网", "cnki");
        Add("万方数据", "work", "万方数据", "wanfangdata");
        Add("维普", "work", "维普", "cqvip");
        Add("arXiv", "work", "arxiv");
        Add("Google 学术", "work", "scholar.google", "谷歌学术");
        Add("ResearchGate", "work", "researchgate");
        Add("Semantic Scholar", "work", "semanticscholar", "semantic scholar");
        Add("PubMed", "work", "pubmed", "ncbi.nlm");
        Add("IEEE Xplore", "work", "ieeexplore", "ieee xplore");
        Add("ACM DL", "work", "dl.acm.org", "acm digital");
        Add("Springer", "work", "springer", "link.springer");
        Add("ScienceDirect", "work", "sciencedirect");
        Add("Web of Science", "work", "webofscience", "web of science");
        Add("超星 / 学习通", "work", "超星", "chaoxing", "学习通");
        Add("中国大学MOOC", "work", "icourse163", "中国大学mooc");
        Add("学堂在线", "work", "xuetangx", "学堂在线");
        Add("慕课网", "work", "imooc", "慕课网");
        Add("极客时间", "work", "极客时间", "geekbang");
        Add("Coursera", "work", "coursera");
        Add("edX", "work", "edx.org");
        Add("Udemy", "work", "udemy");
        Add("菜鸟/教程站", "work", "教程", "课程", "文档中心", "开发指南");

        // ============================================================ 工作 · 招聘 / 金融 / 政务

        Add("BOSS直聘", "work", "boss直聘", "zhipin.com");
        Add("拉勾", "work", "拉勾", "lagou");
        Add("猎聘", "work", "猎聘", "liepin");
        Add("智联招聘", "work", "智联招聘", "zhaopin");
        Add("前程无忧", "work", "前程无忧", "51job");
        Add("LinkedIn", "work", "linkedin", "领英");
        Add("电子税务局", "work", "电子税务局", "etax", "税务局", "发票查验", "开票");
        Add("企业财务/报销", "work", "报销", "财务系统", "费控", "用友", "金蝶", "sap");
        Add("银行企业网银", "work", "企业网银", "网银", "对公");
        Add("政府/政务平台", "work", "政务", "一网通办", "国家企业信用", "信用中国");

        // ============================================================ 工作 · 搜索（你明确要求算工作）

        Add("Google", "work", "- google", "google 搜索", "google.com");
        Add("Bing", "work", "- bing", "必应", "bing.com");
        Add("百度", "work", "百度一下", "百度搜索", "- 百度");
        Add("搜狗搜索", "work", "搜狗搜索");
        Add("360 搜索", "work", "360搜索", "so.com");
        Add("DuckDuckGo", "work", "duckduckgo");
        Add("Yandex", "work", "yandex");
        Add("Ecosia", "work", "ecosia");

        return file;
    }
}
