using Dismusic.Core.Mirror;

namespace Dismusic.Tests;

public class AudioRingBufferTests
{
    [Fact]
    public void ReadsBackInOrderAcrossWrapAround()
    {
        var ring = new AudioRingBuffer(channels: 2, capacityFrames: 4);
        ring.Write([1, 1, 2, 2, 3, 3]);
        var dest = new float[4];
        Assert.Equal(2, ring.Read(dest));
        ring.Write([4, 4, 5, 5, 6, 6]); // wraps
        var all = new float[8];
        Assert.Equal(4, ring.Read(all));
        Assert.Equal(new float[] { 3, 3, 4, 4, 5, 5, 6, 6 }, all);
        Assert.Equal(0, ring.CountFrames);
    }

    [Fact]
    public void OverflowDropsOldestAudio()
    {
        var ring = new AudioRingBuffer(channels: 1, capacityFrames: 3);
        ring.Write([1, 2, 3, 4, 5]);
        var dest = new float[3];
        Assert.Equal(3, ring.Read(dest));
        Assert.Equal(new float[] { 3, 4, 5 }, dest);
        Assert.Equal(2, ring.OverflowFrames);
    }

    [Fact]
    public void IgnoresTrailingPartialFrameAndHonorsSkip()
    {
        var ring = new AudioRingBuffer(channels: 2, capacityFrames: 8);
        ring.Write([1, 1, 2, 2, 9]); // trailing half-frame is dropped
        Assert.Equal(2, ring.CountFrames);
        ring.Skip(1);
        var dest = new float[4];
        Assert.Equal(1, ring.Read(dest));
        Assert.Equal(2, dest[0]);
    }

    [Fact]
    public void ReadFromEmptyReturnsZero()
    {
        var ring = new AudioRingBuffer(2, 4);
        Assert.Equal(0, ring.Read(new float[8]));
    }
}

public class MirrorChannelTests
{
    private const int Rate = 48000;
    private const int BlockFrames = 480; // 10 ms

    private static float[] Block(int frames, float value = 0.5f)
    {
        var b = new float[frames * 2];
        Array.Fill(b, value);
        return b;
    }

    [Fact]
    public void OutputsSilenceUntilTargetLatencyIsBuffered()
    {
        var ch = new MirrorChannel(Rate, 2, Rate, 2);
        ch.Write(Block(BlockFrames));
        var output = new float[BlockFrames * 2];
        Array.Fill(output, 1f);
        ch.Render(output);
        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void PassesAudioThroughOnceTargetIsReachedAndAppliesVolume()
    {
        var ch = new MirrorChannel(Rate, 2, Rate, 2) { Volume = 0.5f };
        for (int i = 0; i < 8; i++) ch.Write(Block(BlockFrames, 0.8f));
        var output = new float[BlockFrames * 2];
        ch.Render(output);
        Assert.All(output, s => Assert.Equal(0.4f, s, 3));
    }

    [Theory]
    [InlineData(200)]   // capture clock 200 ppm fast
    [InlineData(-200)]  // capture clock 200 ppm slow
    [InlineData(1000)]  // badly mismatched clocks
    public void HoldsBufferNearTargetUnderClockDrift(int ppm)
    {
        var ch = new MirrorChannel(Rate, 2, Rate, 2);
        double produced = 0;
        int written = 0;
        var output = new float[BlockFrames * 2];
        long underrunsAfterWarmup = -1;

        for (int block = 0; block < 6000; block++) // 60 simulated seconds
        {
            produced += BlockFrames * (1 + ppm / 1e6);
            int toWrite = (int)produced - written;
            ch.Write(Block(toWrite));
            written += toWrite;
            ch.Render(output);
            if (block == 500) underrunsAfterWarmup = ch.Underruns;
        }

        double target = MirrorChannel.BaseLatencyMs;
        Assert.InRange(ch.BufferedMs, target - 25, target + 25);
        Assert.Equal(underrunsAfterWarmup, ch.Underruns);
        Assert.Equal(Math.Sign(ppm), Math.Sign(ch.Correction));
    }

    [Fact]
    public void ResamplesToDeviceRateWithoutDriftingTheBuffer()
    {
        // 48 kHz in, 44.1 kHz device: 441 output frames per 10 ms consume 480 input frames.
        var ch = new MirrorChannel(48000, 2, 44100, 2);
        var output = new float[441 * 2];
        for (int block = 0; block < 3000; block++)
        {
            ch.Write(Block(480));
            ch.Render(output);
        }
        Assert.InRange(ch.BufferedMs, MirrorChannel.BaseLatencyMs - 25, MirrorChannel.BaseLatencyMs + 25);
    }

    [Fact]
    public void UnderrunFillsSilenceAndCounts()
    {
        var ch = new MirrorChannel(Rate, 2, Rate, 2);
        for (int i = 0; i < 7; i++) ch.Write(Block(BlockFrames));
        var output = new float[BlockFrames * 2];
        for (int i = 0; i < 20; i++) ch.Render(output); // drain far beyond what was written
        Assert.True(ch.Underruns >= 1);
        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void MapsStereoToMonoAndSurround()
    {
        var mono = new MirrorChannel(Rate, 2, Rate, 1);
        var surround = new MirrorChannel(Rate, 2, Rate, 6);
        var frame = new float[BlockFrames * 2];
        for (int i = 0; i < frame.Length; i += 2) { frame[i] = 1f; frame[i + 1] = 0f; }
        for (int i = 0; i < 8; i++) { mono.Write(frame); surround.Write(frame); }

        var m = new float[BlockFrames];
        mono.Render(m);
        Assert.All(m, s => Assert.Equal(0.5f, s, 3));

        var s6 = new float[BlockFrames * 6];
        surround.Render(s6);
        for (int f = 0; f < BlockFrames; f++)
        {
            Assert.Equal(1f, s6[f * 6], 3);
            Assert.Equal(0f, s6[f * 6 + 1], 3);
            for (int c = 2; c < 6; c++) Assert.Equal(0f, s6[f * 6 + c]);
        }
    }

    [Fact]
    public void DelayRaisesTheBufferedLatency()
    {
        var ch = new MirrorChannel(Rate, 2, Rate, 2) { DelayMs = 200 };
        var output = new float[BlockFrames * 2];
        for (int block = 0; block < 1500; block++)
        {
            ch.Write(Block(BlockFrames));
            ch.Render(output);
        }
        Assert.InRange(ch.BufferedMs, MirrorChannel.BaseLatencyMs + 200 - 25, MirrorChannel.BaseLatencyMs + 200 + 25);
    }
}
