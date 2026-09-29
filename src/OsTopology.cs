// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HikariZenTuner
{
    // What Windows itself reports: physical cores in logical-processor order and the
    // L3 groups they share. Used only to decide whether the SMU fuse map can be trusted.
    public sealed class OsTopology
    {
        public sealed class Core
        {
            public int Index;
            public List<int> LogicalProcessors = new List<int>();
            public int FirstLogical => LogicalProcessors.Count > 0 ? LogicalProcessors[0] : int.MaxValue;
        }

        public readonly List<Core> Cores = new List<Core>();
        public readonly List<List<int>> L3Groups = new List<List<int>>();
        public int LogicalProcessorCount;

        private const int RelationProcessorCore = 0;
        private const int RelationCache = 2;
        private const int RelationAll = 0xFFFF;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

        public static OsTopology Read()
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref length);
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer || length == 0) throw new Win32Exception(error);

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return Parse(buffer, (int)length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static List<int> ReadGroupMasks(IntPtr entry, int countOffset, int masksOffset, bool countMayBeZero)
        {
            int groupCount = Marshal.ReadInt16(entry, countOffset);
            if (groupCount <= 0 && countMayBeZero) groupCount = 1;
            var result = new List<int>();
            for (int g = 0; g < groupCount; g++)
            {
                IntPtr affinity = entry + masksOffset + g * 16;
                ulong mask = unchecked((ulong)Marshal.ReadInt64(affinity));
                int group = Marshal.ReadInt16(affinity, 8);
                for (int bit = 0; bit < 64; bit++)
                {
                    if (((mask >> bit) & 1UL) == 1UL) result.Add(group * 64 + bit);
                }
            }
            result.Sort();
            return result;
        }

        private static OsTopology Parse(IntPtr buffer, int length)
        {
            var topology = new OsTopology();
            int offset = 0;
            while (offset + 8 <= length)
            {
                IntPtr entry = buffer + offset;
                int relationship = Marshal.ReadInt32(entry, 0);
                int size = Marshal.ReadInt32(entry, 4);
                if (size <= 0 || offset + size > length) break;
                IntPtr body = entry + 8;

                if (relationship == RelationProcessorCore)
                {
                    // PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass, Reserved[20], GroupCount, GroupMask[]
                    var core = new Core { LogicalProcessors = ReadGroupMasks(body, 22, 24, false) };
                    if (core.LogicalProcessors.Count > 0) topology.Cores.Add(core);
                }
                else if (relationship == RelationCache)
                {
                    // CACHE_RELATIONSHIP: Level, Associativity, LineSize, CacheSize, Type, Reserved[18], GroupCount, GroupMask[]
                    byte level = Marshal.ReadByte(body, 0);
                    if (level == 3)
                    {
                        var lps = ReadGroupMasks(body, 30, 32, true);
                        if (lps.Count > 0) topology.L3Groups.Add(lps);
                    }
                }
                offset += size;
            }

            topology.Cores.Sort((a, b) => a.FirstLogical.CompareTo(b.FirstLogical));
            for (int i = 0; i < topology.Cores.Count; i++)
            {
                topology.Cores[i].Index = i;
                topology.LogicalProcessorCount += topology.Cores[i].LogicalProcessors.Count;
            }
            topology.L3Groups.Sort((a, b) => a[0].CompareTo(b[0]));
            return topology;
        }

        // Number of physical cores whose logical processors fall inside each L3 group, in order.
        public List<int> CoresPerL3()
        {
            var counts = new List<int>();
            foreach (var group in L3Groups)
            {
                var set = new HashSet<int>(group);
                int count = 0;
                foreach (var core in Cores)
                {
                    if (set.Contains(core.FirstLogical)) count++;
                }
                counts.Add(count);
            }
            return counts;
        }
    }
}
