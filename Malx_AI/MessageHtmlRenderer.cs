using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Markdig;

namespace Malx_AI
{
    internal static class MessageHtmlRenderer
    {
        private const string KatexResourceBaseUrl = "http://katex.local";
        private static readonly Regex RichContentRegex = new(@"(\$\$|\\\(|\\\[|`{1,3}|!\[[^\]]*\]\([^)]+\)|\[[^\]]+\]\([^)]+\)|\*\*[^*\r\n]+\*\*|__[^_\r\n]+__|^\s{0,3}(#{1,6}\s|[-*+]\s|\d+\.\s|>\s)|\|.+\|)", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex DoubleDollarLatexRegex = new(@"(?<!\\)\$\$(?<content>[\s\S]+?)(?<!\\)\$\$", RegexOptions.Compiled);
        private static readonly Regex SingleDollarLatexRegex = new(@"(?<!\$)(?<!\\)\$(?!\$)(?<content>(?:\\.|[^\r\n$])+?)(?<!\\)\$(?!\$)", RegexOptions.Compiled);
        private static readonly Regex BracketLatexRegex = new(@"\\\[(?<content>[\s\S]+?)\\\]", RegexOptions.Compiled);
        private static readonly Regex ParenthesisLatexRegex = new(@"\\\((?<content>[\s\S]+?)\\\)", RegexOptions.Compiled);
        private static readonly Regex LatexEnvironmentRegex = new(@"\\begin\{(?<env>equation\*?|align\*?|aligned|gather\*?|gathered|multline\*?|array|matrix|bmatrix|Bmatrix|pmatrix|vmatrix|Vmatrix|cases|split)\}(?<content>[\s\S]+?)\\end\{\k<env>\}", RegexOptions.Compiled);
        private static readonly Regex LatexSignalWordRegex = new(@"\b(?:frac|sqrt|sum|int|lim|alpha|beta|gamma|delta|theta|pi|sigma|omega|infty|partial)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CurrencyOnlyRegex = new(@"^[\d\s\.,]+$", RegexOptions.Compiled);
        private static readonly char[] MathOperatorChars = ['=', '+', '-', '*', '/', '<', '>', '(', ')', '[', ']', '{', '}', '|'];
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        private static readonly Lazy<string> KatexInlineCss = new(LoadKatexInlineCss);

        private static readonly string[] DangerousNodeNames =
        [
            "script",
            "iframe",
            "object",
            "embed",
            "form",
            "input",
            "button",
            "textarea",
            "select",
            "option",
            "link",
            "meta",
            "base"
        ];

        public static string BuildHtml(string markdown)
        {
            var (sanitizedMarkdown, latexBlocks) = ExtractLatexBlocks(markdown ?? string.Empty);
            string parsedHtml = Markdown.ToHtml(sanitizedMarkdown, Pipeline);
            string restoredHtml = RestoreLatexBlocks(parsedHtml, latexBlocks);
            string body = SanitizeBodyHtml(restoredHtml);

            const string template = @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8' />
<meta name='viewport' content='width=device-width, initial-scale=1.0' />
<link rel='stylesheet' href='__KATEX_BASE_URL__/katex.min.css'>
<script>
function tryRenderMath(remainingAttempts) {
    if (typeof renderMathInElement !== 'function') {
        if (remainingAttempts > 0) {
            setTimeout(function() { tryRenderMath(remainingAttempts - 1); }, 50);
        }
        return;
    }

    renderMathInElement(document.body, {
        delimiters: [
            { left: '$$', right: '$$', display: true },
            { left: '\\[', right: '\\]', display: true },
            { left: '$', right: '$', display: false },
            { left: '\\(', right: '\\)', display: false }
        ],
        ignoredTags: ['script', 'noscript', 'style', 'textarea', 'pre', 'code', 'option'],
        throwOnError: false,
        errorColor: '#FF3B3B',
        trust: false
    });
}

document.addEventListener('DOMContentLoaded', function() {
    tryRenderMath(20);
});
</script>
<style>
__KATEX_INLINE_CSS__
html, body {
    overflow: hidden;
}
::-webkit-scrollbar {
    width: 0;
    height: 0;
}
body {
    margin: 0;
    color: #EDE8E3;
    background: #211F1D;
    font-family: 'Segoe UI Variable Text', 'Segoe UI', sans-serif;
    font-size: 14px;
    font-weight: 500;
    line-height: 1.6;
    overflow-wrap: anywhere;
    word-break: break-word;
    -webkit-font-smoothing: antialiased;
    text-rendering: optimizeLegibility;
}
p, li, td, th, blockquote {
    overflow-wrap: anywhere;
    word-break: break-word;
}
#content-root {
    display: block;
    width: 100%;
    max-width: 100%;
    margin: 0;
    padding: 0;
}
a {
    color: #B8924A;
    text-decoration: none;
}
a:hover {
    text-decoration: underline;
}
h1, h2, h3, h4, h5, h6 {
    color: #EDE8E3;
    margin: 0 0 8px 0;
}
p, ul, ol { margin: 0 0 8px 0; }
ul, ol { padding-left: 20px; }
pre {
    background: #171615;
    border: 1px solid #302D2A;
    border-radius: 8px;
    padding: 10px;
    overflow-x: auto;
}
code {
    background: #171615;
    border: 1px solid #302D2A;
    border-radius: 4px;
    padding: 1px 4px;
}
pre code { border: none; background: transparent; padding: 0; }
blockquote {
    border-left: 3px solid #302D2A;
    margin: 0;
    padding-left: 10px;
    color: #8A8279;
}
table {
    border-collapse: collapse;
    width: 100%;
    margin: 0 0 10px 0;
}
th, td {
    border: 1px solid #302D2A;
    padding: 6px 8px;
    text-align: left;
}
th {
    background: #171615;
}
.python-result-block {
    margin: 10px 0 0 0;
    padding: 10px 12px;
    border: 1px solid #302D2A;
    border-radius: 8px;
    background: #171615;
}
.python-result-label {
    color: #B8924A;
    font-size: 11px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-bottom: 6px;
}
.python-result-body {
    color: #EDE8E3;
    font-family: Consolas, 'Cascadia Code', monospace;
    font-size: 12px;
    white-space: pre-wrap;
}
.sandbox-timeout-block {
    margin: 10px 0 0 0;
    padding: 10px 12px;
    border: 1px solid #FF3B3B;
    border-radius: 8px;
    background: rgba(255, 59, 59, 0.08);
}
.sandbox-timeout-label {
    color: #FF3B3B;
    font-size: 11px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-bottom: 4px;
}
.sandbox-timeout-note {
    color: #EDE8E3;
    font-size: 12px;
}
.katex {
    font-size: 1em;
}
.katex-display {
    margin: 0.8em 0;
    overflow-x: auto;
    overflow-y: hidden;
}
.katex-error {
    color: #FF3B3B;
}
</style>
</head>
<body>
<div id='content-root'>__BODY__</div>
<script>
var heightObserversInitialized = false;

function measureHeight() {
    var root = document.getElementById('content-root') || document.body;
    if (!root) {
        return 24;
    }

    var rect = root.getBoundingClientRect();
    var style = window.getComputedStyle(root);
    var marginTop = parseFloat(style.marginTop || '0') || 0;
    var marginBottom = parseFloat(style.marginBottom || '0') || 0;
    var rectHeight = rect && isFinite(rect.height) ? rect.height : 0;
    var contentHeight = Math.max(
        rectHeight,
        root.scrollHeight || 0,
        root.offsetHeight || 0,
        root.clientHeight || 0
    );

    return Math.max(24, Math.ceil(contentHeight + marginTop + marginBottom));
}

function postHeight() {
    var h = measureHeight();
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({ type: 'height', value: h });
    }
}

function postRenderComplete() {
    var h = measureHeight();
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({ type: 'renderComplete', value: h });
    }
}

function schedulePostHeightBursts() {
    requestAnimationFrame(function() {
        requestAnimationFrame(postHeight);
    });

    setTimeout(postHeight, 40);
    setTimeout(postHeight, 120);
    setTimeout(postHeight, 260);
}

function initializeHeightObservers() {
    if (heightObserversInitialized) {
        return;
    }

    heightObserversInitialized = true;

    var root = document.getElementById('content-root') || document.body;
    if (!root) {
        return;
    }

    if (window.ResizeObserver) {
        var resizeObserver = new ResizeObserver(function() {
            schedulePostHeightBursts();
        });

        resizeObserver.observe(root);
    }

    if (window.MutationObserver) {
        var mutationObserver = new MutationObserver(function() {
            schedulePostHeightBursts();
        });

        mutationObserver.observe(root, {
            childList: true,
            subtree: true,
            characterData: true
        });
    }
}

async function finalizeRender() {
    tryRenderMath(20);
    initializeHeightObservers();

    if (document.fonts && document.fonts.ready) {
        try {
            await document.fonts.ready;
        } catch (e) {
        }
    }

    await new Promise(function(resolve) {
        requestAnimationFrame(function() {
            requestAnimationFrame(resolve);
        });
    });

    schedulePostHeightBursts();
    await new Promise(function(resolve) { setTimeout(resolve, 80); });
    postRenderComplete();
}

window.addEventListener('load', finalizeRender);
window.__malxFinalizeRender = finalizeRender;
</script>
</body>
</html>";

            return template
                .Replace("__KATEX_BASE_URL__", KatexResourceBaseUrl)
                .Replace("__KATEX_INLINE_CSS__", KatexInlineCss.Value)
                .Replace("__BODY__", body);
        }

        private static string LoadKatexInlineCss()
        {
            string? katexFolderPath = ResolveKatexFolderPath();
            if (string.IsNullOrWhiteSpace(katexFolderPath))
                return string.Empty;

            string cssPath = Path.Combine(katexFolderPath, "katex.min.css");
            return File.Exists(cssPath) ? File.ReadAllText(cssPath) : string.Empty;
        }

        private static string? ResolveKatexFolderPath()
        {
            string outputFolderPath = Path.Combine(AppContext.BaseDirectory, "KaTeX");
            if (Directory.Exists(outputFolderPath))
                return outputFolderPath;

            string projectFolderPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "KaTeX"));
            if (Directory.Exists(projectFolderPath))
                return projectFolderPath;

            return null;
        }

        public static bool NeedsHtmlRendering(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return false;

            return RichContentRegex.IsMatch(markdown)
                || DoubleDollarLatexRegex.IsMatch(markdown)
                || BracketLatexRegex.IsMatch(markdown)
                || ParenthesisLatexRegex.IsMatch(markdown)
                || LatexEnvironmentRegex.IsMatch(markdown)
                || ContainsQualifyingInlineLatex(markdown);
        }

        private static (string SanitizedText, Dictionary<string, string> PlaceholderMap) ExtractLatexBlocks(string rawText)
        {
            string sanitized = rawText ?? string.Empty;
            var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
            int placeholderIndex = 0;

            (sanitized, placeholderIndex) = ReplaceLatexBlocks(sanitized, DoubleDollarLatexRegex, _ => true, placeholders, placeholderIndex);
            (sanitized, placeholderIndex) = ReplaceLatexBlocks(sanitized, BracketLatexRegex, _ => true, placeholders, placeholderIndex);
            (sanitized, placeholderIndex) = ReplaceLatexBlocks(sanitized, ParenthesisLatexRegex, _ => true, placeholders, placeholderIndex);
            (sanitized, placeholderIndex) = ReplaceLatexBlocks(sanitized, LatexEnvironmentRegex, _ => true, placeholders, placeholderIndex, match => "$$\n" + match.Value.Trim() + "\n$$");
            (sanitized, placeholderIndex) = ReplaceLatexBlocks(sanitized, SingleDollarLatexRegex, match => IsQualifyingSingleDollarLatex(match.Groups["content"].Value), placeholders, placeholderIndex);

            return (sanitized, placeholders);
        }

        private static string RestoreLatexBlocks(string html, IReadOnlyDictionary<string, string> placeholderMap)
        {
            if (string.IsNullOrEmpty(html) || placeholderMap == null || placeholderMap.Count == 0)
                return html ?? string.Empty;

            string restored = html;
            foreach (KeyValuePair<string, string> pair in placeholderMap.OrderByDescending(pair => pair.Key.Length))
                restored = restored.Replace(pair.Key, WebUtility.HtmlEncode(pair.Value), StringComparison.Ordinal);

            return restored;
        }

        private static (string Result, int NextPlaceholderIndex) ReplaceLatexBlocks(
            string input,
            Regex regex,
            Func<Match, bool> shouldReplace,
            IDictionary<string, string> placeholderMap,
            int placeholderIndex,
            Func<Match, string>? storedValueFactory = null)
        {
            int nextPlaceholderIndex = placeholderIndex;
            string result = regex.Replace(input, match =>
            {
                if (!match.Success || !shouldReplace(match))
                    return match.Value;

                string placeholder = $"KATEX_PLACEHOLDER_{nextPlaceholderIndex++}";
                placeholderMap[placeholder] = storedValueFactory?.Invoke(match) ?? match.Value;
                return placeholder;
            });

            return (result, nextPlaceholderIndex);
        }

        private static bool ContainsQualifyingInlineLatex(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return SingleDollarLatexRegex.Matches(text)
                .Cast<Match>()
                .Any(match => IsQualifyingSingleDollarLatex(match.Groups["content"].Value));
        }

        private static bool IsQualifyingSingleDollarLatex(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string candidate = content.Trim();
            if (CurrencyOnlyRegex.IsMatch(candidate))
                return false;

            bool containsLetter = candidate.Any(char.IsLetter);
            bool containsDigit = candidate.Any(char.IsDigit);
            bool containsOperator = candidate.IndexOfAny(MathOperatorChars) >= 0;

            return candidate.Contains('\\')
                || candidate.Contains('^')
                || candidate.Contains('_')
                || LatexSignalWordRegex.IsMatch(candidate)
                || (containsLetter && (containsOperator || containsDigit))
                || (containsLetter && !candidate.Contains(' ') && candidate.Length <= 24);
        }

        private static string SanitizeBodyHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            var document = new HtmlDocument();
            document.LoadHtml(html);

            HtmlNodeCollection dangerousNodes = document.DocumentNode.SelectNodes(string.Join(" | ", DangerousNodeNames.Select(name => $"//{name}")));
            if (dangerousNodes != null)
            {
                foreach (HtmlNode node in dangerousNodes.ToArray())
                    node.Remove();
            }

            foreach (HtmlNode node in document.DocumentNode.Descendants().ToArray())
            {
                if (!node.HasAttributes)
                    continue;

                foreach (HtmlAttribute attribute in node.Attributes.ToArray())
                {
                    if (attribute == null)
                        continue;

                    if (string.Equals(attribute.Name, "style", StringComparison.OrdinalIgnoreCase))
                    {
                        node.Attributes.Remove(attribute);
                        continue;
                    }

                    if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        node.Attributes.Remove(attribute);
                        continue;
                    }

                    if (!string.Equals(attribute.Name, "href", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(attribute.Name, "src", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string value = HtmlEntity.DeEntitize(attribute.Value ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        node.Attributes.Remove(attribute);
                        continue;
                    }

                    if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                    {
                        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                            || uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (value.StartsWith("#", StringComparison.Ordinal)
                        || value.StartsWith("/", StringComparison.Ordinal)
                        || value.StartsWith("./", StringComparison.Ordinal)
                        || value.StartsWith("../", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    node.Attributes.Remove(attribute);
                }
            }

            return document.DocumentNode.InnerHtml;
        }
    }
}
