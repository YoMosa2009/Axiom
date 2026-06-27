using System;
using System.Windows;
using System.Windows.Input;

namespace Malx_AI
{
    public partial class CouncilPetWindow : Window
    {
        public event EventHandler? DisableRequested;

        public CouncilPetWindow()
        {
            InitializeComponent();
            Loaded += CouncilPetWindow_Loaded;
        }

        public void UpdateStatus(string role, string message)
        {
            RoleText.Text = string.IsNullOrWhiteSpace(role) ? "Council Bit" : role.Trim();
            MessageText.Text = string.IsNullOrWhiteSpace(message) ? "Watching the council." : message.Trim();
        }

        private void CouncilPetWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!double.IsNaN(Left) && !double.IsNaN(Top) && (Left != 0 || Top != 0))
                return;

            Rect area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left + 16, area.Right - Width - 28);
            Top = Math.Max(area.Top + 16, area.Bottom - Height - 28);
        }

        private void PetRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;

            try
            {
                DragMove();
            }
            catch
            {
                // DragMove can throw if the mouse state changes mid-drag; ignoring keeps the pet lightweight.
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DisableRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
