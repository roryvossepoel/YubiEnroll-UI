# Read-only inventory; no PINs, credentials, writes or resets.
import json
import re
from ykman.pcsc import list_devices
from yubikit.core import TRANSPORT
from yubikit.core.smartcard import SmartCardConnection
from yubikit.management import CAPABILITY
from yubikit.support import read_info, get_name
from smartcard.Exceptions import NoCardException
from smartcard.pcsc.PCSCExceptions import ListReadersException, EstablishContextException

keys = []
reader_states = []
unavailable = 0
try:
    devices = list_devices("")
except (ListReadersException, EstablishContextException):
    devices = []
    unavailable = 1
# Software-backed PC/SC endpoints are not physical contactless readers.
virtual_reader = re.compile(r"\b(?:Windows Hello|Microsoft Passport|virtual|TPM)\b", re.I)
readers = [d for d in devices if d.transport == TRANSPORT.NFC
           and not virtual_reader.search(d.fingerprint)]
for device in readers:
    name = device.fingerprint
    # ykman treats every non-Yubico PC/SC reader as potentially NFC, including
    # contact/virtual readers. An empty reader is not proof of NFC support.
    # Recognize the tested model and explicit contactless names without a card;
    # otherwise require a real NFC transport (or an earlier verified session).
    known_nfc = bool(re.search(r"\b(?:NFC|contactless|PICC)\b|\bOMNIKEY\s*5022\b", name, re.I))
    state = {"name": name, "nfc": known_nfc, "state": "empty"}
    reader_states.append(state)
    if sum(name.lower() in d.fingerprint.lower() for d in devices) != 1:
        state["state"] = "ambiguous"
        unavailable += 1
        continue
    try:
        with device.open_connection(SmartCardConnection) as connection:
            if connection.transport != TRANSPORT.NFC:
                state["nfc"] = False
                state["state"] = "contact"
                continue
            # Connection.transport is also only a heuristic for third-party readers.
            # Do not remember it as NFC until YubiKey identity has actually been read.
            info = read_info(connection, device.pid)
            if info.serial and TRANSPORT.NFC in info.config.enabled_capabilities:
                state["nfc"] = True
        if not info.serial or not (info.config.enabled_capabilities.get(TRANSPORT.NFC, 0) & CAPABILITY.FIDO2):
            state["state"] = "unsupported"
            unavailable += 1
            continue
        keys.append({"serial": str(info.serial), "model": f"{get_name(info, None)} ({info.version})", "reader": name})
        state["state"] = "ready"
    except NoCardException:
        pass  # Connected reader without a card is a normal state.
    except Exception:
        state["state"] = "unavailable"
        unavailable += 1
print(json.dumps({"schema": 2, "keys": keys, "unavailable": unavailable,
                  "readers": len(readers), "readerStates": reader_states}))
