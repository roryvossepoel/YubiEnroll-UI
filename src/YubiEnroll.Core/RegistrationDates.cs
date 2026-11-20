using System.Globalization;

namespace YubiEnroll.Core;

public sealed record RegistrationDates(DateTimeOffset? Registered, DateTimeOffset? LastUsed);

// Keep optional display metadata out of the identity used to confirm and delete a registration.
public sealed record RegistrationCard(RegisteredCredential Credential, RegistrationDates? Dates)
{
    public string Name => Credential.Name;
    public string Model => Credential.Model;
    public string Id => Credential.Id;
    private static string LocalDate(DateTimeOffset? date) =>
        date?.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "Unknown";
    public string DateLine => $"Registered: {LocalDate(Dates?.Registered)}   ·   Last used: {LocalDate(Dates?.LastUsed)}";
}
