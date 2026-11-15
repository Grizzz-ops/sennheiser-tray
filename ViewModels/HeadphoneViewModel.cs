using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace SennheiserTray
{
    public sealed partial class HeadphoneViewModel : ObservableObject, IDisposable
    {
        private readonly SennheiserService _service;
        private readonly ModeManager _modeManager = new();
        private readonly BatteryTrendEstimator _batteryTrendEstimator = new();
        private readonly AudioLevelService _audioLevelService = new();
        private readonly CaptureEndpointMuteService _captureEndpointMuteService = new();
        private readonly PlaybackVolumeService _playbackVolumeService = new();
        private readonly SettingsStore _settingsStore = new();
        private readonly StartupRegistration _startupRegistration = new();
        private readonly AutoConnectPollingPolicy _autoConnectPollingPolicy = new();
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _ancLevelTimer;
        private readonly DispatcherTimer _adaptiveEqTimer;
        private readonly DispatcherTimer _modePinTimer;
        private readonly DispatcherTimer _microphoneStateTimer;
        private readonly DispatcherTimer _focusCycleTimer;
        private readonly DispatcherTimer _batteryPollTimer;
        private readonly DispatcherTimer _playbackVolumeTimer;
        private readonly DispatcherTimer _autoConnectTimer;
        private readonly SemaphoreSlim _connectSemaphore = new(1, 1);
        private readonly SemaphoreSlim _headsetOperationSemaphore = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly AdaptiveEqProfileLearner _adaptiveEqLearner = new();
        private double[]? _latestPlaybackSpectrum;
        private double[]? _bassImpactPreviousEq;
        private Task? _reconnectTask;
        private string _pendingAction = "";
        private string _statusText = "Disconnected";
        private string _lastCommand = "Move a slider to adjust your sound";
        private string _adaptiveEqStatus = "Learns a steady profile from playback · local only";
        private string? _selectedDeviceId;
        private int _ancMode = -1;
        private double _ancLevel;
        private double _audioLevel;
        private int? _batteryLevel;
        private bool? _autoAncEnabled;
        private bool _isConnected;
        private bool _isDarkTheme;
        private bool _isAutoConnectEnabled = true;
        private bool _isLaunchOnStartup;
        private bool _bassBoostEnabled;
        private bool _isBassImpactEnabled;
        private bool _isBassImpactBusy;
        private bool _isBassBoostBusy;
        private bool _isApplyingMode;
        private bool _isMicrophoneMuted;
        private bool _microphoneStateAvailable;
        private bool _isGlobalMuteHotkeyAvailable;
        private bool _isWindowVisible;
        private bool _isGamingLatencyHintDismissed;
        private bool _isFocusTimerRunning;
        private bool _isFocusBreak;
        private DateTimeOffset _focusTimerStartedAt;
        private bool _lastFocusBreakState;
        private bool _adaptiveEqEnabled;
        private bool _hasNewPlaybackSpectrum;
        private bool _applyingAdaptiveEq;
        private bool _isApplyingAncMode;
        private bool _isSendingAncLevel;
        private bool _ancLevelChangedWhileSending;
        private bool _isApplyingBassImpact;
        private bool _bassImpactPreviousBoostState;
        private bool _isTuneOpen;
        private bool _isLabOpen;
        private bool _isUpdatingAutoToggle;
        private bool _isBatteryPollInProgress;
        private bool _isRefreshingPlaybackVolume;
        private bool _isAutoConnectScanInProgress;
        private string _muteHotkeyKey = "M";
        private bool _isPlaybackVolumeAvailable;
        private bool _hasLoggedPlaybackVolumeError;
        private int _modeBeforeAdaptive = AncModeRules.Custom;
        private int _playbackVolume = 50;
        private double _lastCustomAncLevel = 50;
        private string _selectedMode = "Music";
        private int _modeApplyGeneration;
        private bool _isDisposed;

        public HeadphoneViewModel() : this(new SennheiserService(), Dispatcher.CurrentDispatcher)
        {
        }

        public void SetWindowVisible(bool isVisible)
        {
            if (_isDisposed || _isWindowVisible == isVisible)
            {
                return;
            }

            _isWindowVisible = isVisible;
            UpdateAudioCapture();
        }

        private void UpdateAudioCapture()
        {
            bool shouldCapture = _isWindowVisible || IsAdaptiveEqEnabled;
            if (shouldCapture == _audioLevelService.IsRunning)
            {
                return;
            }

            try
            {
                _audioLevelService.Mode = IsAdaptiveEqEnabled
                    ? AudioAnalysisMode.AdaptiveEq
                    : AudioAnalysisMode.Visualizer;
                if (shouldCapture)
                {
                    _audioLevelService.Start();
                }
                else
                {
                    _audioLevelService.Stop();
                    _audioLevel = 0;
                    OnPropertyChanged(nameof(AudioLevel));
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not update playback analysis capture", ex);
                LastCommand = $"PLAYBACK ANALYSIS ERROR -> {ex.Message}";
            }
        }

        public HeadphoneViewModel(SennheiserService service, Dispatcher dispatcher)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            EqValues = new ObservableCollection<double> { 0, 0, 0, 0, 0 };
            EqValues.CollectionChanged += EqValues_CollectionChanged;
            ModeChipItems = new ObservableCollection<ModeChipItem>(
                _modeManager.Modes.Select(mode => new ModeChipItem(mode)));
            SyncModeChipItems();

            ConnectCommand = new RelayCommand(async _ => await RefreshAndConnectAsync());
            SetAncModeCommand = new RelayCommand(parameter =>
            {
                if (parameter is int mode)
                {
                    SetAncMode(mode);
                }
            });
            ResetEqCommand = new RelayCommand(_ => ApplyEqValues(0, 0, 0, 0, 0));
            BassBoostCommand = new RelayCommand(async _ => await ToggleBassBoostAsync(), _ => CanToggleBassBoost);
            BassImpactCommand = new RelayCommand(
                async _ => await ToggleBassImpactAsync(),
                _ => !IsModeApplying && !IsBassImpactBusy && (!IsBassImpactEnabled || _isConnected));
            AdaptiveEqCommand = new RelayCommand(_ => ToggleAdaptiveEq());
            OpenTuneCommand = new RelayCommand(_ =>
            {
                IsLabOpen = false;
                IsTuneOpen = true;
            });
            OpenLabCommand = new RelayCommand(_ =>
            {
                IsTuneOpen = false;
                IsLabOpen = true;
            });
            CloseDrawerCommand = new RelayCommand(_ => CloseDrawers());
            ToggleOrbCommand = new RelayCommand(_ => ToggleAncFromOrb());
            OrbLevelChangedCommand = new RelayCommand(parameter =>
            {
                if (parameter is double level)
                {
                    if (AncMode != AncModeRules.Custom)
                    {
                        SetAncMode(AncModeRules.Custom);
                    }

                    AncLevel = level;
                }
            });
            AdaptiveAncCommand = new RelayCommand(_ => ToggleAdaptiveAnc());
            ToggleMicrophoneMuteCommand = new RelayCommand(_ => ToggleMicrophoneMute());
            FocusCycleCommand = new RelayCommand(_ => ToggleFocusCycle(), _ => IsFocusMode);
            DismissGamingHintCommand = new RelayCommand(_ => DismissGamingHint());
            ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
            SelectModeCommand = new RelayCommand(async parameter =>
            {
                if (parameter is string modeId)
                {
                    await SelectModeAsync(modeId);
                }
            }, _ => !IsModeApplying);
            ToggleModePinCommand = new RelayCommand(_ => ToggleModePin(), _ => !IsModeApplying);
            RestorePreviousModeCommand = new RelayCommand(
                async _ => await RestorePreviousModeAsync(),
                _ => !IsModeApplying && _modeManager.PreviousMode.Id != _modeManager.CurrentMode.Id);

            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _debounceTimer.Tick += DebounceTimer_Tick;
            _ancLevelTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _ancLevelTimer.Tick += AncLevelTimer_Tick;
            _adaptiveEqTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _adaptiveEqTimer.Tick += AdaptiveEqTimer_Tick;
            _modePinTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _modePinTimer.Tick += ModePinTimer_Tick;
            _microphoneStateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _microphoneStateTimer.Tick += MicrophoneStateTimer_Tick;
            _focusCycleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _focusCycleTimer.Tick += FocusCycleTimer_Tick;
            _batteryPollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMinutes(2)
            };
            _batteryPollTimer.Tick += BatteryPollTimer_Tick;
            _playbackVolumeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _playbackVolumeTimer.Tick += PlaybackVolumeTimer_Tick;
            _autoConnectTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = _autoConnectPollingPolicy.Interval
            };
            _autoConnectTimer.Tick += AutoConnectTimer_Tick;

            try
            {
                _settingsStore.Load();
                _isGamingLatencyHintDismissed = _settingsStore.GamingLatencyHintDismissed;
                _isDarkTheme = _settingsStore.IsDarkTheme;
                _isAutoConnectEnabled = _settingsStore.AutoConnectEnabled;
                _muteHotkeyKey = _settingsStore.MuteHotkeyKey;
                ThemeManager.Apply(_isDarkTheme);
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not load local settings", ex);
                LastCommand = $"SETTINGS LOAD ERROR -> {ex.Message}";
            }

            try
            {
                _isLaunchOnStartup = _startupRegistration.IsEnabled();
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not read Windows startup registration", ex);
                LastCommand = $"STARTUP SETTING ERROR -> {ex.Message}";
            }

            RefreshMicrophoneMuteState();
            RefreshPlaybackVolume();
            _playbackVolumeTimer.Start();

            _service.ConnectionChanged += Service_ConnectionChanged;
            _service.NotificationReceived += Service_NotificationReceived;
            _audioLevelService.AudioLevelAvailable += AudioLevelService_AudioLevelAvailable;
            _audioLevelService.SpectrumAnalyzed += AudioLevelService_SpectrumAnalyzed;

            _ = InitializeAsync();
        }

        public RelayCommand ConnectCommand { get; }
        public RelayCommand SetAncModeCommand { get; }
        public RelayCommand ResetEqCommand { get; }
        public RelayCommand BassBoostCommand { get; }
        public RelayCommand BassImpactCommand { get; }
        public RelayCommand AdaptiveEqCommand { get; }
        public RelayCommand OpenTuneCommand { get; }
        public RelayCommand OpenLabCommand { get; }
        public RelayCommand CloseDrawerCommand { get; }
        public RelayCommand ToggleOrbCommand { get; }
        public RelayCommand OrbLevelChangedCommand { get; }
        public RelayCommand AdaptiveAncCommand { get; }
        public RelayCommand ToggleMicrophoneMuteCommand { get; }
        public RelayCommand FocusCycleCommand { get; }
        public RelayCommand DismissGamingHintCommand { get; }
        public RelayCommand ToggleThemeCommand { get; }
        public RelayCommand SelectModeCommand { get; }
        public RelayCommand ToggleModePinCommand { get; }
        public RelayCommand RestorePreviousModeCommand { get; }
        public event Action<string>? AutomaticConnectionSucceeded;
        public event Action? MuteHotkeyChanged;
        public ObservableCollection<SennheiserDevice> Devices { get; } = new();
        public ObservableCollection<double> EqValues { get; }
        public ObservableCollection<ModeChipItem> ModeChipItems { get; }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetProperty(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HeaderText));
                    OnPropertyChanged(nameof(StatusForeground));
                    OnPropertyChanged(nameof(ConnectionBrush));
                }
            }
        }

        public string HeaderText
        {
            get
            {
                string? headsetName = IsConnected
                    ? _service.ConnectedDeviceName
                    : Devices.FirstOrDefault(device =>
                        string.Equals(device.Id, SelectedDeviceId, StringComparison.Ordinal))?.Name;
                return string.IsNullOrWhiteSpace(headsetName) ? StatusText : headsetName;
            }
        }

        public System.Windows.Media.Brush StatusForeground => _isConnected
            ? ThemeBrush("StatusConnectedBrush")
            : StatusText == "Connecting..." || StatusText == "Reconnecting..."
                ? ThemeBrush("StatusPendingBrush")
                : StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || StatusText.Contains("choose", StringComparison.OrdinalIgnoreCase)
                    || StatusText.Contains("no paired", StringComparison.OrdinalIgnoreCase)
                    ? ThemeBrush("StatusWarningBrush")
                    : ThemeBrush("TextPrimaryBrush");

        private static System.Windows.Media.Brush ThemeBrush(string resourceKey) =>
            System.Windows.Application.Current?.TryFindResource(resourceKey) as System.Windows.Media.Brush
            ?? MediaBrushes.Black;

        public System.Windows.Media.Brush ConnectionBrush => _isConnected
            ? new SolidColorBrush(MediaColor.FromRgb(63, 128, 96))
            : StatusText == "Connecting..." || StatusText == "Reconnecting..."
                ? new SolidColorBrush(MediaColor.FromRgb(173, 108, 47))
                : StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || StatusText.Contains("choose", StringComparison.OrdinalIgnoreCase)
                    || StatusText.Contains("no paired", StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(MediaColor.FromRgb(155, 112, 42))
                    : new SolidColorBrush(MediaColor.FromRgb(120, 129, 125));

        public int? BatteryLevel
        {
            get => _batteryLevel;
            private set
            {
                if (SetProperty(ref _batteryLevel, value))
                {
                    OnPropertyChanged(nameof(BatteryText));
                    OnPropertyChanged(nameof(IsChargingInferred));
                    OnPropertyChanged(nameof(BatteryEstimateStatus));
                }
            }
        }

        public string BatteryText => BatteryLevel is int level
            ? BatteryEstimateText is { Length: > 0 } estimate
                ? $"Battery {level}% · {estimate}"
                : $"Battery {level}%"
            : "Battery --";

        public bool IsChargingInferred =>
            IsConnected && _batteryTrendEstimator.GetChargingState() == true;

        public string BatteryToolTip =>
            "The charging indicator and time estimate are inferred from recent battery readings, not reported directly by the headset.";

        public string BatteryEstimateStatus
        {
            get
            {
                if (!IsConnected)
                {
                    return "Connect the headset to estimate battery time.";
                }

                if (BatteryLevel == null)
                {
                    return "Waiting for a battery reading from the headset.";
                }

                if (BatteryEstimateText.Length > 0)
                {
                    return BatteryEstimateText;
                }

                if (_batteryTrendEstimator.SampleCount < 4)
                {
                    return $"Learning battery use · {_batteryTrendEstimator.SampleCount}/4 readings";
                }

                return _batteryTrendEstimator.HasMinimumSampleWindow
                    ? "Waiting for battery level to change before estimating."
                    : "Learning battery trend · collecting readings over 6 minutes.";
            }
        }

        private string BatteryEstimateText { get; set; } = "";

        public int PlaybackVolume
        {
            get => _playbackVolume;
            set
            {
                int volume = Math.Clamp(value, 0, 100);
                if (!SetProperty(ref _playbackVolume, volume) || _isRefreshingPlaybackVolume)
                {
                    return;
                }

                try
                {
                    _playbackVolumeService.SetDefaultPlaybackVolume(volume);
                    IsPlaybackVolumeAvailable = true;
                }
                catch (Exception ex)
                {
                    ReportPlaybackVolumeError("Could not set Windows playback volume", ex);
                }
            }
        }

        public string PlaybackVolumeStatus => IsPlaybackVolumeAvailable
            ? "Controls the current Windows default output device."
            : "Windows playback output is unavailable.";

        public bool IsPlaybackVolumeAvailable
        {
            get => _isPlaybackVolumeAvailable;
            private set
            {
                if (SetProperty(ref _isPlaybackVolumeAvailable, value))
                {
                    OnPropertyChanged(nameof(PlaybackVolumeStatus));
                }
            }
        }

        public string ConnectButtonText => IsConnected ? "Reconnect" : "Connect";
        public bool IsDisconnected => !IsConnected;

        public string? SelectedDeviceId
        {
            get => _selectedDeviceId;
            set
            {
                if (SetProperty(ref _selectedDeviceId, value))
                {
                    OnPropertyChanged(nameof(HeaderText));
                }
            }
        }

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    if (!value)
                    {
                        _batteryPollTimer.Stop();
                        _batteryTrendEstimator.Reset();
                        BatteryLevel = null;
                        BatteryEstimateText = "";
                        OnPropertyChanged(nameof(BatteryText));
                        OnPropertyChanged(nameof(BatteryEstimateStatus));
                    }

                    else
                    {
                        _batteryPollTimer.Start();
                    }

                    OnPropertyChanged(nameof(StatusForeground));
                    OnPropertyChanged(nameof(ConnectionBrush));
                    OnPropertyChanged(nameof(HeaderText));
                    OnPropertyChanged(nameof(ConnectButtonText));
                    OnPropertyChanged(nameof(IsDisconnected));
                    ConnectCommand.NotifyCanExecuteChanged();
                    BassBoostCommand.NotifyCanExecuteChanged();
                    BassImpactCommand.NotifyCanExecuteChanged();
                    AdaptiveEqCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsDarkTheme => _isDarkTheme;
        public string ThemeToggleText => IsDarkTheme ? "Switch to day theme" : "Switch to night theme";

        public bool IsAutoConnectEnabled
        {
            get => _isAutoConnectEnabled;
            set
            {
                if (_isAutoConnectEnabled == value)
                {
                    return;
                }

                try
                {
                    _settingsStore.SetAutoConnectEnabled(value);
                    SetProperty(ref _isAutoConnectEnabled, value);
                    LastCommand = value
                        ? "HEADSET -> Automatic connection enabled"
                        : "HEADSET -> Automatic connection disabled";
                    if (value)
                    {
                        _autoConnectTimer.Interval = _autoConnectPollingPolicy.Interval;
                        _autoConnectTimer.Start();
                        _ = ScanAndConnectAsync();
                    }
                    else
                    {
                        _autoConnectTimer.Stop();
                        if (!IsConnected && StatusText == "Reconnecting...")
                        {
                            StatusText = "Disconnected";
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Could not save automatic headset connection setting", ex);
                    OnPropertyChanged(nameof(IsAutoConnectEnabled));
                    LastCommand = $"AUTO-CONNECT SETTING ERROR -> {ex.Message}";
                }
            }
        }

        public bool IsLaunchOnStartup
        {
            get => _isLaunchOnStartup;
            set
            {
                if (_isLaunchOnStartup == value)
                {
                    return;
                }

                try
                {
                    _startupRegistration.SetEnabled(value);
                    if (SetProperty(ref _isLaunchOnStartup, value))
                    {
                        LastCommand = value
                            ? "STARTUP -> Sennheiser Control will launch when you sign in"
                            : "STARTUP -> Automatic launch disabled";
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Could not update Windows startup registration", ex);
                    OnPropertyChanged(nameof(IsLaunchOnStartup));
                    LastCommand = $"STARTUP SETTING ERROR -> {ex.Message}";
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _lifetime.Cancel();
            _debounceTimer.Stop();
            _ancLevelTimer.Stop();
            _modePinTimer.Stop();
            _microphoneStateTimer.Stop();
            _focusCycleTimer.Stop();
            _batteryPollTimer.Stop();
            _playbackVolumeTimer.Stop();
            _autoConnectTimer.Stop();
            _modePinTimer.Tick -= ModePinTimer_Tick;
            _microphoneStateTimer.Tick -= MicrophoneStateTimer_Tick;
            _focusCycleTimer.Tick -= FocusCycleTimer_Tick;
            _batteryPollTimer.Tick -= BatteryPollTimer_Tick;
            _playbackVolumeTimer.Tick -= PlaybackVolumeTimer_Tick;
            _autoConnectTimer.Tick -= AutoConnectTimer_Tick;
            StopAdaptiveEq(restoreManualEq: false);
            _audioLevelService.AudioLevelAvailable -= AudioLevelService_AudioLevelAvailable;
            _audioLevelService.SpectrumAnalyzed -= AudioLevelService_SpectrumAnalyzed;
            _audioLevelService.Dispose();
            _service.ConnectionChanged -= Service_ConnectionChanged;
            _service.NotificationReceived -= Service_NotificationReceived;
            _service.Dispose();
            _captureEndpointMuteService.Dispose();
            _playbackVolumeService.Dispose();
            _lifetime.Dispose();
        }
    }
}
