// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace HikariZenTuner
{
    // Pure encodings shared by the self-test. Actual SMU calls always go through
    // ZenStates-Core (Cpu.MakeCoreMask / Utils.MakePsmMarginArg); these mirror them so the
    // self-test can prove both sides agree bit for bit.
    public static class CoEncoding
    {
        public const int AbsoluteMin = -50;
        public const int Zen3Min = -30;
        public const int AbsoluteMax = 0;
        public const int PlausibleReadLimit = 60;

        // Zen 3 and newer: [31-28] ccd, [27-24] ccx (always 0), [23-20] core slot within the ccd.
        public static uint CoreMask(uint slot, uint ccd)
        {
            return (ccd << 28) | ((slot % 8) << 20);
        }

        public static uint MarginArg(int margin)
        {
            int offset = margin < 0 ? 0x100000 : 0;
            return (uint)(offset + margin) & 0xffff;
        }

        // The SMU answers either a sign-extended 32-bit value or a bare 16-bit field.
        public static bool TryDecodeMargin(uint raw, out int margin)
        {
            int wide = unchecked((int)raw);
            if (wide >= -PlausibleReadLimit && wide <= PlausibleReadLimit)
            {
                margin = wide;
                return true;
            }
            if ((raw & 0xFFFF0000u) == 0)
            {
                int narrow = unchecked((short)(raw & 0xFFFF));
                if (narrow >= -PlausibleReadLimit && narrow <= PlausibleReadLimit)
                {
                    margin = narrow;
                    return true;
                }
            }
            margin = 0;
            return false;
        }

        public static int MinimumFor(string codename)
        {
            return IsZen3(codename) ? Zen3Min : AbsoluteMin;
        }

        public static bool IsZen3(string codename)
        {
            return string.Equals(codename, "Vermeer", StringComparison.Ordinal)
                || string.Equals(codename, "Cezanne", StringComparison.Ordinal)
                || string.Equals(codename, "Chagall", StringComparison.Ordinal)
                || string.Equals(codename, "Milan", StringComparison.Ordinal)
                || string.Equals(codename, "Rembrandt", StringComparison.Ordinal);
        }

        // Desktop-silicon parts whose per-core write path is the plain ccd/slot mask.
        public static bool WriteSupported(string codename)
        {
            return string.Equals(codename, "Vermeer", StringComparison.Ordinal)
                || string.Equals(codename, "Raphael", StringComparison.Ordinal)
                || string.Equals(codename, "DragonRange", StringComparison.Ordinal)
                || string.Equals(codename, "GraniteRidge", StringComparison.Ordinal);
        }

        public static int PopCount8(uint value)
        {
            int count = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                if (((value >> bit) & 1u) == 1u) count++;
            }
            return count;
        }
    }
}
