using Dismusic.Core.Routing;
using Dismusic.Core.Settings;

namespace Dismusic.Core.Mirror;

public static class MirrorPlanner
{
    /// <summary>
    /// Devices where the source's original audio already plays. A copy sent there would be heard twice,
    /// ~60 ms apart (an echo), so those targets are skipped.
    /// <list type="bullet">
    /// <item>"Everything on &lt;device&gt;": that device.</item>
    /// <item>"Only &lt;app&gt;": every device the app is actively playing on; when it is paused, the device it
    /// would play on (its pinned output, else the Windows default).</item>
    /// <item>"All apps": unknown per app, so nothing is skipped.</item>
    /// </list>
    /// </summary>
    public static IReadOnlySet<string> OriginalDevices(string sourceKey, RoutingSnapshot snapshot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? defaultId = snapshot.Devices.FirstOrDefault(d => d.IsDefault)?.Id;

        if (sourceKey == MirrorSettings.DefaultDeviceSource)
        {
            if (defaultId is not null) result.Add(defaultId);
        }
        else if (sourceKey.StartsWith(MirrorSettings.DevicePrefix, StringComparison.Ordinal))
        {
            result.Add(sourceKey[MirrorSettings.DevicePrefix.Length..]);
        }
        else if (sourceKey.StartsWith(MirrorSettings.AppPrefix, StringComparison.Ordinal))
        {
            var exe = sourceKey[MirrorSettings.AppPrefix.Length..];
            var app = snapshot.Apps.FirstOrDefault(a => string.Equals(a.ExeKey, exe, StringComparison.OrdinalIgnoreCase));
            if (app is not null && app.PlayingDeviceIds.Count > 0)
                result.UnionWith(app.PlayingDeviceIds);
            else if ((app?.CurrentDeviceId ?? defaultId) is { } fallback)
                result.Add(fallback);
        }
        return result;
    }
}
