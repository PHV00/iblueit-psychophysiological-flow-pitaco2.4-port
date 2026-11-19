using Ibit.Core.Oximetry;
using Ibit.Core.Oximetry.Integration;
using NUnit.Framework;

public class OximetryDataBridgeTests
{
    [SetUp]
    public void EnsureDemoDisabledByDefault()
    {
        OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(false);
    }

    [TearDown]
    public void RemoveDemoOptInAfterTest()
    {
        OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(false);
    }

    [Test]
    public void Pitaco24DemoSpo2IsNotEligibleForLegacyGameplayByDefault()
    {
        var reading = new PpgMeasurement(12000, 25, 82, 96, 0.6,
            0.75, "EXPERIMENTAL - NAO CALIBRADO");
        var snapshot = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, reading, 0.4f);

        Assert.AreEqual(OximetrySource.Pitaco24, snapshot.Source);
        Assert.AreEqual(82.0, snapshot.Bpm.Value);
        Assert.AreEqual(96.0, snapshot.SpO2Percent.Value);
        Assert.IsTrue(snapshot.HasBpm);
        Assert.IsTrue(snapshot.HasSpO2);
        Assert.IsTrue(snapshot.SpO2IsDemonstration);
        Assert.IsFalse(OximetrySnapshotFactory.EnableDemoSpo2LegacyRulesForTesting);
        Assert.IsFalse(snapshot.EligibleForLegacySpo2Rules);
    }

    [Test]
    public void DemoRulesRequireExplicitEditorOrDevelopmentOptIn()
    {
        OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(true);
        var reading = new PpgMeasurement(12000, 25, 82, 96, 0.6,
            0.75, "EXPERIMENTAL - NAO CALIBRADO");
        var snapshot = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, reading, 0.4f);
        Assert.IsTrue(snapshot.EligibleForLegacySpo2Rules);
    }

    [Test]
    public void InvalidContactGateDisconnectedOrStaleDataNeverPasses()
    {
        var reading = new PpgMeasurement(12000, 25, 82, 96, 0.6,
            0.75, "EXPERIMENTAL");
        var noContact = OximetrySnapshotFactory.FromPitaco24(
            true, false, true, reading, 0.1f);
        var failedGate = OximetrySnapshotFactory.FromPitaco24(
            true, true, false, reading, 0.1f);
        var disconnected = OximetrySnapshotFactory.FromPitaco24(
            false, true, true, reading, 0.1f);
        var stale = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, reading, 2.1f);
        Assert.IsFalse(noContact.HasBpm);
        Assert.IsFalse(failedGate.HasSpO2);
        Assert.IsFalse(disconnected.HasSpO2);
        Assert.IsFalse(stale.HasBpm);
        Assert.IsFalse(noContact.EligibleForLegacySpo2Rules);
        Assert.IsFalse(failedGate.EligibleForLegacySpo2Rules);
        Assert.IsFalse(disconnected.EligibleForLegacySpo2Rules);
        Assert.IsFalse(stale.EligibleForLegacySpo2Rules);
    }

    [Test]
    public void BpmCanContinueWithoutReconstructedSpo2()
    {
        var reading = new PpgMeasurement(12000, 25, 82, null,
            null, 0.75, "BPM reconstructed");
        var snapshot = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, reading, 0.1f);
        Assert.IsTrue(snapshot.HasBpm);
        Assert.IsFalse(snapshot.HasSpO2);
        Assert.IsFalse(snapshot.EligibleForLegacySpo2Rules);
    }

    [Test]
    public void LegacyOximeterRetainsOriginalEligibilityWhenRecentAndConnected()
    {
        var snapshot = OximetrySnapshotFactory.FromLegacy(
            true, 81, 97, 6f);
        Assert.AreEqual(OximetrySource.LegacyOximeter, snapshot.Source);
        Assert.AreEqual(97, snapshot.SpO2Percent.Value);
        Assert.IsTrue(snapshot.EligibleForLegacySpo2Rules);
        Assert.IsFalse(snapshot.SpO2IsDemonstration);
        Assert.IsFalse(OximetrySnapshotFactory.FromLegacy(
            false, 81, 97, 0f).EligibleForLegacySpo2Rules);
        Assert.IsFalse(OximetrySnapshotFactory.FromLegacy(
            true, 81, 97, 16f).HasSpO2);
    }

    [Test]
    public void CalibratedFuturePitacoCanReuseSameMixerPath()
    {
        // Only a positive contract test. The current PPG estimator always
        // produces the default (false) eligibility flag.
        var reading = new PpgMeasurement(12000, 25, 82, 96,
            0.6, 0.75, "VALIDATED", true);
        var snapshot = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, reading, 0.1f);
        Assert.IsTrue(snapshot.EligibleForLegacySpo2Rules);
        Assert.IsFalse(snapshot.SpO2IsDemonstration);
    }

    [Test]
    public void SeparateLegacyOximeterKeepsSafetyRulesWhenBothAreConnected()
    {
        var ppg = new PpgMeasurement(12000, 25, 82, 96,
            0.6, 0.75, "EXPERIMENTAL");
        var primary = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, ppg, 0.1f);
        var legacy = OximetrySnapshotFactory.FromLegacy(
            true, 79, 98, 0.1f);
        var chosen = OximetrySnapshotFactory.SelectForLegacyRules(
            primary, legacy);

        Assert.AreEqual(OximetrySource.Pitaco24, primary.Source);
        Assert.AreEqual(OximetrySource.LegacyOximeter, chosen.Source);
        Assert.AreEqual(98.0, chosen.SpO2Percent.Value);
        Assert.IsTrue(chosen.EligibleForLegacySpo2Rules);

        var unavailableLegacy = OximetrySnapshotFactory.FromLegacy(
            false, 79, 98, 0f);
        var fallback = OximetrySnapshotFactory.SelectForLegacyRules(
            primary, unavailableLegacy);
        Assert.AreEqual(OximetrySource.Pitaco24, fallback.Source);
        Assert.IsFalse(fallback.EligibleForLegacySpo2Rules);
        Assert.IsTrue(fallback.SpO2IsDemonstration);
    }

    [Test]
    public void InvalidNumbersAndUnknownAgesNeverMasqueradeAsMeasurements()
    {
        var reading = new PpgMeasurement(12000, 25,
            double.NaN, double.PositiveInfinity, null, 0.0, "invalid");
        var snapshot = OximetrySnapshotFactory.FromPitaco24(
            true, true, true, reading, 0.1f);
        Assert.IsFalse(snapshot.HasBpm);
        Assert.IsFalse(snapshot.HasSpO2);
        Assert.IsFalse(snapshot.EligibleForLegacySpo2Rules);
        Assert.IsFalse(OximetrySnapshotFactory.FromLegacy(
            true, double.NaN, 0, float.PositiveInfinity).HasBpm);
    }
}
