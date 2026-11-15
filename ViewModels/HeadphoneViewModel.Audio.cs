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
            UpdateAudioCapture();
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
    }
}
