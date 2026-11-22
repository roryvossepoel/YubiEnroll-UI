using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YubiEnroll.Core;

public enum RegistrationOutcome { Unknown, Succeeded, FailedOrInterrupted }

// Explicit fields only. Generated initial PINs require the per-attempt opt-in; no tokens or transcripts.
public sealed record RegistrationRecord(
    int SchemaVersion, Guid AttemptId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string TenantName, string TenantId, string UserId, string DisplayName, string UserPrincipalName,
    string Email, string KeyModel, string TargetSerial, string ProfileName, string RegistrationName,
    bool ResetBeforeEnrollment, bool ForcePinChangeOnFirstUse, RegistrationOutcome Outcome, int? NativeExitCode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GeneratedPin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? GeneratePinConfigured { get; init; }
    [JsonIgnore] public string PinModeLabel => (GeneratePinConfigured ?? (GeneratedPin != null ? true : (bool?)null)) switch
    {
        true => "Generated initial PIN",
        false => "Not generated",
        null => "Not recorded"
    };
    [JsonIgnore] public string ChangePinLabel => ForcePinChangeOnFirstUse ? "Yes" : "No";
    [JsonIgnore] public DateTimeOffset RecordedAt => CompletedAt ?? StartedAt;
    [JsonIgnore] public string ListTimeLabel => RecordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    [JsonIgnore] public string TimeLabel => RecordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    [JsonIgnore] public string UserLabel => string.IsNullOrEmpty(DisplayName) ? UserPrincipalName.Length > 0 ? UserPrincipalName : UserId : DisplayName;
    [JsonIgnore] public string OutcomeLabel => Outcome switch
    {
        RegistrationOutcome.Succeeded => "Succeeded (YubiEnroll)",
        RegistrationOutcome.FailedOrInterrupted => "Failed / interrupted",
        _ => "Unknown — check Entra"
    };
    public static RegistrationRecord Start(Provider tenant, UserCandidate user, ResetKey key, ProfileSetup profile, string registrationName) =>
        new(1, Guid.NewGuid(), DateTimeOffset.Now, null, tenant.Name, tenant.TenantId, user.Id,
            user.DisplayName, user.UserPrincipalName, user.Email, key.Model, key.Serial, profile.Name, registrationName.Trim(),
            profile.Reset ?? throw new InvalidOperationException("Choose an explicit reset setting."),
            profile.ForcePinChange ?? throw new InvalidOperationException("Choose an explicit first-use PIN setting."), RegistrationOutcome.Unknown, null) { GeneratePinConfigured = profile.RandomPin };

    public RegistrationRecord Complete(int? code) => this with
    {
        CompletedAt = DateTimeOffset.Now, NativeExitCode = code,
        Outcome = code switch { 0 => RegistrationOutcome.Succeeded, null or -1 or 125 => RegistrationOutcome.Unknown, _ => RegistrationOutcome.FailedOrInterrupted }
    };
}

public sealed record RegistrationLogPreferences(bool Enabled, string Directory, bool SaveGeneratedPin = false, int RetentionDays = RegistrationJournal.DefaultRetentionDays);
public sealed record RegistrationAttempt(string Directory, RegistrationRecord Record, bool SaveGeneratedPin = false);

public sealed class RegistrationJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<RegistrationOutcome>(allowIntegerValues: false) }
    };
    public const int DefaultRetentionDays = 30;
    public const int MaxRetentionDays = 3650;
    private DateOnly? _lastPrune;
    public string? RetentionError { get; private set; }
    private readonly string _settingsPath;
    private readonly Dictionary<Guid, RegistrationAttempt> _pending = new();
    public RegistrationLogPreferences Preferences { get; private set; }
    public string DefaultLogDirectory { get; }
    public string? SettingsError { get; private set; }
    public int PendingCount => _pending.Count;
    public event Action? Changed;
    public static string DefaultSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YubiEnroll.UI", "registration-log-settings.json");

    public bool IsManaged { get; }
    public static string LogDirectory(string leaf, string? documents = null)
    {
        documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
        return Path.IsPathFullyQualified(documents) ? Path.Combine(documents, "YubiKey Desk", leaf) : "";
    }

    public RegistrationJournal(string settingsPath, string? documentsFolder = null, RegistrationLogPreferences? policy = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        // Resolve the current account's known folder (including OneDrive or network redirection).
        // If it is unavailable, the operator can still browse to an explicit location.
        DefaultLogDirectory = LogDirectory("RegistrationLog", documentsFolder);
        IsManaged = policy != null;
        if (policy != null) { ValidateDirectory(policy.Directory); ValidateDays(policy.RetentionDays); Preferences = policy; PruneExpired(); return; }
        Preferences = new(false, DefaultLogDirectory);
        try
        {
            var preferences = JsonSerializer.Deserialize<RegistrationLogPreferences>(File.ReadAllText(_settingsPath), Json)
                ?? throw new InvalidDataException();
            ValidateDirectory(preferences.Directory); ValidateDays(preferences.RetentionDays);
            Preferences = preferences;
            PruneExpired();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (IsStorageError(ex))
        {
            SettingsError = "Registration log preferences could not be read. Open Settings → App settings and save your choice before enrollment.";
        }
    }

    public static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException or System.Security.SecurityException;
    private static void ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new ArgumentException("Choose an absolute log folder path.");
    }
    private static void ValidateDays(int days)
    {
        if (days is < 1 or > MaxRetentionDays) throw new ArgumentOutOfRangeException(nameof(days), "Choose a retention period of 1–3650 days.");
    }

    private static IEnumerable<string> RecordFiles(string directory) =>
        System.IO.Directory.EnumerateFiles(directory, "*.json", new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0
        }).Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) && new FileInfo(path).LinkTarget == null);

    // The file's timestamp may change during copying or sync. Use the validated record's UTC date.
    private void PruneExpired()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!Preferences.Enabled || _lastPrune == today || !System.IO.Directory.Exists(Preferences.Directory)) return;
        try
        {
            using var lease = new FileStream(Path.Combine(Preferences.Directory, ".journal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var cutoff = today.AddDays(-Preferences.RetentionDays);
            foreach (var path in RecordFiles(Preferences.Directory))
            {
                RegistrationRecord record;
                try { record = ReadRecord(path); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { continue; } // Never delete unverifiable files.
                if (!_pending.ContainsKey(record.AttemptId) && DateOnly.FromDateTime(record.RecordedAt.UtcDateTime) <= cutoff) File.Delete(path);
            }
            RetentionError = null;
            _lastPrune = today;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            RetentionError = "Registration log cleanup could not finish. Check folder access; cleanup will be retried.";
        }
    }

    public void Configure(bool enabled, string directory, bool saveGeneratedPin = false, int retentionDays = DefaultRetentionDays)
    {
        if (IsManaged) throw new InvalidOperationException("Registration logging is managed by your organization.");
        ValidateDirectory(directory); ValidateDays(retentionDays);
        directory = Path.GetFullPath(directory);
        if (PendingCount > 0 && !directory.Equals(Preferences.Directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save the pending results before changing the log folder.");
        if (enabled)
        {
            System.IO.Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) stream.Flush(true);
        }
        var preferences = new RegistrationLogPreferences(enabled, directory, saveGeneratedPin, retentionDays);
        AtomicWrite(_settingsPath, JsonSerializer.Serialize(preferences, Json));
        Preferences = preferences;
        SettingsError = null; RetentionError = null; _lastPrune = null;
        PruneExpired();
        Changed?.Invoke();
    }

    // Persist the unknown outcome BEFORE invoking native enrollment. A crash never turns into success.
    public RegistrationAttempt? Begin(RegistrationRecord record)
    {
        if (SettingsError != null) throw new InvalidOperationException(SettingsError);
        if (!Preferences.Enabled) return null;
        PruneExpired();
        var attempt = new RegistrationAttempt(Preferences.Directory, record with { GeneratedPin = null }, Preferences.SaveGeneratedPin);
        SaveRecord(attempt);
        Changed?.Invoke();
        return attempt;
    }

    // This method never repeats enrollment, even when saving the native result fails.
    public bool Complete(RegistrationAttempt? attempt, int? exitCode, string? generatedPin = null)
    {
        if (attempt == null) return true;
        var completed = attempt with { Record = attempt.Record.Complete(exitCode) with { GeneratedPin = attempt.SaveGeneratedPin && GeneratedPinSummary.IsValid(generatedPin) ? generatedPin : null } };
        _pending[completed.Record.AttemptId] = completed;
        try { SaveRecord(completed); _pending.Remove(completed.Record.AttemptId); }
        catch (Exception ex) when (IsStorageError(ex)) { }
        Changed?.Invoke();
        return !_pending.ContainsKey(completed.Record.AttemptId);
    }

    public void RetryPending()
    {
        foreach (var pending in _pending.Values.ToArray())
        {
            try { SaveRecord(pending); _pending.Remove(pending.Record.AttemptId); }
            catch (Exception ex) when (IsStorageError(ex)) { }
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<RegistrationRecord> Read()
    {
        if (SettingsError != null) throw new InvalidOperationException(SettingsError);
        PruneExpired();
        var records = new Dictionary<Guid, RegistrationRecord>();
        if (System.IO.Directory.Exists(Preferences.Directory))
            foreach (var path in RecordFiles(Preferences.Directory))
            {
                var record = ReadRecord(path);
                if (!records.TryAdd(record.AttemptId, record)) throw new InvalidDataException("Duplicate registration record.");
            }
        foreach (var pending in _pending.Values) records[pending.Record.AttemptId] = pending.Record;
        return records.Values.OrderByDescending(r => r.RecordedAt).ToArray();
    }

    private static RegistrationRecord ReadRecord(string path)
    {
        var record = JsonSerializer.Deserialize<RegistrationRecord>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Unreadable registration record.");
        Validate(record);
        if (!Path.GetFileNameWithoutExtension(path).Equals(record.AttemptId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Registration record identity does not match its filename.");
        return record;
    }
    private static void Validate(RegistrationRecord record)
    {
        if (record.GeneratedPin != null && !GeneratedPinSummary.IsValid(record.GeneratedPin) || record.SchemaVersion != 1 || record.AttemptId == Guid.Empty || record.StartedAt == default ||
            !Guid.TryParse(record.TenantId, out var tenant) || tenant == Guid.Empty || !Guid.TryParse(record.UserId, out var user) || user == Guid.Empty ||
            string.IsNullOrWhiteSpace(record.TargetSerial) || !record.TargetSerial.All(char.IsAsciiDigit) ||
            new[] { record.TenantName, record.DisplayName, record.UserPrincipalName, record.Email, record.KeyModel, record.ProfileName, record.RegistrationName }.Any(s => s == null) ||
            !Enum.IsDefined(record.Outcome) || record.Outcome == RegistrationOutcome.Succeeded && (record.NativeExitCode != 0 || record.CompletedAt == null) ||
            record.CompletedAt == null && (record.Outcome != RegistrationOutcome.Unknown || record.NativeExitCode != null))
            throw new InvalidDataException("Invalid registration record.");
    }
    private static void SaveRecord(RegistrationAttempt attempt)
    {
        Validate(attempt.Record);
        ValidateDirectory(attempt.Directory);
        System.IO.Directory.CreateDirectory(attempt.Directory);
        using var lease = new FileStream(Path.Combine(attempt.Directory, ".journal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(attempt.Directory, attempt.Record.AttemptId.ToString("N") + ".json");
        if (File.Exists(path))
        {
            var existing = ReadRecord(path);
            if (existing == attempt.Record) return; // Retry is idempotent.
            if (existing.CompletedAt != null || existing != (attempt.Record with { CompletedAt = null, NativeExitCode = null, Outcome = RegistrationOutcome.Unknown, GeneratedPin = null }))
                throw new InvalidOperationException("The registration record changed; it was not overwritten.");
        }
        AtomicWrite(path, JsonSerializer.Serialize(attempt.Record, Json));
    }
    internal static void AtomicWrite(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<RegistrationRecord> Filter(IEnumerable<RegistrationRecord> records, string? tenantId, DateTime? from, DateTime? through, bool successfulOnly = false) =>
        records.Where(r => (tenantId == null || r.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase)) &&
            (from == null || r.RecordedAt.LocalDateTime.Date >= from.Value.Date) &&
            (through == null || r.RecordedAt.LocalDateTime.Date <= through.Value.Date) &&
            (!successfulOnly || r.Outcome == RegistrationOutcome.Succeeded)).ToArray();

    public static string Csv(IEnumerable<RegistrationRecord> records, string separator = ";", bool includeHeader = true)
    {
        if (separator is not (";" or ",")) throw new ArgumentException("Use a comma or semicolon separator.");
        static string Cell(string value, bool text = false)
        {
            // Quoting alone does not protect Excel from formula injection. Serial numbers stay text.
            if (text || value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') ||
                value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        string Row(IEnumerable<string> cells) => string.Join(separator, cells.Select(c => Cell(c))) + "\r\n";
        var csv = new StringBuilder();
        if (includeHeader) csv.Append(Row(["Attempt ID", "Started at", "Completed at", "Tenant", "Tenant ID", "Display name", "UPN", "Email", "User ID", "Key model", "Target serial", "Profile", "Registration name", "Reset before enrollment (configured)", "PIN change on first use (configured)", "Result", "Native exit code", "Generated initial PIN"]));
        foreach (var r in records.DistinctBy(r => r.AttemptId))
        {
            string[] cells = [r.AttemptId.ToString(), r.StartedAt.ToString("o"), r.CompletedAt?.ToString("o") ?? "", r.TenantName, r.TenantId, r.DisplayName, r.UserPrincipalName, r.Email, r.UserId,
                r.KeyModel, r.TargetSerial, r.ProfileName, r.RegistrationName, r.ResetBeforeEnrollment ? "Yes" : "No", r.ForcePinChangeOnFirstUse ? "Yes" : "No", r.OutcomeLabel, r.NativeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "", r.GeneratedPin ?? ""];
            csv.Append(string.Join(separator, cells.Select((v, i) => Cell(v, i == 10 || i == 17 && v.Length > 0)))).Append("\r\n");
        }
        return csv.ToString();
    }
}
