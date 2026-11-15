using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Dismusic.Core.Sessions;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public sealed record AudioSessionInfo(uint ProcessId, string ExeKey, string DeviceId, bool IsActive);

/// <summary>Enumerates active render devices and the audio sessions playing on them.</summary>
public sealed class AudioSystem : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<AudioDevice> GetRenderDevices()
    {
        string? defaultId = null;
        if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }

        var result = new List<AudioDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
                result.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
        }
        return result;
    }

    public IReadOnlyList<AudioSessionInfo> GetSessions(ProcessTable processes)
    {
        var result = new List<AudioSessionInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.IsSystemSoundsSession) continue;
                        uint pid = session.GetProcessID;
                        if (pid == 0) continue;
                        var process = processes.Get(pid);
                        if (process is null) continue; // exited since the snapshot
                        result.Add(new AudioSessionInfo(pid, process.ExeKey, device.ID,
                            session.State == AudioSessionState.AudioSessionStateActive));
                    }
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    // A device can disappear mid-enumeration; skip it this round.
                    Log.Warn($"Could not read sessions for device '{device.FriendlyName}': 0x{ex.HResult:X8}");
                }
            }
        }
        return result;
    }

    public MMDevice GetDevice(string id) => _enumerator.GetDevice(id);

    /// <summary>
    /// Sets the Windows per-app volume of every session owned by <paramref name="processIds"/>, on every
    /// active device, to <paramref name="volume"/>. Returns how many sessions were found and the volume one of
    /// them had before being changed (null if none needed changing).
    /// </summary>
    public (int Sessions, float? PreviousVolume) SetSessionVolume(IReadOnlySet<uint> processIds, float volume)
    {
        int found = 0;
        float? previous = null;
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (!processIds.Contains(session.GetProcessID)) continue;
                        found++;
                        var simple = session.SimpleAudioVolume;
                        float current = simple.Volume;
                        if (Math.Abs(current - volume) > 1e-5f)
                        {
                            previous ??= current;
                            simple.Volume = volume;
                        }
                    }
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    Log.Warn($"Could not set session volume on '{device.FriendlyName}': 0x{ex.HResult:X8}");
                }
            }
        }
        return (found, previous);
    }

    /// <summary>Current per-app volume of the first session owned by <paramref name="processIds"/>, or null.</summary>
    public float? GetSessionVolume(IReadOnlySet<uint> processIds)
    {
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (processIds.Contains(session.GetProcessID)) return session.SimpleAudioVolume.Volume;
                }
            }
        }
        return null;
    }

    public void Dispose() => _enumerator.Dispose();
}
