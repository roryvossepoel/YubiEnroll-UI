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
        await service.CheckVersionAsync();
        var inventory = await service.DiscoverAsync();
        try { EnrollmentTargetGuard.Require(target, inventory); }
        catch
        {
            // Invalidate stale UI state; never substitute the administrator's remaining key.
            ApplyInventory(inventory.Keys.Select(k => new KeyStatus(k, FidoPinState.Unknown)).ToArray(), inventory.Unavailable);
            throw;
        }
    }
}
