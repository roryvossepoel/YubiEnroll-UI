namespace YubiEnroll.Core;

// A preflight restriction for the existing CLI, not a device-binding mechanism.
// YubiEnroll 1.2.0 does not expose a USB serial selector. A native SDK backend is
// required before enrollment with multiple connected keys can be supported here.
public static class EnrollmentTargetGuard
{
    public static bool CanStart(ResetKey? target, IReadOnlyList<ResetKey> connected, int unavailable) =>
        target != null && unavailable == 0 && connected.Count == 1 && connected[0] == target;

    public static string Explain(ResetKey? target, IReadOnlyList<ResetKey> connected, int unavailable)
    {
        if (target == null) return "Choose and confirm the target key first.";
        if (!connected.Contains(target)) return "The target key is unavailable. Reconnect it and refresh; no other key will be selected automatically.";
        if (unavailable > 0) return "Some connected devices could not be identified. Enrollment is blocked until the device inventory can be verified.";
        if (connected.Count > 1) return "Enrollment is blocked: YubiEnroll cannot be restricted to this USB key while other keys are connected. Direct device targeting is not available in this preview.";
        return "YubiEnroll still selects the device in its own prompts. Keep only this target connected during enrollment. USB selection by serial number is not supported by the current enrollment tool.";
    }

    public static void Require(ResetKey target, KeyInventory inventory)
    {
        if (!CanStart(target, inventory.Keys, inventory.Unavailable))
            throw new InvalidOperationException(Explain(target, inventory.Keys, inventory.Unavailable));
    }
}
