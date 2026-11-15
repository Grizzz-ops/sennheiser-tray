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
    }
}
