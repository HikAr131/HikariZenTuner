// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Globalization;

namespace HikariZenTuner
{
    // One slot of a probeSlots request: write Probe there (Alternate when the slot already holds Probe),
    // read it back, then put the old value back.
    public sealed class ProbeStep
    {
        public uint Ccd;
        public uint Slot;
        public int Probe;
        public int? Alternate;
    }

    // probeSlots: executes the probe and reports what the SMU answered. Deciding which slots
    // are real cores is left to the caller.
    public sealed partial class Tuner
    {
        public const int MaxProbeSlots = 64;

        // Pure: every probe request is fully validated here, before the bus lock and before any SMU call.
        public static List<ProbeStep> BuildProbePlan(object slots, int minimum, uint ccdEnableMap)
        {
            var list = slots as List<object>;
            if (list == null || list.Count == 0) throw new TunerException("BAD_REQUEST", "slots must be a non-empty array");
            if (list.Count > MaxProbeSlots) throw new TunerException("BAD_REQUEST", "at most 64 slots per request");
            if (minimum < CoEncoding.AbsoluteMin) minimum = CoEncoding.AbsoluteMin;

            var plan = new List<ProbeStep>();
            var seen = new HashSet<long>();
            foreach (object item in list)
            {
                var entry = item as JObject;
                if (entry == null) throw new TunerException("BAD_REQUEST", "each slot must be an object");
                long ccd;
                long slot;
                long probe;
                if (!TryGetInteger(entry, "ccd", out ccd) || !TryGetInteger(entry, "slot", out slot) || !TryGetInteger(entry, "probe", out probe))
                    throw new TunerException("BAD_REQUEST", "ccd, slot and probe must be integers");
                if (!IsEnabledCcd(ccd, ccdEnableMap)) throw new TunerException("BAD_REQUEST", "ccd " + ccd.ToString(CultureInfo.InvariantCulture) + " is not enabled");
                if (slot < 0 || slot > MaxSlotIndex) throw new TunerException("BAD_REQUEST", "slot must be 0-7");
                if (probe < minimum || probe > CoEncoding.AbsoluteMax)
                    throw new TunerException("OUT_OF_RANGE", "ccd " + ccd.ToString(CultureInfo.InvariantCulture) + " slot " + slot.ToString(CultureInfo.InvariantCulture) + " probe " + probe.ToString(CultureInfo.InvariantCulture));

                // Optional; a missing key and null both mean "no alternate".
                int? alternate = null;
                object alternateValue = entry.Get("alternate");
                if (alternateValue != null)
                {
                    if (!(alternateValue is long)) throw new TunerException("BAD_REQUEST", "alternate must be an integer");
                    long value = (long)alternateValue;
                    if (value < minimum || value > CoEncoding.AbsoluteMax)
                        throw new TunerException("OUT_OF_RANGE", "ccd " + ccd.ToString(CultureInfo.InvariantCulture) + " slot " + slot.ToString(CultureInfo.InvariantCulture) + " alternate " + value.ToString(CultureInfo.InvariantCulture));
                    if (value == probe) throw new TunerException("BAD_REQUEST", "alternate must differ from probe");
                    alternate = (int)value;
                }

                if (!seen.Add(ccd * (MaxSlotIndex + 1) + slot)) throw new TunerException("BAD_REQUEST", "duplicate ccd/slot");
                plan.Add(new ProbeStep { Ccd = (uint)ccd, Slot = (uint)slot, Probe = (int)probe, Alternate = alternate });
            }
            plan.Sort((a, b) => a.Ccd != b.Ccd ? a.Ccd.CompareTo(b.Ccd) : a.Slot.CompareTo(b.Slot));
            return plan;
        }

        // Pure: the value to write once the slot's current value is known; null means leave the slot alone.
        public static int? ProbeValueFor(ProbeStep step, int before)
        {
            if (before != step.Probe) return step.Probe;
            return step.Alternate;
        }

        private static JObject ProbeEntry(ProbeStep step)
        {
            // Every key up front so each result has the same shape; unknown facts stay null.
            // "probe" is the value actually written, so it stays null when nothing was written.
            return new JObject()
                .Set("ccd", (int)step.Ccd)
                .Set("slot", (int)step.Slot)
                .Set("probe", null)
                .Set("readable", null)
                .Set("before", null)
                .Set("beforeRaw", null)
                .Set("probeAccepted", null)
                .Set("probeReadback", null)
                .Set("restoreAccepted", null)
                .Set("restoreReadback", null)
                .Set("restoreAttempts", null)
                .Set("restored", null)
                .Set("code", null);
        }

        public JObject ProbeSlots(JObject request)
        {
            Open();
            CheckExpectations(request);
            if (_isApu || !CoEncoding.WriteSupported(Codename))
                throw new TunerException("WRITE_UNSUPPORTED_CPU", "per-core writes are not offered for " + Codename);
            if (!CanReadCo || !CanWriteCo)
                throw new TunerException("CO_UNSUPPORTED", "this SMU lacks the per-core Curve Optimizer read or write command");

            var plan = BuildProbePlan(request.Get("slots"), CoEncoding.MinimumFor(Codename), _cpu.info.topology.ccdEnableMap);

            var results = new List<object>();
            bool halted = false;
            using (AcquireBus())
            {
                if (!_cpu.SendTestMessage()) throw new TunerException("SMU_NOT_RESPONDING", "SMU did not answer the test message");

                foreach (var step in plan)
                {
                    var entry = ProbeEntry(step);
                    results.Add(entry);
                    if (halted)
                    {
                        entry.Set("code", "SKIPPED");
                        continue;
                    }

                    string readCode;
                    uint? beforeRaw = ReadSlotRaw(step.Ccd, step.Slot);
                    int? before = DecodeMargin(beforeRaw, out readCode);
                    entry.Set("readable", before.HasValue).Set("before", before).Set("beforeRaw", RawText(beforeRaw));
                    if (!before.HasValue)
                    {
                        // Without the old value there is nothing to restore, so this slot is never written.
                        entry.Set("restoreAttempts", 0).Set("code", readCode);
                        continue;
                    }

                    int? written = ProbeValueFor(step, before.Value);
                    if (!written.HasValue)
                    {
                        // The slot already holds the probe value and no alternate was given: writing it would prove nothing.
                        entry.Set("restoreAttempts", 0).Set("restored", true).Set("code", "PROBE_EQUALS_BEFORE");
                        continue;
                    }

                    string ignored;
                    entry.Set("probe", written.Value);
                    bool probeAccepted = WriteSlot(step.Ccd, step.Slot, written.Value);
                    int? probeReadback = DecodeMargin(ReadSlotRaw(step.Ccd, step.Slot), out ignored);

                    int attempts = 1;
                    bool restoreAccepted = WriteSlot(step.Ccd, step.Slot, before.Value);
                    int? restoreReadback = DecodeMargin(ReadSlotRaw(step.Ccd, step.Slot), out ignored);
                    if (!restoreReadback.HasValue || restoreReadback.Value != before.Value)
                    {
                        attempts = 2;
                        restoreAccepted = WriteSlot(step.Ccd, step.Slot, before.Value);
                        restoreReadback = DecodeMargin(ReadSlotRaw(step.Ccd, step.Slot), out ignored);
                    }
                    bool restored = restoreReadback.HasValue && restoreReadback.Value == before.Value;

                    entry.Set("probeAccepted", probeAccepted)
                        .Set("probeReadback", probeReadback)
                        .Set("restoreAccepted", restoreAccepted)
                        .Set("restoreReadback", restoreReadback)
                        .Set("restoreAttempts", attempts)
                        .Set("restored", restored);
                    if (!restored)
                    {
                        entry.Set("code", "RESTORE_FAILED");
                        halted = true;
                    }
                }
            }

            return new JObject().Set("results", results).Set("halted", halted);
        }
    }
}
