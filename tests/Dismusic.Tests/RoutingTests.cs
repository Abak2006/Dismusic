using Dismusic.Core.Routing;
using Dismusic.Core.Screens;
using Dismusic.Core.Sessions;

namespace Dismusic.Tests;

public class RoutingPlannerTests
{
    private const string Speakers = "{0.0.0.00000000}.{speakers}";
    private const string Hdmi = "{0.0.0.00000000}.{hdmi}";
    private static readonly IReadOnlySet<string> Both = new HashSet<string> { Speakers, Hdmi };
    private static readonly IReadOnlySet<string> OnlySpeakers = new HashSet<string> { Speakers };

    [Fact]
    public void NoRuleNoFollowLeavesAppAlone() =>
        Assert.Equal(RouteDecision.Unmanaged, RoutingPlanner.Decide(null, false, default, Both));

    [Fact]
    public void FixedRuleApplies() =>
        Assert.Equal(new RouteDecision(true, Hdmi, RouteReason.Rule), RoutingPlanner.Decide(Hdmi, false, default, Both));

    [Fact]
    public void RuleForUnpluggedDeviceIsNotApplied() =>
        Assert.False(RoutingPlanner.Decide(Hdmi, false, default, OnlySpeakers).Manage);

    [Fact]
    public void ScreenBeatsRule() =>
        Assert.Equal(new RouteDecision(true, Hdmi, RouteReason.Screen),
            RoutingPlanner.Decide(Speakers, true, new ScreenState(true, Hdmi), Both));

    [Fact]
    public void MinimizedFollowingAppIsLeftWhereItIs() =>
        Assert.False(RoutingPlanner.Decide(Speakers, true, new ScreenState(false, null), Both).Manage);

    [Fact]
    public void UnmappedScreenFallsBackToRule() =>
        Assert.Equal(new RouteDecision(true, Speakers, RouteReason.Rule),
            RoutingPlanner.Decide(Speakers, true, new ScreenState(true, null), Both));

    [Fact]
    public void UnmappedScreenWithoutRuleGoesToWindowsDefault() =>
        Assert.Equal(new RouteDecision(true, null, RouteReason.ScreenFallbackToDefault),
            RoutingPlanner.Decide(null, true, new ScreenState(true, null), Both));

    [Fact]
    public void ScreenDeviceUnpluggedFallsBack() =>
        Assert.Equal(new RouteDecision(true, null, RouteReason.ScreenFallbackToDefault),
            RoutingPlanner.Decide(null, true, new ScreenState(true, Hdmi), OnlySpeakers));

    [Fact]
    public void DeviceIdsCompareCaseInsensitively() =>
        Assert.True(RoutingPlanner.SameDevice("{0.0.0.00000000}.{ABC}", "{0.0.0.00000000}.{abc}"));
}

public class AudioPolicyConfigPathTests
{
    private const string Id = "{0.0.0.00000000}.{8640d54d-46d5-4630-aa34-4b2c47034741}";

    [Fact]
    public void RoundTripsDeviceIdThroughInterfacePath()
    {
        var path = AudioPolicyConfig.ToInterfacePath(Id);
        Assert.Equal(@"\\?\SWD#MMDEVAPI#" + Id + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}", path);
        Assert.Equal(Id, AudioPolicyConfig.FromInterfacePath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\?\SWD#MMDEVAPI#")]
    public void EmptyPathMeansDefault(string? path) => Assert.Null(AudioPolicyConfig.FromInterfacePath(path));
}

public class ProcessTableTests
{
    [Fact]
    public void ResolvesToTopmostAncestorWithSameExe()
    {
        // explorer(1) → chrome(10) → chrome(11, audio service)
        var table = new ProcessTable([new(1, 0, "explorer.exe"), new(10, 1, "chrome.exe"), new(11, 10, "chrome.exe")]);
        Assert.Equal(10u, table.ResolveRootOfSameExe(11));
        Assert.Equal(10u, table.ResolveRootOfSameExe(10));
    }

    [Fact]
    public void StopsAtMissingParentAndSurvivesCycles()
    {
        var table = new ProcessTable([new(5, 99, "a.exe"), new(6, 7, "b.exe"), new(7, 6, "b.exe")]);
        Assert.Equal(5u, table.ResolveRootOfSameExe(5));
        var root = table.ResolveRootOfSameExe(6); // pid-reuse cycle must terminate
        Assert.Contains(root, new uint[] { 6, 7 });
        Assert.Equal(42u, table.ResolveRootOfSameExe(42)); // unknown pid
    }

    [Fact]
    public void GroupsPidsByExeCaseInsensitively()
    {
        var table = new ProcessTable([new(1, 0, "spotify.exe"), new(2, 1, "spotify.exe")]);
        Assert.Equal(2, table.PidsForExe("Spotify.exe").Count);
        Assert.False(table.IsRunning("vlc.exe"));
    }
}

public class DeviceMatcherTests
{
    private static MonitorInfo Monitor(string friendly, bool isInternal) =>
        new("key", friendly, friendly, IntPtr.Zero, @"\\.\DISPLAY1", 1920, 1080, false, isInternal);

    private static readonly AudioDevice[] Devices =
    [
        new("speakers", "Speakers (Realtek(R) Audio)", true),
        new("hdmi", "LG ULTRAGEAR (NVIDIA High Definition Audio)", false),
    ];

    [Fact]
    public void MatchesHdmiEndpointByMonitorName() =>
        Assert.Equal("hdmi", DeviceMatcher.GuessDeviceForMonitor(Monitor("LG ULTRAGEAR", false), Devices));

    [Fact]
    public void MatchesBuiltInPanelToTheOnlySpeakers() =>
        Assert.Equal("speakers", DeviceMatcher.GuessDeviceForMonitor(Monitor("", true), Devices));

    [Fact]
    public void NoGuessWhenAmbiguous()
    {
        AudioDevice[] twoSpeakers = [new("a", "Speakers (Realtek)", true), new("b", "FxSound Speakers", false)];
        Assert.Null(DeviceMatcher.GuessDeviceForMonitor(Monitor("", true), twoSpeakers));
        Assert.Null(DeviceMatcher.GuessDeviceForMonitor(Monitor("DELL U2720Q", false), Devices));
    }
}
