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
              zentimings --timings [--extended] [--pm-table] [--modules] [--apob] [--json]
              zentimings --compare [--extended] [--pm-table] [--modules] [--apob] [--json]
              zentimings --pm-table [--json]
              zentimings --modules [--json]
              zentimings --apob [--json]
              zentimings --apob-file FILE [--json]
              zentimings --apob-image FILE [--json]
              zentimings --debug
              zentimings --replay FILE [--board BOARD --bios VERSION] [--json]
              zentimings --help

            --debug includes private raw UEFI payloads on stdout; share only deliberately.
            Live access: Linux UEFI efivarfs, permissions as configured by the OS.
            --timings/--compare require an already loaded ryzen_smu driver on supported AMD CPUs.
            Do not run other SMN clients concurrently. Nominal clocks assume 100 MHz BCLK.
            --apob: explicit read-only attempt at known reserved APOB addresses; respects kernel access restrictions.
            --apob-file: offline parsing of an existing complete APOB binary.
            --apob-image: find a unique validated persisted APOB in an existing SPI image file.
            --modules: SMBIOS DIMM inventory and already-bound SPD EEPROMs (no I2C probing).
            --extended: additional UMC fields, BGS, refresh mode and DDR5 Nitro.
            --pm-table: experimental Picasso SMU telemetry; no configured clock substitution.
            Exit codes: 0 success, 1 unavailable/unmatched, 2 invalid arguments or I/O.
            """;

        public static int Main(string[] args)
        {
            try
            {
                bool json = false, debug = false, origins = false, timings = false, compare = false, pmTable = false, extended = false, modules = false, apob = false;
                string apobImage = null, apobFile = null, replay = null, board = null, bios = null, root = "/sys";
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
                        case "--pm-table": pmTable = true; break;
                        case "--extended": extended = true; break;
                        case "--modules": modules = true; break;
                        case "--apob": apob = true; break;
                        case "--apob-file": apobFile = Next(); break;
                        case "--apob-image": apobImage = Next(); break;
                        case "--replay": replay = Next(); break;
                        case "--board": board = Next(); break;
                        case "--bios": bios = Next(); break;
                        case "--sysfs-root": root = Next(); break; // fixture/test override
                        default: throw new ArgumentException("Unknown option: " + arg);
                    }
                }
                if(apobFile!=null || apobImage!=null)
                {
                    if((apobFile!=null && apobImage!=null) || apob || modules || pmTable || extended || timings || compare || origins || debug ||
                        replay!=null || board!=null || bios!=null || seen.Contains("--sysfs-root"))
                        throw new ArgumentException("--apob-file and --apob-image are offline, standalone and mutually exclusive.");
                    return PrintApob(apobImage!=null?LinuxApob.ReadImage(apobImage):LinuxApob.ReadFile(apobFile),json);
                }
                if(apob && (origins || debug || replay!=null || seen.Contains("--sysfs-root") ||
                    ((modules || pmTable) && !(timings || compare))))
                    throw new ArgumentException("--apob is live-only, alone or with --timings/--compare.");
                if(modules && (origins || debug || replay != null || (pmTable && !(timings || compare))))
                    throw new ArgumentException("--modules may be used alone or with --timings/--compare.");
                if(extended && !(timings || compare))
                    throw new ArgumentException("--extended requires --timings or --compare.");
                if (pmTable && (debug || origins || replay != null || seen.Contains("--sysfs-root")))
                    throw new ArgumentException("PM Table mode is explicit, live-only, and incompatible with origins/debug/replay.");
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
                        return PrintRunning(firmware, compare, json, pmTable, extended, modules, apob);
                    if (apob) return PrintApob(LinuxApob.Capture(),json);
                    if (pmTable) return PrintPmTable(json);
                    if (modules) return PrintModules(LinuxMemoryInventory.Capture(root),json);
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

        private static int PrintRunning(LinuxFirmware firmware, bool compare, bool json, bool pmTable, bool extended, bool modules, bool apob)
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
                try { snapshot=RunningTimings.Capture(cpu,new RyzenSmuReader().Read,"ryzen_smu",extended); }
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
            var telemetry=pmTable?LinuxPowerTable.Capture():null;
            var inventory=modules?LinuxMemoryInventory.Capture("/sys"):null;
            var boot=apob?LinuxApob.Capture():null;
            var data=snapshot.Channels.Select(c=>new {
                channel=c.Index, memory_type=c.MemoryType, nominal_mclk_mhz=c.NominalMclkMHz,
                nominal_data_rate_mts=2*c.NominalMclkMHz, refresh_status=c.RefreshStatus, registers=c.Registers, extended=c.Extended,
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
                pm_table=telemetry, memory_inventory=inventory, apob=boot,
                channels=data
            },new JsonSerializerOptions{WriteIndented=true}));
            else
            {
                Console.WriteLine(snapshot.Note);
                if(telemetry!=null) PrintPmTableText(telemetry);
                if(inventory!=null) PrintModules(inventory,false);
                if(boot!=null) PrintApob(boot,false);
                if(compare) Console.WriteLine("Saved settings: "+(saved?.Status??"efivarfs unavailable"));
                foreach(var c in data)
                {
                    Console.WriteLine($"UMC {c.channel}: {c.memory_type}, nominal MCLK {c.nominal_mclk_mhz:F2} MHz");
                    Console.WriteLine("{0,-20} {1,8} {2,10} {3,12} {4,8}","Setting","Running","Origin","Saved","Differs");
                    foreach(var p in c.rows)
                        Console.WriteLine("{0,-20} {1,8} {2,10} {3,12} {4,8}",p.Key,DisplayRunning(p.Key,p.Value.running),
                            p.Value.origin??"-",p.Value.saved??"-",p.Value.differs_from_running?.ToString()??"-");
                    if(c.extended!=null) PrintExtended(c.extended);
                }
            }
            return 0;
        }

        private static int PrintApob(LinuxApob.Result result,bool json)
        {
            if(json)
                Console.WriteLine(JsonSerializer.Serialize(new {schema_version=1,apob=result},
                    new JsonSerializerOptions{WriteIndented=true}));
            else
            {
                Console.WriteLine("APOB: "+result.status);
                if(result.available)
                    Console.WriteLine($"  {result.source}; address {result.address??"-"}; version 0x{result.version:X}; {result.table_size} bytes; {result.entries.Length} entries");
                if(result.image_offset!=null) Console.WriteLine("  SPI image offset: "+result.image_offset);
                if(result.bios_directory!=null)
                {
                    var dir=result.bios_directory;
                    Console.WriteLine("  BIOS directory: "+dir.status);
                    foreach(var region in dir.nv_regions) Console.WriteLine($"    Advertised NV region {region.image_offset}, {region.size} bytes; directories {string.Join(", ",region.directory_offsets)}");
                    foreach(var addr in dir.advertised_ram_addresses) Console.WriteLine("    Advertised RAM destination "+addr);
                    if(dir.matched_nv_region.HasValue) Console.WriteLine("    Exact NV region match: "+dir.matched_nv_region.Value);
                    foreach(var unresolved in dir.unresolved_nv_entries) Console.WriteLine("    Unresolved: "+unresolved);
                    Console.WriteLine("    "+dir.note);
                }
                foreach(var d in result.diagnostics) Console.WriteLine("  "+d);
                foreach(var c in result.electrical)
                {
                    Console.WriteLine($"  Instance {c.instance}, APOB channel {c.channel}: boot MEMCLK {c.memclk_mhz} MHz");
                    foreach(var p in c.settings) Console.WriteLine($"    {p.Key,-20} {p.Value.display} (code {p.Value.code})");
                }
                foreach(var slot in result.spd)
                {
                    Console.WriteLine($"  SPD copy: socket {slot.socket}, channel {slot.channel}, DIMM {slot.dimm}: {slot.data.status}; {slot.data.part_number}");
                    PrintSpdDetails(slot.data);
                }
                if(result.gen_config!=null)
                {
                    Console.WriteLine("  GEN configuration: saved frequency blocks (no channel association)");
                    foreach(var g in result.gen_config.groups)
                    {
                        foreach(var s in g.states)
                        {
                            Console.WriteLine($"    Group {g.group_index} state {s.state_index}: DDR4-{s.nominal_rate_mts}, {s.memclk_mhz} MHz");
                            Console.WriteLine("    Timings: "+string.Join(", ",s.timings_clocks.Select(p=>$"{p.Key}={p.Value}")));
                            Console.WriteLine($"    MR0..6: {string.Join(" ",s.mode_registers.words)}; VrefDQ {s.mode_registers.initial_vref_dq_percent_vddq}% VDDQ (saved)");
                        }
                    }
                    if(result.gen_config.metadata_observations!=null)
                    {
                        foreach(var m in result.gen_config.metadata_observations.observations)
                            Console.WriteLine($"    Metadata record at 0x{m.payload_offset:X}: index {m.index_word}, words +20/+22/+24 = {m.word_20}/{m.word_22}/{m.word_24}; equal groups {string.Join(", ",m.equal_group_indices)}");
                        Console.WriteLine("    "+result.gen_config.metadata_observations.note);
                    }
                    Console.WriteLine("    "+result.gen_config.note);
                }
                if(result.ddr5_config!=null)
                {
                    Console.WriteLine("  DDR5 GEN configuration: "+result.ddr5_config.status+" (saved states; group mapping unknown)");
                    foreach(var g in result.ddr5_config.groups)
                        foreach(var s in g.states)
                        {
                            Console.WriteLine($"    Group {g.group_index} state {s.state_index}: DDR5-{s.nominal_rate_mts}, MCLK {s.memclk_mhz}, DFI {s.dfi_clk_mhz} MHz");
                            Console.WriteLine("    Candidate timings: "+string.Join(", ",s.timings_clocks.Select(p=>$"{p.Key}={p.Value}")));
                            Console.WriteLine("    Refresh candidates: "+string.Join(", ",s.refresh_candidates_clocks.Select(p=>$"{p.Key}={p.Value}")));
                            Console.WriteLine("    Electrical candidates: "+string.Join(", ",s.electrical_candidates.Select(p=>$"{p.Key}={p.Value.display}")));
                        }
                    if(result.ddr5_config.replay_correlation!=null)
                        Console.WriteLine("    "+result.ddr5_config.replay_correlation.note);
                    Console.WriteLine("    "+result.ddr5_config.note);
                }
                if(result.ddr5_boot_info!=null)
                {
                    var boot=result.ddr5_boot_info;
                    Console.WriteLine($"  DDR5 APCB boot info: both SPD identities match (not printed); active instance 0x{boot.apcb_active_instance_code:X}, DIMM configuration updated: {boot.dimm_configuration_updated_candidate}");
                    Console.WriteLine($"    Last training date candidate {boot.last_training_date_candidate??"unavailable"} ({boot.last_training_date_encoding}); recovery/action/profile codes {boot.apcb_recovery_flag_code}/{boot.action_on_bist_failure_code}/{boot.workload_profile_code}");
                    Console.WriteLine("    "+boot.note);
                }
                if(result.ddr5_training_structure!=null)
                {
                    var training=result.ddr5_training_structure;
                    Console.WriteLine("  DDR5 PHY/PMU: bounded structural observations only");
                    foreach(var phase in training.phy_phases)
                        Console.WriteLine($"    MEM {phase.type}: {phase.record_count_candidate} candidate pairs, {phase.equal_adjacent_u16_pairs}/{phase.adjacent_u16_pairs} equal adjacent u16 pairs");
                    var match=training.phy_fixed_alignment;
                    if(match!=null)
                        Console.WriteLine($"    PHY alignment +0x{match.mem30_offset_minus_mem31_bytes:X}: {match.shifted.equal_u16}/{match.shifted.compared_u16} equal u16 words; {match.shifted.equal_nonzero_u16} nonzero");
                    var pmu=training.pmu_smb;
                    if(pmu!=null)
                        Console.WriteLine($"    PMU windows: {pmu.equal_bytes}/{pmu.compared_bytes} equal bytes, both rate words match GEN: {pmu.both_rate_words_match_highest_gen}");
                    Console.WriteLine("    "+training.note);
                }
                if(result.replay!=null)
                {
                    var replay=result.replay;
                    Console.WriteLine($"  S3 replay: {replay.record_count} bounded records; offsets 0x{replay.start_offset:X}..0x{replay.end_exclusive:X} (exclusive)");
                    foreach(var c in replay.channels)
                    {
                        Console.WriteLine($"    UMC address index {c.umc_address_index}: {c.observed_registers.Count} observed registers, {c.missing_registers.Length} missing; candidate MCLK {c.nominal_mclk_mhz} MHz");
                        Console.WriteLine("    Candidate timings: "+string.Join(", ",c.candidate_timings.Select(p=>$"{p.Key}={p.Value}")));
                        foreach(var r in c.refresh_word_candidates)
                            Console.WriteLine("    Refresh word "+r.Key+": "+string.Join(", ",r.Value.Select(p=>$"{p.Key}={p.Value}")));
                    }
                    foreach(var companion in replay.companions)
                    {
                        var st=companion.legacy_structure;
                        if(st!=null) Console.WriteLine($"    MEM {companion.type}: {st.equal_adjacent_u16_pairs}/{st.adjacent_u16_pairs} equal adjacent u16 pairs, motif 05B0/05B1 {st.motif_05b0_count}/{st.motif_05b1_count} (structural only)");
                    }
                    Console.WriteLine("    "+replay.note);
                }
                Console.WriteLine(result.note);
            }
            return result.available?0:1;
        }

        private static int PrintModules(LinuxMemoryInventory.Result inventory,bool json)
        {
            if(json)
                Console.WriteLine(JsonSerializer.Serialize(new {schema_version=1,memory_inventory=inventory},
                    new JsonSerializerOptions{WriteIndented=true}));
            else
            {
                Console.WriteLine("SMBIOS: "+inventory.smbios_status);
                foreach(var m in inventory.modules)
                    Console.WriteLine($"  {m.locator??"?"} / {m.bank_locator??"?"}: {m.manufacturer??"?"} {m.part_number??"?"}, "+
                        $"{m.memory_type} {m.form_factor}, {m.capacity_bytes/1048576} MiB, ranks {m.ranks}, "+
                        $"rated {m.rated_mts}, configured {m.configured_mts} MT/s, configured {m.configured_mv} mV");
                Console.WriteLine("SPD: "+inventory.spd_status);
                foreach(var d in inventory.spd)
                {
                    Console.WriteLine($"  {d.device} ({d.driver}): {d.status}; {d.memory_type} {d.part_number}, "+
                        $"revision {d.revision}, tCK min {d.tck_min_ps} ps, base CRC {d.base_crc_valid}");
                    PrintSpdDetails(d);
                }
                Console.WriteLine(inventory.note);
            }
            return inventory.modules.Length>0 || inventory.spd.Any(d=>d.base_crc_valid==true)?0:1;
        }

        private static void PrintSpdDetails(LinuxMemoryInventory.Spd spd)
        {
            var d=spd.ddr4;
            if(d==null)
            {
                var d5=spd.ddr5;
                if(d5==null) return;
                Console.WriteLine($"    {d5.module_manufacturer} / DRAM {d5.dram_manufacturer}; {d5.capacity_mib} MiB, {d5.ranks_per_channel}R/channel, x{d5.device_width_bits}");
                Console.WriteLine($"    JEDEC max {d5.jedec_speed_mts} MT/s; XMP: {d5.xmp_status}; EXPO: {d5.expo_status}");
                Console.WriteLine("    JEDEC at max rate: "+string.Join(", ",d5.jedec_timings_at_max_rate_clocks.Select(p=>$"{p.Key}={p.Value}")));
                foreach(var p in d5.profiles)
                    Console.WriteLine($"    {p.kind} {p.profile_number}: {p.speed_mts} MT/s, VDD {p.vdd_mv} mV, VDDQ {p.vddq_mv} mV; "+
                        string.Join(", ",p.timings_clocks.Select(x=>$"{x.Key}={x.Value}")));
                return;
            }
            Console.WriteLine($"    {d.module_manufacturer} / DRAM {d.dram_manufacturer}; {d.capacity_mib} MiB, {d.package_ranks}R, x{d.device_width_bits}");
            Console.WriteLine($"    JEDEC max {d.jedec_speed_mts} MT/s; {d.xmp_status}");
            if(d.jedec_timings_at_max_rate_clocks!=null)
                Console.WriteLine("    JEDEC at max rate: "+string.Join(", ",d.jedec_timings_at_max_rate_clocks.Select(p=>$"{p.Key}={p.Value}")));
            foreach(var x in d.xmp_profiles)
                Console.WriteLine($"    XMP {x.profile_number}: {x.speed_mts} MT/s, {x.voltage_mv} mV; "+
                    string.Join(", ",x.timings_clocks.Select(p=>$"{p.Key}={p.Value}")));
        }

        private static string DisplayRunning(string key,uint value) =>
            key=="Cmd2T"?(value==0?"1T":"2T"):
            key is "GDM" or "PwrDwn"?(value==0?"Disabled":"Enabled"):
            value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static void PrintExtended(ExtendedUmc.Result result)
        {
            Console.WriteLine("Extended UMC: "+result.status);
            Console.WriteLine("  Refresh mode: "+result.refresh_mode);
            if(result.nominal_effective_trfc_ns.HasValue)
                Console.WriteLine($"  Nominal effective tRFC: {result.nominal_effective_trfc_ns:F2} ns (assumed BCLK)");
            if(result.nitro!=null) Console.WriteLine("  Nitro Rx/Tx/Ctrl: "+result.nitro);
            foreach(var p in result.fields)
                Console.WriteLine("  {0,-24} {1}",p.Key,p.Key is "BGS" or "BGSAlt"?
                    (p.Value==0?"Disabled":"Enabled"):p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach(var p in result.unavailable)
                Console.WriteLine("  Unavailable "+p.Key+": "+p.Value);
            Console.WriteLine("  "+result.electrical_status);
        }

        private static int PrintPmTable(bool json)
        {
            var result=LinuxPowerTable.Capture();
            if(json) Console.WriteLine(JsonSerializer.Serialize(new {schema_version=1,pm_table=result},
                new JsonSerializerOptions{WriteIndented=true}));
            else PrintPmTableText(result);
            return result.available?0:1;
        }
        private static void PrintPmTableText(LinuxPowerTable.Result result)
        {
            Console.WriteLine("PM Table: "+result.status);
            if(!result.available) return;
            var p=result.sample;
            Console.WriteLine($"  Version: {p.table_version}; bytes: {p.table_size}; driver: {p.driver_version}; SMU: {p.smu_firmware}");
            Console.WriteLine($"  SMU FCLK {p.fclk_mhz:F2} MHz, UCLK {p.uclk_mhz:F2} MHz, MCLK {p.mclk_mhz:F2} MHz");
            Console.WriteLine($"  SMU VSOC {p.vsoc_v:F4} V, CLDO VDDP {p.cldo_vddp_v:F4} V");
            Console.WriteLine("  "+p.note);
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
