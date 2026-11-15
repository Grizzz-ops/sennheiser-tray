using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Resources;
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
        private readonly Dictionary<string, WinForms.ToolStripMenuItem> _soundProfileItems =
            new(StringComparer.Ordinal);
        private readonly Dictionary<int, WinForms.ToolStripMenuItem> _noiseControlItems = new();
        private WinForms.NotifyIcon? _trayIcon;
        private System.Drawing.Icon? _trayIconImage;
        private HwndSource? _hotkeySource;
        private WinForms.ToolStripMenuItem? _volumeStatusItem;
        private WinForms.ToolStripMenuItem? _volumeDownItem;
        private WinForms.ToolStripMenuItem? _volumeUpItem;
        private bool _muteHotkeyRegistered;
        private bool _lowBatteryAlertShown;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new HeadphoneViewModel();
            DataContext = _viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.AutomaticConnectionSucceeded += ViewModel_AutomaticConnectionSucceeded;
            InitializeTrayIcon();
            RefreshTrayMenu();
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

                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.AutomaticConnectionSucceeded -= ViewModel_AutomaticConnectionSucceeded;
                _viewModel.Dispose();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }

                _trayIconImage?.Dispose();
                _trayIconImage = null;
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
            StreamResourceInfo iconResource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/SennheiserTray;component/Assets/SennheiserTray.ico"))
                ?? throw new FileNotFoundException("The application tray icon resource is missing.");
            using Stream iconStream = iconResource.Stream;
            using var resourceIcon = new System.Drawing.Icon(iconStream);
            _trayIconImage = (System.Drawing.Icon)resourceIcon.Clone();
            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = _trayIconImage,
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
            menu.Opening += (_, _) => RefreshTrayMenu();
            menu.Items.Add("Open Sennheiser Control", null, (_, _) => ShowMainWindow());
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var profileMenu = new WinForms.ToolStripMenuItem("Sound profile");
            foreach (ModeChipItem mode in _viewModel.ModeChipItems)
            {
                var item = new WinForms.ToolStripMenuItem(mode.Name);
                item.Click += (_, _) => _viewModel.SelectModeCommand.Execute(mode.Id);
                profileMenu.DropDownItems.Add(item);
                _soundProfileItems.Add(mode.Id, item);
            }
            menu.Items.Add(profileMenu);

            var noiseControlMenu = new WinForms.ToolStripMenuItem("Noise control");
            AddNoiseControlItem(noiseControlMenu, "Off", AncModeRules.Off);
            AddNoiseControlItem(noiseControlMenu, "ANC · current level", AncModeRules.Custom);
            AddNoiseControlItem(noiseControlMenu, "Adaptive ANC", AncModeRules.Adaptive);
            var awarenessItem = new WinForms.ToolStripMenuItem("Awareness · 100%");
            awarenessItem.Click += (_, _) =>
            {
                _viewModel.OrbLevelChangedCommand.Execute(100d);
            };
            noiseControlMenu.DropDownItems.Add(awarenessItem);
            _noiseControlItems.Add(100, awarenessItem);
            menu.Items.Add(noiseControlMenu);

            var volumeMenu = new WinForms.ToolStripMenuItem("Windows volume");
            _volumeStatusItem = new WinForms.ToolStripMenuItem("Volume: --")
            {
                Enabled = false
            };
            _volumeDownItem = new WinForms.ToolStripMenuItem("Volume down 5%");
            _volumeDownItem.Click += (_, _) =>
                _viewModel.PlaybackVolume = Math.Max(0, _viewModel.PlaybackVolume - 5);
            _volumeUpItem = new WinForms.ToolStripMenuItem("Volume up 5%");
            _volumeUpItem.Click += (_, _) =>
                _viewModel.PlaybackVolume = Math.Min(100, _viewModel.PlaybackVolume + 5);
            volumeMenu.DropDownItems.AddRange(new WinForms.ToolStripItem[]
            {
                _volumeStatusItem,
                new WinForms.ToolStripSeparator(),
                _volumeDownItem,
                _volumeUpItem
            });
            menu.Items.Add(volumeMenu);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Restart", null, (_, _) =>
            {
                if (System.Windows.Application.Current is App app)
                {
                    app.RequestRestart();
                }
            });
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());
            _trayIcon.ContextMenuStrip = menu;
        }

        private void AddNoiseControlItem(
            WinForms.ToolStripMenuItem menu,
            string text,
            int mode)
        {
            var item = new WinForms.ToolStripMenuItem(text);
            item.Click += (_, _) => _viewModel.SetAncModeCommand.Execute(mode);
            menu.DropDownItems.Add(item);
            _noiseControlItems.Add(mode, item);
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HeadphoneViewModel.BatteryLevel))
            {
                UpdateLowBatteryAlert();
            }

            if (e.PropertyName is nameof(HeadphoneViewModel.BatteryLevel)
                or nameof(HeadphoneViewModel.IsConnected)
                or nameof(HeadphoneViewModel.SelectedMode)
                or nameof(HeadphoneViewModel.AncMode)
                or nameof(HeadphoneViewModel.AncLevel)
                or nameof(HeadphoneViewModel.CanChangeMode)
                or nameof(HeadphoneViewModel.PlaybackVolume)
                or nameof(HeadphoneViewModel.IsPlaybackVolumeAvailable))
            {
                RefreshTrayMenu();
            }
        }

        private void ViewModel_AutomaticConnectionSucceeded(string deviceName)
        {
            _trayIcon?.ShowBalloonTip(
                4000,
                "Headset connected",
                $"Connected to {deviceName}.",
                WinForms.ToolTipIcon.Info);
        }

        private void UpdateLowBatteryAlert()
        {
            if (!_viewModel.IsConnected || _viewModel.BatteryLevel is not int level)
            {
                _lowBatteryAlertShown = false;
                return;
            }

            if (level > 25)
            {
                _lowBatteryAlertShown = false;
            }
            else if (level <= 20 && !_lowBatteryAlertShown)
            {
                _lowBatteryAlertShown = true;
                _trayIcon?.ShowBalloonTip(
                    5000,
                    "Headset battery low",
                    $"Battery is at {level}%. Consider charging your headset.",
                    WinForms.ToolTipIcon.Warning);
            }
        }

        private void RefreshTrayMenu()
        {
            foreach ((string modeId, WinForms.ToolStripMenuItem item) in _soundProfileItems)
            {
                item.Checked = StringComparer.Ordinal.Equals(modeId, _viewModel.SelectedMode);
                item.Enabled = _viewModel.CanChangeMode;
            }

            bool canChangeNoiseControl = _viewModel.IsConnected && _viewModel.CanChangeMode;
            foreach ((int mode, WinForms.ToolStripMenuItem item) in _noiseControlItems)
            {
                item.Checked = mode == 100
                    ? _viewModel.AncMode == AncModeRules.Custom && _viewModel.AncLevel >= 95
                    : _viewModel.AncMode == mode;
                item.Enabled = canChangeNoiseControl;
            }

            if (_volumeStatusItem != null)
            {
                _volumeStatusItem.Text = _viewModel.IsPlaybackVolumeAvailable
                    ? $"Current volume: {_viewModel.PlaybackVolume}%"
                    : "Windows volume unavailable";
            }

            if (_volumeDownItem != null)
            {
                _volumeDownItem.Enabled =
                    _viewModel.IsPlaybackVolumeAvailable && _viewModel.PlaybackVolume > 0;
            }

            if (_volumeUpItem != null)
            {
                _volumeUpItem.Enabled =
                    _viewModel.IsPlaybackVolumeAvailable && _viewModel.PlaybackVolume < 100;
            }
        }

        private void ShowMainWindow()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            PositionNearSystemTray();
            Show();
            Activate();
            Focus();
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
