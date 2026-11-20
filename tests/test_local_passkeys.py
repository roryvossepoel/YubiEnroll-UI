"""Run the shipped helper against isolated CTAP devices, never real hardware."""
import contextlib
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

SRC = (Path(__file__).resolve().parents[1] / "src/YubiEnroll.Core/Resources/LocalPasskeys.py").read_text()
ROW = dict(id="abcd", rp="login.example", uid="01", name="alex", display="Alex")
OTHER = dict(ROW, id="abcd00")


class LocalPasskeysTests(unittest.TestCase):
    def run_op(self, action="list", selected=None, nfc=False, scenario="", existing=None):
        trace, rows = [], list(existing if existing is not None else [ROW.copy(), OTHER.copy()])
        reader = "Mock OMNIKEY 0" if nfc else None
        class Err(Exception):
            ERR = types.SimpleNamespace(PIN_INVALID=1, PIN_AUTH_BLOCKED=2, PIN_BLOCKED=3)
            def __init__(self): self.code = 1
        class Handle:
            transport = "nfc" if nfc else "usb"
            def __enter__(self): return self
            def __exit__(self, *exc): pass
        class Device:
            pid = None if nfc else 123
            transport = Handle.transport
            fingerprint = reader
            def open_connection(self, cls): trace.append("open"); return Handle()
        def info(handle, pid):
            trace.append("identity")
            return types.SimpleNamespace(serial=8877 if scenario == "swapped" else 1234,
                config=types.SimpleNamespace(enabled_capabilities={Handle.transport: 1}))
        class Client:
            PERMISSION = types.SimpleNamespace(CREDENTIAL_MGMT=4)
            protocol = object()
            def __init__(self, ctap): pass
            def get_pin_token(self, pin, perm):
                trace.append("pin")
                if pin != "fake" or perm != 4: raise AssertionError()
                if scenario == "wrong-pin": raise Err()
                return b"mock"
        class Manager:
            RESULT = types.SimpleNamespace(EXISTING_CRED_COUNT=0, RP=1, RP_ID_HASH=2, CREDENTIAL_ID=3, USER=4)
            @staticmethod
            def is_supported(i): return scenario != "unsupported"
            def __init__(self, ctap, proto, token): pass
            def get_metadata(self):
                trace.append("snapshot")
                return {0: len(rows) + (scenario == "incomplete")}
            def enumerate_rps(self): return [{1: {"id": "login.example"}, 2: b"mock-hash"}]
            def enumerate_creds(self, rp_hash):
                return [{3: {"id": bytes.fromhex(row["id"]), "type": "public-key"},
                         4: {"id": bytes.fromhex(row["uid"]), "name": row["name"], "displayName": row["display"]}} for row in rows]
            def delete_cred(self, descriptor):
                trace.append(("remove",descriptor["id"].hex()))
                if scenario != "still-there":
                    rows[:] = [r for r in rows if r["id"] != descriptor["id"].hex()]
                if scenario == "missing-other": rows.clear()
                if scenario == "interrupted": raise OSError()
        def ctap(handle):
            return types.SimpleNamespace(info=types.SimpleNamespace(options={"clientPin": scenario != "no-pin"}, force_pin_change=False))
        modules = {}
        for name, members in {
            "fido2.ctap":dict(CtapError=Err),
            "fido2.ctap2":dict(CredentialManagement=Manager,ClientPin=Client,Ctap2=ctap),
            "ykman.device":dict(list_all_devices=lambda:[(Device(),types.SimpleNamespace(serial=1234))]),
            "ykman.pcsc":dict(list_devices=lambda r:[Device()] * (2 if scenario == "ambiguous-reader" else 1)),
            "yubikit.core":dict(TRANSPORT=types.SimpleNamespace(USB="usb",NFC="nfc")),
            "yubikit.core.fido":dict(FidoConnection=Handle,SmartCardCtapDevice=lambda h:h),
            "yubikit.core.smartcard":dict(SmartCardConnection=Handle),
            "yubikit.management":dict(CAPABILITY=types.SimpleNamespace(FIDO2=1)),
            "yubikit.support":dict(read_info=info),
        }.items():
            m=types.ModuleType(name); m.__dict__.update(members); modules[name]=m
        req = dict(action=action,serial="1234",reader=reader,selected=selected,pin="fake")
        out = io.StringIO(); code=0
        with patch.dict(sys.modules,modules),patch.object(sys,"stdin",io.StringIO(json.dumps(req))),contextlib.redirect_stdout(out):
            try: exec(compile(SRC,"LocalPasskeys.py","exec"), {})
            except SystemExit as exc: code=exc.code
        return code, trace, json.loads(out.getvalue()) if out.getvalue() else None

    def test_reads_only_and_handles_empty(self):
        for nfc in (True,False):
            code, trace, data=self.run_op(nfc=nfc)
            self.assertEqual((code, data["rows"]),(0,[ROW,OTHER]))
            self.assertEqual(trace,["open","identity","pin","snapshot"])
        self.assertEqual(self.run_op(existing=[])[2]["rows"],[])

    def test_verify_same_handle_before_pin(self):
        for nfc,scenario,code in [(True,"swapped",41),(False,"swapped",41),(True,"ambiguous-reader",41),(False,"no-pin",43),(True,"unsupported",42)]:
            result,trace,data=self.run_op("remove",ROW,nfc,scenario)
            self.assertEqual(result,code);self.assertNotIn("pin",trace);self.assertIsNone(data)

    def test_wrong_pin_once(self):
        result,trace,data=self.run_op("remove",ROW,scenario="wrong-pin")
        self.assertEqual((result,trace.count("pin"),data),(44,1,None))
        self.assertNotIn(("remove","abcd"),trace)

    def test_must_match_reviewed_row(self):
        for changed in [dict(ROW,id="ab"),dict(ROW,name="different"),dict(ROW,rp="other"),dict(ROW,uid="02")]:
            result,trace,_=self.run_op("remove",changed)
            self.assertEqual(result,46);self.assertFalse(any(isinstance(x,tuple) for x in trace))
        for scenario,rows in [("incomplete",None),("",[ROW,ROW])]:
            result,trace,_=self.run_op("remove",ROW,scenario=scenario,existing=rows)
            self.assertNotEqual(result,0);self.assertFalse(any(isinstance(x,tuple) for x in trace))

    def test_one_exact_delete_followed_by_verification(self):
        for nfc in (True,False):
            result,trace,data=self.run_op("remove",ROW,nfc)
            self.assertEqual((result,data["rows"]),(0,[OTHER]))
            self.assertEqual(trace,["open","identity","pin","snapshot",("remove","abcd"),"snapshot"])
        for scenario in ("missing-other","still-there","interrupted"):
            result,trace,data=self.run_op("remove",ROW,scenario=scenario)
            self.assertEqual((result,trace.count(("remove","abcd")),data),(47,1,None))
