namespace Dismusic.App.ViewModels;

/// <summary>An output device's Windows volume, mute and default state, shared by the Multi-output tab and the tray flyout.</summary>
public sealed class DeviceViewModel : ObservableObject
{
    private const string SpeakerGlyph = "";
    private const string MutedGlyph = "";

    private readonly Action<DeviceViewModel> _volumeChanged;
    private readonly Action<DeviceViewModel> _muteChanged;
    private string _name = "";
    private bool _isDefault;
    private int _volume;
    private bool _isMuted;

    public DeviceViewModel(string id, Action<DeviceViewModel> volumeChanged, Action<DeviceViewModel> muteChanged)
    {
        Id = id;
        _volumeChanged = volumeChanged;
        _muteChanged = muteChanged;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        private set
        {
            if (!Set(ref _name, value)) return;
            Raise(nameof(VolumeAutomationName));
            Raise(nameof(MuteAutomationName));
        }
    }

    public bool IsDefault { get => _isDefault; private set => Set(ref _isDefault, value); }

    /// <summary>Windows master volume, 0–100.</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 100))) return;
            Raise(nameof(Glyph));
            _volumeChanged(this);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!Set(ref _isMuted, value)) return;
            Raise(nameof(Glyph));
            Raise(nameof(MuteAutomationName));
            _muteChanged(this);
        }
    }

    public string Glyph => _isMuted || _volume == 0 ? MutedGlyph : SpeakerGlyph;
    public string VolumeAutomationName => $"{Name} volume";
    public string MuteAutomationName => _isMuted ? $"Unmute {Name}" : $"Mute {Name}";

    /// <summary>Updates from Windows without writing back (no callbacks).</summary>
    public void Load(string name, bool isDefault, float volume, bool muted)
    {
        Name = name;
        IsDefault = isDefault;
        if (Set(ref _volume, (int)Math.Round(volume * 100), nameof(Volume))) Raise(nameof(Glyph));
        if (Set(ref _isMuted, muted, nameof(IsMuted)))
        {
            Raise(nameof(Glyph));
            Raise(nameof(MuteAutomationName));
        }
    }
}
