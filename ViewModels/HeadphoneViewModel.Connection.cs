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
        private async Task InitializeAsync()
        {
            try
            {
                await LoadAvailableDevicesAsync();
                if (IsAutoConnectEnabled
                    && !string.IsNullOrWhiteSpace(SelectedDeviceId)
                    && !await TryConnectAsync(isAutomatic: true))
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
            finally
            {
                if (IsAutoConnectEnabled)
                {
                    _autoConnectTimer.Start();
                }
            }
        }

        private async Task<bool> LoadAvailableDevicesAsync()
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
                OnPropertyChanged(nameof(HeaderText));

                if (devices.Count == 0)
                {
                    StatusText = "No paired Sennheiser control device";
                }
                else if (devices.Count > 1 && SelectedDeviceId == null)
                {
                    StatusText = "Choose a headset to connect";
                }

                return devices.Count > 0;
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not enumerate Sennheiser devices", ex);
                StatusText = "Device search failed";
                LastCommand = $"DEVICE SEARCH ERROR -> {ex.Message}";
                return false;
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

        private async Task<bool> TryConnectAsync(bool isAutomatic = false)
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

                if (isAutomatic)
                {
                    string connectedDeviceName = _service.ConnectedDeviceName;
                    _ = _dispatcher.BeginInvoke(new Action(
                        () => AutomaticConnectionSucceeded?.Invoke(connectedDeviceName)));
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
                    StatusText = IsAutoConnectEnabled ? "Reconnecting..." : "Disconnected";
                    if (IsAutoConnectEnabled)
                    {
                        StartAutoReconnect();
                    }
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
                || !IsAutoConnectEnabled
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
                while (!cancellationToken.IsCancellationRequested
                    && IsAutoConnectEnabled
                    && !_service.IsConnected)
                {
                    StatusText = "Reconnecting...";
                    await Task.Delay(ReconnectBackoff.DelayForAttempt(attempt), cancellationToken);
                    if (cancellationToken.IsCancellationRequested
                        || !IsAutoConnectEnabled
                        || _service.IsConnected)
                    {
                        return;
                    }

                    await TryConnectAsync(isAutomatic: true);
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

        private async void AutoConnectTimer_Tick(object? sender, EventArgs e)
        {
            _autoConnectTimer.Stop();
            try
            {
                await ScanAndConnectAsync();
            }
            finally
            {
                if (!_isDisposed && IsAutoConnectEnabled)
                {
                    _autoConnectTimer.Start();
                }
            }
        }

        private async Task ScanAndConnectAsync()
        {
            if (_isDisposed
                || !IsAutoConnectEnabled
                || _service.IsConnected
                || _isAutoConnectScanInProgress
                || (_reconnectTask != null && !_reconnectTask.IsCompleted))
            {
                return;
            }

            _isAutoConnectScanInProgress = true;
            try
            {
                bool compatibleDeviceFound = await LoadAvailableDevicesAsync();
                _autoConnectPollingPolicy.RecordScan(compatibleDeviceFound);
                _autoConnectTimer.Interval = _autoConnectPollingPolicy.Interval;
                if (compatibleDeviceFound
                    && !string.IsNullOrWhiteSpace(SelectedDeviceId)
                    && !await TryConnectAsync(isAutomatic: true))
                {
                    StartAutoReconnect();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Automatic headset detection failed", ex);
                LastCommand = $"AUTO-CONNECT ERROR -> {ex.Message}";
            }
            finally
            {
                _isAutoConnectScanInProgress = false;
            }
        }
    }
}
