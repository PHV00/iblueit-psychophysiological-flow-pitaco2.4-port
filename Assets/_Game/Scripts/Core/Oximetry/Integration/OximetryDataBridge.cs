using System;
using Ibit.Core.Serial;
using UnityEngine;

namespace Ibit.Core.Oximetry.Integration
{
    // The source is part of the data contract. A numeric SpO2 alone does not
    // establish whether the existing saturation rules may be applied.
    public enum OximetrySource
    {
        None,
        LegacyOximeter,
        Pitaco24
    }

    /// <summary>
    /// Final numerical output for the Mixer. Quality and permission to use
    /// legacy saturation rules are independent of numerical availability.
    /// </summary>
    public struct OximetrySnapshot
    {
        public readonly OximetrySource Source;
        public readonly double? Bpm;
        public readonly double? SpO2Percent;
        public readonly bool OpticalContactConfirmed;
        public readonly bool BasicSignalQualityApproved;
        public readonly bool SpO2IsDemonstration;
        public readonly bool EligibleForLegacySpo2Rules;
        public readonly float AgeSeconds;

        public bool HasBpm { get { return Bpm.HasValue; } }
        public bool HasSpO2 { get { return SpO2Percent.HasValue; } }

        public OximetrySnapshot(OximetrySource source, double? bpm,
            double? spo2Percent, bool contact, bool quality,
            bool demonstration, bool eligibleForLegacyRules, float age)
        {
            Source = source;
            Bpm = bpm;
            SpO2Percent = spo2Percent;
            OpticalContactConfirmed = contact;
            BasicSignalQualityApproved = quality;
            SpO2IsDemonstration = demonstration;
            EligibleForLegacySpo2Rules = eligibleForLegacyRules;
            AgeSeconds = age;
        }

        public static OximetrySnapshot Unavailable(OximetrySource source)
        {
            return new OximetrySnapshot(source, null, null, false, false,
                source == OximetrySource.Pitaco24, false,
                float.PositiveInfinity);
        }
    }

    /// <summary>
    /// Pure mapping for unit tests. 'Final' means no further RED/IR processing;
    /// it does not mean clinical validation or fitness for patient alerts.
    /// </summary>
    public static class OximetrySnapshotFactory
    {
        public const float MaxPitacoAgeSeconds = 2f;
        public const float MaxLegacyAgeSeconds = 15f;

        // Integration testing ONLY: reproduce legacy gameplay rules using the
        // uncalibrated PITACO 2.4 estimate. Set false to restore the original
        // calibration requirement; never use this to claim clinical validity.
        // Disabled by default. Explicit opt-in is restricted to controlled
        // gameplay integration tests; never implies clinical calibration.
        public static bool EnableDemoSpo2LegacyRulesForTesting { get; private set; }

        public static void ConfigureDemoSpo2LegacyRulesForTesting(bool enabled)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            EnableDemoSpo2LegacyRulesForTesting = enabled;
#else
            // Never allow DEMO oxygenation to drive patient-facing gameplay
            // adaptations in a normal release build.
            EnableDemoSpo2LegacyRulesForTesting = false;
#endif
        }

        // Unity publication time alone is insufficient when serial frames
        // remained queued through a scene stall. Queue delay is a conservative
        // lower bound; it never makes a measurement appear younger.
        public static float EffectiveAgeSeconds(float unityPublicationAge,
            int queueLagMs)
        {
            if (float.IsNaN(unityPublicationAge) ||
                float.IsInfinity(unityPublicationAge) ||
                unityPublicationAge < 0f)
                return float.PositiveInfinity;
            return unityPublicationAge + Math.Max(0, queueLagMs) / 1000f;
        }

        // Monotonic time since actual publication, plus estimated age of the
        // last device sample at that publication. Alignment can be unavailable
        // during startup; then the old queue-age lower bound remains mandatory.
        public static float EffectiveAlignedAgeSeconds(float monotonicSincePublication,
            int queueLagMs, float alignedSampleAgeAtPublicationSeconds)
        {
            float minimumAge = EffectiveAgeSeconds(monotonicSincePublication, queueLagMs);
            if (float.IsNaN(alignedSampleAgeAtPublicationSeconds) ||
                float.IsInfinity(alignedSampleAgeAtPublicationSeconds) ||
                alignedSampleAgeAtPublicationSeconds < 0f)
                return minimumAge;
            if (float.IsInfinity(minimumAge)) return minimumAge;
            return monotonicSincePublication + Math.Max(
                Math.Max(0, queueLagMs) / 1000f,
                alignedSampleAgeAtPublicationSeconds);
        }

        private static bool HasValidAge(float age, float limit)
        {
            return !float.IsNaN(age) && !float.IsInfinity(age) &&
                age >= 0f && age <= limit;
        }

        private static bool IsBpm(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) &&
                !double.IsInfinity(value.Value) && value.Value > 0;
        }

        private static bool IsSpO2(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) &&
                !double.IsInfinity(value.Value) && value.Value > 0 &&
                value.Value <= 100;
        }

        public static OximetrySnapshot FromPitaco24(
            bool connected, bool contact, bool gate01Approved,
            PpgMeasurement measurement, float ageSeconds)
        {
            if (!connected || !contact || !gate01Approved ||
                measurement == null || !HasValidAge(ageSeconds, MaxPitacoAgeSeconds))
                return OximetrySnapshot.Unavailable(OximetrySource.Pitaco24);

            double? bpm = IsBpm(measurement.BpmEstimate)
                ? measurement.BpmEstimate : null;
            double? spo2 = IsSpO2(measurement.SpO2DemoPercent)
                ? measurement.SpO2DemoPercent : null;

            // Keep clinical calibration separate from test-only permission to
            // exercise the old game rules. DEMO must remain explicitly labeled.
            bool calibrated = measurement.SpO2CalibratedForLegacyRules;
            bool eligible = spo2.HasValue &&
                (calibrated || EnableDemoSpo2LegacyRulesForTesting);
            return new OximetrySnapshot(OximetrySource.Pitaco24,
                bpm, spo2, true, true, !calibrated, eligible, ageSeconds);
        }

        public static OximetrySnapshot SelectForLegacyRules(
            OximetrySnapshot primary, OximetrySnapshot legacy)
        {
            // Preserve existing saturation alerts if a separate legacy device
            // is connected, even while PITACO 2.4 provides the primary HUD data.
            return legacy.EligibleForLegacySpo2Rules ? legacy : primary;
        }

        public static OximetrySnapshot FromLegacy(
            bool connected, double bpm, double spo2, float ageSeconds)
        {
            if (!connected || !HasValidAge(ageSeconds, MaxLegacyAgeSeconds))
                return OximetrySnapshot.Unavailable(
                    OximetrySource.LegacyOximeter);
            double? finalBpm = IsBpm(bpm) ? (double?)bpm : null;
            double? finalSpO2 = IsSpO2(spo2) ? (double?)spo2 : null;
            return new OximetrySnapshot(OximetrySource.LegacyOximeter,
                finalBpm, finalSpO2, true, true, false,
                finalSpO2.HasValue, ageSeconds);
        }
    }

    /// <summary>
    /// Persistent read-only integration. Does not open any USB/COM port.
    /// Uses existing PpgProcessingCoordinator and legacy Player inputs.
    /// </summary>
    public sealed class OximetryDataBridge : MonoBehaviour
    {
        public static OximetryDataBridge Active { get; private set; }

        [Header("Somente testes de integracao; NUNCA habilitar para pacientes")]
        [SerializeField] private bool allowUncalibratedSpo2GameRulesForTesting = false;

        private PpgProcessingCoordinator ppg;
        private SerialControllerOximetro legacyController;
        private float nextLegacyLookupAt;
        private PpgMeasurement lastPublished;
        private float lastPitacoPublicationAt = -1f;
        private int lastPitacoPublicationQueueLagMs;
        private double lastPitacoPublicationMonotonicMs = double.NaN;
        private float lastPitacoAlignedSampleAgeAtPublicationSeconds = float.NaN;
        private float lastLegacyPublicationAt = -1f;
        private double lastLegacyBpm;
        private double lastLegacySpo2;

        // Event-driven, no generic five-second duplicate snapshot spam.
        private float nextDiagnosticAt;
        private float nextHeartbeatAt;
        private string previousDiagnosticSignature;

        // A Bridge snapshot alone does NOT prove that the Mixer consumed it.
        // MixerManager acknowledges only after its fusion/adaptation calls finish.
        private float lastMixerConsumptionAt = -1f;
        private int mixerConsumptionCount;
        private OximetrySnapshot lastMixerConsumed;

        private void Awake()
        {
            if (Active != null && Active != this)
            {
                Destroy(this);
                return;
            }
            Active = this;
        }

        private void OnEnable()
        {
            if (Active != null && Active != this) return;
            Active = this;
            OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(
                allowUncalibratedSpo2GameRulesForTesting);
            if (allowUncalibratedSpo2GameRulesForTesting)
                Debug.LogWarning("[PPG-TEST] SpO2 DEMO nao calibrado autorizado " +
                    "para regras legadas SOMENTE neste ensaio de integracao.");
            AttachPpg();
        }

        private void Update()
        {
            // Poll state at a low rate, log every eligibility/availability
            // transition immediately. Include a heartbeat once every 10 s.
            if (Time.unscaledTime < nextDiagnosticAt) return;
            nextDiagnosticAt = Time.unscaledTime + 0.5f;
            OximetrySnapshot state = GetSnapshot();
            bool mixerRecent = lastMixerConsumptionAt >= 0f &&
                Time.unscaledTime - lastMixerConsumptionAt <= 2f;
            PpgMeasurement current = ppg != null ? ppg.LastMeasurement : null;
            // Diagnostic candidate for the HUD, NOT proof a UI widget rendered
            // the value. Mixer eligibility continues to use LastMeasurement.
            PpgMeasurement hudCandidate = ppg != null ? ppg.HudMeasurement : null;
            bool samePublication = current != null &&
                object.ReferenceEquals(current, lastPublished);
            bool bpmHudHold = ppg != null && ppg.HudHoldingRejectedBpm;
            float publicationAge = GetPitacoPublicationAgeSeconds();

            string cause;
            if (state.Source != OximetrySource.Pitaco24)
                cause = "LEGACY_SOURCE";
            else if (ppg == null)
                cause = "NO_COORDINATOR";
            else if (!ppg.OpticalContactConfirmed)
                cause = "CONTACT_NOT_CONFIRMED";
            else if (!ppg.Gate01Approved)
                cause = "G1_NOT_APPROVED_" + ppg.Gate01Status;
            else if (!samePublication)
                cause = "MEASUREMENT_NOT_PUBLISHED";
            else if (publicationAge > OximetrySnapshotFactory.MaxPitacoAgeSeconds)
                cause = "PUBLISHED_MEASUREMENT_STALE";
            else if (!state.HasSpO2 && !state.HasBpm)
                cause = "ESTIMATORS_NO_VALUES";
            else if (!state.HasSpO2)
                cause = "SPO2_ONLY_REJECTED";
            else if (!state.HasBpm)
                cause = "BPM_ONLY_REJECTED";
            else
                cause = "AVAILABLE";

            string signature = state.Source + "|" + cause + "|" +
                state.HasBpm + "|" + state.HasSpO2 + "|" +
                state.EligibleForLegacySpo2Rules + "|" + mixerRecent +
                "|" + bpmHudHold;
            if (signature == previousDiagnosticSignature &&
                Time.unscaledTime < nextHeartbeatAt)
                return;
            bool isTransition = signature != previousDiagnosticSignature;
            previousDiagnosticSignature = signature;
            nextHeartbeatAt = Time.unscaledTime + 10f;
            Debug.Log("[PPG-DIAG] t=" + Time.unscaledTime.ToString("F2") +
                " | event=" + (isTransition ? "BRIDGE_TRANSITION" : "BRIDGE_HEARTBEAT") +
                " | cause=" + cause +
                " | source=" + state.Source +
                " | rawContact=" + (ppg != null && ppg.OpticalContactConfirmed) +
                " | rawG1=" + (ppg != null && ppg.Gate01Approved) +
                " | samePublication=" + samePublication +
                " | ageS=" + publicationAge.ToString("F2") +
                " | publishQueueLagMs=" + lastPitacoPublicationQueueLagMs +
                " | clockAligned=" +
                    (ppg != null && ppg.HasDeviceClockAlignment) +
                " | alignedAgeAtPublishMs=" +
                    (float.IsNaN(lastPitacoAlignedSampleAgeAtPublicationSeconds)
                        ? "--" : (lastPitacoAlignedSampleAgeAtPublicationSeconds * 1000f).ToString("F1")) +
                " | bpm=" + FormatReading(state.Bpm) +
                " | spo2Demo=" + FormatReading(state.SpO2Percent) +
                " | eligible=" + state.EligibleForLegacySpo2Rules +
                " | mixerRecent=" + mixerRecent +
                " | mixerReads=" + mixerConsumptionCount +
                " | coordinatorStatus=" + (current != null ? current.Status : "none") +
                " | hudHold=" + (ppg != null && ppg.HudHoldingShortContactGap) +
                " | bpmHudHold=" + bpmHudHold +
                " | bpmRejectStreak=" +
                (ppg != null ? ppg.ConsecutiveBpmRejections : 0) +
                " | bpmThreshold=" +
                (ppg != null ? ppg.ActiveBpmAutocorrelationThreshold.ToString("F2") : "--") +
                " | bpmHeldVisualOnly=" +
                (ppg != null && ppg.HudRetainedBpmVisualOnly.HasValue
                    ? FormatReading(ppg.HudRetainedBpmVisualOnly) : "--") +
                " | hudCandidateBpm=" +
                (hudCandidate != null ? FormatReading(hudCandidate.BpmEstimate) : "--") +
                " | hudCandidateSpo2Demo=" +
                (hudCandidate != null ? FormatReading(hudCandidate.SpO2DemoPercent) : "--") +
                " | serialPending=" +
                (PitacoConnectionManager.Active != null ? PitacoConnectionManager.Active.PendingLines : 0));
        }

        private static string FormatReading(double? value)
        {
            return value.HasValue ? value.Value.ToString("F1") : "--";
        }

        // Called by MixerManager only AFTER FusionManager and AdaptationGrid
        // execute. This is an integration acknowledgement, not an ONNX input.
        public void RecordMixerConsumption(OximetrySnapshot consumed)
        {
            lastMixerConsumed = consumed;
            lastMixerConsumptionAt = Time.unscaledTime;
            if (mixerConsumptionCount < int.MaxValue)
                mixerConsumptionCount++;
        }

        private void AttachPpg()
        {
            if (ppg == null)
                ppg = GetComponent<PpgProcessingCoordinator>();
            if (ppg == null) return;
            ppg.OnMeasurement -= OnPpgMeasurement;
            ppg.OnMeasurement += OnPpgMeasurement;
        }

        private void OnPpgMeasurement(PpgMeasurement measurement)
        {
            lastPublished = measurement;
            lastPitacoPublicationAt = Time.unscaledTime;
            lastPitacoPublicationMonotonicMs =
                PpgMonotonicClockAligner.HostNowMilliseconds();
            lastPitacoAlignedSampleAgeAtPublicationSeconds = ppg != null
                ? ppg.LastPublishedAlignedSampleAgeAtPublishSeconds : float.NaN;
            var manager = PitacoConnectionManager.Active;
            // An old line does NOT become fresh when Unity finally dispatches it.
            lastPitacoPublicationQueueLagMs = manager != null
                ? Math.Max(0, manager.CurrentDispatchQueueLagMs) : 0;
        }

        private float GetPitacoPublicationAgeSeconds()
        {
            if (lastPitacoPublicationAt < 0f ||
                double.IsNaN(lastPitacoPublicationMonotonicMs))
                return float.PositiveInfinity;
            double nowMs = PpgMonotonicClockAligner.HostNowMilliseconds();
            double elapsedMs = nowMs - lastPitacoPublicationMonotonicMs;
            if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs) ||
                elapsedMs < 0 || elapsedMs > float.MaxValue * 1000.0)
                return float.PositiveInfinity;
            return OximetrySnapshotFactory.EffectiveAlignedAgeSeconds(
                (float)(elapsedMs / 1000.0),
                lastPitacoPublicationQueueLagMs,
                lastPitacoAlignedSampleAgeAtPublicationSeconds);
        }

        // Called after the legacy Player accepts its next comma-delimited pair.
        public void RecordLegacy(double bpm, double spo2)
        {
            lastLegacyBpm = bpm;
            lastLegacySpo2 = spo2;
            lastLegacyPublicationAt = Time.unscaledTime;
        }

        public OximetrySnapshot GetSnapshot()
        {
            PitacoConnectionManager manager = PitacoConnectionManager.Active;
            if (manager != null &&
                manager.ConnectedVersion == PitacoConnectionManager.Version.Pitaco24)
            {
                if (ppg == null) AttachPpg();
                PpgMeasurement measurement = ppg != null ? ppg.LastMeasurement : null;
                // No old value after loss of contact, rejected G1, timeout or
                // a transient invalidation: LastMeasurement gets replaced,
                // but only successful processing emits OnMeasurement.
                float age = measurement != null &&
                    object.ReferenceEquals(measurement, lastPublished)
                    ? GetPitacoPublicationAgeSeconds()
                    : float.PositiveInfinity;
                return OximetrySnapshotFactory.FromPitaco24(
                    true, ppg != null && ppg.OpticalContactConfirmed,
                    ppg != null && ppg.Gate01Approved, measurement, age);
            }

            // Legacy oximeter remains independent: no stale values after
            // disconnection and no silent fallback while PITACO 2.4 is active.
            return GetLegacySnapshot();
        }

        // A separate legacy device must keep its original safety-path
        // behavior when both sensors are connected. It does NOT replace
        // the PITACO 2.4 primary snapshot shown to the Mixer.
        public OximetrySnapshot GetLegacyRulesSnapshot()
        {
            OximetrySnapshot primary = GetSnapshot();
            if (primary.Source == OximetrySource.LegacyOximeter)
                return primary;
            return OximetrySnapshotFactory.SelectForLegacyRules(
                primary, GetLegacySnapshot());
        }

        private OximetrySnapshot GetLegacySnapshot()
        {
            // No legacy oximeter is a normal PITACO-only configuration.
            // Do not execute a whole-scene search on every Mixer Update.
            if (legacyController == null && Time.unscaledTime >= nextLegacyLookupAt)
            {
                nextLegacyLookupAt = Time.unscaledTime + 1f;
                legacyController = FindObjectOfType<SerialControllerOximetro>();
            }
            bool connected = legacyController != null && legacyController.IsConnected;
            float legacyAge = lastLegacyPublicationAt >= 0f
                ? Mathf.Max(0f, Time.unscaledTime - lastLegacyPublicationAt)
                : float.PositiveInfinity;
            return OximetrySnapshotFactory.FromLegacy(connected,
                lastLegacyBpm, lastLegacySpo2, legacyAge);
        }

        private void OnDisable()
        {
            if (ppg != null)
                ppg.OnMeasurement -= OnPpgMeasurement;
            lastPublished = null;
            lastPitacoPublicationAt = -1f;
            lastPitacoPublicationQueueLagMs = 0;
            lastPitacoPublicationMonotonicMs = double.NaN;
            lastPitacoAlignedSampleAgeAtPublicationSeconds = float.NaN;
            lastMixerConsumptionAt = -1f;
            mixerConsumptionCount = 0;
            if (Active == this)
            {
                OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(false);
                Active = null;
            }
        }
    }
}
