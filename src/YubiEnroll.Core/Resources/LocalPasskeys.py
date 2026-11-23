# Bundled ykman helper. stdin is request data only; stdout contains a verified list.
# No Graph, reset, PIN changes, credential creation, or deletion by prefix.
import json
import sys
from contextlib import ExitStack
from fido2.ctap import CtapError
from fido2.ctap2 import Ctap2, ClientPin, CredentialManagement
from ykman.device import list_all_devices
from yubikit.core import TRANSPORT
from yubikit.core.fido import FidoConnection, SmartCardCtapDevice
from yubikit.core.smartcard import SmartCardConnection
from yubikit.management import CAPABILITY
from yubikit.support import read_info


def snapshot(manager):
    result = CredentialManagement.RESULT
    count = manager.get_metadata()[result.EXISTING_CRED_COUNT]
    rows = {}
    if count:
        for rp in manager.enumerate_rps():
            service = rp[result.RP]["id"]
            for cred in manager.enumerate_creds(rp[result.RP_ID_HASH]):
                descriptor = cred[result.CREDENTIAL_ID]
                user = cred[result.USER]
                cid = descriptor["id"].hex()
                if not service or not cid or cid in rows or descriptor["type"] != "public-key":
                    raise ValueError()
                rows[cid] = (dict(id=cid, rp=service, uid=user["id"].hex(),
                                  name=user.get("name") or "", display=user.get("displayName") or ""), descriptor)
    if len(rows) != count:
        raise ValueError()
    return rows


def operate(manager, action, selected):
    before = snapshot(manager)
    if action == "list" and selected is None:
        return [v[0] for v in before.values()]
    if action != "remove" or not isinstance(selected, dict):
        raise SystemExit(46)
    hit = before.get(selected.get("id"))
    if hit is None or hit[0] != selected:
        raise SystemExit(46)
    # Pass the descriptor returned by this device, on THIS open connection, once.
    try:
        manager.delete_cred(hit[1])
        after = snapshot(manager)
        if set(after) != set(before) - {selected["id"]}:
            raise ValueError()
    except Exception:
        raise SystemExit(47) from None
    return [v[0] for v in after.values()]


def main(req):
    serial, reader = req["serial"], req["reader"]
    with ExitStack() as stack:
        if reader is not None:
            from ykman.pcsc import list_devices
            matches = list_devices(reader)
            if len(matches) != 1 or matches[0].fingerprint != reader or matches[0].transport != TRANSPORT.NFC:
                raise SystemExit(41)
            dev = matches[0]
            handle = stack.enter_context(dev.open_connection(SmartCardConnection))
            if handle.transport != TRANSPORT.NFC:
                raise SystemExit(41)
        else:
            matches = [d for d, info in list_all_devices() if str(info.serial) == serial and d.transport == TRANSPORT.USB]
            if len(matches) != 1:
                raise SystemExit(41)
            dev = matches[0]
            handle = stack.enter_context(dev.open_connection(FidoConnection))
        # Re-read identity on the same handle that will authenticate and act, even on USB.
        info = read_info(handle, dev.pid)
        if str(info.serial) != serial:
            raise SystemExit(41)
        if not (info.config.enabled_capabilities.get(dev.transport, 0) & CAPABILITY.FIDO2):
            raise SystemExit(42)
        ctap = Ctap2(SmartCardCtapDevice(handle) if reader is not None else handle)
        if not CredentialManagement.is_supported(ctap.info):
            raise SystemExit(42)
        if not ctap.info.options.get("clientPin"):
            raise SystemExit(43)
        if ctap.info.force_pin_change:
            raise SystemExit(49)
        client = ClientPin(ctap)
        token = client.get_pin_token(req.pop("pin"), ClientPin.PERMISSION.CREDENTIAL_MGMT)
        rows = operate(CredentialManagement(ctap, client.protocol, token), req["action"], req["selected"])
    print(json.dumps(dict(schema=1, serial=serial, reader=reader, rows=rows), ensure_ascii=True))


try:
    main(json.load(sys.stdin))
except CtapError as e:
    # Never output arbitrary exceptions, requests, PINs or credential data on error.
    code = {CtapError.ERR.PIN_INVALID: 44, CtapError.ERR.PIN_AUTH_BLOCKED: 45,
            CtapError.ERR.PIN_BLOCKED: 48}.get(e.code, 50)
    raise SystemExit(code) from None
except Exception:
    raise SystemExit(50) from None
