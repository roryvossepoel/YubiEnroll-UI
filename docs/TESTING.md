# Manual acceptance checks for preview 0.4.2

## Preview 0.4.3: current acceptance workflow

Earlier version sections below are historical checks; use this workflow for the current navigation.

- Choose a target explicitly on Overview or Enroll key. A sole discovered key is not selected automatically. Navigate between Overview, Enroll key and Settings: no discovery is triggered by navigation and the serial remains the same.
- Connect an administrator key and target. Sign-in remains available. Before sign-in the UI says to authenticate first; afterward it lists the other serials to disconnect. Enrollment stays disabled until only the target remains. User search and Registered keys remain available.
- Disconnect the administrator key: inventory refresh keeps the target and enables enrollment when the tenant, user and profile are ready. Disconnect the target instead: it must never be replaced by the administrator key.
- While device discovery is pending, key actions are disabled immediately. Failed or unrecognized discovery cannot leave stale action readiness enabled. Explicit Refresh recovers normally.
- Review the vertical target/tenant/user/profile steps and fixed action footer at 100%, 150% and 200% scaling. The target serial must remain visible when scrolling down to the profile. Inspect the compact registered-keys list and confirmation dialog.
- Confirm **Change key** with a different device: the old user and registration name must be cleared; the tenant session and selected profile stay. Refreshing or confirming the same key must preserve a custom name and recipient.
- Use a disposable target for enrollment. Confirm it in YubiEnroll’s native prompts and leave other keys disconnected until it finishes. If sign-in is requested again, cancel before reconnecting the administrator key; check any uncertain result before retrying.
- Reset FIDO from Overview on a disposable target. Confirm the serial, follow native reinsertion/touch, and verify PIN read-back. With the administrator key also attached, reset must still address only the confirmed serial. Never use a production key for acceptance testing.
- The inventory check is not a running-process device binding. Real CLI login, PIN generation, reset/enrollment and interrupted-operation outcomes require hardware acceptance; automated CI does not prove these.



Use a test tenant/user and a spare key. Record the Windows architecture, YubiEnroll version and key firmware. These checks have not yet been executed against physical hardware for this C# port.

| Check | Expected result |
|---|---|
| Missing .NET Desktop Runtime 10 x64 | On a clean test machine, the native launcher reports the missing runtime and offers a download before the GUI opens. Install the runtime, then restart. |
| Missing YubiEnroll | Prerequisite status says it is not located; install it in the standard folder, then use Settings → Prerequisites → Recheck. |
| Missing config | Window points to Settings → Tenants → Add tenant without an exception; cancellation leaves the configuration absent. |
| Add tenant | Invalid IDs/redirect/PIN length stay in the form; valid values save and load the tenant and selected profile. |
| Existing configuration | Add a second tenant; existing text and active provider remain intact; a backup contains the original. |
| Multiple profiles | Create two profiles with different PIN lengths; both remain selectable, including after restart. |
| Edit shared profile | Link two tenants to one profile; editor lists both, saves all seven settings and preserves other profiles, endpoints and tenant links. |
| Cancel profile | Cancel a new/edit profile form; configuration remains unchanged. |
| Concurrent change | Edit configuration after opening Add tenant; saving is refused and external changes remain intact. |
| Native CLI compatibility | After saving, `yubienroll providers show <name>` and `profiles list` show the same tenant/profile. |
| Invalid executable | Diagnostics reports the native Windows error; config still loads independently. |
| Start normally | Existing provider/profile configuration loads; installed CLI version is displayed. |
| System information | Executable, version and TOML path appear only under Settings → Prerequisites. |
| Choose non-default profile | Search and enrollment keep that profile; native CLI shows the same settings. |
| Switch tenant | Old user and connection clear; search/preparation stay disabled until Connect completes. |
| Change config externally | Next command refuses stale provider/profile settings and asks for reload. |
| Connect | Browser and interactive console work; GUI resumes after Enter. |
| User with different mail and UPN | Selected Entra object ID matches the intended account; no email guessing. |
| Search format not recognized | Raw output available in Details; enrollment remains disabled. Report a redacted sample for parser support. |
| Profile reset false | Confirmation and CLI use no-reset. Existing FIDO credentials are not deliberately erased. |
| Profile reset true | No duplicate checkbox; reset warning and confirmation appear. Only proceed using a disposable test key. |
| Long device prompts | GUI repaints; tenant/profile/buttons cannot be changed while active. |
| Success | Console exit 0 recorded; credential list displayed; separately confirm actual sign-in. |
| Failure or early console close | No automatic retry or success claim. Check both cloud and key state before retrying. |
| Normal UAC | Native elevation prompt works without elevating the GUI. |
| Elevated fallback | App relaunches and reloads that Windows account's configuration. Check same-account and different-account scenarios. |
| PIN handling | PIN appears only in native console; no GUI activity entry/file/clipboard copy contains it. |

Automated checks use synthetic data only. They test TOML names/comments, stale configuration, strict object-ID extraction, safe reset/profile arguments and concurrent output handling. The published WPF startup test covers first-run setup, add-tenant form validation/save/cancel, malformed TOML, an invalid executable and successful initialization with a mock CLI. The published console helper is also started. Authentication and hardware enrollment remain manual checks.

A separate CI integration check uses the official signed YubiEnroll 1.2.0 MSI on a disposable runner, with synthetic local configuration only. It verifies that the native CLI reads the generated provider and profile; it never authenticates or enrolls a key.

Additional 0.2.0 checks: import preview without writing, cancelled import, malformed/unsupported import, stale preview, backup and replacement, sanitized export round-trip, tenant editing/profile reassignment, and a profile with omitted reset. Settings closes without changing the connection when no configuration changes were made. Native Entra logout is not offered. CI renders synthetic screenshots for visual review; real login, reset confirmation and physical-key enrollment remain manual.

## Standalone FIDO reset (0.4.0)

Use only a disposable key with known test credentials and record its serial, model, firmware and ykman version. Physical reset and UAC checks below remain manual; CI uses a simulated reset service and read-only native CLI commands.

| Check | Expected result |
|---|---|
| Missing/old ykman | Key Management explains the optional 5.9.2+ (5.x) prerequisite; enrollment remains available. |
| Standard user | Reset disabled; restart as administrator offered. After restart, key must be selected and confirmed again. |
| No key / no serial / FIDO disabled | No selectable key; no command targets an arbitrary device. |
| Multiple keys | Select by serial; only that serial is passed to ykman. Verify the other key's test credentials remain usable. |
| Cancel GUI confirmation | No reset command; existing credentials still work. |
| Unplug/swap before or during confirmation | Reset blocked if selected key absent/changed; reinsert prompt must reject a different serial. |
| Successful reset | Follow native confirmation/reinsert/touch; exit 0 reported. Read FIDO info, verify PIN removed and old test credentials no longer sign in. Entra records still exist. |
| Cancel native prompt / omit touch | CLI failure/unknown outcome; no automatic retry or success claim. Inspect the key. |
| Close console early | Outcome unconfirmed, never success. Inspect the key and make sure the native process has finished before another action. |
| UI while resetting | Device, refresh, reset and closing disabled; main window cannot start another action. |
| Profiles and other apps | Standalone reset does not edit TOML. Verify known test data in PIV/OATH/OTP remains unchanged on supported models. |
| Model-specific restriction | Respect native warnings/errors; no fallback to `config reset` or another destructive command. |

Core tests cover CLI version/serial validation, duplicate/unknown device output, cancellation, missing/swapped devices, failed FIDO access, exact serial targeting and failed command outcomes without retry. WPF smoke tests cover prerequisites, administrator gating, explicit selection, cancellation, device removal, busy/close protection and result messages, with no physical reset. A signed native ykman 5.9.2 compatibility check only runs version, list and reset **--help**.

### PIN status and reset return flow (0.4.0)

- Open Key Management with a key that has a PIN: its entry shows **FIDO PIN: Set**. No PIN value is read or requested.
- Complete a reset and press Enter in the console: the same key is selected again, its entry shows **Not set**, the reset button is enabled, and the result states that PIN status was read back. The reset is never repeated automatically.
- If several keys are connected, verify each displayed status belongs to that entry's serial. Removing the reset target must not select another key.
- Failed/unrecognized status reads show **Unknown**; disabled/unsupported or blocked PIN states are not reported as no PIN. A reset failure remains unconfirmed even if the PIN happens to be unset.
- If the inventory reload fails, the original CLI outcome remains visible with Refresh guidance. Refresh must recover normally.
- The console's generated-PIN reminder appears only for enrollment, not for login or standalone reset.

Synthetic UI tests cover refreshed selection/status, cancellation, failed reads, a PIN still set after exit 0, target removal/replacement and reload recovery. Core fixtures cover the documented native status formats. Hardware sign-in validation remains manual.


## Preview 0.4.0 UI and embedded terminal acceptance

1. Start on Overview without tenant authentication. Inspect a single key, multiple keys, no key, non-admin access and missing optional ykman. Unknown PIN status must not appear as Not set.
2. Open Settings and Key Management from the sidebar. Verify they use the same content area; edit dialogs may still be modal. Inspect all pages at small window sizes and 100/150/200% display scaling.
3. Rename a shared profile using spaces; confirm every linked tenant retains that profile and other settings remain unchanged. Reject duplicate names. Remove an unused profile; refuse removal while referenced. Rename/remove an active tenant, reopen the app and explicitly reconnect before enrollment.
4. Import a test configuration: confirm incoming entries and existing entries being replaced are visible, Cancel changes nothing, and a successful replacement reports the backup path. Confirm export omits credentials/authentication data.
5. Enable **Use embedded terminal (preview, this session)** under Settings → Prerequisites and navigate back to Prepare Key. Run login and then a disposable-key preparation. Test typing, hidden PIN input, generated PIN output, confirmation, touch and remove/reinsert. Record PIN securely before returning. Confirm no interactive transcript appears in activity/diagnostics or exported settings.
6. Verify native failures return their exit code, do not auto-retry, and leave the UI usable. Never interrupt a live reset as a casual test. Use the external console for the next attempt if the embedded mode cannot support your CLI/UAC environment, after inspecting any uncertain outcome.
7. Reset FIDO on a disposable key. After completion, verify the same device is selected if still present and PIN status has been reread. Disconnecting it must not select another key automatically.
8. No claim of physical-device acceptance is made by the synthetic CI checks. CI exercises ConPTY input, no-echo synthetic input, VT rendering and exit-code propagation without authenticating or touching a key.

The optional temporary enrollment register/PIN handover is tracked in issue #19; it is not part of this preview. Additional application reset options remain in issue #7.


## Preview 0.4.1 feedback acceptance
- Maximize on the primary and secondary monitor at 100%, 150% and 200% scaling. The window must fill the work area above the taskbar.
- Inspect one key, navigate to Key Management: the same serial is selected. Add a second key, then remove the selected key: another device must never be substituted silently. Reinsert and refresh.
- Verify blocked / unset / unknown PIN presentation. Unknown is not evidence that a key is empty.
- Connect using the embedded terminal (now default). Native confirmation remains. Browser sign-in success returns automatically; failures remain visible until Return. External console remains available in Settings.
- Close the external console AFTER the native tool has completed: the log must retain the native exit code, not the host window's close code. Closing before completion must not be treated as success.
- Search a user, open Registered keys, refresh, close: tenant/user/profile selection stays intact. These are cloud registrations, not a physical-key inventory.
- Inspect Settings → Activity log after a failed enrollment. HTTP status/request IDs may be recorded; no generated PIN, tokens or terminal transcript may be exported.
- A 405 response must remain reported as a 405; do not infer missing role activation. Retry with the required role activated in the correct tenant and retain request IDs if the error persists.
- Enrollment still uses native YubiEnroll device selection. The review dialog explicitly distinguishes the inspected key from the native enrollment target. Use one test key and confirm the physical device.
- Native USB reset, real Entra/PIM sign-in and enrollment require manual acceptance. Extra application resets and encrypted batch handover remain out of scope for this polish.


## Preview 0.4.2 operator acceptance

- Overview navigation must preserve the inspected key and must not launch another read. Refresh and USB changes still update it; enrollment/reset completion must refresh device state.
- A blocked FIDO PIN offers a direct Key Management route and states that FIDO reset removes the PIN and all passkeys. No reset occurs simply by navigating.
- Confirm the default 1120 × 860 window, capped to the current work area, fits the preparation form at normal desktop scaling. At small sizes, scrolling must remain available with the prepare action visible.
- Settings body text must retain Segoe UI 14 when hosted. Prerequisites lists YubiEnroll, ykman, then administrative permissions. Help & About is project information/documentation.
- Settings has no Done button or persistent tenant/profile totals. Activity appears directly inside its tab; the same session events survive navigating away and back. Copy/export remain explicit and exclude terminal transcripts/PINs.
- Registered keys displays the registration ID directly, without a Tool response section. An unrecognized response is not proof of no keys.
- The key-name suggestion uses the inspected model without firmware and its serial, e.g. `YubiKey 5C (12345678)`. Custom names survive device refresh. The native CLI still chooses the enrollment device; verify the physical device in the confirmation/prompt. No claim of automatic serial targeting is made.
- Prepare Key has no profile-details expander or repeated reset notice; the final enrollment review retains the exact profile reset warning.
- Confirm the original charcoal/green icon in Explorer, title bar, Alt+Tab and the taskbar after launching the new executable. Existing pinned shortcuts may reference an older download; unpin that old shortcut and pin the new executable when testing.
- Verify real Connect output remains visible before browser authentication completes. The app must provide a waiting message even when the CLI has not emitted output. Test real enrollment prompts, hidden PIN input and generated PIN visibility using a disposable key. External console remains an explicit fallback; never auto-retry a started operation.

Automated regressions include live terminal text before input/process exit, actual native YubiEnroll `--help` through the published renderer (no authentication), both native window icon sizes, hosted fonts, direct Activity navigation and editable name suggestions. Physical browser sign-in and taskbar shell caching remain manual checks.
