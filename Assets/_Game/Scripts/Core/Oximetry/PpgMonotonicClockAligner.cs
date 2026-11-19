using System;
using System.Diagnostics;

namespace Ibit.Core.Oximetry
{
    /// <summary>
    /// One-way, approximate alignment between PITACO millis() and the
    /// host monotonic clock. This is NOT an NTP sync and does not recover
    /// the ADC conversion instant: firmware t_ms is captured at FIFO read.
    /// Keep the lower envelope of one-way offsets so queue/USB delays
    /// cannot make delayed samples artificially fresh.
    /// No Unity, serial, SQI or physiologic processing dependency.
    /// </summary>
    public enum PpgClockObservation
    {
        Initializing,
        Tracking,
        DeviceRestart,
        DeviceWrap,
        AcquisitionGap,
        Unusable
    }

    public sealed class PpgMonotonicClockAligner
    {
        private const double DeviceWrapMilliseconds = 4294967296.0;
        private const uint WrapNearEndMs = 60000;
        private const uint WrapNearStartMs = 60000;
        private const double SampleGapThresholdMs = 750.0;
        // Allow the baseline to follow slow clock drift (~100 ppm), but do
        // not let a burst of delayed arrivals rapidly inflate the baseline.
        private const double MaximumBaselineIncreasePerMs = 0.0001;

        private bool started;
        private ushort lastSequence;
        private uint lastDeviceMs;
        private double lastArrivalHostMs;
        private double wrapCount;
        private double lastUnwrappedDeviceMs;
        private double baselineHostOffsetMs;
        private int stableObservations;

        public bool HasAlignment { get { return started && stableObservations >= 3; } }
        public int DeviceRestarts { get; private set; }
        public int DeviceWraps { get; private set; }
        public int AcquisitionGaps { get; private set; }
        public double OffsetMilliseconds { get { return started ? baselineHostOffsetMs : double.NaN; } }
        public double LastExcessArrivalDelayMilliseconds { get; private set; }
        public uint LastDeviceMilliseconds { get { return lastDeviceMs; } }
        public ushort LastSequence { get { return lastSequence; } }
        public double LastArrivalHostMilliseconds { get { return lastArrivalHostMs; } }

        public static double HostTimestampMilliseconds(long stopwatchTicks)
        {
            return stopwatchTicks > 0
                ? stopwatchTicks * 1000.0 / Stopwatch.Frequency
                : double.NaN;
        }

        public static double HostNowMilliseconds()
        {
            return HostTimestampMilliseconds(Stopwatch.GetTimestamp());
        }

        public void Clear()
        {
            started = false;
            stableObservations = 0;
            wrapCount = 0;
            lastSequence = 0;
            lastDeviceMs = 0;
            lastArrivalHostMs = 0;
            lastUnwrappedDeviceMs = 0;
            baselineHostOffsetMs = 0;
            LastExcessArrivalDelayMilliseconds = 0;
        }

        public PpgClockObservation Observe(ushort sequence, uint deviceMs,
            double arrivalHostMs)
        {
            if (double.IsNaN(arrivalHostMs) ||
                double.IsInfinity(arrivalHostMs) || arrivalHostMs < 0)
                return PpgClockObservation.Unusable;

            if (!started)
            {
                BeginEpoch(sequence, deviceMs, arrivalHostMs);
                return PpgClockObservation.Initializing;
            }

            // Packet order is preserved by the single USB serial owner.
            // A 16-bit sequence rollover is ordinary; backwards steps that
            // are not rollover signal a reset or a corrupted stream.
            int sequenceAdvance = (sequence - lastSequence + 65536) & 0xFFFF;
            bool badSequence = sequenceAdvance == 0 ||
                sequenceAdvance > 32768;
            bool backwardDevice = deviceMs < lastDeviceMs;
            bool realDeviceWrap = backwardDevice &&
                lastDeviceMs >= uint.MaxValue - WrapNearEndMs &&
                deviceMs <= WrapNearStartMs;
            bool backwardHost = arrivalHostMs + 0.001 < lastArrivalHostMs;

            if (badSequence || (backwardDevice && !realDeviceWrap) ||
                backwardHost)
            {
                ++DeviceRestarts;
                BeginEpoch(sequence, deviceMs, arrivalHostMs);
                return PpgClockObservation.DeviceRestart;
            }

            double previousDeviceMs = lastUnwrappedDeviceMs;
            double previousHostMs = lastArrivalHostMs;
            if (realDeviceWrap)
            {
                wrapCount += 1;
                ++DeviceWraps;
            }

            double unwrappedMs = deviceMs + wrapCount * DeviceWrapMilliseconds;
            double candidateOffsetMs = arrivalHostMs - unwrappedMs;
            double hostDeltaMs = Math.Max(0.0, arrivalHostMs - previousHostMs);

            // Minimum observed offset is one-way lower-envelope alignment.
            // Only a very slow increase is allowed for device oscillator drift.
            if (candidateOffsetMs < baselineHostOffsetMs)
                baselineHostOffsetMs = candidateOffsetMs;
            else
                baselineHostOffsetMs += Math.Min(
                    candidateOffsetMs - baselineHostOffsetMs,
                    hostDeltaMs * MaximumBaselineIncreasePerMs);

            lastSequence = sequence;
            lastDeviceMs = deviceMs;
            lastArrivalHostMs = arrivalHostMs;
            lastUnwrappedDeviceMs = unwrappedMs;
            if (stableObservations < 3) ++stableObservations;
            LastExcessArrivalDelayMilliseconds = Math.Max(0.0,
                candidateOffsetMs - baselineHostOffsetMs);

            if (realDeviceWrap)
                return PpgClockObservation.DeviceWrap;
            if (unwrappedMs - previousDeviceMs > SampleGapThresholdMs)
            {
                ++AcquisitionGaps;
                return PpgClockObservation.AcquisitionGap;
            }
            return PpgClockObservation.Tracking;
        }

        public double EstimateLatestSampleAgeMilliseconds(double hostNowMs)
        {
            if (!HasAlignment || double.IsNaN(hostNowMs) ||
                double.IsInfinity(hostNowMs) || hostNowMs < lastArrivalHostMs)
                return double.NaN;

            // An aligned sample cannot be newer than its actual arrival.
            // This is the age since the estimated FIFO-read timestamp,
            // not the exact analog-to-digital conversion instant.
            double alignedMs = lastUnwrappedDeviceMs + baselineHostOffsetMs;
            return Math.Max(hostNowMs - lastArrivalHostMs,
                Math.Max(0.0, hostNowMs - alignedMs));
        }

        private void BeginEpoch(ushort sequence, uint deviceMs,
            double arrivalHostMs)
        {
            started = true;
            stableObservations = 1;
            wrapCount = 0;
            lastSequence = sequence;
            lastDeviceMs = deviceMs;
            lastArrivalHostMs = arrivalHostMs;
            lastUnwrappedDeviceMs = deviceMs;
            baselineHostOffsetMs = arrivalHostMs - deviceMs;
            LastExcessArrivalDelayMilliseconds = 0;
        }
    }
}
