using System.IO;
using Xunit;

namespace SennheiserTray.Tests;

public sealed class CoreRulesTests
{
    [Fact]
    public void ReconnectBackoffCapsAtThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ReconnectBackoff.DelayForAttempt(0));
        Assert.Equal(TimeSpan.FromSeconds(5), ReconnectBackoff.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(10), ReconnectBackoff.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(30), ReconnectBackoff.DelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(30), ReconnectBackoff.DelayForAttempt(30));
    }

    [Fact]
    public void ReconnectBackoffRejectsNegativeAttempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconnectBackoff.DelayForAttempt(-1));
    }

    [Fact]
    public void ManualModeSelectionPinsAutomaticChanges()
    {
        var manager = new ModeManager();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        SoundMode selected = manager.SelectManually("Gaming", now);
        bool changed = manager.TrySelectAutomatically("Movie", now.AddSeconds(30), out SoundMode? automatic);

        Assert.Equal("Gaming", selected.Id);
        Assert.False(changed);
        Assert.Null(automatic);
        Assert.Equal("Music", manager.PreviousMode.Id);
        Assert.True(manager.IsPinned(now.AddSeconds(30)));
    }

    [Fact]
    public void AutoModeWaitsThreeSecondsBeforeSelectingCalls()
    {
        var controller = new AutoModeController();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var signals = new AutoModeSignals(true, "Teams", false, null, false, null);

        Assert.Null(controller.Evaluate(signals, "Music", start));
        AutoModeDecision? decision = controller.Evaluate(signals, "Music", start.AddSeconds(3));

        Assert.NotNull(decision);
        Assert.Equal(AutoModeRule.Call, decision!.Rule);
        Assert.Equal("Calls", decision.ModeId);
        Assert.Contains("Teams", decision.Caption);
    }

    [Fact]
    public void AutoModeClassifiesNonFiniteAudioFeaturesAsUnknown()
    {
        (AutoContentClass classification, int confidence) = AutoModeController.Classify(
            new AudioContentFeatures(double.NaN, 0.1, 3));

        Assert.Equal(AutoContentClass.Unknown, classification);
        Assert.Equal(0, confidence);
    }

    [Fact]
    public void GaiaPacketCodecBuildsBigEndianFrameAndParsesItsHeader()
    {
        byte[] payload = { 0x12, 0x34, 0x56 };

        byte[] packet = GaiaPacketCodec.BuildPacket(0x0495, 0x1009, payload);
        GaiaPacketHeader header = GaiaPacketCodec.ParseHeader(packet);

        Assert.Equal(new byte[] { 0xFF, 0x03, 0, 3, 0x04, 0x95, 0x10, 0x09 }, packet[..8]);
        Assert.Equal(payload, packet[GaiaPacketCodec.HeaderLength..]);
        Assert.Equal((ushort)3, header.PayloadLength);
        Assert.Equal((ushort)0x0495, header.VendorId);
        Assert.Equal((ushort)0x1009, header.CommandId);
    }

    [Fact]
    public void GaiaPacketCodecRejectsInvalidOrTruncatedHeaders()
    {
        Assert.Throws<InvalidDataException>(() =>
            GaiaPacketCodec.ParseHeader(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Throws<ArgumentException>(() => GaiaPacketCodec.ParseHeader(new byte[7]));
    }

    [Fact]
    public void GaiaPacketCodecRejectsOversizedPayload()
    {
        byte[] payload = new byte[ushort.MaxValue + 1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GaiaPacketCodec.BuildPacket(0x0495, 0x1009, payload));
    }
}
