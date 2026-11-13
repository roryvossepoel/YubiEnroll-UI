# Explicit target key: implementation draft

Status: UI and fail-closed CLI preflight implemented; direct multi-key enrollment **not implemented**. This branch is not a replacement release for 0.4.2.

## Operator workflow

Overview and Enroll key share one target selection. Discovery lists devices but never commits a target automatically. The operator chooses a model/serial and selects **Use this key** once. Navigation preserves that selection; **Change key** explicitly opens the chooser again; **Refresh keys** refreshes inventory. Removing the target disables actions and never substitutes the remaining administrator key. Reconnecting the exact target can restore it.

Enroll key places target, tenant, user and profile/name vertically in that order. Reset FIDO is an action on Overview, bound to the confirmed serial through `ykman --device SERIAL fido reset`. Its destructive confirmation and native same-device reinsertion/touch remain. Key Management is no longer a navigation destination, so visiting it cannot cause repeated initialization.

Registered keys uses a compact list; the user and tenant appear once, followed by display name, distinct model and selectable registration ID. There is no raw tool output and no extra ID expander.

## Enrollment limitation — do not claim device binding

The documented YubiEnroll 1.2.0 `credentials add` interface has no USB serial selector. `--reader` selects a smart-card reader for NFC, not a USB serial. The current command builder has no target argument. Neither a UI dropdown nor checking the number of keys creates a binding between that CLI process and a USB device.

This draft blocks enrollment with multiple or unidentified connected keys, or a missing/changed target. It rechecks before review and again after provider activation before launching the CLI. The single-key CLI flow still requires native physical confirmation and keeping only that key connected throughout. Preflight does **not** eliminate device changes during the CLI operation. It does **not** implement the requested administrator-key-plus-target-key workflow.

## Required next integration

Use Yubico's .NET SDK to resolve the selected serial to a specific `IYubiKeyDevice` and construct the FIDO session from that device. Browser sign-in remains separate: its administrator authenticator is not the enrollment target.

This requires owning the Entra enrollment flow: delegated authentication, retrieving Microsoft Graph creation options, applying the selected profile to the specific device, creating the passkey, encoding its attestation response and registering that response with Graph. The native CLI's private token cache must not be repurposed. Existing tenant app registration/redirect/scopes and profile support need an explicit compatibility review. Unsupported settings must block before any reset or write.

Capture the target identity at operation start; forbid selection changes while operating; revalidate exact identity after reinsertion and never fall back to the first discovered device. Partial failures must distinguish key changes, credential creation and Graph registration; never retry destructive operations automatically. PINs, tokens and raw secret output must not enter the activity log.

Acceptance requires two real keys, removal/reinsertion, touching the wrong key, failed authentication, expired privileges, Graph failure after local creation and native SDK capability/profile differences. The administrator key's FIDO state must remain unchanged. Passing mock tests alone is not acceptance of this integration.

## Primary references

- https://docs.yubico.com/software/yubikey/tools/yubienroll/commands.html
- https://docs.yubico.com/yesdk/yubikey-api/Yubico.YubiKey.YubiKeyDevice.html
- https://docs.yubico.com/yesdk/yubikey-api/Yubico.YubiKey.Fido2.Fido2Session.html
- https://learn.microsoft.com/en-us/graph/api/fido2authenticationmethod-creationoptions?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/authentication-post-fido2methods?view=graph-rest-1.0
