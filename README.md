# YubiEnroll UI

An independent community Windows application for registering YubiKeys on behalf of Microsoft Entra ID users. Built in C# / WPF, based on Rory Vossepoel's PowerShell / WinForms enrollment workflow.

**Version 0.2.0 is a preview.** Automated tests cover configuration parsing, command arguments and process handling. Actual Entra sign-in, UAC, hardware enrollment require manual validation with a test user and test key. This is not an official Yubico product.

## What it does

- Reads existing YubiEnroll tenants and profiles, and adds Entra tenants through an Add tenant form. Existing configuration is preserved and backed up before additions.
- Activates the selected tenant using the official CLI and preserves your explicitly chosen profile.
- Searches users and uses their Entra object ID, avoiding confusion between email addresses and usernames.
- Opens a real console for YubiEnroll login and key enrollment, including PIN, touch and reinsertion prompts.
- Reports the CLI exit code and retrieves the registered credential list after a successful command.
- Keeps the UI responsive and prevents another operation or application shutdown while an operation is active.
- Uses the selected profile for reset and PIN settings. Shows a destructive-reset warning and asks for confirmation before enrollment.
- Keeps a session-only activity log. PINs, authentication tokens and console transcripts are not captured or saved by the GUI.

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

The executable is detected in the standard Yubico installation folder under Program Files. **Settings → System** shows the executable, version and configuration path, with **Recheck** and the administrator restart fallback. No Yubico executable or configuration containing tenant-specific values is committed to this repository.

### Manage multiple profiles

Open **Settings → Profiles** to create and edit named profiles. All seven settings are available: reset, minimum PIN length, Always UV, enterprise attestation, forced PIN change, random PIN and random PIN length. Minimum and generated PIN lengths are separate fields. Empty numeric fields and indeterminate checkboxes preserve CLI defaults.

Each tenant can reference a profile; multiple tenants can share the same profile. The editor lists the tenants that use it, since saving changes affects all of them. Choose an existing profile when adding a tenant. Selecting another profile in the main window overrides it for that enrollment without changing the tenant's default. Existing profile names cannot be changed in the editor, preserving tenant references.

Profile saves preserve configuration values, including provider endpoints and other profiles. TOML formatting and comments may change; the exact original file is backed up first. Stale saves and duplicate names are rejected. **Reset now follows the selected profile.** A profile with `reset = true` displays a warning and requires confirmation before enrollment. `false` sends `--no-reset`; an omitted setting leaves the native default/prompt intact. The old duplicate reset checkbox has been removed.

## Prepare a key

1. Select a tenant and click **Connect**. The app activates that provider and opens native sign-in. Complete the browser/console flow and press Enter to return.
2. Search for a user and select the correct result. The Entra object ID remains visible beneath the account details.
3. Choose an enrollment profile and optionally give the key a display name. Expand **Profile details** if needed. Review the reset notice.
4. Click **Prepare YubiKey**, confirm the tenant/user/profile and any destructive reset, then follow the native PIN, touch and reinsertion prompts.
5. Store any generated PIN securely. After the console finishes, inspect **View registered keys** and test sign-in.

Search is enabled after a successful login command, and preparation after selecting a user. Switching tenants or reloading configuration invalidates the current selection and requires Connect again. **A successful CLI exit is not an independent authentication or hardware validation.**

Technical output is under the collapsed **Activity and diagnostics** section. It opens for diagnostics or registered-key results. User-search output uses a monospace font to preserve columns.

### Entra logout

Closing the GUI is not a logout operation. Yubico currently documents the native `logout` command for Okta only, so this Entra interface does not offer a misleading logout button. Browser sessions and any cached authentication remain managed by YubiEnroll/Microsoft. Use **Connect** when switching tenants. See [Yubico command reference](https://docs.yubico.com/software/yubikey/tools/yubienroll/commands.html#yubienroll-logout).

### Import and export

**Settings → Import / export** transfers tenants and profiles together as TOML. Export includes supported Entra provider fields, cloud endpoints and the seven profile settings; other custom fields and authentication caches are excluded. The file contains tenant identifiers and should be shared deliberately.

Import validates types, profile references, duplicate names and policy values, and previews the tenant/profile names before any write. Confirming import **replaces the current configuration**, including other custom settings; it does not merge. An exact backup is made and stale previews are refused. Authentication caches are neither copied nor cleared. Reconnect afterward. Unknown import fields are rejected rather than silently discarded.

When enrollment fails or its console is closed early, a credential may already have been created. Inspect **List credentials** and the key before retrying. There is no automatic retry, rollback, deletion or reset.

## Elevation

The GUI starts with normal user rights. Yubico documents that YubiEnroll requests elevation when needed. If that does not work in your environment, **Settings → System → Restart as administrator** provides an explicit fallback (equivalent to starting the app elevated).

If UAC uses a different Windows administrator account, the new process sees that account's `%APPDATA%`, configuration and authentication state. No configuration or tokens are copied between accounts. Recheck the tenant banner after restarting.

## Preview limitations

- Entra ID only; other provider types are not shown.
- Multiple tenants and profiles can be created and edited under Settings. Existing names cannot be changed; deletion remains in the official CLI. Tenant edits preserve custom cloud endpoints; configure those through native setup or import.
- Search parsing accepts GUID-first table rows and `ID:` blocks. It intentionally rejects unknown formats. The **Details** tab shows the raw search output for diagnosis; real output from the installed YubiEnroll version still needs validation.
- No bulk enrollment, credential deletion, automatic key detection or automatic PIN delivery.
- One operation at a time within each GUI window. **Do not use a second GUI instance or another YubiEnroll session concurrently:** the CLI has a shared active provider. Configuration checks detect changes before commands but cannot lock out external tools.
- No persistent audit file or telemetry. Details/activity can contain user and tenant identifiers; redact them before sharing screenshots or bug reports. The official CLI's own logging settings are outside this GUI's control.
- Closing the console prematurely is treated as unsuccessful/unknown; wait for the CLI result and press Enter instead.

## Build from source

On Windows, install the .NET 10 SDK, then run:

```powershell
./build.ps1 -Runtime win-x64
```

The script runs the core checks and publishes a framework-dependent x64 app with its console helper. The ZIP is written to `artifacts/<runtime>/`. The published WPF app is startup-tested on the x64 GitHub runner with first-run setup, form validation/save/cancel, malformed configuration, an invalid executable, and a mock CLI. The published console helper is also started. A separate Windows CI check downloads the signed official YubiEnroll 1.2.0 installer, verifies its publisher, and checks that the CLI reads a synthetic provider/profile generated by the GUI writer. No login, tenant access or attached hardware is used; YubiEnroll is not redistributed with the app.

For a quick logic test on any .NET 10 development machine:

```text
dotnet run --project tests/YubiEnroll.Tests -c Release
```

See [manual validation](docs/TESTING.md) before operational rollout. Changes are reviewed through pull requests; no secrets, actual tenant configurations, PINs or tokens belong in issues or source control.

## License and attribution

GUI and helper source: [MIT](LICENSE). YubiEnroll is installed separately and remains subject to Yubico's own terms. See [third-party notices](THIRD-PARTY-NOTICES.md). Yubico and YubiKey are trademarks of their respective owner. This community project is not affiliated with or endorsed by Yubico.

## Yubico workflow reference

The interface follows Yubico's [Entra provider setup](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#adding-microsoft-entra-id), [profile creation](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#creating-enrollment-profiles) and [profile setting descriptions](https://docs.yubico.com/software/yubikey/tools/yubienroll/use-cli.html#profile-settings). Minimum PIN length, Always UV and forced PIN change require firmware 5.5+. First-use PIN-change prompting also depends on the end user's platform. Omitted reset is treated as potentially destructive rather than assuming a safe default.
