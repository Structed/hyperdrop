using HyperVDrop.Core.Model;
using HyperVDrop.Core.Transfer;

namespace HyperVDrop.Core.Tests;

public sealed class RateEstimatorTests
{
    [Fact]
    public void BeforeAnySamples_NoRateIsAvailable()
    {
        var estimator = new RateEstimator(new TestTimeProvider());

        Assert.Null(estimator.BytesPerSecond);
        Assert.Null(estimator.EstimateRemaining(1000));
    }

    [Fact]
    public void SteadyTransfer_ConvergesOnTheRealRate()
    {
        var time = new TestTimeProvider();
        var estimator = new RateEstimator(time);

        const long BytesPerSecond = 1_000_000;
        long transferred = 0;

        estimator.Update(transferred);

        for (var i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            transferred += BytesPerSecond;
            estimator.Update(transferred);
        }

        Assert.NotNull(estimator.BytesPerSecond);
        Assert.InRange(estimator.BytesPerSecond!.Value, BytesPerSecond * 0.9, BytesPerSecond * 1.1);
    }

    [Fact]
    public void SamplesArrivingTooQuickly_AreIgnored()
    {
        var time = new TestTimeProvider();
        var estimator = new RateEstimator(time);

        estimator.Update(0);
        time.Advance(TimeSpan.FromMilliseconds(50));
        estimator.Update(5000);

        Assert.Null(estimator.BytesPerSecond);
    }

    [Fact]
    public void EstimateRemaining_UsesTheMeasuredRate()
    {
        var time = new TestTimeProvider();
        var estimator = new RateEstimator(time);

        estimator.Update(0);

        for (var i = 1; i <= 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            estimator.Update(i * 1_000_000L);
        }

        var remaining = estimator.EstimateRemaining(10_000_000);

        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, 8, 12);
    }

    [Fact]
    public void EstimateRemaining_WithNothingLeft_IsZero()
    {
        var estimator = new RateEstimator(new TestTimeProvider());

        Assert.Equal(TimeSpan.Zero, estimator.EstimateRemaining(0));
    }

    [Fact]
    public void ACounterThatGoesBackwards_DiscardsTheRateInsteadOfReportingNonsense()
    {
        var time = new TestTimeProvider();
        var estimator = new RateEstimator(time);

        estimator.Update(0);
        time.Advance(TimeSpan.FromSeconds(1));
        estimator.Update(1_000_000);

        Assert.NotNull(estimator.BytesPerSecond);

        // A retry restarts the file from zero.
        time.Advance(TimeSpan.FromSeconds(1));
        estimator.Update(0);

        Assert.Null(estimator.BytesPerSecond);
    }

    [Fact]
    public void Reset_ClearsPreviousMeasurements()
    {
        var time = new TestTimeProvider();
        var estimator = new RateEstimator(time);

        estimator.Update(0);
        time.Advance(TimeSpan.FromSeconds(1));
        estimator.Update(1_000_000);
        Assert.NotNull(estimator.BytesPerSecond);

        estimator.Reset();

        Assert.Null(estimator.BytesPerSecond);
    }

    [Fact]
    public void VerySlowTransfers_ProduceNoEstimateRatherThanAnAbsurdOne()
    {
        var time = new TestTimeProvider();
        var estimator = new RateEstimator(time);

        estimator.Update(0);
        time.Advance(TimeSpan.FromSeconds(10));
        estimator.Update(10);

        Assert.Null(estimator.EstimateRemaining(1_000_000_000));
    }

    [Fact]
    public void CopyProgress_FromPercent_ScalesAgainstTheFileSize()
    {
        var progress = CopyProgress.FromPercent(25, 4000);

        Assert.Equal(1000, progress.BytesTransferred);
        Assert.Equal(0.25, progress.Fraction);
    }

    [Fact]
    public void CopyProgress_Indeterminate_HasNoFraction()
    {
        Assert.Null(CopyProgress.Indeterminate(1000).Fraction);
    }

    [Fact]
    public void CopyProgress_ClampsOutOfRangeInput()
    {
        Assert.Equal(1000, CopyProgress.FromPercent(150, 1000).BytesTransferred);
        Assert.Equal(0, CopyProgress.FromPercent(-10, 1000).BytesTransferred);
        Assert.Equal(500, CopyProgress.FromBytes(9999, 500).BytesTransferred);
    }

    /// <summary>A clock the test drives by hand, at millisecond resolution.</summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount) => _timestamp += (long)amount.TotalMilliseconds;
    }
}
