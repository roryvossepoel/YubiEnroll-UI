using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private async Task CheckEnrollmentTargetAsync(ResetKey target)
    {
        if (_choosingTarget || _selectedKey?.Key != target)
            throw new InvalidOperationException("Confirm the target key before starting enrollment.");
        var executable = KeyResetService.FindExecutable();
        if (executable.Length == 0)
            throw new InvalidOperationException("YubiKey Manager CLI is required to verify the connected keys.");
        var runner = new CliRunner(executable);
        var service = new KeyResetService(a => runner.CaptureAsync(a), _ => throw new InvalidOperationException());
        InvalidateDeviceInventory();
        try
        {
            await service.CheckVersionAsync();
            var inventory = await service.DiscoverAsync();
            ApplyInventory(inventory.Keys.Select(k => new KeyStatus(k, _knownKeys.FirstOrDefault(x => x.Key == k)?.Pin ?? FidoPinState.Unknown)).ToArray(), inventory.Unavailable);
            EnrollmentTargetGuard.Require(target, inventory);
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
