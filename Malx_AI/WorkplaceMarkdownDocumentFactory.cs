using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;

namespace Malx_AI
{
    internal static class WorkplaceMarkdownDocumentFactory
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        private static readonly Brush TextBrush = FrozenBrush(0xED, 0xE8, 0xE3);
        private static readonly Brush SecondaryBrush = FrozenBrush(0xB0, 0xA8, 0x9F);
        private static readonly Brush CodeBackgroundBrush = FrozenBrush(0x17, 0x16, 0x15);
        private static readonly Brush CodeBorderBrush = FrozenBrush(0x30, 0x2D, 0x2A);
        private static readonly FontFamily BodyFont = new("Segoe UI");
        private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

        public static FlowDocument Create(string? markdown)
        {
            var document = new FlowDocument
            {
                Background = Brushes.Transparent,
                Foreground = TextBrush,
                FontFamily = BodyFont,
                FontSize = 12.5,
                LineHeight = 18,
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left
            };

            if (string.IsNullOrEmpty(markdown))
                return document;

            MarkdownDocument parsed = Markdown.Parse(markdown, Pipeline);
            foreach (MdBlock block in parsed)
                document.Blocks.Add(RenderBlock(block));

            return document;
        }

        private static WpfBlock RenderBlock(MdBlock block)
        {
            return block switch
            {
                HeadingBlock heading => RenderHeading(heading),
                ParagraphBlock paragraph => RenderParagraph(paragraph),
                FencedCodeBlock code => RenderCodeBlock(code),
                CodeBlock code => RenderCodeBlock(code),
                ListBlock list => RenderList(list),
                QuoteBlock quote => RenderContainer(quote),
                ThematicBreakBlock => new Paragraph(new Run("────────"))
                {
                    Foreground = SecondaryBrush,
                    Margin = new Thickness(0, 4, 0, 4)
                },
                ContainerBlock container => RenderContainer(container),
                LeafBlock leaf => new Paragraph(new Run(leaf.Lines.ToString())) { Margin = new Thickness(0) },
                _ => new Paragraph(new Run(block.ToString() ?? string.Empty)) { Margin = new Thickness(0) }
            };
        }

        private static Paragraph RenderHeading(HeadingBlock heading)
        {
            var paragraph = new Paragraph
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = heading.Level switch { 1 => 18, 2 => 16, _ => 14 },
                Margin = new Thickness(0, heading.Level <= 2 ? 8 : 5, 0, 3)
            };
            AppendInlines(paragraph.Inlines, heading.Inline);
            return paragraph;
        }

        private static Paragraph RenderParagraph(ParagraphBlock paragraphBlock)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AppendInlines(paragraph.Inlines, paragraphBlock.Inline);
            return paragraph;
        }

        private static Section RenderCodeBlock(CodeBlock codeBlock)
        {
            string code = codeBlock.Lines.ToString().TrimEnd('\r', '\n');
            var paragraph = new Paragraph(new Run(code))
            {
                FontFamily = CodeFont,
                FontSize = 11,
                LineHeight = 15,
                Margin = new Thickness(0),
                Padding = new Thickness(9, 7, 9, 7),
                Background = CodeBackgroundBrush,
                BorderBrush = CodeBorderBrush,
                BorderThickness = new Thickness(1)
            };
            return new Section(paragraph) { Margin = new Thickness(0, 4, 0, 6) };
        }

        private static System.Windows.Documents.List RenderList(ListBlock listBlock)
        {
            var list = new System.Windows.Documents.List
            {
                MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(18, 1, 0, 4),
                Padding = new Thickness(0)
            };
            if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start))
                list.StartIndex = start;

            foreach (MdBlock child in listBlock)
            {
                if (child is not ListItemBlock itemBlock)
                    continue;

                var item = new ListItem { Margin = new Thickness(0, 0, 0, 2) };
                foreach (MdBlock itemChild in itemBlock)
                    item.Blocks.Add(RenderBlock(itemChild));
                list.ListItems.Add(item);
            }
            return list;
        }

        private static Section RenderContainer(ContainerBlock container)
        {
            var section = new Section { Margin = new Thickness(0) };
            foreach (MdBlock child in container)
                section.Blocks.Add(RenderBlock(child));
            return section;
        }

        private static void AppendInlines(InlineCollection target, ContainerInline? container)
        {
            if (container is null)
                return;

            for (MdInline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        target.Add(new Run(literal.Content.ToString()));
                        break;
                    case CodeInline code:
                        target.Add(new Run(code.Content)
                        {
                            FontFamily = CodeFont,
                            FontSize = 11,
                            Background = CodeBackgroundBrush
                        });
                        break;
                    case EmphasisInline emphasis:
                        Span emphasisSpan = emphasis.DelimiterCount >= 2
                            ? new Bold()
                            : new Italic();
                        AppendInlines(emphasisSpan.Inlines, emphasis);
                        target.Add(emphasisSpan);
                        break;
                    case LineBreakInline:
                        target.Add(new LineBreak());
                        break;
                    case LinkInline link:
                        var linkSpan = new Span { Foreground = SecondaryBrush };
                        AppendInlines(linkSpan.Inlines, link);
                        target.Add(linkSpan);
                        break;
                    case ContainerInline nested:
                        var span = new Span();
                        AppendInlines(span.Inlines, nested);
                        target.Add(span);
                        break;
                    default:
                        target.Add(new Run(inline.ToString() ?? string.Empty));
                        break;
                }
            }
        }

        private static Brush FrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
