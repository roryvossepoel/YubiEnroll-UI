"""Exercise the shipped read-only probe with isolated fake device APIs; no hardware."""
import contextlib
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

SCRIPT = (Path(__file__).resolve().parents[1] / "src/YubiEnroll.Core/Resources/InspectFido.py").read_text()


class ProbeTests(unittest.TestCase):
    def probe(self, *, serials=(12345678,), bio=False, minimum=4, maximum=63,
              blocked=0, transports=(), client_pin=False, extra_options=None):
        calls = []
        options = {"clientPin": client_pin, "rk": True, **(extra_options or {})}
        ctap_info = types.SimpleNamespace(options=options, min_pin_length=minimum,
                                         max_pin_length=maximum, transports_for_reset=transports)
        class Connection:
            def __init__(self, serial): self.serial = serial
            def __enter__(self): calls.append(("open", self.serial)); return self
            def __exit__(self, *args): calls.append(("close", self.serial))
        class Device:
            transport = "usb"
            def __init__(self, serial): self.serial = serial
            def open_connection(self, kind): return Connection(self.serial)
        def device_info(serial):
            return types.SimpleNamespace(serial=serial, version="5.7.1", _is_bio=bio,
                config=types.SimpleNamespace(enabled_capabilities={"usb": 3 if bio else 1}),
                reset_blocked=blocked, fips_capable=0)
        def ctap(connection):
            calls.append(("get_info", connection.serial))
            return types.SimpleNamespace(info=ctap_info)
        modules = {}
        for name, members in {
            "ykman.device": {"list_all_devices": lambda: [(Device(s), device_info(s)) for s in serials]},
            "yubikit.core": {"TRANSPORT": types.SimpleNamespace(USB="usb")},
            "yubikit.core.fido": {"FidoConnection": Connection},
            "yubikit.management": {"CAPABILITY": types.SimpleNamespace(FIDO2=1, PIV=2)},
            "fido2.ctap2": {"Ctap2": ctap},
        }.items():
            module = types.ModuleType(name)
            module.__dict__.update(members)
            modules[name] = module
        output = io.StringIO()
        with patch.dict(sys.modules, modules), patch.object(sys, "argv", ["-", "12345678"]), contextlib.redirect_stdout(output):
            exec(compile(SCRIPT, "InspectFido.py", "exec"), {})
        return json.loads(output.getvalue()), calls

    def test_exact_serial_with_admin_connected(self):
        data, calls = self.probe(serials=(87654321, 12345678))
        self.assertEqual(data["serial"], "12345678")
        self.assertEqual(calls, [("open", 12345678), ("get_info", 12345678), ("close", 12345678)])
        self.assertEqual(data["options"], {"clientPin": False, "rk": True})

    def test_missing_and_duplicate_serial_fail_closed(self):
        for serials in ((), (87654321,), (12345678, 12345678)):
            with self.subTest(serials=serials), self.assertRaises(SystemExit) as result:
                self.probe(serials=serials)
            self.assertEqual(result.exception.code, 4)

    def test_pin_policy_allowlist_and_no_pin_data(self):
        data, _ = self.probe(extra_options={"setMinPINLength": True, "authnrCfg": True, "ep": False, "unexpected": "SECRET"})
        self.assertEqual(data["options"]["ep"], False)
        self.assertNotIn("unexpected", data["options"])
        self.assertNotIn("SECRET", json.dumps(data))

    def test_bio_shared_pin_and_reset_constraints(self):
        data, _ = self.probe(bio=True, minimum=6, blocked=1, transports=("nfc",))
        self.assertEqual((data["minimumPinLength"], data["maximumPinLength"]), (6, 8))
        self.assertTrue(data["resetBlocked"])
        self.assertFalse(data["resetOverUsb"])

    def test_legacy_default_transport(self):
        data, _ = self.probe()
        self.assertTrue(data["resetOverUsb"])
        self.assertFalse(data["resetBlocked"])
        self.assertNotIn("setMinPINLength", data["options"])


if __name__ == "__main__":
    unittest.main()
