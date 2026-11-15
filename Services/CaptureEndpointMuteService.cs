using NAudio.CoreAudioApi;

namespace SennheiserTray
{
    public sealed class CaptureEndpointMuteService : IDisposable
    {
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private bool _disposed;

        public bool IsMuted
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using MMDevice device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                using AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;
                return endpointVolume.Mute;
            }
        }

        public bool ToggleMute()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using MMDevice device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            using AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;
            endpointVolume.Mute = !endpointVolume.Mute;
            return endpointVolume.Mute;
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
    }
}
