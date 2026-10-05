using WorkTimer.Core.Classification;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.Core.Config;

/// <summary>config.json 的读写。</summary>
public sealed class ConfigStore
{
    private readonly JsonFileStore<AppConfig> _store;

    public ConfigStore(DataLocation location)
    {
        _store = new JsonFileStore<AppConfig>(location.ConfigPath, "Config");
    }

    public string Path => _store.Path;

    public AppConfig Current { get; private set; } = new();

    public AppConfig Load()
    {
        Current = _store.Load();
        Current.Normalize();
        return Current;
    }

    public bool Save()
    {
        Current.Normalize();
        var ok = _store.TrySave(Current);
        if (ok)
        {
            Log.Info("Config", "配置已保存");
        }
        return ok;
    }
}

/// <summary>rules.user.json 的读写。用户规则与内置规则合并后由 RuleEngine 使用。</summary>
public sealed class RuleStore
{
    private readonly JsonFileStore<RuleFile> _store;

    public RuleStore(DataLocation location)
    {
        _store = new JsonFileStore<RuleFile>(location.UserRulesPath, "Config");
    }

    public string Path => _store.Path;

    public List<Rule> Rules { get; private set; } = new();

    public void Load()
    {
        var file = _store.Load();
        Rules = file.Rules ?? new List<Rule>();
        foreach (var rule in Rules)
        {
            rule.IsBuiltIn = false;
        }
        Log.Info("Config", $"加载用户规则 {Rules.Count} 条");
    }

    public bool Save()
    {
        var ok = _store.TrySave(new RuleFile { Rules = Rules });
        if (ok)
        {
            Log.Info("Config", $"已保存用户规则 {Rules.Count} 条");
        }
        return ok;
    }

    /// <summary>新增或用同 Id 覆盖一条用户规则。返回该规则。</summary>
    public Rule Upsert(Rule rule)
    {
        rule.IsBuiltIn = false;
        var index = Rules.FindIndex(r => r.Id == rule.Id);
        if (index >= 0)
        {
            Rules[index] = rule;
        }
        else
        {
            rule.Order = Rules.Count == 0 ? 10 : Rules.Max(r => r.Order) + 10;
            Rules.Add(rule);
        }

        Save();
        return rule;
    }

    public bool Remove(string id)
    {
        var removed = Rules.RemoveAll(r => r.Id == id) > 0;
        if (removed)
        {
            Save();
        }
        return removed;
    }

    public void Move(string id, int direction)
    {
        var index = Rules.FindIndex(r => r.Id == id);
        if (index < 0)
        {
            return;
        }

        var target = index + direction;
        if (target < 0 || target >= Rules.Count)
        {
            return;
        }

        (Rules[index], Rules[target]) = (Rules[target], Rules[index]);

        // 重排 Order，保证顺序稳定可持久化
        for (var i = 0; i < Rules.Count; i++)
        {
            Rules[i].Order = (i + 1) * 10;
        }

        Save();
    }

    /// <summary>清空用户规则，回到纯内置规则。</summary>
    public void ResetToBuiltIn()
    {
        Rules.Clear();
        Save();
        Log.Warn("Config", "用户规则已清空，回到内置规则");
    }
}

/// <summary>sites.json 的读写。</summary>
public sealed class SiteStore
{
    private readonly JsonFileStore<SiteFile> _store;

    public SiteStore(DataLocation location)
    {
        _store = new JsonFileStore<SiteFile>(location.SitesPath, "Config");
    }

    public string Path => _store.Path;

    public SiteFile File { get; private set; } = DefaultSites.Create();

    public void Load()
    {
        var loaded = _store.Load();

        // 第一次运行会生成一个只含兜底字段的空文件 —— 补上内置站点表，否则浏览器判定会全落到兜底。
        if (loaded.Sites is null || loaded.Sites.Count == 0)
        {
            Log.Info("Config", "sites.json 为空，写入内置站点表");
            File = DefaultSites.Create();
            Save();
            return;
        }

        foreach (var site in loaded.Sites)
        {
            site.IsBuiltIn = false;
        }

        File = loaded;
        Log.Info("Config", $"加载站点表 {File.Sites.Count} 个");
    }

    public bool Save()
    {
        var ok = _store.TrySave(File);
        if (ok)
        {
            Log.Info("Config", $"已保存站点表 {File.Sites.Count} 个");
        }
        return ok;
    }

    public void AddOrUpdate(SiteDefinition site)
    {
        site.IsBuiltIn = false;
        var index = File.Sites.FindIndex(s => string.Equals(s.Platform, site.Platform, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            File.Sites[index] = site;
        }
        else
        {
            File.Sites.Add(site);
        }

        Save();
    }

    public void Remove(string platform)
    {
        if (File.Sites.RemoveAll(s => string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    public void ResetToBuiltIn()
    {
        File = DefaultSites.Create();
        Save();
        Log.Warn("Config", "站点表已恢复内置默认值");
    }
}
