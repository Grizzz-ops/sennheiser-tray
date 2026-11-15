using Xunit;

namespace SennheiserTray.Tests;

public sealed class AdaptiveEqProfileLearnerTests
{
    [Fact]
    public void LearnerWaitsForStableWindowBeforeReturningAClampedProfile()
    {
        var learner = new AdaptiveEqProfileLearner();
        learner.Start(new[] { 60d, 60d, 0d, -60d, -60d });
        var spectrum = new[] { -20d, -30d, -40d, -50d, -60d };

        for (int sample = 0; sample < 14; sample++)
        {
            AdaptiveEqFrameResult result = learner.ProcessSpectrum(spectrum);
            Assert.Equal(AdaptiveEqFrameState.Learning, result.State);
            Assert.Equal(sample + 1, result.SampleCount);
            Assert.Null(result.Profile);
        }

        AdaptiveEqFrameResult completed = learner.ProcessSpectrum(spectrum);

        Assert.Equal(AdaptiveEqFrameState.ProfileReady, completed.State);
        Assert.Equal(15, completed.SampleCount);
        Assert.NotNull(completed.Profile);
        Assert.All(completed.Profile!, value =>
        {
            Assert.InRange(value, -60, 60);
            Assert.Equal(0, value % 5);
        });
        Assert.Equal(AdaptiveEqFrameState.Locked, learner.ProcessSpectrum(spectrum).State);
    }

    [Fact]
    public void ThreeSilentFramesResetLearningAndAllowRelearning()
    {
        var learner = new AdaptiveEqProfileLearner();
        learner.Start(new double[5]);
        var spectrum = new[] { -20d, -30d, -40d, -50d, -60d };
        learner.ProcessSpectrum(spectrum);

        Assert.Equal(AdaptiveEqFrameState.Silent, learner.ProcessSpectrum(new[] { -80d, -80d, -80d, -80d, -80d }).State);
        Assert.Equal(AdaptiveEqFrameState.Silent, learner.ProcessSpectrum(new[] { -80d, -80d, -80d, -80d, -80d }).State);
        AdaptiveEqFrameResult paused = learner.ProcessSpectrum(new[] { -80d, -80d, -80d, -80d, -80d });

        Assert.Equal(AdaptiveEqFrameState.Paused, paused.State);
        Assert.Equal(0, learner.SampleCount);
        Assert.Equal(AdaptiveEqFrameState.Learning, learner.ProcessSpectrum(spectrum).State);
    }

    [Fact]
    public void UpdatingManualBaselineRestartsTheLearningWindow()
    {
        var learner = new AdaptiveEqProfileLearner();
        learner.Start(new double[5]);
        var spectrum = new[] { -20d, -30d, -40d, -50d, -60d };
        learner.ProcessSpectrum(spectrum);
        learner.ProcessSpectrum(spectrum);

        learner.UpdateBaseline(new[] { 10d, 20d, 30d, 40d, 50d });

        Assert.Equal(0, learner.SampleCount);
        Assert.Equal(new[] { 10d, 20d, 30d, 40d, 50d }, learner.ManualProfile);
        Assert.Equal(AdaptiveEqFrameState.Learning, learner.ProcessSpectrum(spectrum).State);
    }

    [Fact]
    public void LearnerRejectsInvalidProfilesAndSpectra()
    {
        var learner = new AdaptiveEqProfileLearner();

        Assert.Throws<ArgumentException>(() => learner.Start(new double[4]));
        learner.Start(new double[5]);
        Assert.Throws<ArgumentException>(() => learner.ProcessSpectrum(new double[4]));
        Assert.Throws<ArgumentException>(() =>
            learner.ProcessSpectrum(new[] { -20d, -30d, double.NaN, -50d, -60d }));
    }
}
