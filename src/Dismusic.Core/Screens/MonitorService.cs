using System.Runtime.InteropServices;
using Dismusic.Core.Interop;
using Dismusic.Core.Sessions;

namespace Dismusic.Core.Screens;

/// <param name="Key">Stable identity (monitor device path) used to save the monitor → audio mapping.</param>
/// <param name="FriendlyName">EDID name, e.g. "LG ULTRAGEAR"; empty for many built-in panels.</param>
public sealed record MonitorInfo(string Key, string Name, string FriendlyName, IntPtr Handle, string GdiName,
    int Width, int Height, bool IsPrimary, bool IsInternal);

public static class MonitorService
{
    private const uint OutputTechnologyInternal = 0x80000000;
    private const uint OutputTechnologyDisplayPortEmbedded = 11;
    private const uint OutputTechnologyUdiEmbedded = 13;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var targets = QueryTargets();
        var result = new List<MonitorInfo>();
        int index = 0;
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            index++;
            var info = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(hMonitor, ref info)) return true;
            var r = info.rcMonitor;
            bool primary = (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0;
            var target = targets.FirstOrDefault(t => string.Equals(t.GdiName, info.szDevice, StringComparison.OrdinalIgnoreCase));
            bool isInternal = target is not null && target.OutputTechnology is OutputTechnologyInternal
                or OutputTechnologyDisplayPortEmbedded or OutputTechnologyUdiEmbedded;
            string friendly = target?.FriendlyName ?? "";
            string name = friendly.Length > 0 ? friendly : isInternal ? "Built-in display" : $"Display {index}";
            string key = !string.IsNullOrEmpty(target?.DevicePath) ? target.DevicePath : "gdi:" + info.szDevice;
            result.Add(new MonitorInfo(key, name, friendly, hMonitor, info.szDevice, r.Right - r.Left, r.Bottom - r.Top, primary, isInternal));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private sealed record Target(string GdiName, string FriendlyName, string DevicePath, uint OutputTechnology);

    private static List<Target> QueryTargets()
    {
        var result = new List<Target>();
        Win32.DISPLAYCONFIG_PATH_INFO[] paths;
        uint pathCount;
        int status;
        int attempts = 0;
        do
        {
            status = Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, out pathCount, out uint modeCount);
            if (status != Win32.ERROR_SUCCESS) return result;
            paths = new Win32.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Win32.DISPLAYCONFIG_MODE_INFO[modeCount];
            status = Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        } while (status == Win32.ERROR_INSUFFICIENT_BUFFER && ++attempts < 5); // topology changed between calls

        if (status != Win32.ERROR_SUCCESS)
        {
            Log.Warn($"QueryDisplayConfig failed ({status}); monitor names unavailable.");
            return result;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            var source = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                },
            };
            var target = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };
            if (Win32.DisplayConfigGetDeviceInfo(ref source) != Win32.ERROR_SUCCESS) continue;
            if (Win32.DisplayConfigGetDeviceInfo(ref target) != Win32.ERROR_SUCCESS) continue;
            result.Add(new Target(source.viewGdiDeviceName, target.monitorFriendlyDeviceName ?? "",
                target.monitorDevicePath ?? "", path.targetInfo.outputTechnology));
        }
        return result;
    }
}

public static class DeviceMatcher
{
    /// <summary>
    /// Best-effort guess of the audio endpoint belonging to a monitor. HDMI/DisplayPort audio endpoints
    /// are usually named after the monitor's EDID name; a built-in panel maps to the built-in speakers.
    /// Returns null when there is no confident match; the user can always set it manually.
    /// </summary>
    public static string? GuessDeviceForMonitor(MonitorInfo monitor, IReadOnlyList<AudioDevice> devices)
    {
        if (!string.IsNullOrWhiteSpace(monitor.FriendlyName))
        {
            var byName = devices.FirstOrDefault(d => d.Name.Contains(monitor.FriendlyName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName.Id;
        }
        if (monitor.IsInternal)
        {
            var speakers = devices.Where(d => d.Name.Contains("speaker", StringComparison.OrdinalIgnoreCase)).ToList();
            if (speakers.Count == 1) return speakers[0].Id;
        }
        return null;
    }
}
