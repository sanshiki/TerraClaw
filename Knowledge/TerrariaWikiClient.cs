#nullable enable

using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
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

    static TerrariaWikiClient()
    {
        EnsureEncodingProvidersRegistered();
    }

    private readonly WikiSourceConfig _source;
    private readonly WikiExtractionProfile _profile;

    public TerrariaWikiClient(WikiSourceConfig source)
    {
        _source = source;
        _profile = WikiExtractionProfile.For(source);
    }

    public async Task<IReadOnlyList<KnowledgeSearchResult>> QueryAsync(
        string query,
        int limit,
        int extractChars,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SearchHit> searchHits = await SearchAsync(query, limit, cancellationToken);
        IReadOnlyList<SearchHit> hits = MergeHits(InferTitleCandidates(query, _profile), searchHits, limit);
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

    private static IReadOnlyList<string> InferTitleCandidates(string query, WikiExtractionProfile profile)
    {
        string normalizedQuery = CleanQuery(query);
        if (ContainsCjk(normalizedQuery))
        {
            string stripped = normalizedQuery;
            foreach (string intent in profile.IntentWords.OrderByDescending(word => word.Length))
                stripped = stripped.Replace(intent, "", StringComparison.OrdinalIgnoreCase).Trim();
            stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(stripped))
                return stripped.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    ? new[] { stripped }
                    : new[] { stripped, normalizedQuery };
        }

        string[] words = QueryTerms(normalizedQuery, profile).ToArray();
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

    private string ExtractContext(IDocument document, string query, int maxLength)
    {
        string queryLower = query.ToLowerInvariant();
        if (ContainsAny(queryLower, _profile.RecipeIntentWords))
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

    private IReadOnlyList<string> ExtractRecipeTables(IDocument document)
    {
        var rows = new List<string>();
        foreach (string selector in _profile.RecipeTableSelectors)
        {
            foreach (IElement table in document.QuerySelectorAll(selector))
            {
                string[] headers = table.QuerySelectorAll("th")
                    .Select(header => CleanText(header.TextContent).ToLowerInvariant())
                    .ToArray();
                if (!headers.Any(header => ContainsAny(header, _profile.IngredientHeaderWords)))
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

    private IReadOnlyList<IElement> FindRelevantSection(IDocument document, string query)
    {
        string queryLower = query.ToLowerInvariant();
        var candidates = new List<string>();
        if (ContainsAny(queryLower, _profile.DropIntentWords))
            candidates.AddRange(_profile.DropSectionWords);
        if (ContainsAny(queryLower, _profile.RecipeIntentWords))
            candidates.AddRange(_profile.RecipeSectionWords);
        candidates.AddRange(QueryTerms(query, _profile));

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
        string text = string.Equals(_source.Transport, "curl", StringComparison.OrdinalIgnoreCase)
            ? await GetTextWithCurlAsync(url, cancellationToken)
            : await GetTextWithHttpClientAsync(url, cancellationToken);
        return JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException("MediaWiki API returned non-object JSON.");
    }

    private static async Task<string> GetTextWithHttpClientAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task<string> GetTextWithCurlAsync(string url, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "curl.exe" : "curl",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        process.StartInfo.ArgumentList.Add("-sS");
        process.StartInfo.ArgumentList.Add("-L");
        process.StartInfo.ArgumentList.Add("--fail");
        process.StartInfo.ArgumentList.Add("--max-time");
        process.StartInfo.ArgumentList.Add("20");
        process.StartInfo.ArgumentList.Add("-A");
        process.StartInfo.ArgumentList.Add("TerraClaw/0.1");
        process.StartInfo.ArgumentList.Add("-H");
        process.StartInfo.ArgumentList.Add("Accept: application/json,text/plain,*/*");
        process.StartInfo.ArgumentList.Add(url);

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start curl.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("curl transport requires curl on PATH.", ex);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            KillCurlProcess(process);
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new HttpRequestException($"curl exited with code {process.ExitCode}: {stderr.Trim()}");
        if (string.IsNullOrWhiteSpace(stdout))
            throw new HttpRequestException("curl returned an empty response.");

        return stdout;
    }

    private static void KillCurlProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private string BuildUrl(IReadOnlyDictionary<string, string> parameters)
    {
        string separator = _source.Api.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        string query = string.Join("&", parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        return _source.Api + separator + query;
    }

    private static IEnumerable<string> QueryTerms(string query, WikiExtractionProfile profile)
    {
        return Regex.Matches(query, @"[\u3400-\u9FFF]+|[A-Za-z0-9']+")
            .Cast<Match>()
            .Select(match => match.Value.Trim())
            .Where(word => word.Length > 0)
            .Where(word => !profile.IntentWords.Contains(word));
    }

    private static bool ContainsAny(string text, IEnumerable<string> words)
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

    private string BuildPageUrl(string title)
    {
        return _source.PageBase + Uri.EscapeDataString(title.Replace(' ', '_'));
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

    private static string CleanQuery(string query)
    {
        return Regex.Replace(query ?? "", @"\s+", " ").Trim();
    }

    private static bool ContainsCjk(string text)
    {
        return text.Any(ch => ch >= '\u3400' && ch <= '\u9FFF');
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

    private sealed record WikiExtractionProfile(
        HashSet<string> IntentWords,
        string[] RecipeIntentWords,
        string[] DropIntentWords,
        string[] RecipeSectionWords,
        string[] DropSectionWords,
        string[] IngredientHeaderWords,
        string[] RecipeTableSelectors)
    {
        public static WikiExtractionProfile For(WikiSourceConfig source)
        {
            bool zh = source.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || source.Profile.Contains("zh", StringComparison.OrdinalIgnoreCase);

            string[] commonIntent = zh
                ? new[] { "怎么", "如何", "什么", "wiki", "terraria", "泰拉瑞亚", "灾厄", "模组" }
                : new[] { "how", "to", "do", "i", "terraria", "wiki", "guide", "for", "of", "the", "mod", "calamity" };
            string[] recipeIntent = zh
                ? new[] { "配方", "合成", "制作", "制作表", "recipe", "recipes", "craft", "crafting", "make" }
                : new[] { "recipe", "recipes", "crafting", "craft", "crafted", "make", "making" };
            string[] dropIntent = zh
                ? new[] { "掉落", "掉落物", "掉率", "获取", "获得", "来源", "drop", "drops", "obtain" }
                : new[] { "drop", "drops", "dropped", "obtain", "obtained", "get", "getting", "loot" };
            string[] recipeSections = zh
                ? new[] { "制作", "合成", "配方", "用于", "用于制作", "crafting", "recipes", "used in" }
                : new[] { "crafting", "recipes", "used in", "recipe" };
            string[] dropSections = zh
                ? new[] { "掉落", "掉落物", "掉率", "来源", "获取", "drops", "drop rates", "loot" }
                : new[] { "drops", "drop rates", "loot", "obtaining" };
            string[] ingredientHeaders = zh
                ? new[] { "材料", "素材", "成分", "ingredient", "material" }
                : new[] { "ingredient", "ingredients", "material", "materials" };

            var intentWords = new HashSet<string>(commonIntent.Concat(recipeIntent).Concat(dropIntent), StringComparer.OrdinalIgnoreCase);
            return new WikiExtractionProfile(
                intentWords,
                recipeIntent,
                dropIntent,
                recipeSections,
                dropSections,
                ingredientHeaders,
                new[] { "table.recipes", "table.wikitable.recipes", "table.crafting", "table.wikitable" });
        }
    }

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