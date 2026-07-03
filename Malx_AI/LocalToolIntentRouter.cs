using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>
    /// Pure string-logic core for local-model tool use: deterministic intent routing (the host
    /// picks the tool from the request — hallucination-free, works for every model size),
    /// semantic tool-query validation (a grammar guarantees valid JSON, not a sensible call),
    /// observation digesting (small models copy raw envelopes; they use plain facts),
    /// tolerant [PAUSE:] marker parsing, and tool-observation echo detection.
    /// No UI or inference dependencies — the whole class is exercised standalone by the
    /// tool-rails verification harness.
    /// </summary>
    internal static class LocalToolIntentRouter
    {
        public const string ToolCalculate = "CALCULATE";
        public const string ToolPythonMath = "PYTHON_MATH";
        public const string ToolRunSandbox = "RUN_SANDBOX";
        public const string ToolWebSearch = "WEB_SEARCH";
        public const string ToolHippocampus = "SEARCH_HIPPOCAMPUS";
        public const string ToolReadFile = "READ_FILE";
        public const string ToolSearchCodebase = "SEARCH_CODEBASE";
        public const string ToolListFiles = "LIST_FILES";

        public sealed record ToolIntent(string Tool, string Query, string Reason);

        public enum ValidationStrictness
        {
            /// <summary>Shape checks only — used for mid-generation pauses, where a model
            /// legitimately computes with values it derived itself.</summary>
            Structural,
            /// <summary>Shape checks plus request-grounding — used for preflight decisions,
            /// where an ungrounded query means the model invented the task.</summary>
            Grounded
        }

        private const int MaxQueryChars = 4000;

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "that", "this", "with", "from", "for", "are", "was", "were", "have",
            "has", "had", "will", "would", "could", "should", "into", "about", "your", "their",
            "them", "then", "than", "what", "when", "where", "which", "while", "does", "please",
            "make", "give", "want", "need", "using", "used", "just", "also", "some", "any"
        };

        // ── Deterministic intent routing ──────────────────────────────────────

        private static readonly Regex CurrentInfoRegex = new(
            @"\b(latest|current|today|yesterday|recent|newest|release notes|version|price|news|202[5-9])\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NumericSignalRegex = new(
            @"\d\s*(?:[+\-*/^=]|%|percent|mph|km/h|usd|dollars|kg|lbs|cm|mm|m2|m\^2|hours?|minutes?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ComputeVerbRegex = new(
            @"\b(calculate|compute|solve|convert|how many|how much|total|average|sum|rate|formula)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UnitConversionRegex = new(
            @"\d[\d.,]*\s*(gallons?|liters?|litres?|miles?|km|kilometers?|kilometres?|kg|kilograms?|lbs?|pounds?|ounces?|oz|celsius|fahrenheit|°\s?[cf]|meters?|metres?|feet|foot|ft|inches?|cm|mm|acres?|hectares?)\b[^\n]{0,30}\b(to|in|into|as)\b\s*\w+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReadFileIntentRegex = new(
            @"\b(open|read|show|inspect|review|look\s+at|what(?:'s|\s+is)\s+(?:in|inside)|contents?\s+of)\b[^\n]{0,50}?(?<file>[\w\-./\\]+\.[A-Za-z0-9]{1,5})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ListFilesIntentRegex = new(
            @"\b(list|show|enumerate|what)\b[^\n]{0,40}\b(files|file\s+structure|folder\s+structure|project\s+structure|directory\s+tree|file\s+tree)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CodebaseSymbolSearchRegex = new(
            @"\b(find|where|locate|search)\b[^\n]{0,60}?(?:(?<q>[`'""])(?<sym>[A-Za-z_][A-Za-z0-9_]{2,})\k<q>|\b(?:function|method|class|symbol|variable|definition\s+of)\s+(?<sym>[A-Za-z_][A-Za-z0-9_]{2,}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SessionMemoryIntentRegex = new(
            @"\b(last|previous|earlier|prior)\s+(session|chat|conversation)\b|do\s+you\s+remember|we\s+(talked|discussed|worked\s+on|built|made)\b[^\n]{0,30}\b(earlier|before|previously|last\s+time)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FencedCodeRegex = new(
            @"```(?:[a-zA-Z0-9]*)\r?\n(?<code>[\s\S]*?)```",
            RegexOptions.Compiled);

        private static readonly Regex RunCodeVerbRegex = new(
            @"\b(run|execute|test|check|verify|try)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Routes a request to a tool deterministically. Most-specific intents win; unmatched
        /// requests return false (no tool — the model answers directly). This is the ONLY
        /// preflight tool source for sub-1B models and the first source for compact models.
        /// </summary>
        public static bool TryRouteIntent(
            string request,
            bool webSearchEnabled,
            bool codebaseToolsEnabled,
            out ToolIntent? intent)
        {
            intent = null;
            string objective = (request ?? string.Empty).Trim();
            if (objective.Length == 0)
                return false;
            if (objective.Length > MaxQueryChars)
                objective = objective[..MaxQueryChars];

            if (codebaseToolsEnabled)
            {
                Match readFile = ReadFileIntentRegex.Match(objective);
                if (readFile.Success)
                {
                    intent = new ToolIntent(ToolReadFile, readFile.Groups["file"].Value.Trim(), "file-read intent");
                    return true;
                }

                if (ListFilesIntentRegex.IsMatch(objective))
                {
                    intent = new ToolIntent(ToolListFiles, "*", "file-listing intent");
                    return true;
                }

                Match symbolSearch = CodebaseSymbolSearchRegex.Match(objective);
                if (symbolSearch.Success)
                {
                    intent = new ToolIntent(ToolSearchCodebase, symbolSearch.Groups["sym"].Value.Trim(), "symbol-search intent");
                    return true;
                }
            }

            if (webSearchEnabled && CurrentInfoRegex.IsMatch(objective))
            {
                intent = new ToolIntent(ToolWebSearch, objective, "current-information intent");
                return true;
            }

            bool looksNumeric = NumericSignalRegex.IsMatch(objective) && ComputeVerbRegex.IsMatch(objective);
            if (looksNumeric || UnitConversionRegex.IsMatch(objective))
            {
                intent = new ToolIntent(ToolCalculate, objective, "numeric/conversion intent");
                return true;
            }

            Match fencedCode = FencedCodeRegex.Match(objective);
            if (fencedCode.Success && RunCodeVerbRegex.IsMatch(objective))
            {
                string code = fencedCode.Groups["code"].Value.Trim();
                if (code.Length > 0)
                {
                    intent = new ToolIntent(ToolRunSandbox, code, "run-snippet intent");
                    return true;
                }
            }

            if (SessionMemoryIntentRegex.IsMatch(objective))
            {
                intent = new ToolIntent(ToolHippocampus, objective, "session-memory intent");
                return true;
            }

            return false;
        }

        // ── Semantic tool-query validation ────────────────────────────────────

        /// <summary>
        /// Validates that a tool query is a sensible call, not just valid JSON. Returns false
        /// with a SPECIFIC error suitable for a repair prompt. Unknown/absent evidence fails
        /// open (a missing workspace file list never rejects READ_FILE) — validation must not
        /// take tools away from models that use them correctly.
        /// </summary>
        public static bool TryValidateToolQuery(
            string tool,
            string query,
            string groundingContext,
            ValidationStrictness strictness,
            IReadOnlyCollection<string>? knownWorkspaceFiles,
            out string error)
        {
            error = string.Empty;
            string normalizedTool = NormalizeToolName(tool);
            string normalizedQuery = (query ?? string.Empty).Trim();

            if (normalizedQuery.Length == 0)
            {
                error = "The tool query is empty. Provide a standalone query.";
                return false;
            }

            if (normalizedQuery.Length > MaxQueryChars)
            {
                error = $"The tool query exceeds {MaxQueryChars} characters. Send a focused query.";
                return false;
            }

            if (normalizedTool == ToolReadFile)
            {
                if (normalizedQuery.Contains("..", StringComparison.Ordinal))
                {
                    error = "READ_FILE paths must be workspace-relative without '..' segments.";
                    return false;
                }

                if (!Regex.IsMatch(normalizedQuery, @"\.[A-Za-z0-9]{1,8}$"))
                {
                    error = "READ_FILE expects a single relative file path ending in a file extension.";
                    return false;
                }

                if (knownWorkspaceFiles is { Count: > 0 } && !MatchesKnownWorkspaceFile(normalizedQuery, knownWorkspaceFiles, out string suggestions))
                {
                    error = "READ_FILE path does not match any connected workspace file."
                        + (suggestions.Length > 0 ? " Closest known files: " + suggestions : string.Empty);
                    return false;
                }
            }

            if (strictness == ValidationStrictness.Structural)
                return true;

            // Grounded: the query must connect to the actual task. A query about numbers or
            // entities that appear nowhere in the request means the model invented the task —
            // the exact failure that produced "PYTHON_MATH: 5 * 100" for a fun-facts request.
            string context = groundingContext ?? string.Empty;
            if (context.Length == 0)
                return true; // no evidence to ground against — fail open

            switch (normalizedTool)
            {
                case ToolCalculate:
                case ToolPythonMath:
                case ToolRunSandbox:
                {
                    // ALL query numbers must be grounded — "any" would let an invented query
                    // through whenever one digit coincides with the request ("Give me 5 fun
                    // facts" → "5 * 100"). Legitimately derived constants (unit factors,
                    // intermediate results) pass through the compute-verb escape hatch: if the
                    // request itself asks for computation, derivation is expected.
                    var queryNumbers = ExtractNumbers(normalizedQuery);
                    var contextNumbers = new HashSet<string>(ExtractNumbers(context), StringComparer.Ordinal);
                    bool numbersGrounded = queryNumbers.Count == 0 || queryNumbers.All(contextNumbers.Contains);
                    bool tokenGrounded = SharesContentToken(normalizedQuery, context, 4);
                    bool computeRequested = ComputeVerbRegex.IsMatch(context) || NumericSignalRegex.IsMatch(context);
                    if (!numbersGrounded && !tokenGrounded && !computeRequested)
                    {
                        error = $"{normalizedTool} query uses numbers/terms that appear nowhere in the request, and the request does not ask for computation. Choose final unless the task itself needs this tool.";
                        return false;
                    }

                    break;
                }

                case ToolWebSearch:
                    if (!SharesContentToken(normalizedQuery, context, 4))
                    {
                        error = "WEB_SEARCH query shares no meaningful term with the request. Search for the request's actual entities, or choose final.";
                        return false;
                    }

                    break;

                case ToolHippocampus:
                case ToolSearchCodebase:
                    if (!SharesContentToken(normalizedQuery, context, 3))
                    {
                        error = $"{normalizedTool} query shares no term with the request. Query the request's actual topic, or choose final.";
                        return false;
                    }

                    break;
            }

            return true;
        }

        private static bool MatchesKnownWorkspaceFile(string query, IReadOnlyCollection<string> knownFiles, out string suggestions)
        {
            suggestions = string.Empty;
            string normalizedQuery = query.Replace('\\', '/').TrimStart('/', '.');
            string queryFileName = normalizedQuery.Contains('/') ? normalizedQuery[(normalizedQuery.LastIndexOf('/') + 1)..] : normalizedQuery;

            foreach (string known in knownFiles)
            {
                string normalizedKnown = (known ?? string.Empty).Replace('\\', '/');
                if (normalizedKnown.Length == 0)
                    continue;

                if (normalizedKnown.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || normalizedKnown.EndsWith("/" + normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || normalizedKnown.EndsWith("/" + queryFileName, StringComparison.OrdinalIgnoreCase)
                    || normalizedKnown.Equals(queryFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string stem = queryFileName.Contains('.') ? queryFileName[..queryFileName.LastIndexOf('.')] : queryFileName;
            if (stem.Length >= 3)
            {
                suggestions = string.Join(", ", knownFiles
                    .Where(f => !string.IsNullOrWhiteSpace(f)
                        && f.Contains(stem, StringComparison.OrdinalIgnoreCase))
                    .Take(8));
            }

            return false;
        }

        private static List<string> ExtractNumbers(string text)
        {
            var results = new List<string>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"\d+(?:\.\d+)?"))
            {
                // Canonicalize through double so "018", "18", and "18.0" compare equal.
                if (double.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
                {
                    string canonical = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    if (!results.Contains(canonical))
                        results.Add(canonical);
                }
            }

            return results;
        }

        private static bool SharesContentToken(string query, string context, int minLength)
        {
            var contextTokens = new HashSet<string>(ExtractContentTokens(context, minLength), StringComparer.OrdinalIgnoreCase);
            if (contextTokens.Count == 0)
                return true; // context carries no comparable tokens — fail open

            return ExtractContentTokens(query, minLength).Any(contextTokens.Contains);
        }

        private static IEnumerable<string> ExtractContentTokens(string text, int minLength)
        {
            return Regex.Matches(text ?? string.Empty, @"[A-Za-z_][A-Za-z0-9_]*")
                .Select(m => m.Value)
                .Where(t => t.Length >= minLength && !StopWords.Contains(t));
        }

        // ── Observation digesting for small models ────────────────────────────

        public const string DigestBlockLabel = "VERIFIED TOOL FACTS";

        /// <summary>
        /// Compresses a raw tool observation into plain FACT lines. Small models copy raw
        /// observation envelopes back out as their "answer"; the same information as bare
        /// labeled facts gets USED instead of echoed.
        /// </summary>
        public static string DigestObservation(string tool, string query, string resultText, int maxFacts = 3, int maxCharsPerFact = 240)
        {
            string normalizedTool = NormalizeToolName(tool);
            var candidateLines = (resultText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Where(line => !line.StartsWith("[[", StringComparison.Ordinal))
                .Where(line => !Regex.IsMatch(line, @"^[\-=_~*#]{3,}$"))
                .ToList();

            if (candidateLines.Count == 0)
                return string.Empty;

            List<string> facts;
            if (normalizedTool is ToolCalculate or ToolPythonMath or ToolRunSandbox)
            {
                // Numeric tools: result lines carry the value ("expr = value", "Result: value").
                facts = candidateLines
                    .Where(line => line.Contains('=') || Regex.IsMatch(line, @"\d"))
                    .Take(maxFacts)
                    .ToList();
                if (facts.Count == 0)
                    facts = candidateLines.Take(maxFacts).ToList();
            }
            else
            {
                // Evidence tools: prefer substantive lines over headers and separators.
                facts = candidateLines
                    .Where(line => line.Length >= 20)
                    .Take(maxFacts)
                    .ToList();
                if (facts.Count == 0)
                    facts = candidateLines.Take(maxFacts).ToList();
            }

            var block = new StringBuilder();
            block.AppendLine($"[[{DigestBlockLabel}]]");
            foreach (string fact in facts)
            {
                string trimmedFact = fact.Length <= maxCharsPerFact ? fact : fact[..maxCharsPerFact].TrimEnd() + "…";
                block.AppendLine("FACT: " + trimmedFact);
            }

            block.AppendLine($"(verified by {normalizedTool})");
            block.AppendLine("Use these facts directly in the answer. Never mention tools and never repeat this block.");
            block.AppendLine($"[[END {DigestBlockLabel}]]");
            return block.ToString().TrimEnd();
        }

        // ── Tool-name normalization (alias tolerance) ─────────────────────────

        private static readonly Dictionary<string, string> ToolAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CALCULATE"] = ToolCalculate,
            ["CALC"] = ToolCalculate,
            ["CALCULATOR"] = ToolCalculate,
            ["WEB_SEARCH"] = ToolWebSearch,
            ["WEBSEARCH"] = ToolWebSearch,
            ["SEARCH_WEB"] = ToolWebSearch,
            ["SEARCHWEB"] = ToolWebSearch,
            ["PYTHON_MATH"] = ToolPythonMath,
            ["PYTHONMATH"] = ToolPythonMath,
            ["PYTHON"] = ToolPythonMath,
            ["RUN_PYTHON"] = ToolPythonMath,
            ["RUNPYTHON"] = ToolPythonMath,
            ["SEARCH_HIPPOCAMPUS"] = ToolHippocampus,
            ["HIPPOCAMPUS"] = ToolHippocampus,
            ["SEARCH_MEMORY"] = ToolHippocampus,
            ["SEARCHMEMORY"] = ToolHippocampus,
            ["MEMORY"] = ToolHippocampus,
            ["RECALL"] = ToolHippocampus,
            ["RUN_SANDBOX"] = ToolRunSandbox,
            ["RUNSANDBOX"] = ToolRunSandbox,
            ["SANDBOX"] = ToolRunSandbox,
            ["RUN_CODE"] = ToolRunSandbox,
            ["RUNCODE"] = ToolRunSandbox,
            ["READ_FILE"] = ToolReadFile,
            ["READFILE"] = ToolReadFile,
            ["OPEN_FILE"] = ToolReadFile,
            ["OPENFILE"] = ToolReadFile,
            ["SEARCH_CODEBASE"] = ToolSearchCodebase,
            ["SEARCHCODEBASE"] = ToolSearchCodebase,
            ["CODE_SEARCH"] = ToolSearchCodebase,
            ["CODESEARCH"] = ToolSearchCodebase,
            ["LIST_FILES"] = ToolListFiles,
            ["LISTFILES"] = ToolListFiles
        };

        /// <summary>
        /// Maps model-typed tool-name variants ("calc", "search_web", "RunPython") onto the
        /// canonical tool names. Unknown names are returned uppercased/trimmed so dispatch
        /// can still report them precisely.
        /// </summary>
        public static string NormalizeToolName(string rawTool)
        {
            string cleaned = Regex.Replace((rawTool ?? string.Empty).Trim(), @"[^A-Za-z0-9_]", string.Empty);
            if (cleaned.Length == 0)
                return string.Empty;

            return ToolAliases.TryGetValue(cleaned, out string? canonical)
                ? canonical
                : cleaned.ToUpperInvariant();
        }

        // ── Tolerant [PAUSE:] marker parsing ──────────────────────────────────

        public enum PauseMarkerClassification { NotPause, Possible, Confirmed }

        /// <summary>
        /// Classifies a speculative buffer that starts with '[' against the pause-marker shape,
        /// tolerating the drift small models produce: case variance and stray spaces
        /// ("[pause :", "[ PAUSE|"). On Confirmed, <paramref name="innerStartIndex"/> points
        /// just past the separator, where "TOOL | query" begins.
        /// </summary>
        public static PauseMarkerClassification ClassifyPauseCandidate(string buffered, out int innerStartIndex)
        {
            innerStartIndex = -1;
            if (string.IsNullOrEmpty(buffered) || buffered[0] != '[')
                return PauseMarkerClassification.NotPause;

            const string keyword = "PAUSE";
            int i = 1;

            while (i < buffered.Length && buffered[i] == ' ')
                i++;

            for (int k = 0; k < keyword.Length; k++, i++)
            {
                if (i >= buffered.Length)
                    return PauseMarkerClassification.Possible;
                if (char.ToUpperInvariant(buffered[i]) != keyword[k])
                    return PauseMarkerClassification.NotPause;
            }

            while (i < buffered.Length && buffered[i] == ' ')
                i++;

            if (i >= buffered.Length)
                return PauseMarkerClassification.Possible;

            if (buffered[i] == ':' || buffered[i] == '|')
            {
                innerStartIndex = i + 1;
                return PauseMarkerClassification.Confirmed;
            }

            return PauseMarkerClassification.NotPause;
        }

        /// <summary>
        /// Parses the inner text of a pause marker ("TOOL | query", with model drift tolerated:
        /// alias tool names, or a missing pipe when the first word is a recognizable tool name).
        /// </summary>
        public static bool TryParsePauseCommandText(string inner, out string tool, out string query)
        {
            tool = string.Empty;
            query = string.Empty;
            string text = (inner ?? string.Empty).Trim();
            if (text.Length == 0)
                return false;

            int pipeIndex = text.IndexOf('|');
            if (pipeIndex > 0)
            {
                tool = NormalizeToolName(text[..pipeIndex]);
                query = text[(pipeIndex + 1)..].Trim();
                return tool.Length > 0 && query.Length > 0;
            }

            // No pipe — accept "TOOL query" only when the first word maps to a known tool.
            int splitIndex = text.IndexOfAny(new[] { ' ', '\t', ':' });
            if (splitIndex <= 0)
                return false;

            string candidate = NormalizeToolName(text[..splitIndex]);
            if (!ToolAliases.ContainsValue(candidate))
                return false;

            tool = candidate;
            query = text[(splitIndex + 1)..].Trim();
            return query.Length > 0;
        }

        // ── Few-shot examples for the tool-decision prompt ────────────────────

        /// <summary>
        /// Compact worked examples for the Builder tool-decision system prompt. Few-shot
        /// examples are the highest-leverage routing aid for 1–4B models; the NONE example is
        /// as important as the tool examples — not calling is usually correct.
        /// </summary>
        public static string BuildToolDecisionFewShotExamples(bool webSearchEnabled, bool codebaseToolsEnabled)
        {
            var examples = new StringBuilder();
            examples.Append("Examples: ");
            examples.Append("request \"Build a landing page for a bakery\" -> {\"action\":\"final\",\"tool\":\"NONE\",\"query\":\"\"}. ");
            examples.Append("request \"Give me 5 random fun facts\" -> {\"action\":\"final\",\"tool\":\"NONE\",\"query\":\"\"}. ");
            examples.Append("request \"How much is 18% of 240?\" -> {\"action\":\"tool\",\"tool\":\"CALCULATE\",\"query\":\"0.18 * 240\"}. ");
            if (webSearchEnabled)
                examples.Append("request \"Use the latest stable Node.js LTS version\" -> {\"action\":\"tool\",\"tool\":\"WEB_SEARCH\",\"query\":\"current Node.js LTS version\"}. ");
            if (codebaseToolsEnabled)
                examples.Append("request \"Fix the null check in UserService.cs\" -> {\"action\":\"tool\",\"tool\":\"READ_FILE\",\"query\":\"UserService.cs\"}. ");
            return examples.ToString().TrimEnd();
        }

        // ── Tool-observation echo detection ───────────────────────────────────

        private static readonly string[] AllToolNames =
        {
            ToolHippocampus, ToolCalculate, ToolRunSandbox, ToolPythonMath,
            ToolWebSearch, ToolReadFile, ToolSearchCodebase, ToolListFiles
        };

        /// <summary>
        /// Detects the failure shape where a (typically small) model returns the injected tool
        /// observations — or the tool catalog — instead of a deliverable: leaked observation
        /// envelopes, a leading bare tool-name line, retyped envelope field lines, or a short
        /// answer assembled almost entirely from observation lines.
        /// </summary>
        public static bool IsToolObservationEcho(string answer, string toolContext)
        {
            string normalized = (answer ?? string.Empty).Trim();
            if (normalized.Length == 0 || string.IsNullOrWhiteSpace(toolContext))
                return false;

            if (normalized.Contains("[[TOOL OBSERVATION", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[[END TOOL OBSERVATION", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[[" + DigestBlockLabel, StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[[END " + DigestBlockLabel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Long outputs that merely cite observation facts are legitimate deliverables.
            if (normalized.Length > 600)
                return false;

            string firstLine = normalized.Split('\n', 2)[0].Trim();
            if (AllToolNames.Any(tool =>
                    firstLine.StartsWith(tool, StringComparison.OrdinalIgnoreCase)
                    && firstLine.Length <= tool.Length + 24))
            {
                return true;
            }

            // Envelope field lines ("Tool:", "Query:", "Status: success") only exist inside the
            // injected observations; two or more of them means the envelope was retyped.
            int envelopeMarkers = 0;
            foreach (string marker in new[] { "Tool:", "Query:", "Status: success", "Status: failed", "execution output", "FACT:" })
            {
                if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    envelopeMarkers++;
            }

            if (envelopeMarkers >= 2)
                return true;

            // A short multi-line answer whose lines nearly all appear verbatim inside the tool
            // observations is a copy, not a synthesis. Single-line answers are exempt: a correct
            // calculator-backed result legitimately repeats the observation's number.
            var lines = normalized.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length >= 8)
                .ToList();
            if (lines.Count < 2)
                return false;

            int echoedLines = lines.Count(line => toolContext.Contains(line, StringComparison.OrdinalIgnoreCase));
            return echoedLines * 10 >= lines.Count * 6;
        }
    }
}
