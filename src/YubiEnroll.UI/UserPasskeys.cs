using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private int _manageStep;
    private sealed record RegistrationList(Provider Tenant, UserCandidate User, IReadOnlyList<RegisteredCredential> Rows);
    private RegistrationList? _managedList;
    private CancellationTokenSource? _dateRequest;
    private RegistrationDateSession? _dateSession;
    private Provider? _dateProvider;
    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowPage(ManagePage);
        ResetManagement();
    }
    private void ResetManagement()
    {
        if (ManageBack == null) return;
        _dateSession = null; _dateProvider = null;
        _manageStep = 0;
        ManageQuery.Clear(); ManageUsers.ItemsSource = null;
        ManageSearchHint.Text = "";
        ClearManagedList();
        RenderManagement();
    }
    private void ClearManagedList()
    {
        _managedList = null;
        _dateRequest?.Cancel(); _dateRequest = null;
        ManageDateNotice.Visibility = Visibility.Collapsed;
        ManageRows.ItemsSource = null;
        ManageFeedback.Text = "";
    }
    private void RenderManagement()
    {
        if (ManageBack == null || ManagePage.Visibility != Visibility.Visible) return;
        RenderProgress(ManageProgress, ["Tenant", "User", "Passkeys"], _manageStep);
        ManageTenantStep.Visibility = Visible(_manageStep == 0);
        ManageUserStep.Visibility = Visible(_manageStep == 1);
        ManageListStep.Visibility = Visible(_manageStep == 2);
        ManageContext.Text = _manageStep == 0 ? "View or remove a user’s YubiKey registrations in Microsoft Entra. No connected key is required." :
            _manageStep == 1 ? $"Tenant · {SelectedProvider.Name}" :
            $"{SelectedProvider.Name}  ·  {(ManageUsers.SelectedItem as UserCandidate)?.Label}";
        ManageTenantHint.Text = Tenants.Items.Count == 0 ? "Add a tenant in Settings → Tenants." :
            IsConnected ? $"Connected to {SelectedProvider.Name}" : "Sign in with an administrator account for this tenant.";
        ManageConnect.IsEnabled = _cliReady && Tenants.SelectedItem is Provider;
        ManageConnect.Content = IsConnected ? "Continue to user" : "Connect";
        ManageView.IsEnabled = IsConnected && ManageUsers.SelectedItem is UserCandidate;
        ManageBack.Content = _manageStep switch { 2 => "←  Choose another user", 1 => "←  Choose tenant", _ => "Back to Home" };
    }
    private async void ManageConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _manageStep != 0 || Tenants.SelectedItem is not Provider) return;
        if (!IsConnected) await LoginAsync();
        if (IsConnected && ManagePage.IsVisible)
        { _manageStep = 1; RenderManagement(); ManageQuery.Focus(); }
        else ManageTenantHint.Text = StatusText.Text;
    }
    private void ManageBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_manageStep == 0) { ShowPage(OverviewPage); return; }
        if (_manageStep == 1) { ResetManagement(); return; }
        ClearManagedList();
        ManageUsers.SelectedItem = null;
        _manageStep = 1; RenderManagement();
    }
    private void ManageQuery_Changed(object sender, TextChangedEventArgs e)
    {
        if (ManageBack == null) return;
        ManageUsers.ItemsSource = null; ManageSearchHint.Text = ""; ClearManagedList();
    }
    private void ManageUser_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ManageBack == null) return;
        ClearManagedList(); RenderManagement();
    }
    private async void ManageSearch_Click(object sender, RoutedEventArgs e) => await SearchManagedUsersAsync();
    private async void ManageQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchManagedUsersAsync(); }
    }
    private async Task SearchManagedUsersAsync()
    {
        if (_busy || !ManagePage.IsVisible || _manageStep != 1) return;
        ManageUsers.ItemsSource = null; ManageSearchHint.Text = "Searching…";
        var ok = await RunWithResultAsync(async () =>
        {
            var users = await FindUsersAsync(ManageQuery.Text.Trim());
            ManageUsers.ItemsSource = users;
            ManageSearchHint.Text = users.Count == 0 ? "No matching users found." : "Select a user to view their passkeys.";
        }, "Search tenant users", false);
        if (!ok) ManageSearchHint.Text = StatusText.Text;
    }
    private async void ManageView_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ManagePage.IsVisible || !IsConnected || ManageUsers.SelectedItem is not UserCandidate user) return;
        var provider = SelectedProvider;
        _manageStep = 2;
        ClearManagedList(); RenderManagement(); ManageFeedback.Text = "Loading registrations…";
        await ManageOperationAsync(async service =>
        {
            var rows = await service.ListAsync(Guid.Parse(user.Id));
            SetManagedList(provider, user, rows);
        }, provider, user, "Read passkey registrations");
        if (_managedList is { Rows.Count: > 0 } list && MatchesManagementContext(provider, user))
            await LoadRegistrationDatesAsync(list, interactive: true);
    }
    private void SetManagedList(Provider provider, UserCandidate user, IReadOnlyList<RegisteredCredential> rows)
    {
        if (!MatchesManagementContext(provider, user)) throw new InvalidOperationException("The selected tenant or user changed.");
        var hardware = rows.Where(row => row.IsYubicoHardware).ToArray();
        _managedList = new(provider, user, hardware);
        ManageRows.ItemsSource = hardware.Select(x => new RegistrationCard(x, null)).ToArray();
        ManageFeedback.Text = hardware.Length == 0 ? "No recognized YubiKey registrations found for this user." :
            $"{hardware.Length} YubiKey {(hardware.Length == 1 ? "registration" : "registrations")} in Entra.";
    }
    private bool MatchesManagementContext(Provider provider, UserCandidate user) =>
        ManagePage.IsVisible && _manageStep == 2 && IsConnected && SelectedProvider == provider && ManageUsers.SelectedItem is UserCandidate u && u.Id == user.Id;

    private bool ApplyRegistrationDates(RegistrationList list, IReadOnlyDictionary<string, RegistrationDates> dates)
    {
        if (!ReferenceEquals(list, _managedList) || !MatchesManagementContext(list.Tenant, list.User)) return false;
        ManageRows.ItemsSource = list.Rows.Select(row => new RegistrationCard(row, dates.GetValueOrDefault(row.Id))).ToArray();
        return true;
    }

    private async void DateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_dateRequest is { } pending) { pending.Cancel(); return; }
        if (!_busy && _managedList is { } list) await LoadRegistrationDatesAsync(list, interactive: true);
    }

    private async Task LoadRegistrationDatesAsync(RegistrationList list, bool interactive)
    {
        if (_dateRequest != null || !ReferenceEquals(_managedList, list) || !MatchesManagementContext(list.Tenant, list.User)) return;
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _dateRequest = request;
        bool Current() => ReferenceEquals(_dateRequest, request) && ReferenceEquals(_managedList, list)
            && MatchesManagementContext(list.Tenant, list.User);
        ManageDateNotice.Visibility = Visibility.Visible;
        ManageDateMessage.Text = "Loading registration dates…"; ManageDateAction.Content = "Cancel";
        try
        {
            var snapshot = File.ReadAllText(_configPath);
            ConfigReader.EnsureUnchanged(ConfigReader.Parse(snapshot), list.Tenant, null);
            if (_dateSession == null || _dateProvider != list.Tenant)
            {
                var tenant = TenantConfigWriter.Read(snapshot, list.Tenant.Name).Validate();
                _dateSession = new RegistrationDateSession(tenant); _dateProvider = list.Tenant;
            }
            var dates = await _dateSession.ReadAsync(Guid.Parse(list.User.Id), interactive, () =>
            {
                if (Current()) ManageDateMessage.Text = "Sign in in your browser to read registration dates.";
            }, request.Token);
            if (!request.IsCancellationRequested && Current() && ApplyRegistrationDates(list, dates))
                ManageDateNotice.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (Current())
                ManageDateMessage.Text = ex switch
                {
                    _ when request.IsCancellationRequested => "Loading dates was cancelled or timed out.",
                    HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "Graph did not grant access to registration dates.",
                    _ => "Registration dates are unavailable. Check your sign-in and connection."
                };
        }
        finally
        {
            if (ReferenceEquals(_dateRequest, request))
            {
                _dateRequest = null; ManageDateAction.Content = "Load dates";
            }
        }
    }

    private async Task ManageOperationAsync(Func<PasskeyRegistrationService, Task> action, Provider provider, UserCandidate user, string operation)
    {
        string failure = "";
        var ok = await RunWithResultAsync(async () =>
        {
            try
            {
                if (!MatchesManagementContext(provider, user)) throw new InvalidOperationException("Select the tenant and user again.");
                await ActivateAsync(provider);
                // Keep the CLI's actual config readable but not writable or replaceable on Windows
                // until this sequence completes; reject a provider change between activation and opening it.
                using var lease = File.Open(_configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var config = ConfigReader.Read(_configPath);
                ConfigReader.EnsureUnchanged(config, provider, null);
                if (config.ActiveProvider != provider.Name || !MatchesManagementContext(provider, user))
                    throw new InvalidOperationException("The active tenant changed. Reconnect and select the user again.");
                await action(new PasskeyRegistrationService(args => Runner.CaptureAsync(args)));
            }
            catch (Exception ex) { failure = ex is InvalidDataException or InvalidOperationException ? ex.Message : "Could not verify the operation. Refresh before trying again."; throw; }
        }, operation, false);
        if (!ok) { ClearManagedList(); ManageFeedback.Text = failure.Length == 0 ? "Refresh and try again." : failure; }
    }

    private async void ManageRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: RegistrationCard card } || _managedList is not { } list ||
            !list.Rows.Contains(card.Credential) || !MatchesManagementContext(list.Tenant, list.User)) return;
        var row = card.Credential;
        if (new RemoveRegistrationWindow(this, list.Tenant, list.User, row, card.DateLine).ShowDialog() != true) return;
        if (_busy || !ReferenceEquals(_managedList, list) || !MatchesManagementContext(list.Tenant, list.User)) return;
        ClearManagedList(); ManageFeedback.Text = "Checking and removing the selected registration…";
        await ManageOperationAsync(async service =>
        {
            var updated = await service.RemoveAsync(Guid.Parse(list.User.Id), row);
            SetManagedList(list.Tenant, list.User, updated);
            ManageFeedback.Text = $"Removed “{row.Name}” from Entra. " + ManageFeedback.Text;
            Record($"Passkey registration removed from Entra · tenant {list.Tenant.Name} · user {list.User.Id}.");
        }, list.Tenant, list.User, "Remove passkey registration");
        if (_managedList is { Rows.Count: > 0 } remaining && MatchesManagementContext(list.Tenant, list.User))
            await LoadRegistrationDatesAsync(remaining, interactive: false);
    }
}
