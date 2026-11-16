using Dismusic.Core.Mirror;
using Dismusic.Core.Routing;
using Dismusic.Core.Sessions;
using Dismusic.Core.Settings;

namespace Dismusic.Tests;

public class MirrorPlannerTests
{
    private const string Speakers = "spk";
    private const string Headphones = "hp";
    private const string Hdmi = "hdmi";

    private static RoutingSnapshot Snapshot(params AppState[] apps) => new(
        [new AudioDevice(Speakers, "Speakers", IsDefault: true), new AudioDevice(Headphones, "Headphones", false), new AudioDevice(Hdmi, "TV", false)],
        apps, [], new ProcessTable([]));

    private static AppState App(string exe, string? pinned, params string[] playingOn) => new(
        exe, exe, true, true, playingOn.Length > 0, [1], pinned, RouteReason.Unmanaged, null, null,
        playingOn.FirstOrDefault(), playingOn);

    [Fact]
    public void AppSourceSkipsTheDeviceTheAppPlaysOn()
    {
        // The reported bug: Brave pinned to headphones, copies to speakers + headphones → echo on headphones.
        var snap = Snapshot(App("brave.exe", Headphones, Headphones));
        Assert.Equal(new HashSet<string> { Headphones }, MirrorPlanner.OriginalDevices("app:brave.exe", snap));
    }

    [Fact]
    public void PausedAppUsesItsPinnedDeviceThenTheDefault()
    {
        Assert.Equal(new HashSet<string> { Headphones },
            MirrorPlanner.OriginalDevices("app:brave.exe", Snapshot(App("brave.exe", Headphones))));
        Assert.Equal(new HashSet<string> { Speakers },
            MirrorPlanner.OriginalDevices("app:vlc.exe", Snapshot(App("vlc.exe", null))));
    }

    [Fact]
    public void AppPlayingOnTwoDevicesSkipsBoth()
    {
        var snap = Snapshot(App("game.exe", null, Speakers, Hdmi));
        Assert.Equal(new HashSet<string> { Speakers, Hdmi }, MirrorPlanner.OriginalDevices("app:game.exe", snap));
    }

    [Fact]
    public void DeviceSourcesSkipThemselves()
    {
        var snap = Snapshot();
        Assert.Equal(new HashSet<string> { Speakers }, MirrorPlanner.OriginalDevices(MirrorSettings.DefaultDeviceSource, snap));
        Assert.Equal(new HashSet<string> { Hdmi }, MirrorPlanner.OriginalDevices("device:" + Hdmi, snap));
    }

    [Fact]
    public void AllAppsSourceSkipsNothing() =>
        Assert.Empty(MirrorPlanner.OriginalDevices(MirrorSettings.AllAppsSource, Snapshot(App("brave.exe", null, Speakers))));
}
