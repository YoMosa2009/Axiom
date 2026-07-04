using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private sealed class ProjectCanvasArtifactValidation
        {
            public bool SyntaxValid { get; set; }
            public bool StandaloneHtml { get; set; }
            public bool ExternalDependenciesFound { get; set; }
            public bool EmbeddedCssFound { get; set; }
            public bool EmbeddedJsFound { get; set; }
            public bool ProjectCanvasRenderable { get; set; }
            public bool ObviousInteractivityHooksFound { get; set; }
            public Dictionary<string, bool> RequiredElementsFound { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> ExternalDependencies { get; } = new();
            public List<string> RuntimeErrors { get; } = new();
            public List<string> Failures { get; } = new();
            public bool OverallPass => Failures.Count == 0;
        }

        private static readonly Regex HtmlExternalAttributeRegex = new(@"\b(?:src|href)\s*=\s*(['""])(?<value>[^'""]+)\1", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlCssUrlRegex = new(@"url\(\s*(['""]?)(?<value>[^)'""\s]+)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlCssImportRegex = new(@"@import\s+(?:url\()?['""]?(?<value>https?:\/\/[^)'"";\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlJsImportRegex = new(@"\bimport\s+(?:[^;]+?\s+from\s+)?['""](?<value>https?:\/\/[^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlFetchExternalRegex = new(@"\b(?:fetch|XMLHttpRequest|EventSource|WebSocket)\s*\([^)]*['""](?<value>https?:\/\/[^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static ProjectCanvasArtifactValidation ValidateProjectCanvasHtmlArtifact(string code, CouncilRunContext? context)
        {
            var result = new ProjectCanvasArtifactValidation();
            string source = StripChatFromCode(code ?? string.Empty).Trim();
            string request = ((context?.UserPrompt ?? string.Empty) + "\n" + (context?.Objective ?? string.Empty)).Trim();
            string lowerRequest = request.ToLowerInvariant();
            string lowerSource = source.ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(source))
            {
                result.Failures.Add("Artifact source is empty.");
                return result;
            }

            var document = new HtmlDocument
            {
                OptionFixNestedTags = true,
                OptionAutoCloseOnEnd = true
            };
            document.LoadHtml(source);

            result.SyntaxValid = LooksSyntacticallyLikeHtml(source);
            result.StandaloneHtml = Regex.IsMatch(source, @"<!doctype\s+html", RegexOptions.IgnoreCase)
                && Regex.IsMatch(source, @"<html\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(source, @"<head\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(source, @"<body\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(source, @"</html\s*>", RegexOptions.IgnoreCase);
            result.ProjectCanvasRenderable = ArtifactRenderService.DetectForCanvas(source, null).Kind == ArtifactKind.Html;
            result.EmbeddedCssFound = document.DocumentNode.SelectNodes("//style")?.Any(node => !string.IsNullOrWhiteSpace(node.InnerText)) == true
                || document.DocumentNode.SelectNodes("//*[@style]")?.Any() == true;
            result.EmbeddedJsFound = document.DocumentNode.SelectNodes("//script[not(@src)]")?.Any(node => !string.IsNullOrWhiteSpace(node.InnerText)) == true;

            foreach (string external in FindExternalHtmlDependencies(source, document))
            {
                if (!result.ExternalDependencies.Contains(external, StringComparer.OrdinalIgnoreCase))
                    result.ExternalDependencies.Add(external);
            }
            result.ExternalDependenciesFound = result.ExternalDependencies.Count > 0;

            if (!result.SyntaxValid)
                result.Failures.Add("HTML syntax appears incomplete or badly unbalanced.");
            if (!result.ProjectCanvasRenderable)
                result.Failures.Add("Project Canvas did not detect a renderable HTML artifact.");
            if (ContainsMarkdownCodeFenceMarker(source))
                result.Failures.Add("HTML source still contains markdown code fence markers; remove ```html/``` before rendering or saving.");

            bool expectsStandalone = context?.IsArtifactCanvasRequest == true
                || lowerRequest.Contains("standalone html", StringComparison.Ordinal)
                || lowerRequest.Contains("single-file", StringComparison.Ordinal)
                || lowerRequest.Contains("complete html", StringComparison.Ordinal)
                || lowerRequest.Contains("complete artifact code", StringComparison.Ordinal);
            if (expectsStandalone && !result.StandaloneHtml)
                result.Failures.Add("Expected one complete standalone HTML document with <!DOCTYPE html>, <html>, <head>, and <body>.");

            bool expectsOffline = context?.IsArtifactCanvasRequest == true
                || lowerRequest.Contains("no external", StringComparison.Ordinal)
                || lowerRequest.Contains("self-contained", StringComparison.Ordinal)
                || lowerRequest.Contains("offline", StringComparison.Ordinal)
                || lowerRequest.Contains("single-file", StringComparison.Ordinal);
            if (expectsOffline && result.ExternalDependenciesFound)
                result.Failures.Add("External dependencies found: " + string.Join(", ", result.ExternalDependencies.Take(6)) + ".");

            bool expectsCss = lowerRequest.Contains("embedded css", StringComparison.Ordinal)
                || lowerRequest.Contains("inline css", StringComparison.Ordinal)
                || lowerRequest.Contains("glassmorphism", StringComparison.Ordinal)
                || lowerRequest.Contains("dashboard", StringComparison.Ordinal)
                || lowerRequest.Contains("dark ", StringComparison.Ordinal)
                || context?.IsArtifactCanvasRequest == true;
            if (expectsCss && !result.EmbeddedCssFound)
                result.Failures.Add("Expected embedded CSS, but no inline <style> block or style attributes were found.");

            bool expectsJs = lowerRequest.Contains("embedded javascript", StringComparison.Ordinal)
                || lowerRequest.Contains("inline javascript", StringComparison.Ordinal)
                || lowerRequest.Contains(" javascript", StringComparison.Ordinal)
                || lowerRequest.Contains("interactive", StringComparison.Ordinal)
                || lowerRequest.Contains("filter", StringComparison.Ordinal)
                || lowerRequest.Contains("toggle", StringComparison.Ordinal)
                || lowerRequest.Contains("animated", StringComparison.Ordinal)
                || lowerRequest.Contains("button", StringComparison.Ordinal);
            if (expectsJs && !result.EmbeddedJsFound)
                result.Failures.Add("Expected embedded JavaScript, but no inline <script> block was found.");

            AddRequiredElementCheck(result, "task cards", ExpectedTaskCardsFound(source, document, lowerRequest, out string taskEvidence), taskEvidence);
            AddRequiredElementCheck(result, "filter button", ExpectedFilterButtonFound(source, document, lowerRequest, out string filterEvidence), filterEvidence);
            AddRequiredElementCheck(result, "radial chart", ExpectedRadialChartFound(source, document, lowerRequest, out string chartEvidence), chartEvidence);
            AddRequiredElementCheck(result, "animated starfield", ExpectedStarfieldFound(source, lowerRequest, out string starEvidence), starEvidence);
            AddRequiredElementCheck(result, "glassmorphism styling", ExpectedGlassmorphismFound(source, lowerRequest, out string glassEvidence), glassEvidence);

            bool expectsInteractivity = lowerRequest.Contains("interactive", StringComparison.Ordinal)
                || lowerRequest.Contains("button", StringComparison.Ordinal)
                || lowerRequest.Contains("filter", StringComparison.Ordinal)
                || lowerRequest.Contains("toggle", StringComparison.Ordinal)
                || lowerRequest.Contains("animated", StringComparison.Ordinal);
            result.ObviousInteractivityHooksFound = !expectsInteractivity || HasInteractivityHook(source);
            if (!result.ObviousInteractivityHooksFound)
                result.Failures.Add("Expected interactivity, but no event handler, DOM query, timer, or animation hook was found.");

            result.Failures.AddRange(ValidateRequestedHtmlBehavior(source, context));

            return result;
        }

        private static void AddRequiredElementCheck(ProjectCanvasArtifactValidation result, string label, bool? found, string evidence)
        {
            if (!found.HasValue)
                return;

            result.RequiredElementsFound[label] = found.Value;
            if (!found.Value)
                result.Failures.Add(string.IsNullOrWhiteSpace(evidence) ? $"Required element missing: {label}." : evidence);
        }

        private static bool LooksSyntacticallyLikeHtml(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;

            if (!Regex.IsMatch(source, @"<[^>]+>", RegexOptions.Singleline))
                return false;

            int openAngles = source.Count(c => c == '<');
            int closeAngles = source.Count(c => c == '>');
            if (openAngles == 0 || closeAngles == 0 || Math.Abs(openAngles - closeAngles) > Math.Max(2, openAngles / 4))
                return false;

            int openScripts = Regex.Matches(source, @"<script\b", RegexOptions.IgnoreCase).Count;
            int closeScripts = Regex.Matches(source, @"</script\s*>", RegexOptions.IgnoreCase).Count;
            if (openScripts != closeScripts)
                return false;

            int openStyles = Regex.Matches(source, @"<style\b", RegexOptions.IgnoreCase).Count;
            int closeStyles = Regex.Matches(source, @"</style\s*>", RegexOptions.IgnoreCase).Count;
            return openStyles == closeStyles;
        }

        private static IEnumerable<string> FindExternalHtmlDependencies(string source, HtmlDocument document)
        {
            foreach (Match match in HtmlExternalAttributeRegex.Matches(source))
            {
                string value = match.Groups["value"].Value.Trim();
                if (IsExternalDependency(value))
                    yield return value;
            }

            foreach (Match match in HtmlCssUrlRegex.Matches(source))
            {
                string value = match.Groups["value"].Value.Trim();
                if (IsExternalDependency(value))
                    yield return value;
            }

            foreach (Match match in HtmlCssImportRegex.Matches(source))
                yield return match.Groups["value"].Value.Trim();

            foreach (Match match in HtmlJsImportRegex.Matches(source))
                yield return match.Groups["value"].Value.Trim();

            foreach (Match match in HtmlFetchExternalRegex.Matches(source))
                yield return match.Groups["value"].Value.Trim();

            var linkedNodes = document.DocumentNode.SelectNodes("//link[@rel]") ?? Enumerable.Empty<HtmlNode>();
            foreach (var node in linkedNodes)
            {
                string href = node.GetAttributeValue("href", string.Empty).Trim();
                if (IsExternalDependency(href))
                    yield return href;
            }
        }

        private static bool IsExternalDependency(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("//", StringComparison.OrdinalIgnoreCase);
        }

        private static bool? ExpectedTaskCardsFound(string source, HtmlDocument document, string lowerRequest, out string evidence)
        {
            evidence = string.Empty;
            if (!lowerRequest.Contains("task", StringComparison.Ordinal) || !lowerRequest.Contains("card", StringComparison.Ordinal))
                return null;

            int expected = ExtractExpectedCount(lowerRequest, @"(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:\w+\s+){0,3}?task\s+cards?");
            if (expected <= 0)
                expected = 1;

            int count = CountTaskCardEvidence(source, document);
            bool found = count >= expected;
            evidence = found
                ? $"Found evidence for {count} task card(s), expected {expected}."
                : $"Expected {expected} task card(s), found evidence for {count}.";
            return found;
        }

        private static int CountTaskCardEvidence(string source, HtmlDocument document)
        {
            int classCount = document.DocumentNode
                .SelectNodes("//*[contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'task') or contains(translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'task') or @data-task or @data-status]")
                ?.Count(node =>
                {
                    string combined = (node.GetAttributeValue("class", "") + " " + node.GetAttributeValue("id", "") + " " + node.Name).ToLowerInvariant();
                    return combined.Contains("card", StringComparison.Ordinal)
                        || node.Attributes["data-task"] != null
                        || node.Attributes["data-status"] != null;
                }) ?? 0;

            int completedObjectCount = Regex.Matches(source, @"\bcompleted\s*:", RegexOptions.IgnoreCase).Count;
            int titleObjectCount = Regex.Matches(source, @"\{\s*(?:title|name|task)\s*:", RegexOptions.IgnoreCase).Count;
            int repeatedTaskCardTokens = Regex.Matches(source, @"task[-_\s]?card", RegexOptions.IgnoreCase).Count;
            return Math.Max(classCount, Math.Max(completedObjectCount, Math.Max(titleObjectCount, repeatedTaskCardTokens)));
        }

        private static bool? ExpectedFilterButtonFound(string source, HtmlDocument document, string lowerRequest, out string evidence)
        {
            evidence = string.Empty;
            bool expected = lowerRequest.Contains("filter", StringComparison.Ordinal)
                || lowerRequest.Contains("show completed", StringComparison.Ordinal)
                || lowerRequest.Contains("completed tasks", StringComparison.Ordinal)
                || lowerRequest.Contains("pending", StringComparison.Ordinal);
            if (!expected)
                return null;

            var buttons = document.DocumentNode.SelectNodes("//button") ?? Enumerable.Empty<HtmlNode>();
            bool hasButton = buttons.Any(button =>
            {
                string text = HtmlEntity.DeEntitize(button.InnerText ?? string.Empty).ToLowerInvariant();
                string attrs = string.Join(" ", button.Attributes.Select(a => a.Value)).ToLowerInvariant();
                return text.Contains("completed", StringComparison.Ordinal)
                    || text.Contains("filter", StringComparison.Ordinal)
                    || text.Contains("pending", StringComparison.Ordinal)
                    || text.Contains("show", StringComparison.Ordinal)
                    || attrs.Contains("filter", StringComparison.Ordinal)
                    || attrs.Contains("completed", StringComparison.Ordinal);
            });

            bool hasToggleLogic = Regex.IsMatch(source, @"addEventListener|onclick|classList|style\.display|hidden|filter\s*\(", RegexOptions.IgnoreCase);
            bool found = hasButton && hasToggleLogic;
            evidence = found
                ? "Found a completion/filter button with DOM toggle logic."
                : "Expected a working completed-task filter button; button or toggle logic was not found.";
            return found;
        }

        private static bool? ExpectedRadialChartFound(string source, HtmlDocument document, string lowerRequest, out string evidence)
        {
            evidence = string.Empty;
            bool expected = lowerRequest.Contains("radial", StringComparison.Ordinal) && lowerRequest.Contains("chart", StringComparison.Ordinal);
            if (!expected)
                return null;

            bool hasSvgRadial = document.DocumentNode.SelectNodes("//svg")?.Any(svg =>
                svg.InnerHtml.Contains("<circle", StringComparison.OrdinalIgnoreCase)
                || svg.InnerHtml.Contains("<path", StringComparison.OrdinalIgnoreCase)
                || svg.InnerHtml.Contains("stroke-dash", StringComparison.OrdinalIgnoreCase)) == true;
            bool hasCssRadial = source.Contains("conic-gradient", StringComparison.OrdinalIgnoreCase)
                || source.Contains("radial-gradient", StringComparison.OrdinalIgnoreCase)
                || source.Contains("stroke-dasharray", StringComparison.OrdinalIgnoreCase)
                || source.Contains("stroke-dashoffset", StringComparison.OrdinalIgnoreCase);

            bool found = hasSvgRadial || hasCssRadial;
            evidence = found
                ? "Found SVG/CSS radial chart evidence."
                : "Expected a radial priority chart; no SVG/CSS radial chart evidence was found.";
            return found;
        }

        private static bool? ExpectedStarfieldFound(string source, string lowerRequest, out string evidence)
        {
            evidence = string.Empty;
            if (!lowerRequest.Contains("starfield", StringComparison.Ordinal) && !lowerRequest.Contains("stars", StringComparison.Ordinal))
                return null;

            bool hasStarTerms = source.Contains("star", StringComparison.OrdinalIgnoreCase);
            bool hasAnimation = Regex.IsMatch(source, @"@keyframes|animation\s*:|requestAnimationFrame|setInterval|setTimeout", RegexOptions.IgnoreCase);
            bool found = hasStarTerms && hasAnimation;
            evidence = found
                ? "Found starfield naming plus animation timing/keyframes."
                : "Expected an animated starfield background; starfield or animation evidence was not found.";
            return found;
        }

        private static bool? ExpectedGlassmorphismFound(string source, string lowerRequest, out string evidence)
        {
            evidence = string.Empty;
            if (!lowerRequest.Contains("glassmorphism", StringComparison.Ordinal))
                return null;

            bool found = source.Contains("backdrop-filter", StringComparison.OrdinalIgnoreCase)
                || (source.Contains("rgba(", StringComparison.OrdinalIgnoreCase) && source.Contains("blur", StringComparison.OrdinalIgnoreCase))
                || source.Contains("glass", StringComparison.OrdinalIgnoreCase);
            evidence = found
                ? "Found glassmorphism styling evidence."
                : "Expected glassmorphism styling; no backdrop-filter, blur/rgba, or glass styling evidence was found.";
            return found;
        }

        private static bool HasInteractivityHook(string source)
        {
            return Regex.IsMatch(source, @"addEventListener|onclick\s*=|querySelector|getElementById|classList|requestAnimationFrame|setInterval|setTimeout", RegexOptions.IgnoreCase);
        }

        private static IReadOnlyList<string> ValidateRequestedHtmlBehavior(string source, CouncilRunContext? context, string? relativePath = null)
        {
            string request = BuildHtmlBehaviorValidationRequest(context);
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(request))
                return Array.Empty<string>();

            string lowerSource = source.ToLowerInvariant();
            string script = ExtractInlineScriptSource(source);
            string lowerScript = script.ToLowerInvariant();
            string evidence = lowerSource + "\n" + lowerScript;
            HtmlBehaviorExpectations expectations = InferHtmlBehaviorExpectations(context);
            var failures = new List<string>();

            void Add(string message)
            {
                string full = string.IsNullOrWhiteSpace(relativePath)
                    ? message
                    : $"{relativePath}: {message}";
                if (!failures.Contains(full, StringComparer.OrdinalIgnoreCase))
                    failures.Add(full);
            }

            if (ContainsMarkdownCodeFenceMarker(source))
                Add("HTML result contains markdown code fence markers; this would render visible ```html text instead of a clean page.");

            if (expectations.ExpectsInput)
            {
                if (!Regex.IsMatch(lowerSource, @"<(input|textarea)\b", RegexOptions.IgnoreCase))
                    Add("requested input is missing an <input> or <textarea> element.");
                if (!lowerScript.Contains(".value", StringComparison.Ordinal))
                    Add("requested input appears unused; inline JavaScript does not read an input value.");
            }

            if (expectations.ExpectsPrimaryAction && !HasNamedEventHandler(lowerSource, lowerScript, ["generate", "fold", "simulate", "start", "run", "play", "pause"]))
                Add("requested primary action appears unwired; no matching click/submit handler was found in JavaScript.");

            if (expectations.ExpectsVisualization)
            {
                bool hasCanvas = lowerSource.Contains("<canvas", StringComparison.Ordinal);
                bool hasSvg = lowerSource.Contains("<svg", StringComparison.Ordinal);
                if (expectations.ExpectsCanvas && !hasCanvas)
                    Add("request called for an HTML <canvas> element, but no <canvas> element was found.");
                else if (!hasCanvas && !hasSvg)
                    Add("requested visualization surface is missing; no canvas or SVG evidence was found.");

                if (hasCanvas)
                {
                    if (!lowerScript.Contains("getcontext", StringComparison.Ordinal))
                        Add("canvas exists but JavaScript never obtains a drawing context.");
                    if (!HasCanvasDrawingEvidence(lowerScript))
                        Add("canvas exists but no drawing calls were found, so the visualization is likely blank.");
                }
            }

            if (expectations.ExpectsAnimation && !HasAnimationEvidence(evidence))
                Add("requested animation/folding-over-time behavior is missing requestAnimationFrame, timer, or keyframe evidence.");

            if (expectations.ExpectsRandom && !lowerScript.Contains("math.random", StringComparison.Ordinal))
                Add("Random control was requested, but JavaScript does not generate random values.");

            if (expectations.ExpectsReset && !HasNamedEventHandler(lowerSource, lowerScript, ["reset", "clear"]))
                Add("Reset control was requested, but no matching reset handler was found.");

            if (expectations.ExpectsHover && !Regex.IsMatch(evidence, @"mousemove|mouseover|mouseenter|pointermove|pointerenter|title\s*=|tooltip", RegexOptions.IgnoreCase))
                Add("hover/inspection details were requested, but no pointer or tooltip handling was found.");

            if (expectations.ExpectsStats && !Regex.IsMatch(lowerScript, @"textcontent|innertext|innerhtml|setattribute\s*\(", RegexOptions.IgnoreCase))
                Add("stats/readouts were requested, but JavaScript does not appear to update visible text.");

            if (expectations.ExpectsProteinRules)
            {
                if (!RequestMentionsAny(lowerScript, "hydrophobic", "polar", "charged", "positive", "negative"))
                    Add("protein/amino-acid behavior was requested, but JavaScript lacks amino-acid property classification.");
                if (RequestMentionsAny(expectations.RequestText, "spring", "physics", "force", "repel", "attract", "cluster", "fold")
                    && !Regex.IsMatch(lowerScript, @"force|velocity|vx|vy|spring|distance|dx|dy|attract|repel|cluster|energy", RegexOptions.IgnoreCase))
                {
                    Add("folding behavior was requested, but JavaScript lacks force/position update logic.");
                }
            }

            return failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        }

        private readonly struct HtmlBehaviorExpectations
        {
            public HtmlBehaviorExpectations(
                string requestText,
                bool expectsInput,
                bool expectsPrimaryAction,
                bool expectsCanvas,
                bool expectsVisualization,
                bool expectsAnimation,
                bool expectsRandom,
                bool expectsReset,
                bool expectsHover,
                bool expectsStats,
                bool expectsProteinRules)
            {
                RequestText = requestText;
                ExpectsInput = expectsInput;
                ExpectsPrimaryAction = expectsPrimaryAction;
                ExpectsCanvas = expectsCanvas;
                ExpectsVisualization = expectsVisualization;
                ExpectsAnimation = expectsAnimation;
                ExpectsRandom = expectsRandom;
                ExpectsReset = expectsReset;
                ExpectsHover = expectsHover;
                ExpectsStats = expectsStats;
                ExpectsProteinRules = expectsProteinRules;
            }

            public string RequestText { get; }
            public bool ExpectsInput { get; }
            public bool ExpectsPrimaryAction { get; }
            public bool ExpectsCanvas { get; }
            public bool ExpectsVisualization { get; }
            public bool ExpectsAnimation { get; }
            public bool ExpectsRandom { get; }
            public bool ExpectsReset { get; }
            public bool ExpectsHover { get; }
            public bool ExpectsStats { get; }
            public bool ExpectsProteinRules { get; }
        }

        private static HtmlBehaviorExpectations InferHtmlBehaviorExpectations(CouncilRunContext? context)
        {
            string request = NormalizeHtmlBehaviorValidationRequest(BuildHtmlBehaviorValidationRequest(context));
            bool expectsCanvas = Regex.IsMatch(request, @"<\s*canvas\b|\b(?:html\s+)?canvas\b", RegexOptions.IgnoreCase);
            bool expectsVisualization = expectsCanvas
                || RequestMentionsAny(request, "visual", "visualization", "simulator", "simulation", "folding", "fold over time", "draw", "graph", "plot", "wavefront");
            bool expectsProteinRules = RequestMentionsAny(request, "protein", "amino acid", "hydrophobic");

            return new HtmlBehaviorExpectations(
                request,
                RequestMentionsAny(request, "input", "paste", "type", "sequence", "amino acid"),
                RequestMentionsExplicitPrimaryAction(request) || expectsProteinRules,
                expectsCanvas,
                expectsVisualization,
                RequestMentionsAny(request, "animation", "animated", "animate", "over time", "folding over time", "smooth", "play/pause"),
                request.Contains("random", StringComparison.Ordinal),
                request.Contains("reset", StringComparison.Ordinal),
                RequestMentionsAny(request, "hover", "tooltip", "inspect", "details"),
                RequestMentionsAny(request, "statistic", "statistics", "stats", "score", "readout"),
                expectsProteinRules);
        }

        private static string NormalizeHtmlBehaviorValidationRequest(string request)
        {
            if (string.IsNullOrWhiteSpace(request))
                return string.Empty;

            string normalized = request.ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"\bproject\s*canvas\b", "project artifact surface", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bprojectcanvas\b", "project artifact surface", RegexOptions.IgnoreCase);
            return normalized;
        }

        private static bool RequestMentionsExplicitPrimaryAction(string request)
        {
            if (string.IsNullOrWhiteSpace(request))
                return false;

            if (Regex.IsMatch(request, @"\bgenerate\s*/\s*fold\b|\bgenerate[-\s]+fold\b", RegexOptions.IgnoreCase))
                return true;

            foreach (string rawLine in request.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                bool mentionsAction = RequestMentionsAny(line, "generate", "fold", "simulate", "start", "run", "play", "pause", "play/pause");
                bool mentionsControl = RequestMentionsAny(line, "button", "control", "action", "click", "tap", "submit");
                if (mentionsAction && mentionsControl)
                    return true;
            }

            return false;
        }

        private static string BuildHtmlBehaviorValidationRequest(CouncilRunContext? context)
        {
            if (context == null)
                return string.Empty;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(context.UserPrompt))
                sb.AppendLine(context.UserPrompt);
            if (!string.IsNullOrWhiteSpace(context.Objective))
                sb.AppendLine(context.Objective);
            if (!string.IsNullOrWhiteSpace(context.ArchitectOutput))
                sb.AppendLine(context.ArchitectOutput);
            if (context.GoalContract != null)
            {
                sb.AppendLine(context.GoalContract.Goal);
                foreach (string item in context.GoalContract.Requirements)
                    sb.AppendLine(item);
                foreach (string item in context.GoalContract.AcceptanceChecks)
                    sb.AppendLine(item);
            }

            return sb.ToString();
        }

        private static string ExtractInlineScriptSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return string.Empty;

            return string.Join("\n", Regex.Matches(source, @"<script\b(?![^>]*\bsrc\s*=)[^>]*>(?<code>[\s\S]*?)</script>", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(match => match.Groups["code"].Value));
        }

        private static bool ContainsMarkdownCodeFenceMarker(string source)
        {
            string value = source ?? string.Empty;
            return value.TrimStart().StartsWith("```", StringComparison.Ordinal)
                || value.Contains("```html", StringComparison.OrdinalIgnoreCase)
                || value.Contains("```javascript", StringComparison.OrdinalIgnoreCase)
                || value.Contains("```js", StringComparison.OrdinalIgnoreCase)
                || value.TrimEnd().EndsWith("```", StringComparison.Ordinal);
        }

        private static bool RequestMentionsAny(string text, params string[] terms)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasNamedEventHandler(string lowerSource, string lowerScript, IReadOnlyList<string> names)
        {
            if (string.IsNullOrWhiteSpace(lowerScript))
                return false;

            string combined = lowerSource + "\n" + lowerScript;
            bool hasEventHook = Regex.IsMatch(combined, @"addEventListener\s*\(\s*['""](?:click|submit|input|change|pointer|mouse)|\.onclick\s*=|onclick\s*=|onsubmit\s*=|oninput\s*=|onchange\s*=", RegexOptions.IgnoreCase);
            if (!hasEventHook)
                return false;

            if (names.Any(name => lowerScript.Contains(name, StringComparison.Ordinal)))
                return true;

            bool genericPrimaryClick = lowerScript.Contains("click", StringComparison.Ordinal)
                && lowerScript.Contains("button", StringComparison.Ordinal)
                && (lowerScript.Contains(".value", StringComparison.Ordinal) || HasCanvasDrawingEvidence(lowerScript));
            return genericPrimaryClick && names.Any(name => lowerSource.Contains(name, StringComparison.Ordinal));
        }

        private static bool HasCanvasDrawingEvidence(string lowerScript)
        {
            return Regex.IsMatch(lowerScript ?? string.Empty, @"\b(beginPath|moveTo|lineTo|arc|stroke|fill|fillRect|clearRect|drawImage|fillText|strokeRect|ellipse)\s*\(", RegexOptions.IgnoreCase);
        }

        private static bool HasAnimationEvidence(string source)
        {
            return Regex.IsMatch(source ?? string.Empty, @"requestAnimationFrame|setInterval\s*\(|setTimeout\s*\(|@keyframes|animation\s*:", RegexOptions.IgnoreCase);
        }

        private static int ExtractExpectedCount(string lowerRequest, string pattern)
        {
            Match match = Regex.Match(lowerRequest, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                return 0;

            string token = match.Groups["count"].Value.ToLowerInvariant();
            if (int.TryParse(token, out int parsed))
                return parsed;

            return token switch
            {
                "one" => 1,
                "two" => 2,
                "three" => 3,
                "four" => 4,
                "five" => 5,
                "six" => 6,
                "seven" => 7,
                "eight" => 8,
                "nine" => 9,
                "ten" => 10,
                _ => 0
            };
        }

        private static string BuildProjectCanvasSandboxResult(string code, string htmlPath, CouncilRunContext? context, IReadOnlyList<string>? runtimeErrors = null)
        {
            ProjectCanvasArtifactValidation validation = ValidateProjectCanvasHtmlArtifact(code, context);
            foreach (string error in runtimeErrors ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(error))
                    validation.RuntimeErrors.Add(error.Trim());
            }
            if (validation.RuntimeErrors.Count > 0)
                validation.Failures.Add("WebView2 console-error gate failed: " + string.Join(" | ", validation.RuntimeErrors.Take(4)) + ".");

            var sb = new StringBuilder();
            sb.AppendLine("SANDBOX_RESULT");
            sb.AppendLine("syntax_valid: " + validation.SyntaxValid.ToString().ToLowerInvariant());
            sb.AppendLine("standalone_html: " + validation.StandaloneHtml.ToString().ToLowerInvariant());
            sb.AppendLine("external_dependencies_found: " + validation.ExternalDependenciesFound.ToString().ToLowerInvariant());
            if (validation.ExternalDependenciesFound)
                sb.AppendLine("external_dependencies: " + string.Join(", ", validation.ExternalDependencies.Take(8)));
            sb.AppendLine("embedded_css_found: " + validation.EmbeddedCssFound.ToString().ToLowerInvariant());
            sb.AppendLine("embedded_js_found: " + validation.EmbeddedJsFound.ToString().ToLowerInvariant());
            sb.AppendLine("project_canvas_renderable: " + validation.ProjectCanvasRenderable.ToString().ToLowerInvariant());
            sb.AppendLine("required_elements_found:");
            if (validation.RequiredElementsFound.Count == 0)
            {
                sb.AppendLine("- none inferred: true");
            }
            else
            {
                foreach (var item in validation.RequiredElementsFound)
                    sb.AppendLine($"- {item.Key}: {item.Value.ToString().ToLowerInvariant()}");
            }
            sb.AppendLine("obvious_interactivity_hooks_found: " + validation.ObviousInteractivityHooksFound.ToString().ToLowerInvariant());
            sb.AppendLine("webview2_console_error_gate: " + (validation.RuntimeErrors.Count == 0 ? "pass" : "fail"));
            sb.AppendLine("runtime_errors: " + (validation.RuntimeErrors.Count == 0 ? "[]" : string.Join(" | ", validation.RuntimeErrors)));
            if (validation.Failures.Count > 0)
            {
                sb.AppendLine("failures:");
                foreach (string failure in validation.Failures.Take(10))
                    sb.AppendLine("- " + failure);
            }
            sb.AppendLine("saved_to: " + htmlPath);
            sb.AppendLine("overall: " + (validation.OverallPass ? "pass" : "fail"));
            sb.AppendLine("END_SANDBOX_RESULT");
            return sb.ToString().TrimEnd();
        }

        private static List<string> BuildProjectCanvasFinalVerificationFailures(CouncilRunContext context, string finalOutput)
        {
            var failures = new List<string>();
            string output = finalOutput ?? string.Empty;
            if (!context.BuilderRoutedToCanvas)
                failures.Add("No implementation from this run was routed to Project Canvas.");
            if (!CanvasHasRealContent(output))
                failures.Add("Project Canvas has no generated implementation.");

            ArtifactRenderInfo artifact = ArtifactRenderService.DetectForCanvas(output, null);
            if (!artifact.SupportsPreview)
            {
                failures.Add("Project Canvas did not detect a renderable artifact in the final output.");
                return failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            }

            if (artifact.Kind == ArtifactKind.Html)
            {
                ProjectCanvasArtifactValidation validation = ValidateProjectCanvasHtmlArtifact(output, context);
                failures.AddRange(validation.Failures.Select(failure => "Artifact validation: " + failure));
            }

            return failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        }

        private static void ReconcileArtifactValidationState(CouncilRunContext context, string finalOutput)
        {
            if (!context.IsArtifactCanvasRequest)
                return;

            var failures = BuildProjectCanvasFinalVerificationFailures(context, finalOutput);
            if (failures.Count == 0)
            {
                context.StaticValidationFindings = context.StaticValidationFindings
                    .Where(finding => !LooksLikeArtifactValidationFinding(finding))
                    .ToList();
                context.StaticValidationIssuesFound = context.StaticValidationFindings.Count > 0;
                context.SandboxExceptionsFound = false;
            }
        }

        private static bool LooksLikeArtifactValidationFinding(string finding)
        {
            if (string.IsNullOrWhiteSpace(finding))
                return false;

            string lower = finding.ToLowerInvariant();
            return lower.Contains("artifact", StringComparison.Ordinal)
                || lower.Contains("behavior", StringComparison.Ordinal)
                || lower.Contains("code fence", StringComparison.Ordinal)
                || lower.Contains("generate/fold", StringComparison.Ordinal)
                || lower.Contains("visualization", StringComparison.Ordinal)
                || lower.Contains("canvas", StringComparison.Ordinal)
                || lower.Contains("hover", StringComparison.Ordinal)
                || lower.Contains("external", StringComparison.Ordinal)
                || lower.Contains("embedded css", StringComparison.Ordinal)
                || lower.Contains("embedded javascript", StringComparison.Ordinal)
                || lower.Contains("task card", StringComparison.Ordinal)
                || lower.Contains("filter button", StringComparison.Ordinal)
                || lower.Contains("radial chart", StringComparison.Ordinal)
                || lower.Contains("starfield", StringComparison.Ordinal)
                || lower.Contains("glassmorphism", StringComparison.Ordinal);
        }
    }
}
