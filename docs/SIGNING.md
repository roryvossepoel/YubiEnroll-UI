# Internal signing

Product: **YubiEnroll UI**. Publisher: **Rory Vossepoel**. The self-signed certificate subject is `CN=Rory Vossepoel`. This is a personal community project, independent of Yubico or an employer.

Both `YubiEnroll.UI.exe` and `YubiEnroll.ConsoleHost.exe` must be signed after publishing and before packaging. Ordinary GitHub preview artifacts remain unsigned. CI tests signing on disposable copies with a temporary certificate; that certificate is never distributed.

## One-time setup on the signing PC

Run PowerShell on your Windows signing PC, from the repository root:

```powershell
$certificate = ./scripts/New-InternalSigningCertificate.ps1
$certificate.Thumbprint
```

The script creates (or reuses) a two-year RSA/SHA-256 code-signing certificate in your Windows account. Its private key is non-exportable and stays on that PC. The public certificate is exported to `artifacts/signing/Rory-Vossepoel-YubiEnroll-UI.cer`. Do not put private keys in the repository, chat, build artifacts or download bundles. Losing this Windows account/key means creating a new certificate and distributing its trust again.

The signing script deliberately does not install trust. After checking the certificate's identity and thumbprint, explicitly trust your public certificate in your signing account:

```powershell
Import-Certificate -FilePath ./artifacts/signing/Rory-Vossepoel-YubiEnroll-UI.cer -CertStoreLocation Cert:\CurrentUser\Root
```

## Build and sign

With the .NET 10 SDK installed:

```powershell
./build.ps1 -SigningCertificateThumbprint $certificate.Thumbprint
```

The build fails if either signature does not validate against the selected certificate. The ZIP contains the signed executables. Omitting the signing argument produces the usual unsigned development build.

Alternatively, download and extract a trusted preview artifact, then sign the extracted executables without installing the SDK:

```powershell
./scripts/Sign-App.ps1 -AppDirectory C:\Builds\YubiEnroll-UI -CertificateThumbprint $certificate.Thumbprint
Compress-Archive -Path C:\Builds\YubiEnroll-UI\* -DestinationPath C:\Builds\YubiEnroll-UI-signed.zip
```

Signing without a timestamp is supported for internal testing. Such signatures depend on the certificate remaining valid. To use a trusted Authenticode timestamp service, supply `-TimestampServer` to either script; when requested, a missing timestamp fails signing. A timestamp does not make a self-signed publisher publicly trusted.

## Internal devices

Share only the public `.cer`, preferably separately from the application and with its thumbprint verified through a trusted channel. For each intended account, explicitly import it into **Trusted Root Certification Authorities**. For all users, including an alternate administrator account used by UAC, deploy it into the **Local Computer** store through your normal IT process. Environments enforcing trusted publishers may also require the public certificate in **Trusted Publishers**. Never distribute the private key.

Trusting this certificate authorizes other code signed with the same key, not just this application. A self-signed signature detects modification; it provides no independent identity verification. SmartScreen or organizational application-control policies may still warn or block. Check signatures on the target device:

```powershell
Get-AuthenticodeSignature C:\Builds\YubiEnroll-UI\YubiEnroll.UI.exe, C:\Builds\YubiEnroll-UI\YubiEnroll.ConsoleHost.exe |
    Select-Object Path, Status, @{Name='Publisher'; Expression={$_.SignerCertificate.Subject}}
```

Both should report `Valid` with `CN=Rory Vossepoel`. Keep the same certificate across internal builds. Arrange renewal and redeployment before expiry; reconsider publicly trusted signing before a community release.
