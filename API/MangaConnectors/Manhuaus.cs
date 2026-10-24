using System.Text.RegularExpressions;
using System.Web;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using HtmlAgilityPack;

namespace API.MangaConnectors;

/// <summary>
/// manhuaus.com, a WordPress Madara site. Every request (images included) is behind a Cloudflare challenge,
/// so this needs FLARESOLVERR_URL (see <see cref="FlareSolverrClearanceDownloadClient"/>).
/// IdOnConnectorSite is the series slug for manga, "slug/chapter-slug" for chapters.
/// </summary>
public sealed class Manhuaus : MangaConnector
{
    private const string BaseUrl = "https://manhuaus.com";
    private const int SearchPages = 2; // 12 results per page

    public Manhuaus() : base("Manhuaus", ["en"], ["manhuaus.com"],
        "https://manhuaus.com/wp-content/uploads/2020/09/cropped-LOGO1-270x270.png")
    {
        downloadClient = new FlareSolverrClearanceDownloadClient();
    }

    private static readonly Regex SeriesUrlRex = new(@"https?://(?:www\.)?manhuaus\.com/manga/(?<slug>[^/?#]+)", RegexOptions.IgnoreCase);
    private static readonly Regex ChapterTextRex = new(@"chapter\s*(?<number>\d+(?:\.\d+)?)(?:\s*[-:–]\s*(?<title>.+))?", RegexOptions.IgnoreCase);
    private static readonly Regex ChapterSlugRex = new(@"/chapter-(?<number>\d+)(?:-(?<decimal>\d+))?/?$", RegexOptions.IgnoreCase);

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching: {0}", mangaSearchName);
        List<string> urls = [];
        for (int page = 1; page <= SearchPages; page++)
        {
            string pagePath = page == 1 ? "/" : $"/page/{page}/";
            if (LoadDocument($"{BaseUrl}{pagePath}?s={HttpUtility.UrlEncode(mangaSearchName)}&post_type=wp-manga") is not { } document)
                break;
            if (document.DocumentNode.SelectNodes("//div[contains(@class,'c-tabs-item__content')]//div[contains(@class,'post-title')]//a[@href]") is not { Count: > 0 } resultNodes)
                break;
            urls.AddRange(resultNodes.Select(n => n.GetAttributeValue("href", "")));
            if (resultNodes.Count < 12)
                break;
        }

        (Manga, MangaConnectorId<Manga>)[] ret = urls.Distinct()
            .Select(GetMangaFromUrl)
            .Where(m => m is not null)
            .Select(m => m!.Value)
            .DistinctBy(m => m.Item1.Key)
            .ToArray();
        Log.InfoFormat("Search '{0}' yielded {1} results.", mangaSearchName, ret.Length);
        return ret;
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        if (SeriesUrlRex.Match(url) is not { Success: true } match)
            return null;
        return GetMangaFromId(match.Groups["slug"].Value);
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        if (LoadDocument(SeriesUrl(mangaIdOnSite)) is not { } document)
            return null;
        HtmlNode root = document.DocumentNode;

        // Renamed series redirect to their new slug, which the canonical link has.
        string canonical = root.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", "") ?? "";
        string slug = SeriesUrlRex.Match(canonical) is { Success: true } canonicalMatch
            ? canonicalMatch.Groups["slug"].Value
            : mangaIdOnSite;

        string? title = Text(root.SelectSingleNode("//div[contains(@class,'post-title')]/h1"));
        if (string.IsNullOrEmpty(title))
        {
            Log.ErrorFormat("No title found for {0}", slug);
            return null;
        }

        string coverUrl = root.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null)
            ?? ImageSource(root.SelectSingleNode("//div[contains(@class,'summary_image')]//img"))
            ?? "";

        string description = string.Join("\n",
            root.SelectNodes("//div[contains(@class,'summary__content')]//p")?
                .Select(Text)
                .Where(p => !string.IsNullOrEmpty(p)) ?? []);

        List<Author> authors = (root.SelectNodes("//div[contains(@class,'author-content')]/a | //div[contains(@class,'artist-content')]/a") ?? Enumerable.Empty<HtmlNode>())
            .Select(Text)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .Select(a => new Author(a!))
            .ToList();

        List<MangaTag> tags = (root.SelectNodes("//div[contains(@class,'genres-content')]/a | //div[contains(@class,'tags-content')]/a") ?? Enumerable.Empty<HtmlNode>())
            .Select(Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .Select(t => new MangaTag(t!))
            .ToList();

        List<AltTitle> altTitles = (SummaryContent(root, "Alternative") ?? "")
            .Split([',', '/', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length <= 256)
            .Distinct()
            .Select(t => new AltTitle("", t))
            .ToList();

        MangaReleaseStatus releaseStatus = SummaryContent(root, "Status")?.ToLowerInvariant().Replace(" ", "") switch
        {
            "ongoing" => MangaReleaseStatus.Continuing,
            "completed" => MangaReleaseStatus.Completed,
            "onhold" or "hiatus" => MangaReleaseStatus.OnHiatus,
            "canceled" or "cancelled" or "dropped" => MangaReleaseStatus.Cancelled,
            _ => MangaReleaseStatus.Unreleased
        };

        uint? year = uint.TryParse(SummaryContent(root, "Release"), out uint parsedYear)
            ? parsedYear
            : null;

        Manga manga = new(title, description, coverUrl, releaseStatus, authors, tags, [], altTitles, year: year);
        MangaConnectorId<Manga> mcId = new(manga, this, slug, SeriesUrl(slug));
        manga.MangaConnectorIds.Add(mcId);
        return (manga, mcId);
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId, string? language = null)
    {
        Log.InfoFormat("Getting chapters for {0}", mangaId);
        if (LoadDocument(SeriesUrl(mangaId.IdOnConnectorSite)) is not { } document)
            return [];
        if (document.DocumentNode.SelectNodes("//li[contains(@class,'wp-manga-chapter')]/a[@href]") is not { Count: > 0 } chapterNodes)
        {
            Log.WarnFormat("No chapters found for {0}", mangaId);
            return [];
        }

        List<(Chapter, MangaConnectorId<Chapter>)> chapters = [];
        foreach (HtmlNode node in chapterNodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
            string text = Text(node) ?? "";

            string chapterNumber;
            string? title = null;
            if (ChapterTextRex.Match(text) is { Success: true } textMatch)
            {
                chapterNumber = textMatch.Groups["number"].Value;
                title = textMatch.Groups["title"].Success ? textMatch.Groups["title"].Value.Trim() : null;
            }
            else if (ChapterSlugRex.Match(href) is { Success: true } slugMatch)
                chapterNumber = slugMatch.Groups["decimal"].Success
                    ? $"{slugMatch.Groups["number"].Value}.{slugMatch.Groups["decimal"].Value}"
                    : slugMatch.Groups["number"].Value;
            else
            {
                Log.WarnFormat("Unknown chapter format ignored: {0} {1}", text, href);
                continue;
            }

            string chapterSlug = href.TrimEnd('/').Split('/').Last();
            Chapter chapter = new(mangaId.Obj, chapterNumber, null, title);
            MangaConnectorId<Chapter> chId = new(chapter, this, $"{mangaId.IdOnConnectorSite}/{chapterSlug}", href);
            chapter.MangaConnectorIds.Add(chId);
            chapters.Add((chapter, chId));
        }

        Log.InfoFormat("Found {0} chapters for {1}", chapters.Count, mangaId);
        return chapters.DistinctBy(c => c.Item1.Key)
            .OrderBy(c => c.Item1, new Chapter.ChapterComparer())
            .ToArray();
    }

    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
        string url = chapterId.WebsiteUrl ?? $"{BaseUrl}/manga/{chapterId.IdOnConnectorSite}/";
        if (LoadDocument(url) is not { } document)
            return [];

        // Only the chapter's own pages; the reader also contains ad images.
        string[] imageUrls = (document.DocumentNode.SelectNodes("//div[contains(@class,'reading-content')]//img[contains(@class,'wp-manga-chapter-img')]") ?? Enumerable.Empty<HtmlNode>())
            .Select(ImageSource)
            .Where(u => u is not null)
            .Select(u => u!)
            .ToArray();
        Log.InfoFormat("Found {0} images for {1}", imageUrls.Length, chapterId);
        return imageUrls;
    }

    private static string SeriesUrl(string slug) => $"{BaseUrl}/manga/{slug}/";

    private HtmlDocument? LoadDocument(string url)
    {
        HttpResponseMessage response = downloadClient.MakeRequest(url, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.ErrorFormat("Request {0} failed: {1}", url, response.StatusCode);
            return null;
        }
        HtmlDocument document = new();
        document.LoadHtml(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return document;
    }

    private static string? Text(HtmlNode? node) =>
        node is null ? null : Regex.Replace(HtmlEntity.DeEntitize(node.InnerText), @"\s+", " ").Trim();

    /// <summary>
    /// Value next to a "summary-heading" such as "Status" or "Alternative" on a series page.
    /// </summary>
    private static string? SummaryContent(HtmlNode root, string heading) =>
        Text(root.SelectSingleNode($"//div[contains(@class,'post-content_item')][.//div[contains(@class,'summary-heading')][starts-with(normalize-space(.),'{heading}')]]//div[contains(@class,'summary-content')]"));

    /// <summary>
    /// Lazy-loaded images keep the real URL in data-src, with whitespace around it.
    /// </summary>
    private static string? ImageSource(HtmlNode? img)
    {
        if (img is null)
            return null;
        string src = img.GetAttributeValue("data-src", "").Trim();
        if (src == string.Empty)
            src = img.GetAttributeValue("src", "").Trim();
        return src.StartsWith("http") ? src : null;
    }
}
