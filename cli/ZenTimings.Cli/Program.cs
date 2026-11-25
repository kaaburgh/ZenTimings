using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZenTimings.BiosOrigin;

namespace ZenTimings.Cli
{
    internal static class Program
    {
        private static readonly (string Name, string Guid)[] DiagnosticVariables = {
            ("AmdSetupRPL","3a997502-647a-4c82-998e-52ef9486a247"),
            ("AmdSetup","3a997502-647a-4c82-998e-52ef9486a247"),
            ("AodSetupRpl","5ed15dc0-edef-4161-9151-6014c4cc630c"),
            ("Setup","ec87d643-eba4-4bb5-a1e5-3f3e36b20da9"),
            ("AMD_PBS_SETUP","a339d746-f678-49b3-9fc7-54ce0f9df226")
        };

        private const string Help = """
            zentimings — saved BIOS settings and experimental running UMC timings
              zentimings [--origins] [--json]
              zentimings --timings [--json]\n              zentimings --compare [--json]\n              zentimings --debug
              zentimings --replay FILE [--board BOARD --bios VERSION] [--json]
              zentimings --help

            --debug includes private raw UEFI payloads on stdout; share only deliberately.
            Live access: Linux UEFI efivarfs, permissions as configured by the OS.
            --timings/--compare require an already loaded ryzen_smu driver on supported AMD CPUs.\n            Do not run other SMN clients concurrently. Nominal clocks assume 100 MHz BCLK.
            Exit codes: 0 success, 1 unavailable/unmatched, 2 invalid arguments or I/O.
            """;

        public static int Main(string[] args)
        {
            try
            {
                bool json = false, debug = false, origins = false, timings = false, compare = false;
                string replay = null, board = null, bios = null, root = "/sys";
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (!seen.Add(arg)) throw new ArgumentException("Duplicate option: " + arg);
                    string Next() => ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)
                        ? args[i] : throw new ArgumentException("Missing value for " + arg);
                    switch (arg)
                    {
                        case "--help": case "-h": Console.WriteLine(Help); return 0;
                        case "--json": json = true; break;
                        case "--debug": debug = true; break;
                        case "--origins": origins = true; break;
                        case "--timings": timings = true; break;
                        case "--compare": compare = true; break;
                        case "--replay": replay = Next(); break;
                        case "--board": board = Next(); break;
                        case "--bios": bios = Next(); break;
                        case "--sysfs-root": root = Next(); break; // fixture/test override
                        default: throw new ArgumentException("Unknown option: " + arg);
                    }
                }
                if ((timings || compare) && (debug || replay != null || origins || (timings && compare) ||
                    seen.Contains("--sysfs-root")))
                    throw new ArgumentException("Running modes are explicit, live-only, and do not accept fixture paths.");
                if (debug && (json || replay != null || origins) ||
                    replay != null && (origins || seen.Contains("--sysfs-root")) ||
                    replay == null && (board != null || bios != null) ||
                    (board == null) != (bios == null))
                    throw new ArgumentException("Incompatible options (board/bios overrides are replay-only and paired).");

                SettingOriginReport report;
                if (replay != null)
                {
                    string raw = File.ReadAllText(replay);
                    board ??= Identity(raw, "Resolver board (systemInfo.MbName)");
                    bios ??= Identity(raw, "Resolver BIOS (systemInfo.BiosVersion)");
                    if (string.IsNullOrWhiteSpace(board) || string.IsNullOrWhiteSpace(bios))
                        throw new ArgumentException("Replay requires resolver identity lines or explicit --board and --bios.");
                    report = DrfxiSettingOrigins.FromDebugReport(board, bios, raw);
                }
                else
                {
                    if (!OperatingSystem.IsLinux())
                        throw new PlatformNotSupportedException("Live access requires Linux; use --replay offline.");
                    var firmware = new LinuxFirmware(root);
                    board = firmware.Board;
                    bios = firmware.Bios;
                    if (timings || compare)
                        return PrintRunning(firmware, compare, json);
                    if (!firmware.IsAvailable)
                    {
                        Console.Error.WriteLine("efivarfs unavailable: " + firmware.EfiDirectory);
                        return 1;
                    }
                    if (debug) return Debug(firmware, board, bios);
                    report = DrfxiSettingOrigins.ReadFromProvider(board, bios,
                        (name, guid) => firmware.Read(name, guid)?.Payload);
                }
                Print(board, bios, report, json);
                return report.IsMatched ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 2;
            }
        }

        private static int PrintRunning(LinuxFirmware firmware, bool compare, bool json)
        {
            // No hardware I/O before explicit --timings or --compare and CPUID validation.
            // The sysfs driver has a global response buffer. Two passes detect some,
            // but not all, concurrent interference; never claim atomicity.
            RunningTimings.Snapshot snapshot;
            try
            {
                var cpu=RunningTimings.Identify();
                if (RunningTimings.MemoryType(cpu)<0)
                    throw new NotSupportedException("Unsupported CPU family/model; no SMN requests issued.");
                if (!File.Exists("/sys/kernel/ryzen_smu_drv/smn"))
                    throw new IOException("ryzen_smu SMN interface unavailable. Driver must be installed and loaded separately.");
                Console.Error.WriteLine("Experimental SMN read. Stop other ryzen_smu/SMN clients before use.");
                // Prevent two instances of this CLI from interleaving requests.
                // Kernel/other userspace programs do not honor this lock.
                using var mutex = new System.Threading.Mutex(false,"ZenTimingsLinuxSmnRead");
                if (!mutex.WaitOne(0)) throw new IOException("Another ZenTimings SMN reader is active.");
                try { snapshot=RunningTimings.Capture(cpu,new RyzenSmuReader().Read,"ryzen_smu"); }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex)
            {
                if (json) Console.WriteLine(JsonSerializer.Serialize(new {schema_version=1,running_available=false,status=ex.Message}));
                else Console.Error.WriteLine(ex.Message);
                return 1;
            }
            SettingOriginReport saved = compare && firmware.IsAvailable ?
                DrfxiSettingOrigins.ReadFromProvider(firmware.Board,firmware.Bios,(name,guid)=>firmware.Read(name,guid)?.Payload) : null;
            var data=snapshot.Channels.Select(c=>new {
                channel=c.Index, memory_type=c.MemoryType, nominal_mclk_mhz=c.NominalMclkMHz,
                nominal_data_rate_mts=2*c.NominalMclkMHz, refresh_status=c.RefreshStatus, registers=c.Registers,
                rows=c.Timings.ToDictionary(p=>p.Key,p=>{
                    var row=saved?.Find(p.Key);
                    string running=p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return new {running=p.Value,origin=row?.Source,saved=row?.Stored,
                        differs_from_running=row?.DiffersFrom(running),note=row?.Details};
                })
            }).ToArray();
            if (json) Console.WriteLine(JsonSerializer.Serialize(new {
                schema_version=1, running_available=true, experimental=true, cpu=snapshot.Cpu,
                board=firmware.Board,bios_version=firmware.Bios,source=snapshot.Source,note=snapshot.Note,
                origins_matched=saved?.IsMatched ?? false,origins_status=compare?(saved?.Status??"efivarfs unavailable"):null,
                channels=data
            },new JsonSerializerOptions{WriteIndented=true}));
            else
            {
                Console.WriteLine(snapshot.Note);
                if(compare) Console.WriteLine("Saved settings: "+(saved?.Status??"efivarfs unavailable"));
                foreach(var c in data)
                {
                    Console.WriteLine($"UMC {c.channel}: {c.memory_type}, nominal MCLK {c.nominal_mclk_mhz:F2} MHz");
                    Console.WriteLine("{0,-20} {1,8} {2,10} {3,12} {4,8}","Setting","Running","Origin","Saved","Differs");
                    foreach(var p in c.rows)
                        Console.WriteLine("{0,-20} {1,8} {2,10} {3,12} {4,8}",p.Key,p.Value.running,
                            p.Value.origin??"-",p.Value.saved??"-",p.Value.differs_from_running?.ToString()??"-");
                }
            }
            return 0;
        }

        private static string Identity(string raw, string label)
        {
            var matches = Regex.Matches(raw, "^" + Regex.Escape(label) + @":[ \t]*([^\r\n]+)\r?$",
                RegexOptions.Multiline);
            var values = matches.Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (values.Length > 1) throw new InvalidDataException("Conflicting replay identities: " + label);
            return values.SingleOrDefault();
        }

        private static void Print(string board, string bios, SettingOriginReport report, bool json)
        {
            var rows = report.IsMatched ? report.Rows.Values.OrderBy(r => r.Row, StringComparer.Ordinal).ToArray()
                : Array.Empty<SettingOriginRow>();
            if (json)
            {
                var output = new {
                    schema_version = 1, board, bios_version = bios, matched = report.IsMatched,
                    layout_id = report.LayoutId, status = report.Status, plausibility = report.Plausibility,
                    running_available = false,
                    rows = rows.ToDictionary(r => r.Row, r => new {
                        origin = r.Source, badge = r.Badge, saved = r.Stored, saved_number = r.StoredNumber,
                        running = (string)null, differs_from_running = (bool?)null,
                        both_menus_manual = r.OtherMenuManual, note = r.Details })
                };
                Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            Console.WriteLine("Board: " + (board ?? "unknown") + "\nBIOS: " + (bios ?? "unknown"));
            Console.WriteLine("Status: " + report.Status);
            Console.WriteLine("Saved UEFI settings only; running values not measured.");
            if (!report.IsMatched) return;
            Console.WriteLine("{0,-20} {1,-10} {2}", "Setting", "Origin", "Saved");
            foreach (var r in rows)
                Console.WriteLine("{0,-20} {1,-10} {2}", r.Row, r.Badge, r.Stored ?? "-");
        }

        private static int Debug(LinuxFirmware firmware, string board, string bios)
        {
            Console.Error.WriteLine("WARNING: raw UEFI payloads may contain private data. No upload is performed.");
            Console.WriteLine("Capture: explicit user opt-in; read-only Linux efivarfs.");
            Console.WriteLine("Resolver board (systemInfo.MbName): " + (board ?? "<unavailable>"));
            Console.WriteLine("Resolver BIOS (systemInfo.BiosVersion): " + (bios ?? "<unavailable>"));
            Console.WriteLine("Board vendor: " + firmware.Dmi("board_vendor"));
            Console.WriteLine("Raw DMI board: " + firmware.Dmi("board_name"));
            Console.WriteLine("System vendor: " + firmware.Dmi("sys_vendor"));
            Console.WriteLine("System family: " + firmware.Dmi("product_family"));
            Console.WriteLine("System product: " + firmware.Dmi("product_name"));
            Console.WriteLine("Catalog decision: " + BiosOriginLayoutCatalog.DescribeSelection(board, bios));
            int readable = 0;
            foreach (var variable in DiagnosticVariables)
            {
                Console.WriteLine("Variable: " + variable.Name + " GUID " + variable.Guid);
                try
                {
                    var v = firmware.Read(variable.Name, variable.Guid);
                    if (v == null) { Console.WriteLine("  Not found"); continue; }
                    Console.WriteLine($"  attributes: 0x{v.Attributes:X8}; length: {v.Payload.Length}");
                    Console.WriteLine("  SHA-256: " + Convert.ToHexString(SHA256.HashData(v.Payload)).ToLowerInvariant());
                    Console.WriteLine(variable.Name + ".base64: " + Convert.ToBase64String(v.Payload));
                    readable++;
                }
                catch (Exception ex) { Console.WriteLine("  Read error: " + ex.GetType().Name + ": " + ex.Message); }
            }
            return readable == 0 ? 1 : 0;
        }
    }
}
