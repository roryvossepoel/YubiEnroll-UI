using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class SettingsWindow
{
    private static readonly HttpClient UpdateClient = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    internal static string CurrentVersion => typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false; ReleaseButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking GitHub releases…";
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var releases = new List<PublishedRelease>();
            // Include published preview releases for this community preview app.
            for (var page = 1; page <= 10; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ReleaseUpdates.Repository}/releases?per_page=100&page={page}");
                request.Headers.UserAgent.ParseAdd("PasskeyDesk/" + CurrentVersion);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                using var response = await UpdateClient.SendAsync(request, deadline.Token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(deadline.Token);
                releases.AddRange(ReleaseUpdates.Parse(json));
                using var count = JsonDocument.Parse(json);
                if (count.RootElement.GetArrayLength() < 100) break;
                if (page == 10) throw new InvalidOperationException("Too many releases to check completely.");
            }
            var latest = releases.OrderByDescending(r => r.Version).FirstOrDefault();
            var current = ReleaseVersion.Parse(CurrentVersion) ?? throw new InvalidOperationException();
            if (latest == null) UpdateStatus.Text = "No releases published yet.";
            else if (latest.Version.CompareTo(current) > 0)
            {
                UpdateStatus.Text = $"{latest.Tag} is available" + (latest.Preview ? " (preview)." : ".");
                ReleaseButton.Tag = latest.Url.AbsoluteUri; ReleaseButton.Visibility = Visibility.Visible;
            }
            else UpdateStatus.Text = "You are using the latest available version.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        { UpdateStatus.Text = "Could not check for updates. Check your connection and try again later."; }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }
}
