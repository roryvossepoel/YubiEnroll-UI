# Read-only inventory; no PINs, credentials, writes or resets.
import json
from ykman.pcsc import list_devices
from yubikit.core import TRANSPORT
from yubikit.core.smartcard import SmartCardConnection
from yubikit.management import CAPABILITY
from yubikit.support import read_info, get_name
from smartcard.Exceptions import NoCardException
from smartcard.pcsc.PCSCExceptions import ListReadersException

keys = []
unavailable = 0
try:
    devices = list_devices("")
except ListReadersException:
    devices = []
    unavailable = 1
readers = [d for d in devices if d.transport == TRANSPORT.NFC]
for device in readers:
    name = device.fingerprint
    # Native --reader selectors use a case-insensitive substring, not exact matching.
    # Reject duplicate/overlapping names before offering a selectable target.
    if sum(name.lower() in d.fingerprint.lower() for d in devices) != 1:
        unavailable += 1
        continue
    try:
        with device.open_connection(SmartCardConnection) as connection:
            if connection.transport != TRANSPORT.NFC:
                continue
            info = read_info(connection, device.pid)
        if not info.serial or not (info.config.enabled_capabilities.get(TRANSPORT.NFC, 0) & CAPABILITY.FIDO2):
            unavailable += 1
            continue
        keys.append({"serial": str(info.serial), "model": f"{get_name(info, None)} ({info.version})", "reader": name})
    except NoCardException:
        pass
    except Exception:
        # Busy, unsupported or restricted cards are never inferred to be usable keys.
        unavailable += 1
print(json.dumps({"schema": 1, "keys": keys, "unavailable": unavailable, "readers": len(readers)}))
