# Manual acceptance checks for preview 0.1.1

Use a test tenant/user and a spare key. Record the Windows architecture, YubiEnroll version and key firmware. These checks have not yet been executed against physical hardware for this C# port.

| Check | Expected result |
|---|---|
| Missing config | Window stays usable; Diagnostics identifies the missing folder/file and Windows account. |
| Invalid executable | Diagnostics reports the native Windows error; config still loads independently. |
| Start normally | Existing provider/profile configuration loads; installed CLI version is displayed. |
| Alternate install folder | Browse selects the installed executable; subsequent commands use that path. |
| Choose non-default profile | Search and enrollment keep that profile; native CLI shows the same settings. |
| Switch tenant | Old user selection clears; banner and subsequent lookup use the new tenant. |
| Change config externally | Next command refuses stale provider/profile settings and asks for reload. |
| Login | Browser and interactive console work; GUI resumes after Enter. |
| User with different mail and UPN | Selected Entra object ID matches the intended account; no email guessing. |
| Search format not recognized | Raw output available in Details; enrollment remains disabled. Report a redacted sample for parser support. |
| Enroll without reset | Confirmation and CLI use no-reset even if profile reset is true. Existing credentials are not deliberately erased. |
| Reset selected | Explicit warning appears; only proceed using a disposable test key. |
| Long device prompts | GUI repaints; tenant/profile/buttons cannot be changed while active. |
| Success | Console exit 0 recorded; credential list displayed; separately confirm actual sign-in. |
| Failure or early console close | No automatic retry or success claim. Check both cloud and key state before retrying. |
| Normal UAC | Native elevation prompt works without elevating the GUI. |
| Elevated fallback | App relaunches and reloads that Windows account's configuration. Check same-account and different-account scenarios. |
| PIN handling | PIN appears only in native console; no GUI activity entry/file/clipboard copy contains it. |
| ARM64 | Repeat core flow on ARM64 with the supported/working CLI installation; ARM64 build alone is insufficient. |

Automated checks use synthetic data only. They test TOML names/comments, stale configuration, strict object-ID extraction, safe reset/profile arguments and concurrent output handling. The bundled WPF startup test covers missing folders/files, malformed TOML, an invalid executable and successful initialization with a mock CLI. The bundled console helper is also started. Authentication and hardware enrollment remain manual checks.
