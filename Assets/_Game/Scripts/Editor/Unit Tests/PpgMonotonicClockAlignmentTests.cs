using System;
using Ibit.Core.Oximetry;
using Ibit.Core.Oximetry.Integration;
using NUnit.Framework;

public sealed class PpgMonotonicClockAlignmentTests
{
    [Test]
    public void RequiresThreeFramesBeforeUsingOneWayOffset()
    {
        var clock = new PpgMonotonicClockAligner();
        Assert.IsFalse(clock.HasAlignment);
        Assert.IsTrue(double.IsNaN(clock.EstimateLatestSampleAgeMilliseconds(11000)));
        Assert.AreEqual(PpgClockObservation.Initializing,
            clock.Observe(100, 1000, 11000));
        Assert.IsFalse(clock.HasAlignment);
        clock.Observe(101, 1040, 11040);
        Assert.IsFalse(clock.HasAlignment);
        clock.Observe(102, 1080, 11080);
        Assert.IsTrue(clock.HasAlignment);
        Assert.AreEqual(2.0, clock.EstimateLatestSampleAgeMilliseconds(11082), 0.01);
    }

    [Test]
    public void UsbBurstDelayIsVisibleInsteadOfRejuvenatingTheSample()
    {
        var clock = new PpgMonotonicClockAligner();
        clock.Observe(100, 1000, 11000);
        clock.Observe(101, 1040, 11040);
        clock.Observe(102, 1080, 11080);
        // Host receives the next valid device timestamp 180ms later,
        // even though the sensor advanced only 40ms.
        Assert.AreEqual(PpgClockObservation.Tracking,
            clock.Observe(103, 1120, 11260));
        Assert.Greater(clock.LastExcessArrivalDelayMilliseconds, 135);
        Assert.Greater(clock.EstimateLatestSampleAgeMilliseconds(11260), 135);
        Assert.AreEqual(0, clock.DeviceRestarts);
    }

    [Test]
    public void FiveSecondUnityStallDoesNotChangeTheWorkerTimestamp()
    {
        var clock = new PpgMonotonicClockAligner();
        clock.Observe(1, 1000, 11000);
        clock.Observe(2, 1040, 11040);
        clock.Observe(3, 1080, 11080);
        // Now simulates time when Unity finally dispatches the backlog.
        Assert.Greater(clock.EstimateLatestSampleAgeMilliseconds(16080), 4990);
        Assert.AreEqual(0, clock.DeviceRestarts);
    }

    [Test]
    public void FirmwareMillisAndSequenceRestartInvalidateOldEpoch()
    {
        var clock = new PpgMonotonicClockAligner();
        clock.Observe(1000, 300000, 500000);
        clock.Observe(1001, 300040, 500040);
        clock.Observe(1002, 300080, 500080);
        Assert.IsTrue(clock.HasAlignment);
        Assert.AreEqual(PpgClockObservation.DeviceRestart,
            clock.Observe(10, 100, 501000));
        Assert.AreEqual(1, clock.DeviceRestarts);
        Assert.IsFalse(clock.HasAlignment);
        Assert.IsTrue(double.IsNaN(clock.EstimateLatestSampleAgeMilliseconds(501001)));
        clock.Observe(11, 140, 501040);
        clock.Observe(12, 180, 501080);
        Assert.IsTrue(clock.HasAlignment);
    }

    [Test]
    public void DeviceMillisWrapDoesNotLookLikeRestart()
    {
        var clock = new PpgMonotonicClockAligner();
        Assert.AreEqual(PpgClockObservation.Initializing,
            clock.Observe(65534, uint.MaxValue - 40u, 70000));
        clock.Observe(65535, uint.MaxValue, 70040);
        Assert.AreEqual(PpgClockObservation.DeviceWrap,
            clock.Observe(0, 39u, 70080));
        Assert.IsTrue(clock.HasAlignment);
        Assert.AreEqual(1, clock.DeviceWraps);
        Assert.AreEqual(0, clock.DeviceRestarts);
        Assert.AreEqual(0.0, clock.EstimateLatestSampleAgeMilliseconds(70080), 1.0);
    }

    [Test]
    public void SixteenBitSequenceWrapIsNotFirmwareRestart()
    {
        var clock = new PpgMonotonicClockAligner();
        clock.Observe(65535, 1000, 11000);
        clock.Observe(0, 1040, 11040);
        clock.Observe(1, 1080, 11080);
        Assert.IsTrue(clock.HasAlignment);
        Assert.AreEqual(0, clock.DeviceRestarts);
    }

    [Test]
    public void LargeFifoReadTimestampGapIsDiagnosticNotAClockReset()
    {
        var clock = new PpgMonotonicClockAligner();
        clock.Observe(10, 1000, 11000);
        clock.Observe(11, 1040, 11040);
        clock.Observe(12, 1080, 11080);
        Assert.AreEqual(PpgClockObservation.AcquisitionGap,
            clock.Observe(13, 2080, 12080));
        Assert.AreEqual(1, clock.AcquisitionGaps);
        Assert.AreEqual(0, clock.DeviceRestarts);
    }

    [Test]
    public void UnexpectedSequenceRegressionStartsNewClockEpoch()
    {
        var clock = new PpgMonotonicClockAligner();
        clock.Observe(400, 1000, 11000);
        clock.Observe(401, 1040, 11040);
        clock.Observe(402, 1080, 11080);
        Assert.AreEqual(PpgClockObservation.DeviceRestart,
            clock.Observe(200, 1120, 11120));
        Assert.IsFalse(clock.HasAlignment);
    }

    [Test]
    public void InvalidHostTimesNeverEstablishAlignment()
    {
        var clock = new PpgMonotonicClockAligner();
        Assert.AreEqual(PpgClockObservation.Unusable,
            clock.Observe(1, 1000, double.NaN));
        Assert.AreEqual(PpgClockObservation.Unusable,
            clock.Observe(2, 1040, double.PositiveInfinity));
        Assert.IsFalse(clock.HasAlignment);
        Assert.IsTrue(double.IsNaN(
            PpgMonotonicClockAligner.HostTimestampMilliseconds(0)));
    }

    [Test]
    public void BridgeAgeNeverLessThanQueueAgeAndIsMonotonic()
    {
        Assert.AreEqual(0.13f, OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
            0.1f, 30, float.NaN), 0.0001f);
        Assert.AreEqual(2.1f, OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
            0.1f, 30, 2.0f), 0.0001f);
        Assert.AreEqual(2.6f, OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
            0.1f, 2500, 0.2f), 0.0001f);
        Assert.IsTrue(float.IsPositiveInfinity(
            OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
                float.NaN, 30, 0.2f)));
        Assert.Greater(OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
            1.2f, 30, 0.5f),
            OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
                0.2f, 30, 0.5f));
    }

    [Test]
    public void AlignedAgeCannotReviveStalePpgInMixer()
    {
        var measurement = new PpgMeasurement(1080, 25.0, 55.0, 97.0,
            0.5, 0.81, "EXPERIMENTAL - NAO CALIBRADO");
        float age = OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
            0.2f, 20, 2.2f);
        OximetrySnapshot stale = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, measurement, age);
        Assert.IsFalse(stale.HasBpm);
        Assert.IsFalse(stale.HasSpO2);

        float freshAge = OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
            0.2f, 20, 0.1f);
        OximetrySnapshot fresh = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, measurement, freshAge);
        Assert.IsTrue(fresh.HasBpm);
    }

    [Test]
    public void ExplicitClearRemovesAlignmentWithoutInventingAnEpoch()
    {
        var clock = new PpgMonotonicClockAligner();
        clock.Observe(1, 1000, 11000);
        clock.Observe(2, 1040, 11040);
        clock.Observe(3, 1080, 11080);
        clock.Clear();
        Assert.IsFalse(clock.HasAlignment);
        Assert.IsTrue(double.IsNaN(clock.OffsetMilliseconds));
        Assert.AreEqual(PpgClockObservation.Initializing,
            clock.Observe(1, 100, 12000));
    }
}
