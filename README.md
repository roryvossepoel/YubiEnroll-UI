# Passkey Desk for YubiKeys

YubiKey setup & passkey management. Inspect passkeys on a physical YubiKey, manage YubiKey registrations in Microsoft Entra, or set up a key for a user.

## 0.11.7

**Search tenant passkeys** opens a read-only inventory for one Entra tenant. Choose a tenant, connect, then filter the registrations by user, UPN, registration name, model or serial. The serial column identifies numbers taken from a YubiKey registration name. Missing or ambiguous serials appear as Unknown. The inventory reads directory users and their FIDO2 methods with the signed-in operator’s delegated Graph access. You can filter while it loads, cancel or refresh. The view reports unreadable accounts and incomplete searches.

The activity and registration logs load retained records in the background. Search and export use the complete filtered list. The installer uses a proportional font and reflows the license text.

Machine policy revision 1.5 supports ten tenant slots and five profile slots. TenantsJson and ProfilesJson independently replace the complete list for their section. An explicit empty array clears that section. Removing the JSON policy restores the configured slots at the next start. The policy folder includes an example of each JSON schema.

## 0.11.6

Windows x64 is available as a portable ZIP and a machine-wide MSI. Wizard actions stay at the bottom of the window as status text changes. A batch registration marked **Check required** explains the outcome and opens the recipient’s Entra registrations. Use **Back to batch** to resume the same queue.

Passkey Desk keeps the ten most recent automatic backups for each TOML file after a persistent configuration change, including policy updates and imports. Enrollment profiles and unchanged settings produce no backups. Previously generated backups count toward the same limit on the next change.

## 0.11.5

The embedded terminal displays the generated initial PIN during both single and batch setup. After a successful setup, the app opens the single registration summary or the current batch recipient’s handoff automatically. The operator chooses **Next user** after handing over the key. Errors, an unverified outcome, a missing expected PIN or a log write failure keep the terminal open for review. The PIN is masked in the summary, handoff and batch results until the operator uses the eye button.

## 0.11.4

The batch results table shows each verified, generated initial PIN behind an eye button until you leave the batch, including when PIN logging is off. Batch CSV exports select **Include initial PINs** by default when PINs are available. Clear the checkbox to export only registration details. An export that includes PINs contains readable PINs. The registration log follows its own settings.

## 0.11.3

Batch issuance verifies the serial on the selected NFC reader after a FIDO reset. The embedded terminal also checks the serial in the successful native reset summary. A missing or mismatched key requires verification in Entra. The registration log is finalized before leaving the embedded terminal. If a generated PIN was not saved, record it before continuing.

The batch wizard shows profile cards, key compatibility, numbered input, recipient and result tables, and fixed footer actions. Leaving the running batch cancels the remaining queue. Completed registrations remain recorded according to the logging settings.

## 0.11.2

In the registration log, the User column shows the recorded email beneath the display name. When no email was recorded, a distinct sign-in name is labeled **UPN**. Existing records use the same display without being rewritten; CSV still exports email and UPN separately.

## 0.11.1

The registration log combines the PIN and its first-use change requirement in one column. The eye next to a saved temporary PIN reveals just that row. Additional passkey registrations show **Existing PIN** with no reveal action. **PIN not logged** means generation was configured but the record contains no PIN. The first-use note describes the profile at registration, not the current state of the key. Logging preferences, CSV columns and machine policy are unchanged.

## 0.11.0

Both logs have subtle column separators and a labeled Copy column. The registration log aligns Clear filters with the date and result fields. The console executable uses the app’s two tiles with a terminal prompt. Both log tables scroll horizontally when columns exceed the available width. In the registration log, an eye icon reveals or hides one initial PIN at a time. In Settings, a hovered tab has a grey underline and the selected tab keeps its green underline. Help & About adapts the introduction to the available window width. Both registration flows show the same review layout. When a temporary PIN is available it stays masked inside the result card until revealed. A failed registration is distinguished from an interrupted or unknown outcome. The local passkey list shows its count in the heading, and selecting a credential keeps its ID on one line. The policy templates are unchanged.

Machine policy support is now included in the standard Windows x64 build. With no recognized machine policy the normal local editing and CLI configuration remain in use. Only the configured section is read-only: tenants, profiles, activity logging, registration logging or terminal preference. A managed list shows a read-only policy badge in place of its transfer action. Under partial policy, only the editable list can be imported or exported. Policies are read at startup; only the declared tenant/profile collections are reconciled into the current account’s ordinary TOML. When policy is present, the startup screen shows its status beneath YubiEnroll CLI and the Activity log records the synchronization outcome once per launch. See [pilot instructions and rollback](policy/PILOT-NL.md). The templates are optional. Both logs have their own retention in days. For registration logging, App settings and the Registration logging policy accept 1–3650 days (default 30). While logging is enabled, expired registration records are deleted from the configured folder; exported CSVs are kept.

Use the bundled ADMX/ADML revision 1.5 with 0.11.7. Below `HKLM\SOFTWARE\Policies\PasskeyDesk`, CLI configuration lives under `YubiEnroll\Tenants` and `YubiEnroll\Profiles\Profile1` through `Profile5`. App settings use `AppSettings\Logging\Activity`, `AppSettings\Logging\Registration` and, if explicitly configured, `AppSettings\Terminal`. Each managed settings group shows its own read-only badge. Leaving the terminal policy unconfigured keeps the choice editable. Flat registry values from previous pilots are ignored.


## Features inherited from preview 0.10.9

Local passkey management follows Key → Unlock → Passkeys. Empty discovery cards give USB or NFC guidance. Profiles summarize reset and PIN settings, show key-specific compatibility, and preserve unspecified imported options. Activity log and Registration log have separate searchable views. Import/export is available from Tenants and Profiles. Help & About lists required software. Activity summaries survive restarts; retention and the log folder are configurable. Only selected diagnostic codes are persisted.

**Set up multiple YubiKeys** issues one new YubiKey per recipient. Sign in to one tenant, choose one configuration profile and paste sign-in names or import a CSV with a `UserPrincipalName` column (up to 500 recipients). Correct duplicate or unmatched rows before starting. The app requires one exact tenant user per row, checks the object ID again before issuance and verifies the selected key. Profiles without reset require an unset PIN; reset profiles explicitly confirm the recipient and serial before clearing FIDO passkeys and the PIN. Select the USB key or named NFC reader and confirm **Register YubiKey** for each user. Keep the key connected until the native command finishes; record any generated PIN in the native prompt before returning. USB uses the existing single-USB-target preflight.

The handoff stays on the same user until **Next user**. Once a serial was attempted it cannot be reused in the batch, including through another connection. An interrupted or uncertain attempt is marked **Check required** and is never retried automatically. You can skip a recipient or cancel the remaining queue. When the batch is complete, export its results as a CSV, with **Include initial PINs** selected when generated PINs are available. The queue is held in memory; the optional registration journal keeps its existing separate settings.

NFC readers are now supported alongside USB for Set up a YubiKey, Add a passkey and Clear passkeys & PIN. Each target shows its connection and full reader name. Connected NFC readers have their own named status even when empty; without a reader the NFC status is hidden and USB stays available. PC/SC presence monitoring refreshes the list when a key is placed or lifted. Native reader selection and transport-aware profile checks are covered by automated tests; physical HID OMNIKEY 5022 acceptance is still required. See [NFC behavior and test checklist](docs/NFC.md).


- **Manage user passkeys** now signs in once at **Connect**. Searching, viewing dates and refreshing share that Graph session.
- Home offers **Set up a YubiKey**, **Add a passkey**, **Passkeys on a YubiKey**, **Manage user passkeys**, and **Clear passkeys & PIN**.
- Setup follows **Key → Profile → Tenant → User → Review**. Profiles are independent of tenants; incompatible choices show their reasons and cannot advance to review.
- Tenant forms no longer assign or create a profile. Existing TOML provider defaults remain readable for native CLI compatibility, but the UI never selects or inherits them. Editing a tenant removes its old default; removing a profile clears legacy references.
- Add a passkey uses the existing FIDO PIN and does not request reset, random PIN, or forced PIN change. It carries forward the detected minimum PIN length and Always UV setting. Missing/blocked PINs, pending forced PIN changes, and unreadable capabilities block this action. Existing enterprise attestation is not requested for the new credential.
- The native CLI receives a unique, temporary explicit profile, so a destructive provider default cannot leak into an addition. Cleanup removes only that unchanged profile, preserving concurrent edits. A crash can leave a harmless `UI-enrollment-*` profile; it can be removed in Settings. Creating and removing this temporary profile does not create configuration backups. Basic setup profiles with an omitted PIN floor also use a temporary, complete native profile with the freshly detected floor: YubiEnroll 1.2.0 requires this field even when no policy change is intended. The reusable profile stays unchanged.
- Both tasks support USB and NFC. USB retains the single-target USB preflight restriction because YubiEnroll has no USB serial selector. NFC uses an explicit reader; a separate USB administrator key can remain connected. Keep the selected target in place throughout the native operation.
- Help & About includes a manual GitHub update check, including published previews. No release yet and network failures have separate messages. Updates open the release page; installation remains manual.
- Prerequisites includes .NET Desktop Runtime 10 x64 alongside YubiEnroll CLI and YubiKey Manager CLI (ykman).
- Review deduplicates the user's sign-in/email information. Task icons share one background; the reset button has explicit readable active/hover/focus/disabled states.

Real hardware testing remains required, especially adding a credential while checking that existing credentials, PIN, and key settings remain usable.


An independent community Windows application for registering YubiKeys on behalf of Microsoft Entra ID users. Built in C# / WPF, based on Rory Vossepoel's PowerShell / WinForms enrollment workflow.

**This is a test build.** Automated tests cover configuration parsing, command arguments and process handling. Actual Entra sign-in, UAC, hardware enrollment and standalone FIDO reset require manual validation with a test user and test key. This is not an official Yubico product.

## What it does

- Reads existing YubiEnroll tenants and profiles, and adds Entra tenants through an Add tenant form. Existing configuration is preserved and backed up before additions.
- Activates the selected tenant using the official CLI and preserves your explicitly chosen profile.
- Searches users and uses their Entra object ID, avoiding confusion between email addresses and usernames.
- Starts on Home with Set up a YubiKey and Clear passkeys & PIN as separate guided tasks. Settings remains available in the same window.
- Provides a dedicated cancellable sign-in screen. Enrollment uses an embedded terminal, with an external-console option under Settings → App settings. Single and batch setups that generate an initial PIN use the in-app terminal so the PIN is available in the summary, including when PIN logging is off.
- Reports the enrollment outcome; cloud registrations are managed separately from enrollment.
- Keeps the UI responsive and prevents another key operation while one is active.
- Uses the selected profile for reset and PIN settings. Shows a destructive-reset warning and asks for confirmation before enrollment.
- Offers **Clear passkeys & PIN** to clear FIDO credentials and the FIDO PIN on a USB or NFC key without removing Entra registrations, using the separately installed YubiKey Manager CLI.
- Keeps an activity history in the configurable Documents/YubiKey Desk/ActivityLog folder (30 days by default). Interactive output is displayed transiently (including any generated PIN), and is not persisted or added to the activity/diagnostic log.

## Preview 0.5.4: optional registration log

Enable **Settings → App settings → Save enrollment records**, choose a folder and save the preference. Logging is off by default. New configurations suggest `Documents/YubiKey Desk/RegistrationLog` under the current account’s Windows Documents folder (including redirected folders). A saved folder stays selected after an upgrade. To switch, choose **Use default** and **Save changes**; existing records are not moved. Review and export records under **Settings → Registration log**. Preferences are stored separately from the shared YubiEnroll TOML in `%LOCALAPPDATA%/YubiEnroll.UI/registration-log-settings.json`. They apply to the current Windows account; elevation into a different account uses that account's preference and history.

Each attempt records its unique ID, start/result timestamps with offsets, tenant name/ID, user display name/UPN/email/object ID, confirmed target model/serial, profile and registration name, configured reset/first-use PIN-change flags and native outcome. Identity fields are read only from recognised labelled CLI columns/fields. An absent or unrecognised email is left blank, never copied from UPN. PIN storage is off by default. Enable **Save generated PINs in the registration log** to include the generated initial PIN in the same JSON record and CSV export. Existing, manually entered PINs are never captured. Tokens, authentication caches and terminal transcripts are excluded. These are personal data stored as local JSON, not an encrypted or tamper-proof audit trail; choose a folder with appropriate access.

An unknown attempt is durably written before enrollment starts. If this fails, enrollment does not start while logging is enabled. Native success is recorded separately from failed/interrupted or unknown outcomes; even a nonzero exit may already have created a credential. Success means YubiEnroll reported completion, not an independent sign-in test or a serial binding of the native tool. The target serial is the UI-confirmed target. The result timestamp is when the app receives the native outcome (after returning from the interactive console), not the Entra credential creation time. Reset and first-use PIN flags describe the reviewed configuration, not later user behavior.

One JSON file per attempt is replaced atomically under a file lock. A crash or unsaved final result leaves the previous unknown record. If the final write fails, the app retains the outcome in memory and offers **Retry saving registration log**, without repeating enrollment. Pending outcomes survive navigation to another key within the session; closing warns before discarding them. Restore folder access and retry before changing folders. Disabling logging stops new records and retains history.

Filter the registration list by tenant, local date range and successful outcome; **Export CSV…** exports the filtered rows with UTF-8 BOM and the current Windows comma/semicolon list separator. Serial numbers are exported as text to preserve leading zeroes; spreadsheet-formula prefixes are escaped. Unsaved in-memory outcomes supersede their unknown disk rows in exports. A damaged/unreadable record blocks export rather than silently dropping history. Exports contain personal data and should be shared deliberately. Standalone FIDO resets and logins do not create registration rows; existing Entra registrations are not backfilled.

## Preview 0.5.3: profile compatibility

- Profile editor offers PIN change optional and PIN change required starting settings, with live requirement descriptions. Basic keeps generated initial PINs while omitting enforced minimum length and forced PIN change; FIDO reset remains a separate explicit choice.
- Read-only capability discovery runs a bundled, fixed Python script through the installed ykman scripting runtime. No additional Python installation or SDK is required. It reads only the selected key's CTAP GetInfo and device metadata; it never requests a PIN, verifies a PIN, reads credentials or changes a key.
- Enrollment checks configurable minimum length / forced change, Always UV, enterprise provisioning, PIN limits, reset restrictions and FIDO2 support. Unknown or malformed readings block enrollment. The same checks run again on fresh data before native enrollment. Unsupported settings are never silently dropped or used to rewrite a shared profile.
- CLI-default checkboxes and generated PIN lengths must be made explicit for verification; existing TOML is preserved until the operator edits it. Current minimum PIN length is a conservative floor even when reset is requested. No destructive probe is used to discover the factory floor.
- Firmware is checked for identity consistency but is not the feature gate. Native YubiEnroll still performs enrollment and final PIN/complexity checks; platform/IdP support and physical hardware tests remain necessary.

## Preview 0.5.2: interaction polish and USB refresh

- USB arrival/removal automatically refreshes the discovered key list alongside the manual Refresh button. A target disappearing before an operation returns the relevant wizard to key selection and clears reset confirmation. Reconnecting does not restore the old confirmation; another key is never substituted.
- Device changes during an in-flight discovery invalidate that snapshot. Refresh is deferred while an operation is running, so native reset/enrollment reinsertion prompts keep control of the key.
- Connect sits beside the tenant selector. An empty tenant list points to Settings → Tenants. Both step indicators have progress connectors.
- Enrollment uses the same slanted USB-key/plus icon on Home and in navigation; Settings uses an eight-tooth gear. Form fields have extra space beside scrollbars.
- Activity rows only expand when they contain details. Prerequisites no longer has Technical details; Import / export explains the shared YubiEnroll TOML file, and documentation links share one row.

## Preview 0.5.1: startup requirements and visual refinement

At startup the app checks administrator access, YubiEnroll and a supported YubiKey Manager CLI version. If all checks pass it opens Home automatically. Otherwise the startup screen offers administrator restart and/or Prerequisites. Settings remains accessible. No key is enumerated or modified by these software checks.

Prerequisites groups each tool’s status, download and documentation in its own card. Detection runs automatically when returning to the startup screen or Prerequisites after an installer/browser; it does not reload configuration, discard tenant sign-in or change the selected key. There is no manual Recheck button. Version probes time out after ten seconds. The .NET Desktop Runtime must already be present for the app itself to launch.

The app mark is rendered as a vector inside the UI; the executable contains separately rendered icon sizes. Home cards use neutral resting borders and distinct hover/focus states. Profiles are explicitly described as YubiKey configuration profiles.

## Preview 0.5.0: guided tasks

Home offers two tasks: **Enroll a key** and **Reset FIDO**. Enrollment shows one step at a time: Key → Tenant → User → Review, followed by an explicit result. Reset follows Key → Confirm → Result. Choose a device by model and serial, then use Continue to confirm it. Discovery never chooses a target automatically.

The approved visual system uses `#84BD00` accents, `#0B536F` headings, and one original community key mark. The square Passkey Desk mark with two overlapping teal tiles and a lime keyhole in `src/YubiEnroll.UI/Assets/App.svg` is the vector master. Run `python tools/build_icon.py` (Inkscape and Pillow) after editing it to regenerate the WPF drawing, PNG and 16–256 px Windows executable/taskbar icon. No official Yubico wordmark is included.

Navigation preserves the target and task step without rediscovery. A different confirmed target clears the previous recipient/name. Missing targets never substitute the remaining administrator key. USB and NFC presence changes invalidate readiness and revoke any reset acknowledgment. Reset requires a reviewed serial, explicit acknowledgment and administrator access; native ykman confirmations and same-key reinsertion remain enabled.

The SDK remains deferred. YubiEnroll still performs enrollment and PIN handling. For USB enrollment, sign in with the administrator key, disconnect it, and keep only the chosen USB target connected during enrollment. For NFC, keep the selected target on the named reader; a separate USB admin key may remain connected. Fresh inventory checks do not bind the running CLI to a USB serial.

The embedded terminal is enabled by default. Its toggle under Settings → App settings applies **for the current session only**. It runs the same CLI commands and native confirmations, has a fixed 100-column screen with scrolling, does not support clipboard paste, and is not a general-purpose terminal emulator. Keep the app open until completion. After successful setup, use the eye in the summary to read the generated PIN for handoff. On a launch/connection failure it never automatically reruns a command in the external console. Physical PIN/touch/reinsertion, UAC and browser login require manual acceptance with test hardware before relying on this mode operationally.

Only standalone **FIDO reset** is currently available. Additional application-specific resets (issue #7), final product name/logo (#9), and optional batch handover (#19) remain separate follow-up work. No repository rename or full factory-reset claim is made in this preview.

## Download and start — no C# knowledge required

1. Open the repository's **Actions** tab and choose a successful **Build Windows preview** run.
2. Under **Artifacts**, download `PasskeyDesk-for-YubiKeys-win-x64` for an Intel/AMD Windows PC. GitHub may require you to sign in.
3. Extract the downloaded artifact once. Keep `PasskeyDesk.exe` and `PasskeyDesk.ConsoleHost.exe` together.
4. Install the **[.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**. Choose **Desktop Runtime**, not just the base .NET Runtime. Visual Studio and the SDK are not required.
5. Install in the standard location and configure [Yubico YubiEnroll](https://www.yubico.com/support/download/#h-yubienroll) separately.
6. Install **[YubiKey Manager CLI](https://developers.yubico.com/yubikey-manager/) 5.9.2 or a newer 5.x release** in its standard location. It is required for target verification, inspection and standalone reset.
7. Start `PasskeyDesk.exe`. The app displays the prerequisites and checks for YubiEnroll at startup.

Only Windows x64 is built for now. Builds are unsigned previews and are not automatically published as GitHub Releases.

## Package contents

The download contains only five files:

- `PasskeyDesk.exe` — the Windows interface.
- `PasskeyDesk.ConsoleHost.exe` — the interactive console for native enrollment prompts.
- `README.md`, `LICENSE`, and `THIRD-PARTY-NOTICES.md`.

Application assemblies are bundled into the two executables; the .NET runtime and YubiEnroll are installed separately. Extract this version to a new folder so old loose DLLs from 0.1.0 do not remain alongside it. An installer that checks these prerequisites may be added later; this preview is a ZIP download.

## Startup troubleshooting

If .NET 10 Desktop Runtime x64 is missing, the native .NET launcher reports the missing runtime before the C# window can open and offers a download link. The GUI cannot perform its own check until .NET is available. See [Microsoft startup troubleshooting](https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch).

A missing configuration folder/file is a normal first-run state: the window offers **Add tenant**, without a technical error. Configuration loading and executable discovery run independently, so Settings remains available to configure tenants and resolve prerequisites.

Open **Settings → Activity log** for the failed step, exception type, HRESULT/native Windows error, Windows account, architecture and attempted paths. The log excludes configuration contents, arbitrary exception messages and enrollment console output. It does contain local account names and paths; redact those before posting publicly.

If the configuration is missing, start the app under the same Windows account used for your working PowerShell/YubiEnroll setup. Elevating with another account changes `%APPDATA%`. If Windows reports native error 740, use the administrator restart fallback. Other errors should be investigated using the diagnostic details rather than assuming elevation is the cause.

## Existing YubiEnroll configuration

The application reuses existing configuration in these locations (legacy first):

- `%APPDATA%\Yubico\yubienroll\yubienroll.toml`
- `%APPDATA%\yubienroll\yubienroll.toml`

A new configuration uses `%APPDATA%\Yubico\yubienroll\yubienroll.toml`, checked against the native YubiEnroll 1.2.0 CLI in CI. The current Yubico documentation omits the `Yubico` directory; our integration check found that 1.2.0 did not read that alternative path. The GUI does not redirect the CLI to ProgramData: a shared configuration path is not documented, and authentication remains per Windows account.

Configuration must belong to the Windows account running the app. Configure provider app registration, permissions and profiles using the [official Entra setup guide](https://docs.yubico.com/software/yubikey/tools/yubienroll/yubienroll-entra.html). For example, start with `yubienroll providers add <name>` and `yubienroll profiles add <name>` as documented by Yubico. The GUI does not create Entra app registrations or grant tenant permissions.

### Add your first tenant

Open **Settings → Tenants → Add tenant** and enter the tenant name, Directory (tenant) ID, Application (client) ID and Redirect URI. The redirect must use `http://localhost` and exactly match the public-client Entra app registration. Configuration profiles are independent: choose one later in the separate Profile step of Set up a YubiKey. New profiles use a random PIN with the chosen length, no reset, no Always UV and no enterprise attestation; forcing a PIN change is an explicit checkbox. Minimum PIN length and forced PIN change require actual CTAP capability support (standard YubiKey 5: generally firmware 5.7+). Use Settings → Profiles → PIN change optional for older keys.

**Save tenant** validates the fields, creates the directory if necessary, and adds the provider. It preserves original configuration text and makes a timestamped `.bak` file before replacing an existing file. Duplicate names and configuration changes since opening the form are rejected. Cancel makes no changes. Close other YubiEnroll sessions while saving; their external writes cannot be locked by this GUI. Close Settings, select the tenant and use **Connect** to activate it and authenticate.

This form targets Microsoft Entra public cloud. For national clouds, use YubiEnroll’s native provider setup to configure the appropriate endpoints.

The executable is detected in the standard Yubico installation folder under Program Files. **Settings → Help & About → Required software** groups installation status and links by tool. Administrator restart is offered on the startup requirements screen when needed. No Yubico executable or configuration containing tenant-specific values is committed to this repository.

### Manage multiple profiles

Open **Settings → Profiles** to create and edit named profiles. All seven settings are available: reset, minimum PIN length, Always UV, enterprise attestation, forced PIN change, random PIN and random PIN length. Minimum and generated PIN lengths are separate fields. Empty numeric fields and indeterminate checkboxes preserve CLI defaults.

Profiles are independent of tenants. Choose a compatible profile for each setup in its own wizard step, immediately after selecting the key. The editor shows its PIN/reset requirements. Renaming or deleting a profile also updates or removes legacy native tenant references, but the UI never uses those references to choose a profile automatically.

Profile saves preserve configuration values, including provider endpoints and other profiles. TOML formatting and comments may change; the exact original file is backed up first. Stale saves and duplicate names are rejected. **Reset now follows the selected profile.** A profile with `reset = true` displays a warning and requires confirmation before enrollment. `false` sends `--no-reset`; an omitted setting leaves the native default/prompt intact. The old duplicate reset checkbox has been removed.

## Enroll a key

1. Open **Set up a YubiKey**, choose the intended target by model and serial, and select **Continue to profile**, choose a compatible profile, then continue to the tenant. Do not select your administrator sign-in key.
2. Select a tenant and click **Connect**. Complete browser sign-in with your administrator key. The app verifies and confirms the native login parameters. **Open browser again** reopens the same session; **Cancel sign-in** returns to tenant selection. Sign-in times out after five minutes. Closing the app offers to cancel sign-in; enrollment/reset retain their busy guard.
3. For USB enrollment, after sign-in **disconnect the administrator key and all other USB security keys**, leaving only the target. For NFC enrollment, keep the target on the selected reader; the USB administrator key can remain connected. The app refreshes on USB changes; **Refresh keys** is also available. User search and registered-key lookup remain available while multiple keys are connected, but enrollment is blocked.
4. Search for and select the intended recipient, then review the suggested editable registration name.
5. Continue to **Review**. Check the target serial, tenant, user, profile and reset warning. Select **Start enrollment** after reviewing the details. The app rechecks the inventory before and after provider activation before launching YubiEnroll.
6. Keep only the target connected until YubiEnroll finishes. Follow its native PIN, touch and same-key reinsertion prompts. If authentication is requested again, cancel that attempt before reconnecting the administrator key; inspect any uncertain outcome before restarting.
7. Record any generated PIN securely. Review **Registered keys…** and test sign-in. For the next key, select **Enroll another key** and confirm the next target; the previous user/name are cleared, while the tenant connection and profile remain.

**The UI does not bind the YubiEnroll process to a USB serial.** YubiEnroll 1.2.0 has no documented USB serial selector. Inventory checks cannot prevent a device swap during the native operation; physical confirmation and the one-key rule remain necessary. The app never disables or modifies the administrator key to hide it from the CLI. SDK-based enrollment is deferred.

Switching tenants or reloading configuration requires Connect again. A successful CLI exit is not independent proof of hardware or sign-in success. Activity is under **Settings → Activity log**; use **Manage user passkeys** on Home or in the sidebar for Entra registrations. PINs and interactive transcripts are not written to the activity log.

### Erase a key without enrollment

Open **Clear passkeys & PIN**. Target verification and this reset feature require **[YubiKey Manager CLI](https://developers.yubico.com/yubikey-manager/) 5.9.2 or newer 5.x** installed in its standard `Program Files\Yubico\YubiKey Manager CLI` folder. The app also checks the older `YubiKey Manager` folder. Version 5.9.2 is checked against the official signed installer in CI. This CLI is separate from YubiEnroll and is not bundled; missing it blocks enrollment until target verification is available.

Run the GUI as administrator, connect the key via USB or place it on an NFC reader, Refresh, and select its model/serial number. Each entry shows the FIDO PIN status read from that key (set, not set, blocked, disabled/unsupported or unknown). No tenant connection or user selection is required. Select **Review reset**, check the serial, acknowledge the erasure and select **Erase FIDO passkeys and PIN**. Then follow ykman's native confirmation, unplug/reinsert and touch prompts in the console. Press Enter when it finishes. The GUI reloads the connected keys and their PIN status, restoring the same selected key if it is still available. After exit 0 it reports whether the PIN is now unset; an unreadable status stays unknown, and a PIN still present requires inspection. This checks PIN state, not every passkey or cloud registration. A different key is never selected automatically. The GUI never passes `--force`, automatically retries, or substitutes a broader factory-reset command.

**This permanently resets all FIDO2/U2F credentials and the FIDO PIN on the selected key, for all accounts. Entra and other service registrations remain and must be removed separately.** PIV, OATH, OpenPGP and OTP are outside this reset. Native restrictions and model-specific warnings still apply (including YubiKey 4 FIPS attestation warnings); if ykman refuses the reset, the GUI does not bypass it. Read [Yubico's FIDO reset documentation](https://docs.yubico.com/software/yubikey/tools/ykman/FIDO_Commands.html).

This preview only selects keys with FIDO enabled and a readable serial number. Serial-less keys remain unsupported. Device identity is checked again after confirmation. USB commands target the selected serial; NFC commands target the verified reader, with serial checks before launch and native same-key reinsertion checks. A successful message means **ykman reported success**; physical-key verification remains necessary. Closing or cancelling the console does not prove that nothing changed. Inspect the key before retrying.

### Entra logout

Closing the GUI is not a logout operation. Yubico currently documents the native `logout` command for Okta only, so this Entra interface does not offer a misleading logout button. Browser sessions and any cached authentication remain managed by YubiEnroll/Microsoft. Use **Connect** when switching tenants. See [Yubico command reference](https://docs.yubico.com/software/yubikey/tools/yubienroll/commands.html#yubienroll-logout).

### Import and export

**Settings → App settings → Import / export** transfers tenants and profiles together as TOML. Export includes supported Entra provider fields, cloud endpoints and the seven profile settings; other custom fields and authentication caches are excluded. The file contains tenant identifiers and should be shared deliberately.

Import validates types, profile references, duplicate names and policy values, and previews the tenant/profile names before any write. Confirming import **replaces the current configuration**, including other custom settings; it does not merge. An exact backup is made and stale previews are refused. Authentication caches are neither copied nor cleared. Reconnect afterward. Unknown import fields are rejected rather than silently discarded.

When enrollment fails or its console is closed early, a credential may already have been created. Inspect **Manage user passkeys** and the key before retrying. There is no automatic retry, rollback, deletion or reset.

## Elevation

The GUI starts with the current Windows rights. If it is not elevated, the startup requirements screen offers **Restart as administrator** before key tasks are available. This opens the Windows UAC prompt. Cancelling leaves the existing app open; successful process launch closes the old instance. Settings remains accessible without elevation.

If UAC uses a different Windows administrator account, the new process sees that account's `%APPDATA%`, configuration and authentication state. No configuration or tokens are copied between accounts. Recheck the tenant banner after restarting.

## Preview limitations

- Entra ID only; other provider types are not shown.
- Multiple tenants and profiles can be created and edited under Settings. Names can be changed, including spaces. Tenant renaming updates active_provider. Tenants and profiles can be removed locally; deleting a profile clears any legacy native tenant references. Tenant edits preserve custom cloud endpoints; configure those through native setup or import.
- Search parsing accepts GUID-first table rows and `ID:` blocks. It intentionally rejects unknown formats. Unrecognized output is not used for enrollment; real output from the installed YubiEnroll version still needs validation.
- No automated bulk enrollment, cloud credential deletion or automatic PIN delivery. The optional registration log records issuance metadata and exports CSV; generated initial PINs are included only when enabled. Key selection lists connected serial-addressable FIDO keys. Enrollment requires only the confirmed target to remain connected; multiple keys are permitted for login and serial-targeted standalone reset.
- One operation at a time within each GUI window. **Do not use a second GUI instance or another YubiEnroll session concurrently:** the CLI has a shared active provider. Configuration checks detect changes before commands but cannot lock out external tools.
- No telemetry. The optional registration log persists issuance records separately from the activity history. Activity can contain user and tenant identifiers; redact them before sharing screenshots or bug reports. The official CLI's own logging settings are outside this GUI's control.
- Closing the console prematurely is treated as unsuccessful/unknown; wait for the CLI result and press Enter instead.

## Build from source

The app is **Passkey Desk for YubiKeys**, published by **Rory Vossepoel**. The executable is `PasskeyDesk.exe`. Update existing shortcuts or deployment commands that referred to `YubiEnroll.UI.exe`. Existing settings and registration records retain their locations. For optional self-signed internal builds, see [internal code signing](docs/SIGNING.md). Standard GitHub preview downloads remain unsigned.

On Windows, install the .NET 10 SDK, then run:

```powershell
./build.ps1 -Runtime win-x64
```

Add `-IncludeMsi` on Windows to package the same files as a machine-wide installer in `artifacts/win-x64/installer/`. The MSI installs to `%ProgramFiles%\Passkey Desk`, adds **Passkey Desk** to the all-users Start menu and registers **Passkey Desk for YubiKeys** in Programs and Features. The .NET Desktop Runtime and the Yubico command-line tools are separate prerequisites.

The script runs the core checks and publishes a framework-dependent x64 app with its console helper. The ZIP is written to `artifacts/<runtime>/`. The published WPF app is startup-tested on the x64 GitHub runner with first-run setup, form validation/save/cancel, malformed configuration, an invalid executable, and a mock CLI. The published console helper is also started. A separate Windows CI check downloads the signed official YubiEnroll 1.2.0 installer, verifies its publisher, and checks that the CLI reads a synthetic provider/profile generated by the GUI writer. A separate check verifies the signed ykman 5.9.2 installer, expected installation folder, version parser, empty device discovery and serial-targeted reset help. No login, tenant access, reset command or attached hardware is used; neither Yubico CLI is redistributed with the app.

For a quick logic test on any .NET 10 development machine:

```text
dotnet run --project tests/YubiEnroll.Tests -c Release
```

See [manual validation](docs/TESTING.md) before operational rollout. Changes are reviewed through pull requests; no secrets, actual tenant configurations, PINs or tokens belong in issues or source control.

## License and attribution

GUI and helper source: [MIT](LICENSE). YubiEnroll is installed separately and remains subject to Yubico's own terms. See [third-party notices](THIRD-PARTY-NOTICES.md). Yubico and YubiKey are trademarks of their respective owner. This community project is not affiliated with or endorsed by Yubico.

## Yubico workflow reference

The interface follows Yubico's [Entra provider setup](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#adding-microsoft-entra-id), [profile creation](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#creating-enrollment-profiles) and [profile setting descriptions](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#profile-settings). Minimum PIN length, Always UV and forced PIN change are validated using the selected key’s reported capabilities, not a universal firmware threshold. First-use PIN-change prompting also depends on the end user's platform. Omitted reset is treated as potentially destructive rather than assuming a safe default.


## Earlier previews (historical)

### Preview 0.4.1 polish
Overview focuses on the connected key; Key Management shares its selection and refreshes after USB changes. Settings contains Help & About and the activity history. Registered keys open beside the selected user in their own dialog. The embedded terminal is now the default; successful login returns automatically. Native key-operation confirmations remain and enrollment device targeting is still performed by YubiEnroll. The app icon is an original community-project asset, not a Yubico mark.


### Preview 0.4.2 polish

Settings now hosts Activity directly, with no Done button or persistent totals. Typography is retained when pages are hosted, navigation uses neutral colors, and prerequisites separates component status from project help. Overview reuses its inspected state until Refresh, a device change or a completed key operation. Blocked PIN guidance links to Key Management without performing a reset.

Prepare Key suggests an editable model/serial registration name and leaves reset warnings in the final review. Registered keys shows IDs directly without a raw tool-output section. The main window starts taller, and the original charcoal/green icon is explicitly applied to native window/taskbar identity. The terminal reads and decodes pipe output immediately and requests unbuffered Python output; CI checks live prompts and the real native help renderer. Real authentication and physical-key acceptance are still required.

### Passkeys on a YubiKey (preview 0.8.0)

Choose a physical key by model, serial and connection (USB or named NFC reader), select **View passkeys** and enter its existing FIDO PIN. No tenant, account search or Graph connection is required. Each discoverable credential shows its service (RP ID), available account names and full credential ID. Non-discoverable/U2F credentials cannot be listed. Registration and last-use dates are not supplied by the device.

**Remove from YubiKey** requires a separate review of the target and credential and a fresh PIN entry. Only that credential is removed; the PIN, other credentials and registrations at services such as Entra remain. The app checks the key and exact credential over the same connection, performs one deletion and verifies the remaining IDs. If verification fails, unlock the list again; there is no automatic retry. Device changes or leaving the page clear the list. The local PIN is passed only through a private stdin stream and is not saved or logged. A physical USB/NFC acceptance test is still required.

### Manage user passkeys (preview 0.7.3)

Choose the tenant, connect using your delegated account, search for the user and select **View passkeys**. No target key or profile is required. The list shows registrations whose Entra model identifies a supported Yubico hardware family, regardless of the user-editable display name. Software passkeys, other vendors and unrecognized models stay hidden.

Connect opens the system browser for the selected tenant’s public-client app registration. User search, the registration list, refresh and confirmed removal use that same delegated Microsoft Graph v1.0 session. The hardware tools are not required for this flow. Leaving management or changing tenants clears its in-memory session. If the tenant requires sign-in again, the app returns to Connect; it never automatically repeats a removal. Enrollment continues to use the YubiEnroll CLI.

Cards show **Registered** and **Last used** in local time from the same response as the registrations. **Unknown** means Microsoft Graph did not supply a usable timestamp. It does not mean the key has never been used.

Each listed registration has its own **Remove** action. Review the tenant, user and exact registration before confirming. This revokes that sign-in registration in Entra; it does not erase the credential or PIN on the physical key. The app checks the list again after removal. If it cannot verify the result, refresh before trying again. There is no bulk removal or automatic retry.
