# YubiEnroll UI

An independent community Windows application for registering YubiKeys on behalf of Microsoft Entra ID users. Built in C# / WPF, based on Rory Vossepoel's PowerShell / WinForms enrollment workflow.

**Version 0.1.1 is a preview.** Automated tests cover configuration parsing, command arguments and process handling. Actual Entra sign-in, UAC, hardware enrollment require manual validation with a test user and test key. This is not an official Yubico product.

## What it does

- Reads your existing YubiEnroll tenants and enrollment profiles without rewriting the configuration itself.
- Activates the selected tenant using the official CLI and preserves your explicitly chosen profile.
- Searches users and uses their Entra object ID, avoiding confusion between email addresses and usernames.
- Opens a real console for YubiEnroll login and key enrollment, including PIN, touch and reinsertion prompts.
- Reports the CLI exit code and retrieves the registered credential list after a successful command.
- Keeps the UI responsive and prevents another operation or application shutdown while an operation is active.
- Requires explicit confirmation for enrollment. Overrides profile reset with `--no-reset` unless the reset checkbox is selected.
- Keeps a session-only activity log. PINs, authentication tokens and console transcripts are not captured or saved by the GUI.

## Download and start — no C# knowledge required

1. Open the repository's **Actions** tab and choose a successful **Build Windows preview** run.
2. Under **Artifacts**, download `YubiEnroll-UI-win-x64` for an Intel/AMD Windows PC. GitHub may require you to sign in.
3. Extract the downloaded artifact once. Keep `YubiEnroll.UI.exe` and `YubiEnroll.ConsoleHost.exe` together.
4. Install the **[.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**. Choose **Desktop Runtime**, not just the base .NET Runtime. Visual Studio and the SDK are not required.
5. Install and configure [Yubico YubiEnroll](https://www.yubico.com/products/yubienroll/) separately.
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

A missing configuration folder/file is now shown in the main window, without a blocking startup popup. Configuration loading and executable discovery run independently, so you can still browse to YubiEnroll or reload after correcting setup.

Open **Diagnostics** for the failed step, exception type, HRESULT/native Windows error, Windows account, architecture and attempted paths. This tab excludes configuration contents, arbitrary exception messages and enrollment console output. It does contain local account names and paths; redact those before posting publicly.

If the configuration is missing, start the app under the same Windows account used for your working PowerShell/YubiEnroll setup. Elevating with another account changes `%APPDATA%`. If Windows reports native error 740, use the administrator restart fallback. Other errors should be investigated using the diagnostic details rather than assuming elevation is the cause.

## Existing YubiEnroll configuration

The application looks for the same configuration locations used by the original script, in this order:

- `%APPDATA%\Yubico\yubienroll\yubienroll.toml`
- `%APPDATA%\yubienroll\yubienroll.toml`

Configuration must belong to the Windows account running the app. Configure provider app registration, permissions and profiles using the [official Entra setup guide](https://docs.yubico.com/software/yubikey/tools/yubienroll/yubienroll-entra.html). For example, start with `yubienroll providers add <name>` and `yubienroll profiles add <name>` as documented by Yubico. The GUI does not create Entra app registrations or grant tenant permissions.

The executable is detected under Program Files. Use **Browse** if you installed it elsewhere. No Yubico executable or configuration containing tenant-specific values is committed to this repository.

## Register a key

1. Select the tenant and profile. Check the tenant name/ID in the banner and the profile summary.
2. Use **Login**, complete the console/browser flow and press Enter in the console to return. **Status** displays the CLI authentication status.
3. Search for a user, inspect the result and select it. Enrollment uses the displayed object ID.
4. Optionally enter a key display name. Leave **Reset key** unchecked unless intentionally reinitializing a test/approved key. A reset destroys existing FIDO credentials on that key; it does not clean up registrations in every identity provider.
5. Click **Register YubiKey**, check the confirmation, then follow YubiEnroll's native console prompts.
6. Handle any generated PIN securely. Press Enter after the command finishes to return to the GUI.
7. Inspect the credential list and test sign-in. **Exit code 0 means YubiEnroll reported success; the GUI does not independently verify possession or successful authentication.**

When enrollment fails or its console is closed early, a credential may already have been created. Inspect **List credentials** and the key before retrying. There is no automatic retry, rollback, deletion or reset.

## Elevation

The GUI starts with normal user rights. Yubico documents that YubiEnroll requests elevation when needed. If that does not work in your environment, **Restart as administrator** provides an explicit fallback (equivalent to starting the app elevated).

If UAC uses a different Windows administrator account, the new process sees that account's `%APPDATA%`, configuration and authentication state. No configuration or tokens are copied between accounts. Recheck the tenant banner after restarting.

## Preview limitations

- Entra ID only; other provider types are not shown.
- Provider/profile creation and editing remain in the official CLI.
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

The script runs the core checks and publishes a framework-dependent x64 app with its console helper. The ZIP is written to `artifacts/<runtime>/`. The published WPF app is startup-tested on the x64 GitHub runner with missing folders/files, malformed configuration, an invalid executable, and a mock CLI. The published console helper is also started. These checks use no real YubiEnroll, tenant access or attached hardware.

For a quick logic test on any .NET 10 development machine:

```text
dotnet run --project tests/YubiEnroll.Tests -c Release
```

See [manual validation](docs/TESTING.md) before operational rollout. Changes are reviewed through pull requests; no secrets, actual tenant configurations, PINs or tokens belong in issues or source control.

## License and attribution

GUI and helper source: [MIT](LICENSE). YubiEnroll is installed separately and remains subject to Yubico's own terms. See [third-party notices](THIRD-PARTY-NOTICES.md). Yubico and YubiKey are trademarks of their respective owner. This community project is not affiliated with or endorsed by Yubico.
