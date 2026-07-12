using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Malx_AI
{
    internal sealed class WebSearchService
    {
        private static readonly int MaxResultCount = 6;
        private static readonly int MaxResultsPerQuery = 10;
        private static readonly int MaxEvidenceFetchCount = 5;
        private const int MaxSearchPayloadChars = 450_000;
        private const int MaxEvidencePayloadChars = 650_000;
        private const string CacheVersionPrefix = "v2::";
        private static readonly string CacheGenerationPrefix = "v6::";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan StableInfoCacheTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CurrentInfoCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SearchDeadline = TimeSpan.FromSeconds(18);
        private static readonly TimeSpan CurrentSearchDeadline = TimeSpan.FromSeconds(22);
        private static readonly TimeSpan ProviderDeadline = TimeSpan.FromSeconds(7);
        private static readonly TimeSpan EvidenceDeadline = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan CurrentEvidenceDeadline = TimeSpan.FromSeconds(9);
        private static readonly Regex TokenRegex = new(@"[A-Za-z0-9][A-Za-z0-9\.\+#_-]*", RegexOptions.Compiled);
        private static readonly Regex QuotedPhraseRegex = new("\"(?<value>[^\"]{3,80})\"|'(?<value>[^']{3,80})'", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly ConcurrentDictionary<string, (string Data, DateTimeOffset SavedAt)> SearchCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","and","are","as","at","be","but","by","can","could","do","does","for","from","had","has","have","help","how",
            "i","if","in","into","is","it","its","latest","look","me","more","news","of","on","online","or","please","recent","research",
            "search","show","tell","than","that","the","their","them","there","these","this","to","up","using","want","was","web","what",
            "when","where","which","who","why","will","with","would","you","your","about","information","info","details","update","updates",
            "expand","expanded","expanding","elaborate","elaborated","elaborating"
        };
        private static readonly HashSet<string> TopicShiftStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","with","that","this","from","into","about","have","has","are",
            "was","were","will","just","what","how","can","does","please","make","create",
            "write","give","need","want","should","could","would","also","like","using","you",
            "your","they","them","their","there","here","then","than","explain","describe",
            "definition","meaning","mean","means","effect","effects","affect","affects",
            "latest","current","recent","new","newest","information","info","details","update","updates","news","article","articles",
            "expand","expanded","expanding","elaborate","elaborated","elaborating"
        };
        private static readonly HashSet<string> QuestionFillerWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "what","which","who","why","where","when","how","does","do","did","is","are","was","were",
            "can","could","would","should","will","to","for","of","the","a","an","please","tell","me",
            "explain","describe","define","expand","elaborate","meaning","mean","means","exactly","actually","basically",
            "generally","general","main","focus","prompt","question","about","on","regarding",
            "latest","current","recent","new","newest","information","info","details","update","updates","news","article","articles"
        };
        private static readonly HashSet<string> OverlapStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","and","are","as","at","be","but","by","can","could","do","does","for","from","had","has","have","help","how",
            "i","if","in","into","is","it","its","look","me","more","of","on","online","or","please","research",
            "search","show","tell","than","that","the","their","them","there","these","this","to","up","using","want","was","web","what",
            "when","where","which","who","why","will","with","would","you","your","about","information","info","details","update","updates",
            "expand","expanded","expanding","elaborate","elaborated","elaborating"
        };
        private static readonly Regex FreshnessYearRegex = new(@"\b(?<year>20[2-9][0-9])\b", RegexOptions.Compiled);
        private static readonly Regex IsoDateRegex = new(@"\b(?<year>20\d{2})-(?<month>0[1-9]|1[0-2])-(?<day>0[1-9]|[12]\d|3[01])\b", RegexOptions.Compiled);
        private static readonly Regex MonthDateRegex = new(@"\b(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?<day>\d{1,2})(?:,\s*(?<year>20\d{2}))?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> QueryQualifierWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "reliable", "trustworthy", "reputable", "credible", "accurate", "official", "good", "best"
        };
        private static readonly HashSet<string> GenericBroadNewsTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            "latest", "news", "today", "headline", "headlines", "breaking", "current", "recent", "top",
            "stories", "story", "events", "update", "updates", "world", "global", "live", "reported", "report"
        };
        private static readonly string[] TrustedSourceHosts =
        [
            "microsoft.com", "learn.microsoft.com", "docs.microsoft.com", "github.com", "docs.github.com",
            "developer.mozilla.org", "mozilla.org", "python.org", "docs.python.org", "w3.org", "ietf.org",
            "wikipedia.org", "stackexchange.com", "nuget.org", "npmjs.com", "openai.com", "anthropic.com",
            "reuters.com", "apnews.com", "bbc.com", "bbc.co.uk", "arstechnica.com"
        ];
        private static readonly string[] PreferredBroadNewsHosts =
        [
            "reuters.com", "apnews.com", "bbc.com", "bbc.co.uk", "npr.org", "abcnews.go.com",
            "cbsnews.com", "nbcnews.com", "cnn.com", "wsj.com", "nytimes.com", "usatoday.com"
        ];
        private static readonly string[] DeprioritizedBroadNewsHosts =
        [
            "ndtv.com", "timesofindia.indiatimes.com", "hindustantimes.com", "indianexpress.com",
            "firstpost.com", "livemint.com", "thehindu.com", "news18.com", "moneycontrol.com"
        ];
        private static readonly string[] LowConfidenceHosts =
        [
            "quora.com", "pinterest.com", "facebook.com", "instagram.com", "tiktok.com", "reddit.com"
        ];
        private static readonly string[] BlockedSourceHosts =
        [
            "youtube.com", "youtu.be", "linkedin.com", "medium.com", "forums.tomshardware.com"
        ];
        private static readonly string[] AggregatorSourceHosts =
        [
            "news.google.com", "news.googleusercontent.com"
        ];
        private static readonly string[] GenericPromptWordHosts =
        [
            "theinformation.com", "informationweek.com"
        ];
        private static readonly string[] DefinitionResultMarkers =
        [
            "dictionary", "definition", "thesaurus", "meaning of", "define ", "merriam-webster", "cambridge english"
        ];
        private static readonly string[] BoilerplateEvidenceMarkers =
        [
            "is your source for",
            "source for the latest",
            "source for breaking news",
            "latest breaking news, comment and features",
            "visit bbc news for",
            "ap news has the latest",
            "the latest news and headlines",
            "breaking news, live coverage",
            "watch live coverage",
            "follow us on",
            "sign up for",
            "newsletter",
            "all rights reserved",
            "cookie policy",
            "privacy policy"
        ];
        private static readonly string[] GenericNewsLandingTitleMarkers =
        [
            "breaking news",
            "latest news",
            "top stories",
            "news headlines",
            "latest headlines",
            "live updates",
            "news and videos",
            "breaking news updates"
        ];
        private static readonly string[] GenericNewsLandingSnippetMarkers =
        [
            "the latest u.s., world, weather, entertainment, politics, and health headlines",
            "latest news from",
            "includes updates on u.s., world, entertainment, health, business, technology, politics, and sports",
            "read the latest news",
            "get the latest breaking news",
            "top news stories from around the world",
            "the latest news, sport, business and weather",
            "latest headlines from around the world"
        ];
        private static readonly string[] RegionalFocusMarkers =
        [
            "india", "indian", "united states", "u.s.", "usa", "america", "american", "uk", "britain", "british",
            "europe", "european", "china", "chinese", "russia", "russian", "canada", "canadian", "australia",
            "australian", "japan", "japanese", "korea", "korean", "middle east", "africa", "latin america"
        ];

        private sealed record SearchResult(string Title, string Snippet, string Url, int Position, DateTimeOffset? PublishedAt = null);
        private sealed record SearchIntent(string Query, string BasePrompt, bool CurrentInfo, bool Docs, bool Release, bool News, IReadOnlyList<string> QueryTerms);
        private sealed record PageEvidence(string Text, string Title, DateTimeOffset? PublishedAt, bool HighSignal);

        public string BuildStrategicSearchQuery(string prompt)
        {
            return BuildSearchIntent(prompt).Query;
        }

        public string BuildFocusedNormalChatQuery(string prompt)
        {
            string normalizedPrompt = NormalizeQuery(prompt);
            if (string.IsNullOrWhiteSpace(normalizedPrompt))
                return string.Empty;

            if (TryBuildQuestionFocusedQuery(normalizedPrompt, out string focusedQuery, out _))
                return focusedQuery;

            if (normalizedPrompt.Length > 220)
                normalizedPrompt = normalizedPrompt[..220].TrimEnd();

            var contentWords = Regex.Matches(normalizedPrompt, @"\b[a-zA-Z][a-zA-Z0-9_\.-]*\b")
                .Select(m => m.Value.Trim())
                .Where(w => w.Length > 1)
                .Where(w => !TopicShiftStopWords.Contains(w))
                .ToList();

            if (contentWords.Count == 0)
                return normalizedPrompt;

            if (contentWords.Count < 8)
                return string.Join(' ', contentWords);

            var ranked = contentWords
                .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Word = g.Key,
                    Count = g.Count(),
                    FirstIndex = contentWords.FindIndex(w => string.Equals(w, g.Key, StringComparison.OrdinalIgnoreCase))
                })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.FirstIndex)
                .Take(6)
                .Select(x => x.Word)
                .ToList();

            return ranked.Count == 0 ? normalizedPrompt : string.Join(' ', ranked);
        }

        public bool RequiresFreshOrSourceBackedGrounding(string prompt)
        {
            SearchIntent intent = BuildSearchIntent(prompt);
            if (string.IsNullOrWhiteSpace(intent.BasePrompt))
                return false;

            return intent.CurrentInfo
                || intent.News
                || intent.Release
                || intent.Docs
                || LooksLikeHighStakesOrSourceBackedRequest(intent.BasePrompt);
        }

        public static bool LooksLikeLowSpecificitySearchQuery(string query)
        {
            string normalized = NormalizeQuery(query);
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            List<string> meaningfulTokens = TokenRegex.Matches(normalized)
                .Select(m => m.Value.Trim())
                .Where(token => token.Length >= 3)
                .Where(token => !StopWords.Contains(token))
                .Where(token => !TopicShiftStopWords.Contains(token))
                .Where(token => !GenericBroadNewsTerms.Contains(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return meaningfulTokens.Count < 2 && normalized.Length < 42;
        }

        public async Task<string> SearchTopSnippetsForNormalChatAsync(string originalMessage, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(originalMessage))
                return "No web results available for an empty query.";

            string refinedQuery = BuildFocusedNormalChatQuery(originalMessage);
            if (string.IsNullOrWhiteSpace(refinedQuery))
                refinedQuery = NormalizeQuery(originalMessage);

            using var searchTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            searchTimeout.CancelAfter(LooksLikeCurrentInfoQuery(originalMessage) || LooksLikeNewsQuery(originalMessage)
                ? CurrentSearchDeadline
                : SearchDeadline);
            CancellationToken searchToken = searchTimeout.Token;

            try
            {
                SearchIntent intent = BuildSearchIntent(originalMessage);
                if (string.IsNullOrWhiteSpace(intent.Query))
                    intent = BuildSearchIntent(refinedQuery);
                string normalizedQuery = intent.Query;
                if (string.IsNullOrWhiteSpace(normalizedQuery))
                    return "No web results available for an empty query.";

                string cacheKey = BuildCacheKey("normalchat::", normalizedQuery);
                if (TryGetCached(cacheKey, intent, out string cached))
                    return cached;

                List<SearchResult> fetchedResults = await FetchRankedResultsAsync(intent, searchToken).ConfigureAwait(false);
                if (fetchedResults.Count == 0)
                    return "No web results were found.";

                List<SearchResult> relevanceFiltered = FilterResultsByRelevance(fetchedResults, intent, originalMessage);
                relevanceFiltered = PrioritizeFreshResults(relevanceFiltered, intent);
                List<SearchResult> extractedResults = await FetchExtractedPageResultsAsync(relevanceFiltered, intent, searchToken).ConfigureAwait(false);
                if (extractedResults.Count == 0)
                    return "No web results were found.";

                string formatted = FormatSourcesBlock(extractedResults, intent, includeFreshnessLabel: true);
                SaveCache(cacheKey, formatted);
                return formatted;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return "Web search timed out.";
            }
            catch (Exception ex)
            {
                return "Web search unavailable: " + ex.Message;
            }
        }

        public async Task<string> SearchTopSnippetsAsync(string query, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "No web results available for an empty query.";

            using var searchTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            searchTimeout.CancelAfter(LooksLikeCurrentInfoQuery(query) || LooksLikeNewsQuery(query)
                ? CurrentSearchDeadline
                : SearchDeadline);
            CancellationToken searchToken = searchTimeout.Token;

            try
            {
                SearchIntent intent = BuildSearchIntent(query);
                string normalizedQuery = intent.Query;
                if (string.IsNullOrWhiteSpace(normalizedQuery))
                    return "No web results available for an empty query.";

                string cacheKey = BuildCacheKey(string.Empty, normalizedQuery);
                if (TryGetCached(cacheKey, intent, out string cached))
                    return cached;

                List<SearchResult> selected = await FetchRankedResultsAsync(intent, searchToken).ConfigureAwait(false);
                if (selected.Count == 0)
                    return "No web results were found.";

                selected = PrioritizeFreshResults(selected, intent);
                List<SearchResult> extractedResults = await FetchExtractedPageResultsAsync(selected, intent, searchToken).ConfigureAwait(false);
                if (extractedResults.Count == 0)
                    return "No web results were found.";

                string formatted = FormatSourcesBlock(extractedResults, intent);
                SaveCache(cacheKey, formatted);
                return formatted;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return "Web search timed out.";
            }
            catch (Exception ex)
            {
                return "Web search unavailable: " + ex.Message;
            }
        }

        public static double GetWordOverlap(string left, string right)
        {
            return ComputeWordOverlap(left, right);
        }

        private async Task<List<SearchResult>> FetchRankedResultsAsync(SearchIntent intent, CancellationToken token)
        {
            List<string> subQueries = BuildDeterministicSubQueries(intent);

            // Sub-queries run concurrently (each already fans out across providers with its own
            // deadline). The old sequential loop meant a second sub-query often had little or no
            // time left inside the overall search deadline. Position numbering preserves the
            // deterministic sub-query order so ranking tie-breaks stay stable.
            Task<List<SearchResult>>[] subQueryTasks = subQueries
                .Select(subQuery => SearchAcrossProvidersAsync(subQuery, intent, token))
                .ToArray();
            List<SearchResult>[] subQueryResults = await Task.WhenAll(subQueryTasks).ConfigureAwait(false);

            var aggregated = new List<SearchResult>();
            int position = 0;
            foreach (List<SearchResult> results in subQueryResults)
            {
                foreach (SearchResult result in results)
                    aggregated.Add(result with { Position = position++ });
            }

            List<SearchResult> deduplicated = DeduplicateResults(aggregated)
                .OrderByDescending(r => ScoreResult(r, intent))
                .ThenBy(r => r.Position)
                .ToList();

            List<SearchResult> selected = SelectHighConfidenceResults(deduplicated, intent);
            if (selected.Count == 0)
                return new List<SearchResult>();

            if (ShouldRetryWithFreshnessBias(selected, intent))
            {
                List<SearchResult> retryResults = await FetchAlternateFreshResultsAsync(intent, token).ConfigureAwait(false);
                if (retryResults.Count > 0)
                    return retryResults;
            }

            return selected;
        }

        private static async Task<List<SearchResult>> SearchAcrossProvidersAsync(string query, SearchIntent intent, CancellationToken token)
        {
            var providers = new List<Func<string, CancellationToken, Task<List<SearchResult>>>>();
            if (intent.News)
                providers.Add(SearchGoogleNewsRssAsync);

            providers.Add(SearchDuckDuckGoAsync);

            if (intent.CurrentInfo || intent.News || intent.Docs || intent.Release)
                providers.Add(SearchBingHtmlAsync);

            providers.Add(SearchBingRssAsync);

            Task<List<SearchResult>>[] tasks = providers
                .Select(provider => RunTimedProviderAsync(provider, query, token))
                .ToArray();

            List<SearchResult>[] providerResults = await Task.WhenAll(tasks).ConfigureAwait(false);
            return providerResults
                .SelectMany(results => results)
                .Take(MaxResultsPerQuery)
                .ToList();
        }

        private async Task<List<SearchResult>> FetchExtractedPageResultsAsync(IReadOnlyList<SearchResult> results, SearchIntent intent, CancellationToken token)
        {
            List<SearchResult> candidates = (results ?? []).Take(MaxResultCount).ToList();
            Task<SearchResult?>[] tasks = candidates
                .Select((result, index) => FetchExtractedSingleResultAsync(result, intent, index, token))
                .ToArray();

            SearchResult?[] resolved = await Task.WhenAll(tasks).ConfigureAwait(false);
            return resolved
                .Where(result => result != null)
                .Select(result => result!)
                .Where(result => !string.IsNullOrWhiteSpace(result.Snippet)
                    || !string.IsNullOrWhiteSpace(result.Title)
                    || !string.IsNullOrWhiteSpace(result.Url))
                .ToList();
        }

        private static async Task<List<SearchResult>> RunTimedProviderAsync(
            Func<string, CancellationToken, Task<List<SearchResult>>> provider,
            string query,
            CancellationToken token)
        {
            using var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutTokenSource.CancelAfter(ProviderDeadline);

            try
            {
                List<SearchResult> results = await provider(query, timeoutTokenSource.Token).ConfigureAwait(false);
                return results.Take(MaxResultsPerQuery).ToList();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new List<SearchResult>();
            }
        }

        private async Task<SearchResult?> FetchExtractedSingleResultAsync(SearchResult originalResult, SearchIntent intent, int index, CancellationToken token)
        {
            SearchResult result = originalResult;
            string snippet = result.Snippet ?? string.Empty;
            string host = GetHostName(result.Url);
            bool structuredFeedSnippet = LooksLikeStructuredFeedSnippet(snippet);

            if (index < MaxEvidenceFetchCount && !ShouldSkipPageEvidenceFetch(host, structuredFeedSnippet))
            {
                PageEvidence pageEvidence = await TryFetchPageEvidenceAsync(result.Url, result.Title, intent, token).ConfigureAwait(false);
                if (ShouldReplaceSnippetWithFetchedEvidence(snippet, pageEvidence.Text, intent, result.Title, host))
                {
                    result = result with
                    {
                        Title = string.IsNullOrWhiteSpace(pageEvidence.Title) ? result.Title : pageEvidence.Title,
                        Snippet = pageEvidence.Text,
                        PublishedAt = pageEvidence.PublishedAt ?? result.PublishedAt
                    };
                }
            }

            return result;
        }

        public string PreparePromptContext(string data, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(data) || maxChars <= 0)
                return string.Empty;

            string normalized = data.Trim();
            if (normalized.Length <= maxChars)
                return normalized;

            const string beginMarker = "[[WEB SEARCH DATA]]";
            const string endMarker = "[[END WEB SEARCH DATA]]";

            int start = normalized.IndexOf(beginMarker, StringComparison.OrdinalIgnoreCase);
            int end = normalized.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end <= start)
                return normalized[..Math.Min(normalized.Length, maxChars)].TrimEnd();

            string body = normalized[(start + beginMarker.Length)..end].Trim();
            var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var headerLines = new List<string>();
            var entryBlocks = new List<string>();
            var currentBlock = new StringBuilder();
            bool readingEntries = false;

            static int ComputePriority(string block)
            {
                string lower = block?.ToLowerInvariant() ?? string.Empty;
                int score = 0;
                if (lower.Contains("high confidence", StringComparison.Ordinal))
                    score += 6;
                if (Regex.IsMatch(lower, @"\[(?:20\d{2}|date unknown)", RegexOptions.IgnoreCase))
                    score += 2;
                if (lower.Contains("url:", StringComparison.Ordinal))
                    score += 1;
                if (lower.Contains("reuters", StringComparison.Ordinal)
                    || lower.Contains("apnews", StringComparison.Ordinal)
                    || lower.Contains("bbc", StringComparison.Ordinal)
                    || lower.Contains("microsoft", StringComparison.Ordinal)
                    || lower.Contains("github", StringComparison.Ordinal))
                {
                    score += 2;
                }

                return score;
            }

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                if (Regex.IsMatch(line, @"^\d+\.\s"))
                {
                    if (currentBlock.Length > 0)
                    {
                        entryBlocks.Add(currentBlock.ToString().Trim());
                        currentBlock.Clear();
                    }

                    readingEntries = true;
                }

                if (readingEntries)
                    currentBlock.AppendLine(line);
                else
                    headerLines.Add(line);
            }

            if (currentBlock.Length > 0)
                entryBlocks.Add(currentBlock.ToString().Trim());

            entryBlocks = entryBlocks
                .OrderByDescending(ComputePriority)
                .ThenByDescending(block => block.Length > 0 ? 1 : 0)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine(beginMarker);

            foreach (string headerLine in headerLines)
                sb.AppendLine(headerLine);

            if (headerLines.Count > 0)
                sb.AppendLine();

            int appendedEntries = 0;
            foreach (string entryBlock in entryBlocks)
            {
                string candidate = sb.ToString() + entryBlock.Trim() + Environment.NewLine + Environment.NewLine + endMarker;
                if (candidate.Length > maxChars)
                {
                    if (appendedEntries == 0)
                    {
                        int remaining = maxChars - sb.Length - endMarker.Length - Environment.NewLine.Length - 32;
                        if (remaining > 0)
                        {
                            string trimmedEntry = entryBlock.Length <= remaining
                                ? entryBlock.Trim()
                                : entryBlock[..Math.Min(entryBlock.Length, remaining)].TrimEnd() + "\n[...truncated to fit prompt budget]";

                            sb.AppendLine(trimmedEntry);
                            sb.AppendLine();
                        }
                    }

                    break;
                }

                sb.AppendLine(entryBlock.Trim());
                sb.AppendLine();
                appendedEntries++;
            }

            sb.AppendLine(endMarker);
            return sb.ToString().Trim();
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(18)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return client;
        }

        private static async Task<string> ReadContentAsStringBoundedAsync(HttpContent content, int maxChars, CancellationToken token)
        {
            if (content == null || maxChars <= 0)
                return string.Empty;

            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maxChars * 4L)
                return string.Empty;

            await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: false);
            var buffer = new char[Math.Min(8192, maxChars)];
            var sb = new StringBuilder(Math.Min(maxChars, 32_768));

            while (sb.Length < maxChars)
            {
                int remaining = maxChars - sb.Length;
                int read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (read <= 0)
                    break;

                sb.Append(buffer, 0, read);
            }

            return sb.ToString();
        }

        private static string NormalizeQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            string q = Regex.Replace(query, @"```[\s\S]*?```", " ", RegexOptions.IgnoreCase)
                .Replace("[web]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            q = WhitespaceRegex.Replace(q, " ");

            if (q.Length >= 2 && ((q.StartsWith('"') && q.EndsWith('"')) || (q.StartsWith('\'') && q.EndsWith('\''))))
                q = q[1..^1].Trim();

            string lower = q.ToLowerInvariant();
            string[] prefixes =
            [
                "search the web for", "web search for", "search web for", "look up", "lookup", "find online", "check online", "search online", "find"
            ];

            foreach (string prefix in prefixes)
            {
                if (lower.StartsWith(prefix, StringComparison.Ordinal))
                {
                    q = q[prefix.Length..].Trim();
                    break;
                }
            }

            return q.Trim(' ', '"', '\'', '.', '?', '!', ',', ':', ';');
        }

        private static SearchIntent BuildSearchIntent(string prompt)
        {
            string normalizedPrompt = NormalizeQuery(prompt);
            if (string.IsNullOrWhiteSpace(normalizedPrompt))
                return new SearchIntent(string.Empty, string.Empty, false, false, false, false, Array.Empty<string>());

            bool currentInfo = LooksLikeCurrentInfoQuery(normalizedPrompt);
            bool docs = LooksLikeDocsQuery(normalizedPrompt);
            bool release = LooksLikeReleaseQuery(normalizedPrompt);
            bool news = LooksLikeNewsQuery(normalizedPrompt);

            string selectedSentence = SelectMostSearchableSentence(normalizedPrompt, currentInfo, docs, release, news);
            IReadOnlyList<string> queryTerms = ExpandIntentTerms(
                ExtractStrategicTerms(selectedSentence, normalizedPrompt),
                normalizedPrompt,
                currentInfo,
                docs,
                release,
                news);
            string query = BuildQueryText(queryTerms, selectedSentence, currentInfo, docs, release, news);
            if (string.IsNullOrWhiteSpace(query))
                query = normalizedPrompt;

            return new SearchIntent(query, normalizedPrompt, currentInfo, docs, release, news, queryTerms);
        }

        private static List<string> BuildDeterministicSubQueries(SearchIntent intent)
        {
            var ranked = ExtractRankedKeywords(intent.Query);
            bool broadNews = IsBroadNewsIntent(intent) && !HasExplicitRegionalFocus(intent.BasePrompt);
            if (broadNews)
            {
                return
                [
                    intent.CurrentInfo ? "top headlines today Reuters AP BBC world" : "top headlines Reuters AP BBC world",
                    intent.CurrentInfo ? "latest world headlines today Reuters AP" : "latest world headlines Reuters AP",
                    intent.CurrentInfo ? "top US and world news today Reuters AP BBC" : "top US and world news Reuters AP BBC",
                    intent.CurrentInfo ? "breaking world news today Reuters AP BBC" : "breaking world news Reuters AP BBC"
                ];
            }

            if (intent.News && (ranked.Count == 0 || ranked.All(IsYearToken)))
            {
                if (broadNews)
                {
                    return
                    [
                        "latest US headlines today Reuters AP",
                        "top US breaking news today",
                        "latest world news today Reuters BBC",
                        "United States world news live headlines"
                    ];
                }

                return
                [
                    intent.CurrentInfo ? "latest breaking news reported today" : "breaking news reported today",
                    intent.CurrentInfo ? "top news stories today Reuters AP" : "top news stories Reuters AP",
                    "world news latest reported developments"
                ];
            }

            if (ranked.Count == 0)
                return [intent.Query];

            var topicWords = ranked.Take(4).ToList();
            var root = topicWords.Take(Math.Min(3, topicWords.Count)).ToList();
            string rootPhrase = string.Join(' ', root);
            string basePair = string.Join(' ', ranked.Take(Math.Min(2, ranked.Count)));
            string year = DateTime.UtcNow.Year.ToString();

            var subQueries = new List<string>();

            void Add(string value)
            {
                string candidate = NormalizeQuery(value);
                if (!string.IsNullOrWhiteSpace(candidate) && !subQueries.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    subQueries.Add(candidate);
            }

            Add(intent.Query);

            if (!string.Equals(intent.BasePrompt, intent.Query, StringComparison.OrdinalIgnoreCase) && intent.BasePrompt.Length <= 180)
                Add(intent.BasePrompt);

            Add(intent.CurrentInfo ? rootPhrase + " " + year : rootPhrase);

            if (intent.Docs)
            {
                Add(basePair + " official documentation");
                Add(basePair + " learn microsoft github docs");
            }
            else if (intent.Release)
            {
                Add(basePair + " official release notes");
                Add(basePair + " changelog release notes");
            }
            else if (intent.News)
            {
                Add(basePair + " reported news article");
                Add(basePair + " reported today Reuters AP article");
                if (broadNews)
                {
                    Add(basePair + " US headlines today Reuters AP");
                    Add(basePair + " United States world news today");
                    Add(basePair + " top US breaking news");
                }
            }
            else if (intent.CurrentInfo)
            {
                Add(basePair + " latest updates article");
                Add(basePair + " current status " + year);
            }
            else
                Add(string.Join(' ', ranked.Take(Math.Min(4, ranked.Count))));

            if (intent.CurrentInfo || intent.News)
                Add(basePair + " latest reported developments article");
            else if (intent.Docs)
                Add(basePair + " official docs");
            else if (intent.Release)
                Add(basePair + " changelog");
            else
                Add(basePair + " overview");

            if (intent.News)
                Add(basePair + " Reuters AP article");

            if (broadNews)
                Add("latest US world headlines Reuters AP BBC");

            return subQueries.Take(5).ToList();
        }

        private static string SelectMostSearchableSentence(string prompt, bool currentInfo, bool docs, bool release, bool news)
        {
            var sentences = Regex.Split(prompt, @"(?<=[\.!?])\s+")
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (sentences.Count == 0)
                return prompt;

            return sentences
                .OrderByDescending(sentence => ScoreSentence(sentence, currentInfo, docs, release, news))
                .ThenBy(s => s.Length)
                .First();
        }

        private static int ScoreSentence(string sentence, bool currentInfo, bool docs, bool release, bool news)
        {
            int score = ExtractRankedKeywords(sentence).Count;
            if (sentence.Contains('?', StringComparison.Ordinal))
                score += 4;
            if (QuotedPhraseRegex.IsMatch(sentence))
                score += 3;
            if (currentInfo && LooksLikeCurrentInfoQuery(sentence))
                score += 3;
            if (docs && LooksLikeDocsQuery(sentence))
                score += 3;
            if (release && LooksLikeReleaseQuery(sentence))
                score += 3;
            if (news && LooksLikeNewsQuery(sentence))
                score += 3;
            score += Regex.Matches(sentence, @"\b[A-Z][A-Za-z0-9\.\+#_-]*\b").Count;
            score += Regex.Matches(sentence, @"\b\d+(?:\.\d+)*\b").Count;
            return score;
        }

        private static List<string> ExtractStrategicTerms(string selectedSentence, string fullPrompt)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddTerm(string value)
            {
                string candidate = value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                if (IsGenericPromptTerm(candidate))
                    return;

                if (seen.Add(candidate))
                    ordered.Add(candidate);
            }

            if (TryBuildQuestionFocusedQuery(selectedSentence, out _, out List<string> focusTerms))
            {
                foreach (string term in focusTerms)
                    AddTerm(term);
            }

            foreach (Match match in QuotedPhraseRegex.Matches(selectedSentence))
                AddTerm(match.Groups["value"].Value.Trim());

            foreach (Match match in Regex.Matches(selectedSentence, @"\b[A-Z][A-Za-z0-9\.\+#_-]*\b"))
                AddTerm(match.Value);

            foreach (Match match in Regex.Matches(selectedSentence, @"\b[A-Za-z][A-Za-z0-9_\.-]*\d+(?:\.\d+)*[A-Za-z0-9_\.-]*\b"))
                AddTerm(match.Value);

            foreach (string token in ExtractRankedKeywords(selectedSentence))
                AddTerm(token);

            if (ordered.Count < 4)
            {
                foreach (string token in ExtractRankedKeywords(fullPrompt))
                {
                    AddTerm(token);
                    if (ordered.Count >= 8)
                        break;
                }
            }

            return ordered.Take(10).ToList();
        }

        private static bool IsGenericPromptTerm(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return true;

            string normalized = term.Trim().Trim(' ', '"', '\'', '.', '?', '!', ',', ':', ';');
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            var tokens = TokenRegex.Matches(normalized)
                .Select(m => m.Value)
                .ToList();
            if (tokens.Count == 0)
                return true;

            return tokens.All(token =>
                StopWords.Contains(token)
                || TopicShiftStopWords.Contains(token)
                || QuestionFillerWords.Contains(token)
                || QueryQualifierWords.Contains(token)
                || GenericBroadNewsTerms.Contains(token));
        }

        private static bool TryBuildQuestionFocusedQuery(string prompt, out string query, out List<string> focusTerms)
        {
            query = string.Empty;
            focusTerms = new List<string>();
            string normalized = NormalizeQuery(prompt);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            string subject = string.Empty;
            string relation = string.Empty;
            string target = string.Empty;

            Match infoAbout = Regex.Match(normalized,
                @"\b(?:latest|current|recent|new|newest|updated)?\s*(?:information|info|details|updates?|news|articles?|reports?)\s+(?:about|on|regarding|for)\s+(?<subject>[^?.,;]{2,160})",
                RegexOptions.IgnoreCase);
            if (infoAbout.Success)
            {
                subject = CleanFocusPhrase(infoAbout.Groups["subject"].Value);
                relation = "latest news updates";
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                Match latestOn = Regex.Match(normalized,
                    @"\b(?:what'?s\s+)?(?:the\s+)?(?:latest|current|recent|newest)\s+(?:on|about|regarding|for)\s+(?<subject>[^?.,;]{2,160})",
                    RegexOptions.IgnoreCase);
                if (latestOn.Success)
                {
                    subject = CleanFocusPhrase(latestOn.Groups["subject"].Value);
                    relation = "latest news updates";
                }
            }

            Match doesTo = Regex.Match(normalized,
                @"\bwhat\s+(?:does|do|did)\s+(?<subject>.+?)\s+(?<relation>do(?:es)?\s+to|do\s+for|mean\s+for|cause\s+in|cause\s+to)\s+(?<target>[^?.,;]{2,120})",
                RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(subject) && doesTo.Success)
            {
                subject = CleanFocusPhrase(doesTo.Groups["subject"].Value);
                relation = NormalizeRelationPhrase(doesTo.Groups["relation"].Value);
                target = CleanFocusPhrase(doesTo.Groups["target"].Value);
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                Match affect = Regex.Match(normalized,
                    @"\b(?:what|how)\s+(?:does|do|did|can|could|would|will)\s+(?<subject>.+?)\s+(?<relation>affect|impact|change|influence|interact\s+with|work\s+with|react\s+with|relate\s+to)\s+(?<target>[^?.,;]{2,120})",
                    RegexOptions.IgnoreCase);
                if (affect.Success)
                {
                    subject = CleanFocusPhrase(affect.Groups["subject"].Value);
                    relation = NormalizeRelationPhrase(affect.Groups["relation"].Value);
                    target = CleanFocusPhrase(affect.Groups["target"].Value);
                }
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                Match explainRelation = Regex.Match(normalized,
                    @"\b(?:explain|describe)\s+(?:the\s+)?(?<relation>relationship|relation|connection|interaction|effect|impact)\s+(?:between|of)\s+(?<subject>.+?)\s+(?:and|on|to|with)\s+(?<target>[^?.,;]{2,120})",
                    RegexOptions.IgnoreCase);
                if (explainRelation.Success)
                {
                    subject = CleanFocusPhrase(explainRelation.Groups["subject"].Value);
                    relation = NormalizeRelationPhrase(explainRelation.Groups["relation"].Value);
                    target = CleanFocusPhrase(explainRelation.Groups["target"].Value);
                }
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                Match definition = Regex.Match(normalized,
                    @"\b(?:what\s+(?:is|are|was|were)|define|explain|describe)\s+(?<subject>[^?.,;]{2,140})",
                    RegexOptions.IgnoreCase);
                if (definition.Success)
                {
                    subject = CleanFocusPhrase(definition.Groups["subject"].Value);
                    relation = "overview";
                }
            }

            if (string.IsNullOrWhiteSpace(subject))
                return false;

            focusTerms = ExtractFocusTerms(subject)
                .Concat(ExtractFocusTerms(target))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            if (focusTerms.Count == 0)
                return false;

            if (!string.IsNullOrWhiteSpace(target))
                query = NormalizeQuery($"{subject} {relation} {target}");
            else
                query = NormalizeQuery($"{subject} {relation}");

            if (string.IsNullOrWhiteSpace(query))
                query = string.Join(' ', focusTerms);

            return !string.IsNullOrWhiteSpace(query);
        }

        private static string CleanFocusPhrase(string phrase)
        {
            string cleaned = CleanText(phrase);
            if (string.IsNullOrWhiteSpace(cleaned))
                return string.Empty;

            cleaned = Regex.Replace(cleaned,
                @"\b(?:in general|generally|exactly|actually|basically|overall|main focus|the main focus|my prompt|this prompt|this question|the question)\b",
                " ",
                RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^(?:the|a|an|about|of|to|for|on|with)\s+", string.Empty, RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '"', '\'', '.', '?', '!', ',', ':', ';');

            List<string> tokens = TokenRegex.Matches(cleaned)
                .Select(m => m.Value)
                .Where(token => token.Length > 1 && !QuestionFillerWords.Contains(token))
                .Take(8)
                .ToList();

            return tokens.Count == 0 ? string.Empty : string.Join(' ', tokens);
        }

        private static List<string> ExtractFocusTerms(string phrase)
        {
            return TokenRegex.Matches(phrase ?? string.Empty)
                .Select(m => m.Value.Trim())
                .Where(token => token.Length >= 2)
                .Where(token => !QuestionFillerWords.Contains(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeRelationPhrase(string relation)
        {
            string lower = CleanText(relation).ToLowerInvariant();
            return lower switch
            {
                "do to" or "does to" or "do for" => "effect on",
                "mean for" => "meaning for",
                "cause in" or "cause to" => "causes effects on",
                "affect" or "impact" or "influence" => "effect on",
                "change" => "changes in",
                "interact with" or "work with" or "react with" => "interaction with",
                "relate to" or "relationship" or "relation" or "connection" => "relationship with",
                "effect" or "impact" => "effect on",
                _ => string.IsNullOrWhiteSpace(lower) ? "overview" : lower
            };
        }

        private static IReadOnlyList<string> ExpandIntentTerms(IReadOnlyList<string> baseTerms, string prompt, bool currentInfo, bool docs, bool release, bool news)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? value)
            {
                string candidate = value?.Trim() ?? string.Empty;
                if (candidate.Length < 3)
                    return;

                if (seen.Add(candidate))
                    ordered.Add(candidate);
            }

            foreach (string term in baseTerms)
                Add(term);

            foreach (string term in ExtractLooseKeywords(prompt))
            {
                Add(term);
                if (ordered.Count >= 8)
                    break;
            }

            if (news)
            {
                Add("news");
                Add("headlines");
                Add("reported");
                if (currentInfo)
                    Add("today");
            }

            if (currentInfo)
            {
                Add("latest");
                Add("current");
                Add(DateTime.UtcNow.Year.ToString());
            }

            if (docs)
            {
                Add("documentation");
                Add("docs");
                Add("api");
            }

            if (release)
            {
                Add("release");
                Add("version");
                Add("changelog");
            }

            return ordered.Take(10).ToList();
        }

        private static List<string> ExtractLooseKeywords(string text)
        {
            return TokenRegex.Matches(text ?? string.Empty)
                .Select(m => m.Value.Trim().ToLowerInvariant())
                .Where(token => token.Length >= 3)
                .Where(token => !TopicShiftStopWords.Contains(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildQueryText(IReadOnlyList<string> queryTerms, string selectedSentence, bool currentInfo, bool docs, bool release, bool news)
        {
            string selected = Regex.Replace(selectedSentence,
                @"^(?:can you|could you|would you|will you|please|tell me|show me|help me|i need to know|i want to know|search for|look up|find out)\b[\s,:-]*",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();

            bool broadNews = news && IsBroadNewsPrompt(selectedSentence, queryTerms);

            if (!broadNews && TryBuildQuestionFocusedQuery(selectedSentence, out string focusedQuestionQuery, out _))
            {
                string focused = focusedQuestionQuery;
                if (docs && !focused.Contains("documentation", StringComparison.OrdinalIgnoreCase) && !focused.Contains("docs", StringComparison.OrdinalIgnoreCase))
                    focused += " official documentation";
                else if (release && !focused.Contains("release", StringComparison.OrdinalIgnoreCase) && !focused.Contains("changelog", StringComparison.OrdinalIgnoreCase))
                    focused += " official release notes";
                else if ((news || currentInfo) && !focused.Contains("article", StringComparison.OrdinalIgnoreCase))
                    focused += " article";

                if (currentInfo && !Regex.IsMatch(focused, @"\b20\d{2}\b"))
                    focused += " " + DateTime.UtcNow.Year;

                return NormalizeQuery(focused);
            }

            List<string> filteredTerms = queryTerms
                .Where(term => !QueryQualifierWords.Contains(term))
                .ToList();

            string baseQuery = filteredTerms.Count == 0
                ? selected
                : string.Join(' ', filteredTerms);

            if (broadNews && !HasExplicitRegionalFocus(selected))
                return NormalizeQuery(currentInfo ? "top headlines today Reuters AP BBC world" : "top headlines Reuters AP BBC world");

            if (news && (filteredTerms.Count == 0 || filteredTerms.All(IsYearToken)))
                baseQuery = currentInfo ? "latest breaking news reported today" : "breaking news reported today";

            if (docs && !baseQuery.Contains("documentation", StringComparison.OrdinalIgnoreCase) && !baseQuery.Contains("docs", StringComparison.OrdinalIgnoreCase))
                baseQuery += " official documentation";
            else if (release && !baseQuery.Contains("release", StringComparison.OrdinalIgnoreCase) && !baseQuery.Contains("changelog", StringComparison.OrdinalIgnoreCase))
                baseQuery += " official release notes";
            else if (news && !baseQuery.Contains("news", StringComparison.OrdinalIgnoreCase))
                baseQuery += " reported news";

            if ((news || currentInfo) && !baseQuery.Contains("article", StringComparison.OrdinalIgnoreCase))
                baseQuery += " article";

            if (news && !HasExplicitRegionalFocus(baseQuery) && LooksLikeBroadNewsText(selected))
                baseQuery += " US world";

            if (currentInfo && !Regex.IsMatch(baseQuery, @"\b20\d{2}\b"))
                baseQuery += " " + DateTime.UtcNow.Year;

            return NormalizeQuery(baseQuery);
        }

        private static bool IsYearToken(string token)
        {
            return Regex.IsMatch(token ?? string.Empty, @"^20\d{2}$");
        }

        private static List<string> ExtractRankedKeywords(string text)
        {
            var counts = new Dictionary<string, (int Count, int FirstIndex)>(StringComparer.OrdinalIgnoreCase);
            int index = 0;

            foreach (Match match in TokenRegex.Matches(text ?? string.Empty))
            {
                string token = match.Value.Trim();
                if (token.Length < 3 || StopWords.Contains(token))
                    continue;

                if (counts.TryGetValue(token, out var entry))
                    counts[token] = (entry.Count + 1, entry.FirstIndex);
                else
                    counts[token] = (1, index);

                index++;
            }

            return counts
                .OrderByDescending(kvp => kvp.Value.Count)
                .ThenBy(kvp => kvp.Value.FirstIndex)
                .Select(kvp => kvp.Key.ToLowerInvariant())
                .ToList();
        }

        private static bool LooksLikeNewsQuery(string query)
        {
            string lower = query.ToLowerInvariant();
            string[] markers = ["news", "headline", "headlines", "breaking", "current events", "top stories"];
            return markers.Any(lower.Contains);
        }

        private static bool LooksLikeCurrentInfoQuery(string query)
        {
            string lower = query.ToLowerInvariant();
            string[] markers =
            [
                "latest", "today", "current", "recent", "new", "now",
                "this week", "this month", "yesterday", "right now",
                "what's happening", "whats happening", "at the moment",
                "just released", "just launched", "just announced",
                "breaking", "presently", "happening now"
            ];
            if (markers.Any(lower.Contains))
                return true;

            // Match current and next calendar year dynamically
            int year = DateTime.UtcNow.Year;
            return lower.Contains(year.ToString()) || lower.Contains((year + 1).ToString());
        }

        private static bool LooksLikeDocsQuery(string query)
        {
            string lower = query.ToLowerInvariant();
            string[] markers = ["docs", "documentation", "api", "sdk", "reference", "manual"];
            return markers.Any(lower.Contains);
        }

        private static bool LooksLikeReleaseQuery(string query)
        {
            string lower = query.ToLowerInvariant();
            string[] markers = ["release", "version", "changelog", "roadmap", "announcement", "update"];
            return markers.Any(lower.Contains);
        }

        private static bool LooksLikeHighStakesOrSourceBackedRequest(string query)
        {
            string lower = (query ?? string.Empty).ToLowerInvariant();
            string[] markers =
            [
                "official", "source", "sources", "cite", "citation", "evidence", "verify", "fact check",
                "documentation", "docs", "api", "sdk", "pricing", "price", "cost", "policy", "terms",
                "legal", "law", "regulation", "compliance", "medical", "health", "clinical", "finance",
                "financial", "tax", "rate", "rates", "version", "release", "roadmap", "changelog"
            ];

            return markers.Any(lower.Contains);
        }

        private static async Task<List<SearchResult>> SearchDuckDuckGoAsync(string query, CancellationToken token)
        {
            var aggregated = new List<SearchResult>();
            string[] endpoints =
            [
                "https://html.duckduckgo.com/html/?q=",
                "https://lite.duckduckgo.com/lite/?q="
            ];

            foreach (string endpoint in endpoints)
            {
                string url = endpoint + Uri.EscapeDataString(query);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                string html = await ReadContentAsStringBoundedAsync(response.Content, MaxSearchPayloadChars, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                    continue;

                if (html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("Unfortunately, bots use DuckDuckGo too", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                aggregated.AddRange(ParseResults(html));
                if (aggregated.Count >= MaxResultsPerQuery)
                    break;
            }

            return aggregated.Take(MaxResultsPerQuery).ToList();
        }

        private static async Task<List<SearchResult>> SearchBingRssAsync(string query, CancellationToken token)
        {
            string url = "https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new List<SearchResult>();

            string xml = await ReadContentAsStringBoundedAsync(response.Content, MaxSearchPayloadChars, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml))
                return new List<SearchResult>();

            try
            {
                var document = XDocument.Parse(xml);
                return document.Descendants("item")
                    .Select((item, index) => new SearchResult(
                        CleanText(item.Element("title")?.Value),
                        TruncateSnippet(CleanText(item.Element("description")?.Value)),
                        CleanText(item.Element("link")?.Value),
                        index,
                        TryParseDate(item.Element("pubDate")?.Value)))
                    .Where(result => !string.IsNullOrWhiteSpace(result.Title)
                        && !string.IsNullOrWhiteSpace(result.Snippet)
                        && !string.IsNullOrWhiteSpace(result.Url))
                    .Take(MaxResultsPerQuery)
                    .ToList();
            }
            catch
            {
                return new List<SearchResult>();
            }
        }

        private static async Task<List<SearchResult>> SearchBingHtmlAsync(string query, CancellationToken token)
        {
            string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&setlang=en-US&cc=us";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new List<SearchResult>();

            string html = await ReadContentAsStringBoundedAsync(response.Content, MaxSearchPayloadChars, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
                return new List<SearchResult>();

            return ParseBingHtmlResults(html);
        }

        private static async Task<List<SearchResult>> SearchGoogleNewsRssAsync(string query, CancellationToken token)
        {
            string url = "https://news.google.com/rss/search?q=" + Uri.EscapeDataString(query) + "&hl=en-US&gl=US&ceid=US:en";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new List<SearchResult>();

            string xml = await ReadContentAsStringBoundedAsync(response.Content, MaxSearchPayloadChars, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml))
                return new List<SearchResult>();

            try
            {
                var document = XDocument.Parse(xml);
                return document.Descendants("item")
                    .Select((item, index) =>
                    {
                        string rawTitle = CleanText(item.Element("title")?.Value);
                        string sourceHost = ExtractSourceNameFromFeedTitle(rawTitle);
                        string normalizedTitle = NormalizeFeedTitle(rawTitle);
                        string description = CleanText(item.Element("description")?.Value);
                        string link = CleanText(item.Element("link")?.Value);
                        DateTimeOffset? publishedAt = TryParseDate(item.Element("pubDate")?.Value);

                        string snippet = BuildFeedSnippet(normalizedTitle, description, sourceHost, publishedAt);
                        return new SearchResult(normalizedTitle, snippet, link, index, publishedAt);
                    })
                    .Where(result => !string.IsNullOrWhiteSpace(result.Title)
                        && !string.IsNullOrWhiteSpace(result.Snippet)
                        && !string.IsNullOrWhiteSpace(result.Url))
                    .Take(MaxResultsPerQuery)
                    .ToList();
            }
            catch
            {
                return new List<SearchResult>();
            }
        }

        private static List<SearchResult> DeduplicateResults(IEnumerable<SearchResult> source)
        {
            var kept = new List<SearchResult>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SearchResult result in source.OrderBy(r => r.Position))
            {
                string normalizedUrl = NormalizeUrl(result.Url);
                if (!string.IsNullOrWhiteSpace(normalizedUrl) && !seenUrls.Add(normalizedUrl))
                    continue;

                string sentence = GetFirstSentence(result.Snippet);
                if (kept.Any(existing => ComputeWordOverlap(sentence, GetFirstSentence(existing.Snippet)) > 0.60))
                    continue;

                kept.Add(result);
            }

            return kept;
        }

        private static List<SearchResult> SelectHighConfidenceResults(IEnumerable<SearchResult> ranked, SearchIntent intent)
        {
            bool definitionFriendly = IsDefinitionOrExplanationIntent(intent);
            var candidates = ranked
                .Where(result => !IsBlockedAuthority(result))
                .Where(result => definitionFriendly || !LooksLikeDefinitionResult(result))
                .Where(result => !LooksLikeGenericLandingResult(result, intent))
                .Where(result => ScoreResult(result, intent) >= 1)
                .ToList();

            if (candidates.Count == 0)
                return new List<SearchResult>();

            var preferred = candidates
                .Where(result => IsTrustedAuthority(result)
                    || ComputeQuestionRelationScore(result, intent) >= 6
                    || ComputeTermCoverage(result, intent) >= 2)
                .ToList();

            if (preferred.Count == 0)
                preferred = candidates;

            if (IsBroadNewsIntent(intent) && !HasExplicitRegionalFocus(intent.BasePrompt))
                return SelectBroadNewsResults(preferred, candidates, intent);

            var selected = preferred.Take(MaxResultCount).ToList();
            if (selected.Count < MaxResultCount)
            {
                foreach (SearchResult result in candidates)
                {
                    if (selected.Any(existing => string.Equals(NormalizeUrl(existing.Url), NormalizeUrl(result.Url), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    selected.Add(result);
                    if (selected.Count >= MaxResultCount)
                        break;
                }
            }

            return selected;
        }

        private static string FormatSourcesBlock(IReadOnlyList<SearchResult> results, SearchIntent intent, bool includeFreshnessLabel = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[[WEB SEARCH DATA]]");
            sb.AppendLine($"WEB SEARCH RESULTS — {DateTime.UtcNow:yyyy-MM-dd}");
            sb.AppendLine("Search focus: " + intent.Query);
            sb.AppendLine("Use the evidence below for current/source-backed claims that it actually covers. Prefer high-confidence/official sources, do not treat off-topic results as support, and keep stable background knowledge separate from source-backed claims.");
            if (IsBroadNewsIntent(intent) && !HasExplicitRegionalFocus(intent.BasePrompt))
                sb.AppendLine("For broad news/headline requests, synthesize multiple distinct top headlines from different reputable sources. Do not collapse the answer to a single article unless only one distinct verified item is available.");
            else if (intent.News)
                sb.AppendLine("When an entry includes Headline, Source, or Published fields, treat those fields as directly usable evidence and surface the actual headline titles in the answer.");
            else
                sb.AppendLine("Answer with the strongest supported conclusion available from these sources. If a detail is missing, state what is confirmed and briefly note the gap instead of refusing the entire answer.");
            List<string> synthesizedEvidence = BuildEvidenceDigest(results, intent);
            if (synthesizedEvidence.Count > 0)
            {
                sb.AppendLine("Key evidence matched to the prompt:");
                foreach (string bullet in synthesizedEvidence)
                    sb.AppendLine("- " + bullet);
                sb.AppendLine();
            }
            sb.AppendLine();

            for (int i = 0; i < results.Count; i++)
            {
                SearchResult result = results[i];
                string heading = string.IsNullOrWhiteSpace(result.Title) ? result.Url : result.Title;
                if (string.IsNullOrWhiteSpace(heading))
                    heading = "Source";

                string authorityDisplay = GetResultAuthorityDisplayName(result);
                string reliability = GetReliabilityLabel(result);
                string freshnessLabel = includeFreshnessLabel ? GetFreshnessLabel(result.Snippet, result.PublishedAt) + " " : string.Empty;
                string snippetText = result.Snippet?.Trim() ?? string.Empty;
                bool isFullArticleContent = snippetText.Length > 400 && !LooksLikeStructuredFeedSnippet(snippetText);

                sb.Append(i + 1)
                    .Append(". ")
                    .AppendLine(string.IsNullOrWhiteSpace(authorityDisplay)
                        ? heading.Trim()
                        : $"{heading.Trim()} {freshnessLabel}[{authorityDisplay} • {reliability}]");
                if (!string.IsNullOrWhiteSpace(authorityDisplay))
                    sb.AppendLine("Source domain: " + authorityDisplay.Trim());
                string sourceUrl = result.Url?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sourceUrl))
                    sb.AppendLine("URL: " + sourceUrl);
                string publishedLabel = GetFreshnessLabel(snippetText, result.PublishedAt).Trim('[', ']');
                if (!string.IsNullOrWhiteSpace(publishedLabel))
                    sb.AppendLine("Published: " + publishedLabel);
                if (!string.IsNullOrWhiteSpace(snippetText))
                {
                    // Label long fetched article content so the AI knows it is full article body.
                    if (isFullArticleContent)
                        sb.Append("Article content: ");
                    sb.AppendLine(snippetText);
                }

                if (i < results.Count - 1)
                    sb.AppendLine();
            }

            sb.AppendLine("[[END WEB SEARCH DATA]]");
            return sb.ToString().Trim();
        }

        private static List<SearchResult> FilterResultsByRelevance(IReadOnlyList<SearchResult> results, SearchIntent intent, string originalMessage)
        {
            if (results.Count == 0)
                return new List<SearchResult>();

            if (IsBroadNewsIntent(intent) && !HasExplicitRegionalFocus(intent.BasePrompt))
                return SelectBroadNewsResults(results, results, intent);

            List<SearchResult> filtered = results
                .Where(r => ComputeWordOverlap((r.Title ?? string.Empty) + " " + (r.Snippet ?? string.Empty), originalMessage) >= 0.06
                    || ComputeWordOverlap(r.Snippet, originalMessage) >= 0.08)
                .OrderByDescending(r => ComputeWordOverlap((r.Title ?? string.Empty) + " " + (r.Snippet ?? string.Empty), originalMessage))
                .ThenBy(r => r.Position)
                .ToList();

            if (filtered.Count > 0)
                return filtered;

            return results
                .OrderByDescending(r => ComputeWordOverlap((r.Title ?? string.Empty) + " " + (r.Snippet ?? string.Empty), originalMessage))
                .ThenBy(r => r.Position)
                .Take(3)
                .ToList();
        }

        private static List<SearchResult> PrioritizeFreshResults(IReadOnlyList<SearchResult> results, SearchIntent intent)
        {
            if (results == null || results.Count == 0)
                return new List<SearchResult>();

            if (!intent.CurrentInfo && !intent.News)
                return results.Take(MaxResultCount).ToList();

            var ordered = results
                .OrderByDescending(r => IsFreshEnough(r.PublishedAt, intent))
                .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => ScoreResult(r, intent))
                .ThenBy(r => r.Position)
                .ToList();

            List<SearchResult> fresh = ordered
                .Where(r => IsFreshEnough(r.PublishedAt, intent))
                .Take(MaxResultCount)
                .ToList();

            if (fresh.Count >= Math.Min(2, ordered.Count))
                return fresh;

            List<SearchResult> dated = ordered
                .Where(r => r.PublishedAt.HasValue)
                .Take(MaxResultCount)
                .ToList();

            return dated.Count > 0 ? dated : ordered.Take(MaxResultCount).ToList();
        }

        private static string GetFreshnessLabel(string snippet, DateTimeOffset? publishedAt = null)
        {
            if (publishedAt.HasValue)
                return $"[{publishedAt.Value.UtcDateTime:yyyy-MM-dd}]";

            if (string.IsNullOrWhiteSpace(snippet))
                return "[date unknown]";

            Match match = FreshnessYearRegex.Match(snippet);
            if (!match.Success)
                return "[date unknown]";

            if (!int.TryParse(match.Groups["year"].Value, out int year))
                return "[date unknown]";

            int currentYear = DateTime.UtcNow.Year;
            return year >= 2020 && year <= currentYear
                ? $"[{year}]"
                : "[date unknown]";
        }

        // Query parameters that only identify the click source, never the page content. Stripping
        // them lets dedup recognize the same article shared through different tracking links,
        // which yields more DISTINCT sources for the model instead of near-duplicates.
        private static readonly HashSet<string> TrackingQueryParameters = new(StringComparer.OrdinalIgnoreCase)
        {
            "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
            "fbclid", "gclid", "msclkid", "twclid", "igshid", "mc_cid", "mc_eid", "ref", "ref_src",
            "cmpid", "ocid", "smid", "ito", "srnd", "guccounter", "guce_referrer", "ncid", "sref"
        };

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            string trimmed = url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return trimmed.TrimEnd('/');
            }

            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
                host = host[4..];

            string path = uri.AbsolutePath.TrimEnd('/');

            string query = string.Empty;
            if (!string.IsNullOrEmpty(uri.Query) && uri.Query.Length > 1)
            {
                var keptPairs = uri.Query[1..]
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(pair =>
                    {
                        int equalsIndex = pair.IndexOf('=');
                        string key = equalsIndex >= 0 ? pair[..equalsIndex] : pair;
                        return !TrackingQueryParameters.Contains(key);
                    })
                    .ToList();
                if (keptPairs.Count > 0)
                    query = "?" + string.Join("&", keptPairs);
            }

            // Scheme and fragment are dropped: http/https and #anchors point at the same content.
            return host + path + query;
        }

        private static string GetHostName(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return string.Empty;

            return uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetUrlPath(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return string.Empty;

            return uri.AbsolutePath ?? string.Empty;
        }

        private static int ScoreResult(SearchResult result, SearchIntent intent)
        {
            int score = 0;
            string title = (result.Title ?? string.Empty).ToLowerInvariant();
            string snippet = (result.Snippet ?? string.Empty).ToLowerInvariant();
            string host = GetHostName(result.Url).ToLowerInvariant();
            string authorityKey = GetResultAuthorityKey(result);
            bool broadNews = intent.News && IsBroadNewsIntent(intent) && !HasExplicitRegionalFocus(intent.BasePrompt);

            if (IsBlockedAuthority(result))
                return -100;

            bool definitionFriendly = IsDefinitionOrExplanationIntent(intent);
            if (LooksLikeDefinitionResult(result))
                score += definitionFriendly ? 2 : -40;

            if (LooksLikeGenericLandingResult(result, intent))
                return -40;

            foreach (string term in intent.QueryTerms)
            {
                string lowerTerm = term.ToLowerInvariant();
                if (title.Contains(lowerTerm, StringComparison.Ordinal))
                    score += 4;
                if (snippet.Contains(lowerTerm, StringComparison.Ordinal))
                    score += 2;
            }

            if (MatchesAuthorityList(authorityKey, TrustedSourceHosts))
                score += 8;
            if (broadNews && MatchesAuthorityList(authorityKey, PreferredBroadNewsHosts))
                score += 8;
            if (broadNews && MatchesAuthorityList(authorityKey, DeprioritizedBroadNewsHosts))
                score -= 14;
            if (MatchesAuthorityList(authorityKey, LowConfidenceHosts))
                score -= 6;
            if (host.EndsWith(".gov", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".edu", StringComparison.OrdinalIgnoreCase))
                score += 5;
            if (intent.Docs && (host.Contains("docs", StringComparison.OrdinalIgnoreCase) || title.Contains("documentation", StringComparison.Ordinal) || title.Contains("api", StringComparison.Ordinal)))
                score += 5;
            if (intent.Release && (title.Contains("release", StringComparison.Ordinal) || title.Contains("changelog", StringComparison.Ordinal) || snippet.Contains("release notes", StringComparison.Ordinal)))
                score += 5;
            if (intent.News && (title.Contains("news", StringComparison.Ordinal) || snippet.Contains("reported", StringComparison.Ordinal)))
                score += 3;
            if (intent.News && snippet.Contains("headline:", StringComparison.Ordinal))
                score += 6;
            if (intent.News && snippet.Contains("published:", StringComparison.Ordinal))
                score += 3;
            if (intent.CurrentInfo && (title.Contains(DateTime.UtcNow.Year.ToString(), StringComparison.Ordinal) || snippet.Contains(DateTime.UtcNow.Year.ToString(), StringComparison.Ordinal)))
                score += 3;
            if (result.PublishedAt.HasValue)
                score += ScoreRecency(result.PublishedAt.Value, intent);

            score += ComputeTermCoverage(result, intent);
            score += ComputeQuestionRelationScore(result, intent);
            score += ComputeSourceQualityScore(result, intent);

            return score;
        }

        private static int ComputeQuestionRelationScore(SearchResult result, SearchIntent intent)
        {
            if (!TryBuildQuestionFocusedQuery(intent.BasePrompt, out string focusedQuery, out List<string> focusTerms)
                || focusTerms.Count == 0)
            {
                return 0;
            }

            string haystack = ((result.Title ?? string.Empty) + " " + (result.Snippet ?? string.Empty) + " " + (result.Url ?? string.Empty)).ToLowerInvariant();
            int coveredTerms = focusTerms
                .Take(8)
                .Count(term => haystack.Contains(term.ToLowerInvariant(), StringComparison.Ordinal));

            int score = coveredTerms * 2;
            if (coveredTerms >= Math.Min(2, focusTerms.Count))
                score += 2;

            string focusedLower = focusedQuery.ToLowerInvariant();
            if (focusedLower.Contains("effect on", StringComparison.Ordinal)
                || focusedLower.Contains("changes in", StringComparison.Ordinal)
                || focusedLower.Contains("interaction with", StringComparison.Ordinal)
                || focusedLower.Contains("relationship with", StringComparison.Ordinal))
            {
                string[] relationWords = ["effect", "affect", "impact", "change", "interaction", "relationship", "influence", "cause", "causes"];
                if (relationWords.Any(word => haystack.Contains(word, StringComparison.Ordinal)))
                    score += 4;

                if (QuerySidesCovered(focusedQuery, haystack))
                    score += 6;
            }

            return score;
        }

        private static bool QuerySidesCovered(string focusedQuery, string haystack)
        {
            string[] relationMarkers = [" effect on ", " meaning for ", " causes effects on ", " changes in ", " interaction with ", " relationship with "];
            foreach (string marker in relationMarkers)
            {
                int idx = focusedQuery.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx <= 0)
                    continue;

                string left = focusedQuery[..idx];
                string right = focusedQuery[(idx + marker.Length)..];
                bool leftCovered = ExtractFocusTerms(left).Any(term => haystack.Contains(term.ToLowerInvariant(), StringComparison.Ordinal));
                bool rightCovered = ExtractFocusTerms(right).Any(term => haystack.Contains(term.ToLowerInvariant(), StringComparison.Ordinal));
                return leftCovered && rightCovered;
            }

            return false;
        }

        private static int ComputeSourceQualityScore(SearchResult result, SearchIntent intent)
        {
            string host = GetHostName(result.Url).ToLowerInvariant();
            string title = (result.Title ?? string.Empty).ToLowerInvariant();
            string snippet = (result.Snippet ?? string.Empty).ToLowerInvariant();
            string authorityKey = GetResultAuthorityKey(result);
            int score = 0;

            if (MatchesAuthorityList(authorityKey, TrustedSourceHosts))
                score += 5;
            if (host.EndsWith(".gov", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".edu", StringComparison.OrdinalIgnoreCase))
                score += 5;
            if (intent.Docs && (host.Contains("docs", StringComparison.Ordinal) || title.Contains("documentation", StringComparison.Ordinal) || title.Contains("reference", StringComparison.Ordinal)))
                score += 5;
            if (intent.Release && (title.Contains("release notes", StringComparison.Ordinal) || title.Contains("changelog", StringComparison.Ordinal)))
                score += 5;
            if (IsDefinitionOrExplanationIntent(intent) && (host.EndsWith("wikipedia.org", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".edu", StringComparison.OrdinalIgnoreCase)))
                score += 3;
            if (LooksLikeAggregatorOrLowEvidencePage(result))
                score -= 5;
            if (GenericPromptWordHosts.Any(host.EndsWith) && !HostLooksOfficialForFocus(host, intent))
                score -= 8;
            if (HostLooksOfficialForFocus(host, intent))
                score += 6;

            return score;
        }

        private static bool HostLooksOfficialForFocus(string host, SearchIntent intent)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            if (!TryBuildQuestionFocusedQuery(intent.BasePrompt, out _, out List<string> focusTerms))
                focusTerms = intent.QueryTerms.Take(6).ToList();

            string normalizedHost = NormalizeAuthorityValue(host);
            foreach (string term in focusTerms)
            {
                string normalizedTerm = NormalizeAuthorityValue(term);
                if (normalizedTerm.Length >= 4 && normalizedHost.Contains(normalizedTerm, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool LooksLikeAggregatorOrLowEvidencePage(SearchResult result)
        {
            string host = GetHostName(result.Url);
            string path = GetUrlPath(result.Url);
            string snippet = CleanText(result.Snippet).ToLowerInvariant();

            if (AggregatorSourceHosts.Any(host.EndsWith))
                return true;

            if (string.IsNullOrWhiteSpace(snippet) || snippet.Length < 45)
                return true;

            string trimmedPath = path.Trim('/');
            return string.IsNullOrWhiteSpace(trimmedPath)
                && (snippet.Contains("latest", StringComparison.Ordinal)
                    || snippet.Contains("home", StringComparison.Ordinal)
                    || snippet.Contains("sign up", StringComparison.Ordinal));
        }

        private static int ComputeTermCoverage(SearchResult result, SearchIntent intent)
        {
            string haystack = ((result.Title ?? string.Empty) + " " + (result.Snippet ?? string.Empty) + " " + (result.Url ?? string.Empty)).ToLowerInvariant();
            return intent.QueryTerms
                .Take(6)
                .Count(term => haystack.Contains(term.ToLowerInvariant(), StringComparison.Ordinal));
        }

        private static bool IsTrustedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            return TrustedSourceHosts.Any(host.EndsWith)
                || host.EndsWith(".gov", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".edu", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBlockedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            return BlockedSourceHosts.Any(host.EndsWith) || LowConfidenceHosts.Any(host.EndsWith);
        }

        private static bool IsTrustedAuthority(SearchResult result)
        {
            string authorityKey = GetResultAuthorityKey(result);
            if (string.IsNullOrWhiteSpace(authorityKey))
                return false;

            return MatchesAuthorityList(authorityKey, TrustedSourceHosts)
                || MatchesAuthorityList(authorityKey, ["gov", "edu"]);
        }

        private static bool IsBlockedAuthority(SearchResult result)
        {
            string authorityKey = GetResultAuthorityKey(result);
            return MatchesAuthorityList(authorityKey, BlockedSourceHosts)
                || MatchesAuthorityList(authorityKey, LowConfidenceHosts);
        }

        private static bool LooksLikeDefinitionResult(SearchResult result)
        {
            string haystack = ((result.Title ?? string.Empty) + " " + (result.Snippet ?? string.Empty) + " " + (result.Url ?? string.Empty)).ToLowerInvariant();
            return DefinitionResultMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal));
        }

        private static bool IsDefinitionOrExplanationIntent(SearchIntent intent)
        {
            string prompt = (intent.BasePrompt ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(prompt))
                return false;

            if (Regex.IsMatch(prompt, @"\b(?:what\s+(?:is|are|was|were)|define|definition|meaning\s+of|explain|describe)\b", RegexOptions.IgnoreCase))
                return true;

            return Regex.IsMatch(prompt, @"\b(?:what|how)\s+(?:does|do|did|can|could|would|will)\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(prompt, @"\b(?:do\s+to|do\s+for|affect|impact|change|influence|interact|work\s+with|react\s+with|relate\s+to|relationship|connection)\b", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeGenericLandingResult(SearchResult result, SearchIntent intent)
        {
            if (!intent.News && !intent.CurrentInfo)
                return false;

            string title = CleanText(result.Title).ToLowerInvariant();
            string snippet = CleanText(result.Snippet).ToLowerInvariant();
            string host = GetHostName(result.Url);
            if (IsBroadNewsIntent(intent) && IsTrustedHost(host))
                return false;
            if (intent.News && snippet.Contains("headline:", StringComparison.Ordinal))
                return false;

            string path = GetUrlPath(result.Url);
            bool genericTitle = GenericNewsLandingTitleMarkers.Any(marker => title.Contains(marker, StringComparison.Ordinal));
            bool genericSnippet = GenericNewsLandingSnippetMarkers.Any(marker => snippet.Contains(marker, StringComparison.Ordinal));
            int coverage = ComputeTermCoverage(result, intent);
            double overlap = ComputeWordOverlap((result.Title ?? string.Empty) + " " + (result.Snippet ?? string.Empty), intent.BasePrompt);

            if (string.IsNullOrWhiteSpace(path))
                return genericTitle || genericSnippet;

            string trimmedPath = path.Trim('/');
            bool rootOrSectionPath = string.IsNullOrWhiteSpace(trimmedPath)
                || Regex.IsMatch(trimmedPath, @"^(news|latest|latest-news|top-news|top-stories|headlines|world|us|politics|business|health|tech|technology|sport|sports|video|videos)$", RegexOptions.IgnoreCase);

            bool sourceBoilerplate = !string.IsNullOrWhiteSpace(host)
                && snippet.Contains(host.Replace(".", " ", StringComparison.Ordinal).ToLowerInvariant(), StringComparison.Ordinal)
                && (snippet.Contains("latest news", StringComparison.Ordinal) || snippet.Contains("breaking news", StringComparison.Ordinal));

            return (genericTitle || genericSnippet || sourceBoilerplate)
                && rootOrSectionPath
                && coverage <= 2
                && overlap < 0.35;
        }

        private static string GetReliabilityLabel(SearchResult result)
        {
            if (IsTrustedAuthority(result))
                return "High confidence";
            if (IsBlockedAuthority(result))
                return "Low confidence";
            return "Medium confidence";
        }

        private static string GetFirstSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (TryExtractStructuredField(text, "Headline", out string headline))
                return headline;

            string cleaned = CleanText(text);
            int sentenceBreak = cleaned.IndexOfAny(['.', '!', '?']);
            return sentenceBreak >= 0 ? cleaned[..(sentenceBreak + 1)] : cleaned;
        }

        private static double ComputeWordOverlap(string left, string right)
        {
            HashSet<string> leftWords = TokenizeForOverlap(left);
            HashSet<string> rightWords = TokenizeForOverlap(right);
            if (leftWords.Count == 0 || rightWords.Count == 0)
                return 0;

            int intersection = leftWords.Intersect(rightWords, StringComparer.OrdinalIgnoreCase).Count();
            int union = leftWords.Union(rightWords, StringComparer.OrdinalIgnoreCase).Count();
            return union == 0 ? 0 : (double)intersection / union;
        }

        private static HashSet<string> TokenizeForOverlap(string text)
        {
            return TokenRegex.Matches(text ?? string.Empty)
                .Select(m => m.Value.ToLowerInvariant())
                .Where(t => t.Length > 2 && !OverlapStopWords.Contains(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<SearchResult> ParseResults(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var list = new List<SearchResult>();
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'result')]")
                             ?? doc.DocumentNode.SelectNodes("//table//tr");

            if (resultNodes == null)
                return list;

            foreach (var node in resultNodes)
            {
                if (list.Count >= MaxResultsPerQuery)
                    break;

                var titleNode = node.SelectSingleNode(".//a[contains(@class,'result__a')]")
                               ?? node.SelectSingleNode(".//h2//a")
                               ?? node.SelectSingleNode(".//a");

                var snippetNode = node.SelectSingleNode(".//a[contains(@class,'result__snippet')]")
                                 ?? node.SelectSingleNode(".//div[contains(@class,'result__snippet')]")
                                 ?? node.SelectSingleNode(".//td[contains(@class,'result-snippet')]")
                                 ?? node.SelectSingleNode(".//td[@class='result-snippet']")
                                 ?? node.SelectSingleNode(".//div[contains(@class,'result__body')]");

                string title = CleanText(titleNode?.InnerText);
                string snippet = CleanText(snippetNode?.InnerText);
                string link = ExtractResultUrl(titleNode);

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(snippet))
                    continue;

                if (!seenTitles.Add(title))
                    continue;

                list.Add(new SearchResult(title, TruncateSnippet(snippet), link, int.MaxValue));
            }

            return list;
        }

        private static List<SearchResult> ParseBingHtmlResults(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var list = new List<SearchResult>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultNodes = doc.DocumentNode.SelectNodes("//li[contains(@class,'b_algo')]");
            if (resultNodes == null)
                return list;

            foreach (HtmlNode node in resultNodes)
            {
                if (list.Count >= MaxResultsPerQuery)
                    break;

                HtmlNode? titleNode = node.SelectSingleNode(".//h2//a")
                    ?? node.SelectSingleNode(".//a");
                HtmlNode? snippetNode = node.SelectSingleNode(".//div[contains(@class,'b_caption')]//p")
                    ?? node.SelectSingleNode(".//p");

                string title = CleanText(titleNode?.InnerText);
                string snippet = TruncateSnippet(CleanText(snippetNode?.InnerText));
                string link = ExtractBingResultUrl(titleNode);

                if (string.IsNullOrWhiteSpace(title)
                    || string.IsNullOrWhiteSpace(snippet)
                    || string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                string normalizedUrl = NormalizeUrl(link);
                if (!seenUrls.Add(normalizedUrl))
                    continue;

                list.Add(new SearchResult(title, snippet, link, int.MaxValue));
            }

            return list;
        }

        private static string ExtractResultUrl(HtmlNode? titleNode)
        {
            string href = titleNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(href))
                return string.Empty;

            if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
                return absoluteUri.ToString();

            if (href.StartsWith("//", StringComparison.Ordinal))
                return "https:" + href;

            if (href.StartsWith("/l/?uddg=", StringComparison.OrdinalIgnoreCase))
            {
                int uddgIndex = href.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
                if (uddgIndex >= 0)
                {
                    string encoded = href[(uddgIndex + 5)..];
                    int ampIndex = encoded.IndexOf('&');
                    if (ampIndex >= 0)
                        encoded = encoded[..ampIndex];
                    return Uri.UnescapeDataString(encoded);
                }
            }

            return string.Empty;
        }

        private static string NormalizeFeedTitle(string title)
        {
            string cleaned = CleanText(title);
            if (string.IsNullOrWhiteSpace(cleaned))
                return string.Empty;

            int separatorIndex = cleaned.LastIndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex > 24)
                return cleaned[..separatorIndex].Trim();

            return cleaned;
        }

        private static string ExtractSourceNameFromFeedTitle(string title)
        {
            string cleaned = CleanText(title);
            if (string.IsNullOrWhiteSpace(cleaned))
                return string.Empty;

            int separatorIndex = cleaned.LastIndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex > 0 && separatorIndex + 3 < cleaned.Length)
                return cleaned[(separatorIndex + 3)..].Trim();

            return string.Empty;
        }

        private static string BuildFeedSnippet(string title, string description, string sourceName, DateTimeOffset? publishedAt)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(title))
                parts.Add("Headline: " + title.Trim());

            string cleanedDescription = StripFeedTitlePrefix(description, title);
            if (!string.IsNullOrWhiteSpace(cleanedDescription))
                parts.Add(TruncateSnippet(cleanedDescription));

            if (!string.IsNullOrWhiteSpace(sourceName))
                parts.Add("Source: " + sourceName.Trim());

            if (publishedAt.HasValue)
                parts.Add("Published: " + publishedAt.Value.UtcDateTime.ToString("yyyy-MM-dd"));

            return string.Join(" ", parts);
        }

        private static string StripFeedTitlePrefix(string description, string title)
        {
            string cleanedDescription = CleanText(description);
            if (string.IsNullOrWhiteSpace(cleanedDescription))
                return string.Empty;

            string cleanedTitle = CleanText(title);
            if (!string.IsNullOrWhiteSpace(cleanedTitle)
                && cleanedDescription.StartsWith(cleanedTitle, StringComparison.OrdinalIgnoreCase))
            {
                cleanedDescription = cleanedDescription[cleanedTitle.Length..].TrimStart(' ', '-', ':', '–', '|');
            }

            return cleanedDescription;
        }

        private static string ExtractBingResultUrl(HtmlNode? titleNode)
        {
            string href = titleNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(href))
                return string.Empty;

            if (Uri.TryCreate(href, UriKind.Absolute, out Uri? absoluteUri))
                return absoluteUri.ToString();

            if (href.StartsWith("//", StringComparison.Ordinal))
                return "https:" + href;

            return string.Empty;
        }

        private static string TruncateSnippet(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            const int maxSnippetChars = 600;
            if (text.Length <= maxSnippetChars)
                return text;

            int wordBoundary = text.LastIndexOf(' ', maxSnippetChars);
            if (wordBoundary <= 0)
                wordBoundary = maxSnippetChars;

            return text[..wordBoundary].TrimEnd();
        }

        private static string TruncateEvidence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            const int maxEvidenceChars = 900;
            return text.Length > maxEvidenceChars
                ? text[..maxEvidenceChars].TrimEnd()
                : text;
        }

        private static async Task<PageEvidence> TryFetchPageEvidenceAsync(string url, string title, SearchIntent intent, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return new PageEvidence(string.Empty, string.Empty, null, false);

            try
            {
                // Use a longer timeout for news/current-info queries — these often require redirect
                // chains (e.g. news.google.com → actual article site) which take more round trips.
                using var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutTokenSource.CancelAfter((intent.News || intent.CurrentInfo) ? CurrentEvidenceDeadline : EvidenceDeadline);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutTokenSource.Token).ConfigureAwait(false);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    return new PageEvidence(string.Empty, string.Empty, null, false);

                string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(mediaType) && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return new PageEvidence(string.Empty, string.Empty, null, false);

                string html = await ReadContentAsStringBoundedAsync(response.Content, MaxEvidencePayloadChars, timeoutTokenSource.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                    return new PageEvidence(string.Empty, string.Empty, null, false);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                DateTimeOffset? publishedAt = ExtractPublishedDate(doc);
                string pageTitle = CleanText(doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
                string host = GetHostName(url);

                RemoveNonContentNodes(doc);

                // Priority 1: semantic article containers give focused article body text.
                // Priority 2: fall back to document-wide heading/paragraph search.
                const string ArticleXPath =
                    "//article|//main//div[contains(@class,'article')]|//main//div[contains(@class,'story')]" +
                    "|//div[contains(@class,'article-body')]|//div[contains(@class,'article-content')]" +
                    "|//div[contains(@class,'story-body')]|//div[contains(@class,'entry-content')]" +
                    "|//div[contains(@class,'post-content')]|//div[@id='article-body']|//main";

                HtmlNode? articleContainer = doc.DocumentNode.SelectSingleNode(ArticleXPath);
                HtmlNodeCollection? rawNodes = articleContainer != null
                    ? articleContainer.SelectNodes(".//h1|.//h2|.//h3|.//p")
                    : doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6|//p");

                var textNodes = (rawNodes ?? new HtmlNodeCollection(null))
                    .Select(node => CleanText(node.InnerText))
                    .Where(text => !string.IsNullOrWhiteSpace(text) && text.Length >= 20)
                    .Where(text => !LooksLikeBoilerplateEvidence(text, host, title))
                    .ToList();

                if (textNodes.Count == 0)
                    return new PageEvidence(string.Empty, string.Empty, null, false);

                string evidenceText = TruncateExtractedPageText(string.Join(Environment.NewLine, textNodes));
                if (string.IsNullOrWhiteSpace(evidenceText))
                    return new PageEvidence(string.Empty, string.Empty, null, false);

                return new PageEvidence(evidenceText, pageTitle, publishedAt, true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new PageEvidence(string.Empty, string.Empty, null, false);
            }
        }

        private static void RemoveNonContentNodes(HtmlDocument doc)
        {
            HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//script|//style|//nav|//header|//footer|//aside|//form");
            if (nodes == null)
                return;

            foreach (HtmlNode node in nodes.ToArray())
                node.Remove();
        }

        private static string TruncateExtractedPageText(string text)
        {
            string normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            const int maxChars = 2500;
            if (normalized.Length <= maxChars)
                return normalized;

            int lastSentenceBoundary = normalized.LastIndexOfAny(['.', '!', '?'], maxChars - 1, maxChars);
            if (lastSentenceBoundary >= 200)
                return normalized[..(lastSentenceBoundary + 1)].Trim();

            int lastWhitespace = normalized.LastIndexOf(' ', maxChars);
            if (lastWhitespace >= 200)
                return normalized[..lastWhitespace].Trim();

            return normalized[..maxChars].Trim();
        }

        private static int ScoreEvidenceCandidate(string text, SearchIntent intent, string title)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            int score = 0;
            string lower = text.ToLowerInvariant();
            string lowerTitle = (title ?? string.Empty).ToLowerInvariant();
            int termHits = 0;

            foreach (string term in intent.QueryTerms.Take(8))
            {
                string normalizedTerm = term.ToLowerInvariant();
                if (lower.Contains(normalizedTerm, StringComparison.Ordinal))
                {
                    score += normalizedTerm.Contains(' ') ? 5 : 3;
                    termHits++;
                }

                if (!string.IsNullOrWhiteSpace(lowerTitle) && lowerTitle.Contains(normalizedTerm, StringComparison.Ordinal) && lower.Contains(normalizedTerm, StringComparison.Ordinal))
                    score += 1;
            }

            score += (int)Math.Round(ComputeWordOverlap(text, intent.BasePrompt) * 24);

            if (intent.CurrentInfo && FreshnessYearRegex.IsMatch(text))
                score += 2;
            if (intent.News && lower.Contains("headline:", StringComparison.Ordinal))
                score += 5;
            if (intent.News && lower.Contains("published:", StringComparison.Ordinal))
                score += 2;
            if (Regex.IsMatch(text, @"\b\d+(?:\.\d+)?%?\b"))
                score += 1;
            if (text.Length >= 90 && text.Length <= 320)
                score += 1;
            if (termHits == 0)
                score -= 4;

            return score;
        }

        private static List<(string Text, int Score)> ExtractHeadlineEvidenceCandidates(HtmlDocument doc, SearchIntent intent, string host, string title)
        {
            var headlineNodes = doc.DocumentNode.SelectNodes("//article//h1|//article//h2|//article//h3|//main//h1|//main//h2|//main//h3|//section//h2|//section//h3|//article//a|//main//a|//section//a|//h2|//h3");
            if (headlineNodes == null || headlineNodes.Count == 0)
                return new List<(string Text, int Score)>();

            var headlines = new List<string>();
            foreach (string headline in headlineNodes
                .Select(node => CleanText(node.InnerText))
                .Where(text => text.Length >= 24 && text.Length <= 180)
                .Where(text => Regex.Matches(text, @"\b[A-Za-z]{3,}\b").Count >= 4)
                .Where(text => !LooksLikeBoilerplateEvidence(text, host, title))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (headlines.Any(existing => ComputeWordOverlap(existing, headline) > 0.82))
                    continue;

                headlines.Add(headline);
                if (headlines.Count >= 5)
                    break;
            }

            if (headlines.Count == 0)
                return new List<(string Text, int Score)>();

            string prefix = IsBroadNewsIntent(intent)
                ? "Top verified headlines from this source: "
                : "Relevant reported developments: ";
            string combined = prefix + string.Join("; ", headlines.Take(4));
            int score = ScoreEvidenceCandidate(combined, intent, title) + headlines.Count + (IsTrustedHost(host) ? 2 : 0);
            return new List<(string Text, int Score)> { (combined, score) };
        }

        private static bool LooksLikeBoilerplateEvidence(string text, string host, string title)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            string lower = text.ToLowerInvariant();
            if (BoilerplateEvidenceMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal)))
                return true;

            if (lower.StartsWith("photo:", StringComparison.Ordinal)
                || lower.StartsWith("image:", StringComparison.Ordinal)
                || lower.StartsWith("copyright", StringComparison.Ordinal)
                || lower.StartsWith("advertisement", StringComparison.Ordinal)
                || lower.Contains("javascript", StringComparison.Ordinal)
                || lower.Contains("enable cookies", StringComparison.Ordinal))
            {
                return true;
            }

            string normalizedTitle = CleanText(title);
            if (!string.IsNullOrWhiteSpace(normalizedTitle) && ComputeWordOverlap(text, normalizedTitle) >= 0.90)
                return true;

            if (!string.IsNullOrWhiteSpace(host))
            {
                string hostLabel = host.Replace(".", " ", StringComparison.Ordinal).ToLowerInvariant();
                if (hostLabel.Length > 4 && lower.Contains(hostLabel, StringComparison.Ordinal) && lower.Contains("source", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static List<string> BuildEvidenceDigest(IReadOnlyList<SearchResult> results, SearchIntent intent)
        {
            var bullets = new List<string>();

            foreach (SearchResult result in results
                .OrderByDescending(r => ScoreResult(r, intent))
                .ThenBy(r => r.Position))
            {
                string host = GetResultAuthorityDisplayName(result);
                string dateLabel = GetFreshnessLabel(result.Snippet, result.PublishedAt);
                string attribution = string.IsNullOrWhiteSpace(host) ? dateLabel : $"{host} {dateLabel}";

                // For news/current-info queries extract multiple high-value sentences so the AI has
                // enough article-body context to synthesize a real answer rather than just repeating
                // headline metadata. For all other queries one sentence is sufficient.
                IEnumerable<string> sentences;
                if ((intent.News || intent.CurrentInfo) && result.Snippet?.Length > 300)
                    sentences = ExtractTopEvidenceSentences(result.Snippet, intent, maxSentences: 3);
                else
                    sentences = [ExtractMostRelevantSentence(result.Snippet, intent)];

                foreach (string sentence in sentences)
                {
                    if (string.IsNullOrWhiteSpace(sentence))
                        continue;

                    string bullet = string.IsNullOrWhiteSpace(attribution)
                        ? sentence
                        : $"{sentence} ({attribution})";

                    if (bullets.Any(existing => ComputeWordOverlap(existing, bullet) > 0.75))
                        continue;

                    bullets.Add(bullet);
                    if (bullets.Count >= 6)
                        return bullets;
                }
            }

            return bullets;
        }

        private static IEnumerable<string> ExtractTopEvidenceSentences(string text, SearchIntent intent, int maxSentences)
        {
            if (string.IsNullOrWhiteSpace(text))
                return [];

            // For structured feed snippets (Headline/Source/Published) the headline is the evidence.
            if (intent.News && TryBuildStructuredHeadlineEvidence(text, out string structuredEvidence))
                return [structuredEvidence];

            var sentences = Regex.Split(CleanText(text), @"(?<=[\.!?])\s+")
                .Select(s => s.Trim())
                .Where(s => s.Length >= 30)
                .ToList();

            if (sentences.Count == 0)
                return [];

            // Score each sentence and take the top N, then re-order by position (lede first).
            return sentences
                .Select((s, idx) => (Sentence: s, Score: ScoreEvidenceCandidate(s, intent, string.Empty), Index: idx))
                .OrderByDescending(x => x.Score)
                .Take(maxSentences)
                .OrderBy(x => x.Index)
                .Select(x => x.Sentence);
        }

        private static bool IsBroadNewsIntent(SearchIntent intent)
        {
            if (!intent.News)
                return false;

            int topicTerms = intent.QueryTerms
                .Where(term => !IsYearToken(term))
                .Select(term => term.Trim())
                .Count(term => term.Length >= 2
                    && !GenericBroadNewsTerms.Contains(term)
                    && !RegionalFocusMarkers.Any(marker => string.Equals(marker, term, StringComparison.OrdinalIgnoreCase)));

            int promptTokenCount = TokenRegex.Matches(intent.BasePrompt ?? string.Empty).Count;
            return topicTerms == 0 && promptTokenCount <= 8;
        }

        private static bool IsBroadNewsPrompt(string selectedSentence, IReadOnlyList<string> queryTerms)
        {
            var intent = new SearchIntent(string.Empty, selectedSentence ?? string.Empty, false, false, false, true, queryTerms);
            return IsBroadNewsIntent(intent);
        }

        private static List<SearchResult> SelectBroadNewsResults(IEnumerable<SearchResult> preferred, IEnumerable<SearchResult> fallback, SearchIntent intent)
        {
            var selected = new List<SearchResult>();
            var seenAuthorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenHeadlines = new List<string>();

            void AddFrom(IEnumerable<SearchResult> source)
            {
                foreach (SearchResult result in source
                    .OrderByDescending(r => ScoreResult(r, intent))
                    .ThenBy(r => r.Position))
                {
                    string authorityKey = GetResultAuthorityKey(result);
                    string headline = NormalizeAuthorityValue(result.Title);

                    if (!string.IsNullOrWhiteSpace(authorityKey) && !seenAuthorities.Add(authorityKey))
                        continue;

                    if (!string.IsNullOrWhiteSpace(headline) && seenHeadlines.Any(existing => ComputeWordOverlap(existing, headline) > 0.72))
                    {
                        if (!string.IsNullOrWhiteSpace(authorityKey))
                            seenAuthorities.Remove(authorityKey);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(headline))
                        seenHeadlines.Add(headline);

                    selected.Add(result);
                    if (selected.Count >= Math.Min(MaxResultCount, 4))
                        return;
                }
            }

            AddFrom(preferred);
            if (selected.Count < 3)
                AddFrom(fallback);

            return selected;
        }

        private static string GetResultAuthorityDisplayName(SearchResult result)
        {
            string sourceName = ExtractSourceNameFromSnippet(result.Snippet);
            if (!string.IsNullOrWhiteSpace(sourceName))
                return sourceName;

            return GetHostName(result.Url);
        }

        private static string GetResultAuthorityKey(SearchResult result)
        {
            string sourceName = ExtractSourceNameFromSnippet(result.Snippet);
            if (!string.IsNullOrWhiteSpace(sourceName))
                return NormalizeAuthorityValue(sourceName);

            return NormalizeAuthorityValue(GetHostName(result.Url));
        }

        private static string ExtractSourceNameFromSnippet(string snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet))
                return string.Empty;

            Match match = Regex.Match(snippet, @"\bSource:\s*(?<source>.+?)(?=\s+Published:\s|$)", RegexOptions.IgnoreCase);
            return match.Success ? CleanText(match.Groups["source"].Value) : string.Empty;
        }

        private static bool MatchesAuthorityList(string authorityKey, IEnumerable<string> authorities)
        {
            if (string.IsNullOrWhiteSpace(authorityKey))
                return false;

            foreach (string authority in authorities)
            {
                string normalizedAuthority = NormalizeAuthorityValue(authority);
                if (string.IsNullOrWhiteSpace(normalizedAuthority))
                    continue;

                if (authorityKey.Contains(normalizedAuthority, StringComparison.Ordinal)
                    || normalizedAuthority.Contains(authorityKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeAuthorityValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.ToLowerInvariant();
            normalized = normalized.Replace("https://", string.Empty, StringComparison.Ordinal)
                .Replace("http://", string.Empty, StringComparison.Ordinal)
                .Replace("www.", string.Empty, StringComparison.Ordinal)
                .Replace("the ", string.Empty, StringComparison.Ordinal);

            return Regex.Replace(normalized, @"[^a-z0-9]", string.Empty);
        }

        private static bool ShouldSkipPageEvidenceFetch(string host, bool structuredFeedSnippet)
        {
            // Never skip — aggregator-host URLs (e.g. news.google.com) redirect via HttpClient's
            // built-in redirect following to the real article page. Skipping them prevented article
            // body text from ever reaching the AI for the most common news-query result format.
            return false;
        }

        private static bool LooksLikeStructuredFeedSnippet(string snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet))
                return false;

            return snippet.Contains("Headline:", StringComparison.OrdinalIgnoreCase)
                && (snippet.Contains("Source:", StringComparison.OrdinalIgnoreCase)
                    || snippet.Contains("Published:", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldReplaceSnippetWithFetchedEvidence(string originalSnippet, string fetchedSnippet, SearchIntent intent, string title, string host)
        {
            string original = originalSnippet?.Trim() ?? string.Empty;
            string fetched = fetchedSnippet?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fetched))
                return false;

            if (string.IsNullOrWhiteSpace(original))
                return true;

            // If the original is only a metadata stub (Headline/Source/Published with no body text),
            // accept any non-empty fetched content — even boilerplate beats a datestamp-only snippet.
            bool originalStructured = LooksLikeStructuredFeedSnippet(original);
            if (originalStructured && fetched.Length > original.Length)
                return true;

            // If the original is very short (< 120 chars), a longer fetch is almost always better.
            if (original.Length < 120 && fetched.Length > original.Length * 2)
                return true;

            bool fetchedStructured = LooksLikeStructuredFeedSnippet(fetched);
            bool aggregatorHost = !string.IsNullOrWhiteSpace(host) && AggregatorSourceHosts.Any(host.EndsWith);

            if (aggregatorHost && originalStructured && !fetchedStructured)
                return false;

            int originalScore = ScoreEvidenceCandidate(original, intent, title);
            int fetchedScore = ScoreEvidenceCandidate(fetched, intent, title);

            if (originalStructured && !fetchedStructured && fetchedScore <= originalScore + 2)
                return false;

            if (TryExtractStructuredField(original, "Headline", out string originalHeadline)
                && !ContainsApproximateText(fetched, originalHeadline)
                && fetchedScore <= originalScore + 4)
            {
                return false;
            }

            return fetchedScore > originalScore;
        }

        private static bool TryBuildStructuredHeadlineEvidence(string text, out string evidence)
        {
            evidence = string.Empty;
            if (!TryExtractStructuredField(text, "Headline", out string headline))
                return false;

            TryExtractStructuredField(text, "Source", out string source);
            TryExtractStructuredField(text, "Published", out string published);

            evidence = headline.Trim();
            if (!string.IsNullOrWhiteSpace(source))
                evidence += " (" + source.Trim();
            if (!string.IsNullOrWhiteSpace(published))
                evidence += string.IsNullOrWhiteSpace(source) ? " (" + published.Trim() : ", " + published.Trim();
            if (!string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(published))
                evidence += ")";

            return true;
        }

        private static bool TryExtractStructuredField(string text, string fieldName, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fieldName))
                return false;

            Match match = Regex.Match(text, $@"\b{Regex.Escape(fieldName)}:\s*(?<value>.+?)(?=\s+(?:Headline|Source|Published):\s|$)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            value = CleanText(match.Groups["value"].Value);
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool ContainsApproximateText(string haystack, string needle)
        {
            if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
                return false;

            string normalizedHaystack = NormalizeAuthorityValue(haystack);
            string normalizedNeedle = NormalizeAuthorityValue(needle);
            if (string.IsNullOrWhiteSpace(normalizedHaystack) || string.IsNullOrWhiteSpace(normalizedNeedle))
                return false;

            return normalizedHaystack.Contains(normalizedNeedle, StringComparison.Ordinal)
                || ComputeWordOverlap(haystack, needle) >= 0.55;
        }

        private static bool HasExplicitRegionalFocus(string text)
        {
            string lower = (text ?? string.Empty).ToLowerInvariant();
            return RegionalFocusMarkers.Any(lower.Contains);
        }

        private static bool LooksLikeBroadNewsText(string text)
        {
            string lower = (text ?? string.Empty).ToLowerInvariant();
            return lower.Contains("latest news", StringComparison.Ordinal)
                || lower.Contains("breaking news", StringComparison.Ordinal)
                || lower.Contains("top headlines", StringComparison.Ordinal)
                || lower.Contains("latest headlines", StringComparison.Ordinal)
                || string.Equals(lower.Trim(), "news", StringComparison.Ordinal)
                || string.Equals(lower.Trim(), "latest news", StringComparison.Ordinal)
                || string.Equals(lower.Trim(), "headlines", StringComparison.Ordinal);
        }

        private static string ExtractMostRelevantSentence(string text, SearchIntent intent)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (intent.News && TryBuildStructuredHeadlineEvidence(text, out string structuredHeadlineEvidence))
                return structuredHeadlineEvidence;

            List<string> sentences = Regex.Split(CleanText(text), @"(?<=[\.!?])\s+")
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (sentences.Count == 0)
                return string.Empty;

            return sentences
                .OrderByDescending(sentence => ScoreEvidenceCandidate(sentence, intent, string.Empty))
                .ThenByDescending(sentence => ComputeWordOverlap(sentence, intent.BasePrompt))
                .FirstOrDefault() ?? string.Empty;
        }

        private async Task<List<SearchResult>> FetchAlternateFreshResultsAsync(SearchIntent intent, CancellationToken token)
        {
            var alternateQueries = new List<string>();

            void Add(string value)
            {
                string candidate = NormalizeQuery(value);
                if (!string.IsNullOrWhiteSpace(candidate) && !alternateQueries.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    alternateQueries.Add(candidate);
            }

            string leadingTerms = string.Join(' ', intent.QueryTerms.Take(4));
            if (!string.IsNullOrWhiteSpace(leadingTerms))
            {
                Add(leadingTerms + " " + DateTime.UtcNow.Year + " article");
                Add(leadingTerms + " today article");
                Add(leadingTerms + " reported developments");
            }

            if (!string.IsNullOrWhiteSpace(intent.BasePrompt))
                Add(intent.BasePrompt + " latest article");

            var aggregated = new List<SearchResult>();
            int position = 0;

            foreach (string query in alternateQueries.Take(4))
            {
                List<SearchResult> results = await SearchAcrossProvidersAsync(query, intent, token).ConfigureAwait(false);

                foreach (SearchResult result in results)
                    aggregated.Add(result with { Position = position++ });
            }

            List<SearchResult> selected = SelectHighConfidenceResults(
                DeduplicateResults(aggregated)
                    .OrderByDescending(r => ScoreResult(r, intent))
                    .ThenBy(r => r.Position)
                    .ToList(),
                intent);

            if (selected.Count == 0)
                return new List<SearchResult>();

            return selected;
        }

        private static bool ShouldRetryWithFreshnessBias(IReadOnlyList<SearchResult> results, SearchIntent intent)
        {
            if ((!intent.CurrentInfo && !intent.News) || results.Count == 0)
                return false;

            int freshCount = results.Count(r => IsFreshEnough(r.PublishedAt, intent));
            int highSignalCount = results.Count(r => ComputeWordOverlap((r.Title ?? string.Empty) + " " + (r.Snippet ?? string.Empty), intent.BasePrompt) >= 0.12);
            return freshCount < Math.Min(2, results.Count) || highSignalCount == 0;
        }

        private static bool IsFreshEnough(DateTimeOffset? publishedAt, SearchIntent intent)
        {
            if (!publishedAt.HasValue)
                return false;

            TimeSpan age = DateTimeOffset.UtcNow - publishedAt.Value;
            if (intent.News)
                return age <= TimeSpan.FromDays(10);
            if (intent.CurrentInfo)
                return age <= TimeSpan.FromDays(30);
            return true;
        }

        private static int ScoreRecency(DateTimeOffset publishedAt, SearchIntent intent)
        {
            TimeSpan age = DateTimeOffset.UtcNow - publishedAt;
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;

            if (intent.News)
            {
                if (age <= TimeSpan.FromDays(2)) return 8;
                if (age <= TimeSpan.FromDays(7)) return 6;
                if (age <= TimeSpan.FromDays(10)) return 3;
                if (age >= TimeSpan.FromDays(90)) return -6;
            }

            if (intent.CurrentInfo)
            {
                if (age <= TimeSpan.FromDays(7)) return 6;
                if (age <= TimeSpan.FromDays(21)) return 4;
                if (age <= TimeSpan.FromDays(30)) return 2;
                if (age >= TimeSpan.FromDays(120)) return -5;
            }

            return age <= TimeSpan.FromDays(30) ? 1 : 0;
        }

        private static DateTimeOffset? ExtractPublishedDate(HtmlDocument doc)
        {
            string[] selectors =
            [
                "//meta[@property='article:published_time']",
                "//meta[@name='article:published_time']",
                "//meta[@property='og:published_time']",
                "//meta[@name='pubdate']",
                "//meta[@name='publishdate']",
                "//meta[@name='date']",
                "//time[@datetime]"
            ];

            foreach (string selector in selectors)
            {
                HtmlNode? node = doc.DocumentNode.SelectSingleNode(selector);
                string raw = selector.Contains("time[@datetime]", StringComparison.Ordinal)
                    ? node?.GetAttributeValue("datetime", string.Empty) ?? string.Empty
                    : node?.GetAttributeValue("content", string.Empty) ?? string.Empty;

                DateTimeOffset? parsed = TryParseDate(raw);
                if (parsed.HasValue)
                    return parsed;
            }

            return null;
        }

        private static DateTimeOffset? TryParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string text = CleanText(raw);
            if (DateTimeOffset.TryParse(text, out DateTimeOffset parsed))
                return parsed;

            Match isoMatch = IsoDateRegex.Match(text);
            if (isoMatch.Success && DateTime.TryParse(isoMatch.Value, out DateTime isoDate))
                return new DateTimeOffset(DateTime.SpecifyKind(isoDate, DateTimeKind.Utc));

            Match monthMatch = MonthDateRegex.Match(text);
            if (monthMatch.Success)
            {
                string normalized = monthMatch.Groups["year"].Success
                    ? monthMatch.Value
                    : monthMatch.Value + ", " + DateTime.UtcNow.Year;

                if (DateTime.TryParse(normalized, out DateTime monthDate))
                    return new DateTimeOffset(DateTime.SpecifyKind(monthDate, DateTimeKind.Utc));
            }

            return null;
        }

        private static string CleanText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            string decoded = HtmlEntity.DeEntitize(raw);
            var doc = new HtmlDocument();
            doc.LoadHtml(decoded);
            string text = doc.DocumentNode.InnerText ?? string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static bool TryGetCached(string query, SearchIntent intent, out string data)
        {
            data = string.Empty;
            if (!SearchCache.TryGetValue(query, out var item))
                return false;

            // Docs/reference lookups are stable content — hold them longer so repeated council
            // iterations on the same task reuse the evidence instead of re-searching.
            TimeSpan ttl = intent.CurrentInfo || intent.News
                ? CurrentInfoCacheTtl
                : intent.Docs || intent.Release
                    ? StableInfoCacheTtl
                    : CacheTtl;
            if (DateTimeOffset.UtcNow - item.SavedAt > ttl)
            {
                SearchCache.TryRemove(query, out _);
                return false;
            }

            data = item.Data;
            return !string.IsNullOrWhiteSpace(data);
        }

        private static void SaveCache(string query, string data)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(data))
                return;

            // Evict the 10 oldest entries when the cache exceeds 50 to prevent unbounded growth
            // in long-running sessions.
            if (SearchCache.Count >= 50)
            {
                var toEvict = SearchCache
                    .OrderBy(kvp => kvp.Value.SavedAt)
                    .Take(10)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (string key in toEvict)
                    SearchCache.TryRemove(key, out _);
            }

            SearchCache[query] = (data, DateTimeOffset.UtcNow);
        }

        private static string BuildCacheKey(string scope, string query)
        {
            return CacheVersionPrefix + CacheGenerationPrefix + scope + query;
        }
    }
}
