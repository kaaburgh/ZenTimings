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
