# NFC preview (0.6.0)

Connect a PC/SC contactless reader such as the HID OMNIKEY 5022 and place a supported YubiKey on it. The existing Set up a key, Add a passkey and Reset FIDO flows list USB and NFC endpoints together. Each card shows the connection and, for NFC, the full reader name. Select the intended endpoint explicitly; moving a key from USB to NFC requires a new selection.

YubiEnroll CLI 1.2.0 accepts `credentials add ... --reader NAME`. YubiKey Manager CLI 5.9.2 accepts `--reader NAME fido info/reset`. A reader selector cannot be combined with ykman's USB serial selector. Both native tools match reader names by substring: the read-only inventory rejects ambiguous matches and checks serial, firmware, enabled NFC FIDO2 and actual capabilities before offering a target. Keys without a readable serial remain unsupported.

Windows PC/SC presence monitoring detects card/reader insertion and removal without opening a card. It invalidates stale selections/confirmations and schedules refresh alongside the existing USB notifications and Refresh button. Native operations own the card while running; discovery waits until the operation has finished. Reader/service failures do not block usable USB targets.

An NFC enrollment explicitly selects the reader, so an administrator's USB key may remain connected. USB enrollment still requires a single accessible USB target. Do not replace the target while an operation is starting or running: YubiEnroll binds to a reader, not a serial, and app preflight is not an atomic device lock. During native reset prompts, remove and replace the same key only when requested; ykman verifies its identity on reinsertion. Profiles requesting reset are checked against the current transport's reset capabilities. No USB fallback or broader factory reset is attempted.

## Hardware acceptance — HID OMNIKEY 5022

Automated checks use isolated device doubles and the real CLI packages without attached hardware. They do not establish physical reader compatibility. Test with disposable test credentials:

1. Empty reader, card placed, card lifted, reader unplugged/reconnected: list updates; no automatic target selection.
2. Select key A, then replace it with key B: enrollment/reset are blocked until B is explicitly selected. Repeat with A also connected via USB.
3. Set up a key with a compatible profile over NFC, including initial PIN generation and opt-in logging. Verify the Entra registration and sign-in.
4. Add a passkey over NFC using the existing PIN; verify old credentials and PIN are preserved. Leave a separate USB admin key connected.
5. FIDO reset with an allowed NFC transport: follow native removal/replacement prompts; verify same serial and PIN Not set afterwards. Verify profiles with a forbidden NFC reset are blocked.
6. Two readers, overlapping names, unsupported card, competing key tool, service interruption: no wrong-reader selection or USB fallback.

Viewing/deleting credentials stored on a key is outside this change; the existing Registered keys view still shows Entra registrations.

## Official references

- https://docs.yubico.com/software/yubikey/tools/yubienroll/commands.html
- https://docs.yubico.com/software/yubikey/tools/ykman/Base_Commands.html
- https://github.com/Yubico/yubikey-manager/blob/5.9.2/ykman/pcsc/__init__.py
- https://github.com/Yubico/yubikey-manager/blob/5.9.2/ykman/_cli/fido.py
