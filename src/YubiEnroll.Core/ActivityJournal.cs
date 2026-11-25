using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record ActivityPreferences(bool Enabled, string Directory, int RetentionDays);
public sealed record ActivityEvent(DateTimeOffset Time, string Level, string Message, string Detail);

public sealed class ActivityJournal
{
    private readonly string _settingsPath;
    private readonly Guid _session = Guid.NewGuid();
    private DateOnly? _lastPrune;
    private bool _canWrite = true;
    private static readonly Regex FilePattern = new(@"^activity-(\d{4}-\d{2}-\d{2})-[a-f0-9]{32}\.jsonl$", RegexOptions.CultureInvariant);
    public ActivityPreferences Preferences { get; private set; }
    public string DefaultDirectory { get; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public bool IsManaged { get; }

    public ActivityJournal(string? settingsPath = null, string? documents = null, ActivityPreferences? policy = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(Path.GetDirectoryName(RegistrationJournal.DefaultSettingsPath)!, "activity-settings.json");
        DefaultDirectory = RegistrationJournal.LogDirectory("ActivityLog", documents);
        IsManaged = policy != null;
        if (policy != null) { Validate(policy); Preferences = policy; return; }
        Preferences = new(true, DefaultDirectory, 30);
        try
        {
            var p = JsonSerializer.Deserialize<ActivityPreferences>(File.ReadAllText(_settingsPath)) ?? throw new InvalidDataException();
            Validate(p); Preferences = p;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex) || ex is InvalidDataException)
        { _canWrite = false; Error = "Could not read activity settings. Save a log location under App settings."; }
    }

    private static void Validate(ActivityPreferences p)
    {
        if (string.IsNullOrWhiteSpace(p.Directory) || !Path.IsPathFullyQualified(p.Directory) || p.RetentionDays is < 1 or > 3650)
            throw new ArgumentException("Choose an absolute folder and a retention period of 1–3650 days.");
    }

    public void Configure(bool enabled, string folder, int days)
    {
        if (IsManaged) throw new InvalidOperationException("Activity logging is managed by your organization.");
        var p = new ActivityPreferences(enabled, folder.Trim(), days); Validate(p);
        p = p with { Directory = Path.GetFullPath(p.Directory) };
        if (enabled)
        {
            Directory.CreateDirectory(p.Directory);
            using var probe = new FileStream(Path.Combine(p.Directory, ".write-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        RegistrationJournal.AtomicWrite(_settingsPath, JsonSerializer.Serialize(p));
        Preferences = p; _lastPrune = null; Error = null; _canWrite = true; Changed?.Invoke();
    }

    private static IEnumerable<string> Files(ActivityPreferences preferences, DateOnly today, bool prune)
    {
        foreach (var path in Directory.EnumerateFiles(preferences.Directory, "activity-*.jsonl"))
        {
            var m = FilePattern.Match(Path.GetFileName(path));
            if (!m.Success || !DateOnly.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day)) continue;
            if (day > today.AddDays(-preferences.RetentionDays)) yield return path;
            else if (prune) File.Delete(path);
        }
    }

    public IReadOnlyList<ActivityEvent> Read()
    {
        var result = ReadFiles(Preferences, _canWrite, Preferences.Enabled, default);
        if (result.Error != null) Error = result.Error;
        return result.Events;
    }

    public Task<(IReadOnlyList<ActivityEvent> Events, string? Error)> ReadAsync(CancellationToken ct)
    {
        var preferences = Preferences;
        var readable = _canWrite;
        return Task.Run(() => ReadFiles(preferences, readable, false, ct), ct);
    }

    private static (IReadOnlyList<ActivityEvent> Events, string? Error) ReadFiles(ActivityPreferences preferences, bool readable, bool prune, CancellationToken ct)
    {
        var entries = new List<ActivityEvent>();
        string? error = null;
        try
        {
            if (!readable || !Path.IsPathFullyQualified(preferences.Directory) || !Directory.Exists(preferences.Directory)) return (entries, null);
            foreach (var path in Files(preferences, DateOnly.FromDateTime(DateTime.UtcNow), prune))
            {
                using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                while (input.ReadLine() is { } line)
                {
                    ct.ThrowIfCancellationRequested();
                    try { if (JsonSerializer.Deserialize<ActivityEvent>(line) is { Message.Length: > 0, Level: not null, Detail: not null } e) entries.Add(e); }
                    catch (JsonException) { /* An interrupted last line must not hide earlier events. */ }
                }
            }
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex)) { error = "Some activity files could not be read. Check the log folder."; }
        return (entries.OrderByDescending(x => x.Time).ToArray(), error);
    }

    public bool Append(ActivityEvent entry)
    {
        if (!Preferences.Enabled || !_canWrite) return false;
        try
        {
            Validate(Preferences); Directory.CreateDirectory(Preferences.Directory);
            var today = DateOnly.FromDateTime(entry.Time.UtcDateTime);
            if (_lastPrune != today) { _ = Files(Preferences, today, true).Count(); _lastPrune = today; }
            var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var path = Path.Combine(Preferences.Directory, $"activity-{date}-{_session:N}.jsonl");
            // Only persist known diagnostic fields. Native transcripts, verbose exceptions, paths and PINs stay out.
            var detail = string.Join("\n", entry.Detail.Split('\n').Select(x => x.Trim()).Where(x =>
                Regex.IsMatch(x, @"^(?:HTTP status: [45]\d{2}|HRESULT: 0x[A-Fa-f0-9]{8}|(?:Native error|YubiEnroll exit code): -?\d+|Exception: [\w.]+)$")));
            File.AppendAllText(path, JsonSerializer.Serialize(entry with { Detail = detail }) + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex))
        { Error = "Activity could not be saved. Export this session before closing and check the folder in App settings."; return false; }
    }
}
