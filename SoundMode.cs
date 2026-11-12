namespace SennheiserTray
{
    public sealed record SoundMode(
        string Id,
        string Name,
        string Icon,
        string Accent,
        int AncMode,
        int AncLevel,
        int[] EqBands,
        bool BassBoost)
    {
        public SoundMode Copy() => this with { EqBands = (int[])EqBands.Clone() };
    }

    public sealed class ModeChipItem : ObservableObject
    {
        private SoundMode _mode;
        private bool _isSelected;

        public ModeChipItem(SoundMode mode)
        {
            _mode = mode.Copy();
        }

        public SoundMode Mode
        {
            get => _mode.Copy();
            private set
            {
                _mode = value.Copy();
                OnPropertyChanged();
                OnPropertyChanged(nameof(Id));
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(Accent));
            }
        }

        public string Id => _mode.Id;
        public string Name => _mode.Name;
        public string Icon => _mode.Icon;
        public string Accent => _mode.Accent;

        public bool IsSelected
        {
            get => _isSelected;
            internal set => SetProperty(ref _isSelected, value);
        }

        internal void UpdateMode(SoundMode mode) => Mode = mode;
    }
}
