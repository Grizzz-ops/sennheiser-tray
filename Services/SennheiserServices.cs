using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace SennheiserTray
{
    public class SennheiserService : IDisposable
    {
        private static readonly Guid SennheiserUuid = new("A2129FF3-081B-4C45-8AFE-469D9C4842EC");
        private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
        private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
        private readonly ConcurrentDictionary<ushort, SemaphoreSlim> _requestSemaphores = new();
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<GaiaResult>> _pendingRequests = new();
        private StreamSocket? _socket;
        private DataWriter? _writer;
        private DataReader? _reader;
        private Task? _receiveTask;
        private volatile bool _isConnected;

        public event Action<bool>? ConnectionChanged;
        public event Action<ushort, byte[]>? NotificationReceived;

        public bool IsConnected => _isConnected;
        public string ConnectedDeviceName { get; private set; } = "Disconnected";
        public string? ConnectedDeviceId { get; private set; }
        public string LastConnectionError { get; private set; } = "";

        public async Task<IReadOnlyList<SennheiserDevice>> GetAvailableDevicesAsync()
        {
            string selector = RfcommDeviceService.GetDeviceSelector(
                RfcommServiceId.FromUuid(SennheiserUuid));
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
            return devices
                .Select(device => new SennheiserDevice(device.Id, device.Name))
                .ToArray();
        }

        public async Task<bool> ConnectAsync(string deviceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            await _connectionSemaphore.WaitAsync();
            try
            {
                if (IsConnected && string.Equals(ConnectedDeviceId, deviceId, StringComparison.Ordinal))
                {
                    return true;
                }

                CloseTransport(notify: false);
                LastConnectionError = "";
                IReadOnlyList<SennheiserDevice> devices = await GetAvailableDevicesAsync();
                SennheiserDevice? target = devices.FirstOrDefault(
                    device => string.Equals(device.Id, deviceId, StringComparison.Ordinal));
                if (target == null)
                {
                    LastConnectionError = "The selected headset is no longer available.";
                    return false;
                }

                RfcommDeviceService? rfcommService = await RfcommDeviceService.FromIdAsync(target.Id);
                if (rfcommService == null)
                {
                    LastConnectionError = "Windows could not open the headset control channel.";
                    return false;
                }

                var socket = new StreamSocket();
                try
                {
                    await socket.ConnectAsync(
                        rfcommService.ConnectionHostName,
                        rfcommService.ConnectionServiceName);
                    var writer = new DataWriter(socket.OutputStream);
                    var reader = new DataReader(socket.InputStream)
                    {
                        InputStreamOptions = InputStreamOptions.None
                    };

                    _socket = socket;
                    _writer = writer;
                    _reader = reader;
                    ConnectedDeviceName = target.Name;
                    ConnectedDeviceId = target.Id;
                    SetConnected(true);
                    _receiveTask = ReadResponsesAsync(reader);
                    return true;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                finally
                {
                    rfcommService.Dispose();
                }
            }
            catch (Exception ex)
            {
                LastConnectionError = ex.Message;
                AppLogger.LogException("Bluetooth connection failed", ex);
                ConnectedDeviceName = "Disconnected";
                ConnectedDeviceId = null;
                SetConnected(false);
                return false;
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public async Task<GaiaResult> SendPacketAsync(ushort commandId, byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), "GAIA payloads cannot exceed 65535 bytes.");
            }

            byte[] packet = GaiaPacketCodec.BuildPacket(GaiaCommands.VendorId, commandId, payload);
            string hexPreview = BitConverter.ToString(packet).Replace("-", " ");
            await _writeSemaphore.WaitAsync();
            try
            {
                if (!IsConnected || _writer == null)
                {
                    return new GaiaResult(false, $"NOT SENT (GAIA disconnected): {hexPreview}", null);
                }

                _writer.WriteBytes(packet);
                await _writer.StoreAsync();
                return new GaiaResult(true, hexPreview, null);
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Bluetooth packet write failed", ex);
                MarkConnectionDropped(ex);
                return new GaiaResult(false, $"SEND FAILED: {ex.Message}", null);
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public async Task<GaiaResult> RequestAsync(
            ushort commandId,
            byte[] payload,
            ushort expectedReplyId,
            TimeSpan timeout)
        {
            if (expectedReplyId == 0)
            {
                return new GaiaResult(false, "REQUEST DISABLED: reply command is unverified.", null);
            }

            SemaphoreSlim requestSemaphore = _requestSemaphores.GetOrAdd(
                expectedReplyId,
                static _ => new SemaphoreSlim(1, 1));
            await requestSemaphore.WaitAsync();
            try
            {
                var completion = new TaskCompletionSource<GaiaResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pendingRequests.TryAdd(expectedReplyId, completion))
                {
                    throw new InvalidOperationException(
                        $"A request for reply 0x{expectedReplyId:X4} is already registered.");
                }

                try
                {
                    GaiaResult sent = await SendPacketAsync(commandId, payload);
                    if (!sent.Success)
                    {
                        return sent;
                    }

                    try
                    {
                        GaiaResult reply = await completion.Task.WaitAsync(timeout);
                        return reply with { Log = $"REQUEST 0x{commandId:X4} -> {sent.Log} | {reply.Log}" };
                    }
                    catch (TimeoutException)
                    {
                        return new GaiaResult(
                            false,
                            $"REQUEST TIMEOUT: sent 0x{commandId:X4}, expected reply 0x{expectedReplyId:X4}.",
                            null);
                    }
                }
                finally
                {
                    _pendingRequests.TryRemove(expectedReplyId, out _);
                }
            }
            finally
            {
                requestSemaphore.Release();
            }
        }

        public async Task<GaiaResult> SetAncAsync(bool enabled)
        {
            GaiaResult transparency = await SendPacketAsync(
                GaiaCommands.TransparencySet, new byte[] { 0 });
            if (!transparency.Success)
            {
                return transparency;
            }

            GaiaResult anc = await SendPacketAsync(
                GaiaCommands.AncEnableSet, new byte[] { enabled ? (byte)1 : (byte)0 });
            return Combine("ANC", transparency, anc);
        }

        public async Task<GaiaResult> SetAncModeAsync(bool adaptive, int customLevel)
        {
            GaiaResult anc = await SendPacketAsync(GaiaCommands.AncEnableSet, new byte[] { 1 });
            if (!anc.Success)
            {
                return anc;
            }

            byte modeState = adaptive ? (byte)1 : (byte)0;
            GaiaResult mode = await SendPacketAsync(
                GaiaCommands.AncModeSet, new byte[] { 3, modeState });
            if (!mode.Success || adaptive)
            {
                return Combine("ANC MODE", anc, mode);
            }

            GaiaResult level = await SetAncCustomLevelAsync(customLevel);
            return Combine("ANC CUSTOM", anc, mode, level);
        }

        public Task<GaiaResult> SetAncCustomLevelAsync(int level)
        {
            byte clampedLevel = (byte)Math.Clamp(level, 0, 100);
            return SendPacketAsync(GaiaCommands.AncLevelSet, new[] { clampedLevel });
        }

        public async Task<GaiaResult> SetEqAsync(
            sbyte bass, sbyte lowMid, sbyte mid, sbyte highMid, sbyte treble)
        {
            sbyte[] gains = { bass, lowMid, mid, highMid, treble };
            var results = new List<GaiaResult>(gains.Length);
            for (byte band = 0; band < gains.Length; band++)
            {
                GaiaResult result = await SendPacketAsync(
                    GaiaCommands.EqBandSet,
                    new byte[] { band, unchecked((byte)gains[band]) });
                results.Add(result);
                if (!result.Success)
                {
                    break;
                }
            }

            return Combine("EQ", results.ToArray());
        }

        public async Task<GaiaResult> SetBassBoostAsync(bool enabled)
        {
            GaiaResult setResult = await SendPacketAsync(
                GaiaCommands.BassBoostSet, new byte[] { enabled ? (byte)1 : (byte)0 });
            if (!setResult.Success)
            {
                return setResult;
            }

            GaiaResult readback = await RequestAsync(
                GaiaCommands.BassBoostGet,
                Array.Empty<byte>(),
                GaiaCommands.BassBoostReply,
                TimeSpan.FromSeconds(2));
            if (!readback.Success)
            {
                return new GaiaResult(
                    false,
                    $"SET SENT; READBACK NOT CONFIRMED: {setResult.Log} | {readback.Log}",
                    readback.Payload);
            }

            if (readback.Payload == null || readback.Payload.Length == 0)
            {
                return new GaiaResult(false, "HEADSET RETURNED EMPTY BASS BOOST STATE.", readback.Payload);
            }

            bool actualState = readback.Payload[0] != 0;
            string status = actualState == enabled
                ? $"HEADSET CONFIRMED BASS BOOST {(actualState ? "ON" : "OFF")}"
                : $"HEADSET REPORTS BASS BOOST {(actualState ? "ON" : "OFF")}; REQUESTED {(enabled ? "ON" : "OFF")}";
            return new GaiaResult(actualState == enabled, $"{status}: {setResult.Log} | {readback.Log}", readback.Payload);
        }

        private static GaiaResult Combine(string label, params GaiaResult[] results)
        {
            return new GaiaResult(
                results.All(result => result.Success),
                $"{label} -> {string.Join(" | ", results.Select(result => result.Log))}",
                results.LastOrDefault(result => result.Payload != null)?.Payload);
        }

        private async Task ReadResponsesAsync(DataReader reader)
        {
            Exception? failure = null;
            try
            {
                while (IsConnected)
                {
                    var headerBytes = new byte[GaiaPacketCodec.HeaderLength];
                    if (!await ReadExactlyAsync(reader, headerBytes))
                    {
                        break;
                    }

                    GaiaPacketHeader header = GaiaPacketCodec.ParseHeader(headerBytes);
                    var payload = new byte[header.PayloadLength];
                    if (payload.Length > 0 && !await ReadExactlyAsync(reader, payload))
                    {
                        break;
                    }

                    if (header.VendorId != GaiaCommands.VendorId)
                    {
                        RaiseNotification(header.CommandId, payload);
                        continue;
                    }

                    if (_pendingRequests.TryRemove(
                        header.CommandId,
                        out TaskCompletionSource<GaiaResult>? pending))
                    {
                        pending.TrySetResult(new GaiaResult(
                            true,
                            $"REPLY 0x{header.CommandId:X4}: {BitConverter.ToString(payload).Replace("-", " ")}",
                            payload));
                    }
                    else
                    {
                        RaiseNotification(header.CommandId, payload);
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                AppLogger.LogException("Bluetooth response reader stopped", ex);
            }
            finally
            {
                if (IsConnected)
                {
                    MarkConnectionDropped(failure);
                }
            }
        }

        private void RaiseNotification(ushort commandId, byte[] payload)
        {
            Action<ushort, byte[]>? handlers = NotificationReceived;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<ushort, byte[]> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(commandId, (byte[])payload.Clone());
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("GAIA notification handler failed", ex);
                }
            }
        }

        private static async Task<bool> ReadExactlyAsync(DataReader reader, byte[] buffer)
        {
            uint offset = 0;
            while (offset < buffer.Length)
            {
                uint loaded = await reader.LoadAsync((uint)(buffer.Length - offset));
                if (loaded == 0)
                {
                    return false;
                }

                uint bytesToRead = Math.Min(loaded, (uint)(buffer.Length - offset));
                var chunk = new byte[(int)bytesToRead];
                reader.ReadBytes(chunk);
                Array.Copy(chunk, 0, buffer, offset, bytesToRead);
                offset += bytesToRead;
            }

            return true;
        }

        private void MarkConnectionDropped(Exception? exception)
        {
            if (exception != null)
            {
                Debug.WriteLine($"Bluetooth connection dropped: {exception.Message}");
            }

            CloseTransport(notify: true);
        }

        private void SetConnected(bool connected)
        {
            if (IsConnected == connected)
            {
                return;
            }

            _isConnected = connected;
            RaiseConnectionChanged(connected);
        }

        private void RaiseConnectionChanged(bool connected)
        {
            Action<bool>? handlers = ConnectionChanged;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<bool> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(connected);
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Connection state handler failed", ex);
                }
            }
        }

        private void CloseTransport(bool notify)
        {
            bool wasConnected = IsConnected;
            _isConnected = false;
            foreach (TaskCompletionSource<GaiaResult> pending in _pendingRequests.Values)
            {
                pending.TrySetResult(new GaiaResult(false, "REQUEST FAILED: GAIA connection closed.", null));
            }

            _reader?.Dispose();
            _writer?.Dispose();
            _socket?.Dispose();
            _reader = null;
            _writer = null;
            _socket = null;
            if (notify || wasConnected)
            {
                RaiseConnectionChanged(false);
            }
        }

        public void Disconnect()
        {
            CloseTransport(notify: true);
            ConnectedDeviceName = "Disconnected";
            ConnectedDeviceId = null;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
