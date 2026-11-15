namespace Dismusic.Core.Routing;

public enum RouteReason
{
    /// <summary>Dismusic leaves the app alone (Windows' own setting applies).</summary>
    Unmanaged,
    /// <summary>The app's fixed output rule.</summary>
    Rule,
    /// <summary>The device mapped to the monitor the app's window is on.</summary>
    Screen,
    /// <summary>Screen-follow is on but the window's monitor is unmapped and there is no rule: use the Windows default.</summary>
    ScreenFallbackToDefault,
}

/// <param name="DeviceId">Target MMDevice id, or null for the Windows default. Ignored when <see cref="Manage"/> is false.</param>
public readonly record struct RouteDecision(bool Manage, string? DeviceId, RouteReason Reason)
{
    public static readonly RouteDecision Unmanaged = new(false, null, RouteReason.Unmanaged);
}

/// <summary>Where the app's window currently is, as far as screen-follow is concerned.</summary>
/// <param name="WindowFound">Whether a visible, non-minimized window of the app was found.</param>
/// <param name="ScreenDeviceId">Device mapped to that window's monitor, or null if unmapped.</param>
public readonly record struct ScreenState(bool WindowFound, string? ScreenDeviceId);

public static class RoutingPlanner
{
    /// <summary>
    /// Decides where an app's audio should go. Precedence: screen-follow (when on and the window is on a
    /// mapped monitor) → fixed rule → leave alone. Devices that are not currently present are never targeted,
    /// so an unplugged device doesn't trap audio.
    /// </summary>
    public static RouteDecision Decide(string? ruleDeviceId, bool followScreen, ScreenState screen,
        IReadOnlySet<string> presentDeviceIds)
    {
        bool rulePresent = ruleDeviceId is not null && presentDeviceIds.Contains(ruleDeviceId);

        if (followScreen)
        {
            if (!screen.WindowFound)
                return RouteDecision.Unmanaged; // minimized/hidden: keep whatever it has now
            if (screen.ScreenDeviceId is not null && presentDeviceIds.Contains(screen.ScreenDeviceId))
                return new RouteDecision(true, screen.ScreenDeviceId, RouteReason.Screen);
            return rulePresent
                ? new RouteDecision(true, ruleDeviceId, RouteReason.Rule)
                : new RouteDecision(true, null, RouteReason.ScreenFallbackToDefault);
        }

        return rulePresent ? new RouteDecision(true, ruleDeviceId, RouteReason.Rule) : RouteDecision.Unmanaged;
    }

    public static bool SameDevice(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
