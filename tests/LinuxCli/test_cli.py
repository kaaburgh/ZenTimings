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
        struct.pack_into("<HH",cfg,52,1200,2400)
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
            self.assertEqual(details["device_width_bits"],8)
            self.assertEqual(details["jedec_speed_mts"],2667)
            self.assertEqual(details["xmp_profiles"],[])
            self.assertEqual(details["xmp_status"],"not present")
            self.assertNotIn("serial",json.dumps(details).lower())
        self.assertFalse(run(prefix+table[:-1],code=1)["available"])
        self.assertFalse(run(prefix+table+table,code=1)["available"])
        self.assertFalse(run(prefix,code=1)["available"])
        self.invoke("--apob-image",str(f),"--apob",code=2,live=False)

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
