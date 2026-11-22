# NFC preview (0.6.2)

Connect a PC/SC contactless reader such as the HID OMNIKEY 5022 and place a supported YubiKey on it. The existing Set up a YubiKey, Add a passkey and Clear passkeys & PIN flows list USB and NFC endpoints together. Each card shows the connection and, for NFC, the full reader name. The OMNIKEY 5022 and readers explicitly named NFC/contactless/PICC are recognized without a card. Other PC/SC readers become visible as NFC after a YubiKey with NFC capabilities has been successfully read, and remain recognized for the session when empty. Contact-only and unknown failing readers do not generate an unrelated NFC warning. Each visible reader has a compact presence line with errors only when relevant. Windows Hello and known virtual/TPM smartcard endpoints are excluded. Unplugging the reader removes its status line; USB remains available.

Select the intended endpoint explicitly; moving a key from USB to NFC requires a new selection.

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

## Hardware feedback

On 20 November 2026, the operator confirmed detection of a YubiKey 5C NFC (5.4.3) through the HID OMNIKEY 5022. Physical enrollment/addition/reset acceptance is still pending. Preview 0.6.2 shows a compact reader line, excludes known Windows Hello/virtual PC/SC endpoints and requires successful YubiKey identity before learning an unknown reader as NFC. Merely opening a PC/SC connection is not evidence of NFC support. Refresh keeps the current inventory visible with actions disabled and one progress message beside Refresh. These changes address the physical feedback on 0.6.1; physical acceptance of 0.6.2 remains pending.
