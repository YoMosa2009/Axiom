using System.Windows;
using System.Windows.Input;

namespace Malx_AI
{
    public partial class ChatRenameDialog : Window
    {
        public string ChatName => ChatNameBox.Text.Trim();

        public ChatRenameDialog(string currentName)
        {
            InitializeComponent();
            ChatNameBox.Text = currentName;
            Loaded += (_, _) =>
            {
                ChatNameBox.Focus();
                ChatNameBox.SelectAll();
            };
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (ChatName.Length == 0)
                return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ChatNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Rename_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
