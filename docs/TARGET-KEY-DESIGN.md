# Explicit target key and YubiEnroll workflow

Decision (2026-11-13): retain YubiEnroll for enrollment, PIN generation and profiles. Preview 0.5.3 supports administrator sign-in followed by enrollment with only the confirmed target connected. Direct SDK integration is deferred.

## Operator workflow

Home offers two tasks: **Enroll a key** and **Reset FIDO**. Both share one confirmed target selection. Discovery lists devices but never commits a target automatically. The operator chooses a model/serial and selects **Continue**. Navigation preserves the selection and task step; returning to the Key step allows a deliberate change. **Refresh** refreshes inventory. Removing the target disables actions and never substitutes the remaining administrator key. If the target disappears before an operation, the relevant wizard returns to Key and clears reset confirmation. Reconnecting the exact target restores its list entry but requires a fresh Continue/review. Device-change notifications immediately invalidate action readiness and schedule discovery; snapshots overtaken by another USB event are discarded. Discovery waits while native operations are running. Explicitly switching to a different target clears the previous recipient and key name, while preserving the signed-in tenant and chosen profile.

Enrollment shows one step at a time: **Key → Tenant → User → Review**, followed by the result. Review includes the profile, editable registration name and applicable destructive warning. Reset FIDO has **Key → Confirm → Result**. Its confirmation requires explicit acknowledgement and administrative access. Reset is bound to the confirmed serial through `ykman --device SERIAL fido reset`; native same-device reinsertion/touch remain. USB changes revoke the reset acknowledgement. Navigation alone never starts an operation or rereads an already loaded inventory.

Registered keys uses a compact list; the user and tenant appear once, followed by display name, distinct model and selectable registration ID. There is no raw tool output and no extra ID expander.

## Enrollment limitation — do not claim device binding

The documented YubiEnroll 1.2.0 `credentials add` interface has no USB serial selector. `--reader` selects a smart-card reader for NFC, not a USB serial. The current command builder has no target argument. Neither a UI dropdown nor checking the number of keys creates a binding between that CLI process and a USB device.

The app blocks enrollment with multiple or unidentified connected keys, or a missing/changed target. It rechecks before review and again after provider activation before launching the CLI. The UI first permits sign-in with the administrator key attached, then identifies the other serials to disconnect. The single-key CLI flow still requires native physical confirmation and keeping only that key connected throughout. Preflight does **not** eliminate device changes during the CLI operation. It does **not** implement the requested administrator-key-plus-target-key workflow.

## Deferred option: direct integration

If revisited, use Yubico's .NET SDK to resolve the selected serial to a specific `IYubiKeyDevice` and construct the FIDO session from that device. Browser sign-in remains separate: its administrator authenticator is not the enrollment target.

This requires owning the Entra enrollment flow: delegated authentication, retrieving Microsoft Graph creation options, applying the selected profile to the specific device, creating the passkey, encoding its attestation response and registering that response with Graph. The native CLI's private token cache must not be repurposed. Existing tenant app registration/redirect/scopes and profile support need an explicit compatibility review. Unsupported settings must block before any reset or write.

Capture the target identity at operation start; forbid selection changes while operating; revalidate exact identity after reinsertion and never fall back to the first discovered device. Partial failures must distinguish key changes, credential creation and Graph registration; never retry destructive operations automatically. PINs, tokens and raw secret output must not enter the activity log.

Acceptance requires two real keys, removal/reinsertion, touching the wrong key, failed authentication, expired privileges, Graph failure after local creation and native SDK capability/profile differences. The administrator key's FIDO state must remain unchanged. Passing mock tests alone is not acceptance of this integration.

## Primary references

- https://docs.yubico.com/software/yubikey/tools/yubienroll/commands.html
- https://docs.yubico.com/yesdk/yubikey-api/Yubico.YubiKey.YubiKeyDevice.html
- https://docs.yubico.com/yesdk/yubikey-api/Yubico.YubiKey.Fido2.Fido2Session.html
- https://learn.microsoft.com/en-us/graph/api/fido2authenticationmethod-creationoptions?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/authentication-post-fido2methods?view=graph-rest-1.0
