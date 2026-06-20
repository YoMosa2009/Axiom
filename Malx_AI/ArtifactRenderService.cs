using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Malx_AI
{
    public enum ArtifactKind
    {
        None,
        Html,
        Svg,
        Chart,
        Document,
        InteractiveJavaScript
    }

    public sealed class ArtifactRenderInfo
    {
        public static ArtifactRenderInfo None(string rawSource) => new()
        {
            Kind = ArtifactKind.None,
            RawSource = rawSource ?? string.Empty
        };

        public ArtifactKind Kind { get; init; }
        public string RawSource { get; init; } = string.Empty;
        public string RenderSource { get; init; } = string.Empty;
        public string SaveContent { get; init; } = string.Empty;
        public string BinaryBase64 { get; init; } = string.Empty;
        public string DisplayTitle { get; init; } = string.Empty;
        public string SuggestedFileExtension { get; init; } = string.Empty;
        public bool SupportsPreview => Kind is ArtifactKind.Html or ArtifactKind.Svg or ArtifactKind.Chart or ArtifactKind.Document or ArtifactKind.InteractiveJavaScript;
        public bool RequiresWebView => SupportsPreview;
        public bool IsPreviewOnly => Kind is ArtifactKind.Chart or ArtifactKind.Document or ArtifactKind.InteractiveJavaScript;
    }

    internal static class ArtifactRenderService
    {
        private const string ChartPrefix = "CHART_OUTPUT:";
        private static readonly MarkdownPipeline DocumentPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        private static readonly Regex HtmlDocumentRegex = new(@"<html\b[\s\S]*?<body\b[\s\S]*?</body>[\s\S]*?</html>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SvgRegex = new(@"<svg\b[\s\S]*?</svg>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlSnippetRegex = new(@"<(div|canvas|table|form|section|article|main)\b[\s\S]*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MarkdownHeadingRegex = new(@"(?m)^#{1,6}\s+.+$", RegexOptions.Compiled);
        // Markdown table: header row | sep row (dashes) | one or more data rows
        private static readonly Regex MarkdownTableRegex = new(@"(?m)^\|.+\|[ \t]*\r?\n\|[\s\-\|:]+\|[ \t]*\r?\n(?:\|.+\|[ \t]*\r?\n?)+", RegexOptions.Compiled);
        private static readonly Regex FencedCodeBlockRegex = new(@"```(?<language>[^\r\n`]*)\r?\n(?<code>[\s\S]*?)```", RegexOptions.Compiled);
        private static readonly Regex ExternalResourceAttributeRegex = new("\\s(href|src)\\s*=\\s*(['\"]) (?<value>[^'\"]+)\\2".Replace(" ", string.Empty), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExternalCssUrlRegex = new("url\\((['\"]?)(?<value>[^)'\"]+)\\1\\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ScriptTagRegex = new(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex JsVisualSignalRegex = new(@"\b(document\.|window\.|canvas\b|svg\b|chart\b|plotly\b|echarts\b|d3\b|appendChild\b|getElementById\b|innerHTML\b|createElement\b|requestAnimationFrame\b|addEventListener\b|getContext\b|fillRect\b|beginPath\b|stroke\b|fill\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RawJsExecutableSignalRegex = new(@"\b(?:document\.|window\.|(?:getElementById|querySelector|querySelectorAll|createElement|appendChild|addEventListener|requestAnimationFrame|getContext|fillRect|beginPath)\s*\(|(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*=)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConsoleOnlyRegex = new(@"^\s*(console\.(log|info|warn|error)\s*\([^\r\n]*\)\s*;?\s*)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SvgRootAttributeRegex = new(@"<svg\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SvgFixedDimensionRegex = new(@"\s(?:width|height)\s*=\s*(?:'[^']*'|""[^""]*""|\d+\w*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Injected as the last <style> block in every rendered HTML artifact so it applies
        // after the model's own CSS. Uses non-destructive max-width/overflow rules only —
        // it never overrides intentional fixed sizes, just prevents them from overflowing
        // the user-resizable (~300-730px wide) Project Canvas WebView2 pane.
        private const string CanvasResponsiveNormalize =
            "<style id='_canvas_normalize'>" +
            "html{overflow-x:auto;}" +
            "body{max-width:100%;overflow-x:auto;}" +
            "img,video{max-width:100%;height:auto;}" +
            "table{max-width:100%;word-break:break-word;}" +
            "pre,code{white-space:pre-wrap;word-break:break-word;}" +
            "canvas{display:block;}" +
            "svg:not([preserveAspectRatio]){overflow:visible;}" +
            "</style>";

        // Resize script appended just before </body>. On DOMContentLoaded it checks every
        // <canvas> element: if the canvas has no explicit JS-set dimensions but its CSS
        // container is narrower than the drawn width, it scales the canvas down to fit.
        // Also fixes SVG root elements that have hardcoded width > viewport by rewriting
        // them to width=100% while preserving viewBox for correct aspect ratio.
        // fixContrast: the WebView2 backdrop is dark (#171615); a document that sets no
        // background renders transparent over it, so browser-default black text becomes
        // invisible. If both html and body backgrounds are transparent AND the body text is
        // dark, paint the page white. App-authored previews (intentional transparent bg with
        // light text) are left untouched because their text luminance is high.
        private const string CanvasResponsiveResizeScript =
            "<script id='_canvas_resize'>(function(){" +
            "function fixContrast(){" +
            "var b=document.body;if(!b)return;" +
            "function transparent(c){return !c||c==='transparent'||c==='rgba(0, 0, 0, 0)';}" +
            "var bodyBg=getComputedStyle(b).backgroundColor;" +
            "var htmlBg=getComputedStyle(document.documentElement).backgroundColor;" +
            "if(!transparent(bodyBg)||!transparent(htmlBg))return;" +
            "var m=(getComputedStyle(b).color||'').match(/\\d+/g);" +
            "var lum=m&&m.length>=3?(0.299*m[0]+0.587*m[1]+0.114*m[2])/255:0;" +
            "if(lum<0.5){document.documentElement.style.background='#ffffff';}" +
            "}" +
            "function fixSvg(){" +
            "var svgs=document.querySelectorAll('svg');" +
            "for(var i=0;i<svgs.length;i++){" +
            "var s=svgs[i];" +
            "var vb=s.getAttribute('viewBox');" +
            "var w=parseInt(s.getAttribute('width')||'0',10);" +
            "var h=parseInt(s.getAttribute('height')||'0',10);" +
            // If no viewBox but has width+height, generate one so aspect ratio is preserved
            "if(!vb&&w>0&&h>0){s.setAttribute('viewBox','0 0 '+w+' '+h);}" +
            // Remove hardcoded width/height so CSS can size it
            "if(w>0){s.removeAttribute('width');}" +
            "if(h>0){s.removeAttribute('height');}" +
            "s.style.width='100%';s.style.height='auto';" +
            "}}" +
            "function fixCanvases(){" +
            "var cs=document.querySelectorAll('canvas');" +
            "for(var i=0;i<cs.length;i++){" +
            "var c=cs[i];" +
            "var parentW=c.parentElement?c.parentElement.clientWidth:0;" +
            "if(parentW>0&&c.width>parentW&&c.getAttribute('data-canvas-fixed')!=='1'){" +
            "var ratio=parentW/c.width;" +
            "c.style.width=parentW+'px';" +
            "c.style.height=Math.floor(c.height*ratio)+'px';" +
            "}}" +
            "}" +
            "function runFixes(){fixContrast();fixSvg();fixCanvases();}" +
            "if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',runFixes);}else{runFixes();}" +
            "window.addEventListener('resize',function(){fixSvg();fixCanvases();});" +
            "})();</script>";

        /// <summary>
        /// Full detection pipeline for the Workplace Project Canvas.
        /// Tries every artifact type in priority order and returns the first match.
        /// </summary>
        public static ArtifactRenderInfo DetectForCanvas(string builderOutput, string? sandboxOutput = null)
        {
            string raw = builderOutput ?? string.Empty;

            // 1. Chart (requires sandbox execution output with CHART_OUTPUT: marker)
            if (TryCreateChartArtifact(raw, sandboxOutput, out ArtifactRenderInfo? chartArtifact))
                return chartArtifact;

            // 2. HTML (full document, DOCTYPE, fenced html block, or recognisable snippet)
            // Prefer HTML before SVG so full UI screens that contain inline SVG icons/decoration
            // render as the complete interface instead of being reduced to the first SVG node.
            if (TryCreateHtmlArtifact(raw, out ArtifactRenderInfo? htmlArtifact))
                return htmlArtifact;

            // 3. SVG (inline vector graphics)
            if (TryCreateSvgArtifact(raw, out ArtifactRenderInfo? svgArtifact))
                return svgArtifact;

            // 4. Interactive JavaScript (visual DOM/canvas code in a js/javascript fence)
            //    Previously missing from the canvas pipeline — only existed in normal chat.
            if (TryCreateInteractiveJavaScriptArtifact(raw, allowRawSource: true, out ArtifactRenderInfo? jsArtifact))
                return jsArtifact;

            // 5. Document / structured text / Markdown table (datasheets, reports, etc.)
            if (TryCreateDocumentArtifact(raw, out ArtifactRenderInfo? documentArtifact))
                return documentArtifact;

            return ArtifactRenderInfo.None(raw);
        }

        public static ArtifactRenderInfo DetectForNormalChat(string responseText)
        {
            string raw = responseText ?? string.Empty;
            if (TryCreateInteractiveJavaScriptArtifact(raw, allowRawSource: false, out ArtifactRenderInfo? jsArtifact))
                return jsArtifact;

            return ArtifactRenderInfo.None(raw);
        }

        public static string ExtractChartOutputBase64(string? sandboxOutput)
        {
            if (string.IsNullOrWhiteSpace(sandboxOutput))
                return string.Empty;

            foreach (string line in sandboxOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith(ChartPrefix, StringComparison.Ordinal))
                    return line[ChartPrefix.Length..].Trim();
            }

            return string.Empty;
        }

        public static string RemoveChartOutputLines(string? sandboxOutput)
        {
            if (string.IsNullOrWhiteSpace(sandboxOutput))
                return string.Empty;

            return string.Join("\n", sandboxOutput
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line => !line.StartsWith(ChartPrefix, StringComparison.Ordinal))
                .ToArray()).Trim();
        }

        public static string BuildOfflineSafeHtmlDocument(string html)
        {
            string sanitized = StripExternalResources(html ?? string.Empty);
            // HTML fragments are authored assuming a normal browser page (white background,
            // dark text). Wrapping them on a transparent background let them blend into the dark
            // canvas pane and become invisible ("blank" render). Give snippets a real page so
            // they read correctly; full documents keep whatever the model defined.
            string document = EnsureHtmlDocument(sanitized, "#ffffff");
            return InjectResponsiveNormalize(document);
        }

        public static string BuildSvgPreviewDocument(string svg)
        {
            string content = StripExternalResources(svg ?? string.Empty).Trim();
            string normalized = NormalizeSvgRootDimensions(content);
            string doc = "<!DOCTYPE html><html><head>" +
                "<meta charset='utf-8'>" +
                "<meta name='viewport' content='width=device-width, initial-scale=1.0'>" +
                "<style>html,body{margin:0;padding:0;background:transparent;overflow-x:hidden;overflow-y:auto;}" +
                "body{display:flex;flex-direction:column;align-items:flex-start;width:100%;}" +
                "svg{width:100%;height:auto;max-width:100%;display:block;}</style>" +
                "</head><body>" + normalized + "</body></html>";
            return InjectResponsiveNormalize(doc);
        }

        public static string BuildChartPreviewDocument(string base64)
        {
            string safe = base64 ?? string.Empty;
            return "<!DOCTYPE html><html><head>" +
                "<meta charset='utf-8'>" +
                "<meta name='viewport' content='width=device-width, initial-scale=1.0'>" +
                "<style>html,body{margin:0;background:transparent;font-family:'Segoe UI',sans-serif;overflow-x:hidden;}" +
                "body{display:flex;align-items:flex-start;justify-content:center;padding:12px;box-sizing:border-box;width:100%;}" +
                "img{max-width:100%;width:100%;height:auto;display:block;border-radius:8px;object-fit:contain;}" +
                "</style></head><body>" +
                "<img alt='Chart artifact' src='data:image/png;base64," + safe + "' />" +
                "</body></html>";
        }

        public static string BuildDocumentPreviewDocument(string markdown)
        {
            string body = Markdown.ToHtml(markdown ?? string.Empty, DocumentPipeline);
            return "<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><style>html,body{margin:0;background:transparent;}body{padding:24px 28px;color:#D1D3DF;font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;font-size:15px;line-height:1.8;}h1,h2,h3,h4,h5,h6{color:#D5DAD3;line-height:1.3;margin:1.3em 0 0.55em;}h1{font-size:2em;}h2{font-size:1.65em;}h3{font-size:1.35em;}p,ul,ol,blockquote,table,pre{margin:0 0 1em;}ul,ol{padding-left:1.5em;}blockquote{border-left:3px solid #2D3139;padding-left:12px;color:#9CA3AF;}code{background:#211F1D;border:1px solid #2D3139;border-radius:4px;padding:1px 4px;}pre{background:#171615;border:1px solid #2D3139;border-radius:8px;padding:12px;overflow:auto;}pre code{background:transparent;border:none;padding:0;}table{border-collapse:collapse;width:100%;}th,td{border:1px solid #2D3139;padding:8px 10px;text-align:left;}th{background:#211F1D;}a{color:#B8924A;text-decoration:none;}a:hover{text-decoration:underline;}</style></head><body><article>" + body + "</article></body></html>";
        }

        public static string BuildInteractiveJavaScriptDocument(string javascript)
        {
            string script = javascript ?? string.Empty;
            string doc = "<!DOCTYPE html><html><head>" +
                "<meta charset='utf-8'>" +
                "<meta name='viewport' content='width=device-width, initial-scale=1.0'>" +
                "<style>html,body{margin:0;padding:0;background:transparent;color:#D1D3DF;font-family:'Segoe UI',sans-serif;overflow-x:hidden;overflow-y:auto;}" +
                "body{min-height:100vh;padding:12px;box-sizing:border-box;width:100%;}" +
                "#artifact-root{min-height:calc(100vh - 24px);width:100%;}" +
                "#artifact-error{display:none;white-space:pre-wrap;color:#FF3B3B;font-size:13px;line-height:1.5;" +
                "border:1px solid rgba(255,59,59,0.35);background:rgba(255,59,59,0.08);border-radius:8px;padding:10px;margin-top:10px;}" +
                "</style></head>" +
                "<body>" +
                "<div id='artifact-root'></div>" +
                "<div id='artifact-error'></div>" +
                "<script>window.__artifactCompleted=false;" +
                "function showArtifactError(message){var panel=document.getElementById('artifact-error');" +
                "if(panel){panel.style.display='block';panel.textContent=message||'JavaScript artifact error.';}}" +
                "window.addEventListener('error',function(e){showArtifactError((e&&e.message)?e.message:'JavaScript artifact error.');});" +
                "setTimeout(function(){if(!window.__artifactCompleted){showArtifactError('JavaScript artifact execution exceeded the 10 second limit.');}},10000);" +
                "try{" + script + "\nwindow.__artifactCompleted=true;}" +
                "catch(error){showArtifactError((error&&error.message)?error.message:String(error));}" +
                "</script>" +
                CanvasResponsiveResizeScript +
                "</body></html>";
            return InjectResponsiveNormalize(doc);
        }

        public static bool ContainsChartLibraryReference(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            return code.Contains("matplotlib", StringComparison.OrdinalIgnoreCase)
                || code.Contains("plotly", StringComparison.OrdinalIgnoreCase)
                || code.Contains("seaborn", StringComparison.OrdinalIgnoreCase)
                || code.Contains("plt.", StringComparison.OrdinalIgnoreCase)
                || code.Contains("fig.", StringComparison.OrdinalIgnoreCase);
        }

        public static string AppendChartCaptureScriptIfNeeded(string code)
        {
            if (!ContainsChartLibraryReference(code))
                return code ?? string.Empty;

            string source = code ?? string.Empty;
            if (source.Contains(ChartPrefix, StringComparison.Ordinal))
                return source;

            const string captureBlock = @"

try:
    import io, base64
    _axiom_chart_bytes = None
    if 'plt' in globals():
        _axiom_chart_buf = io.BytesIO()
        plt.savefig(_axiom_chart_buf, format='png', bbox_inches='tight', dpi=150)
        _axiom_chart_buf.seek(0)
        _axiom_chart_bytes = _axiom_chart_buf.read()
    elif 'fig' in globals() and hasattr(fig, 'to_image'):
        _axiom_chart_bytes = fig.to_image(format='png')
    if _axiom_chart_bytes:
        print('CHART_OUTPUT:' + base64.b64encode(_axiom_chart_bytes).decode())
except Exception:
    pass
";
            return source.TrimEnd() + captureBlock;
        }

        private static bool TryCreateHtmlArtifact(string raw, out ArtifactRenderInfo? artifact)
        {
            artifact = null;
            string candidate = ExtractBestHtmlCandidate(raw);
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            artifact = new ArtifactRenderInfo
            {
                Kind = ArtifactKind.Html,
                RawSource = candidate,
                RenderSource = BuildOfflineSafeHtmlDocument(candidate),
                SaveContent = candidate,
                DisplayTitle = "HTML Preview",
                SuggestedFileExtension = ".html"
            };
            return true;
        }

        private static bool TryCreateSvgArtifact(string raw, out ArtifactRenderInfo? artifact)
        {
            artifact = null;
            Match match = SvgRegex.Match(raw ?? string.Empty);
            if (!match.Success)
                return false;

            string svg = match.Value.Trim();
            artifact = new ArtifactRenderInfo
            {
                Kind = ArtifactKind.Svg,
                RawSource = svg,
                RenderSource = BuildSvgPreviewDocument(svg),
                SaveContent = svg,
                DisplayTitle = "SVG Preview",
                SuggestedFileExtension = ".svg"
            };
            return true;
        }

        private static bool TryCreateChartArtifact(string raw, string? sandboxOutput, out ArtifactRenderInfo? artifact)
        {
            artifact = null;
            string base64 = ExtractChartOutputBase64(sandboxOutput);
            if (string.IsNullOrWhiteSpace(base64))
                return false;

            ChatMessageCodeBlock? pythonBlock = ExtractCodeBlocks(raw).FirstOrDefault(block => string.Equals(block.Language, "python", StringComparison.OrdinalIgnoreCase));
            string source = pythonBlock?.Code ?? raw;
            artifact = new ArtifactRenderInfo
            {
                Kind = ArtifactKind.Chart,
                RawSource = source,
                RenderSource = BuildChartPreviewDocument(base64),
                SaveContent = source,
                BinaryBase64 = base64,
                DisplayTitle = "Chart Preview",
                SuggestedFileExtension = ".png"
            };
            return true;
        }

        private static bool TryCreateDocumentArtifact(string raw, out ArtifactRenderInfo? artifact)
        {
            artifact = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            int headingCount = MarkdownHeadingRegex.Matches(raw).Count;
            bool hasTable = MarkdownTableRegex.IsMatch(raw);

            // Accept any of:
            //   • 3+ Markdown headings  (structured document / report)
            //   • 1+ heading + a table  (datasheet, reference sheet, comparison)
            //   • a standalone table with ≥3 data rows and no heading requirement
            bool qualifiesAsDocument = headingCount >= 3
                || (headingCount >= 1 && hasTable)
                || (hasTable && CountMarkdownTableDataRows(raw) >= 3);

            if (!qualifiesAsDocument)
                return false;

            bool isTableFocused = headingCount == 0 && hasTable;
            artifact = new ArtifactRenderInfo
            {
                Kind = ArtifactKind.Document,
                RawSource = raw.Trim(),
                RenderSource = BuildDocumentPreviewDocument(raw),
                SaveContent = raw.Trim(),
                DisplayTitle = isTableFocused ? "Table Preview" : "Document Preview",
                SuggestedFileExtension = ".md"
            };
            return true;
        }

        private static bool TryCreateInteractiveJavaScriptArtifact(string raw, bool allowRawSource, out ArtifactRenderInfo? artifact)
        {
            artifact = null;
            ChatMessageCodeBlock? jsBlock = ExtractCodeBlocks(raw).FirstOrDefault(block =>
                string.Equals(block.Language, "javascript", StringComparison.OrdinalIgnoreCase)
                || string.Equals(block.Language, "js", StringComparison.OrdinalIgnoreCase));
            if (jsBlock == null && !allowRawSource)
                return false;
            if (jsBlock == null && !RawJsExecutableSignalRegex.IsMatch(raw ?? string.Empty))
                return false;

            string code = (jsBlock?.Code ?? raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code)
                || ConsoleOnlyRegex.IsMatch(code)
                || !JsVisualSignalRegex.IsMatch(code))
            {
                return false;
            }

            artifact = new ArtifactRenderInfo
            {
                Kind = ArtifactKind.InteractiveJavaScript,
                RawSource = code,
                RenderSource = BuildInteractiveJavaScriptDocument(code),
                SaveContent = BuildInteractiveJavaScriptDocument(code),
                DisplayTitle = "Interactive Preview",
                SuggestedFileExtension = ".html"
            };
            return true;
        }

        private static string ExtractBestHtmlCandidate(string raw)
        {
            string source = raw ?? string.Empty;
            Match documentMatch = HtmlDocumentRegex.Match(source);
            if (documentMatch.Success)
                return documentMatch.Value.Trim();

            int doctypeStart = source.IndexOf("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase);
            if (doctypeStart >= 0)
            {
                string doctypeHtml = source[doctypeStart..].Trim();
                if (LooksLikeHtmlSnippet(doctypeHtml) || doctypeHtml.Contains("<html", StringComparison.OrdinalIgnoreCase))
                    return doctypeHtml;
            }

            int htmlStart = source.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            if (htmlStart >= 0)
            {
                string htmlTail = source[htmlStart..].Trim();
                if (LooksLikeHtmlSnippet(htmlTail) || htmlTail.Contains("<body", StringComparison.OrdinalIgnoreCase))
                    return htmlTail;
            }

            ChatMessageCodeBlock? htmlBlock = ExtractCodeBlocks(source).FirstOrDefault(block => string.Equals(block.Language, "html", StringComparison.OrdinalIgnoreCase));
            if (htmlBlock != null)
            {
                string fencedHtmlContent = htmlBlock.Code.Trim();
                if (LooksLikeHtmlSnippet(fencedHtmlContent))
                    return fencedHtmlContent;
            }

            Match snippetMatch = HtmlSnippetRegex.Match(source);
            return snippetMatch.Success && LooksLikeHtmlSnippet(snippetMatch.Value) ? snippetMatch.Value.Trim() : string.Empty;
        }

        private static bool LooksLikeHtmlSnippet(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string trimmed = content.Trim();
            return trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<body", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<div", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<canvas", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<table", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<form", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<input", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<button", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<section", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<article", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<ul", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<ol", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<p ", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<p>", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<h1", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<h2", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<h3", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<script", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("<style", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountMarkdownTableDataRows(string raw)
        {
            // Counts the number of data rows (non-header, non-separator) across all tables in the text.
            int dataRows = 0;
            bool inTable = false;
            bool pastSeparator = false;
            foreach (string line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith('|'))
                {
                    inTable = false;
                    pastSeparator = false;
                    continue;
                }

                if (!inTable)
                {
                    inTable = true;
                    pastSeparator = false;
                    continue; // this is the header row
                }

                if (!pastSeparator && trimmed.Replace("|", "").Replace("-", "").Replace(":", "").Replace(" ", "").Length == 0)
                {
                    pastSeparator = true;
                    continue; // this is the separator row
                }

                if (pastSeparator)
                    dataRows++;
            }
            return dataRows;
        }

        private static List<ChatMessageCodeBlock> ExtractCodeBlocks(string content)
        {
            var blocks = new List<ChatMessageCodeBlock>();
            foreach (Match match in FencedCodeBlockRegex.Matches(content ?? string.Empty))
            {
                string language = match.Groups["language"].Value.Trim();
                string code = match.Groups["code"].Value.Replace("\r\n", "\n").Trim();
                if (!string.IsNullOrWhiteSpace(code))
                    blocks.Add(new ChatMessageCodeBlock(language, code));
            }

            return blocks;
        }

        private static string StripExternalResources(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            string cleaned = ExternalResourceAttributeRegex.Replace(html, match =>
            {
                string value = match.Groups["value"].Value.Trim();
                return IsExternalResource(value) ? string.Empty : match.Value;
            });

            cleaned = ExternalCssUrlRegex.Replace(cleaned, match =>
            {
                string value = match.Groups["value"].Value.Trim();
                return IsExternalResource(value) ? "url()" : match.Value;
            });

            return cleaned;
        }

        private static bool IsExternalResource(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("//", StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureHtmlDocument(string html, string background)
        {
            string trimmed = (html ?? string.Empty).Trim();
            if (HtmlDocumentRegex.IsMatch(trimmed))
                return trimmed;

            // When the snippet sits on a light page, also give it a sensible default text color so
            // unstyled content is legible (a bare snippet inherits no color otherwise).
            bool lightBackground = background.StartsWith("#f", StringComparison.OrdinalIgnoreCase)
                || background.Equals("white", StringComparison.OrdinalIgnoreCase);
            string defaultColor = lightBackground ? "color:#1a1a1a;" : string.Empty;

            return "<!DOCTYPE html><html><head>" +
                "<meta charset='utf-8'>" +
                "<meta name='viewport' content='width=device-width, initial-scale=1.0'>" +
                "<style>html,body{margin:0;padding:0;background:" + background + ";" + defaultColor + "width:100%;overflow-x:hidden;}" +
                "*,*::before,*::after{box-sizing:border-box;}" +
                "img,canvas{max-width:100%;height:auto;}</style>" +
                "</head><body>" + trimmed + "</body></html>";
        }

        /// <summary>
        /// Injects the responsive normalize stylesheet and resize script into a complete HTML
        /// document. The style tag is inserted just before </head> so it applies after the
        /// model's own CSS; the resize script is inserted just before </body> so it runs after
        /// all DOM elements and inline scripts have executed.
        /// </summary>
        private static string InjectResponsiveNormalize(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html;

            // Skip if already injected
            if (html.Contains("_canvas_normalize", StringComparison.Ordinal))
                return html;

            int headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            int bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

            string result = html;

            if (headClose >= 0)
                result = result[..headClose] + CanvasResponsiveNormalize + result[headClose..];

            // Recalculate bodyClose offset after head injection
            bodyClose = result.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyClose >= 0)
                result = result[..bodyClose] + CanvasResponsiveResizeScript + result[bodyClose..];

            return result;
        }

        /// <summary>
        /// Strips hardcoded width/height attributes from the root SVG element and replaces them
        /// with a viewBox (if not already present) so CSS can control the rendered size.
        /// This prevents models from generating SVGs like width="800" height="600" that overflow
        /// the ~526px WebView2 canvas pane.
        /// </summary>
        private static string NormalizeSvgRootDimensions(string svg)
        {
            if (string.IsNullOrWhiteSpace(svg))
                return svg;

            Match svgOpenTag = SvgRootAttributeRegex.Match(svg);
            if (!svgOpenTag.Success)
                return svg;

            string attrs = svgOpenTag.Groups["attrs"].Value;

            // Extract existing width/height values before stripping
            var widthMatch = Regex.Match(attrs, @"\bwidth\s*=\s*['""]?(\d+(?:\.\d+)?)['""]?", RegexOptions.IgnoreCase);
            var heightMatch = Regex.Match(attrs, @"\bheight\s*=\s*['""]?(\d+(?:\.\d+)?)['""]?", RegexOptions.IgnoreCase);
            bool hasViewBox = attrs.Contains("viewBox", StringComparison.OrdinalIgnoreCase);

            double w = widthMatch.Success && double.TryParse(widthMatch.Groups[1].Value, out double pw) ? pw : 0;
            double h = heightMatch.Success && double.TryParse(heightMatch.Groups[1].Value, out double ph) ? ph : 0;

            // Generate viewBox from width/height if missing — preserves aspect ratio when scaled
            string newAttrs = SvgFixedDimensionRegex.Replace(attrs, string.Empty).Trim();
            if (!hasViewBox && w > 0 && h > 0)
                newAttrs += $" viewBox=\"0 0 {w} {h}\"";

            // Replace the old opening tag with the cleaned version
            string newOpenTag = "<svg " + newAttrs.Trim() + ">";
            return svg[..svgOpenTag.Index] + newOpenTag + svg[(svgOpenTag.Index + svgOpenTag.Length)..];
        }
    }
}
