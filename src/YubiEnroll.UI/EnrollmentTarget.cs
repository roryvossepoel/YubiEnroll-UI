using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private async Task<KeyStatus> CheckEnrollmentTargetAsync(ResetKey target, ProfileSetup profile, EnrollmentBatch? batch = null)
    {
        if (batch == null && (_choosingTarget || _selectedKey?.Key != target))
            throw new InvalidOperationException("Confirm the target key before starting enrollment.");
        var executable = KeyResetService.FindExecutable();
        if (executable.Length == 0)
            throw new InvalidOperationException("YubiKey Manager CLI is required to verify the connected keys.");
        var runner = new CliRunner(executable);
        var service = new KeyResetService(a => runner.CaptureAsync(a), _ => throw new InvalidOperationException(), () => NfcDiscovery.ReadAsync(runner));
        InvalidateDeviceInventory();
        var generation = _deviceGeneration;
        try
        {
            await service.CheckVersionAsync();
            var inventory = await service.DiscoverAsync();
            EnrollmentTargetGuard.Require(target, inventory);
            var pin = await service.ReadPinStateAsync(target);
            var capabilities = await FidoCapabilityProbe.ReadAsync(runner, target);
            if (!TryApplyInventory(inventory.Keys.Select(k => k == target ? new KeyStatus(k, pin, capabilities) : new KeyStatus(k, FidoPinState.Unknown)).ToArray(), inventory.Unavailable, generation, inventory.ReaderStates))
                throw new InvalidOperationException("Connected keys changed during verification. Wait for automatic refresh and review the target again.");
            if (batch?.StillConnected(inventory.Keys) is { } previous)
                throw new InvalidOperationException($"Remove previously attempted key {previous} before issuing another YubiKey.");
            if (batch != null && (pin != FidoPinState.NotSet || profile.Reset != false))
                throw new InvalidOperationException("Batch issuance requires a YubiKey without a FIDO PIN and a profile with reset turned off.");
            var compatibility = _addPasskey ? PasskeyAddition.Evaluate(capabilities, pin) : ProfileCompatibility.Evaluate(profile, capabilities, pin);
            if (_addPasskey && profile != PasskeyAddition.Settings(capabilities))
                throw new InvalidOperationException("The key settings changed. Review the key again before adding a passkey.");
            if (!compatibility.Compatible) throw new InvalidOperationException(compatibility.Message);
            return new KeyStatus(target, pin, capabilities);
        }
        catch
        {
            // Failed discovery cannot leave an old inventory enabled. A rejected fresh
            // inventory remains visible so the operator can correct the physical setup.
            if (_inventoryDirty) ApplyInventory(Array.Empty<KeyStatus>(), 0);
            throw;
        }
    }
}
