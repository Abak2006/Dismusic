namespace Dismusic.App.ViewModels;

public sealed class AppRowViewModel(string exeKey, Action<AppRowViewModel> deviceChanged, Action<AppRowViewModel> followChanged)
    : ObservableObject
{
    private string _displayName = exeKey;
    private string _statusText = "";
    private string? _selectedDeviceId;
    private bool _follow;
    private string _nowOnText = "";
    private string? _errorText;
    private bool _isPlaying;

    public string ExeKey { get; } = exeKey;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value))
            {
                Raise(nameof(OutputAutomationName));
                Raise(nameof(FollowAutomationName));
            }
        }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public string NowOnText { get => _nowOnText; set => Set(ref _nowOnText, value); }
    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value); }

    /// <summary>The app's fixed output rule (null = Windows default).</summary>
    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            if (Set(ref _selectedDeviceId, value)) deviceChanged(this);
        }
    }

    public bool Follow
    {
        get => _follow;
        set
        {
            if (Set(ref _follow, value)) followChanged(this);
        }
    }

    public string OutputAutomationName => $"Output device for {DisplayName}";
    public string FollowAutomationName => $"{DisplayName} follows the screen its window is on";

    /// <summary>Updates from a refresh without triggering the change callbacks.</summary>
    public void Load(string? deviceId, bool follow)
    {
        Set(ref _selectedDeviceId, deviceId, nameof(SelectedDeviceId));
        Set(ref _follow, follow, nameof(Follow));
    }
}

public sealed class MonitorRowViewModel(string key, Action<MonitorRowViewModel> deviceChanged) : ObservableObject
{
    private string _name = "";
    private string _details = "";
    private string? _selectedDeviceId;

    public string Key { get; } = key;
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(AutomationName)); } }
    public string Details { get => _details; set => Set(ref _details, value); }
    public string AutomationName => $"Audio device for {Name}";

    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            if (Set(ref _selectedDeviceId, value)) deviceChanged(this);
        }
    }

    public void Load(string? deviceId) => Set(ref _selectedDeviceId, deviceId, nameof(SelectedDeviceId));
}

public sealed class MirrorTargetViewModel(string deviceId, Action<MirrorTargetViewModel, bool> changed) : ObservableObject
{
    private string _name = "";
    private bool _enabled;
    private int _delayMs;
    private int _volume = 100;
    private string _status = "";
    private bool _hasError;

    public string DeviceId { get; } = deviceId;
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(DelayAutomationName)); } }

    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) changed(this, true); }
    }

    public int DelayMs
    {
        get => _delayMs;
        set { if (Set(ref _delayMs, value)) changed(this, false); }
    }

    public int Volume
    {
        get => _volume;
        set { if (Set(ref _volume, value)) changed(this, false); }
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }

    /// <summary>The source's original audio already plays on this device, so no copy is sent here.</summary>
    public bool IsOriginal { get => _isOriginal; set => Set(ref _isOriginal, value); }
    private bool _isOriginal;
    public string DelayAutomationName => $"Delay for {Name} in milliseconds";
    public string VolumeAutomationName => $"Volume for {Name} in percent";

    public void Load(bool enabled, int delayMs, int volume)
    {
        Set(ref _enabled, enabled, nameof(Enabled));
        Set(ref _delayMs, delayMs, nameof(DelayMs));
        Set(ref _volume, volume, nameof(Volume));
    }
}
