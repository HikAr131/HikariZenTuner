// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;

namespace HikariZenTuner
{
    // Offline checks only: never touches PawnIO, the SMU or any hardware.
    public static class SelfTest
    {
        private sealed class Runner
        {
            public int Passed;
            public readonly List<object> Failures = new List<object>();

            public void Check(bool condition, string name)
            {
                if (condition) Passed++;
                else Failures.Add(name);
            }

            public void Throws(Action action, string name)
            {
                try
                {
                    action();
                    Failures.Add(name);
                }
                catch (JsonException)
                {
                    Passed++;
                }
            }

            public void Rejects(Action action, string code, string name)
            {
                try
                {
                    action();
                    Failures.Add(name);
                }
                catch (TunerException ex)
                {
                    if (ex.Code == code) Passed++;
                    else Failures.Add(name + " (got " + ex.Code + ")");
                }
            }
        }

        public static JObject Run()
        {
            var r = new Runner();

            // Margin argument: 16-bit two's complement, exactly as the SMU expects it.
            r.Check(CoEncoding.MarginArg(0) == 0x0000u, "margin 0");
            r.Check(CoEncoding.MarginArg(-1) == 0xFFFFu, "margin -1");
            r.Check(CoEncoding.MarginArg(-30) == 0xFFE2u, "margin -30");
            r.Check(CoEncoding.MarginArg(-50) == 0xFFCEu, "margin -50");
            r.Check(CoEncoding.MarginArg(5) == 0x0005u, "margin 5");
            for (int m = -60; m <= 60; m++)
            {
                r.Check(CoEncoding.MarginArg(m) == ZenStates.Core.Utils.MakePsmMarginArg(m), "library margin " + m.ToString(CultureInfo.InvariantCulture));
            }

            // Core mask: ccd in bits 31-28, slot in bits 23-20, ccx always 0 on Zen 3 and newer.
            r.Check(CoEncoding.CoreMask(0, 0) == 0x00000000u, "mask ccd0 slot0");
            r.Check(CoEncoding.CoreMask(7, 0) == 0x00700000u, "mask ccd0 slot7");
            r.Check(CoEncoding.CoreMask(3, 1) == 0x10300000u, "mask ccd1 slot3");
            r.Check(CoEncoding.CoreMask(8, 1) == 0x10000000u, "mask wraps at 8");
            r.Check((CoEncoding.CoreMask(5, 1) & 0x000FFFFFu) == 0, "mask leaves margin bits clear");

            // Readback decoding: sign-extended 32-bit and bare 16-bit answers.
            int decoded;
            r.Check(CoEncoding.TryDecodeMargin(0xFFFFFFE2u, out decoded) && decoded == -30, "decode 32-bit -30");
            r.Check(CoEncoding.TryDecodeMargin(0x0000FFE2u, out decoded) && decoded == -30, "decode 16-bit -30");
            r.Check(CoEncoding.TryDecodeMargin(0u, out decoded) && decoded == 0, "decode 0");
            r.Check(CoEncoding.TryDecodeMargin(0x0000FFCEu, out decoded) && decoded == -50, "decode 16-bit -50");
            r.Check(!CoEncoding.TryDecodeMargin(0x12345678u, out decoded), "decode rejects garbage");
            r.Check(!CoEncoding.TryDecodeMargin(0x00001000u, out decoded), "decode rejects out-of-range");

            // Limits per generation.
            r.Check(CoEncoding.MinimumFor("Vermeer") == -30, "zen3 minimum");
            r.Check(CoEncoding.MinimumFor("Raphael") == -50, "zen4 minimum");
            r.Check(CoEncoding.MinimumFor("GraniteRidge") == -50, "zen5 minimum");
            r.Check(CoEncoding.WriteSupported("GraniteRidge") && CoEncoding.WriteSupported("Raphael") && CoEncoding.WriteSupported("Vermeer"), "desktop writes");
            r.Check(!CoEncoding.WriteSupported("Phoenix") && !CoEncoding.WriteSupported("StrixPoint") && !CoEncoding.WriteSupported("Unsupported"), "apu writes refused");
            r.Check(CoEncoding.PopCount8(0xC0u) == 2 && CoEncoding.PopCount8(0u) == 0, "popcount");

            // Write spec parsing.
            JObject co;
            string error;
            r.Check(Program.TryParseCoSpec("0:-20,3:-26", out co, out error) && (long)co.Get("0") == -20 && (long)co.Get("3") == -26, "co spec");
            r.Check(!Program.TryParseCoSpec("0:-20,0:-21", out co, out error), "co spec duplicate");
            r.Check(!Program.TryParseCoSpec("x:-1", out co, out error), "co spec bad core");
            r.Check(!Program.TryParseCoSpec("1:-", out co, out error), "co spec bad value");

            // Write plan validation: runs before the bus lock, so nothing here can reach the SMU.
            var map = new List<CoreSlot>();
            for (int i = 0; i < 8; i++) map.Add(new CoreSlot { Core = i, Ccd = 0, Slot = (uint)i });
            Func<string, JObject> co1 = spec =>
            {
                JObject parsedSpec;
                string specError;
                Program.TryParseCoSpec(spec, out parsedSpec, out specError);
                return parsedSpec;
            };
            var okPlan = Tuner.BuildWritePlan(co1("3:-26,0:-20"), -50, map);
            r.Check(okPlan.Count == 2 && okPlan[0].Key.Core == 0 && okPlan[0].Value == -20 && okPlan[1].Key.Core == 3, "plan sorted");
            r.Rejects(() => Tuner.BuildWritePlan(co1("0:5"), -50, map), "OUT_OF_RANGE", "plan rejects positive");
            r.Rejects(() => Tuner.BuildWritePlan(co1("0:1"), -50, map), "OUT_OF_RANGE", "plan rejects +1");
            r.Rejects(() => Tuner.BuildWritePlan(co1("0:-51"), -50, map), "OUT_OF_RANGE", "plan rejects below -50");
            r.Rejects(() => Tuner.BuildWritePlan(co1("0:-31"), -30, map), "OUT_OF_RANGE", "plan rejects below zen3 floor");
            r.Rejects(() => Tuner.BuildWritePlan(co1("0:-60"), -80, map), "OUT_OF_RANGE", "plan clamps caller minimum to -50");
            r.Rejects(() => Tuner.BuildWritePlan(co1("99:-1"), -50, map), "UNKNOWN_CORE", "plan rejects unknown core");
            r.Rejects(() => Tuner.BuildWritePlan(new JObject().Set("0", "-1"), -50, map), "BAD_REQUEST", "plan rejects string value");
            r.Rejects(() => Tuner.BuildWritePlan(new JObject().Set("0", -1.5), -50, map), "BAD_REQUEST", "plan rejects float value");
            r.Rejects(() => Tuner.BuildWritePlan(new JObject().Set("-1", -1L), -50, map), "BAD_REQUEST", "plan rejects negative core");
            r.Rejects(() => Tuner.BuildWritePlan(new JObject().Set("0", -1L).Set("00", -2L), -50, map), "BAD_REQUEST", "plan rejects aliased core");
            r.Rejects(() => Tuner.BuildWritePlan(new JObject(), -50, map), "BAD_REQUEST", "plan rejects empty");

            ProbePlanChecks(r);
            MapOverrideChecks(r);
            PmTableHeadChecks(r);

            // JSON codec.
            var parsed = Json.Parse("{\"cmd\":\"write\",\"id\":7,\"co\":{\"0\":-20},\"flag\":true,\"n\":null,\"f\":1.5,\"s\":\"a\\\"b\"}") as JObject;
            r.Check(parsed != null && (string)parsed.Get("cmd") == "write", "json string");
            r.Check(parsed != null && (long)parsed.Get("id") == 7, "json integer");
            r.Check(parsed != null && (bool)parsed.Get("flag"), "json bool");
            r.Check(parsed != null && parsed.Get("n") == null, "json null");
            r.Check(parsed != null && Math.Abs((double)parsed.Get("f") - 1.5) < 1e-9, "json float");
            r.Check(parsed != null && (string)parsed.Get("s") == "a\"b", "json escape");
            r.Throws(() => Json.Parse("{"), "json unterminated");
            r.Throws(() => Json.Parse("{\"a\":1,}"), "json trailing comma");
            r.Throws(() => Json.Parse("[1 2]"), "json missing comma");
            r.Throws(() => Json.Parse("{\"a\":1}{"), "json trailing data");
            r.Throws(() => Json.Parse("{\"a\":1,\"a\":2}"), "json duplicate key");
            r.Throws(() => Json.Parse("\"a" + (char)1 + "\""), "json control char");
            r.Throws(() => Json.Parse(new string('[', 40) + new string(']', 40)), "json depth");
            r.Throws(() => Json.Parse("01"), "json leading zero");
            r.Throws(() => Json.Parse("99999999999999999999"), "json integer overflow");

            string text = Json.Serialize(new JObject().Set("a", "x" + (char)0x4E2D + "y").Set("b", new List<object> { 1, -2L, true, null }));
            bool ascii = true;
            foreach (char c in text) if (c > 0x7E || c < 0x20) ascii = false;
            r.Check(ascii, "json output is ascii");
            var roundTrip = Json.Parse(text) as JObject;
            r.Check(roundTrip != null && (string)roundTrip.Get("a") == "x" + (char)0x4E2D + "y", "json round trip");

            return new JObject()
                .Set("ok", r.Failures.Count == 0)
                .Set("cmd", "self-test")
                .Set("passed", r.Passed)
                .Set("failed", r.Failures.Count)
                .Set("failures", r.Failures);
        }

        // Triples of (ccd, slot, probe) as a parsed JSON array would hold them.
        private static List<object> SlotList(params long[] triples)
        {
            var list = new List<object>();
            for (int i = 0; i + 2 < triples.Length; i += 3)
                list.Add(new JObject().Set("ccd", triples[i]).Set("slot", triples[i + 1]).Set("probe", triples[i + 2]));
            return list;
        }

        // Triples of (core, ccd, slot) as a parsed JSON array would hold them.
        private static List<object> CoreList(params long[] triples)
        {
            var list = new List<object>();
            for (int i = 0; i + 2 < triples.Length; i += 3)
                list.Add(new JObject().Set("core", triples[i]).Set("ccd", triples[i + 1]).Set("slot", triples[i + 2]));
            return list;
        }

        // Probe validation runs before the bus lock, so nothing here can reach the SMU.
        private static void ProbePlanChecks(Runner r)
        {
            var plan = Tuner.BuildProbePlan(SlotList(1, 7, -1, 0, 3, -2, 0, 0, -1), -50, 0x3u);
            r.Check(plan.Count == 3
                && plan[0].Ccd == 0 && plan[0].Slot == 0 && plan[0].Probe == -1
                && plan[1].Ccd == 0 && plan[1].Slot == 3 && plan[1].Probe == -2
                && plan[2].Ccd == 1 && plan[2].Slot == 7, "probe plan sorted");
            r.Check(Tuner.BuildProbePlan(SlotList(0, 0, 0, 0, 1, -50), -50, 0x1u).Count == 2, "probe accepts 0 and the floor");
            r.Check(Tuner.BuildProbePlan(Json.Parse("[{\"ccd\":0,\"slot\":5,\"probe\":-1}]"), -50, 0x1u).Count == 1, "probe accepts parsed json");

            var sixtyFour = new List<long>();
            for (int ccd = 0; ccd < 8; ccd++)
            {
                for (int slot = 0; slot < 8; slot++) sixtyFour.AddRange(new long[] { ccd, slot, -1 });
            }
            r.Check(Tuner.BuildProbePlan(SlotList(sixtyFour.ToArray()), -50, 0xFFFFu).Count == 64, "probe accepts 64 slots");
            sixtyFour.AddRange(new long[] { 8, 0, -1 });
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(sixtyFour.ToArray()), -50, 0xFFFFu), "BAD_REQUEST", "probe rejects 65 slots");

            r.Rejects(() => Tuner.BuildProbePlan(null, -50, 0x1u), "BAD_REQUEST", "probe rejects missing slots");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object>(), -50, 0x1u), "BAD_REQUEST", "probe rejects empty");
            r.Rejects(() => Tuner.BuildProbePlan(new JObject().Set("ccd", 0L), -50, 0x1u), "BAD_REQUEST", "probe rejects an object");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { 1L }, -50, 0x1u), "BAD_REQUEST", "probe rejects a bare number");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { new JObject().Set("ccd", 0L).Set("slot", 1L) }, -50, 0x1u), "BAD_REQUEST", "probe rejects missing probe");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { new JObject().Set("ccd", 0L).Set("slot", 1L).Set("probe", -1.5) }, -50, 0x1u), "BAD_REQUEST", "probe rejects float");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { new JObject().Set("ccd", 0L).Set("slot", 1L).Set("probe", "-1") }, -50, 0x1u), "BAD_REQUEST", "probe rejects string");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { new JObject().Set("ccd", "0").Set("slot", 1L).Set("probe", -1L) }, -50, 0x1u), "BAD_REQUEST", "probe rejects string ccd");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(1, 0, -1), -50, 0x1u), "BAD_REQUEST", "probe rejects disabled ccd");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(-1, 0, -1), -50, 0x1u), "BAD_REQUEST", "probe rejects negative ccd");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(16, 0, -1), -50, 0xFFFFFFFFu), "BAD_REQUEST", "probe rejects ccd beyond the mask");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, 8, -1), -50, 0x1u), "BAD_REQUEST", "probe rejects slot 8");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, -1, -1), -50, 0x1u), "BAD_REQUEST", "probe rejects negative slot");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, 1, 1), -50, 0x1u), "OUT_OF_RANGE", "probe rejects +1");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, 1, -51), -50, 0x1u), "OUT_OF_RANGE", "probe rejects below -50");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, 1, -31), -30, 0x1u), "OUT_OF_RANGE", "probe rejects below zen3 floor");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, 1, -60), -80, 0x1u), "OUT_OF_RANGE", "probe clamps caller minimum to -50");
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, 1, -1, 0, 1, -2), -50, 0x1u), "BAD_REQUEST", "probe rejects duplicate slot");
            // One bad entry anywhere refuses the whole request.
            r.Rejects(() => Tuner.BuildProbePlan(SlotList(0, 0, -1, 0, 1, -1, 0, 9, -1), -50, 0x1u), "BAD_REQUEST", "probe rejects whole request");

            // Optional alternate: same range rules as probe, must differ from it; missing and null both mean none.
            Func<object, JObject> withAlternate = alternate => new JObject().Set("ccd", 0L).Set("slot", 2L).Set("probe", -1L).Set("alternate", alternate);
            var alt = Tuner.BuildProbePlan(new List<object> { withAlternate(-2L) }, -50, 0x1u);
            r.Check(alt.Count == 1 && alt[0].Probe == -1 && alt[0].Alternate == -2, "probe keeps alternate");
            r.Check(Tuner.BuildProbePlan(SlotList(0, 2, -1), -50, 0x1u)[0].Alternate == null, "probe without alternate");
            r.Check(Tuner.BuildProbePlan(new List<object> { withAlternate(null) }, -50, 0x1u)[0].Alternate == null, "probe null alternate");
            r.Check(Tuner.BuildProbePlan(Json.Parse("[{\"ccd\":0,\"slot\":5,\"probe\":-1,\"alternate\":0}]"), -50, 0x1u)[0].Alternate == 0, "probe alternate 0 from json");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { withAlternate(-1L) }, -50, 0x1u), "BAD_REQUEST", "probe rejects alternate equal to probe");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { withAlternate(1L) }, -50, 0x1u), "OUT_OF_RANGE", "probe rejects alternate +1");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { withAlternate(-51L) }, -50, 0x1u), "OUT_OF_RANGE", "probe rejects alternate below -50");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { withAlternate(-31L) }, -30, 0x1u), "OUT_OF_RANGE", "probe rejects alternate below zen3 floor");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { withAlternate(-2.0) }, -50, 0x1u), "BAD_REQUEST", "probe rejects float alternate");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { withAlternate("-2") }, -50, 0x1u), "BAD_REQUEST", "probe rejects string alternate");
            r.Rejects(() => Tuner.BuildProbePlan(new List<object> { withAlternate(true) }, -50, 0x1u), "BAD_REQUEST", "probe rejects bool alternate");

            // Which value gets written once the slot's current value is known.
            var step = new ProbeStep { Ccd = 0, Slot = 2, Probe = -1, Alternate = -2 };
            var bare = new ProbeStep { Ccd = 0, Slot = 2, Probe = -1 };
            r.Check(Tuner.ProbeValueFor(step, 0) == -1 && Tuner.ProbeValueFor(bare, -20) == -1, "probe written when it differs");
            r.Check(Tuner.ProbeValueFor(step, -2) == -1, "probe written when only the alternate matches");
            r.Check(Tuner.ProbeValueFor(step, -1) == -2, "alternate written when probe equals before");
            r.Check(Tuner.ProbeValueFor(bare, -1) == null, "nothing written when probe equals before");
        }

        // The replacement map must look exactly like a fuse map would.
        private static void MapOverrideChecks(Runner r)
        {
            var one = new List<int> { 8 };
            var eight = CoreList(0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 7, 0, 7);
            var accepted = Tuner.ValidateMapOverride(eight, 0x1u, 8, one);
            r.Check(accepted.Count == 8 && accepted[0].Core == 0 && accepted[7].Core == 7 && accepted[7].Slot == 7, "map accepts the fuse layout");

            // Two CCDs with two slots fused off in each, in scrambled request order.
            var six = new List<int> { 6, 6 };
            var gaps = CoreList(
                7, 1, 2, 0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 4, 4, 0, 5, 5, 0, 6,
                6, 1, 1, 11, 1, 7, 8, 1, 3, 9, 1, 5, 10, 1, 6);
            var sorted = Tuner.ValidateMapOverride(gaps, 0x3u, 12, six);
            r.Check(sorted.Count == 12 && sorted[3].Slot == 4 && sorted[6].Ccd == 1 && sorted[6].Slot == 1 && sorted[11].Slot == 7, "map accepts gaps and sorts by core");

            r.Rejects(() => Tuner.ValidateMapOverride(null, 0x1u, 8, one), "BAD_REQUEST", "map rejects missing cores");
            r.Rejects(() => Tuner.ValidateMapOverride(new List<object>(), 0x1u, 8, one), "BAD_REQUEST", "map rejects empty");
            r.Rejects(() => Tuner.ValidateMapOverride(new List<object> { 0L }, 0x1u, 1, new List<int> { 1 }), "BAD_REQUEST", "map rejects a bare number");
            r.Rejects(() => Tuner.ValidateMapOverride(new List<object> { new JObject().Set("core", 0L).Set("ccd", 0L).Set("slot", 0.0) }, 0x1u, 1, new List<int> { 1 }), "BAD_REQUEST", "map rejects float");
            r.Rejects(() => Tuner.ValidateMapOverride(CoreList(0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 8, 0, 7), 0x1u, 8, one), "BAD_REQUEST", "map rejects a gap in core indices");
            r.Rejects(() => Tuner.ValidateMapOverride(CoreList(0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 6, 0, 7), 0x1u, 8, one), "BAD_REQUEST", "map rejects duplicate core");
            r.Rejects(() => Tuner.ValidateMapOverride(CoreList(0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 7, 0, 6), 0x1u, 8, one), "BAD_REQUEST", "map rejects duplicate slot");
            r.Rejects(() => Tuner.ValidateMapOverride(CoreList(0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 7, 1, 7), 0x1u, 8, one), "BAD_REQUEST", "map rejects disabled ccd");
            r.Rejects(() => Tuner.ValidateMapOverride(CoreList(0, 0, 1, 1, 0, 2, 2, 0, 3, 3, 0, 4, 4, 0, 5, 5, 0, 6, 6, 0, 7, 7, 0, 8), 0x1u, 8, one), "BAD_REQUEST", "map rejects slot 8");
            r.Rejects(() => Tuner.ValidateMapOverride(CoreList(0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6), 0x1u, 8, one), "BAD_REQUEST", "map rejects core count");
            r.Rejects(() => Tuner.ValidateMapOverride(CoreList(0, 0, 1, 1, 0, 0, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 7, 0, 7), 0x1u, 8, one), "BAD_REQUEST", "map rejects slot order");
            r.Rejects(() => Tuner.ValidateMapOverride(
                CoreList(0, 1, 0, 1, 1, 1, 2, 1, 2, 3, 1, 3, 4, 0, 0, 5, 0, 1, 6, 0, 2, 7, 0, 3),
                0x3u, 8, new List<int> { 4, 4 }), "BAD_REQUEST", "map rejects ccd order");
            r.Rejects(() => Tuner.ValidateMapOverride(
                CoreList(0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 1, 0, 7, 1, 1),
                0x3u, 8, new List<int> { 4, 4 }), "BAD_REQUEST", "map rejects ccd shape");
            r.Rejects(() => Tuner.ValidateMapOverride(eight, 0x3u, 8, one), "BAD_REQUEST", "map rejects an enabled ccd without cores");

            var issues = new List<string>();
            Tuner.CheckMapAgainstOs(8, new List<int> { 8 }, 8, new List<int> { 8 }, issues);
            r.Check(issues.Count == 0, "os check passes a matching map");
            Tuner.CheckMapAgainstOs(7, new List<int> { 4, 3 }, 8, new List<int> { 4, 4 }, issues);
            r.Check(issues.Count == 2 && issues[0] == "core-count-mismatch:7/8" && issues[1] == "ccd-shape-mismatch", "os check names both issues");
            r.Check(Tuner.CountCoresPerCcd(Tuner.ValidateMapOverride(gaps, 0x3u, 12, six), 0x3u).Count == 2, "cores per ccd");
        }

        // PM table head: raw floats only; nothing is labelled, non-finite or absurd values become null.
        private static void PmTableHeadChecks(Runner r)
        {
            var table = new float[40];
            for (int i = 0; i < table.Length; i++) table[i] = i;
            float[] special = { 162f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2e6f, -2e6f, 1e6f, 45.5f, 0.1f };
            Array.Copy(special, table, special.Length);
            var head = Tuner.PmTableHead(table);
            r.Check(head.Count == 32 && head[31] is double && (double)head[31] == 31.0, "pm head length");
            r.Check(head[0] is double && (double)head[0] == 162.0, "pm head value");
            r.Check(head[1] == null && head[2] == null && head[3] == null, "pm head non-finite");
            r.Check(head[4] == null && head[5] == null, "pm head beyond 1e6");
            r.Check(head[6] is double && (double)head[6] == 1e6, "pm head keeps 1e6");
            r.Check(head[8] is double && (double)head[8] == 0.1, "pm head shortest decimal");
            r.Check(Tuner.PmTableHead(new float[] { 1f, 2f }).Count == 2, "pm head short table");
            r.Check(Tuner.PmTableHead(null).Count == 0, "pm head no table");
            r.Check(Json.Serialize(Tuner.PmTableHead(new float[] { 162f, float.NaN, 45.5f, 0.1f })) == "[162,null,45.5,0.1]", "pm head json");
        }
    }
}
