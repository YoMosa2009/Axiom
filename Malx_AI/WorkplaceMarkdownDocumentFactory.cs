using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using WpfBlock = System.Windows.Documents.Block;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace Malx_AI
{
    internal static class WorkplaceMarkdownDocumentFactory
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        private static readonly Brush TextBrush = FrozenBrush(0xED, 0xE8, 0xE3);
        private static readonly Brush SecondaryBrush = FrozenBrush(0xB8, 0xB0, 0xA6);
        private static readonly Brush HeadingGoldBrush = FrozenBrush(0xF5, 0xE6, 0xC8);
        private static readonly Brush SubHeadingBrush = FrozenBrush(0xD8, 0xB5, 0x6B);
        private static readonly Brush CodeBackgroundBrush = FrozenBrush(0x16, 0x15, 0x13);
        private static readonly Brush InlineCodeBackgroundBrush = FrozenBrush(0x28, 0x25, 0x22);
        private static readonly Brush InlineCodeForegroundBrush = FrozenBrush(0xF5, 0xD5, 0x91);
        private static readonly Brush CodeBorderBrush = FrozenBrush(0x30, 0x2D, 0x2A);
        private static readonly Brush TableHeaderBrush = FrozenBrush(0x26, 0x23, 0x20);
        private static readonly Brush TableAltRowBrush = FrozenBrush(0x1F, 0x1D, 0x1B);
        private static readonly Brush TableRowDividerBrush = FrozenBrush(0x28, 0x25, 0x22);
        private static readonly Brush QuoteBackgroundBrush = FrozenBrush(0x1E, 0x1C, 0x1A);
        private static readonly Brush QuoteBorderBrush = FrozenBrush(0xB8, 0x92, 0x4A);
        private static FontFamily BodyFont => AppFonts.Ui;
        private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

        public static FlowDocument Create(string? markdown)
        {
            var document = new FlowDocument
            {
                Background = Brushes.Transparent,
                Foreground = TextBrush,
                FontFamily = BodyFont,
                FontSize = 12.5,
                LineHeight = 20,
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
                QuoteBlock quote => RenderQuote(quote),
                MdTable table => RenderTable(table),
                ThematicBreakBlock => new Paragraph(new Run("────────────────"))
                {
                    Foreground = CodeBorderBrush,
                    Margin = new Thickness(0, 6, 0, 6)
                },
                ContainerBlock container => RenderContainer(container),
                LeafBlock leaf => new Paragraph(new Run(leaf.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 4) },
                _ => new Paragraph(new Run(block.ToString() ?? string.Empty)) { Margin = new Thickness(0, 0, 0, 4) }
            };
        }

        private static Paragraph RenderHeading(HeadingBlock heading)
        {
            var paragraph = new Paragraph
            {
                FontWeight = heading.Level == 1 ? FontWeights.Bold : FontWeights.SemiBold,
                FontSize = heading.Level switch { 1 => 15.5, 2 => 13.5, _ => 12.5 },
                Foreground = heading.Level switch { 1 => HeadingGoldBrush, 2 => TextBrush, _ => SubHeadingBrush },
                Margin = new Thickness(0, heading.Level <= 2 ? 10 : 8, 0, heading.Level <= 2 ? 5 : 4)
            };
            AppendInlines(paragraph.Inlines, heading.Inline);
            return paragraph;
        }

        private static Paragraph RenderParagraph(ParagraphBlock paragraphBlock)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
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
                LineHeight = 16,
                Margin = new Thickness(0),
                Padding = new Thickness(12, 8, 12, 8),
                Background = CodeBackgroundBrush,
                BorderBrush = CodeBorderBrush,
                BorderThickness = new Thickness(1)
            };
            return new Section(paragraph) { Margin = new Thickness(0, 4, 0, 8) };
        }

        private static Section RenderQuote(QuoteBlock quote)
        {
            var section = new Section
            {
                Background = QuoteBackgroundBrush,
                BorderBrush = QuoteBorderBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 8, 12, 4),
                Margin = new Thickness(0, 6, 0, 8)
            };
            foreach (MdBlock child in quote)
                section.Blocks.Add(RenderBlock(child));
            return section;
        }

        private static WpfTable RenderTable(MdTable mdTable)
        {
            var rows = mdTable.OfType<MdTableRow>().ToList();
            int colCount = Math.Max(1, rows.Count > 0 ? rows.Max(r => r.Count) : 1);

            var wpfTable = new WpfTable
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 6, 0, 10),
                BorderBrush = CodeBorderBrush,
                BorderThickness = new Thickness(1),
                Background = CodeBackgroundBrush
            };
            for (int i = 0; i < colCount; i++)
                wpfTable.Columns.Add(new TableColumn());

            var rowGroup = new TableRowGroup();
            int bodyIndex = 0;
            foreach (MdTableRow mdRow in rows)
            {
                var wpfRow = new WpfTableRow
                {
                    Background = mdRow.IsHeader
                        ? TableHeaderBrush
                        : (bodyIndex % 2 == 1 ? TableAltRowBrush : Brushes.Transparent)
                };
                if (!mdRow.IsHeader)
                    bodyIndex++;

                foreach (MdTableCell mdCell in mdRow.OfType<MdTableCell>())
                {
                    var cell = new WpfTableCell
                    {
                        Padding = new Thickness(10, 6, 10, 6),
                        BorderBrush = mdRow.IsHeader ? CodeBorderBrush : TableRowDividerBrush,
                        BorderThickness = new Thickness(0, 0, 0, 1)
                    };
                    if (mdRow.IsHeader)
                    {
                        cell.FontWeight = FontWeights.SemiBold;
                        cell.Foreground = HeadingGoldBrush;
                    }

                    bool hasContent = false;
                    foreach (MdBlock cellBlock in mdCell)
                    {
                        WpfBlock rendered = RenderBlock(cellBlock);
                        if (rendered is Paragraph p)
                            p.Margin = new Thickness(0, 0, 0, 2);
                        cell.Blocks.Add(rendered);
                        hasContent = true;
                    }
                    if (!hasContent)
                        cell.Blocks.Add(new Paragraph { Margin = new Thickness(0) });

                    wpfRow.Cells.Add(cell);
                }

                rowGroup.Rows.Add(wpfRow);
            }

            wpfTable.RowGroups.Add(rowGroup);
            return wpfTable;
        }

        private static System.Windows.Documents.List RenderList(ListBlock listBlock)
        {
            var list = new System.Windows.Documents.List
            {
                MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(18, 2, 0, 8),
                Padding = new Thickness(0)
            };
            if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start))
                list.StartIndex = start;

            foreach (MdBlock child in listBlock)
            {
                if (child is not ListItemBlock itemBlock)
                    continue;

                var item = new ListItem { Margin = new Thickness(0, 0, 0, 4) };
                foreach (MdBlock itemChild in itemBlock)
                {
                    WpfBlock rendered = RenderBlock(itemChild);
                    if (rendered is Paragraph p)
                        p.Margin = new Thickness(0, 0, 0, 2);
                    item.Blocks.Add(rendered);
                }
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
                        target.Add(new Run(" " + code.Content + " ")
                        {
                            FontFamily = CodeFont,
                            FontSize = 11,
                            Foreground = InlineCodeForegroundBrush,
                            Background = InlineCodeBackgroundBrush
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
                        if (!string.IsNullOrWhiteSpace(link.Url))
                        {
                            var hyperlink = new Hyperlink
                            {
                                Foreground = InlineCodeForegroundBrush,
                                TextDecorations = TextDecorations.Underline,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                Tag = link.Url
                            };
                            AppendInlines(hyperlink.Inlines, link);
                            target.Add(hyperlink);
                        }
                        else
                        {
                            var linkSpan = new Span { Foreground = SecondaryBrush };
                            AppendInlines(linkSpan.Inlines, link);
                            target.Add(linkSpan);
                        }
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
