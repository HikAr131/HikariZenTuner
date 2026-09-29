// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ZenStates.Core;
using ZenStates.Core.Hardware;
using ZenStates.Core.Hardware.MutexLock;

namespace HikariZenTuner
{
    public sealed class TunerException : Exception
    {
        public string Code { get; }

        public TunerException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    // One physical core as the SMU addresses it: logical order index, die, slot within the die.
    public sealed class CoreSlot
    {
        public int Core;
        public uint Ccd;
        public uint Slot;
    }

    public sealed partial class Tuner : IDisposable
    {
        public const int BusLockTimeoutMs = 5000;
        public const int MaxSlotIndex = 7;
        // The SMU core mask carries the CCD index in four bits.
        public const int MaxCcdIndex = 15;
        public const int PmTableHeadLength = 32;
        public const double PmTableValueLimit = 1e6;

        private static readonly string[] ForbiddenSiblings = { "inpoutx64.dll", "WinIo32.dll", "WinIo32.sys", "inpout32.dll" };

        private Cpu _cpu;
        private OsTopology _os;
        private string _osError;
        private readonly List<CoreSlot> _map = new List<CoreSlot>();
        private readonly List<string> _mapIssues = new List<string>();
        private bool _mapTrusted;
        private string _mapSource = MapSourceFuses;
        private bool _isApu;

        public bool IsOpen => _cpu != null;

        public static JObject PawnIoState()
        {
            var state = new JObject();
            Version version = null;
            try { version = ZenStates.Core.PawnIo.PawnIo.Version; } catch { version = null; }
            state.Set("installed", version != null);
            state.Set("version", version != null ? version.ToString() : null);
            return state;
        }

        public void Open()
        {
            if (_cpu != null) return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            foreach (string name in ForbiddenSiblings)
            {
                if (File.Exists(Path.Combine(baseDir, name)))
                    throw new TunerException("UNEXPECTED_FILE", "refusing to start: " + name + " sits next to the helper");
            }

            bool pawnInstalled;
            try { pawnInstalled = ZenStates.Core.PawnIo.PawnIo.IsInstalled; } catch { pawnInstalled = false; }
            if (!pawnInstalled) throw new TunerException("PAWNIO_NOT_INSTALLED", "PawnIO is not installed.");

            try
            {
                _cpu = new Cpu();
            }
            catch (Exception ex)
            {
                string message = ex.Message ?? string.Empty;
                if (message.IndexOf("Not an AMD CPU", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new TunerException("NOT_AMD", message);
                if (message.IndexOf("PawnIO", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new TunerException("PAWNIO_MODULE_LOAD_FAILED", message);
                throw new TunerException("INIT_FAILED", message);
            }

            _isApu = _cpu.smu != null
                && _cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0
                && _cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2;

            try
            {
                _os = OsTopology.Read();
            }
            catch (Exception ex)
            {
                _os = null;
                _osError = ex.Message;
            }

            BuildMap();
        }

        public void Dispose()
        {
            var cpu = _cpu;
            _cpu = null;
            if (cpu != null)
            {
                try { cpu.Dispose(); } catch { }
            }
        }

        private string Codename => _cpu.info.codeName.ToString();

        private void BuildMap()
        {
            _map.Clear();
            _mapIssues.Clear();
            _mapTrusted = false;
            _mapSource = MapSourceFuses;

            int osCores = _os != null ? _os.Cores.Count : 0;
            if (_os == null) _mapIssues.Add("os-topology-unreadable");

            if (_isApu)
            {
                // APUs address cores by flat index on reads; writes are not offered here.
                int count = osCores > 0 ? osCores : (int)_cpu.info.topology.cores;
                for (int core = 0; core < count; core++)
                    _map.Add(new CoreSlot { Core = core, Ccd = 0, Slot = (uint)core });
                _mapIssues.Add("apu-flat-index");
                return;
            }

            var topology = _cpu.info.topology;
            if (topology.coreDisableMap == null || topology.ccds == 0)
            {
                _mapIssues.Add("fuse-map-unreadable");
                return;
            }

            var perCcd = new List<int>();
            int next = 0;
            for (int i = 0; i < topology.coreDisableMap.Length; i++)
            {
                if (((topology.ccdEnableMap >> i) & 1u) != 1u)
                {
                    _mapIssues.Add("ccd-" + i.ToString(CultureInfo.InvariantCulture) + "-not-enabled");
                    continue;
                }
                uint disabled = topology.coreDisableMap[i] & 0xFFu;
                int enabledHere = 0;
                for (uint slot = 0; slot < 8; slot++)
                {
                    if (((disabled >> (int)slot) & 1u) == 1u) continue;
                    _map.Add(new CoreSlot { Core = next++, Ccd = (uint)i, Slot = slot });
                    enabledHere++;
                }
                perCcd.Add(enabledHere);
            }

            if (_map.Count == 0) _mapIssues.Add("no-enabled-slots");
            if (_os != null) CheckMapAgainstOs(_map.Count, perCcd, osCores, _os.CoresPerL3(), _mapIssues);

            _mapTrusted = _mapIssues.Count == 0;
        }

        private JObject MapJson()
        {
            var cores = new List<object>();
            foreach (var slot in _map)
            {
                cores.Add(new JObject()
                    .Set("core", slot.Core)
                    .Set("ccd", (int)slot.Ccd)
                    .Set("slot", (int)slot.Slot));
            }
            var issues = new List<object>();
            foreach (var issue in _mapIssues) issues.Add(issue);
            return new JObject()
                .Set("source", _mapSource)
                .Set("trusted", _mapTrusted)
                .Set("issues", issues)
                .Set("cores", cores);
        }

        private static string SmuVersionText(uint version)
        {
            return ((version >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture) + "."
                + ((version >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture) + "."
                + (version & 0xFF).ToString(CultureInfo.InvariantCulture);
        }

        private bool CanReadCo => _cpu.smu != null && _cpu.smu.Rsmu != null && _cpu.smu.Rsmu.SMU_MSG_GetDldoPsmMargin != 0;

        private bool CanWriteCo => _cpu.smu != null
            && ((_cpu.smu.Rsmu != null && _cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
                || (_cpu.smu.Mp1Smu != null && _cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0));

        public JObject Identify()
        {
            Open();
            var info = _cpu.info;
            var topology = info.topology;

            var osJson = new JObject();
            if (_os != null)
            {
                var perL3 = new List<object>();
                foreach (int count in _os.CoresPerL3()) perL3.Add(count);
                osJson.Set("physicalCores", _os.Cores.Count)
                    .Set("logicalProcessors", _os.LogicalProcessorCount)
                    .Set("coresPerL3", perL3);
            }
            else
            {
                osJson.Set("error", _osError);
            }

            var disableMap = new List<object>();
            if (topology.coreDisableMap != null)
            {
                foreach (uint value in topology.coreDisableMap) disableMap.Add((long)value);
            }

            bool testMessage = false;
            try { testMessage = _cpu.SendTestMessage(); } catch { testMessage = false; }

            var caps = new JObject();
            try
            {
                var oc = _cpu.GetOverclockingCaps();
                caps.Set("overclockingEnabled", oc[Cpu.OcCapabilities.OverclockingEnabled])
                    .Set("pboAvailable", oc[Cpu.OcCapabilities.PboAvailable])
                    .Set("powerLimitsAvailable", oc[Cpu.OcCapabilities.PowerLimitsAvailable]);
            }
            catch (Exception ex)
            {
                caps.Set("error", ex.Message);
            }

            return new JObject()
                .Set("pawnio", PawnIoState())
                .Set("cpu", new JObject()
                    .Set("name", (info.cpuName ?? string.Empty).Trim())
                    .Set("vendor", info.vendor)
                    .Set("codename", Codename)
                    .Set("family", (long)info.family)
                    .Set("model", (long)info.model)
                    .Set("stepping", (long)info.stepping)
                    .Set("packageType", (long)info.packageType)
                    .Set("cpuid", "0x" + info.cpuid.ToString("X8", CultureInfo.InvariantCulture))
                    .Set("patchLevel", "0x" + info.patchLevel.ToString("X8", CultureInfo.InvariantCulture))
                    .Set("threadsPerCore", (long)topology.threadsPerCore))
                .Set("topology", new JObject()
                    .Set("ccds", (long)topology.ccds)
                    .Set("ccdEnableMap", (long)topology.ccdEnableMap)
                    .Set("coreDisableMap", disableMap))
                .Set("os", osJson)
                .Set("map", MapJson())
                .Set("smu", new JObject()
                    .Set("version", _cpu.smu != null ? SmuVersionText(_cpu.smu.Version) : null)
                    .Set("type", _cpu.smu != null ? _cpu.smu.SMU_TYPE.ToString() : null)
                    .Set("apu", _isApu)
                    .Set("coRead", CanReadCo)
                    .Set("coWrite", CanWriteCo)
                    .Set("coMp1", _cpu.smu != null && _cpu.smu.Mp1Smu != null && _cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0)
                    .Set("testMessage", testMessage))
                .Set("support", new JObject()
                    .Set("cpu", info.codeName != Cpu.CodeName.Unsupported)
                    .Set("write", CoEncoding.WriteSupported(Codename) && !_isApu)
                    .Set("minimum", CoEncoding.MinimumFor(Codename))
                    .Set("ocCaps", caps))
                .Set("library", new JObject()
                    .Set("status", _cpu.Status.ToString())
                    .Set("lastError", _cpu.LastError != null ? _cpu.LastError.Message : null));
        }

        private uint? ReadRaw(CoreSlot slot)
        {
            return _isApu
                ? _cpu.GetPsmMarginSingleCore((uint)slot.Core, 0, 0)
                : ReadSlotRaw(slot.Ccd, slot.Slot);
        }

        private uint? ReadSlotRaw(uint ccd, uint slot)
        {
            return _cpu.GetPsmMarginSingleCore(slot, ccd, 0);
        }

        private bool WriteSlot(uint ccd, uint slot, int margin)
        {
            return _cpu.SetPsmMarginSingleCore(slot, ccd, 0, margin);
        }

        private static string RawText(uint? raw)
        {
            return raw.HasValue ? "0x" + raw.Value.ToString("X8", CultureInfo.InvariantCulture) : null;
        }

        // The one decode path for every CO read: no answer is SMU_READ_FAILED, an implausible one CO_DECODE_FAILED.
        private static int? DecodeMargin(uint? raw, out string code)
        {
            int decoded;
            if (!raw.HasValue)
            {
                code = "SMU_READ_FAILED";
                return null;
            }
            if (!CoEncoding.TryDecodeMargin(raw.Value, out decoded))
            {
                code = "CO_DECODE_FAILED";
                return null;
            }
            code = null;
            return decoded;
        }

        private JObject ReadOne(CoreSlot slot, out int? margin)
        {
            uint? raw = ReadRaw(slot);
            string code;
            margin = DecodeMargin(raw, out code);
            var entry = new JObject().Set("core", slot.Core);
            if (raw.HasValue) entry.Set("raw", RawText(raw));
            if (!margin.HasValue)
            {
                entry.Set("ok", false).Set("code", code);
                return entry;
            }
            entry.Set("ok", true).Set("value", margin.Value);
            return entry;
        }

        public JObject Read()
        {
            Open();
            if (!CanReadCo) throw new TunerException("CO_UNSUPPORTED", "this SMU has no per-core Curve Optimizer read command");

            var cores = new List<object>();
            bool allZero = true;
            int readable = 0;
            using (AcquireBus())
            {
                foreach (var slot in _map)
                {
                    int? margin;
                    cores.Add(ReadOne(slot, out margin));
                    if (margin.HasValue)
                    {
                        readable++;
                        if (margin.Value != 0) allZero = false;
                    }
                }
            }

            object fmax = null;
            object scalar = null;
            object fused = null;
            try
            {
                uint value = _cpu.GetFMax();
                if (value > 0 && value < 10000) fmax = (long)value;
            }
            catch { fmax = null; }
            try
            {
                float value = _cpu.GetPBOScalar();
                if (value > 0f && value <= 10f) scalar = (double)value;
            }
            catch { scalar = null; }
            try
            {
                var limits = _cpu.GetPboFusedLimits();
                if (limits.HasValue)
                {
                    fused = new JObject()
                        .Set("powerLimit", limits.Value.PowerLimit)
                        .Set("fastLimit", limits.Value.FastLimit)
                        .Set("slowLimit", limits.Value.SlowLimit)
                        .Set("vddTdc", limits.Value.VrmVddTdcCurrent)
                        .Set("socTdc", limits.Value.VrmSocTdcCurrent);
                }
            }
            catch { fused = null; }

            return new JObject()
                .Set("cores", cores)
                .Set("readable", readable)
                .Set("allZero", readable > 0 && allZero)
                .Set("fmax", fmax)
                .Set("scalar", scalar)
                .Set("fusedLimits", fused)
                .Set("tctl", ReadTctl())
                .Set("pmTable", ReadPmTable());
        }

        // One source and one plausibility filter for Tctl, shared by read and telemetry.
        private object ReadTctl()
        {
            try
            {
                float? value = _cpu.GetCpuTemperature();
                if (value.HasValue && value.Value > 0f && value.Value < 130f) return (double)value.Value;
                return null;
            }
            catch
            {
                return null;
            }
        }

        public JObject Telemetry()
        {
            Open();
            object tctl = ReadTctl();
            var ccds = new List<object>();

            var seen = new HashSet<uint>();
            foreach (var slot in _map)
            {
                if (_isApu || !seen.Add(slot.Ccd)) continue;
                object temp = null;
                try
                {
                    float? value = _cpu.GetSingleCcdTemperature(slot.Ccd);
                    if (value.HasValue && value.Value > 0f && value.Value < 130f) temp = (double)value.Value;
                }
                catch { temp = null; }
                ccds.Add(new JObject().Set("ccd", (int)slot.Ccd).Set("temp", temp));
            }
            return new JObject().Set("tctl", tctl).Set("ccds", ccds).Set("pmTable", ReadPmTable());
        }

        // Raw facts only: the table version and its first floats. What each offset means is up to the caller.
        // Called outside AcquireBus: the library takes the PCI bus lock itself for the SMU transfer.
        private JObject ReadPmTable()
        {
            try
            {
                if (_cpu.RefreshPowerTable() != SMU.Status.OK) return null;
                float[] table = _cpu.powerTable != null ? _cpu.powerTable.Table : null;
                if (table == null || table.Length == 0) return null;
                return new JObject()
                    .Set("version", (long)_cpu.GetTableVersion().TableVersion)
                    .Set("head", PmTableHead(table));
            }
            catch
            {
                return null;
            }
        }

        // Pure: the first PmTableHeadLength floats as plain numbers; NaN, infinities and values beyond 1e6 become null.
        public static List<object> PmTableHead(float[] table)
        {
            var head = new List<object>();
            if (table == null) return head;
            int count = Math.Min(PmTableHeadLength, table.Length);
            for (int i = 0; i < count; i++)
            {
                float value = table[i];
                if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > PmTableValueLimit)
                {
                    head.Add(null);
                    continue;
                }
                // Shortest text that round-trips the float, so 0.1f prints as 0.1 rather than 0.100000001490116.
                head.Add(double.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            return head;
        }

        private IDisposable AcquireBus()
        {
            try
            {
                return new PciBusLock(BusLockTimeoutMs);
            }
            catch (TimeoutException)
            {
                throw new TunerException("SMU_BUSY", "another program is holding the PCI bus lock");
            }
        }

        private static bool TryGetInteger(JObject entry, string key, out long value)
        {
            object raw = entry.Get(key);
            if (raw is long)
            {
                value = (long)raw;
                return true;
            }
            value = 0;
            return false;
        }

        private static bool IsEnabledCcd(long ccd, uint ccdEnableMap)
        {
            return ccd >= 0 && ccd <= MaxCcdIndex && ((ccdEnableMap >> (int)ccd) & 1u) == 1u;
        }

        // The caller states which CPU its request was built for; anything else is refused before any SMU call.
        private void CheckExpectations(JObject request)
        {
            string expectCodename = request.Get("expectCodename") as string;
            if (!string.IsNullOrEmpty(expectCodename) && !string.Equals(expectCodename, Codename, StringComparison.Ordinal))
                throw new TunerException("CPU_CHANGED", "codename is " + Codename);
            object expectCores = request.Get("expectCores");
            if (expectCores is long && (long)expectCores != _map.Count)
                throw new TunerException("CPU_CHANGED", "core count is " + _map.Count.ToString(CultureInfo.InvariantCulture));
        }

        // Pure: every request is fully validated here, before the bus lock and before any SMU call.
        public static List<KeyValuePair<CoreSlot, int>> BuildWritePlan(JObject co, int minimum, List<CoreSlot> map)
        {
            if (co == null || co.Count == 0) throw new TunerException("BAD_REQUEST", "co must be a non-empty object");
            if (minimum < CoEncoding.AbsoluteMin) minimum = CoEncoding.AbsoluteMin;
            var plan = new List<KeyValuePair<CoreSlot, int>>();
            var seenCores = new HashSet<int>();
            foreach (var item in co.Items)
            {
                int core;
                if (!int.TryParse(item.Key, NumberStyles.None, CultureInfo.InvariantCulture, out core))
                    throw new TunerException("BAD_REQUEST", "core key must be a non-negative integer");
                if (!(item.Value is long)) throw new TunerException("BAD_REQUEST", "value must be an integer");
                long value = (long)item.Value;
                if (value < minimum || value > CoEncoding.AbsoluteMax)
                    throw new TunerException("OUT_OF_RANGE", "core " + item.Key + " value " + value.ToString(CultureInfo.InvariantCulture));
                CoreSlot slot = map.Find(s => s.Core == core);
                if (slot == null) throw new TunerException("UNKNOWN_CORE", "core " + item.Key);
                if (!seenCores.Add(core)) throw new TunerException("BAD_REQUEST", "duplicate core");
                plan.Add(new KeyValuePair<CoreSlot, int>(slot, (int)value));
            }
            plan.Sort((a, b) => a.Key.Core.CompareTo(b.Key.Core));
            return plan;
        }

        public JObject Write(JObject request)
        {
            Open();
            object coValue = request.Get("co");
            var co = coValue as JObject;
            if (co == null || co.Count == 0) throw new TunerException("BAD_REQUEST", "co must be a non-empty object");
            bool stopOnFailure = !(request.Get("continueOnFailure") is bool) || !(bool)request.Get("continueOnFailure");

            CheckExpectations(request);

            if (_isApu || !CoEncoding.WriteSupported(Codename))
                throw new TunerException("WRITE_UNSUPPORTED_CPU", "per-core writes are not offered for " + Codename);
            if (!CanWriteCo) throw new TunerException("CO_UNSUPPORTED", "this SMU has no per-core Curve Optimizer write command");
            if (!_mapTrusted) throw new TunerException("CORE_MAP_UNTRUSTED", string.Join(",", _mapIssues.ToArray()));

            var plan = BuildWritePlan(co, CoEncoding.MinimumFor(Codename), _map);

            var results = new List<object>();
            bool allOk = true;
            using (AcquireBus())
            {
                if (!_cpu.SendTestMessage()) throw new TunerException("SMU_NOT_RESPONDING", "SMU did not answer the test message");

                bool halted = false;
                foreach (var step in plan)
                {
                    var entry = new JObject().Set("core", step.Key.Core).Set("target", step.Value);
                    if (halted)
                    {
                        entry.Set("ok", false).Set("code", "SKIPPED");
                        results.Add(entry);
                        continue;
                    }

                    int? before;
                    ReadOne(step.Key, out before);
                    entry.Set("before", before.HasValue ? (object)before.Value : null);

                    bool accepted = WriteSlot(step.Key.Ccd, step.Key.Slot, step.Value);
                    int? after;
                    var readback = ReadOne(step.Key, out after);
                    entry.Set("accepted", accepted);
                    entry.Set("readback", after.HasValue ? (object)after.Value : null);
                    if (readback.Get("raw") != null) entry.Set("raw", readback.Get("raw"));

                    string code = null;
                    if (!after.HasValue) code = (readback.Get("code") as string) ?? "SMU_READ_FAILED";
                    else if (after.Value == step.Value) code = accepted ? null : "ACCEPT_FLAG_FALSE";
                    else if (before.HasValue && after.Value == before.Value) code = "SMU_REJECTED";
                    else code = "READBACK_UNEXPECTED";

                    // The value on the core is what counts; a false flag with a matching readback is still applied.
                    bool ok = after.HasValue && after.Value == step.Value;
                    entry.Set("ok", ok);
                    if (code != null) entry.Set("code", code);
                    results.Add(entry);
                    if (!ok)
                    {
                        allOk = false;
                        if (stopOnFailure || code == "READBACK_UNEXPECTED") halted = true;
                    }
                }
            }

            return new JObject().Set("allOk", allOk).Set("results", results);
        }
    }
}
