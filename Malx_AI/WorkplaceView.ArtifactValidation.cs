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

        private static string BuildProjectCanvasSandboxResult(string code, string htmlPath, CouncilRunContext? context)
        {
            ProjectCanvasArtifactValidation validation = ValidateProjectCanvasHtmlArtifact(code, context);
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
