using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace SennheiserTray
{
    public partial class MainWindow : Window
    {
        private const int MuteHotkeyId = 0x5E11;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;
        private const uint VirtualKeyM = 0x4D;
        private const int WmHotkey = 0x0312;
        private readonly HeadphoneViewModel _viewModel;
        private WinForms.NotifyIcon? _trayIcon;
        private HwndSource? _hotkeySource;
        private bool _muteHotkeyRegistered;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new HeadphoneViewModel();
            DataContext = _viewModel;
            InitializeTrayIcon();
            SourceInitialized += MainWindow_SourceInitialized;

            Loaded += async (_, _) =>
            {
                PositionNearSystemTray();
                Show();
                Activate();
                Focus();
                await Task.Delay(1000);
                Deactivated += Window_Deactivated;
            };

            Closed += (_, _) =>
            {
                if (_muteHotkeyRegistered)
                {
                    UnregisterHotKey(new WindowInteropHelper(this).Handle, MuteHotkeyId);
                    _muteHotkeyRegistered = false;
                }

                if (_hotkeySource != null)
                {
                    _hotkeySource.RemoveHook(WindowHook);
                    _hotkeySource = null;
                }

                _viewModel.Dispose();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
            };
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            _hotkeySource = HwndSource.FromHwnd(handle);
            _hotkeySource?.AddHook(WindowHook);
            _muteHotkeyRegistered = RegisterHotKey(
                handle,
                MuteHotkeyId,
                ModControl | ModShift | ModNoRepeat,
                VirtualKeyM);
            _viewModel.SetGlobalMuteHotkeyAvailable(_muteHotkeyRegistered);
            if (!_muteHotkeyRegistered)
            {
                var exception = new Win32Exception(Marshal.GetLastWin32Error());
                AppLogger.LogException("Could not register the global microphone mute shortcut", exception);
            }
        }

        private IntPtr WindowHook(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == WmHotkey && wParam.ToInt32() == MuteHotkeyId)
            {
                _viewModel.ToggleMicrophoneMuteCommand.Execute(null);
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "Sennheiser Control",
                Visible = true
            };

            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button != WinForms.MouseButtons.Left)
                {
                    return;
                }

                if (IsVisible)
                {
                    Hide();
                }
                else
                {
                    PositionNearSystemTray();
                    Show();
                    Activate();
                }
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open Widget", null, (_, _) =>
            {
                PositionNearSystemTray();
                Show();
                Activate();
            });
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());
            _trayIcon.ContextMenuStrip = menu;
        }

        private void ExitApplication()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
            }

            System.Windows.Application.Current.Shutdown();
        }

        private void PositionNearSystemTray()
        {
            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 12;
            Top = workArea.Bottom - Height - 12;
        }

        private void Window_Deactivated(object? sender, EventArgs e) => Hide();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(
            IntPtr window,
            int id,
            uint modifiers,
            uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
