using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Malx_AI.ComputerUse
{
    public partial class ComputerUsePointerWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        public ComputerUsePointerWindow()
        {
            InitializeComponent();
            SourceInitialized += ComputerUsePointerWindow_SourceInitialized;
        }

        public void PlaceAtScreen(int screenX, int screenY)
        {
            System.Windows.Point dip = ComputerUseScreenCapture.DipFromScreen(this, screenX, screenY);
            Left = dip.X - 4;
            Top = dip.Y - 2;
        }

        private void ComputerUsePointerWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
