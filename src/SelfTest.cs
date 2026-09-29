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
    }
}
