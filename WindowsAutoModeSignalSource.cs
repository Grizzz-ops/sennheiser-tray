using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SennheiserTray
{
    public sealed class WindowsAutoModeSignalSource : IAutoModeSignalSource, IDisposable
    {
        private readonly object _contentGate = new();
        private readonly Queue<(DateTimeOffset At, AudioContentFeatures Features)> _contentSamples = new();
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private HashSet<string> _gameProcessNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public void SetGameProcessNames(IEnumerable<string> processNames)
        {
            ArgumentNullException.ThrowIfNull(processNames);
            _gameProcessNames = processNames
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public void AddContentFeatures(AudioContentFeatures features, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(features);
            lock (_contentGate)
            {
                _contentSamples.Enqueue((now, features));
                while (_contentSamples.Count > 0
                    && now - _contentSamples.Peek().At > TimeSpan.FromSeconds(10))
                {
                    _contentSamples.Dequeue();
                }
            }
        }

        public Task<AutoModeSignals> ReadAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            (bool isCallActive, string? callProcessName) = FindActiveMicrophoneSession();
            (bool isFullscreen, string? foregroundProcessName) = GetForegroundProcess();
            bool isGameActive = foregroundProcessName is not null
                && _gameProcessNames.Contains(foregroundProcessName);

            return Task.FromResult(new AutoModeSignals(
                isCallActive,
                callProcessName,
                isGameActive,
                isGameActive ? foregroundProcessName : null,
                isFullscreen,
                GetRecentContentFeatures(DateTimeOffset.UtcNow)));
        }

        private (bool IsActive, string? ProcessName) FindActiveMicrophoneSession()
        {
            int currentProcessId = Environment.ProcessId;
            MMDeviceCollection devices =
                _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (MMDevice device in devices)
            {
                using (device)
                {
                    SessionCollection sessions = device.AudioSessionManager.Sessions;
                    for (int index = 0; index < sessions.Count; index++)
                    {
                        AudioSessionControl session = sessions[index];
                        using (session)
                        {
                            if (session.State != AudioSessionState.AudioSessionStateActive
                                || session.GetProcessID == 0
                                || session.GetProcessID == currentProcessId)
                            {
                                continue;
                            }

                            try
                            {
                                using Process process = Process.GetProcessById((int)session.GetProcessID);
                                return (true, process.ProcessName);
                            }
                            catch (ArgumentException)
                            {
                            }
                        }
                    }
                }
            }

            return (false, null);
        }

        private static (bool IsFullscreen, string? ProcessName) GetForegroundProcess()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return (false, null);
            }

            _ = GetWindowThreadProcessId(window, out uint processId);
            string? processName = null;
            if (processId != 0 && processId != Environment.ProcessId)
            {
                try
                {
                    using Process process = Process.GetProcessById((int)processId);
                    processName = process.ProcessName;
                }
                catch (ArgumentException)
                {
                }
            }

            if (!GetWindowRect(window, out NativeRect windowRect))
            {
                return (false, processName);
            }

            IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return (false, processName);
            }

            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return (false, processName);
            }

            NativeRect bounds = monitorInfo.Monitor;
            bool coversMonitor =
                windowRect.Left <= bounds.Left
                && windowRect.Top <= bounds.Top
                && windowRect.Right >= bounds.Right
                && windowRect.Bottom >= bounds.Bottom;
            return (coversMonitor, processName);
        }

        private AudioContentFeatures? GetRecentContentFeatures(DateTimeOffset now)
        {
            lock (_contentGate)
            {
                while (_contentSamples.Count > 0
                    && now - _contentSamples.Peek().At > TimeSpan.FromSeconds(10))
                {
                    _contentSamples.Dequeue();
                }

                if (_contentSamples.Count == 0)
                {
                    return null;
                }

                return new AudioContentFeatures(
                    _contentSamples.Average(sample => sample.Features.SpectralCentroidHz),
                    _contentSamples.Average(sample => sample.Features.LowFrequencyEnergyRatio),
                    _contentSamples.Average(sample => sample.Features.CrestFactor));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _deviceEnumerator.Dispose();
        }

        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
