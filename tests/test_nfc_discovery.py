"""NFC inventory with fake PC/SC readers; never touches hardware."""
import contextlib
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

SCRIPT = (Path(__file__).resolve().parents[1] / "src/YubiEnroll.Core/Resources/DiscoverNfc.py").read_text()

class NoCard(Exception): pass
class ReaderError(Exception): pass
class ContextError(Exception): pass

class DiscoveryTests(unittest.TestCase):
    def run_probe(self, cards=(), reader_error=False):
        opened = []
        class Device:
            pid = None
            def __init__(self, name, serial, transport="nfc", fido=1):
                self.fingerprint, self.serial, self.transport, self.fido = name, serial, transport, fido
            def open_connection(self, kind):
                if self.serial == "empty": raise NoCard()
                if self.serial == "busy": raise RuntimeError()
                opened.append(self.fingerprint)
                return self
            def __enter__(self): return self
            def __exit__(self, *args): pass
        def list_devices(name):
            if reader_error: raise (ContextError() if reader_error == "context" else ReaderError())
            return [Device(*c) for c in cards]
        def read_info(conn, pid):
            return types.SimpleNamespace(serial=conn.serial, version="5.7.1", config=types.SimpleNamespace(enabled_capabilities={"nfc": conn.fido}))
        modules = {}
        for name, members in {
            "ykman.pcsc": {"list_devices": list_devices},
            "yubikit.core": {"TRANSPORT": types.SimpleNamespace(NFC="nfc", USB="usb")},
            "yubikit.core.smartcard": {"SmartCardConnection": Device},
            "yubikit.management": {"CAPABILITY": types.SimpleNamespace(FIDO2=1)},
            "yubikit.support": {"read_info": read_info, "get_name": lambda info, key_type: "YubiKey 5C NFC"},
            "smartcard.Exceptions": {"NoCardException": NoCard},
            "smartcard.pcsc.PCSCExceptions": {"ListReadersException": ReaderError, "EstablishContextException": ContextError},
        }.items():
            module = types.ModuleType(name); module.__dict__.update(members); modules[name] = module
        output = io.StringIO()
        with patch.dict(sys.modules, modules), contextlib.redirect_stdout(output):
            exec(compile(SCRIPT, "DiscoverNfc.py", "exec"), {})
        return json.loads(output.getvalue()), opened

    def test_empty_reader_is_not_a_key(self):
        data, _ = self.run_probe([("OMNIKEY 5022 0", "empty")])
        self.assertEqual((data["readers"], data["unavailable"], data["keys"]), (1, 0, []))
        self.assertEqual(data["readerStates"], [{"name": "OMNIKEY 5022 0", "nfc": True, "state": "empty"}])

    def test_usb_ignored_and_exact_nfc_identity_returned(self):
        data, opened = self.run_probe([("Yubico YubiKey CCID 0", 111, "usb"), ("OMNIKEY 5022 0", 222)])
        self.assertEqual(opened, ["OMNIKEY 5022 0"])
        self.assertEqual(data["keys"], [{"serial": "222", "model": "YubiKey 5C NFC (5.7.1)", "reader": "OMNIKEY 5022 0"}])

    def test_unrelated_failure_does_not_taint_working_reader(self):
        data, _ = self.run_probe([("Internal contact reader", "busy"), ("OMNIKEY 5022 0", 222)])
        self.assertEqual(data["readerStates"], [
            {"name": "Internal contact reader", "nfc": False, "state": "unavailable"},
            {"name": "OMNIKEY 5022 0", "nfc": True, "state": "ready"}])

    def test_unknown_empty_reader_is_not_assumed_nfc(self):
        data, _ = self.run_probe([("Internal reader", "empty")])
        self.assertEqual(data["readerStates"][0], {"name": "Internal reader", "nfc": False, "state": "empty"})
        data, _ = self.run_probe()
        self.assertEqual(data["readerStates"], [])

    def test_busy_missing_serial_disabled_fido_excluded(self):
        data, _ = self.run_probe([("Reader A", "busy"), ("Reader B", None), ("Reader C", 3, "nfc", 0)])
        self.assertEqual((data["keys"], data["unavailable"]), ([], 3))

    def test_reader_substring_ambiguity_is_rejected(self):
        data, opened = self.run_probe([("OMNIKEY 0", 1), ("OMNIKEY 0 second", 2)])
        self.assertEqual(opened, ["OMNIKEY 0 second"])
        self.assertEqual(data["unavailable"], 1)
        self.assertEqual(data["keys"][0]["serial"], "2")

    def test_duplicate_names_and_service_failure_are_not_empty_success(self):
        data, opened = self.run_probe([("OMNIKEY 0", 1), ("OMNIKEY 0", 2)])
        self.assertEqual((opened, data["unavailable"]), ([], 2))
        for error in (True, "context"):
            data, _ = self.run_probe(reader_error=error)
            self.assertEqual((data["keys"], data["unavailable"]), ([], 1))

if __name__ == "__main__": unittest.main()
