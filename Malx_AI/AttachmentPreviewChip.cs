using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Malx_AI
{
    /// <summary>One staged attachment as the preview tray needs to draw it.</summary>
    public sealed class AttachmentPreviewItem
    {
        /// <summary>1-based position among every file attached to the chat — the number the
        /// model counts with, so the badge and "the 2nd attached file" always agree.</summary>
        public int Number { get; init; }
        public string Name { get; init; } = string.Empty;
        public string KindLabel { get; init; } = string.Empty;
        public string SizeLabel { get; init; } = string.Empty;
        public bool IsImage { get; init; }
        public string? Base64Data { get; init; }
        public string? FilePath { get; init; }
        /// <summary>"image 2 of 3" / "file 1 of 2" — how the user can refer to this one.</summary>
        public string PositionLabel { get; init; } = string.Empty;
        public int TotalCount { get; init; }
    }

    /// <summary>
    /// Builds the preview chips shown above the prompt box in both Normal Chat and Workplace:
    /// a real thumbnail for images, a typed tile for everything else. The chip itself stays clean;
    /// hovering it says which attachment it is and the exact phrase to refer to it by.
    /// </summary>
    public static class AttachmentPreviewChip
    {
        private const double TileSize = 52;
        private const int ThumbnailDecodeWidth = 160;

        private static readonly Brush ChipBackground = Frozen(Color.FromRgb(0x24, 0x21, 0x1F));
        private static readonly Brush ChipBorder = Frozen(Color.FromRgb(0x3A, 0x36, 0x31));
        private static readonly Brush TileBackground = Frozen(Color.FromRgb(0x17, 0x16, 0x15));
        private static readonly Brush PrimaryText = Frozen(Color.FromRgb(0xED, 0xE8, 0xE3));
        private static readonly Brush SecondaryText = Frozen(Color.FromRgb(0x8A, 0x82, 0x79));
        private static readonly Brush BadgeBackground = Frozen(Color.FromArgb(0xCC, 0x14, 0x13, 0x12));

        public static FrameworkElement Build(AttachmentPreviewItem item, Action onRemove, bool animate)
        {
            ImageSource? thumbnail = item.IsImage ? TryLoadThumbnail(item) : null;

            var chip = new Border
            {
                Background = ChipBackground,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 8, 0),
                RenderTransform = new TranslateTransform(),
                ToolTip = BuildTooltip(item)
            };

            var layout = new StackPanel { Orientation = Orientation.Horizontal };
            layout.Children.Add(BuildTile(item, thumbnail));

            // Images read as a thumbnail on their own; other files need their name spelled out.
            if (!item.IsImage)
                layout.Children.Add(BuildTextColumn(item));

            var overlay = new Grid();
            overlay.Children.Add(layout);
            overlay.Children.Add(BuildRemoveButton(item, onRemove));
            chip.Child = overlay;

            if (animate)
            {
                chip.Opacity = 0;
                ((TranslateTransform)chip.RenderTransform).Y = 10;
                chip.Loaded += (_, _) => AnimateEntrance(chip);
            }

            return chip;
        }

        private static UIElement BuildTile(AttachmentPreviewItem item, ImageSource? thumbnail)
        {
            var tile = new Border
            {
                Width = TileSize,
                Height = TileSize,
                CornerRadius = new CornerRadius(8),
                Background = TileBackground,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };

            if (thumbnail != null)
            {
                tile.Background = new ImageBrush(thumbnail)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                return tile;
            }

            // No thumbnail: a glyph plus the extension, which identifies the file faster than
            // the glyph alone when several documents are staged.
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new TextBlock
            {
                Text = GetGlyph(item),
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = PrimaryText
            });

            string extension = GetExtensionLabel(item.Name);
            if (!string.IsNullOrEmpty(extension))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = extension,
                    FontSize = 8,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 1, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = SecondaryText
                });
            }

            tile.Child = stack;
            return tile;
        }

        private static UIElement BuildTextColumn(AttachmentPreviewItem item)
        {
            var column = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 22, 0)
            };

            column.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = PrimaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 170
            });

            string detail = string.IsNullOrWhiteSpace(item.SizeLabel)
                ? item.KindLabel
                : $"{item.KindLabel} • {item.SizeLabel}";
            column.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 10,
                Foreground = SecondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 170
            });

            return column;
        }

        private static UIElement BuildRemoveButton(AttachmentPreviewItem item, Action onRemove)
        {
            var button = new Button
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 2, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                Foreground = PrimaryText,
                Background = BadgeBackground,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                ToolTip = $"Remove {item.Name}",
                // Drawn rather than typed: the glyph font leaves descender space under the mark,
                // which parks a centred text "x" visibly high inside a circle this small.
                Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L7,7 M7,0 L0,7"),
                    Stroke = PrimaryText,
                    StrokeThickness = 1.3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = 7,
                    Height = 7,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Template = BuildRoundButtonTemplate()
            };

            AutomationProperties.SetName(button, $"Remove {item.Name}");
            button.Click += (_, _) => onRemove();
            return button;
        }

        private static ControlTemplate BuildRoundButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Frozen(Color.FromRgb(0x5A, 0x33, 0x30))));
            template.Triggers.Add(hover);
            return template;
        }

        private static string BuildTooltip(AttachmentPreviewItem item)
        {
            string size = string.IsNullOrWhiteSpace(item.SizeLabel) ? string.Empty : $", {item.SizeLabel}";
            string phrase = item.IsImage ? "attached image" : "attached file";
            string ordinal = OrdinalPhrase(GetKindPosition(item.PositionLabel));
            string hint = string.IsNullOrEmpty(ordinal)
                ? string.Empty
                : $"\nRefer to it as \"the {ordinal} {phrase}\".";

            return $"{item.Name} — {item.KindLabel}{size}"
                + $"\nAttachment {item.Number} of {Math.Max(item.Number, item.TotalCount)}"
                + (string.IsNullOrWhiteSpace(item.PositionLabel) ? string.Empty : $" · {item.PositionLabel}")
                + hint;
        }

        // "image 2 of 3" -> 2
        private static int GetKindPosition(string positionLabel)
        {
            if (string.IsNullOrWhiteSpace(positionLabel))
                return 0;

            string[] parts = positionLabel.Split(' ');
            return parts.Length >= 2 && int.TryParse(parts[1], out int position) ? position : 0;
        }

        private static string OrdinalPhrase(int position) => position switch
        {
            1 => "first",
            2 => "second",
            3 => "third",
            4 => "fourth",
            5 => "fifth",
            6 => "sixth",
            7 => "seventh",
            8 => "eighth",
            9 => "ninth",
            10 => "tenth",
            > 10 => position + Suffix(position),
            _ => string.Empty
        };

        private static string Suffix(int value) => (value % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" }
        };

        private static ImageSource? TryLoadThumbnail(AttachmentPreviewItem item)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.Base64Data))
                    return DecodeThumbnail(new MemoryStream(Convert.FromBase64String(item.Base64Data)));

                if (!string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath))
                    return DecodeThumbnail(new MemoryStream(File.ReadAllBytes(item.FilePath)));
            }
            catch (Exception ex)
            {
                // A corrupt or unreadable image must not take the composer down; the chip falls
                // back to the typed tile.
                _ = BackendLogService.LogEventAsync("AttachmentPreview", $"Thumbnail failed for {item.Name}: {ex.Message}");
            }

            return null;
        }

        private static ImageSource DecodeThumbnail(Stream stream)
        {
            using (stream)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = ThumbnailDecodeWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private static string GetGlyph(AttachmentPreviewItem item)
        {
            if (item.IsImage)
                return "\U0001F5BC";

            return GetExtensionLabel(item.Name) switch
            {
                "PDF" => "\U0001F4D5",
                "DOCX" or "DOC" or "ODT" or "RTF" => "\U0001F4C4",
                "XLSX" or "XLS" or "ODS" or "CSV" or "TSV" => "\U0001F4CA",
                "PPTX" or "PPT" or "ODP" => "\U0001F4FD",
                "EPUB" => "\U0001F4DA",
                "IPYNB" => "\U0001F4D3",
                "MD" or "TXT" => "\U0001F4DD",
                _ => "\U0001F4CE"
            };
        }

        private static string GetExtensionLabel(string name)
        {
            string extension = Path.GetExtension(name ?? string.Empty).TrimStart('.');
            return extension.Length is > 0 and <= 5 ? extension.ToUpperInvariant() : string.Empty;
        }

        private static void AnimateEntrance(Border chip)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            chip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = ease
            });
            ((TranslateTransform)chip.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = ease
            });
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
