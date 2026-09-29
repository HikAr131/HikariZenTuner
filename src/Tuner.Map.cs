// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Globalization;

namespace HikariZenTuner
{
    // Core map checks and the session-only map override (setMap).
    public sealed partial class Tuner
    {
        public const string MapSourceFuses = "fuses";
        public const string MapSourceOverride = "override";

        // Pure: how a core map compares with what Windows reports. The fuse map and setMap share these rules.
        public static void CheckMapAgainstOs(int mapCount, List<int> perCcd, int osCores, List<int> perL3, List<string> issues)
        {
            if (mapCount != osCores)
                issues.Add("core-count-mismatch:" + mapCount.ToString(CultureInfo.InvariantCulture) + "/" + osCores.ToString(CultureInfo.InvariantCulture));

            bool sameShape = perL3.Count == perCcd.Count;
            for (int i = 0; sameShape && i < perL3.Count; i++)
            {
                if (perL3[i] != perCcd[i]) sameShape = false;
            }
            if (!sameShape) issues.Add("ccd-shape-mismatch");
        }

        // Cores per enabled CCD, lowest CCD first; an enabled CCD without cores counts as 0.
        public static List<int> CountCoresPerCcd(List<CoreSlot> map, uint ccdEnableMap)
        {
            var counts = new List<int>();
            for (int ccd = 0; ccd <= MaxCcdIndex; ccd++)
            {
                if (!IsEnabledCcd(ccd, ccdEnableMap)) continue;
                int count = 0;
                foreach (var slot in map)
                {
                    if (slot.Ccd == (uint)ccd) count++;
                }
                counts.Add(count);
            }
            return counts;
        }

        private static long SlotOrder(CoreSlot slot)
        {
            return (long)slot.Ccd * (MaxSlotIndex + 1) + slot.Slot;
        }

        private static TunerException MapRejected(string reason)
        {
            return new TunerException("BAD_REQUEST", reason);
        }

        // Pure: a replacement map is accepted only if it has the shape a fuse map would have: core indices 0..n-1
        // in the SMU's own (ccd, slot) order, enabled CCDs, slots 0-7, and the same core count and cores per CCD
        // as the physical cores and L3 groups Windows reports.
        public static List<CoreSlot> ValidateMapOverride(object cores, uint ccdEnableMap, int osCores, List<int> perL3)
        {
            var list = cores as List<object>;
            if (list == null || list.Count == 0) throw MapRejected("cores must be a non-empty array");

            var map = new List<CoreSlot>();
            var seenCores = new HashSet<long>();
            var seenSlots = new HashSet<long>();
            foreach (object item in list)
            {
                var entry = item as JObject;
                if (entry == null) throw MapRejected("each core must be an object");
                long core;
                long ccd;
                long slot;
                if (!TryGetInteger(entry, "core", out core) || !TryGetInteger(entry, "ccd", out ccd) || !TryGetInteger(entry, "slot", out slot))
                    throw MapRejected("core, ccd and slot must be integers");
                if (core < 0 || core >= list.Count) throw MapRejected("core indices must run from 0 to n-1");
                if (!seenCores.Add(core)) throw MapRejected("duplicate core " + core.ToString(CultureInfo.InvariantCulture));
                if (!IsEnabledCcd(ccd, ccdEnableMap)) throw MapRejected("ccd " + ccd.ToString(CultureInfo.InvariantCulture) + " is not enabled");
                if (slot < 0 || slot > MaxSlotIndex) throw MapRejected("slot must be 0-7");
                if (!seenSlots.Add(ccd * (MaxSlotIndex + 1) + slot)) throw MapRejected("duplicate ccd/slot");
                map.Add(new CoreSlot { Core = (int)core, Ccd = (uint)ccd, Slot = (uint)slot });
            }

            map.Sort((a, b) => a.Core.CompareTo(b.Core));
            for (int i = 1; i < map.Count; i++)
            {
                if (SlotOrder(map[i]) <= SlotOrder(map[i - 1])) throw MapRejected("cores must follow ccd/slot order");
            }

            var issues = new List<string>();
            CheckMapAgainstOs(map.Count, CountCoresPerCcd(map, ccdEnableMap), osCores, perL3, issues);
            if (issues.Count > 0) throw MapRejected(string.Join(",", issues.ToArray()));
            return map;
        }

        // Replaces this process's map only; nothing is written to the SMU or to disk.
        public JObject SetMap(JObject request)
        {
            Open();
            if (_isApu) throw MapRejected("APU cores are addressed by flat index; there is no map to replace");
            if (_os == null) throw MapRejected("os-topology-unreadable");

            uint ccdEnableMap = _cpu.info.topology.ccdEnableMap;
            var perL3 = _os.CoresPerL3();
            var map = ValidateMapOverride(request.Get("cores"), ccdEnableMap, _os.Cores.Count, perL3);

            _map.Clear();
            _map.AddRange(map);
            _mapSource = MapSourceOverride;
            _mapIssues.Clear();
            CheckMapAgainstOs(_map.Count, CountCoresPerCcd(_map, ccdEnableMap), _os.Cores.Count, perL3, _mapIssues);
            _mapTrusted = _mapIssues.Count == 0;
            return new JObject().Set("map", MapJson());
        }
    }
}
