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
    public sealed partial class HeadphoneViewModel
    {
        private void ToggleTheme()
        {
            bool useDarkTheme = !IsDarkTheme;
            try
            {
                _settingsStore.SetDarkTheme(useDarkTheme);
                ThemeManager.Apply(useDarkTheme);
                _isDarkTheme = useDarkTheme;
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(ThemeToggleText));
                LastCommand = useDarkTheme ? "Appearance -> Night theme enabled." : "Appearance -> Day theme enabled.";
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not change the application theme", ex);
                LastCommand = $"THEME CHANGE ERROR -> {ex.Message}";
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
                    ? ThemeBrush("StatusSuccessBrush")
                    : AdaptiveEqStatus.StartsWith("Could not start", StringComparison.Ordinal)
                        || AdaptiveEqStatus.StartsWith("Connect the headset", StringComparison.Ordinal)
                            ? ThemeBrush("StatusWarningBrush")
                            : ThemeBrush("TextMutedBrush");

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
        public IReadOnlyList<string> MuteHotkeyChoices { get; } = new[] { "M", "U", "F9" };
        public string MicrophoneMuteHotkeyStatus => _isGlobalMuteHotkeyAvailable
            ? $"Global shortcut: Ctrl+Shift+{MuteHotkeyKey}"
            : $"Ctrl+Shift+{MuteHotkeyKey} is unavailable; another app may be using it.";
        public bool IsGlobalMuteHotkeyAvailable => _isGlobalMuteHotkeyAvailable;
        public string MuteHotkeyKey
        {
            get => _muteHotkeyKey;
            set
            {
                if (_muteHotkeyKey == value)
                {
                    return;
                }

                try
                {
                    _settingsStore.SetMuteHotkeyKey(value);
                    if (SetProperty(ref _muteHotkeyKey, value))
                    {
                        OnPropertyChanged(nameof(MicrophoneMuteHotkeyStatus));
                        MuteHotkeyChanged?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Could not save global microphone shortcut", ex);
                    OnPropertyChanged(nameof(MuteHotkeyKey));
                    LastCommand = $"MUTE SHORTCUT SETTING ERROR -> {ex.Message}";
                }
            }
        }
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
    }
}
