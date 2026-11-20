# Read-only probe executed by the installed ykman 5.9.2+ scripting runtime.
# No PIN input, authentication, credentials, configuration writes, or resets.
import json
import sys
from ykman.device import list_all_devices
from yubikit.core import TRANSPORT
from yubikit.core.fido import FidoConnection
from yubikit.management import CAPABILITY
from fido2.ctap2 import Ctap2

serial = int(sys.argv[1])
reader = sys.argv[2] if len(sys.argv) > 2 else ""
if reader:
    from ykman.pcsc import list_devices
    from yubikit.core.smartcard import SmartCardConnection
    from yubikit.support import read_info
    matches = list_devices(reader)
    if len(matches) != 1 or matches[0].fingerprint != reader or matches[0].transport != TRANSPORT.NFC:
        raise SystemExit(4)
    device = matches[0]
    with device.open_connection(SmartCardConnection) as connection:
        device_info = read_info(connection, device.pid)
    if device_info.serial != serial:
        raise SystemExit(4)
else:
    matches = [(d, i) for d, i in list_all_devices()
               if i.serial == serial and d.transport == TRANSPORT.USB]
    if len(matches) != 1:
        raise SystemExit(4)
    device, device_info = matches[0]
if not (device_info.config.enabled_capabilities.get(device.transport, 0) & CAPABILITY.FIDO2):
    raise SystemExit(5)
with device.open_connection(FidoConnection) as connection:
    info = Ctap2(connection).info
    options = info.options
    # Only a small allowlist of non-secret capabilities leaves this process.
    data = {
        "schema": 1,
        "reader": reader or None,
        "forcePinChange": bool(info.force_pin_change),
        "serial": str(device_info.serial),
        "firmware": str(device_info.version),
        "options": {k: options[k] for k in (
            "clientPin", "rk", "authnrCfg", "setMinPINLength", "alwaysUv", "ep"
        ) if k in options},
        "minimumPinLength": info.min_pin_length,
        "maximumPinLength": min(info.max_pin_length, 8) if device_info._is_bio and
            (device_info.config.enabled_capabilities.get(TRANSPORT.USB, 0) & CAPABILITY.PIV)
            else info.max_pin_length,
        "resetBlocked": bool(device_info.reset_blocked & CAPABILITY.FIDO2),
        "resetOverNfc": not info.transports_for_reset or "nfc" in info.transports_for_reset,
        "resetOverUsb": not info.transports_for_reset or "usb" in info.transports_for_reset,
        "alwaysUvLocked": bool(device_info.fips_capable & CAPABILITY.FIDO2),
    }
print(json.dumps(data))
