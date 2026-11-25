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
            zentimings — read-only BIOS setting origins (not live memory timings)
              zentimings [--origins] [--json]
              zentimings --debug
              zentimings --replay FILE [--board BOARD --bios VERSION] [--json]
              zentimings --help

            --debug includes private raw UEFI payloads on stdout; share only deliberately.
            Live access: Linux UEFI efivarfs, permissions as configured by the OS.
            Running values and mismatch checks are not available in this release.
            Exit codes: 0 success, 1 unavailable/unmatched, 2 invalid arguments or I/O.
            """;

        public static int Main(string[] args)
        {
            try
            {
                bool json = false, debug = false, origins = false;
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
                        case "--replay": replay = Next(); break;
                        case "--board": board = Next(); break;
                        case "--bios": bios = Next(); break;
                        case "--sysfs-root": root = Next(); break; // fixture/test override
                        default: throw new ArgumentException("Unknown option: " + arg);
                    }
                }
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
