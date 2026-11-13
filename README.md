> **Target-selection development branch:** this is a draft, not the 0.4.2 release. Multi-key enrollment is blocked until direct device targeting is implemented. See [the design and remaining work](docs/TARGET-KEY-DESIGN.md).

# YubiEnroll UI

An independent community Windows application for registering YubiKeys on behalf of Microsoft Entra ID users. Built in C# / WPF, based on Rory Vossepoel's PowerShell / WinForms enrollment workflow.

**Version 0.4.2 is a preview.** Automated tests cover configuration parsing, command arguments and process handling. Actual Entra sign-in, UAC, hardware enrollment and standalone FIDO reset require manual validation with a test user and test key. This is not an official Yubico product.

## What it does

- Reads existing YubiEnroll tenants and profiles, and adds Entra tenants through an Add tenant form. Existing configuration is preserved and backed up before additions.
- Activates the selected tenant using the official CLI and preserves your explicitly chosen profile.
- Searches users and uses their Entra object ID, avoiding confusion between email addresses and usernames.
- Starts on Overview with key inspection and task navigation. Prepare Key, Key Management and Settings share one window.
- Hosts YubiEnroll login and enrollment in an embedded terminal preview, with an external-console fallback under Settings → Prerequisites.
- Reports the CLI exit code and retrieves the registered credential list after a successful command.
- Keeps the UI responsive and prevents another operation or application shutdown while an operation is active.
- Uses the selected profile for reset and PIN settings. Shows a destructive-reset warning and asks for confirmation before enrollment.
- Offers **Key Management → Reset FIDO** to erase a USB key independently of enrollment, using the separately installed YubiKey Manager CLI.
- Keeps a session-only activity log. Interactive output is displayed transiently (including any generated PIN), and is not persisted or added to the activity/diagnostic log.

## Preview 0.4.1 changes

- Overview, Prepare Key and Key Management navigation, with Settings and Help & About.
- Read-only key/PIN inspection and automatic same-key refresh after FIDO reset.
- Rename/remove local tenants and profiles; linked tenant pills and shorter setup help.
- Guided import review, separate export card, prerequisites and technical details.
- Full-height activity/diagnostics view with empty states.
- Embedded monochrome terminal using Windows ConPTY by default; the external console remains an optional fallback.

The embedded terminal is enabled **for the current session only** under Settings → Prerequisites. It runs the same CLI commands and native confirmations, has a fixed 100-column screen with scrolling, does not support clipboard paste, and is not a general-purpose terminal emulator. Keep the app open until completion, record any generated PIN securely, then choose Return. On a launch/connection failure it never automatically reruns a command in the external console. Physical PIN/touch/reinsertion, UAC and browser login require manual acceptance with test hardware before relying on this mode operationally.

Only standalone **FIDO reset** is currently available. Additional application-specific resets (issue #7), final product name/logo (#9), and optional batch handover (#19) remain separate follow-up work. No repository rename or full factory-reset claim is made in this preview.

## Download and start — no C# knowledge required

1. Open the repository's **Actions** tab and choose a successful **Build Windows preview** run.
2. Under **Artifacts**, download `YubiEnroll-UI-win-x64` for an Intel/AMD Windows PC. GitHub may require you to sign in.
3. Extract the downloaded artifact once. Keep `YubiEnroll.UI.exe` and `YubiEnroll.ConsoleHost.exe` together.
4. Install the **[.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**. Choose **Desktop Runtime**, not just the base .NET Runtime. Visual Studio and the SDK are not required.
5. Install in the standard location and configure [Yubico YubiEnroll](https://www.yubico.com/products/yubienroll/) separately.
6. Start `YubiEnroll.UI.exe`. The app displays the prerequisites and checks for YubiEnroll at startup.

Only Windows x64 is built for now. Builds are unsigned previews and are not automatically published as GitHub Releases.

## Package contents

The download contains only five files:

- `YubiEnroll.UI.exe` — the Windows interface.
- `YubiEnroll.ConsoleHost.exe` — the interactive console for native enrollment prompts.
- `README.md`, `LICENSE`, and `THIRD-PARTY-NOTICES.md`.

Application assemblies are bundled into the two executables; the .NET runtime and YubiEnroll are installed separately. Extract this version to a new folder so old loose DLLs from 0.1.0 do not remain alongside it. An installer that checks these prerequisites may be added later; this preview is a ZIP download.

## Startup troubleshooting

If .NET 10 Desktop Runtime x64 is missing, the native .NET launcher reports the missing runtime before the C# window can open and offers a download link. The GUI cannot perform its own check until .NET is available. See [Microsoft startup troubleshooting](https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch).

A missing configuration folder/file is a normal first-run state: the window offers **Add tenant**, without a technical error. Configuration loading and executable discovery run independently, so Settings remains available to configure tenants and recheck prerequisites.

Open **Diagnostics** for the failed step, exception type, HRESULT/native Windows error, Windows account, architecture and attempted paths. This tab excludes configuration contents, arbitrary exception messages and enrollment console output. It does contain local account names and paths; redact those before posting publicly.

If the configuration is missing, start the app under the same Windows account used for your working PowerShell/YubiEnroll setup. Elevating with another account changes `%APPDATA%`. If Windows reports native error 740, use the administrator restart fallback. Other errors should be investigated using the diagnostic details rather than assuming elevation is the cause.

## Existing YubiEnroll configuration

The application reuses existing configuration in these locations (legacy first):

- `%APPDATA%\Yubico\yubienroll\yubienroll.toml`
- `%APPDATA%\yubienroll\yubienroll.toml`

A new configuration uses `%APPDATA%\Yubico\yubienroll\yubienroll.toml`, checked against the native YubiEnroll 1.2.0 CLI in CI. The current Yubico documentation omits the `Yubico` directory; our integration check found that 1.2.0 did not read that alternative path. The GUI does not redirect the CLI to ProgramData: a shared configuration path is not documented, and authentication remains per Windows account.

Configuration must belong to the Windows account running the app. Configure provider app registration, permissions and profiles using the [official Entra setup guide](https://docs.yubico.com/software/yubikey/tools/yubienroll/yubienroll-entra.html). For example, start with `yubienroll providers add <name>` and `yubienroll profiles add <name>` as documented by Yubico. The GUI does not create Entra app registrations or grant tenant permissions.

### Add your first tenant

Open **Settings → Tenants → Add tenant** and enter the tenant name, Directory (tenant) ID, Application (client) ID and Redirect URI. The redirect must use `http://localhost` and exactly match the public-client Entra app registration. Select an existing enrollment profile or create a new one. New profiles use a random PIN with the chosen length, no reset, no Always UV and no enterprise attestation; forcing a PIN change is an explicit checkbox. Minimum PIN length and forcing a PIN change require firmware 5.5+.

**Save tenant** validates the fields, creates the directory if necessary, and adds the provider/profile. It preserves original configuration text and makes a timestamped `.bak` file before replacing an existing file. Duplicate names and configuration changes since opening the form are rejected. Cancel makes no changes. Close other YubiEnroll sessions while saving; their external writes cannot be locked by this GUI. Close Settings, select the tenant and use **Connect** to activate it and authenticate.

This form targets Microsoft Entra public cloud. For national clouds, use YubiEnroll’s native provider setup to configure the appropriate endpoints.

The executable is detected in the standard Yubico installation folder under Program Files. **Settings → Prerequisites** shows the executable, version and configuration path, with **Recheck** and the administrator restart fallback. No Yubico executable or configuration containing tenant-specific values is committed to this repository.

### Manage multiple profiles

Open **Settings → Profiles** to create and edit named profiles. All seven settings are available: reset, minimum PIN length, Always UV, enterprise attestation, forced PIN change, random PIN and random PIN length. Minimum and generated PIN lengths are separate fields. Empty numeric fields and indeterminate checkboxes preserve CLI defaults.

Each tenant can reference a profile; multiple tenants can share the same profile. The editor lists the tenants that use it, since saving changes affects all of them. Choose an existing profile when adding a tenant. Selecting another profile in the main window overrides it for that enrollment without changing the tenant's default. Renaming a profile updates all linked tenant references in the same backed-up save. Remove a profile only after reassigning its linked tenants.

Profile saves preserve configuration values, including provider endpoints and other profiles. TOML formatting and comments may change; the exact original file is backed up first. Stale saves and duplicate names are rejected. **Reset now follows the selected profile.** A profile with `reset = true` displays a warning and requires confirmation before enrollment. `false` sends `--no-reset`; an omitted setting leaves the native default/prompt intact. The old duplicate reset checkbox has been removed.

## Prepare a key

1. Select a tenant and click **Connect**. The app activates that provider and opens native sign-in. Complete the browser/console flow and press Enter to return.
2. Search for a user and select the correct result. The Entra object ID remains visible beneath the account details.
3. Choose an enrollment profile and optionally give the key a display name. Expand **Profile details** if needed. Review the reset notice.
4. Click **Prepare YubiKey**, confirm the tenant/user/profile and any destructive reset, then follow the native PIN, touch and reinsertion prompts.
5. Store any generated PIN securely. After the console finishes, inspect **View registered keys** and test sign-in.

Search is enabled after a successful login command, and preparation after selecting a user. Switching tenants or reloading configuration invalidates the current selection and requires Connect again. **A successful CLI exit is not an independent authentication or hardware validation.**

Technical output is under the collapsed **Activity and diagnostics** section. It opens for diagnostics or registered-key results. User-search output uses a monospace font to preserve columns.

### Erase a key without enrollment

Open **Key Management → Reset FIDO**. This optional feature requires **[YubiKey Manager CLI](https://developers.yubico.com/yubikey-manager/) 5.9.2 or newer 5.x** installed in its standard `Program Files\Yubico\YubiKey Manager CLI` folder. The app also checks the older `YubiKey Manager` folder. Version 5.9.2 is checked against the official signed installer in CI. This CLI is separate from YubiEnroll and is not bundled; missing it does not block enrollment.

Run the GUI as administrator, connect the key via USB, Refresh, and select its model/serial number. Each entry shows the FIDO PIN status read from that key (set, not set, blocked, disabled/unsupported or unknown). No tenant connection or user selection is required. After the GUI confirmation, follow ykman's native confirmation, unplug/reinsert and touch prompts in the console. Press Enter when it finishes. The GUI reloads the connected keys and their PIN status, restoring the same selected key if it is still available. After exit 0 it reports whether the PIN is now unset; an unreadable status stays unknown, and a PIN still present requires inspection. This checks PIN state, not every passkey or cloud registration. A different key is never selected automatically. The GUI never passes `--force`, automatically retries, or substitutes a broader factory-reset command.

**This permanently resets all FIDO2/U2F credentials and the FIDO PIN on the selected key, for all accounts. Entra and other service registrations remain and must be removed separately.** PIV, OATH, OpenPGP and OTP are outside this reset. Native restrictions and model-specific warnings still apply (including YubiKey 4 FIPS attestation warnings); if ykman refuses the reset, the GUI does not bypass it. Read [Yubico's FIDO reset documentation](https://docs.yubico.com/software/yubikey/tools/ykman/FIDO_Commands.html).

This preview only selects keys with FIDO enabled and a readable serial number. Serial-less keys and external NFC readers are not supported. Device identity is checked again after confirmation, and the command always targets the selected serial. A successful message means **ykman reported success**; physical-key verification remains necessary. Closing or cancelling the console does not prove that nothing changed. Inspect the key before retrying.

### Entra logout

Closing the GUI is not a logout operation. Yubico currently documents the native `logout` command for Okta only, so this Entra interface does not offer a misleading logout button. Browser sessions and any cached authentication remain managed by YubiEnroll/Microsoft. Use **Connect** when switching tenants. See [Yubico command reference](https://docs.yubico.com/software/yubikey/tools/yubienroll/commands.html#yubienroll-logout).

### Import and export

**Settings → Import / export** transfers tenants and profiles together as TOML. Export includes supported Entra provider fields, cloud endpoints and the seven profile settings; other custom fields and authentication caches are excluded. The file contains tenant identifiers and should be shared deliberately.

Import validates types, profile references, duplicate names and policy values, and previews the tenant/profile names before any write. Confirming import **replaces the current configuration**, including other custom settings; it does not merge. An exact backup is made and stale previews are refused. Authentication caches are neither copied nor cleared. Reconnect afterward. Unknown import fields are rejected rather than silently discarded.

When enrollment fails or its console is closed early, a credential may already have been created. Inspect **List credentials** and the key before retrying. There is no automatic retry, rollback, deletion or reset.

## Elevation

The GUI starts with normal user rights. Yubico documents that YubiEnroll requests elevation when needed. If that does not work in your environment, **Settings → Prerequisites → Restart as administrator** provides an explicit fallback (equivalent to starting the app elevated).

If UAC uses a different Windows administrator account, the new process sees that account's `%APPDATA%`, configuration and authentication state. No configuration or tokens are copied between accounts. Recheck the tenant banner after restarting.

## Preview limitations

- Entra ID only; other provider types are not shown.
- Multiple tenants and profiles can be created and edited under Settings. Names can be changed, including spaces. Profile renaming updates linked tenants; tenant renaming updates active_provider. Tenants can be removed locally; a profile must be unassigned from all tenants before removal. Tenant edits preserve custom cloud endpoints; configure those through native setup or import.
- Search parsing accepts GUID-first table rows and `ID:` blocks. It intentionally rejects unknown formats. The **Details** tab shows the raw search output for diagnosis; real output from the installed YubiEnroll version still needs validation.
- No automated bulk enrollment, cloud credential deletion or automatic PIN delivery. A temporary batch register with protected PIN handover is tracked separately in issue #19 and is not implemented in this preview. Overview and Key Management inspect connected serial-addressable FIDO keys.
- One operation at a time within each GUI window. **Do not use a second GUI instance or another YubiEnroll session concurrently:** the CLI has a shared active provider. Configuration checks detect changes before commands but cannot lock out external tools.
- No persistent audit file or telemetry. Details/activity can contain user and tenant identifiers; redact them before sharing screenshots or bug reports. The official CLI's own logging settings are outside this GUI's control.
- Closing the console prematurely is treated as unsuccessful/unknown; wait for the CLI result and press Enter instead.

## Build from source

On Windows, install the .NET 10 SDK, then run:

```powershell
./build.ps1 -Runtime win-x64
```

The script runs the core checks and publishes a framework-dependent x64 app with its console helper. The ZIP is written to `artifacts/<runtime>/`. The published WPF app is startup-tested on the x64 GitHub runner with first-run setup, form validation/save/cancel, malformed configuration, an invalid executable, and a mock CLI. The published console helper is also started. A separate Windows CI check downloads the signed official YubiEnroll 1.2.0 installer, verifies its publisher, and checks that the CLI reads a synthetic provider/profile generated by the GUI writer. A separate check verifies the signed ykman 5.9.2 installer, expected installation folder, version parser, empty device discovery and serial-targeted reset help. No login, tenant access, reset command or attached hardware is used; neither Yubico CLI is redistributed with the app.

For a quick logic test on any .NET 10 development machine:

```text
dotnet run --project tests/YubiEnroll.Tests -c Release
```

See [manual validation](docs/TESTING.md) before operational rollout. Changes are reviewed through pull requests; no secrets, actual tenant configurations, PINs or tokens belong in issues or source control.

## License and attribution

GUI and helper source: [MIT](LICENSE). YubiEnroll is installed separately and remains subject to Yubico's own terms. See [third-party notices](THIRD-PARTY-NOTICES.md). Yubico and YubiKey are trademarks of their respective owner. This community project is not affiliated with or endorsed by Yubico.

## Yubico workflow reference

The interface follows Yubico's [Entra provider setup](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#adding-microsoft-entra-id), [profile creation](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#creating-enrollment-profiles) and [profile setting descriptions](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#profile-settings). Minimum PIN length, Always UV and forced PIN change require firmware 5.5+. First-use PIN-change prompting also depends on the end user's platform. Omitted reset is treated as potentially destructive rather than assuming a safe default.


### Preview 0.4.1 polish
Overview focuses on the connected key; Key Management shares its selection and refreshes after USB changes. Settings contains Help & About and one session Activity log. Registered keys open beside the selected user in their own dialog. The embedded terminal is now the default; successful login returns automatically. Native confirmations remain and enrollment device targeting is still performed by YubiEnroll. The app icon is an original community-project asset, not a Yubico mark.


### Preview 0.4.2 polish

Settings now hosts Activity directly, with no Done button or persistent totals. Typography is retained when pages are hosted, navigation uses neutral colors, and prerequisites separates component status from project help. Overview reuses its inspected state until Refresh, a device change or a completed key operation. Blocked PIN guidance links to Key Management without performing a reset.

Prepare Key suggests an editable model/serial registration name and leaves reset warnings in the final review. Registered keys shows IDs directly without a raw tool-output section. The main window starts taller, and the original charcoal/green icon is explicitly applied to native window/taskbar identity. The terminal reads and decodes pipe output immediately and requests unbuffered Python output; CI checks live prompts and the real native help renderer. Real authentication and physical-key acceptance are still required.
