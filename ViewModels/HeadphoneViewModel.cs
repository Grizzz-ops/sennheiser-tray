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
    public sealed class HeadphoneViewModel : ObservableObject, IDisposable
    {
        private readonly SennheiserService _service;
        private readonly ModeManager _modeManager = new();
        private readonly BatteryTrendEstimator _batteryTrendEstimator = new();
        private readonly AudioLevelService _audioLevelService = new();
        private readonly CaptureEndpointMuteService _captureEndpointMuteService = new();
        private readonly PlaybackVolumeService _playbackVolumeService = new();
        private readonly SettingsStore _settingsStore = new();
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _ancLevelTimer;
        private readonly DispatcherTimer _adaptiveEqTimer;
        private readonly DispatcherTimer _modePinTimer;
        private readonly DispatcherTimer _microphoneStateTimer;
        private readonly DispatcherTimer _focusCycleTimer;
        private readonly DispatcherTimer _batteryPollTimer;
        private readonly DispatcherTimer _playbackVolumeTimer;
        private readonly SemaphoreSlim _connectSemaphore = new(1, 1);
        private readonly SemaphoreSlim _headsetOperationSemaphore = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Queue<double[]> _adaptiveEqSamples = new();
        private double[]? _latestPlaybackSpectrum;
        private double[]? _manualEqValues;
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
        private bool _bassBoostEnabled;
        private bool _isBassImpactEnabled;
        private bool _isBassImpactBusy;
        private bool _isBassBoostBusy;
        private bool _isApplyingMode;
        private bool _isMicrophoneMuted;
        private bool _microphoneStateAvailable;
        private bool _isGlobalMuteHotkeyAvailable;
        private bool _isGamingLatencyHintDismissed;
        private bool _isFocusTimerRunning;
        private bool _isFocusBreak;
        private DateTimeOffset _focusTimerStartedAt;
        private bool _lastFocusBreakState;
        private bool _adaptiveEqEnabled;
        private bool _hasNewPlaybackSpectrum;
        private bool _applyingAdaptiveEq;
        private bool _adaptiveEqProfileLocked;
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
        private bool _isPlaybackVolumeAvailable;
        private bool _hasLoggedPlaybackVolumeError;
        private int _modeBeforeAdaptive = AncModeRules.Custom;
        private int _playbackVolume = 50;
        private double _lastCustomAncLevel = 50;
        private string _selectedMode = "Music";
        private int _silentPlaybackTicks;
        private int _modeApplyGeneration;
        private bool _isDisposed;

        public HeadphoneViewModel() : this(new SennheiserService(), Dispatcher.CurrentDispatcher)
        {
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

            try
            {
                _settingsStore.Load();
                _isGamingLatencyHintDismissed = _settingsStore.GamingLatencyHintDismissed;
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not load local settings", ex);
                LastCommand = $"SETTINGS LOAD ERROR -> {ex.Message}";
            }

            RefreshMicrophoneMuteState();
            RefreshPlaybackVolume();
            _playbackVolumeTimer.Start();

            _service.ConnectionChanged += Service_ConnectionChanged;
            _service.NotificationReceived += Service_NotificationReceived;
            _audioLevelService.AudioLevelAvailable += AudioLevelService_AudioLevelAvailable;
            _audioLevelService.SpectrumAnalyzed += AudioLevelService_SpectrumAnalyzed;
            try
            {
                _audioLevelService.Mode = AudioAnalysisMode.Visualizer;
                _audioLevelService.Start();
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not start the audio visualizer", ex);
                LastCommand = $"VISUALIZER UNAVAILABLE -> {ex.Message}";
            }

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
        public RelayCommand SelectModeCommand { get; }
        public RelayCommand ToggleModePinCommand { get; }
        public RelayCommand RestorePreviousModeCommand { get; }
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
                    OnPropertyChanged(nameof(StatusForeground));
                    OnPropertyChanged(nameof(ConnectionBrush));
                }
            }
        }

        public System.Windows.Media.Brush StatusForeground => _isConnected
            ? new SolidColorBrush(MediaColor.FromRgb(63, 128, 96))
            : StatusText == "Connecting..." || StatusText == "Reconnecting..."
                ? new SolidColorBrush(MediaColor.FromRgb(173, 108, 47))
                : StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || StatusText.Contains("choose", StringComparison.OrdinalIgnoreCase)
                    || StatusText.Contains("no paired", StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(MediaColor.FromRgb(155, 112, 42))
                    : new SolidColorBrush(MediaColor.FromRgb(37, 43, 42));

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
            set => SetProperty(ref _selectedDeviceId, value);
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
                    OnPropertyChanged(nameof(ConnectButtonText));
                    OnPropertyChanged(nameof(IsDisconnected));
                    ConnectCommand.NotifyCanExecuteChanged();
                    BassBoostCommand.NotifyCanExecuteChanged();
                    BassImpactCommand.NotifyCanExecuteChanged();
                    AdaptiveEqCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public int AncMode
        {
            get => _ancMode;
            private set
            {
                if (!SetProperty(ref _ancMode, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(AncModeText));
                OnPropertyChanged(nameof(HomeAncModeText));
                OnPropertyChanged(nameof(IsCustomAncMode));
                OnPropertyChanged(nameof(CustomAncOpacity));
                OnPropertyChanged(nameof(IsAncOffSelected));
                OnPropertyChanged(nameof(IsAncCustomSelected));
                OnPropertyChanged(nameof(IsAncAdaptiveSelected));
                OnPropertyChanged(nameof(IsAdaptiveAnc));
                _isUpdatingAutoToggle = true;
                try
                {
                    AutoAncEnabled = value < 0 ? null : AncModeRules.IsAutoEnabled(value);
                }
                finally
                {
                    _isUpdatingAutoToggle = false;
                }

                SyncCurrentModeState();
            }
        }

        public string AncModeText => AncMode switch
        {
            -1 => "Unavailable",
            AncModeRules.Off => "Off",
            AncModeRules.Custom => "Custom",
            _ => "Adaptive"
        };

        public string HomeAncModeText => AncMode switch
        {
            AncModeRules.Off => "OFF",
            AncModeRules.Custom => "CUSTOM",
            AncModeRules.Adaptive => "ADAPTIVE",
            _ => "UNKNOWN"
        };

        public bool IsCustomAncMode => AncModeRules.IsCustomLevelAvailable(AncMode);
        public bool IsAdaptiveAnc => AncMode == AncModeRules.Adaptive;
        public double CustomAncOpacity => IsCustomAncMode ? 1 : 0.55;
        public bool IsAncOffSelected => AncMode == AncModeRules.Off;
        public bool IsAncCustomSelected => AncMode == AncModeRules.Custom;
        public bool IsAncAdaptiveSelected => AncMode == AncModeRules.Adaptive;

        public double AncLevel
        {
            get => _ancLevel;
            set
            {
                double clamped = Math.Clamp(value, 0, 100);
                if (!SetProperty(ref _ancLevel, clamped))
                {
                    return;
                }

                OnPropertyChanged(nameof(AncLevelText));
                OnPropertyChanged(nameof(OrbLabel));
                SyncCurrentModeState();
                if (IsCustomAncMode && !_isApplyingAncMode)
                {
                    _lastCustomAncLevel = clamped;
                    if (_isSendingAncLevel || (_pendingAction == "ANC" && _debounceTimer.IsEnabled))
                    {
                        _ancLevelChangedWhileSending = true;
                    }
                    else if (!_ancLevelTimer.IsEnabled)
                    {
                        _ancLevelTimer.Start();
                    }
                }
            }
        }

        public string AncLevelText
        {
            get
            {
                int level = (int)Math.Round(AncLevel);
                return level switch
                {
                    0 => "Maximum ANC",
                    50 => "Balanced",
                    100 => "Maximum awareness",
                    < 50 => $"ANC {100 - level * 2}%",
                    _ => $"Awareness {(level - 50) * 2}%"
                };
            }
        }

        public string OrbLabel => OrbDialRules.LabelForLevel(AncLevel);

        public double AudioLevel
        {
            get => _audioLevel;
            private set => SetProperty(ref _audioLevel, value);
        }

        public bool? AutoAncEnabled
        {
            get => _autoAncEnabled;
            set
            {
                if (!SetProperty(ref _autoAncEnabled, value))
                {
                    return;
                }

                if (!_isUpdatingAutoToggle
                    && value.HasValue
                    && AncModeRules.ModeForAutoToggle(value.Value) != AncMode)
                {
                    SetAncMode(AncModeRules.ModeForAutoToggle(value.Value));
                }
            }
        }

        public string Eq1Text => FormatEq(0);
        public string Eq2Text => FormatEq(1);
        public string Eq3Text => FormatEq(2);
        public string Eq4Text => FormatEq(3);
        public string Eq5Text => FormatEq(4);

        public bool BassBoostEnabled
        {
            get => _bassBoostEnabled;
            private set
            {
                if (SetProperty(ref _bassBoostEnabled, value))
                {
                    OnPropertyChanged(nameof(BassBoostText));
                    SyncCurrentModeState();
                }
            }
        }

        public string BassBoostText => $"Bass boost: {(BassBoostEnabled ? "On" : "Off")}";

        public bool IsModeApplying
        {
            get => _isApplyingMode;
            private set
            {
                if (!SetProperty(ref _isApplyingMode, value))
                {
                    return;
                }

                SelectModeCommand.NotifyCanExecuteChanged();
                ToggleModePinCommand.NotifyCanExecuteChanged();
                RestorePreviousModeCommand.NotifyCanExecuteChanged();
                BassImpactCommand.NotifyCanExecuteChanged();
                BassBoostCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanChangeMode));
                OnPropertyChanged(nameof(CanToggleBassBoost));
            }
        }

        public bool CanChangeMode => !IsModeApplying;
        public bool CanRestorePreviousMode =>
            !IsModeApplying && _modeManager.PreviousMode.Id != _modeManager.CurrentMode.Id;
        public bool IsModePinned => _modeManager.IsPinned(DateTimeOffset.UtcNow);
        public bool IsModeLocked => _modeManager.IsLocked;
        public string ModePinActionText =>
            IsModeLocked ? "Unlock selected sound mode" : "Lock selected sound mode indefinitely";

        public string ModePinText
        {
            get
            {
                if (_modeManager.IsLocked)
                {
                    return "Mode locked indefinitely · click to unlock";
                }

                if (_modeManager.PinExpiresAt is DateTimeOffset expiresAt
                    && expiresAt > DateTimeOffset.UtcNow)
                {
                    int seconds = (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
                    return $"Mode pinned for {seconds}s · click to lock indefinitely";
                }

                return "Click to lock this mode indefinitely";
            }
        }

        public bool IsBassImpactEnabled
        {
            get => _isBassImpactEnabled;
            private set
            {
                if (!SetProperty(ref _isBassImpactEnabled, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(BassImpactText));
                BassBoostCommand.NotifyCanExecuteChanged();
                BassImpactCommand.NotifyCanExecuteChanged();
            }
        }

        public string BassImpactText => $"Bass impact: {(IsBassImpactEnabled ? "On" : "Off")}";

        public bool IsBassImpactBusy
        {
            get => _isBassImpactBusy;
            private set
            {
                if (SetProperty(ref _isBassImpactBusy, value))
                {
                    OnPropertyChanged(nameof(CanToggleBassBoost));
                    BassBoostCommand.NotifyCanExecuteChanged();
                    BassImpactCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool CanToggleBassBoost =>
            !IsModeApplying && !IsBassImpactEnabled && !IsBassImpactBusy && !_isBassBoostBusy;

        public bool IsAdaptiveEqEnabled
        {
            get => _adaptiveEqEnabled;
            private set
            {
                if (!SetProperty(ref _adaptiveEqEnabled, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(AdaptiveEqButtonText));
                AdaptiveEqCommand.NotifyCanExecuteChanged();
            }
        }

        public string AdaptiveEqButtonText => IsAdaptiveEqEnabled ? "Stop Adaptive EQ" : "Start Adaptive EQ";

        public string AdaptiveEqStatus
        {
            get => _adaptiveEqStatus;
            private set
            {
                if (SetProperty(ref _adaptiveEqStatus, value))
                {
                    OnPropertyChanged(nameof(AdaptiveEqStatusForeground));
                }
            }
        }

        public System.Windows.Media.Brush AdaptiveEqStatusForeground =>
            AdaptiveEqStatus.StartsWith("Listening", StringComparison.Ordinal)
                || AdaptiveEqStatus.StartsWith("Profile locked", StringComparison.Ordinal)
                    ? MediaBrushes.LightGreen
                    : AdaptiveEqStatus.StartsWith("Could not start", StringComparison.Ordinal)
                        || AdaptiveEqStatus.StartsWith("Connect the headset", StringComparison.Ordinal)
                            ? MediaBrushes.Orange
                            : new SolidColorBrush(MediaColor.FromRgb(128, 151, 168));

        public string LastCommand
        {
            get => _lastCommand;
            private set => SetProperty(ref _lastCommand, value);
        }

        public bool IsTuneOpen
        {
            get => _isTuneOpen;
            private set => SetDrawerState(ref _isTuneOpen, value);
        }

        public bool IsLabOpen
        {
            get => _isLabOpen;
            private set => SetDrawerState(ref _isLabOpen, value);
        }

        public bool IsDrawerOpen => IsTuneOpen || IsLabOpen;
        public bool IsHomeScreenVisible => !IsDrawerOpen;
        public bool DrawerAnimationsEnabled => SystemParameters.ClientAreaAnimation;

        public string SelectedMode
        {
            get => _selectedMode;
            private set
            {
                if (!SetProperty(ref _selectedMode, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsMusicSelected));
                OnPropertyChanged(nameof(IsMovieSelected));
                OnPropertyChanged(nameof(IsGamingSelected));
                OnPropertyChanged(nameof(IsCallsSelected));
                OnPropertyChanged(nameof(IsFocusSelected));
                OnPropertyChanged(nameof(IsCallsMode));
                OnPropertyChanged(nameof(IsGamingMode));
                OnPropertyChanged(nameof(IsFocusMode));
                OnPropertyChanged(nameof(IsGamingHintVisible));
                FocusCycleCommand.NotifyCanExecuteChanged();
                if (IsCallsMode)
                {
                    RefreshMicrophoneMuteState();
                    _microphoneStateTimer.Start();
                }
                else
                {
                    _microphoneStateTimer.Stop();
                }

                SyncModeChipItems();
                AnimateAccent(_modeManager.GetMode(value));
                RestorePreviousModeCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanRestorePreviousMode));
            }
        }

        public bool IsMusicSelected => SelectedMode == "Music";
        public bool IsMovieSelected => SelectedMode == "Movie";
        public bool IsGamingSelected => SelectedMode == "Gaming";
        public bool IsCallsSelected => SelectedMode == "Calls";
        public bool IsFocusSelected => SelectedMode == "Focus";
        public bool IsCallsMode => SelectedMode == "Calls";
        public bool IsGamingMode => SelectedMode == "Gaming";
        public bool IsFocusMode => SelectedMode == "Focus";
        public bool IsGamingHintVisible => IsGamingMode && !_isGamingLatencyHintDismissed;
        public bool IsSidetoneAvailable =>
            GaiaCommands.IsVerified(GaiaCommands.SidetoneSet)
            && GaiaCommands.IsVerified(GaiaCommands.SidetoneGet);
        public bool IsLowLatencyAvailable => GaiaCommands.IsVerified(GaiaCommands.LowLatencySet);
        public bool IsMicrophoneMuted => _isMicrophoneMuted;
        public bool IsMicrophoneStateAvailable => _microphoneStateAvailable;
        public string MicrophoneMuteText => _microphoneStateAvailable
            ? _isMicrophoneMuted ? "Unmute mic" : "Mute mic"
            : "Microphone endpoint unavailable";
        public string MicrophoneMuteHotkeyStatus => _isGlobalMuteHotkeyAvailable
            ? "Global shortcut: Ctrl+Shift+M"
            : "Global shortcut unavailable (may already be in use)";
        public bool IsGlobalMuteHotkeyAvailable => _isGlobalMuteHotkeyAvailable;
        public bool IsFocusTimerRunning => _isFocusTimerRunning;
        public bool IsFocusBreak => _isFocusBreak;
        public string FocusCycleButtonText => IsFocusTimerRunning ? "Stop 25/5 timer" : "Start 25/5 timer";
        public string FocusCycleStatus
        {
            get
            {
                if (!IsFocusTimerRunning)
                {
                    return "Optional focus timer · 25 min focus / 5 min break";
                }

                FocusCycleState state = FocusCycleRules.GetState(DateTimeOffset.UtcNow - _focusTimerStartedAt);
                return $"{(state.IsBreak ? "Break" : "Focus")} · {state.Remaining:mm\\:ss} remaining";
            }
        }

        private void SyncCurrentModeState()
        {
            int? ancMode = AncMode is AncModeRules.Off or AncModeRules.Custom or AncModeRules.Adaptive
                ? AncMode
                : null;
            int[] eqBands = EqValues
                .Select(value => ModeManager.NormalizeEqBand((int)Math.Round(value)))
                .ToArray();

            _modeManager.UpdateCurrentState(
                ancMode,
                (int)Math.Round(AncLevel),
                eqBands,
                BassBoostEnabled);
            SyncModeChipItems();
        }

        private void SyncModeChipItems()
        {
            foreach (ModeChipItem item in ModeChipItems)
            {
                item.UpdateMode(_modeManager.GetMode(item.Id));
                item.IsSelected = StringComparer.Ordinal.Equals(item.Id, _modeManager.CurrentMode.Id);
            }
        }

        private void ToggleModePin()
        {
            _modeManager.ToggleLock(DateTimeOffset.UtcNow);
            UpdateModePinState();
        }

        private void UpdateModePinState()
        {
            bool pinned = _modeManager.IsPinned(DateTimeOffset.UtcNow);
            if (pinned && !_modePinTimer.IsEnabled)
            {
                _modePinTimer.Start();
            }
            else if (!pinned)
            {
                _modePinTimer.Stop();
            }

            OnPropertyChanged(nameof(IsModePinned));
            OnPropertyChanged(nameof(IsModeLocked));
            OnPropertyChanged(nameof(ModePinText));
        }

        private void ModePinTimer_Tick(object? sender, EventArgs e)
        {
            _modeManager.ExpirePin(DateTimeOffset.UtcNow);
            UpdateModePinState();
        }

        private void MicrophoneStateTimer_Tick(object? sender, EventArgs e) =>
            RefreshMicrophoneMuteState();

        private void RefreshMicrophoneMuteState()
        {
            try
            {
                bool muted = _captureEndpointMuteService.IsMuted;
                bool changed = SetProperty(ref _isMicrophoneMuted, muted, nameof(IsMicrophoneMuted));
                bool availableChanged = SetProperty(
                    ref _microphoneStateAvailable,
                    true,
                    nameof(IsMicrophoneStateAvailable));
                if (changed || availableChanged)
                {
                    OnPropertyChanged(nameof(MicrophoneMuteText));
                }
            }
            catch (Exception ex)
            {
                bool changed = SetProperty(
                    ref _microphoneStateAvailable,
                    false,
                    nameof(IsMicrophoneStateAvailable));
                if (changed)
                {
                    OnPropertyChanged(nameof(MicrophoneMuteText));
                }

                AppLogger.LogException("Could not read the default microphone mute state", ex);
                LastCommand = $"MICROPHONE STATE ERROR -> {ex.Message}";
            }
        }

        private void ToggleMicrophoneMute()
        {
            try
            {
                bool muted = _captureEndpointMuteService.ToggleMute();
                SetProperty(ref _isMicrophoneMuted, muted, nameof(IsMicrophoneMuted));
                SetProperty(ref _microphoneStateAvailable, true, nameof(IsMicrophoneStateAvailable));
                OnPropertyChanged(nameof(MicrophoneMuteText));
                LastCommand = muted
                    ? "MICROPHONE -> Default capture endpoint muted"
                    : "MICROPHONE -> Default capture endpoint unmuted";
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not toggle the default microphone mute state", ex);
                SetProperty(ref _microphoneStateAvailable, false, nameof(IsMicrophoneStateAvailable));
                OnPropertyChanged(nameof(MicrophoneMuteText));
                LastCommand = $"MICROPHONE MUTE ERROR -> {ex.Message}";
            }
        }

        public void SetGlobalMuteHotkeyAvailable(bool isAvailable)
        {
            if (SetProperty(
                ref _isGlobalMuteHotkeyAvailable,
                isAvailable,
                nameof(IsGlobalMuteHotkeyAvailable)))
            {
                OnPropertyChanged(nameof(MicrophoneMuteHotkeyStatus));
            }
        }

        private void DismissGamingHint()
        {
            _isGamingLatencyHintDismissed = true;
            OnPropertyChanged(nameof(IsGamingHintVisible));
            try
            {
                _settingsStore.DismissGamingLatencyHint();
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not save gaming latency hint dismissal", ex);
                LastCommand = $"SETTINGS SAVE ERROR -> {ex.Message}";
            }
        }

        private void ToggleFocusCycle()
        {
            if (IsFocusTimerRunning)
            {
                _focusCycleTimer.Stop();
                SetProperty(ref _isFocusTimerRunning, false, nameof(IsFocusTimerRunning));
                SetProperty(ref _isFocusBreak, false, nameof(IsFocusBreak));
                OnPropertyChanged(nameof(FocusCycleButtonText));
                OnPropertyChanged(nameof(FocusCycleStatus));
                LastCommand = "FOCUS TIMER -> Stopped";
                return;
            }

            _focusTimerStartedAt = DateTimeOffset.UtcNow;
            _lastFocusBreakState = false;
            SetProperty(ref _isFocusTimerRunning, true, nameof(IsFocusTimerRunning));
            SetProperty(ref _isFocusBreak, false, nameof(IsFocusBreak));
            OnPropertyChanged(nameof(FocusCycleButtonText));
            OnPropertyChanged(nameof(FocusCycleStatus));
            _focusCycleTimer.Start();
            LastCommand = "FOCUS TIMER -> Started a 25/5 local timer";
        }

        private void FocusCycleTimer_Tick(object? sender, EventArgs e)
        {
            FocusCycleState state = FocusCycleRules.GetState(DateTimeOffset.UtcNow - _focusTimerStartedAt);
            if (state.IsBreak != _lastFocusBreakState)
            {
                _lastFocusBreakState = state.IsBreak;
                SetProperty(ref _isFocusBreak, state.IsBreak, nameof(IsFocusBreak));
                try
                {
                    SystemSounds.Asterisk.Play();
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Could not play the focus timer chime", ex);
                    LastCommand = $"FOCUS CHIME ERROR -> {ex.Message}";
                }
            }

            OnPropertyChanged(nameof(FocusCycleStatus));
        }

        private Task SelectModeAsync(string id) => ApplyModeAsync(id, restorePrevious: false);

        private Task RestorePreviousModeAsync() => ApplyModeAsync(null, restorePrevious: true);

        private async Task ApplyModeAsync(string? requestedModeId, bool restorePrevious)
        {
            if (IsModeApplying)
            {
                return;
            }

            IsModeApplying = true;
            _modeApplyGeneration++;
            _debounceTimer.Stop();
            _ancLevelTimer.Stop();
            _modePinTimer.Stop();
            _pendingAction = "";
            _ancLevelChangedWhileSending = false;
            if (IsAdaptiveEqEnabled)
            {
                StopAdaptiveEq(restoreManualEq: false);
            }

            await _headsetOperationSemaphore.WaitAsync();
            try
            {
                SyncCurrentModeState();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (restorePrevious)
                {
                    await _modeManager.RestorePreviousManuallyAsync(now, ApplySelectedModeAsync);
                }
                else
                {
                    await _modeManager.ApplyManuallyAsync(
                        requestedModeId ?? throw new ArgumentNullException(nameof(requestedModeId)),
                        now,
                        ApplySelectedModeAsync);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Sound mode application failed", ex);
                LastCommand = $"MODE APPLICATION ERROR -> {ex.Message}";
            }
            finally
            {
                _headsetOperationSemaphore.Release();
                IsModeApplying = false;
            }
        }

        private async Task ApplySelectedModeAsync(SoundMode mode)
        {
            bool previousBassBoost = BassBoostEnabled;
            SelectedMode = mode.Id;
            UpdateModePinState();

            _isApplyingAncMode = true;
            try
            {
                AncMode = mode.AncMode;
                AncLevel = mode.AncLevel;
            }
            finally
            {
                _isApplyingAncMode = false;
            }

            double[] eqBands = mode.EqBands.Select(value => (double)value).ToArray();
            ApplyEqValues(eqBands);
            BassBoostEnabled = mode.BassBoost;
            SyncCurrentModeState();

            if (!_service.IsConnected)
            {
                LastCommand = $"MODE {mode.Name} LOADED LOCALLY -> Connect the headset to apply it.";
                return;
            }

            GaiaResult ancResult = mode.AncMode switch
            {
                AncModeRules.Off => await _service.SetAncAsync(false),
                AncModeRules.Custom => await _service.SetAncModeAsync(false, mode.AncLevel),
                AncModeRules.Adaptive => await _service.SetAncModeAsync(true, mode.AncLevel),
                _ => throw new InvalidOperationException($"Unsupported ANC mode {mode.AncMode}.")
            };
            if (!ancResult.Success)
            {
                BassBoostEnabled = previousBassBoost;
                LastCommand = $"MODE {mode.Name} FAILED AT ANC -> {ancResult.Log}";
                return;
            }

            GaiaResult eqResult = await SendEqAsync(eqBands);
            if (!eqResult.Success)
            {
                BassBoostEnabled = previousBassBoost;
                LastCommand = $"MODE {mode.Name} FAILED AT EQ -> ANC: {ancResult.Log} | EQ: {eqResult.Log}";
                return;
            }

            GaiaResult bassResult = await _service.SetBassBoostAsync(mode.BassBoost);
            if (bassResult.Payload is { Length: > 0 })
            {
                BassBoostEnabled = bassResult.Payload[0] != 0;
            }
            else
            {
                BassBoostEnabled = previousBassBoost;
            }

            LastCommand = bassResult.Success
                ? $"MODE {mode.Name} APPLIED -> ANC: {ancResult.Log} | EQ: {eqResult.Log} | BASS: {bassResult.Log}"
                : $"MODE {mode.Name} FAILED AT BASS BOOST -> ANC: {ancResult.Log} | EQ: {eqResult.Log} | BASS: {bassResult.Log}";
        }

        public void CloseDrawers()
        {
            IsTuneOpen = false;
            IsLabOpen = false;
        }

        private void SetDrawerState(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (!SetProperty(ref field, value, propertyName))
            {
                return;
            }

            OnPropertyChanged(nameof(IsHomeScreenVisible));
            OnPropertyChanged(nameof(IsDrawerOpen));
        }

        private static void AnimateAccent(SoundMode mode)
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(mode.Accent) is not MediaColor color
                || System.Windows.Application.Current?.Resources["AccentBrush"] is not SolidColorBrush brush)
            {
                return;
            }

            if (brush.IsFrozen)
            {
                brush = brush.CloneCurrentValue();
                System.Windows.Application.Current.Resources["AccentBrush"] = brush;
            }

            if (SystemParameters.ClientAreaAnimation)
            {
                brush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(color, TimeSpan.FromMilliseconds(400))
                    {
                        FillBehavior = FillBehavior.HoldEnd
                    });
            }
            else
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = color;
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                await LoadAvailableDevicesAsync();
                if (!string.IsNullOrWhiteSpace(SelectedDeviceId) && !await TryConnectAsync())
                {
                    StartAutoReconnect();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Headphone view model initialization failed", ex);
                LastCommand = $"INITIALIZATION ERROR -> {ex.Message}";
                StatusText = "Device search failed";
            }
        }

        private async Task LoadAvailableDevicesAsync()
        {
            try
            {
                string? previousSelection = SelectedDeviceId;
                IReadOnlyList<SennheiserDevice> devices = await _service.GetAvailableDevicesAsync();
                Devices.Clear();
                foreach (SennheiserDevice device in devices)
                {
                    Devices.Add(device);
                }

                string? rememberedDeviceId = null;
                try
                {
                    rememberedDeviceId = DeviceSelectionStore.ReadLastDeviceId();
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Could not read the remembered headset", ex);
                    LastCommand = $"DEVICE SETTING ERROR -> {ex.Message}";
                }

                SelectedDeviceId = devices.FirstOrDefault(
                    device => string.Equals(device.Id, previousSelection, StringComparison.Ordinal))?.Id
                    ?? devices.FirstOrDefault(
                        device => string.Equals(device.Id, rememberedDeviceId, StringComparison.Ordinal))?.Id
                    ?? (devices.Count == 1 ? devices[0].Id : null);

                if (devices.Count == 0)
                {
                    StatusText = "No paired Sennheiser control device";
                }
                else if (devices.Count > 1 && SelectedDeviceId == null)
                {
                    StatusText = "Choose a headset to connect";
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not enumerate Sennheiser devices", ex);
                StatusText = "Device search failed";
                LastCommand = $"DEVICE SEARCH ERROR -> {ex.Message}";
            }
        }

        private async Task RefreshAndConnectAsync()
        {
            await LoadAvailableDevicesAsync();
            if (!await TryConnectAsync())
            {
                StartAutoReconnect();
            }
        }

        private async Task<bool> TryConnectAsync()
        {
            await _connectSemaphore.WaitAsync();
            try
            {
                string? deviceId = SelectedDeviceId;
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    StatusText = "Choose a headset to connect";
                    return false;
                }

                StatusText = "Connecting...";
                bool connected = await _service.ConnectAsync(deviceId);
                if (!connected)
                {
                    StatusText = $"Connection failed: {_service.LastConnectionError}";
                    return false;
                }

                StatusText = _service.ConnectedDeviceName;
                try
                {
                    DeviceSelectionStore.SaveLastDeviceId(deviceId);
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Could not remember the selected headset", ex);
                    LastCommand = $"DEVICE SETTING ERROR -> {ex.Message}";
                }

                return true;
            }
            finally
            {
                _connectSemaphore.Release();
            }
        }

        private void Service_ConnectionChanged(bool connected)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isDisposed)
                {
                    return;
                }

                IsConnected = connected;
                if (connected)
                {
                    StatusText = _service.ConnectedDeviceName;
                    _ = SynchronizeHeadsetStateAsync();
                }
                else if (!_lifetime.IsCancellationRequested)
                {
                    StatusText = "Reconnecting...";
                    StartAutoReconnect();
                }
            }));
        }

        private void Service_NotificationReceived(ushort commandId, byte[] payload)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (commandId == GaiaCommands.BatteryNotification
                    && TryParseBatteryLevel(payload, out int batteryLevel))
                {
                    BatteryLevel = batteryLevel;
                    UpdateBatteryEstimate(batteryLevel);
                    return;
                }

                LastCommand =
                    $"GAIA NOTIFICATION 0x{commandId:X4} -> {BitConverter.ToString(payload).Replace("-", " ")}";
            }));
        }

        private static bool TryParseBatteryLevel(byte[]? payload, out int batteryLevel)
        {
            batteryLevel = 0;
            if (payload is not { Length: > 0 })
            {
                return false;
            }

            if (payload.Length == 1)
            {
                batteryLevel = payload[0];
                return batteryLevel <= 100;
            }

            int sourceCount = payload[0];
            if (sourceCount == 0 || sourceCount != payload.Length - 1)
            {
                return false;
            }

            int highestLevel = 0;
            for (int index = 1; index < payload.Length; index++)
            {
                if (payload[index] > 100)
                {
                    return false;
                }

                highestLevel = Math.Max(highestLevel, payload[index]);
            }

            batteryLevel = highestLevel;
            return true;
        }

        private async Task SynchronizeHeadsetStateAsync()
        {
            if (!_service.IsConnected)
            {
                return;
            }

            try
            {
                var stateLogs = new List<string>
                {
                    "Battery not read",
                    "ANC state not read (GET command unverified)",
                    "ANC level not read (GET command unverified)",
                    "EQ not read (GET command unverified)"
                };

                GaiaResult batterySubscription = await _service.SendPacketAsync(
                    GaiaCommands.NotificationRegister,
                    new[] { GaiaCommands.BatteryFeature });
                GaiaResult battery = await _service.RequestAsync(
                    GaiaCommands.BatteryGet,
                    Array.Empty<byte>(),
                    GaiaCommands.BatteryReply,
                    TimeSpan.FromSeconds(2));
                if (battery.Success && TryParseBatteryLevel(battery.Payload, out int batteryLevel))
                {
                    BatteryLevel = batteryLevel;
                    UpdateBatteryEstimate(batteryLevel);
                    stateLogs[0] = $"Battery {batteryLevel}% -> {battery.Log}";
                }
                else
                {
                    stateLogs[0] = battery.Success
                        ? $"Battery reply invalid -> {BitConverter.ToString(battery.Payload ?? Array.Empty<byte>())}"
                        : $"Battery unavailable -> {battery.Log}";
                }

                if (!batterySubscription.Success)
                {
                    stateLogs[0] += $" | notification subscription failed -> {batterySubscription.Log}";
                }

                if (GaiaCommands.IsVerified(GaiaCommands.AncStateGet)
                    && GaiaCommands.IsVerified(GaiaCommands.AncStateReply))
                {
                    GaiaResult ancState = await _service.RequestAsync(
                        GaiaCommands.AncStateGet, Array.Empty<byte>(),
                        GaiaCommands.AncStateReply, TimeSpan.FromSeconds(2));
                    stateLogs[1] = $"ANC state -> {ancState.Log}";
                }

                if (GaiaCommands.IsVerified(GaiaCommands.AncLevelGet)
                    && GaiaCommands.IsVerified(GaiaCommands.AncLevelReply))
                {
                    GaiaResult ancLevel = await _service.RequestAsync(
                        GaiaCommands.AncLevelGet, Array.Empty<byte>(),
                        GaiaCommands.AncLevelReply, TimeSpan.FromSeconds(2));
                    stateLogs[2] = $"ANC level -> {ancLevel.Log}";
                }

                if (GaiaCommands.IsVerified(GaiaCommands.EqBandGet)
                    && GaiaCommands.IsVerified(GaiaCommands.EqBandReply))
                {
                    GaiaResult eq = await _service.RequestAsync(
                        GaiaCommands.EqBandGet, Array.Empty<byte>(),
                        GaiaCommands.EqBandReply, TimeSpan.FromSeconds(2));
                    stateLogs[3] = $"EQ -> {eq.Log}";
                }

                GaiaResult bassBoost = await _service.RequestAsync(
                    GaiaCommands.BassBoostGet, Array.Empty<byte>(),
                    GaiaCommands.BassBoostReply, TimeSpan.FromSeconds(2));
                if (bassBoost.Success && bassBoost.Payload is { Length: > 0 })
                {
                    BassBoostEnabled = bassBoost.Payload[0] != 0;
                }

                stateLogs.Add($"Bass boost -> {bassBoost.Log}");
                LastCommand = $"STATE SYNC -> {string.Join(" | ", stateLogs)}";
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Headset state synchronization failed", ex);
                LastCommand = $"STATE SYNC ERROR -> {ex.Message}";
            }
        }

        private void UpdateBatteryEstimate(int batteryLevel)
        {
            _batteryTrendEstimator.AddSample(DateTimeOffset.UtcNow, batteryLevel);
            OnPropertyChanged(nameof(IsChargingInferred));
            string estimate = _batteryTrendEstimator.GetEstimate(batteryLevel) ?? "";
            if (BatteryEstimateText == estimate)
            {
                return;
            }

            BatteryEstimateText = estimate;
            OnPropertyChanged(nameof(BatteryText));
            OnPropertyChanged(nameof(BatteryEstimateStatus));
        }

        private async void BatteryPollTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsConnected || _isDisposed || _isBatteryPollInProgress)
            {
                return;
            }

            _isBatteryPollInProgress = true;
            await _headsetOperationSemaphore.WaitAsync();
            try
            {
                if (!IsConnected || _isDisposed)
                {
                    return;
                }

                GaiaResult result = await _service.RequestAsync(
                    GaiaCommands.BatteryGet,
                    Array.Empty<byte>(),
                    GaiaCommands.BatteryReply,
                    TimeSpan.FromSeconds(2));
                if (result.Success && TryParseBatteryLevel(result.Payload, out int batteryLevel))
                {
                    BatteryLevel = batteryLevel;
                    UpdateBatteryEstimate(batteryLevel);
                }
                else if (!result.Success)
                {
                    LastCommand = $"BATTERY REFRESH -> {result.Log}";
                }
                else
                {
                    LastCommand =
                        $"BATTERY REFRESH -> Invalid reply: {BitConverter.ToString(result.Payload ?? Array.Empty<byte>())}";
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Battery refresh failed", ex);
                LastCommand = $"BATTERY REFRESH ERROR -> {ex.Message}";
            }
            finally
            {
                _isBatteryPollInProgress = false;
                _headsetOperationSemaphore.Release();
            }
        }

        private void RefreshPlaybackVolume()
        {
            try
            {
                int volume = _playbackVolumeService.GetDefaultPlaybackVolume();
                _hasLoggedPlaybackVolumeError = false;
                IsPlaybackVolumeAvailable = true;
                _isRefreshingPlaybackVolume = true;
                try
                {
                    PlaybackVolume = volume;
                }
                finally
                {
                    _isRefreshingPlaybackVolume = false;
                }
            }
            catch (Exception ex)
            {
                ReportPlaybackVolumeError("Could not read Windows playback volume", ex);
            }
        }

        private void ReportPlaybackVolumeError(string context, Exception exception)
        {
            if (!_hasLoggedPlaybackVolumeError)
            {
                AppLogger.LogException(context, exception);
                LastCommand = $"PLAYBACK VOLUME ERROR -> {exception.Message}";
                _hasLoggedPlaybackVolumeError = true;
            }

            IsPlaybackVolumeAvailable = false;
        }

        private void PlaybackVolumeTimer_Tick(object? sender, EventArgs e) =>
            RefreshPlaybackVolume();

        private void StartAutoReconnect()
        {
            if (_isDisposed
                || _lifetime.IsCancellationRequested
                || string.IsNullOrWhiteSpace(SelectedDeviceId)
                || (_reconnectTask != null && !_reconnectTask.IsCompleted))
            {
                return;
            }

            _reconnectTask = ReconnectWithBackoffAsync(_lifetime.Token);
        }

        private async Task ReconnectWithBackoffAsync(CancellationToken cancellationToken)
        {
            int attempt = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested && !_service.IsConnected)
                {
                    StatusText = "Reconnecting...";
                    await Task.Delay(ReconnectBackoff.DelayForAttempt(attempt), cancellationToken);
                    if (cancellationToken.IsCancellationRequested || _service.IsConnected)
                    {
                        return;
                    }

                    await TryConnectAsync();
                    attempt++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Automatic reconnect failed", ex);
                StatusText = "Reconnecting...";
                LastCommand = $"RECONNECT ERROR -> {ex.Message}";
            }
        }

        private void SetAncMode(int mode)
        {
            if (mode is not (AncModeRules.Off or AncModeRules.Custom or AncModeRules.Adaptive))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (mode == AncModeRules.Custom && AncMode != AncModeRules.Custom)
            {
                AncLevel = _lastCustomAncLevel;
            }

            AncMode = mode;
            _pendingAction = "ANC";
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void ToggleAncFromOrb()
        {
            if (!IsConnected)
            {
                return;
            }

            if (AncMode == AncModeRules.Off)
            {
                AncLevel = _lastCustomAncLevel;
                SetAncMode(AncModeRules.Custom);
            }
            else
            {
                if (AncMode == AncModeRules.Custom)
                {
                    _lastCustomAncLevel = AncLevel;
                }

                SetAncMode(AncModeRules.Off);
            }
        }

        private void ToggleAdaptiveAnc()
        {
            if (AncMode == AncModeRules.Adaptive)
            {
                SetAncMode(_modeBeforeAdaptive);
                return;
            }

            _modeBeforeAdaptive = AncMode is AncModeRules.Off or AncModeRules.Custom
                ? AncMode
                : AncModeRules.Custom;
            SetAncMode(AncModeRules.Adaptive);
        }

        private void AudioLevelService_AudioLevelAvailable(float level)
        {
            _dispatcher.BeginInvoke(new Action(() => AudioLevel = level));
        }

        private void AudioLevelService_SpectrumAnalyzed(double[] spectrum)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsAdaptiveEqEnabled)
                {
                    _latestPlaybackSpectrum = spectrum;
                    _hasNewPlaybackSpectrum = true;
                }
            }));
        }

        private void EqValues_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Eq1Text));
            OnPropertyChanged(nameof(Eq2Text));
            OnPropertyChanged(nameof(Eq3Text));
            OnPropertyChanged(nameof(Eq4Text));
            OnPropertyChanged(nameof(Eq5Text));
            SyncCurrentModeState();
            if (IsAdaptiveEqEnabled && !_applyingAdaptiveEq)
            {
                _manualEqValues = EqValues.ToArray();
                _adaptiveEqSamples.Clear();
                _adaptiveEqProfileLocked = false;
                AdaptiveEqStatus = "Manual curve saved · learning a new steady profile…";
            }

            if (_isApplyingBassImpact || IsModeApplying)
            {
                return;
            }

            _pendingAction = "EQ";
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private string FormatEq(int index) => $"{(EqValues[index] / 10.0):+0.0;-0.0;0.0} dB";

        private void ApplyEqValues(params double[] values)
        {
            if (values.Length != EqValues.Count)
            {
                throw new ArgumentException("An EQ profile must contain five band values.", nameof(values));
            }

            _isApplyingBassImpact = true;
            try
            {
                for (int band = 0; band < EqValues.Count; band++)
                {
                    EqValues[band] = values[band];
                }
            }
            finally
            {
                _isApplyingBassImpact = false;
            }
        }

        private async void DebounceTimer_Tick(object? sender, EventArgs e)
        {
            _debounceTimer.Stop();
            int generation = _modeApplyGeneration;
            if (IsModeApplying)
            {
                _pendingAction = "";
                return;
            }

            await _headsetOperationSemaphore.WaitAsync();
            try
            {
                if (IsModeApplying || generation != _modeApplyGeneration)
                {
                    return;
                }

                string pendingAction = _pendingAction;
                _pendingAction = "";
                if (pendingAction == "ANC")
                {
                    int mode = AncMode;
                    _ancLevelChangedWhileSending = false;
                    _isApplyingAncMode = true;
                    GaiaResult result = mode switch
                    {
                        AncModeRules.Off => await _service.SetAncAsync(false),
                        AncModeRules.Custom => await _service.SetAncModeAsync(false, (int)AncLevel),
                        _ => await _service.SetAncModeAsync(true, (int)AncLevel)
                    };
                    LastCommand = $"NOISE CONTROL -> {result.Log}";
                }
                else if (pendingAction == "EQ")
                {
                    GaiaResult result = await SendEqAsync(EqValues.ToArray());
                    LastCommand = $"EQ -> {result.Log}";
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Debounced headset update failed", ex);
                LastCommand = $"HEADSET UPDATE ERROR -> {ex.Message}";
            }
            finally
            {
                _isApplyingAncMode = false;
                if (_ancLevelChangedWhileSending && IsCustomAncMode)
                {
                    _ancLevelChangedWhileSending = false;
                    _ancLevelTimer.Start();
                }

                _headsetOperationSemaphore.Release();
            }
        }

        private async void AncLevelTimer_Tick(object? sender, EventArgs e)
        {
            _ancLevelTimer.Stop();
            if (!IsCustomAncMode)
            {
                return;
            }

            int generation = _modeApplyGeneration;
            if (IsModeApplying)
            {
                return;
            }

            if (_isSendingAncLevel || _isApplyingAncMode)
            {
                _ancLevelChangedWhileSending = true;
                return;
            }

            _isSendingAncLevel = true;
            _ancLevelChangedWhileSending = false;
            await _headsetOperationSemaphore.WaitAsync();
            try
            {
                if (IsModeApplying || generation != _modeApplyGeneration)
                {
                    return;
                }

                GaiaResult result = await _service.SetAncCustomLevelAsync((int)Math.Round(AncLevel));
                LastCommand = $"CUSTOM NOISE MIX -> {result.Log}";
            }
            catch (Exception ex)
            {
                AppLogger.LogException("ANC level update failed", ex);
                LastCommand = $"ANC LEVEL ERROR -> {ex.Message}";
            }
            finally
            {
                _isSendingAncLevel = false;
                if (_ancLevelChangedWhileSending && IsCustomAncMode)
                {
                    _ancLevelChangedWhileSending = false;
                    _ancLevelTimer.Start();
                }

                _headsetOperationSemaphore.Release();
            }
        }

        private Task<GaiaResult> SendEqAsync(double[] values) => _service.SetEqAsync(
            (sbyte)values[0],
            (sbyte)values[1],
            (sbyte)values[2],
            (sbyte)values[3],
            (sbyte)values[4]);

        private async Task ToggleBassBoostAsync()
        {
            if (IsModeApplying)
            {
                return;
            }

            int generation = _modeApplyGeneration;
            await _headsetOperationSemaphore.WaitAsync();
            if (IsModeApplying || generation != _modeApplyGeneration)
            {
                _headsetOperationSemaphore.Release();
                return;
            }

            _isBassBoostBusy = true;
            OnPropertyChanged(nameof(CanToggleBassBoost));
            BassBoostCommand.NotifyCanExecuteChanged();
            bool newState = !BassBoostEnabled;
            try
            {
                GaiaResult result = await _service.SetBassBoostAsync(newState);
                if (result.Success && result.Payload is { Length: > 0 })
                {
                    BassBoostEnabled = result.Payload[0] != 0;
                }

                LastCommand = $"BASS BOOST -> {result.Log}";
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Bass Boost command failed", ex);
                LastCommand = $"BASS BOOST ERROR -> {ex.Message}";
            }
            finally
            {
                _isBassBoostBusy = false;
                OnPropertyChanged(nameof(CanToggleBassBoost));
                BassBoostCommand.NotifyCanExecuteChanged();
                _headsetOperationSemaphore.Release();
            }
        }

        private async Task ToggleBassImpactAsync()
        {
            if (!IsConnected)
            {
                LastCommand = "BASS IMPACT -> Connect the headset before applying this preset.";
                return;
            }

            if (IsModeApplying)
            {
                return;
            }

            int generation = _modeApplyGeneration;
            await _headsetOperationSemaphore.WaitAsync();
            if (IsModeApplying || generation != _modeApplyGeneration)
            {
                _headsetOperationSemaphore.Release();
                return;
            }

            IsBassImpactBusy = true;
            try
            {
                if (!IsBassImpactEnabled)
                {
                    if (IsAdaptiveEqEnabled)
                    {
                        StopAdaptiveEq(restoreManualEq: true);
                    }

                    _bassImpactPreviousEq = EqValues.ToArray();
                    _bassImpactPreviousBoostState = BassBoostEnabled;
                    ApplyEqValues(60, 30, 0, 0, 0);

                    GaiaResult boostResult = await _service.SetBassBoostAsync(true);
                    if (boostResult.Success && boostResult.Payload is { Length: > 0 })
                    {
                        BassBoostEnabled = boostResult.Payload[0] != 0;
                    }

                    GaiaResult eqResult = await SendEqAsync(new[] { 60d, 30d, 0d, 0d, 0d });
                    if (!boostResult.Success || !eqResult.Success)
                    {
                        double[] previousEq = _bassImpactPreviousEq;
                        ApplyEqValues(previousEq);
                        GaiaResult rollbackBoost = await _service.SetBassBoostAsync(_bassImpactPreviousBoostState);
                        BassBoostEnabled = _bassImpactPreviousBoostState;
                        LastCommand =
                            $"BASS IMPACT FAILED -> {boostResult.Log} | {eqResult.Log} | rollback: {rollbackBoost.Log}";
                        _bassImpactPreviousEq = null;
                        return;
                    }

                    IsBassImpactEnabled = true;
                    LastCommand = $"BASS IMPACT ON -> {boostResult.Log} | EQ: {eqResult.Log}";
                }
                else
                {
                    double[] previousEq = _bassImpactPreviousEq
                        ?? throw new InvalidOperationException("The saved EQ profile is missing.");
                    ApplyEqValues(previousEq);
                    GaiaResult eqResult = await SendEqAsync(previousEq);
                    GaiaResult boostResult = await _service.SetBassBoostAsync(_bassImpactPreviousBoostState);
                    if (boostResult.Success && boostResult.Payload is { Length: > 0 })
                    {
                        BassBoostEnabled = boostResult.Payload[0] != 0;
                    }

                    IsBassImpactEnabled = false;
                    _bassImpactPreviousEq = null;
                    LastCommand = $"BASS IMPACT OFF -> {boostResult.Log} | EQ: {eqResult.Log}";
                }

            }
            catch (Exception ex)
            {
                AppLogger.LogException("Bass Impact operation failed", ex);
                LastCommand = $"BASS IMPACT ERROR -> {ex.Message}";
            }
            finally
            {
                IsBassImpactBusy = false;
                _headsetOperationSemaphore.Release();
            }
        }

        private void ToggleAdaptiveEq()
        {
            if (IsAdaptiveEqEnabled)
            {
                StopAdaptiveEq(restoreManualEq: true);
                return;
            }

            if (!_service.IsConnected)
            {
                AdaptiveEqStatus = "Connect the headset before enabling adaptive EQ.";
                return;
            }

            try
            {
                _audioLevelService.Mode = AudioAnalysisMode.AdaptiveEq;
                if (!_audioLevelService.IsRunning)
                {
                    _audioLevelService.Start();
                }

                double[] previousEq = EqValues.ToArray();
                _manualEqValues = previousEq;
                _latestPlaybackSpectrum = null;
                _hasNewPlaybackSpectrum = false;
                _adaptiveEqSamples.Clear();
                _adaptiveEqProfileLocked = false;
                _silentPlaybackTicks = 0;
                IsAdaptiveEqEnabled = true;
                AdaptiveEqStatus = "Listening to Windows playback locally…";
                _adaptiveEqTimer.Start();
            }
            catch (Exception ex)
            {
                _audioLevelService.Mode = AudioAnalysisMode.Visualizer;
                AppLogger.LogException("Could not start adaptive EQ capture", ex);
                AdaptiveEqStatus = $"Could not start playback analysis: {ex.Message}";
            }
        }

        private void AdaptiveEqTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsAdaptiveEqEnabled || !_hasNewPlaybackSpectrum || _latestPlaybackSpectrum == null)
            {
                return;
            }

            _hasNewPlaybackSpectrum = false;
            double[] spectrum = _latestPlaybackSpectrum;
            if (spectrum.All(level => level < -72))
            {
                _silentPlaybackTicks++;
                if (_silentPlaybackTicks >= 3)
                {
                    _adaptiveEqSamples.Clear();
                    _adaptiveEqProfileLocked = false;
                    AdaptiveEqStatus = "Playback paused · ready to learn the next track";
                }

                return;
            }

            _silentPlaybackTicks = 0;
            if (_adaptiveEqProfileLocked)
            {
                return;
            }

            _adaptiveEqSamples.Enqueue((double[])spectrum.Clone());
            const int stableWindowSamples = 15;
            while (_adaptiveEqSamples.Count > stableWindowSamples)
            {
                _adaptiveEqSamples.Dequeue();
            }

            if (_adaptiveEqSamples.Count < stableWindowSamples)
            {
                AdaptiveEqStatus = $"Learning a steady profile… {_adaptiveEqSamples.Count}/{stableWindowSamples}";
                return;
            }

            double[] stableSpectrum = GetMedianPlaybackSpectrum(_adaptiveEqSamples);
            double averageLevel = stableSpectrum.Average();
            _applyingAdaptiveEq = true;
            try
            {
                for (int band = 0; band < EqValues.Count; band++)
                {
                    double correctionDb = Math.Clamp((averageLevel - stableSpectrum[band]) * 0.08, -2, 2);
                    double baseline = _manualEqValues?[band] ?? 0;
                    double targetTenthsDb = Math.Round((baseline + correctionDb * 10) / 5) * 5;
                    EqValues[band] = Math.Clamp(targetTenthsDb, -60, 60);
                }
            }
            finally
            {
                _applyingAdaptiveEq = false;
            }

            _adaptiveEqProfileLocked = true;
            AdaptiveEqStatus = "Profile locked · pauses trigger relearning";
        }

        private static double[] GetMedianPlaybackSpectrum(IEnumerable<double[]> samples)
        {
            double[][] frames = samples.ToArray();
            int bandCount = frames[0].Length;
            var median = new double[bandCount];
            for (int band = 0; band < bandCount; band++)
            {
                double[] levels = frames.Select(frame => frame[band]).OrderBy(level => level).ToArray();
                median[band] = levels[levels.Length / 2];
            }

            return median;
        }

        private void StopAdaptiveEq(bool restoreManualEq)
        {
            _adaptiveEqTimer.Stop();
            _audioLevelService.Mode = AudioAnalysisMode.Visualizer;

            bool wasEnabled = IsAdaptiveEqEnabled;
            IsAdaptiveEqEnabled = false;
            _latestPlaybackSpectrum = null;
            _hasNewPlaybackSpectrum = false;
            _adaptiveEqSamples.Clear();
            _adaptiveEqProfileLocked = false;
            AdaptiveEqStatus = "Learns a steady profile from playback · local only";

            if (wasEnabled && restoreManualEq && _manualEqValues != null)
            {
                _applyingAdaptiveEq = true;
                try
                {
                    for (int band = 0; band < EqValues.Count; band++)
                    {
                        EqValues[band] = _manualEqValues[band];
                    }
                }
                finally
                {
                    _applyingAdaptiveEq = false;
                }
            }

            _manualEqValues = null;
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
            _modePinTimer.Tick -= ModePinTimer_Tick;
            _microphoneStateTimer.Tick -= MicrophoneStateTimer_Tick;
            _focusCycleTimer.Tick -= FocusCycleTimer_Tick;
            _batteryPollTimer.Tick -= BatteryPollTimer_Tick;
            _playbackVolumeTimer.Tick -= PlaybackVolumeTimer_Tick;
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
