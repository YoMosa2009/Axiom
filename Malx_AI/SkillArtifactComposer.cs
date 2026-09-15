using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public sealed class ComposedSlide
    {
        public string Title { get; set; } = "";
        public List<string> Bullets { get; } = new();
        public string Notes { get; set; } = "";
        public List<ComposedChartPoint> Chart { get; } = new();
    }

    public sealed record ComposedChartPoint(string Label, double Value);

    public sealed class ComposedOutline
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public List<ComposedSlide> Slides { get; } = new();
        public List<ComposedChartPoint> Chart { get; } = new();
        public string ChartTitle { get; set; } = "";

        public bool HasSlides => Slides.Count > 0;
        public bool HasChart => Chart.Count > 0;
    }

    /// <summary>
    /// Builds the rendered artifact from a plain-text outline instead of asking the model for
    /// HTML.
    /// </summary>
    /// <remarks>
    /// A 0.5B model cannot author several hundred lines of correct, self-contained HTML, but it
    /// can reliably emit "SLIDE: title" followed by bullets. Axiom therefore owns the layout for
    /// small models, and keeps the same composer as a fallback for large ones whose HTML did not
    /// come back renderable — so a slide-deck request produces a slide deck at every model size.
    /// </remarks>
    public static class SkillArtifactComposer
    {
        private static readonly Regex LeadingBulletRegex = new(@"^\s*(?:[-*•·]|\d+[.)])\s+", RegexOptions.Compiled);
        private static readonly Regex ChartPointRegex = new(
            @"^\s*(?<label>[^|:]{1,60}?)\s*[|:]\s*(?<value>-?\d[\d,_]*(?:\.\d+)?)\s*(?<unit>%|[A-Za-z$€£]{0,6})\s*$",
            RegexOptions.Compiled);
        private static readonly Regex MarkdownEmphasisRegex = new(@"\*\*(?<text>[^*]+)\*\*|__(?<text2>[^_]+)__", RegexOptions.Compiled);

        /// <summary>The outline format small models are asked to produce. Kept deliberately tiny.</summary>
        public const string OutlineFormatDescription = """
            TITLE: <deck or document title>
            SUBTITLE: <one short line>
            SLIDE: <the point this slide makes>
            - <short supporting line>
            - <short supporting line>
            NOTE: <what to say out loud on this slide>
            SLIDE: <the point the next slide makes>
            - <short supporting line>
            """;

        public const string ChartFormatDescription = """
            TITLE: <what the chart shows>
            CHART: <axis label and units>
            <category name> | <number>
            <category name> | <number>
            """;

        /// <summary>
        /// Builds the artifact a Skill promised out of whatever the model actually returned.
        /// Used as the primary path for small models and as the salvage path for large ones.
        /// </summary>
        /// <returns>False when the text holds too little structure to render honestly.</returns>
        public static bool TryCompose(string? smallModelFormat, string? responseText, out string html)
        {
            html = string.Empty;
            if (string.IsNullOrWhiteSpace(responseText))
                return false;

            ComposedOutline outline = ParseOutline(responseText);
            switch (smallModelFormat)
            {
                case SkillSmallModelFormats.Outline when CanComposeDeck(outline):
                    html = ComposeSlideDeck(outline);
                    return true;

                case SkillSmallModelFormats.Chart when CanComposeChart(outline):
                    html = ComposeChartReport(outline);
                    return true;

                default:
                    // Markdown Skills need no composer: the canvas renders Markdown already.
                    return false;
            }
        }

        /// <summary>
        /// Parses the outline format. Unknown lines attach to the current slide as bullets, so a
        /// model that forgets the "-" prefix still produces a usable deck.
        /// </summary>
        public static ComposedOutline ParseOutline(string? text)
        {
            var outline = new ComposedOutline();
            if (string.IsNullOrWhiteSpace(text))
                return outline;

            ComposedSlide? current = null;
            bool collectingChart = false;

            foreach (string rawLine in StripCodeFences(text).Split('\n'))
            {
                string line = rawLine.Replace("\r", string.Empty).Trim();
                if (line.Length == 0)
                    continue;

                if (TryReadDirective(line, "TITLE", out string title))
                {
                    if (outline.Title.Length == 0)
                        outline.Title = title;
                    collectingChart = false;
                    continue;
                }

                if (TryReadDirective(line, "SUBTITLE", out string subtitle))
                {
                    outline.Subtitle = subtitle;
                    collectingChart = false;
                    continue;
                }

                if (TryReadDirective(line, "SLIDE", out string slideTitle))
                {
                    current = new ComposedSlide { Title = slideTitle };
                    outline.Slides.Add(current);
                    collectingChart = false;
                    continue;
                }

                if (TryReadDirective(line, "NOTE", out string note) || TryReadDirective(line, "NOTES", out note))
                {
                    if (current != null)
                        current.Notes = string.IsNullOrEmpty(current.Notes) ? note : current.Notes + " " + note;
                    collectingChart = false;
                    continue;
                }

                if (TryReadDirective(line, "CHART", out string chartTitle))
                {
                    if (outline.ChartTitle.Length == 0)
                        outline.ChartTitle = chartTitle;
                    collectingChart = true;
                    continue;
                }

                // Markdown headings are accepted as slide breaks: models trained on Markdown reach
                // for "## Heading" far more readily than for a bespoke keyword.
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    string heading = line.TrimStart('#').Trim();
                    if (heading.Length == 0)
                        continue;

                    if (outline.Title.Length == 0 && !line.StartsWith("##", StringComparison.Ordinal))
                        outline.Title = heading;
                    else
                    {
                        current = new ComposedSlide { Title = heading };
                        outline.Slides.Add(current);
                    }

                    collectingChart = false;
                    continue;
                }

                Match point = ChartPointRegex.Match(line);
                if (point.Success && (collectingChart || current?.Chart.Count > 0))
                {
                    var parsed = new ComposedChartPoint(
                        CleanInline(point.Groups["label"].Value),
                        ParseNumber(point.Groups["value"].Value));
                    if (collectingChart && current == null)
                        outline.Chart.Add(parsed);
                    else
                        (current?.Chart ?? outline.Chart).Add(parsed);
                    continue;
                }

                if (collectingChart && point.Success)
                    continue;

                string bullet = CleanBullet(LeadingBulletRegex.Replace(line, string.Empty));
                if (bullet.Length == 0)
                    continue;

                if (current == null)
                {
                    // Prose before the first SLIDE line becomes the subtitle rather than vanishing.
                    if (outline.Subtitle.Length == 0)
                        outline.Subtitle = bullet;
                    continue;
                }

                current.Bullets.Add(bullet);
            }

            return outline;
        }

        /// <summary>True when the outline holds enough structure to be worth rendering.</summary>
        public static bool CanComposeDeck(ComposedOutline outline) =>
            outline.Slides.Count(slide => slide.Title.Length > 0 || slide.Bullets.Count > 0) >= 2;

        public static bool CanComposeChart(ComposedOutline outline) => outline.Chart.Count >= 2;

        /// <summary>
        /// Renders a navigable 16:9 deck. Self-contained and offline: no fonts, scripts, or images
        /// are fetched, which is what the Project Canvas WebView2 requires.
        /// </summary>
        public static string ComposeSlideDeck(ComposedOutline outline)
        {
            List<ComposedSlide> slides = outline.Slides
                .Where(slide => slide.Title.Length > 0 || slide.Bullets.Count > 0)
                .ToList();

            string deckTitle = outline.Title.Length > 0 ? outline.Title : "Presentation";
            var body = new StringBuilder();

            body.Append("<section class=\"slide title-slide\" data-notes=\"")
                .Append(Attr("Title slide."))
                .Append("\"><div class=\"stage\"><div class=\"title-block\"><h1>")
                .Append(Html(deckTitle))
                .Append("</h1>");
            if (outline.Subtitle.Length > 0)
                body.Append("<p class=\"subtitle\">").Append(Html(outline.Subtitle)).Append("</p>");
            body.Append("<span class=\"rule\"></span></div></div></section>");

            foreach (ComposedSlide slide in slides)
            {
                body.Append("<section class=\"slide\" data-notes=\"").Append(Attr(slide.Notes)).Append("\"><div class=\"stage\">");
                if (slide.Title.Length > 0)
                    body.Append("<h2>").Append(Html(slide.Title)).Append("</h2>");

                body.Append("<div class=\"content\">");
                if (slide.Bullets.Count > 0)
                {
                    body.Append("<ul>");
                    foreach (string bullet in slide.Bullets)
                        body.Append("<li>").Append(Inline(bullet)).Append("</li>");
                    body.Append("</ul>");
                }

                if (slide.Chart.Count >= 2)
                    body.Append(ComposeBarChartSvg(slide.Chart, string.Empty));

                body.Append("</div></div></section>");
            }

            return BuildDeckDocument(deckTitle, body.ToString(), slides.Count + 1);
        }

        /// <summary>Renders a titled bar chart with the figures kept underneath as a real table.</summary>
        public static string ComposeChartReport(ComposedOutline outline)
        {
            string title = outline.Title.Length > 0 ? outline.Title : "Chart";
            var body = new StringBuilder();
            body.Append("<h1>").Append(Html(title)).Append("</h1>");
            if (outline.Subtitle.Length > 0)
                body.Append("<p class=\"lede\">").Append(Html(outline.Subtitle)).Append("</p>");

            body.Append(ComposeBarChartSvg(outline.Chart, outline.ChartTitle));

            body.Append("<table><thead><tr><th>Category</th><th class=\"num\">Value</th></tr></thead><tbody>");
            foreach (ComposedChartPoint p in outline.Chart)
            {
                body.Append("<tr><td>").Append(Html(p.Label)).Append("</td><td class=\"num\">")
                    .Append(Html(FormatNumber(p.Value))).Append("</td></tr>");
            }
            body.Append("</tbody></table>");

            return BuildReportDocument(title, body.ToString());
        }

        /// <summary>Inline SVG bar chart: the only charting available with no network access.</summary>
        public static string ComposeBarChartSvg(IReadOnlyList<ComposedChartPoint> points, string axisLabel)
        {
            const int width = 640;
            const int rowHeight = 34;
            const int labelWidth = 150;
            const int rightPad = 64;
            int height = points.Count * rowHeight + 28;
            double max = points.Max(p => Math.Abs(p.Value));
            if (max <= 0)
                max = 1;
            double barSpace = width - labelWidth - rightPad;

            var svg = new StringBuilder();
            svg.Append("<div class=\"chart\">");
            if (!string.IsNullOrWhiteSpace(axisLabel))
                svg.Append("<p class=\"chart-title\">").Append(Html(axisLabel)).Append("</p>");
            svg.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
               .Append("\" role=\"img\" preserveAspectRatio=\"xMidYMid meet\">");

            for (int i = 0; i < points.Count; i++)
            {
                ComposedChartPoint point = points[i];
                int y = 14 + (i * rowHeight);
                double barWidth = Math.Max(2.0, Math.Abs(point.Value) / max * barSpace);

                svg.Append("<text x=\"").Append(labelWidth - 10).Append("\" y=\"").Append(y + 15)
                   .Append("\" text-anchor=\"end\" class=\"lbl\">").Append(Html(Truncate(point.Label, 24))).Append("</text>");
                svg.Append("<rect x=\"").Append(labelWidth).Append("\" y=\"").Append(y)
                   .Append("\" width=\"").Append(Num(barWidth)).Append("\" height=\"20\" rx=\"4\" class=\"bar\"/>");
                svg.Append("<text x=\"").Append(Num(labelWidth + barWidth + 8)).Append("\" y=\"").Append(y + 15)
                   .Append("\" class=\"val\">").Append(Html(FormatNumber(point.Value))).Append("</text>");
            }

            svg.Append("<line x1=\"").Append(labelWidth).Append("\" y1=\"6\" x2=\"").Append(labelWidth)
               .Append("\" y2=\"").Append(height - 8).Append("\" class=\"axis\"/>");
            svg.Append("</svg></div>");
            return svg.ToString();
        }

        private static string BuildDeckDocument(string title, string slidesHtml, int slideCount) =>
            $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{{Html(title)}}</title>
            <style>
            :root{--bg:#141312;--slide:#1E1B19;--edge:#3A342D;--ink:#F0EAE2;--dim:#B3A99C;--accent:#C79F52;}
            *{box-sizing:border-box;}
            body{margin:0;background:var(--bg);color:var(--ink);font-family:"Segoe UI",system-ui,sans-serif;}
            .deck{padding:12px;}
            .slide{display:none;}
            .slide.active{display:block;}
            .stage{aspect-ratio:16/9;background:var(--slide);border:1px solid var(--edge);border-radius:10px;
              padding:5.5% 6%;display:flex;flex-direction:column;justify-content:center;overflow:hidden;
              container-type:inline-size;}
            h1{font-size:clamp(20px,6cqi,52px);line-height:1.12;margin:0;font-weight:600;letter-spacing:-0.01em;}
            h2{font-size:clamp(15px,4.2cqi,32px);line-height:1.2;margin:0 0 3.2cqi;font-weight:600;color:var(--ink);}
            .subtitle{font-size:clamp(11px,2.6cqi,20px);color:var(--dim);margin:2.5cqi 0 0;font-weight:400;}
            .rule{display:block;width:12cqi;height:3px;background:var(--accent);border-radius:2px;margin-top:4cqi;}
            .title-slide .stage{justify-content:center;}
            .content{flex:1;display:flex;flex-direction:column;justify-content:center;gap:2cqi;min-height:0;}
            ul{margin:0;padding:0;list-style:none;display:flex;flex-direction:column;gap:2.1cqi;}
            li{position:relative;padding-left:3.6cqi;font-size:clamp(11px,2.7cqi,20px);line-height:1.45;color:var(--ink);}
            li::before{content:"";position:absolute;left:0;top:0.62em;width:1.5cqi;height:1.5cqi;min-width:5px;min-height:5px;
              border-radius:50%;background:var(--accent);}
            li b{color:var(--accent);font-weight:600;}
            .chart{margin-top:1cqi;}
            .chart svg{width:100%;height:auto;}
            .bar{fill:var(--accent);}
            .lbl{fill:var(--dim);font-size:13px;font-family:"Segoe UI",system-ui,sans-serif;}
            .val{fill:var(--ink);font-size:13px;font-family:"Segoe UI",system-ui,sans-serif;}
            .axis{stroke:var(--edge);stroke-width:1;}
            .bar-row{display:flex;align-items:center;gap:10px;}
            .controls{display:flex;align-items:center;gap:10px;margin-top:10px;}
            button{background:#262220;color:var(--ink);border:1px solid var(--edge);border-radius:7px;
              padding:6px 13px;font-size:12px;font-family:inherit;cursor:pointer;}
            button:hover{border-color:var(--accent);color:var(--accent);}
            .counter{color:var(--dim);font-size:12px;font-variant-numeric:tabular-nums;}
            .hint{color:#7D746A;font-size:11px;margin-left:auto;}
            .notes{margin-top:10px;padding:11px 13px;background:#1B1917;border:1px solid var(--edge);
              border-left:3px solid var(--accent);border-radius:7px;color:var(--dim);font-size:12px;line-height:1.55;}
            .notes[hidden]{display:none;}
            .notes strong{color:var(--ink);display:block;margin-bottom:4px;font-size:11px;text-transform:uppercase;letter-spacing:0.06em;}
            @media print{body{background:#fff;}.controls,.hint{display:none;}.slide{display:block;page-break-after:always;} }
            </style>
            </head>
            <body>
            <div class="deck" id="deck">{{slidesHtml}}</div>
            <div class="controls">
              <button id="prev" type="button">&#8592; Prev</button>
              <button id="next" type="button">Next &#8594;</button>
              <span class="counter" id="counter">1 / {{slideCount}}</span>
              <span class="hint">&#8592; &#8594; to move &#183; N for notes</span>
            </div>
            <div class="notes" id="notes" hidden><strong>Speaker notes</strong><span id="notesText"></span></div>
            <script>
            (function(){
              var slides = Array.prototype.slice.call(document.querySelectorAll('.slide'));
              var index = 0, notesOpen = false;
              var notes = document.getElementById('notes');
              var notesText = document.getElementById('notesText');
              var counter = document.getElementById('counter');
              function render(){
                slides.forEach(function(s, i){ s.classList.toggle('active', i === index); });
                counter.textContent = (index + 1) + ' / ' + slides.length;
                var text = slides[index].getAttribute('data-notes') || '';
                notesText.textContent = text || 'No notes for this slide.';
                notes.hidden = !notesOpen;
              }
              function move(step){ index = Math.min(slides.length - 1, Math.max(0, index + step)); render(); }
              document.getElementById('prev').addEventListener('click', function(){ move(-1); });
              document.getElementById('next').addEventListener('click', function(){ move(1); });
              document.addEventListener('keydown', function(e){
                if (e.key === 'ArrowRight' || e.key === ' ' || e.key === 'PageDown') { move(1); e.preventDefault(); }
                else if (e.key === 'ArrowLeft' || e.key === 'PageUp') { move(-1); e.preventDefault(); }
                else if (e.key === 'n' || e.key === 'N') { notesOpen = !notesOpen; render(); }
              });
              render();
            })();
            </script>
            </body>
            </html>
            """;

        private static string BuildReportDocument(string title, string bodyHtml) =>
            $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{{Html(title)}}</title>
            <style>
            :root{--bg:#141312;--edge:#3A342D;--ink:#F0EAE2;--dim:#B3A99C;--accent:#C79F52;}
            body{margin:0;padding:22px 20px 32px;background:var(--bg);color:var(--ink);
              font-family:"Segoe UI",system-ui,sans-serif;line-height:1.55;}
            h1{font-size:22px;margin:0 0 6px;font-weight:600;letter-spacing:-0.01em;}
            .lede{color:var(--dim);margin:0 0 20px;font-size:14px;}
            .chart{margin:0 0 22px;}
            .chart svg{width:100%;height:auto;}
            .chart-title{font-size:12px;color:var(--dim);margin:0 0 8px;text-transform:uppercase;letter-spacing:0.06em;}
            .bar{fill:var(--accent);}
            .lbl{fill:var(--dim);font-size:13px;font-family:"Segoe UI",system-ui,sans-serif;}
            .val{fill:var(--ink);font-size:13px;font-family:"Segoe UI",system-ui,sans-serif;}
            .axis{stroke:var(--edge);stroke-width:1;}
            table{border-collapse:collapse;width:100%;font-size:13px;}
            th,td{border-bottom:1px solid var(--edge);padding:7px 10px;text-align:left;}
            th{color:var(--dim);font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:0.06em;}
            .num{text-align:right;font-variant-numeric:tabular-nums;}
            @media print{body{background:#fff;color:#111;} }
            </style>
            </head>
            <body>{{bodyHtml}}</body>
            </html>
            """;

        private static bool TryReadDirective(string line, string keyword, out string value)
        {
            value = string.Empty;

            // Models bold their headings as readily as they write them plain, so look past any
            // leading Markdown decoration before matching the keyword.
            line = line.TrimStart('*', '#', ' ');
            if (line.Length <= keyword.Length || !line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return false;

            int cursor = keyword.Length;
            // Models write "SLIDE 3:" and "**SLIDE:**" as readily as "SLIDE:" — accept all of them.
            while (cursor < line.Length && (char.IsDigit(line[cursor]) || line[cursor] == ' ' || line[cursor] == '*'))
                cursor++;
            if (cursor >= line.Length || (line[cursor] != ':' && line[cursor] != '-' && line[cursor] != '—'))
                return false;

            value = CleanInline(line[(cursor + 1)..]);
            return true;
        }

        private static string StripCodeFences(string text)
        {
            var result = new StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    continue;
                result.Append(line).Append('\n');
            }
            return result.ToString();
        }

        /// <summary>Strips the Markdown decoration models wrap titles in.</summary>
        private static string CleanInline(string value) => (value ?? string.Empty).Trim().Trim('*', '#', '"').Trim();

        /// <summary>
        /// Bullets keep their markers: the deck renders **bold** as emphasis, so stripping the
        /// asterisks here would leave a half-open pair in the middle of the line.
        /// </summary>
        private static string CleanBullet(string value) => (value ?? string.Empty).Trim().Trim('"').Trim();

        private static double ParseNumber(string value)
        {
            string cleaned = value.Replace(",", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal);
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
        }

        private static string FormatNumber(double value) =>
            value == Math.Floor(value) && Math.Abs(value) < 1_000_000_000
                ? value.ToString("N0", CultureInfo.InvariantCulture)
                : value.ToString("N2", CultureInfo.InvariantCulture);

        private static string Num(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

        private static string Html(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private static string Attr(string value) => Html(value).Replace("\n", " ", StringComparison.Ordinal);

        /// <summary>Encodes a bullet, then restores **bold** as real emphasis.</summary>
        private static string Inline(string value) =>
            MarkdownEmphasisRegex.Replace(Html(value), match =>
            {
                string inner = match.Groups["text"].Success ? match.Groups["text"].Value : match.Groups["text2"].Value;
                return "<b>" + inner + "</b>";
            });
    }
}
