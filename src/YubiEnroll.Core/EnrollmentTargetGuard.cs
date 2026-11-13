namespace YubiEnroll.Core;

// A preflight restriction for the existing CLI, not a device-binding mechanism.
// YubiEnroll 1.2.0 does not expose a USB serial selector. Keep one target
// connected throughout its enrollment flow; SDK integration is deferred.
public static class EnrollmentTargetGuard
{
    public static bool CanStart(ResetKey? target, IReadOnlyList<ResetKey> connected, int unavailable) =>
        target != null && unavailable == 0 && connected.Count == 1 && connected[0] == target;

    public static string Explain(ResetKey? target, IReadOnlyList<ResetKey> connected, int unavailable, bool signedIn = true)
    {
        if (target == null) return "Choose and confirm the target key first.";
        if (!connected.Contains(target)) return "The target key is unavailable. Reconnect it and refresh; no other key will be selected automatically.";
        if (unavailable > 0) return "Some connected devices could not be identified. Enrollment is blocked until the device inventory can be verified.";
        if (connected.Count > 1)
        {
            var others = string.Join(", ", connected.Where(k => k != target).Select(k => k.Serial));
            return signedIn
                ? $"Sign-in is complete. Disconnect the other keys (serial: {others}) and keep target {target.Serial} connected. Enrollment becomes available when only the target remains."
                : "Use your administrator key to sign in at step 2. After sign-in, disconnect it and any other keys; keep only the target connected for enrollment.";
        }
        return signedIn
            ? "Only the target is connected. Keep other keys disconnected until YubiEnroll finishes. Follow its touch and reinsertion prompts."
            : "Connect to the tenant at step 2. You may use another key to sign in; disconnect that key again before starting enrollment.";
    }

    public static void Require(ResetKey target, KeyInventory inventory)
    {
        if (!CanStart(target, inventory.Keys, inventory.Unavailable))
            throw new InvalidOperationException(Explain(target, inventory.Keys, inventory.Unavailable));
    }
}
