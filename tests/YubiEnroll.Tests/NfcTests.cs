using YubiEnroll.Core;

internal static class NfcTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        const string reader = "HID Global OMNIKEY 5022 Smart Card Reader 0";
        var usb = new ResetKey("12345678", "YubiKey 5C NFC (5.7.1)");
        var nfc = usb with { Reader = reader };
        var admin = new ResetKey("87654321", "YubiKey 5C NFC (5.4.3)");
        check(nfc != usb && nfc.ConnectionLabel.Contains(reader), "NFC identity includes reader and never substitutes the USB endpoint");
        check(EnrollmentTargetGuard.CanStart(nfc, [nfc, admin], 1), "Explicit NFC reader does not require unplugging USB admin keys");
        check(EnrollmentTargetGuard.CanStart(usb, [usb, nfc], 0), "USB enrollment ignores NFC endpoints");
        check(!EnrollmentTargetGuard.CanStart(nfc, [usb], 0), "NFC removal never falls back to USB with the same serial");
        check(!EnrollmentTargetGuard.CanStart(nfc, [nfc with { Reader = reader + " 2" }], 0), "Moving target to a different reader needs new selection");
        check(!EnrollmentTargetGuard.CanStart(nfc, [nfc, admin with { Reader = reader }], 0), "Ambiguous reader inventory cannot start enrollment");
        var command = EnrollmentCommand.Build("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "profile", "Test key", false, reader);
        check(command.TakeLast(2).SequenceEqual(new[] { "--reader", reader }) && !command.Contains("--device") && !command.Contains("--force"), "Enrollment passes the full reader as one argument with native confirmation");
        check(KeyResetService.Selector(nfc).SequenceEqual(new[] { "--reader", reader }), "NFC and USB selectors are never combined");
        var json = """
        {"schema":1,"keys":[{"serial":"12345678","model":"YubiKey 5C NFC (5.7.1)","reader":"HID Global OMNIKEY 5022 Smart Card Reader 0"}],"unavailable":0,"readers":1}
        """;
        check(NfcDiscovery.Parse(json).Keys.Single() == nfc, "NFC inventory preserves model, serial and reader");
        foreach (var invalid in new[] { json.Replace("12345678", "0"), json.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"), json.Replace(reader, ""), json.Replace("\"readers\":1", "\"readers\":0") })
        {
            var rejected = false;
            try { NfcDiscovery.Parse(invalid); } catch { rejected = true; }
            check(rejected, "Malformed NFC identity is rejected");
        }
        var capsJson = """
        {"schema":1,"forcePinChange":false,"serial":"12345678","firmware":"5.7.1","reader":"HID Global OMNIKEY 5022 Smart Card Reader 0","options":{"clientPin":true,"rk":true,"authnrCfg":true,"setMinPINLength":true},"minimumPinLength":4,"maximumPinLength":63,"resetBlocked":false,"resetOverUsb":true,"resetOverNfc":false,"alwaysUvLocked":false}
        """;
        var caps = FidoCapabilities.Parse(capsJson, nfc);
        check(!caps.ResetAllowed && caps.IsNfc && !ProfileCompatibility.Evaluate(ProfileCompatibility.Basic("Basic") with { Reset = true }, caps).Compatible, "NFC reset restriction blocks a resetting profile even when USB reset is allowed");
        check(ProfileCompatibility.Evaluate(ProfileCompatibility.Basic("Basic"), caps).Compatible, "NFC reset restriction does not block a non-reset profile");
        check((caps with { ResetOverNfc = true }).ResetAllowed && !(caps with { ResetOverNfc = true, ResetBlocked = true }).ResetAllowed, "Transport and reset-blocked capability both apply");
        var mismatch = false;
        try { FidoCapabilities.Parse(capsJson.Replace(reader, "Other reader"), nfc); } catch (InvalidDataException) { mismatch = true; }
        check(mismatch, "Capabilities from another reader are rejected");
        foreach (var swap in new[] { false, true })
        {
            var current = nfc;
            var launched = 0;
            var service = new KeyResetService(a => Task.FromResult(a[0] switch
            {
                "--version" => new CliResult(0, "YubiKey Manager (ykman) version: 5.9.2", ""),
                "list" => new CliResult(0, "", ""),
                "--reader" when a.SequenceEqual(new[] { "--reader", reader, "fido", "info" }) => new CliResult(0, "PIN: Set", ""),
                _ => throw new Exception("Unexpected command")
            }), a =>
            {
                check(a.SequenceEqual(new[] { "--reader", reader, "fido", "reset" }), "NFC reset retains native confirmation and reinsertion verification");
                launched++; return Task.FromResult(0);
            }, () => Task.FromResult(new KeyInventory([current], 0, 0, 1)));
            var blocked = false;
            try { await service.ResetAsync(nfc, _ => { if (swap) current = admin with { Reader = reader }; return true; }); }
            catch (InvalidOperationException) { blocked = true; }
            check(blocked == swap && launched == (swap ? 0 : 1), "NFC reset rechecks serial after confirmation and never retries");
        }
    }
}
