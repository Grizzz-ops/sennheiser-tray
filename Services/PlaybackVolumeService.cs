using NAudio.CoreAudioApi;

namespace SennheiserTray
{
    public sealed class PlaybackVolumeService : IDisposable
    {
        private readonly MMDeviceEnumerator _deviceEnumerator = new();
        private bool _disposed;

        public int GetDefaultPlaybackVolume()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using MMDevice device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;
            return (int)Math.Round(endpointVolume.MasterVolumeLevelScalar * 100);
        }

        public void SetDefaultPlaybackVolume(int volume)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using MMDevice device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;
            endpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 100) / 100f;
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
