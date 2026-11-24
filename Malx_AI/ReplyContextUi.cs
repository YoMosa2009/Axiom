using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Malx_AI
{
    /// <summary>
    /// The right-click menu on AI replies ("Copy", "Attach selection as context") and the quote
    /// bar shown above the input box, shared by Chat and Workplace.
    /// </summary>
    internal static class ReplyContextUi
    {
        public static readonly KeyGesture AttachGesture = new(Key.L, ModifierKeys.Control | ModifierKeys.Shift);

        /// <summary>A small menu with the two actions, styled from the current theme.</summary>
        public static ContextMenu CreateMenu(RoutedEventHandler onCopy, RoutedEventHandler onAttach)
        {
            var menu = new ContextMenu { Padding = new Thickness(4), BorderThickness = new Thickness(1) };
            menu.SetResourceReference(Control.BackgroundProperty, "NotebookSurfaceBrush");
            menu.SetResourceReference(Control.BorderBrushProperty, "NotebookBorderBrush");
            menu.SetResourceReference(Control.ForegroundProperty, "NotebookTextBrush");
            var copy = new MenuItem { Header = MenuHeader("Copy", "Ctrl+C") };
            copy.Click += onCopy;
            var attach = new MenuItem { Header = MenuHeader("Attach selection as context", "Ctrl+Shift+L") };
            attach.Click += onAttach;
            menu.Items.Add(copy);
            menu.Items.Add(attach);
            return menu;
        }

        /// <summary>
        /// Label with its shortcut right-aligned and muted. The app's menu style has no gesture
        /// column, so InputGestureText alone never showed.
        /// </summary>
        private static FrameworkElement MenuHeader(string label, string shortcut)
        {
            var grid = new Grid { MinWidth = 250 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { Text = label, LineHeight = double.NaN, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(text);
            var keys = new TextBlock { Text = shortcut, FontSize = 11.5, Margin = new Thickness(24, 0, 0, 0), LineHeight = double.NaN, VerticalAlignment = VerticalAlignment.Center };
            keys.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetColumn(keys, 1);
            grid.Children.Add(keys);
            return grid;
        }

        /// <summary>The element a context-menu item was opened on.</summary>
        public static object? MenuTarget(object sender)
            => sender is MenuItem { Parent: ContextMenu menu } ? menu.PlacementTarget : sender;

        /// <summary>
        /// The selected text of a reply, or the whole reply when nothing is selected. Rich Chat
        /// replies are drawn as an image, so for those the whole message is used.
        /// </summary>
        public static string? SelectedOrWholeText(object? target)
        {
            switch (target)
            {
                case TextBox box:
                    return box.SelectionLength > 0 ? box.SelectedText : box.Text;
                case FlowDocumentScrollViewer viewer:
                {
                    string selected = viewer.Selection?.Text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(selected))
                        return selected;
                    return viewer.Document == null
                        ? null
                        : new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text;
                }
                case FrameworkElement { DataContext: ChatMessage message }:
                    return message.Content;
                default:
                    return null;
            }
        }

        /// <summary>Copies text to the clipboard, ignoring a clipboard held by another app.</summary>
        public static void Copy(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }

        /// <summary>The quote bar: a reply arrow, the quoted text on one line, and a remove button.</summary>
        public static Border CreateQuoteBar(string text, Action onRemove)
        {
            var bar = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 6, 6),
                ToolTip = text.Length > 600 ? text[..600] + "…" : text,
            };
            bar.SetResourceReference(Border.BackgroundProperty, "NotebookBackgroundBrush");
            bar.SetResourceReference(Border.BorderBrushProperty, "NotebookBorderBrush");
            AutomationPropertiesHelper.SetName(bar, "Attached context");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var arrow = new Path
            {
                Data = Geometry.Parse("M6,2 L2,6 L6,10 M2,6 L10,6 C13,6 14,8 14,11"),
                StrokeThickness = 1.4,
                Width = 14,
                Height = 12,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            arrow.SetResourceReference(Shape.StrokeProperty, "MutedTextBrush");
            grid.Children.Add(arrow);

            var quote = new TextBlock
            {
                Text = ReplyContextAttachment.Preview(text),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 12.5,
                LineHeight = double.NaN,
                VerticalAlignment = VerticalAlignment.Center,
            };
            quote.SetResourceReference(TextBlock.ForegroundProperty, "DarkSecondaryBrush");
            Grid.SetColumn(quote, 1);
            grid.Children.Add(quote);

            var remove = new Button
            {
                Content = "✕",
                FontSize = 11,
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Remove attached context",
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.SetResourceReference(Control.ForegroundProperty, "MutedTextBrush");
            AutomationPropertiesHelper.SetName(remove, "Remove attached context");
            remove.Click += (_, _) => onRemove();
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            bar.Child = grid;
            return bar;
        }
    }

    internal static class AutomationPropertiesHelper
    {
        public static void SetName(DependencyObject element, string name)
            => System.Windows.Automation.AutomationProperties.SetName(element, name);
    }
}
