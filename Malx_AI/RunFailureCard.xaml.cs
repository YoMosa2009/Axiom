using System;
using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    /// <summary>
    /// A quiet card shown when a model request fails: a title, a one-line hint and action buttons
    /// chosen by <see cref="RunFailureAdvisor"/>. The host listens to <see cref="ActionRequested"/>
    /// and performs the action (retry the last turn, open the model picker, and so on).
    /// </summary>
    public partial class RunFailureCard : UserControl
    {
        internal event Action<RunFailureAction>? ActionRequested;

        public RunFailureCard()
        {
            InitializeComponent();
        }

        internal void Show(int? httpStatus, string? errorMessage)
            => Show(RunFailureAdvisor.Classify(httpStatus, errorMessage));

        internal void Show(RunFailureAdvice advice)
        {
            TitleText.Text = advice.Title;
            HintText.Text = advice.Hint;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                advice.Kind == RunFailureKind.Cancelled ? "MutedTextBrush" : "WarningBrush");

            ActionPanel.Children.Clear();
            bool first = true;
            foreach (RunFailureAction action in advice.Actions)
            {
                var button = new Button
                {
                    Content = RunFailureAdvisor.ActionLabel(action),
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(0, 0, 8, 0),
                    FontSize = 12,
                    FontWeight = first ? FontWeights.SemiBold : FontWeights.Normal,
                };
                if (first)
                {
                    button.SetResourceReference(Control.BackgroundProperty, "PrimaryAccentBrush");
                    button.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
                    button.SetResourceReference(Control.BorderBrushProperty, "PrimaryAccentBrush");
                }
                else
                {
                    button.SetResourceReference(Control.BackgroundProperty, "NotebookBackgroundBrush");
                    button.SetResourceReference(Control.ForegroundProperty, "DarkSecondaryBrush");
                    button.SetResourceReference(Control.BorderBrushProperty, "NotebookBorderBrush");
                }
                RunFailureAction captured = action;
                button.Click += (_, _) => ActionRequested?.Invoke(captured);
                ActionPanel.Children.Add(button);
                first = false;
            }
        }
    }
}
