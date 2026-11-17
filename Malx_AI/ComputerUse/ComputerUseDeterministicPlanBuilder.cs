using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Malx_AI.ComputerUse
{
    /// <summary>
    /// Builds a deterministic, valid Computer Use outcome plan JSON whenever a local or cloud
    /// model fails to return a valid structured plan after retries. Keeps multi-step Computer Use
    /// sessions running cleanly without requiring fragile model JSON formatting.
    /// </summary>
    internal static class ComputerUseDeterministicPlanBuilder
    {
        private static readonly Regex ClauseSplitRegex = new(
            @"(?:\s*;\s*|\s*,\s*(?:and\s+)?then\s+|\s+and\s+then\s+|\s*,\s*then\s+|\s+then\s+|\s*,\s*(?=(?:open|launch|start|go|take|visit|navigate|type|write|click|search|play|draw|close|focus|switch)\b)|\s+and\s+(?=(?:open|launch|start|go|take|visit|navigate|type|write|click|search|play|draw)\b))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExplicitUrlRegex = new(
            @"https?://[^\s'""<>]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DomainRegex = new(
            @"\b(?<domain>(?:[a-z0-9-]+\.)+(?:com|org|net|io|dev|ai|edu|gov|co|uk|tv|gg)(?:/[^\s'""<>]*)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AppLaunchRegex = new(
            @"^\s*(?:please\s+)?(?:open|launch|start|focus|switch\s+to|bring\s+up|show)\s+(?:up\s+)?(?:the\s+)?(?<app>[A-Za-z0-9][A-Za-z0-9 .+\-_]{0,48}?)(?:\s+(?:app|application|browser|window))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QuotedTextRegex = new(
            @"(?:type|enter|write|input)\s+(?:""(?<q1>[^""]{1,200})""|'(?<q2>[^']{1,200})'|(?<q3>[A-Za-z0-9][^\r\n,;.]{0,120}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NewTabRegex = new(
            @"\b(?:new|another|separate|second|different)\s+tab\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SearchRegex = new(
            @"\bsearch\s+(?:for\s+)?(?<query>.+?)(?:\s+on\s+(?<site>google|bing|duckduckgo|youtube|github))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly (string Keyword, string AppName)[] KnownApps =
        [
            ("microsoft edge", "Microsoft Edge"),
            ("edge", "Microsoft Edge"),
            ("google chrome", "Google Chrome"),
            ("chrome", "Google Chrome"),
            ("firefox", "Firefox"),
            ("brave", "Brave"),
            ("notepad", "Notepad"),
            ("calculator", "Calculator"),
            ("paint", "Paint"),
            ("mspaint", "Paint"),
            ("file explorer", "File Explorer"),
            ("explorer", "File Explorer"),
            ("settings", "Settings"),
            ("windows terminal", "Windows Terminal"),
            ("terminal", "Windows Terminal"),
            ("powershell", "PowerShell"),
            ("cmd", "Command Prompt"),
            ("command prompt", "Command Prompt"),
            ("vscode", "Visual Studio Code"),
            ("vs code", "Visual Studio Code"),
            ("visual studio code", "Visual Studio Code"),
            ("task manager", "Task Manager"),
            ("spotify", "Spotify"),
            ("discord", "Discord")
        ];

        private static readonly (string Keyword, string Url, bool SiteMatch)[] KnownSites =
        [
            ("youtube", "https://www.youtube.com/", true),
            ("github", "https://github.com/", true),
            ("google", "https://www.google.com/", true),
            ("bing", "https://www.bing.com/", true),
            ("wikipedia", "https://www.wikipedia.org/", true),
            ("reddit", "https://www.reddit.com/", true),
            ("weather", "https://weather.com/", true),
            ("gmail", "https://mail.google.com/", true),
            ("outlook", "https://outlook.live.com/", true)
        ];

        internal sealed class FallbackOutcomeDto
        {
            public string request_text { get; set; } = string.Empty;
            public string description { get; set; } = string.Empty;
            public string kind { get; set; } = "desktop";
            public string destination { get; set; } = string.Empty;
            public string match { get; set; } = "exact";
            public bool separate_tab { get; set; }
            public string application { get; set; } = string.Empty;
            public List<string> required_text { get; set; } = new();
        }

        internal sealed class FallbackPlanDto
        {
            public List<FallbackOutcomeDto> outcomes { get; set; } = new();
            public string error { get; set; } = string.Empty;
        }

        public static string BuildFallbackPlanJson(string request, string? navigationContext = null)
        {
            string trimmedRequest = (request ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedRequest))
            {
                return JsonSerializer.Serialize(new FallbackPlanDto
                {
                    error = "No Computer Use goal was supplied."
                });
            }

            string verifiedNavUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(navigationContext))
                ComputerUseTrustedNavigation.TryGetVerifiedUrl(navigationContext, out verifiedNavUrl);

            List<string> rawClauses = SplitClausesOutsideQuotes(trimmedRequest);
            if (rawClauses.Count == 0)
                rawClauses.Add(trimmedRequest);

            var outcomes = new List<FallbackOutcomeDto>();
            bool seenBrowserOutcome = false;
            bool pendingSeparateTab = false;
            string activeApp = string.Empty;

            foreach (string rawClause in rawClauses)
            {
                string clause = rawClause.Trim().Trim(',', ';', '.');
                if (clause.Length == 0)
                    continue;

                bool hasNewTab = NewTabRegex.IsMatch(clause);
                string detectedApp = DetectKnownApp(clause);
                if (!string.IsNullOrEmpty(detectedApp))
                    activeApp = detectedApp;

                List<string> requiredText = ExtractQuotedOrTypedText(clause);

                // Check for URL / site navigation
                string destination = string.Empty;
                bool siteMatch = true;

                Match urlMatch = ExplicitUrlRegex.Match(clause);
                if (urlMatch.Success && ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(urlMatch.Value, out string parsedUrl))
                {
                    destination = parsedUrl;
                    siteMatch = false;
                }
                else if (!string.IsNullOrEmpty(verifiedNavUrl)
                    && (clause.Contains("repo", StringComparison.OrdinalIgnoreCase)
                        || clause.Contains("github", StringComparison.OrdinalIgnoreCase)))
                {
                    destination = verifiedNavUrl;
                    siteMatch = false;
                }
                else
                {
                    Match domainMatch = DomainRegex.Match(clause);
                    if (domainMatch.Success
                        && ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl("https://" + domainMatch.Groups["domain"].Value, out string domainUrl))
                    {
                        destination = domainUrl;
                        siteMatch = false;
                    }
                    else
                    {
                        foreach (var (keyword, siteUrl, isSite) in KnownSites)
                        {
                            if (Regex.IsMatch(clause, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase))
                            {
                                destination = siteUrl;
                                siteMatch = isSite;
                                break;
                            }
                        }

                        if (string.IsNullOrEmpty(destination))
                        {
                            Match searchMatch = SearchRegex.Match(clause);
                            if (searchMatch.Success)
                            {
                                string query = searchMatch.Groups["query"].Value.Trim();
                                if (query.Length > 0)
                                {
                                    destination = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
                                    siteMatch = true;
                                }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(destination))
                {
                    bool separateTab = (hasNewTab || pendingSeparateTab) && seenBrowserOutcome;
                    pendingSeparateTab = false;
                    outcomes.Add(new FallbackOutcomeDto
                    {
                        request_text = clause,
                        description = $"Open {destination} and complete: {clause}.",
                        kind = "browser",
                        destination = destination,
                        match = siteMatch ? "site" : "exact",
                        separate_tab = separateTab,
                        application = string.Empty,
                        required_text = requiredText
                    });
                    seenBrowserOutcome = true;
                    continue;
                }

                // Pure "open a new tab" clause standing alone before a later browser clause:
                // latch pendingSeparateTab so the next browser outcome receives separate_tab = true.
                if (hasNewTab && string.IsNullOrEmpty(detectedApp) && requiredText.Count == 0)
                {
                    pendingSeparateTab = true;
                    continue;
                }

                // Desktop app launch or desktop action
                string appName = !string.IsNullOrEmpty(detectedApp) ? detectedApp : activeApp;
                if (string.IsNullOrEmpty(appName))
                {
                    Match launchMatch = AppLaunchRegex.Match(clause);
                    if (launchMatch.Success)
                        appName = launchMatch.Groups["app"].Value.Trim();
                }

                // Merge text-only follow-up into previous desktop outcome for same app if applicable
                if (outcomes.Count > 0
                    && outcomes[^1].kind == "desktop"
                    && string.Equals(outcomes[^1].application, appName, StringComparison.OrdinalIgnoreCase)
                    && requiredText.Count > 0
                    && outcomes[^1].required_text.Count == 0
                    && !AppLaunchRegex.IsMatch(clause))
                {
                    outcomes[^1].request_text = $"{outcomes[^1].request_text}, {clause}";
                    outcomes[^1].description = $"{outcomes[^1].description} ({clause})";
                    outcomes[^1].required_text.AddRange(requiredText);
                    continue;
                }

                outcomes.Add(new FallbackOutcomeDto
                {
                    request_text = clause,
                    description = char.ToUpperInvariant(clause[0]) + clause[1..],
                    kind = "desktop",
                    destination = string.Empty,
                    match = "exact",
                    separate_tab = false,
                    application = appName,
                    required_text = requiredText
                });
            }

            if (outcomes.Count == 0)
            {
                outcomes.Add(new FallbackOutcomeDto
                {
                    request_text = trimmedRequest,
                    description = trimmedRequest,
                    kind = "desktop",
                    destination = string.Empty,
                    match = "exact",
                    separate_tab = false,
                    application = DetectKnownApp(trimmedRequest),
                    required_text = ExtractQuotedOrTypedText(trimmedRequest)
                });
            }

            return JsonSerializer.Serialize(new FallbackPlanDto
            {
                outcomes = outcomes,
                error = string.Empty
            });
        }

        private static List<string> SplitClausesOutsideQuotes(string text)
        {
            var result = new List<string>();
            // Replace quoted strings temporarily so commas/then inside quotes don't split
            var placeholders = new List<string>();
            string masked = Regex.Replace(text, @"""[^""]*""|'[^']*'", m =>
            {
                string token = $"__Q{placeholders.Count}__";
                placeholders.Add(m.Value);
                return token;
            });

            foreach (string part in ClauseSplitRegex.Split(masked))
            {
                string restored = part;
                for (int i = 0; i < placeholders.Count; i++)
                    restored = restored.Replace($"__Q{i}__", placeholders[i], StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(restored))
                    result.Add(restored.Trim());
            }

            return result;
        }

        private static string DetectKnownApp(string clause)
        {
            foreach (var (keyword, appName) in KnownApps)
            {
                if (Regex.IsMatch(clause, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase))
                    return appName;
            }
            return string.Empty;
        }

        private static List<string> ExtractQuotedOrTypedText(string clause)
        {
            var items = new List<string>();
            foreach (Match m in QuotedTextRegex.Matches(clause))
            {
                string candidate = m.Groups["q1"].Success ? m.Groups["q1"].Value
                    : m.Groups["q2"].Success ? m.Groups["q2"].Value
                    : m.Groups["q3"].Success ? m.Groups["q3"].Value.Trim()
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(candidate)
                    && !candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    && !items.Contains(candidate, StringComparer.Ordinal))
                {
                    items.Add(candidate);
                }
            }
            return items;
        }
    }
}
