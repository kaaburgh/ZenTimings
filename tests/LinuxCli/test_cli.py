"""Black-box tests: fake sysfs and sanitized synthetic UEFI payloads only."""
import base64
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

CLI = Path(sys.argv.pop(1)).resolve()
AMD = "3a997502-647a-4c82-998e-52ef9486a247"
SETUP = "ec87d643-eba4-4bb5-a1e5-3f3e36b20da9"
AOD = "5ed15dc0-edef-4161-9151-6014c4cc630c"

class CliTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.dmi = self.root / "class/dmi/id"
        self.efi = self.root / "firmware/efi/efivars"
        self.dmi.mkdir(parents=True)
        self.efi.mkdir(parents=True)
        self.identity("1234", "M33KT24A", "LENOVO", "ThinkCentre M75n", "11BS0000")
        self.amd = bytearray([255] * 0x53F)
        self.amd[:8] = bytes.fromhex("323f0c4264010000")
        for i in (0xA6, 0xAA, 0xAE, 0xB3, 0xB5, 0xB7, 0xBA, 0xBD):
            self.amd[i] = 0
        self.amd[0x9F] = 1
        self.amd[0xA1] = 16
        self.amd[0xA2] = 18
        self.amd[0xA3] = 20
        self.amd[0xA0] = 36
        self.amd[0xB7] = 1
        self.amd[0xB8:0xBA] = (312).to_bytes(2, "little")
        self.write("AmdSetup", AMD, self.amd)
        self.write("Setup", SETUP, bytes(0x1BE))

    def tearDown(self):
        self.tmp.cleanup()

    def identity(self, board, bios, vendor, family, product):
        for name, value in dict(board_name=board, bios_version=bios, sys_vendor=vendor,
                                product_family=family, product_name=product).items():
            (self.dmi / name).write_text(value + "\n", encoding="utf-8")

    def write(self, name, guid, payload):
        p = self.efi / (name + "-" + guid)
        p.write_bytes(bytes.fromhex("07000000") + payload)
        return p

    def invoke(self, *args, code=0, live=True):
        before = {p.name: p.read_bytes() for p in self.efi.iterdir()} if self.efi.exists() else {}
        cmd = ["dotnet", str(CLI), *args]
        if live:
            cmd += ["--sysfs-root", str(self.root)]
        p = subprocess.run(cmd, text=True, capture_output=True, timeout=30)
        self.assertEqual(p.returncode, code, p.stdout + p.stderr)
        after = {p.name: p.read_bytes() for p in self.efi.iterdir()} if self.efi.exists() else {}
        self.assertEqual(before, after, "CLI must not modify fixture variables")
        return p

    def result(self, code=0):
        return json.loads(self.invoke("--json", code=code).stdout)

    def test_lenovo_live_debug_and_replay(self):
        j = self.result()
        self.assertTrue(j["matched"])
        self.assertEqual(j["board"], "ThinkCentre M75n")
        self.assertEqual(j["layout_id"], "lenovo-m75n-m33")
        self.assertEqual(j["rows"]["tCL"]["saved_number"], 16)
        self.assertEqual(j["rows"]["tRCDWR"]["saved_number"], 20)
        self.assertEqual(j["rows"]["tRFC"]["saved_number"], 312)
        self.assertIsNone(j["rows"]["tCL"]["running"])
        self.assertIsNone(j["rows"]["tCL"]["differs_from_running"])
        self.assertFalse(j["running_available"])
        text = self.invoke("--origins").stdout
        self.assertIn("M·CBS", text)
        self.assertNotIn(".base64:", text)
        debug = self.invoke("--debug")
        self.assertIn("WARNING", debug.stderr)
        self.assertIn("attributes: 0x00000007; length: 1343", debug.stdout)
        self.assertIn(hashlib.sha256(self.amd).hexdigest(), debug.stdout)
        self.assertIn("AmdSetup.base64: " + base64.b64encode(self.amd).decode(), debug.stdout)
        capture = self.root / "capture report.txt"
        capture.write_text(debug.stdout, encoding="utf-8")
        replay = json.loads(self.invoke("--replay", str(capture), "--json", live=False).stdout)
        self.assertEqual(j, replay)
        self.assertNotIn(".base64:", json.dumps(j))

    def test_optional_missing_required_missing_and_bad_header(self):
        (self.efi / ("Setup-" + SETUP)).unlink()
        self.assertTrue(self.result()["matched"])
        path = self.efi / ("AmdSetup-" + AMD)
        path.write_bytes(b"\x07\x00\x00")
        j = self.result(code=1)
        self.assertFalse(j["matched"])
        self.assertEqual(j["rows"], {})
        self.assertIn("truncated", j["status"])
        self.write("AmdSetup", AMD, self.amd[4:])
        self.assertFalse(self.result(code=1)["matched"])
        self.amd[0] ^= 1
        self.write("AmdSetup", AMD, self.amd)
        self.assertFalse(self.result(code=1)["matched"])
        path.unlink()
        self.assertFalse(self.result(code=1)["matched"])

    def test_unknown_bios_and_other_vendor(self):
        self.identity("1234", "M33KT25A", "LENOVO", "ThinkCentre M75n", "11BS0000")
        self.assertFalse(self.result(code=1)["matched"])
        self.identity("1234", "M33KT24A", "OTHER", "ThinkCentre M75n", "11BS0000")
        self.assertFalse(self.result(code=1)["matched"])
        # Diagnostic capture is independent of resolver matching.
        self.assertIn("AmdSetup.base64:", self.invoke("--debug").stdout)

    def test_asrock_native_setup(self):
        self.identity("X670E Steel Legend", "1.21", "ASRock", "", "")
        cbs = bytearray(0x6D6)
        aod = bytearray(0x1AB)
        setup = bytearray(0x1EE)
        cbs[0x116] = aod[0x3C] = 255
        setup[0x1CE] = 0
        setup[0x1CF] = 255
        setup[0x16C:0x16E] = (36).to_bytes(2, "little")
        self.write("AmdSetupRPL", AMD, cbs)
        self.write("AodSetupRpl", AOD, aod)
        self.write("Setup", SETUP, setup)
        j = self.result()
        self.assertEqual(j["rows"]["tCL"]["origin"], "Setup")
        self.assertEqual(j["rows"]["tCL"]["saved_number"], 36)
        self.write("Setup", SETUP, setup[:-1])
        self.assertFalse(self.result(code=1)["matched"])

    def test_temperature_only_and_watch(self):
        bus = self.root / "bus/i2c/devices"
        bus.mkdir(parents=True)
        target = self.root / "bus/i2c/drivers/spd5118"
        target.mkdir(parents=True)
        device = bus / "2-0050"
        hw = device / "hwmon/hwmon17"
        hw.mkdir(parents=True)
        (device / "driver").symlink_to(target, target_is_directory=True)
        (hw / "name").write_text("spd5118\n")
        sensor = hw / "temp1_input"
        sensor.write_text("46750\n")
        # No writer: an unintended open/read of the EEPROM or DMI FIFO
        # would block and fail the subprocess timeout.
        os.mkfifo(device / "eeprom")
        dmi = self.root / "firmware/dmi/tables"
        dmi.mkdir(parents=True)
        os.mkfifo(dmi / "DMI")
        report = json.loads(self.invoke("--temperatures", "--json").stdout)["dimm_temperatures"]
        self.assertTrue(report["available"])
        self.assertEqual(report["sensors"][0]["temperature_millicelsius"], 46750)
        lines = self.invoke("--temperatures", "--watch", "--interval", "1",
                            "--samples", "2", "--json").stdout.splitlines()
        self.assertEqual(len(lines), 2)
        for idx, line in enumerate(lines, 1):
            j = json.loads(line)
            self.assertEqual(j["watch_schema_version"], 1)
            self.assertEqual(j["sample_index"], idx)
            self.assertEqual(j["result"]["dimm_temperatures"], report)
        self.assertEqual(sensor.read_text(), "46750\n")
        self.assertIn("46.75 °C", self.invoke("--temperatures").stdout)
        sensor.write_text("bad")
        lines = self.invoke("--temperatures", "--watch", "--samples", "2", "--json", code=1).stdout.splitlines()
        self.assertEqual(len(lines), 1)
        self.assertFalse(json.loads(lines[0])["result"]["dimm_temperatures"]["available"])
        for flag in ("--modules", "--timings", "--compare", "--pm-table", "--mcr", "--debug", "--apob"):
            self.invoke("--temperatures", flag, code=2)

    def test_dimm_temperatures(self):
        bus = self.root / "bus/i2c/devices"
        bus.mkdir(parents=True)
        def device(name, driver, readings):
            dev = bus / name
            dev.mkdir()
            target = self.root / "bus/i2c/drivers" / driver
            target.mkdir(parents=True, exist_ok=True)
            (dev / "driver").symlink_to(target, target_is_directory=True)
            for idx, (sensor_name, value) in enumerate(readings):
                hw = dev / "hwmon" / ("hwmon" + str(idx))
                hw.mkdir(parents=True)
                (hw / "name").write_text(sensor_name + "\n")
                (hw / "temp1_input").write_text(value)
            return dev
        a = device("1-0050", "spd5118", [("spd5118", "42750\n")])
        b = device("1-0051", "spd5118", [("spd5118", "-1250\n")])
        device("1-0052", "spd5118", [])
        device("1-0053", "spd5118", [("spd5118", "999999\n")])
        device("1-0054", "spd5118", [("spd5118", "42000"), ("spd5118", "43000")])
        device("1-0055", "spd5118", [("other", "42000")])
        device("1-0056", "ee1004", [("spd5118", "50000")])
        device("1-0057", "at24", [("spd5118", "50000")])
        device("1-0040", "spd5118", [("spd5118", "50000")])
        def temperatures(code=0):
            out = json.loads(self.invoke("--modules", "--json", code=code).stdout)["memory_inventory"]
            self.assertTrue(all(x["base_crc_valid"] is None for x in out["spd"]))
            return {x["device"]: x for x in out["dimm_temperatures"]}
        before = {p: p.read_bytes() for p in bus.glob("*/hwmon/hwmon*/temp1_input")}
        j = temperatures()
        self.assertEqual(set(j), {"1-005"+str(i) for i in range(6)})
        self.assertEqual(j["1-0050"]["temperature_millicelsius"], 42750)
        self.assertEqual(j["1-0051"]["temperature_millicelsius"], -1250)
        for i in range(2,6):
            self.assertIsNone(j["1-005"+str(i)]["temperature_millicelsius"])
        self.assertIn("ambiguous", j["1-0054"]["status"])
        self.assertIn("42.75 °C", self.invoke("--modules").stdout)
        self.assertEqual(before, {p: p.read_bytes() for p in before})
        # Sensor success is independent of absent EEPROM / DMI, and
        # malformed readings must not become plausible zero temperatures.
        (a / "hwmon/hwmon0/temp1_input").write_text("42.75")
        (b / "hwmon/hwmon0/temp1_input").write_text("42001")
        j = temperatures(code=1)
        self.assertIsNone(j["1-0050"]["temperature_millicelsius"])
        self.assertIsNone(j["1-0051"]["temperature_millicelsius"])
        (a / "hwmon/hwmon0/temp1_input").write_text("0")
        self.assertEqual(temperatures()["1-0050"]["temperature_millicelsius"], 0)
        (a / "hwmon/hwmon0/temp1_input").write_text("1"*33)
        self.assertIsNone(temperatures(code=1)["1-0050"]["temperature_millicelsius"])
        (a / "hwmon/hwmon0/temp1_input").unlink()
        self.assertIn("unavailable", temperatures(code=1)["1-0050"]["status"])

    def test_memory_inventory(self):
        import binascii
        import struct

        def dimm(handle, size, loc, memory=0x1a, length=0x28):
            b = bytearray(length)
            b[:4] = bytes((17, length)) + struct.pack("<H", handle)
            for off, value in ((8, 64), (10, 64), (12, size), (0x15, 2667),
                               (0x20, 2400), (0x26, 1200)):
                if off+2 <= length:
                    struct.pack_into("<H", b, off, value)
            b[0xe] = 0x0d
            b[0x10:0x12] = bytes((1, 2))
            b[0x12] = memory
            b[0x17:0x1b] = bytes((3, 4, 5, 6))
            b[0x1b] = 1
            return b + b"\x00".join([loc.encode(), b"BANK", b"Micron", b"PRIVATE-SERIAL",
                                      b"PRIVATE-ASSET", b"4ATF51264HZ-2G6E1"]) + b"\x00\x00"

        table = self.root / "firmware/dmi/tables/DMI"
        table.parent.mkdir(parents=True)
        end = bytes((127, 4, 0xff, 0xff, 0, 0))
        table.write_bytes(dimm(1, 4096, "DIMM 0") + dimm(2, 4096, "DIMM 1") +
                          dimm(3, 0, "EMPTY") + end)
        bus = self.root / "bus/i2c/devices"
        bus.mkdir(parents=True)
        def inventory(code=0):
            out = self.invoke("--modules", "--json", code=code).stdout
            self.assertNotIn("PRIVATE-SERIAL", out)
            self.assertNotIn("PRIVATE-ASSET", out)
            return json.loads(out)["memory_inventory"]

        inv = inventory()
        self.assertEqual(inv["smbios_status"], "available")
        self.assertEqual(len(inv["modules"]), 2)
        for m in inv["modules"]:
            self.assertEqual(m["capacity_bytes"], 4*1024**3)
            self.assertEqual(m["ranks"], 1)
            self.assertEqual(m["memory_type"], "DDR4")
            self.assertEqual(m["form_factor"], "SODIMM")
            self.assertEqual(m["rated_mts"], 2667)
            self.assertEqual(m["configured_mts"], 2400)
            self.assertEqual(m["configured_mv"], 1200)
            self.assertEqual(m["part_number"], "4ATF51264HZ-2G6E1")
        self.assertEqual(inv["spd"], [])
        self.assertIn("no bound SPD", inv["spd_status"])

        def spd_device(name, driver, data):
            p = bus / name
            p.mkdir()
            target = self.root / "bus/i2c/drivers" / driver
            target.mkdir(parents=True, exist_ok=True)
            (p / "driver").symlink_to(target, target_is_directory=True)
            (p / "eeprom").write_bytes(data)
            return p / "eeprom"

        d4 = bytearray(512)
        d4[1:3] = bytes((0x11, 0x0c))
        d4[18] = 6
        d4[320:322] = bytes((0, 0x2c))
        d4[329:349] = b"TEST-DDR4".ljust(20)
        d4[325:329] = b"ABCD"
        struct.pack_into("<H", d4, 126, binascii.crc_hqx(d4[:126], 0))
        struct.pack_into("<H", d4, 254, binascii.crc_hqx(d4[128:254], 0))
        e4 = spd_device("1-0050", "ee1004", d4)
        d5 = bytearray(1024)
        d5[1:3] = bytes((0x10, 0x12))
        struct.pack_into("<H", d5, 20, 357)
        d5[521:551] = b"TEST-DDR5".ljust(30)
        # CRC-covered synthetic device descriptors, no private SPD bytes.
        d5[194:198] = bytes((0, 0x2c, 0x80, 0x21))
        d5[198:202] = bytes((0, 0x2c, 0x82, 0x13))
        d5[202:206] = bytes((0, 0x2c, 0x02, 0x13)) # not listed despite populated stale descriptor
        d5[206:210] = b"\xff"*4
        d5[210:214] = bytes((0, 0, 0x41, 0xfa)) # TS1 only, invalid vendor and revision
        struct.pack_into("<H", d5, 510, binascii.crc_hqx(d5[:510], 0))
        e5 = spd_device("2-0051", "spd5118", d5)
        spd_device("3-0052", "at24", b"not an SPD driver")
        spd_device("3-0038", "ee1004", b"not an SPD address")
        inv = inventory()
        self.assertEqual(len(inv["spd"]), 2)
        self.assertEqual(inv["spd"][0]["part_number"], "TEST-DDR4")
        self.assertEqual(inv["spd"][0]["jedec_rate_from_tck_mts"], 2667)
        self.assertTrue(inv["spd"][0]["module_crc_valid"])
        self.assertEqual(inv["spd"][1]["memory_type"], "DDR5")
        self.assertEqual(inv["spd"][1]["tck_min_ps"], 357)
        self.assertTrue(inv["spd"][1]["base_crc_valid"])
        details = inv["spd"][1]["ddr5"]
        devices = {x["role"]: x for x in details["support_devices"]}
        self.assertEqual(devices["SPD"]["type"], "SPD5118")
        self.assertEqual(devices["PMIC0"]["type"], "PMIC5100")
        self.assertEqual(devices["PMIC0"]["revision"], "1.3")
        self.assertTrue(devices["PMIC0"]["listed"])
        self.assertFalse(devices["PMIC1"]["listed"])
        self.assertIsNone(devices["PMIC1"]["type"])
        self.assertIsNone(devices["PMIC2"]["listed"])
        self.assertTrue(devices["TS"]["listed"])
        self.assertIsNone(devices["TS"]["manufacturer"])
        self.assertIsNone(devices["TS"]["revision"])
        self.assertFalse(details["thermal_sensor_0_listed"])
        self.assertTrue(details["thermal_sensor_1_listed"])
        self.assertIn("SPD-listed PMIC0: PMIC5100", self.invoke("--modules").stdout)
        e5.write_bytes(d5[:512])
        self.assertEqual(inventory()["spd"][1]["ddr5"]["support_devices"], details["support_devices"])
        e5.write_bytes(d5)
        self.assertEqual(e4.read_bytes(), d4)
        self.assertEqual(e5.read_bytes(), d5)
        d4[18] ^= 1
        e4.write_bytes(d4)
        e5.write_bytes(d5[:100])
        inv = inventory()
        self.assertFalse(inv["spd"][0]["base_crc_valid"])
        self.assertIsNone(inv["spd"][0]["part_number"])
        self.assertIn("truncated", inv["spd"][1]["status"])
        table.write_bytes(b"\x11\x28\x00")
        inv = inventory(code=1)
        self.assertEqual(inv["modules"], [])
        self.assertIn("Truncated", inv["smbios_status"])
        self.invoke("--modules", "--origins", code=2)
        self.invoke("--modules", "--debug", code=2)

    def test_apob_offline(self):
        import struct
        import binascii

        def entry(group, kind, size, instance=0):
            b = bytearray(size)
            struct.pack_into("<IIII", b, 0, group, kind, instance, size)
            return b

        cfg = entry(1, 2, 256)
        struct.pack_into("<HH", cfg, 48+4, 1200, 2400)
        # Zen1 DDR4: four channel slots; one populated.
        start = 48+0x98
        for i, value in enumerate([14,1,1,3,0,0,0,1,1,1,1]):
            cfg[start+i*4] = value
        spd = entry(1, 17, 48+4+532)
        spd[48:50] = bytes((1,1))
        rec = 52
        spd[rec+4] = 1
        struct.pack_into("<I", spd, rec+8, 0x50)
        spd[rec+12:rec+16] = b"SECR"
        data = rec+20
        spd[data+1:data+3] = bytes((0x11,0x0c))
        spd[data+18] = 6
        spd[data+325:data+329] = b"ABCD"
        spd[data+329:data+349] = b"TEST-APOB-DDR4".ljust(20)
        struct.pack_into("<H", spd, data+126, binascii.crc_hqx(spd[data:data+126],0))
        header = bytearray(0x60)
        def table(*entries):
            struct.pack_into("<IIII",header,0,0x424f5041,1,len(header)+sum(map(len,entries)),len(header))
            return header+b"".join(entries)

        f = self.root / "apob.bin"
        def run(blob, code=0):
            f.write_bytes(blob)
            p = self.invoke("--apob-file",str(f),"--json",code=code,live=False)
            self.assertEqual(f.read_bytes(),blob)
            self.assertNotIn("SECR",p.stdout)
            self.assertNotIn("ABCD",p.stdout)
            return json.loads(p.stdout)["apob"]

        j = run(table(cfg,spd))
        self.assertTrue(j["available"])
        self.assertEqual(len(j["entries"]),2)
        self.assertEqual(len(j["electrical"]),1)
        self.assertEqual(j["electrical"][0]["memclk_mhz"],1200)
        self.assertEqual(j["electrical"][0]["settings"]["ProcODT"]["code"],14)
        self.assertEqual(j["electrical"][0]["settings"]["RttNom"]["code"],1)
        self.assertEqual(len(j["spd"]),1)
        self.assertTrue(j["spd"][0]["data"]["base_crc_valid"])
        self.assertEqual(j["spd"][0]["data"]["part_number"],"TEST-APOB-DDR4")
        encrypted = bytearray(cfg)
        struct.pack_into("<I",encrypted,0,0xffff0001)
        j = run(table(encrypted,spd))
        self.assertEqual(j["electrical"],[])
        self.assertTrue(j["entries"][0]["encrypted"])
        j = run(table(cfg,cfg))
        self.assertEqual(j["electrical"],[])
        j = run(table(cfg,spd)[:-1],code=1)
        self.assertFalse(j["available"])
        self.assertFalse(run(b"not APOB",code=1)["available"])
        # Picasso APOB v0x13, M33KT24A: 1/17 is not SPD; 1/18 is
        # a 2180-byte array of four records, two populated. Synthetic SPD
        # and no unique hardware identifiers; match the captured shape.
        legacy17 = entry(1, 17, 120)
        legacy17[48:52] = bytes((3,0,0,0))
        legacy18 = entry(1, 18, 2180)
        legacy18[48:52] = bytes((1,2,0,0))
        for ch in range(2):
            at = 52+532*ch
            legacy18[at:at+532] = spd[52:584]
            legacy18[at+1] = ch
        def legacy_table(version=0x13):
            h=bytearray(0xc0)
            struct.pack_into("<IIII",h,0,0x424f5041,version,len(h)+len(cfg)+len(legacy17)+len(legacy18),len(h))
            return h+cfg+legacy17+legacy18
        j=run(legacy_table())
        self.assertEqual(len(j["spd"]),2)
        self.assertEqual([x["channel"] for x in j["spd"]],[0,1])
        self.assertTrue(all(x["data"]["base_crc_valid"] and x["data"]["module_crc_valid"] for x in j["spd"]))
        self.assertEqual(run(legacy_table(0x14))["spd"],[])
        legacy18[52+20+18] ^= 1
        self.assertEqual(run(legacy_table())["spd"],[])
        legacy18[52+20+18] ^= 1
        legacy18[52+532+1] = 0
        self.assertEqual(run(legacy_table())["spd"],[])
        self.invoke("--apob-file",str(f),"--compare",code=2,live=False)
        self.invoke("--apob",code=2) # no live fixture override

    def test_persisted_apob_image_and_real_ddr4_spd(self):
        import struct
        # M33KT24A SPD from the AFU APOB, with serial bytes 325..328 cleared.
        # Both installed modules have identical SPD after masking.
        d = bytearray(512)
        for offset, chunk in (
            (0, "23110c03452100080060000302030000"),
            (16, "0000060df8ff01006e6e6e11006ef00a"),
            (32, "2008000500f02b3428007800143c0000"),
            (112, "0000000000009cb500000000e7008011"),
            (128, "0f010200000000000000000000000000"),
            (240, "0000000000000000000000000000c0e2"),
            (320, "802c0000000000000034415446353132"),
            (336, "3634485a2d324736453120200031802c"),
        ):
            d[offset:offset+16] = bytes.fromhex(chunk)
        self.assertEqual(hashlib.sha256(d).hexdigest(),
                         "e644f5ae5d419ff9f1625248a357375db85acf5079d95f61411d791646112f97")
        self.assertEqual(len(d),512)
        self.assertEqual(d[325:329],bytes(4))
        def entry(group,kind,size):
            b=bytearray(size)
            struct.pack_into("<IIII",b,0,group,kind,0,size)
            return b
        cfg=entry(1,2,256)
        struct.pack_into("<HH",cfg,52,1200,1200)
        for i,value in enumerate([24,0,0,5,0,0,0,15,15,15,15]):
            cfg[48+0x98+4*i]=value
        other=entry(1,17,120)
        spd=entry(1,18,2180)
        spd[48:50]=bytes((1,2))
        for ch in range(2):
            off=52+532*ch
            spd[off+1]=ch
            spd[off+4]=1
            spd[off+20:off+532]=d
        h=bytearray(0xc0)
        struct.pack_into("<IIII",h,0,0x424f5041,0x13,len(h)+len(cfg)+len(other)+len(spd),len(h))
        table=h+cfg+other+spd
        f=self.root/"spi image.bin"
        def run(blob,code=0):
            f.write_bytes(blob)
            p=self.invoke("--apob-image",str(f),"--json",code=code,live=False)
            self.assertEqual(f.read_bytes(),blob)
            return json.loads(p.stdout)["apob"]
        prefix=b"APOB"+bytes(4092)
        j=run(prefix+table+bytes(64))
        self.assertTrue(j["available"])
        self.assertEqual(j["image_offset"],"0x00001000")
        self.assertEqual(j["electrical"][0]["settings"]["ProcODT"]["code"],24)
        self.assertEqual(len(j["spd"]),2)
        for x in j["spd"]:
            sp=x["data"]
            self.assertEqual(sp["part_number"],"4ATF51264HZ-2G6E1")
            self.assertTrue(sp["base_crc_valid"] and sp["module_crc_valid"])
            details=sp["ddr4"]
            self.assertEqual(details["capacity_mib"],4096)
            self.assertEqual(details["package_ranks"],1)
            self.assertEqual(details["device_width_bits"],16)
            self.assertEqual(details["jedec_speed_mts"],2667)
            self.assertEqual(details["xmp_profiles"],[])
            self.assertEqual(details["xmp_status"],"not present")
            self.assertNotIn("serial",json.dumps(details).lower())
        # Sparse replay fixture: only the independently matched public UMC
        # words/markers from M33KT24A, no original opaque payload or hashes.
        gen3=entry(7,3,5356)
        # GEN 7/3: real timing and MR values in a synthetic payload.
        for g,base in enumerate((0xaf8,0xc88,0xde8,0xf50)):
            for i,(clk,timings,mr0,mr2,mr6) in enumerate((
                (1200,(17,12,17,17,9,39,56,18,7,3,36,8,9,6),0x096d,0x00d8,0x080e),
                (1067,(15,11,15,15,8,35,50,16,6,3,32,7,8,6),0x0739,0x00d0,0x080e),
                (933,(13,10,13,13,7,30,43,14,5,3,29,6,7,5),0x0529,0x00c8,0x040e),
                (800,(11,9,11,11,6,26,37,12,5,2,28,6,6,5),0x0319,0x00c0,0x040e),
            )):
                o=48+base+i*0x54
                struct.pack_into("<12H",gen3,o-24,550,550,300,1280,1830,600,
                                 212,100,1400 if i==3 else 1200,256,300,(200,224,214,250)[i])
                struct.pack_into("<HH",gen3,o,clk,0)
                gen3[o+4:o+18]=bytes(timings)
                struct.pack_into("<III",gen3,o+0x12,350,260,160)
                struct.pack_into("<7H",gen3,o+0x2a,mr0,1,mr2,0,8,0x0560,mr6-(g%2))
        # Observed legacy metadata correlations; no channel semantics assigned.
        for i,o in enumerate((0xab0,0xc40)):
            struct.pack_into("<24H",gen3,48+o,i,1,0,0,0,1,1,0,0,0,0,0,1,0,0,1,
                             1200,1333,14-i,0,0,0,0,1)
        replay=entry(7,4,13904)
        struct.pack_into("<I",replay,48+16,1454)
        def pair(buf,offset,a,v):
            struct.pack_into("<II",buf,48+offset,a,v)
        pair(replay,0x20,0x1d040,2)
        pair(replay,0x28,0x50100,0x200)
        pair(replay,0x30,0x150100,0x200)
        pair(replay,0x38,0xffffdead,0x16)
        pair(replay,0x40,0xffffdead,0x14)
        for ch in range(2):
            off=ch<<20
            for i,(at,a,v) in enumerate((
                (0x7e8,0x50204,0x11112711),
                (0x7f0,0x50214,0x0009030c),
                (0x7f8,0x50208,0x00110038),
                (0x800,0x5020c,0x09000807),
                (0x808,0x50218,0x12),
                (0x810,0x50210,0x24),
                (0x818,0x50260,0x3049c9a5),
                (0x820,0x5021c,0x800),
                (0x828,0x50238,0x030001b1),
                (0x830,0x50200,0x324),
                (0x840,0x5022c,0x00020080),
                (0x848,0x50230,0x2494),
                (0x850,0x50234,0x18101808),
                (0x858,0x50254,0x06040008),
                (0x860,0x50244,0x3f310006),
                (0x870,0x50258,0x021f070c),
                (0x878,0x50250,0x00090000),
                (0x8a8,0x50220,0x8f0f0f0f),
                (0x8b0,0x50224,0xbf0f0f0f),
                (0x8b8,0x50228,0x1f0f),
                (0x9f8,0x50200,0x124),
                (0xc88,0x5022c,0x09320080),
                (0xcd8,0x50220,0x03010303),
                (0xce0,0x50224,0x03010505),
                (0xce8,0x50228,0xa00),
                (0xd10,0x50258,0x0214070c),
                (0x60,0x50050,0xba954321),
                (0x80,0x500d0,0x24924001),
                (0xe8,0x50000,1),
                (0x2058,0x5012c,0x71100460),
            )):
                # Deliberately sparse, shifted second channel. Preserve
                # ordering for each register rather than actual all offsets.
                pair(replay,0x100+ch*0x1000+8*i,off|a,v)
        pair(replay,0x2d88,0x1c504,3)
        # Do not scan the opaque tail even if it contains a known address.
        pair(replay,0x2d90,0x50204,0xdeadbeef)
        # Synthetic common header (zeros except count), valid delimiters.
        companions=[]
        for t in range(10,15):
            a=entry(1,t,2768)
            n=329 if t<14 else 307
            struct.pack_into("<I",a,48+16,n)
            pair(a,0x20,0xffffdead,7+2*(t-10))
            pair(a,0x20+n*8-16,0xffffdead,0x20)
            pair(a,0x20+n*8-8,0xffffdead,0x17)
            # Two known repeated motifs in synthetic body; never decode
            # their contents as PHY commands.
            struct.pack_into("<12H",a,48+0x28,0xfff,0xfff,0x5b0,0x5b0,0xfff,0xfff,0,0,0x18,0x18,0x2f7,0x2f7)
            struct.pack_into("<12H",a,48+0x68,0xfff,0xfff,0x5b1,0x5b1,0xfff,0xfff,0,0,0x18,0x18,0x2f7,0x2f7)
            companions.append(a)
        def with_config(rep=replay,comps=companions,version=0x13,gen=gen3):
            out=h+cfg+other+spd+gen+rep+b"".join(comps)
            struct.pack_into("<II",out,4,version,len(out))
            return run(prefix+out)
        def with_replay(**kw):
            return with_config(**kw)["replay"]
        gc=with_config()["gen_config"]
        self.assertIsNotNone(gc)
        self.assertEqual(len(gc["groups"]),4)
        meta=gc["metadata_observations"]["observations"]
        self.assertEqual([m["index_word"] for m in meta],[0,1])
        self.assertEqual([m["word_22"] for m in meta],[1333,1333])
        self.assertEqual([m["equal_group_indices"] for m in meta],[[0,2],[1,3]])
        self.assertTrue(all(m["word_22_matches_all_spd_rated_clocks"] for m in meta))
        # Bad metadata must not invalidate decoded timings.
        badmeta=bytearray(gen3)
        badmeta[48+0xad2]^=1<<3
        self.assertIsNone(with_config(gen=badmeta)["gen_config"]["metadata_observations"])
        badcopy=bytearray(gen3)
        badcopy[48+0xde8+0x1e]^=1 # undecoded byte in a replicated group
        self.assertIsNone(with_config(gen=badcopy)["gen_config"]["metadata_observations"])
        for comp in with_replay()["companions"]:
            st=comp["legacy_structure"]
            self.assertEqual(st["motif_05b0_count"],1)
            self.assertEqual(st["motif_05b1_count"],1)
            self.assertEqual(st["adjacent_u16_pairs"],652 if comp["type"]<14 else 608)
            self.assertEqual(st["equal_adjacent_u16_pairs"],st["adjacent_u16_pairs"])
            self.assertEqual(st["observed_marker_ids"],[7+2*(comp["type"]-10),0x20,0x17])
        for g in gc["groups"]:
            self.assertEqual([st["memclk_mhz"] for st in g["states"]],[1200,1067,933,800])
            self.assertEqual([st["nominal_rate_mts"] for st in g["states"]],[2400,2133,1867,1600])
            self.assertEqual([st["matches_boot_memclk"] for st in g["states"]],[True,False,False,False])
            self.assertEqual([st["timings_clocks"]["tCL"] for st in g["states"]],[17,15,13,11])
            self.assertEqual(g["states"][0]["config_min_ps"]["tRCD"],13750)
            self.assertEqual(g["states"][3]["config_min_ps"]["tFAW"],35000)
            for st in g["states"]:
                self.assertEqual(st["refresh_ns"],{"tRFC":350,"tRFC2":260,"tRFC4":160})
                mr=st["mode_registers"]
                self.assertEqual(mr["dram_drive_ohm"],34)
                self.assertEqual(mr["rtt_park_code"],5)
                self.assertAlmostEqual(mr["initial_vref_dq_percent_vddq"],69.1 if g["group_index"]%2==0 else 68.45)
        badgen=bytearray(gen3)
        badgen[48+0xaf8+0x2a]=0 # invalid MR0 CL
        self.assertIsNone(with_config(gen=badgen)["gen_config"])
        self.assertIsNotNone(with_replay(gen=badgen))
        self.assertIsNone(with_config(version=0x14)["gen_config"])
        rr=with_replay()
        self.assertIsNotNone(rr)
        self.assertEqual(rr["record_count"],1454)
        self.assertEqual(rr["end_exclusive"],0x2d90)
        self.assertEqual(rr["opaque_trailing_bytes"],2192)
        self.assertEqual(len(rr["companions"]),5)
        self.assertEqual(len(rr["channels"]),2)
        for ch,c in enumerate(rr["channels"]):
            self.assertEqual(c["umc_address_index"],ch)
            self.assertEqual(c["nominal_mclk_mhz"],1200)
            self.assertEqual(c["candidate_timings"]["tCL"],17)
            self.assertEqual(c["candidate_timings"]["tRC"],56)
            self.assertEqual(c["candidate_timings"]["tRDRDSCL"],3)
            self.assertNotIn("tRFC",c["candidate_timings"])
            self.assertNotIn("BGS",c["candidate_extended_fields"])
            key=f"0x{(ch<<20)|0x50260:08X}"
            self.assertEqual(c["refresh_word_candidates"][key],{"tRFC":421,"tRFC2":313,"tRFC4":193})
            key=f"0x{(ch<<20)|0x50220:08X}"
            self.assertEqual([x["word"] for x in c["observed_registers"][key]["history"]],
                             ["0x8F0F0F0F","0x03010303"])
            self.assertIn(f"0x{(ch<<20)|0x50264:08X}",c["missing_registers"])
            self.assertEqual(len(c["observed_registers"]),25)
            self.assertEqual(len(c["missing_registers"]),10)
        # End-to-end correlation of a valid existing image and complete
        # schema-1 UMC report. Only selected synthetic/sanitized words.
        bank={0x50000:1,0x50008:0,0x50100:0x80000200,0x5012c:0x71100460,
              0x50200:0x124,0x50204:0x11112711,0x50208:0x00110038,
              0x5020c:0x09000807,0x50210:0x24,0x50214:0x0009030c,
              0x50218:0x12,0x50220:0x03010303,0x50224:0x03010505,
              0x50228:0xa00,0x50230:0x2494,0x50254:0x06040008,
              0x50260:0x3049c9a5,0x50264:0x21060138,0x50df0:0x20024}
        snapshot={"schema_version":1,"running_available":True,"experimental":True,
                  "source":"ryzen_smu","cpu":{"Vendor":"AuthenticAMD","Family":23,"Model":24},
                  "private_extra":"NOT-FOR-EXPORT",
                  "channels":[{"channel":ch,"memory_type":"DDR4","extended":None,
                               "registers":{f"0x{a|(ch<<20):08X}":f"0x{v:08X}" for a,v in bank.items()}}
                              for ch in range(2)]}
        report=self.root/"private-timings.json"
        report.write_text(json.dumps(snapshot))
        before=f.read_bytes()
        p=self.invoke("--apob-image",str(f),"--umc-report",str(report),live=False)
        result=json.loads(p.stdout)["apob_umc_correlation"]
        self.assertTrue(result["available"])
        self.assertNotIn("NOT-FOR-EXPORT",p.stdout)
        self.assertNotIn("private-timings",p.stdout)
        self.assertNotIn("0x80000200",p.stdout)
        self.assertEqual(f.read_bytes(),before)
        for ch,row in enumerate(result["channels"]):
            expected=set(snapshot["channels"][ch]["registers"]) & set(rr["channels"][ch]["observed_registers"])
            self.assertEqual(row["compared_registers"],len(expected))
            self.assertEqual(row["equal_registers"],len(expected)-1)
            self.assertEqual(row["differing_register_addresses"],[f"0x{0x50100|(ch<<20):08X}"])
            self.assertTrue(row["config_bit31_only_difference"])
            self.assertEqual(row["timing_fields"]["differing_fields"],[])
            self.assertEqual(row["running_nominal_mclk_mhz"],1200)
        # No arbitrary report field is trusted to bypass word validation.
        del snapshot["channels"][0]["registers"]["0x00050204"]
        report.write_text(json.dumps(snapshot))
        p=self.invoke("--apob-image",str(f),"--umc-report",str(report),code=1,live=False)
        self.assertFalse(json.loads(p.stdout)["apob_umc_correlation"]["available"])
        bad=bytearray(replay)
        struct.pack_into("<I",bad,48+16,0xffffffff)
        self.assertIsNone(with_replay(rep=bad))
        bad=bytearray(replay)
        struct.pack_into("<I",bad,48+16,1455)
        self.assertIsNone(with_replay(rep=bad))
        bad=bytearray(companions[0])
        bad[-1]=1
        self.assertIsNone(with_replay(comps=[bad]+companions[1:]))
        self.assertIsNone(with_replay(comps=companions+[companions[0]]))
        self.assertIsNone(with_replay(version=0x14))
        self.assertFalse(run(prefix+table[:-1],code=1)["available"])
        self.assertFalse(run(prefix+table+table,code=1)["available"])
        self.assertFalse(run(prefix,code=1)["available"])
        self.invoke("--apob-image",str(f),"--apob",code=2,live=False)

    def test_ddr5_donor_apob(self):
        import struct
        import binascii
        def entry(g,t,size):
            b=bytearray(size)
            struct.pack_into("<IIII",b,0,g,t,0,size)
            return b
        # Real donor base CRC bytes, public module identification only.
        # No original serial, manufacturing location/date or vendor extension.
        d=bytearray(1024)
        for off,hx in (
            (0,"30101203040040420000000090020000"),
            (16,"00000000a001f2037a0d00000000803e"),
            (32,"803e803e007d80bb30752701a0008200"),
            (64,"000000000000881308881308204e2010"),
            (80,"27101a4128102710c409044c1d0c0000"),
            (192,"10008632801580ce8214000000000000"),
            (224,"0000000000000f110281002200000000"),
            (496,"00000000000000000000000000000925"),
        ):
            d[off:off+16]=bytes.fromhex(hx)
        d[512:514]=bytes.fromhex("80ce")
        d[521:551]=b"M425R1GB4BB0-CQKOL".ljust(30)
        d[552:554]=bytes.fromhex("80ce")
        self.assertEqual(binascii.crc_hqx(d[:510],0),0x2509)
        spd=entry(1,17,4196)
        spd[48:50]=bytes((1,2))
        for ch in range(2):
            o=52+ch*1036
            spd[o+1]=1
            spd[o+9]=ch
            spd[o+12:o+1036]=d
        main=entry(1,25,96)
        m=bytes.fromhex("01000000060605000000010705070c1c0c1e5d5834340100")
        main[48:]=m*2
        gen=entry(7,3,6020)
        fields=(0x0c,0x10,0x14,0x18,0x1c,0x20,0x24,0x28,0x2c,0x30,
                0x34,0x38,0x3c,0x40,0x5c,0x64,0x68,0x6c,0xc4,0xc8,
                0x100,0x104,0x108,0x118,0x11c)
        for g in range(2):
            base=0x630+g*0x7a0
            for i,(clk,values,refresh) in enumerate((
                (2400,(40,39,39,39,77,116,38,8,12,40,24,6,18,72,732,18,18,9347,13,38,12,48,24,1536,264),(708,384,312)),
                (1600,(26,26,26,26,52,77,24,8,8,40,16,4,12,48,487,12,12,6222,13,25,8,32,16,1024,176),(471,256,208)),
                (1000,(22,16,16,16,32,48,20,8,8,40,16,4,12,48,305,8,8,3889,13,16,8,32,16,1024,176),(295,160,130)),
            )):
                o=48+base+i*0x164
                struct.pack_into("<II",gen,o,clk,clk//2)
                for off,v in zip(fields,values):
                    struct.pack_into("<I",gen,o+off,v)
                for off in (0x84,0x8c,0x98):
                    struct.pack_into("<I",gen,o+off,refresh[0])
                for off in (0x90,0x9c):
                    struct.pack_into("<I",gen,o+off,refresh[1])
                for off in (0xa8,0xb0):
                    struct.pack_into("<I",gen,o+off,refresh[2])
                e=48+base+0x58f+i*0x19
                ext=bytes.fromhex((
                    "00000000060605000000010705070c1c0c1e1e1e5d58343401",
                    "00000000040404010000010505070c1e0c1e1e1e633f272700",
                    "00000000040404010000010505070c1e0c1e1e1e633f272700",
                )[i])
                self.assertEqual(len(ext),25)
                gen[e:e+25]=ext
        self.assertEqual(len(gen),6020)
        rep=entry(7,4,15312)
        def pair(buf,offset,a,v):
            struct.pack_into("<II",buf,48+offset,a,v)
        def header(buf,n):
            struct.pack_into("<8I",buf,48,0x40004,0,1,0,n,0,0x80008,0x80101)
        header(rep,1081)
        pair(rep,0x20,0xdead,0x1a)
        pair(rep,0x38,0xdead,0x1b)
        for ch in range(2):
            for i,(a,v) in enumerate((
                (0x50100,0x201),(0x50100,0x701),(0x5012c,0x15381148),
                (0x50200,0x80050960),(0x50204,0x27274d28),(0x50208,0x270074),
                (0x5020c,0x12000c08),(0x50210,0x28),(0x50214,0x180626),
                (0x50218,0x48),(0x50220,0x05010606),(0x50224,0x11010f0f),
                (0x50228,0x1007),(0x50230,0x302483),(0x50260,0x018002c4),
                (0x502c0,0x00480138),(0x50284,0x00000220),
                (0x50198,0x68),(0x502e0,0x11),
            )):
                pair(rep,0x100+ch*0x1000+i*8,a|(ch<<20),v)
        pair(rep,0x21e0,0x6a3b4,0x437)
        pair(rep,0x21e8,0x50204,0xdeadbeef) # outside bounded records
        companions=[]
        for t,n in ((30,1280),(31,1092)):
            x=entry(1,t,10320)
            header(x,n)
            pair(x,0x20,0xdead,0xb)
            companions.append(x)
        def run(config=gen,rr=rep,mm=main,ss=spd,comps=companions,version=0x18,region_offset=4096,region_size=None,extra=b''):
            h=bytearray(0xa8)
            out=h+ss+mm+config+rr+b"".join(comps)+extra
            struct.pack_into("<IIII",out,0,0x424f5041,version,len(out),len(h))
            f=self.root/"donor.bin"
            prefix=bytearray(b"APOB"+bytes(4092))
            tail=struct.pack("<II",1,0)+struct.pack("<IIQQ",0x20000063,len(out) if region_size is None else region_size,(1<<62)|region_offset,2**64-1)
            a=z=65535
            for (w,) in struct.iter_unpack("<H",tail):
                a=(a+w)%65535
                z=(z+a)%65535
            dr=b"$BL2"+struct.pack("<I",((z or 65535)<<16)|(a or 65535))+tail
            prefix[128:128+len(dr)]=dr
            image=prefix+out
            f.write_bytes(image)
            j=json.loads(self.invoke("--apob-image",str(f),"--json",live=False).stdout)["apob"]
            self.assertEqual(f.read_bytes(),image)
            self.assertEqual(j["image_offset"],"0x00001000")
            return j
        j=run()
        self.assertTrue(j["bios_directory"]["matched_nv_region"])
        self.assertFalse(run(region_offset=4080)["bios_directory"]["matched_nv_region"])
        self.assertFalse(run(region_size=64)["bios_directory"]["matched_nv_region"])
        self.assertEqual(len(j["spd"]),2)
        for s in j["spd"]:
            self.assertTrue(s["data"]["base_crc_valid"])
            d5=s["data"]["ddr5"]
            self.assertEqual(d5["capacity_mib"],8192)
            self.assertEqual(d5["device_width_bits"],16)
            self.assertEqual(d5["jedec_speed_mts"],4800)
            self.assertEqual(d5["profiles"],[])
        cfg=j["ddr5_config"]
        self.assertIsNotNone(cfg)
        self.assertEqual(len(cfg["groups"]),2)
        for g in cfg["groups"]:
            self.assertEqual(g["mem25_record"]["ProcOdt"]["code"],12)
            self.assertEqual([s["nominal_rate_mts"] for s in g["states"]],[4800,3200,2000])
            self.assertEqual([s["timings_clocks"]["tCL"] for s in g["states"]],[40,26,22])
            self.assertEqual(g["states"][0]["refresh_candidates_clocks"],{"tRFC":708,"tRFC2":384,"tRFCsb":312})
            self.assertEqual(g["states"][0]["refresh_slot_words"]["0x88"],0)
            self.assertEqual(g["states"][0]["electrical_candidates"]["ProcOdt"]["code"],12)
            self.assertEqual([s["electrical_candidates"]["RttPark"]["code"] for s in g["states"]],[6,4,4])
        rr=j["replay"]
        self.assertIsNotNone(rr)
        self.assertEqual(rr["layout"],"ddr5-donor-v0x18")
        self.assertEqual(rr["record_count"],1081)
        self.assertEqual(rr["end_exclusive"],0x21e8)
        self.assertEqual(rr["opaque_trailing_bytes"],6584)
        for c in rr["channels"]:
            self.assertEqual(c["candidate_extended_fields"]["UclkGtFclk"],1)
            self.assertEqual(c["candidate_extended_fields"]["BankGroupEn"],1)
            self.assertEqual(c["nominal_mclk_mhz"],2400)
            self.assertEqual(c["candidate_timings"]["tCL"],40)
            self.assertEqual(c["candidate_timings"]["tREFI"],9347)
            self.assertEqual(c["candidate_timings"]["GDM"],1)
            self.assertEqual(c["refresh_word_candidates"][f'0x{(c["umc_address_index"]<<20)|0x50260:08X}'],{"tRFC":708,"tRFC2":384})
            self.assertEqual(c["refresh_word_candidates"][f'0x{(c["umc_address_index"]<<20)|0x502c0:08X}'],{"tRFCsb":312})
        self.assertEqual(cfg["layout"],"ddr5-donor-v0x18")
        self.assertEqual(len(cfg["replay_correlation"]["matched_primary_fields"]),15)
        anchors=cfg["replay_correlation"]["optional_ccdl_anchors"]
        self.assertEqual(len(anchors),4)
        for ch in range(2):
            self.assertEqual(anchors[2*ch],{"umc_address_index":ch,"field":"tCCDL",
                "gen_value":12,"replay_derived_value":18,"matches":False})
            self.assertEqual(anchors[2*ch+1],{"umc_address_index":ch,"field":"tCCDL_WR2",
                "gen_value":24,"replay_derived_value":24,"matches":True})
        # A wholly synthetic 6000 MT/s variant, NOT hardware evidence.
        # Keep layout, lengths, markers, SPD-derived alternative slots and
        # independently modify the highest GEN state and both replay blocks.
        tuned=bytearray(gen)
        for g in range(2):
            o=48+0x630+g*0x7a0
            for off,v in ((0,3000),(4,1500),(0x0c,36),(0x10,40),(0x14,40),
                          (0x18,40),(0x1c,76),(0x24,34),(0x6c,11677),
                          (0x84,885),(0x8c,885),(0x98,885),
                          (0x90,480),(0x9c,480),(0xa8,390),(0xb0,390),
                          (0x88,480),(0x94,400),(0xac,300)):
                struct.pack_into("<I",tuned,o+off,v)
        tuned_rep=bytearray(rep)
        values={0x50200:0x80050bb8,0x50204:0x28284c24,0x50208:0x00280074,
                0x50214:0x00180622,0x50230:0x00302d9d,0x50260:0x019001e0,0x502c0:0x0048012c}
        for o in range(48+32,48+0x21e8,8):
            addr=struct.unpack_from("<I",tuned_rep,o)[0]&~0x100000
            if addr in values:
                struct.pack_into("<I",tuned_rep,o+4,values[addr])
        j=run(config=tuned,rr=tuned_rep)
        vc=j["ddr5_config"]
        self.assertIsNotNone(vc)
        self.assertEqual(vc["layout"],"ddr5-v0x18-same-layout-candidate")
        self.assertEqual(j["replay"]["layout"],vc["layout"])
        self.assertEqual(vc["groups"][0]["states"][0]["nominal_rate_mts"],6000)
        self.assertEqual(vc["groups"][0]["states"][0]["timings_clocks"]["tCL"],36)
        self.assertEqual(vc["groups"][0]["states"][0]["refresh_candidates_clocks"],{"tRFC":885,"tRFC2":480,"tRFCsb":390})
        self.assertEqual(vc["replay_correlation"]["matching_refresh_candidates"],{"tRFC":480,"tRFC2":400,"tRFCsb":300})
        self.assertIsNone(run(config=tuned)["ddr5_config"]) # old replay incompatible
        # One UMC contradicts the saved state: reject whole variant.
        wrong=bytearray(tuned_rep)
        for o in range(48+32,48+0x21e8,8):
            if struct.unpack_from("<I",wrong,o)[0]==0x150204:
                struct.pack_into("<I",wrong,o+4,0x28284c25)
        j=run(config=tuned,rr=wrong)
        self.assertIsNone(j["ddr5_config"])
        self.assertIsNone(j["replay"])
        wrong=bytearray(tuned_rep)
        for o in range(48+32,48+0x21e8,8):
            if struct.unpack_from("<I",wrong,o)[0]==0x150260:
                struct.pack_into("<I",wrong,o+4,0x019001e1)
        self.assertIsNone(run(config=tuned,rr=wrong)["ddr5_config"])
        # Synthetic two-state layout: manual RFC1 in +84, JEDEC-like
        # copies in +8C/+98, absent third state and shorter replay.
        # Arbitrary test values, not a user's actual AFU data.
        two=bytearray(tuned)
        two_rep=bytearray(tuned_rep)
        for g in range(2):
            base=48+0x630+g*0x7a0
            for off,v in ((0x84,510),(0x88,0),(0x94,0),(0xac,0)):
                struct.pack_into("<I",two,base+off,v)
            two[base+2*0x164:base+3*0x164]=bytes(0x164)
        struct.pack_into("<I",two_rep,48+16,811)
        # End at +1978: leave the former donor terminator and opaque
        # suffix outside the shorter bounded stream.
        for o in range(48+32,48+0x1978,8):
            addr=struct.unpack_from("<I",two_rep,o)[0]&~0x100000
            if addr==0x50260:
                struct.pack_into("<I",two_rep,o+4,(480<<16)|510)
            if addr==0x502c0:
                struct.pack_into("<I",two_rep,o+4,0x00480186)
        j=run(config=two,rr=two_rep)
        vc=j["ddr5_config"]
        self.assertIsNotNone(vc)
        self.assertEqual(vc["unused_trailing_state_slots_per_group"],1)
        self.assertEqual([len(g["states"]) for g in vc["groups"]],[2,2])
        self.assertEqual(vc["replay_correlation"]["matching_refresh_candidates"],{"tRFC":510,"tRFC2":480,"tRFCsb":390})
        self.assertEqual(vc["groups"][0]["states"][0]["refresh_slot_words"]["0x8C"],885)
        self.assertEqual(j["replay"]["record_count"],811)
        self.assertEqual(j["replay"]["end_exclusive"],0x1978)
        self.assertEqual(j["replay"]["companions"],[])
        for c in j["replay"]["channels"]:
            fields=c["candidate_extended_fields"]
            self.assertEqual([fields[k] for k in ("NitroRxData","NitroTxData","NitroCtrlLine")],[2,2,0])
        malformed=bytearray(two)
        struct.pack_into("<I",malformed,48+0x630+2*0x164+0x0c,1)
        self.assertIsNone(run(config=malformed,rr=two_rep)["ddr5_config"])
        mismatch=bytearray(two_rep)
        for o in range(48+32,48+0x1978,8):
            if struct.unpack_from("<I",mismatch,o)[0]==0x150260:
                struct.pack_into("<I",mismatch,o+4,(480<<16)|511)
        self.assertIsNone(run(config=two,rr=mismatch)["ddr5_config"])
        self.assertIsNone(run(config=two,rr=tuned_rep)["ddr5_config"])
        bad=bytearray(gen)
        struct.pack_into("<I",bad,48+0x630+0x84,709)
        self.assertIsNone(run(config=bad)["ddr5_config"])
        bad=bytearray(main)
        bad[48+14]^=1
        self.assertIsNone(run(mm=bad)["ddr5_config"])
        bad=bytearray(rep)
        struct.pack_into("<I",bad,48+16,1082)
        j=run(rr=bad)
        self.assertIsNotNone(j["ddr5_config"])
        self.assertIsNone(j["replay"])
        self.assertIsNone(run(version=0x19)["ddr5_config"])
        # Aggregate training-structure tests: synthetic payloads only.
        phases=[bytearray(x) for x in companions]
        # Match an invented nonzero sequence in both candidate streams.
        motif=struct.pack("<16H",*range(100,116))
        phases[0][48+32+0x2fc+64:48+32+0x2fc+96]=motif
        phases[1][48+32+64:48+32+96]=motif
        pmu=entry(1,5,16432)
        stride=0x1fa2
        struct.pack_into("<H",pmu,48+2,4800)
        struct.pack_into("<H",pmu,48+stride+2,4800)
        x=run(comps=phases,extra=pmu)["ddr5_training_structure"]
        self.assertIsNotNone(x)
        self.assertEqual([p["record_count_candidate"] for p in x["phy_phases"]],[1280,1092])
        self.assertEqual(x["phy_fixed_alignment"]["shifted"]["compared_u16"],4368)
        self.assertEqual(x["phy_fixed_alignment"]["shifted"]["equal_nonzero_u16"],16)
        self.assertEqual(x["pmu_smb"]["equal_bytes"],8098)
        self.assertTrue(x["pmu_smb"]["both_rate_words_match_highest_gen"])
        # The opaque trailing bytes cannot change aggregate output.
        other=[bytearray(p) for p in phases]
        other[1][48+0x2240:]=bytes([0xAB])*1504
        pmu2=bytearray(pmu)
        pmu2[48+2*stride:]=bytes([0xCD])*188
        self.assertEqual(run(comps=other,extra=pmu2)["ddr5_training_structure"],x)
        # No training projection without independently corroborated replay.
        self.assertIsNone(run(rr=bad,extra=pmu)["ddr5_training_structure"])
        # Reject duplicate PMU records without suppressing independent phases.
        self.assertIsNone(run(comps=phases,extra=pmu+pmu)["ddr5_training_structure"]["pmu_smb"])
        pmu3=bytearray(pmu)
        struct.pack_into("<H",pmu3,48+stride+2,3200)
        self.assertFalse(run(comps=phases,extra=pmu3)["ddr5_training_structure"]["pmu_smb"]["both_rate_words_match_highest_gen"])
        # Synthetic four-slot DDR5 APCB boot-info layout. Use fake
        # serials only; this report must not reveal even matching identities.
        ids=bytearray(spd)
        boot=entry(1,16,120)
        struct.pack_into("<I",boot,48,0x60)
        for ch,serial in enumerate((b"ABCD",b"WXYZ")):
            d=48+4+ch*1036+12
            ids[d+517:d+521]=serial
            o=48+4+ch*24
            boot[o:o+2]=ids[d+512:d+514]
            boot[o+2:o+4]=ids[d+552:d+554]
            boot[o+4:o+8]=serial
            boot[o+8]=1
        struct.pack_into("<I",boot,48+56,0x00241231)
        boot[48+60:48+68]=b"PRIVATE!"
        info=run(ss=ids,extra=boot)["ddr5_boot_info"]
        self.assertIsNotNone(info)
        self.assertEqual(info["present_slot_indices"],[0,2])
        self.assertTrue(info["both_spd_identity_matches"])
        self.assertEqual(info["last_training_date_candidate"],"2024-12-31")
        self.assertEqual(info["last_training_date_encoding"],"BCD")
        ambiguous=bytearray(boot)
        struct.pack_into("<I",ambiguous,48+56,0x00120512)
        candidate=run(ss=ids,extra=ambiguous)["ddr5_boot_info"]
        self.assertIsNone(candidate["last_training_date_candidate"])
        self.assertEqual(candidate["last_training_date_encoding"],"ambiguous")
        self.assertFalse(info["dimm_configuration_updated_candidate"])
        # Changes to private words, including mutually matching serials,
        # do not alter the exported metadata.
        masked=bytearray(boot)
        masked[48+60:48+68]=b"SECRET!!"
        self.assertEqual(run(ss=ids,extra=masked)["ddr5_boot_info"],info)
        ids2=bytearray(ids)
        ids2[48+4+12+517:48+4+12+521]=b"TEST"
        self.assertIsNone(run(ss=ids2,extra=masked)["ddr5_boot_info"])
        masked[48+8:48+12]=b"TEST"
        self.assertEqual(run(ss=ids2,extra=masked)["ddr5_boot_info"],info)
        self.assertIsNone(run(ss=ids,extra=boot+boot)["ddr5_boot_info"])
        self.assertIsNone(run(ss=ids,rr=bad,extra=boot)["ddr5_boot_info"])
        # Health summary remains available even when GEN/replay is rejected.
        log=entry(7,6,1076)
        struct.pack_into("<H",log,48,3)
        for i,cl in enumerate((2,6,12345678)):
            struct.pack_into("<4I",log,48+4+16*i,cl,0xDEADBEEF,0xABCDEF01,0x12345678)
        failure=entry(1,22,1012)
        mbist=entry(1,18,140)
        failure[-1]=1
        health=run(rr=bad,extra=log+failure+mbist)["health_summary"]
        self.assertEqual(health["event_log"]["declared_count"],3)
        self.assertEqual(health["event_log"]["severity_counts"],{"Bounds check":1,"Warning":1,"Unknown class":1})
        self.assertTrue(health["event_log"]["unused_slots_all_zero"])
        self.assertFalse(health["pmu_training_failure"]["all_zero_payload"])
        self.assertTrue(health["mbist_result"]["all_zero_payload"])
        # Event parameters cannot alter the summary.
        changed=bytearray(log)
        changed[48+4+4:48+4+16]=b"PRIVATE DATA"
        self.assertEqual(run(rr=bad,extra=changed+failure+mbist)["health_summary"],health)
        changed[-1]=1
        self.assertFalse(run(extra=changed)["health_summary"]["event_log"]["unused_slots_all_zero"])
        self.assertIsNone(run(extra=log+log)["health_summary"])
        self.assertIsNone(run(extra=log,version=0x19)["health_summary"])

    def test_apob_directory_metadata(self):
        import struct
        def directory(cookie, entries):
            tail=struct.pack("<II",len(entries),0)+b"".join(struct.pack("<IIQQ",*e) for e in entries)
            a=z=65535
            for (w,) in struct.iter_unpack("<H",tail):
                a=(a+w)%65535
                z=(z+a)%65535
            checksum=((z or 65535)<<16)|(a or 65535)
            return cookie+struct.pack("<I",checksum)+tail
        # Full 16 MiB image, redundant physical / relative references,
        # unsupported mode, out-of-image range and corrupted directory.
        img=bytearray(16*1024*1024)
        entries=[(0x61,0,0,0xA200000),(0x63,0x10000,0xFF2BA000,2**64-1),
                 (0x20000063,0x10000,(1<<62)|0x2BA000,2**64-1),
                 (0x63,4096,(3<<62)|0x1000,0),
                 (0x63,4096,(1<<62)|0xFFFFFF,0)]
        for off,cookie in [(0x1000,b"$BHD"),(0x2000,b"$BL2")]:
            d=directory(cookie,entries)
            img[off:off+len(d)]=d
        d=bytearray(directory(b"$BL2",[(0x61,0,0,0xBAD)]))
        d[4]^=1
        img[0x3000:0x3000+len(d)]=d
        f=self.root/"directory.bin"
        f.write_bytes(img)
        j=json.loads(self.invoke("--apob-image",str(f),"--json",code=1,live=False).stdout)["apob"]
        self.assertFalse(j["available"])
        q=j["bios_directory"]
        self.assertEqual(q["advertised_ram_addresses"],["0xA200000"])
        self.assertEqual(len(q["directories"]),2)
        self.assertEqual(q["nv_regions"],[{"image_offset":"0x2BA000","size":65536,"directory_offsets":["0x1000","0x2000"]}])
        self.assertEqual(len(q["unresolved_nv_entries"]),4)
        self.assertIsNone(q["matched_nv_region"])
        # No directory metadata may make an empty/invalid image acceptable.
        self.assertEqual(f.read_bytes(),img)

    def test_ddr5_spd_profiles(self):
        import struct
        import binascii
        b=bytearray(1024)
        b[:8]=bytes((0x30,0x10,0x12,0x03,0x04,0x00,0x20,0x42))
        b[234]=0
        b[235]=0x22
        def w(off,value):
            struct.pack_into("<H",b,off,value)
        w(20,357)
        w(22,625)
        b[25]=0x10
        for off,value in zip(range(30,48,2),(14000,14000,14000,32000,46000,30000,295,160,130)):
            w(off,value)
        b[512:514]=bytes((0,0x2c))
        b[517:521]=b"SECR"
        b[521:551]=b"SYNTHETIC-DDR5".ljust(30)
        b[640:644]=bytes((0x0c,0x4a,0x30,1))
        b[704:707]=bytes((0x44,0x28,0x28))
        w(709,333)
        b[712]=0x20
        for off,value in zip(range(717,735,2),(10000,12000,12000,32000,44000,30000,295,160,130)):
            w(off,value)
        b[832:838]=b"EXPO"+bytes((0x10,1))
        b[842:845]=bytes((0x27,0x27,0x44))
        for off,value in zip(range(846,866,2),(333,10000,12000,12000,32000,44000,30000,295,160,130)):
            w(off,value)
        w(866,6000)
        w(958,binascii.crc_hqx(b[832:958],0))
        w(510,binascii.crc_hqx(b[:510],0))
        root=self.root/"bus/i2c/devices/0-0050"
        root.mkdir(parents=True)
        driver=self.root/"bus/i2c/drivers/spd5118"
        driver.mkdir(parents=True)
        (root/"driver").symlink_to(driver,target_is_directory=True)
        f=root/"eeprom"
        def read(data,code=0):
            f.write_bytes(data)
            out=self.invoke("--modules","--json",code=code).stdout
            self.assertNotIn("SECR",out)
            self.assertEqual(f.read_bytes(),data)
            return json.loads(out)["memory_inventory"]["spd"][0]
        d=read(b)["ddr5"]
        self.assertEqual(d["jedec_speed_mts"],5600)
        self.assertEqual(d["subchannels_per_dimm"],2)
        self.assertEqual(d["device_width_bits"],8)
        self.assertEqual(d["jedec_refresh_ns"]["tRFC"],295)
        self.assertTrue(d["expo_crc_valid"])
        self.assertEqual([p["kind"] for p in d["profiles"]],["XMP","EXPO"])
        x,e=d["profiles"]
        self.assertEqual(x["speed_mts"],6000)
        self.assertEqual(x["vdd_mv"],1400)
        self.assertEqual(e["speed_mts"],6000)
        self.assertEqual(e["vdd_mv"],1350)
        self.assertEqual(e["timings_ps"]["tRRDL"],6000)
        b[958]^=1
        d=read(b)["ddr5"]
        self.assertFalse(d["expo_crc_valid"])
        self.assertEqual([p["kind"] for p in d["profiles"]],["XMP"])
        d=read(b[:512])["ddr5"]
        self.assertEqual(d["profiles"],[])
        self.assertIsNone(d["expo_crc_valid"])
        b[20]^=1
        d=read(b,code=1)
        self.assertFalse(d["base_crc_valid"])
        self.assertIsNone(d["ddr5"])

    def test_drfxi_ram_spi_comparison(self):
        import binascii, struct
        # Only observed entry lengths and IDs, synthetic contents throughout.
        entries=[
            (7,23,56),(7,26,4144),(9,9,496),(8,8,216),(0x10001,15,88),
            (1,16,120),(0x10009,24,304),(0x10009,21,308),(0x10009,19,52),
            (1,2,732),(1,25,96),(0x10001,1,56),(7,3,6020),(1,27,64),
            (1,28,204),(7,4,15312),(1,5,16432),(1,18,140),(0x10003,1,100),
            (0x10003,2,88),(3,3,456),(7,6,1076),(1,17,4196),(1,22,1012),
            (1,30,10320),(1,31,10320)]
        table=bytearray(0xa8)
        offsets={}
        for g,t,n in entries:
            offsets[g,t]=len(table)
            table.extend(struct.pack("<IIII",g,t,0,n)+bytes(n-16))
        self.assertEqual(len(table),72576)
        struct.pack_into("<4I",table,0,0x424f5041,0x18,len(table),0xa8)
        base=offsets[1,17]+48
        table[base:base+2]=bytes((1,2))
        for ch in range(2):
            slot=base+4+1036*ch
            table[slot+1]=1
            table[slot+9]=ch
            d=slot+12
            table[d+2]=0x12
            struct.pack_into("<H",table,d+510,binascii.crc_hqx(table[d:d+510],0))
            table[d+517:d+521]=b"PRIV"
            table[d+521:d+551]=b"PRIVATE-MODULE".ljust(30)
        img=bytearray(32*1024*1024)
        img[0x125000:0x125000+len(table)]=table
        directory=struct.pack("<II",18,0)+struct.pack("<IIQQ",0x61,0,0,0xa200000)+struct.pack("<IIQQ",0x20000063,0x28000,(1<<62)|0x125000,2**64-1)+bytes(24*16)
        a=z=65535
        for (w,) in struct.iter_unpack("<H",directory):
            a=(a+w)%65535
            z=(z+a)%65535
        directory=b"$BL2"+struct.pack("<I",((z or 65535)<<16)|(a or 65535))+directory
        for o in (0x51d000,0x8e5000):
            img[o:o+len(directory)]=directory
        afu=self.root/"private-afu.bin"
        ram=self.root/"private-ram.bin"
        afu.write_bytes(img)
        def run(b,code=0):
            ram.write_bytes(b)
            p=self.invoke("--apob-compare-image",str(afu),"--apob-compare-file",str(ram),"--json",code=code,live=False)
            self.assertEqual(ram.read_bytes(),b)
            if code: return p
            self.assertNotIn("PRIV",p.stdout)
            self.assertNotIn("private-afu",p.stdout)
            self.assertNotIn("private-ram",p.stdout)
            return json.loads(p.stdout)
        j=run(table)
        self.assertTrue(j["table_equal"])
        self.assertTrue(j["entry_layout_equal"])
        self.assertTrue(j["same_spd_identities"])
        self.assertEqual(len(j["known_records"]),12)
        changed=bytearray(table)
        changed[offsets[1,5]+48+123]^=1
        changed[0x90]^=1 # header (authentication/unknown), equality only
        changed[ offsets[1,16]+48+56 ]^=1 # date word, equality only
        j=run(changed)
        self.assertFalse(j["table_equal"])
        self.assertFalse(j["header_equal"])
        self.assertTrue(j["entry_layout_equal"])
        rows={r["name"]:r for r in j["known_records"]}
        self.assertEqual(rows["PMU SMB"]["changed_payload_bytes"],1)
        self.assertFalse(rows["APCB boot info"]["training_date_word_equal"])
        self.assertIsNone(rows["APCB boot info"]["changed_payload_bytes"])
        self.assertIsNone(rows["SPD"]["changed_payload_bytes"])
        # Different unknown-entry type remains a legitimate layout difference.
        different=bytearray(table)
        struct.pack_into("<I",different,offsets[7,23]+4,99)
        self.assertFalse(run(different)["entry_layout_equal"])
        # Reject identity mismatch, duplicate entry, CRC failure and wrong length.
        bad=bytearray(table);bad[base+4+12+517]^=1
        self.assertIn("identities do not match",run(bad,code=1).stderr)
        bad=bytearray(table);bad[base+4+12+20]^=1
        self.assertIn("CRC mismatch",run(bad,code=1).stderr)
        bad=bytearray(table);struct.pack_into("<II",bad,offsets[7,23],1,17)
        self.assertIn("Ambiguous",run(bad,code=1).stderr)
        run(table[:-1],code=1)
        self.assertEqual(afu.read_bytes(),img)
        bad_img=bytearray(img);bad_img[0x8e5000+4]^=1
        afu.write_bytes(bad_img)
        self.assertIn("directory",run(table,code=1).stderr)
        self.assertEqual(afu.read_bytes(),bad_img)
        self.invoke("--apob-compare-file",str(ram),code=2,live=False)
        self.invoke("--apob-compare-image",str(afu),"--apob",code=2,live=False)

    def test_drfxi_mcr_policy(self):
        import struct
        self.identity("DRFXI", "1.15", "MINISFORUM", "", "")
        cbs = bytearray(0x6D5)
        struct.pack_into("<III", cbs, 0, 0xFC4053AA, 0x2D8, 0x4B9D5809)
        cbs[0x20] = 0x43
        cbs[0x263] = 255
        cbs[0x300:0x30e] = b"PRIVATE-MARKER"
        def read(code=0):
            self.write("AmdSetupRPL", AMD, cbs)
            p = self.invoke("--mcr", "--json", code=code)
            self.assertNotIn("PRIVATE-MARKER", p.stdout)
            self.assertNotIn("4B9D5809", p.stdout)
            return json.loads(p.stdout)["mcr_policy"]
        j = read()
        self.assertTrue(j["available"])
        self.assertEqual(j["saved_cbs_mcr"], "Auto")
        self.assertEqual(j["bios_release"], "1.15")
        self.assertEqual(j["layout_evidence"], "IFR-derived")
        self.assertTrue(j["stored_combo_cbs_suppresses_memory_features"])
        self.assertIn("Saved CBS MCR: Auto", self.invoke("--mcr").stdout)
        # No AOD variable required. A changed settings fingerprint is not a
        # schema mismatch and is not reported as an observed APCB match.
        struct.pack_into("<I", cbs, 8, 0x12345678)
        cbs[0x20] = 255
        cbs[0x263] = 1
        j = read()
        self.assertEqual(j["saved_cbs_mcr"], "Enabled")
        self.assertFalse(j["stored_combo_cbs_suppresses_memory_features"])
        cbs[0x263] = 0
        self.identity("DRFXI", "1.17", "MINISFORUM", "", "")
        self.assertEqual(read()["saved_cbs_mcr"], "Disabled")
        cbs[0x263] = 2
        self.assertIsNone(read(code=1)["saved_cbs_mcr"])
        cbs[0x263] = 255
        cbs[0] ^= 1
        self.assertFalse(read(code=1)["available"])
        cbs[0] ^= 1
        cbs[4] ^= 1
        self.assertFalse(read(code=1)["available"])
        cbs[4] ^= 1
        cbs.append(0)
        self.assertFalse(read(code=1)["available"])
        cbs.pop()
        self.identity("NOTDRFXI", "1.15", "OTHER", "", "")
        self.assertFalse(read(code=1)["available"])
        self.identity("DRFXI", "1.16", "MINISFORUM", "", "")
        self.assertFalse(read(code=1)["available"])
        self.identity("DRFXI", "1.15", "MINISFORUM", "", "")
        path = self.efi / ("AmdSetupRPL-" + AMD)
        path.write_bytes(b"\x07\x00")
        self.assertFalse(json.loads(self.invoke("--mcr", "--json", code=1).stdout)["mcr_policy"]["available"])
        path.unlink()
        self.assertFalse(json.loads(self.invoke("--mcr", "--json", code=1).stdout)["mcr_policy"]["available"])
        self.invoke("--mcr", "--debug", code=2)
        self.invoke("--mcr", "--apob", code=2)
        self.invoke("--mcr", "--compare", code=2)

    def test_umc_report_arguments(self):
        self.invoke("--umc-report", "missing.json", code=2, live=False)
        self.invoke("--apob-image", "missing.bin", "--umc-report", "missing.json",
                    "--timings", code=2, live=False)
        self.invoke("--apob-image", "missing.bin", "--umc-report", "missing.json",
                    "--apob-file", "missing-apob.bin", code=2, live=False)
        p = self.invoke("--apob-image", "private-image.bin", "--umc-report",
                        "private-report.json", code=1, live=False)
        self.assertNotIn("private-image", p.stdout)
        self.assertNotIn("private-report", p.stdout)
        self.assertFalse(json.loads(p.stdout)["apob_umc_correlation"]["available"])

    def test_watch_arguments(self):
        for args in (
            ("--watch",), ("--watch", "--origins"), ("--watch", "--mcr"),
            ("--watch", "--modules"), ("--watch", "--timings", "--apob"),
            ("--watch", "--compare", "--modules"), ("--watch", "--timings", "--compare"),
            ("--watch", "--pm-table", "--extended"), ("--watch", "--pm-table", "--sysfs-root", str(self.root)),
            ("--timings", "--interval", "2"), ("--pm-table", "--samples", "1"),
            ("--watch", "--pm-table", "--interval", "0"), ("--watch", "--pm-table", "--interval", "1.5"),
            ("--watch", "--pm-table", "--interval", "3601"), ("--watch", "--pm-table", "--samples", "86401"),
            ("--watch", "--pm-table", "--samples"), ("--watch", "--apob-file", "missing.bin"),
        ):
            with self.subTest(args=args):
                self.invoke(*args, code=2, live=False)

    def test_arguments_and_non_uefi(self):
        self.invoke("--debug", "--json", code=2)
        self.invoke("--board", "X670E Steel Legend", "--bios", "1.21", code=2)
        self.invoke("--timings", code=2)
        for p in self.efi.iterdir():
            p.unlink()
        self.efi.rmdir()
        self.assertIn("efivarfs unavailable", self.invoke(code=1).stderr)

if __name__ == "__main__":
    unittest.main()
