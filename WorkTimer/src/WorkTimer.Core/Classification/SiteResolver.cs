namespace WorkTimer.Core.Classification;

/// <summary>站点识别命中结果。</summary>
public sealed record SiteMatch(string Platform, Category Category, string MatchedPattern, string? Note);

/// <summary>
/// 浏览器站点识别。
/// 窗口标题拿不到 URL，但主流站点的标题都带可识别特征（"_哔哩哔哩"、"GitHub" 等），
/// 用 sites.json 的关键词表把标题翻译成"平台名 + 类别"。
/// 一次匹配同时产出展示名和分类，明细表因此可以写"Microsoft Edge · 哔哩哔哩 15 分钟"，
/// 而不必存裸标题（隐私上也更干净）。
/// </summary>
public sealed class SiteResolver
{
    private SiteFile _file = DefaultSites.Create();

    public SiteFile File => _file;

    public void Update(SiteFile file)
    {
        _file = file ?? DefaultSites.Create();
    }

    public SiteMatch? Resolve(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        foreach (var site in _file.Sites)
        {
            foreach (var pattern in site.Patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                if (title.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return new SiteMatch(site.Platform, CategoryExtensions.Parse(site.Category), pattern, site.Note);
                }
            }
        }

        return null;
    }

    public string FallbackPlatform => string.IsNullOrWhiteSpace(_file.FallbackPlatform)
        ? DefaultSites.FallbackPlatform
        : _file.FallbackPlatform;
}
