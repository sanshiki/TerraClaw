#nullable enable

using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TerraClaw.Knowledge;

internal sealed class TerrariaWikiClient
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static int _encodingProvidersRegistered;
    private static readonly HashSet<string> IntentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "recipe", "recipes", "crafting", "craft", "crafted", "make", "making",
        "drop", "drops", "dropped", "obtain", "obtained", "get", "getting",
        "how", "to", "do", "i", "terraria", "wiki", "guide", "for", "of", "the",
    };

    static TerrariaWikiClient()
    {
        EnsureEncodingProvidersRegistered();
    }

    private readonly string _apiEndpoint;

    public TerrariaWikiClient(string apiEndpoint)
    {
        _apiEndpoint = string.IsNullOrWhiteSpace(apiEndpoint)
            ? "https://terraria.wiki.gg/api.php"
            : apiEndpoint.Trim();
    }

    public async Task<IReadOnlyList<KnowledgeSearchResult>> QueryAsync(
        string query,
        int limit,
        int extractChars,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SearchHit> searchHits = await SearchAsync(query, limit, cancellationToken);
        IReadOnlyList<SearchHit> hits = MergeHits(InferTitleCandidates(query), searchHits, limit);
        if (hits.Count == 0)
            return Array.Empty<KnowledgeSearchResult>();

        Dictionary<string, PageSummary> pages = await GetPageSummariesAsync(
            hits.Select(hit => hit.Title),
            query,
            extractChars,
            cancellationToken);

        var results = new List<KnowledgeSearchResult>();
        foreach (SearchHit hit in hits)
        {
            pages.TryGetValue(hit.Title, out PageSummary? page);
            if (page == null)
                continue;

            string snippet = Truncate(CleanText(hit.Snippet), Math.Min(extractChars, 320));
            if (string.IsNullOrWhiteSpace(page.Extract) && string.IsNullOrWhiteSpace(snippet))
                continue;

            results.Add(new KnowledgeSearchResult(page.Title, page.Url, page.Extract, snippet));
        }

        return results;
    }

    private async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        string url = BuildUrl(new Dictionary<string, string>
        {
            ["action"] = "query",
            ["list"] = "search",
            ["srnamespace"] = "0",
            ["srsearch"] = query,
            ["srlimit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["format"] = "json",
            ["formatversion"] = "2",
        });

        JsonObject root = await GetJsonAsync(url, cancellationToken);
        if (root["query"] is not JsonObject queryNode || queryNode["search"] is not JsonArray search)
            return Array.Empty<SearchHit>();

        var hits = new List<SearchHit>();
        foreach (JsonNode? node in search)
        {
            if (node is not JsonObject item)
                continue;

            string title = item["title"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(title))
                continue;

            string snippet = item["snippet"]?.GetValue<string>() ?? "";
            hits.Add(new SearchHit(title, snippet));
        }

        return hits;
    }

    private static IReadOnlyList<SearchHit> MergeHits(IReadOnlyList<string> candidates, IReadOnlyList<SearchHit> searchHits, int limit)
    {
        var result = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in candidates)
        {
            string key = NormalizeTitle(candidate);
            if (key.Length > 0 && seen.Add(key))
                result.Add(new SearchHit(candidate, "direct page candidate from query"));
        }

        foreach (SearchHit hit in searchHits)
        {
            string key = NormalizeTitle(hit.Title);
            if (key.Length > 0 && seen.Add(key))
                result.Add(hit);
        }

        return result.Take(limit).ToList();
    }

    private static IReadOnlyList<string> InferTitleCandidates(string query)
    {
        string[] words = Regex.Matches(query, @"[A-Za-z0-9']+")
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => !IntentWords.Contains(word))
            .ToArray();
        string candidate = string.Join(" ", words).Trim();
        return string.IsNullOrWhiteSpace(candidate) ? Array.Empty<string>() : new[] { candidate };
    }

    private async Task<Dictionary<string, PageSummary>> GetPageSummariesAsync(
        IEnumerable<string> titles,
        string query,
        int extractChars,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PageSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (string title in titles.Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            PageSummary? page = await ParsePageAsync(title, query, extractChars, cancellationToken);
            if (page == null)
                continue;

            result[title] = page;
            result[page.Title] = page;
        }

        return result;
    }

    private async Task<PageSummary?> ParsePageAsync(
        string title,
        string query,
        int extractChars,
        CancellationToken cancellationToken)
    {
        string url = BuildUrl(new Dictionary<string, string>
        {
            ["action"] = "parse",
            ["page"] = title,
            ["prop"] = "text|displaytitle",
            ["redirects"] = "1",
            ["format"] = "json",
            ["formatversion"] = "2",
        });

        JsonObject root = await GetJsonAsync(url, cancellationToken);
        if (root["error"] != null || root["parse"] is not JsonObject parsed)
            return null;

        string resolvedTitle = parsed["title"]?.GetValue<string>() ?? title;
        string html = parsed["text"]?.GetValue<string>() ?? "";
        IDocument document = ParseHtml(html);
        CleanupDocument(document);
        string extract = ExtractContext(document, query, extractChars);
        return new PageSummary(resolvedTitle, BuildPageUrl(resolvedTitle), extract);
    }

    private static void CleanupDocument(IDocument document)
    {
        foreach (string selector in new[]
        {
            "script", "style", "noscript", "img", "sup.reference", ".reference", ".mw-editsection",
            ".toc", "#toc", ".metadata", ".noprint", ".navbox", ".catlinks", ".printfooter", ".eico",
        })
        {
            foreach (IElement element in document.QuerySelectorAll(selector).ToArray())
                element.Remove();
        }
    }

    private static string ExtractContext(IDocument document, string query, int maxLength)
    {
        string queryLower = query.ToLowerInvariant();
        if (ContainsAny(queryLower, "recipe", "recipes", "craft", "crafting", "make"))
        {
            IReadOnlyList<string> recipes = ExtractRecipeTables(document);
            if (recipes.Count > 0)
                return Truncate("Recipes: " + string.Join(" ; ", recipes), maxLength);
        }

        IReadOnlyList<IElement> section = FindRelevantSection(document, query);
        if (section.Count > 0)
            return Truncate(CleanText(string.Join(" ", section.Select(element => element.TextContent))), maxLength);

        return Truncate(CleanText(document.Body?.TextContent ?? document.TextContent), maxLength);
    }

    private static IReadOnlyList<string> ExtractRecipeTables(IDocument document)
    {
        var rows = new List<string>();
        foreach (IElement table in document.QuerySelectorAll("table.recipes"))
        {
            string[] headers = table.QuerySelectorAll("th")
                .Select(header => CleanText(header.TextContent).ToLowerInvariant())
                .ToArray();
            if (!headers.Any(header => header.Contains("ingredient", StringComparison.OrdinalIgnoreCase)))
                continue;

            foreach (IElement row in table.QuerySelectorAll("tr"))
            {
                IElement[] cells = row.Children
                    .Where(child => string.Equals(child.TagName, "TD", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (cells.Length < 2)
                    continue;

                string result = CleanCell(cells[0]);
                string ingredients = CleanIngredientCell(cells[1]);
                string station = cells.Length > 2 ? CleanCell(cells[2]) : "";
                if (string.IsNullOrWhiteSpace(result) || string.IsNullOrWhiteSpace(ingredients))
                    continue;

                var parts = new List<string> { $"Result: {result}", $"Ingredients: {ingredients}" };
                if (!string.IsNullOrWhiteSpace(station))
                    parts.Add($"Station: {station}");
                rows.Add(string.Join("; ", parts));
            }
        }

        return Dedupe(rows);
    }

    private static string CleanIngredientCell(IElement cell)
    {
        string[] items = cell.QuerySelectorAll("li")
            .Select(CleanCell)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return items.Length > 0 ? string.Join(", ", Dedupe(items)) : CleanCell(cell);
    }

    private static string CleanCell(IElement element)
    {
        string text = CleanText(element.TextContent);
        text = Regex.Replace(text, @"\((Desktop|Console|Mobile|Old-gen console|3DS)[^)]+versions?\)", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^only:\s*", "", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"\s+", " ").Trim(' ', ',', ';');
    }

    private static IReadOnlyList<IElement> FindRelevantSection(IDocument document, string query)
    {
        string queryLower = query.ToLowerInvariant();
        var candidates = new List<string>();
        if (ContainsAny(queryLower, "drop", "drops", "dropped"))
            candidates.AddRange(new[] { "drops", "drop rates", "loot" });
        if (ContainsAny(queryLower, "recipe", "recipes", "craft", "crafting", "make"))
            candidates.AddRange(new[] { "crafting", "recipes", "used in" });
        candidates.AddRange(QueryTerms(query));

        foreach (IElement heading in document.QuerySelectorAll("h1,h2,h3,h4,h5,h6"))
        {
            string headingText = CleanText(heading.TextContent).ToLowerInvariant();
            if (!candidates.Any(candidate => headingText.Contains(candidate.ToLowerInvariant(), StringComparison.Ordinal)))
                continue;
            return CollectSectionNodes(heading);
        }

        return Array.Empty<IElement>();
    }

    private static IReadOnlyList<IElement> CollectSectionNodes(IElement heading)
    {
        var result = new List<IElement>();
        int level = HeadingLevel(heading);
        for (IElement? sibling = heading.NextElementSibling; sibling != null; sibling = sibling.NextElementSibling)
        {
            if (IsHeading(sibling) && HeadingLevel(sibling) <= level)
                break;
            result.Add(sibling);
        }

        return result;
    }

    private async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException("MediaWiki API returned non-object JSON.");
    }

    private string BuildUrl(IReadOnlyDictionary<string, string> parameters)
    {
        string separator = _apiEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        string query = string.Join("&", parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        return _apiEndpoint + separator + query;
    }

    private static IEnumerable<string> QueryTerms(string query)
    {
        return Regex.Matches(query, @"[A-Za-z0-9']+")
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => !IntentWords.Contains(word));
    }

    private static bool ContainsAny(string text, params string[] words)
    {
        return words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHeading(IElement element)
    {
        return element.TagName.Length == 2
            && element.TagName[0] == 'H'
            && char.IsDigit(element.TagName[1]);
    }

    private static int HeadingLevel(IElement element)
    {
        return IsHeading(element) ? element.TagName[1] - '0' : 6;
    }

    private static string BuildPageUrl(string title)
    {
        return "https://terraria.wiki.gg/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));
    }

    private static IDocument ParseHtml(string html)
    {
        EnsureEncodingProvidersRegistered();
        return new HtmlParser().ParseDocument(html);
    }

    private static void EnsureEncodingProvidersRegistered()
    {
        if (Interlocked.Exchange(ref _encodingProvidersRegistered, 1) == 1)
            return;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding.RegisterProvider(TerraClawEncodingProvider.Instance);
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        string withoutTags = text.Contains('<', StringComparison.Ordinal)
            ? ParseHtml(text).Body?.TextContent ?? text
            : text;
        string decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        if (maxLength <= 0 || text.Length <= maxLength)
            return text;
        return text[..maxLength].TrimEnd();
    }

    private static string NormalizeTitle(string title)
    {
        return Regex.Replace(title.Replace('_', ' ').Trim(), @"\s+", " ").ToLowerInvariant();
    }

    private static IReadOnlyList<string> Dedupe(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            string key = NormalizeTitle(value);
            if (key.Length > 0 && seen.Add(key))
                result.Add(value);
        }
        return result;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TerraClaw/0.1");
        return client;
    }

    private sealed record SearchHit(string Title, string Snippet);

    private sealed record PageSummary(string Title, string Url, string Extract);

    private sealed class TerraClawEncodingProvider : EncodingProvider
    {
        public static readonly TerraClawEncodingProvider Instance = new();

        private TerraClawEncodingProvider()
        {
        }

        public override Encoding? GetEncoding(string name)
        {
            return string.Equals(name, "iso-2022-cn", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8
                : null;
        }

        public override Encoding? GetEncoding(int codepage)
        {
            return null;
        }

        public override Encoding? GetEncoding(string name, EncoderFallback encoderFallback, DecoderFallback decoderFallback)
        {
            Encoding? encoding = GetEncoding(name);
            if (encoding == null)
                return null;

            Encoding clone = (Encoding)encoding.Clone();
            clone.EncoderFallback = encoderFallback;
            clone.DecoderFallback = decoderFallback;
            return clone;
        }
    }
}