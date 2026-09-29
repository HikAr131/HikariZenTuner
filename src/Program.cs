// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This program only turns JSON Lines requests into ZenStates-Core calls and prints the
// answers. It holds no tuning logic, stores nothing on disk and talks to nothing but
// stdin / stdout. Usage:
//   HikariZenTuner.exe identify | read | telemetry | snapshot
//   HikariZenTuner.exe write --co "0:-20,3:-26"
//   HikariZenTuner.exe serve --watch-pid <pid>
//     (probeSlots and setMap exist only as serve requests)
//   HikariZenTuner.exe --self-test | --version

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace HikariZenTuner
{
    public static class Program
    {
        public const string Version = "1.1.0";

        private static readonly object OutputLock = new object();
        // Held while a serve request runs, so the parent watch never exits in the middle of a write batch.
        private static readonly object RequestGate = new object();
        private const int ParentExitDrainMs = 15000;
        private static TextWriter _stdout;

        public static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            try
            {
                if (args.Length == 0) return Emit(Failure(null, "BAD_REQUEST", "missing command"));
                string verb = args[0];
                if (verb == "--version") return Emit(new JObject().Set("ok", true).Set("cmd", "version").Set("helper", HelperInfo()));
                if (verb == "--self-test") return Emit(SelfTest.Run());
                if (verb == "serve") return Serve(args);
                if (verb == "probeSlots" || verb == "setMap")
                    return Emit(Failure(verb, "BAD_REQUEST", verb + " is only available in serve mode"));

                var request = new JObject().Set("cmd", verb);
                if (verb == "write")
                {
                    string spec = OptionValue(args, "--co");
                    if (spec == null) return Emit(Failure(null, "BAD_REQUEST", "write needs --co"));
                    JObject co;
                    string error;
                    if (!TryParseCoSpec(spec, out co, out error)) return Emit(Failure(null, "BAD_REQUEST", error));
                    request.Set("co", co);
                    if (HasFlag(args, "--continue-on-failure")) request.Set("continueOnFailure", true);
                }
                using (var tuner = new Tuner())
                {
                    return Emit(Handle(tuner, request));
                }
            }
            catch (Exception ex)
            {
                return Emit(Failure(null, "INTERNAL", ex.GetType().Name + ": " + ex.Message));
            }
        }

        private static int Serve(string[] args)
        {
            string watch = OptionValue(args, "--watch-pid");
            if (watch != null)
            {
                int pid;
                if (!int.TryParse(watch, NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0)
                    return Emit(Failure(null, "BAD_REQUEST", "--watch-pid needs a process id"));
                StartParentWatch(pid);
            }

            Emit(new JObject().Set("ok", true).Set("event", "ready").Set("helper", HelperInfo()));
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using (var tuner = new Tuner())
            {
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    object id = null;
                    JObject request;
                    try
                    {
                        request = Json.Parse(line) as JObject;
                        if (request == null) throw new JsonException("request must be an object");
                        id = request.Get("id");
                    }
                    catch (JsonException ex)
                    {
                        Emit(Failure(null, "BAD_REQUEST", ex.Message));
                        continue;
                    }

                    if (string.Equals(request.Get("cmd") as string, "quit", StringComparison.Ordinal))
                    {
                        Emit(new JObject().Set("id", id).Set("ok", true).Set("cmd", "quit"));
                        break;
                    }

                    JObject response;
                    try
                    {
                        lock (RequestGate)
                        {
                            response = Handle(tuner, request);
                        }
                    }
                    catch (Exception ex)
                    {
                        response = Failure(request.Get("cmd") as string, "INTERNAL", ex.GetType().Name + ": " + ex.Message);
                    }
                    var tagged = new JObject().Set("id", id);
                    foreach (var item in response.Items) tagged.Set(item.Key, item.Value);
                    Emit(tagged);
                }
            }
            return 0;
        }

        private static JObject Handle(Tuner tuner, JObject request)
        {
            string cmd = request.Get("cmd") as string;
            try
            {
                switch (cmd)
                {
                    case "ping":
                        return Ok(cmd).Set("helper", HelperInfo()).Set("pawnio", Tuner.PawnIoState());
                    case "identify":
                        return Merge(Ok(cmd), tuner.Identify());
                    case "read":
                        return Merge(Ok(cmd), tuner.Read());
                    case "snapshot":
                        return Ok(cmd).Set("identify", tuner.Identify()).Set("read", tuner.Read());
                    case "telemetry":
                        return Merge(Ok(cmd), tuner.Telemetry());
                    case "write":
                        return Merge(Ok(cmd), tuner.Write(request));
                    case "probeSlots":
                        return Merge(Ok(cmd), tuner.ProbeSlots(request));
                    case "setMap":
                        return Merge(Ok(cmd), tuner.SetMap(request));
                    default:
                        return Failure(cmd, "UNKNOWN_COMMAND", "unknown command");
                }
            }
            catch (TunerException ex)
            {
                var failure = Failure(cmd, ex.Code, ex.Message);
                if (ex.Code == "PAWNIO_NOT_INSTALLED" || ex.Code == "PAWNIO_MODULE_LOAD_FAILED") failure.Set("pawnio", Tuner.PawnIoState());
                return failure;
            }
        }

        private static JObject Ok(string cmd)
        {
            return new JObject().Set("ok", true).Set("cmd", cmd);
        }

        private static JObject Merge(JObject target, JObject source)
        {
            foreach (var item in source.Items) target.Set(item.Key, item.Value);
            return target;
        }

        private static JObject Failure(string cmd, string code, string message)
        {
            return new JObject().Set("ok", false).Set("cmd", cmd).Set("code", code).Set("message", message ?? string.Empty);
        }

        private static JObject HelperInfo()
        {
            string library = null;
            try
            {
                var assembly = typeof(ZenStates.Core.Cpu).Assembly;
                var attribute = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
                library = attribute != null ? attribute.InformationalVersion : assembly.GetName().Version.ToString();
            }
            catch
            {
                library = null;
            }
            return new JObject().Set("version", Version).Set("zenStatesCore", library);
        }

        private static int Emit(JObject payload)
        {
            string line = Json.Serialize(payload);
            lock (OutputLock)
            {
                _stdout.Write(line);
                _stdout.Write('\n');
                _stdout.Flush();
            }
            object ok = payload.Get("ok");
            return ok is bool && (bool)ok ? 0 : 1;
        }

        private static void StartParentWatch(int pid)
        {
            Process parent;
            try
            {
                parent = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                Environment.Exit(3);
                return;
            }
            var thread = new Thread(() =>
            {
                try { parent.WaitForExit(); } catch { }
                // stdin normally closes with the parent; this is the fallback when it does not.
                // Let a request that is already running (a write batch) finish first, but never wait forever.
                Thread.Sleep(1500);
                Monitor.TryEnter(RequestGate, ParentExitDrainMs);
                Environment.Exit(3);
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private static string OptionValue(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            }
            return null;
        }

        private static bool HasFlag(string[] args, string name)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // "0:-20,3:-26" -> {"0":-20,"3":-26}
        public static bool TryParseCoSpec(string spec, out JObject co, out string error)
        {
            co = new JObject();
            error = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string part in spec.Split(','))
            {
                string[] pair = part.Split(':');
                int core;
                int value;
                if (pair.Length != 2
                    || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out core)
                    || !int.TryParse(pair[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                {
                    error = "bad --co entry";
                    return false;
                }
                string key = core.ToString(CultureInfo.InvariantCulture);
                if (!seen.Add(key))
                {
                    error = "duplicate core in --co";
                    return false;
                }
                co.Set(key, (long)value);
            }
            if (co.Count == 0)
            {
                error = "empty --co";
                return false;
            }
            return true;
        }
    }
}
