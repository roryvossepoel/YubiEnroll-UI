using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Identity.Client;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private sealed record RegistrationList(Provider Tenant, UserCandidate User, IReadOnlyList<RegistrationCard> Rows);
    private sealed record ManagementConnection(Provider Tenant, ManagementGraph Graph);
    private int _manageStep;
    private RegistrationList? _managedList;
    private ManagementConnection? _management;
    private CancellationTokenSource? _managementLogin;

    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ManagePage.IsVisible) return;
        ShowPage(ManagePage); ResetManagement();
    }

    private void EndManagementSession()
    {
        var pending = _managementLogin; _managementLogin = null; pending?.Cancel();
        _management = null;
    }

    private void ResetManagement()
    {
        if (ManageBack == null) return;
        EndManagementSession();
        _manageStep = 0;
        ManageQuery.Clear(); ManageUsers.ItemsSource = null; ManageSearchHint.Text = "";
        ClearManagedList(); RenderManagement();
    }

    private void ClearManagedList()
    {
        _managedList = null; ManageRows.ItemsSource = null; ManageFeedback.Text = "";
    }

    private void RenderManagement()
    {
        if (ManageBack == null || ManagePage.Visibility != Visibility.Visible) return;
        var connected = _management != null && Equals(_management.Tenant, Tenants.SelectedItem);
        RenderProgress(ManageProgress, ["Tenant", "User", "Passkeys"], _manageStep);
        ManageTenantStep.Visibility = Visible(_manageStep == 0);
        ManageUserStep.Visibility = Visible(_manageStep == 1);
        ManageListStep.Visibility = Visible(_manageStep == 2);
        ManageContext.Text = _manageStep == 0 ? "View or remove a user’s YubiKey registrations in Microsoft Entra. No connected key is required." :
            _manageStep == 1 ? $"Tenant · {_management?.Tenant.Name}" :
            $"{_management?.Tenant.Name}  ·  {(ManageUsers.SelectedItem as UserCandidate)?.Label}";
        ManageTenantHint.Text = Tenants.Items.Count == 0 ? "Add a tenant in Settings → Tenants." :
            _managementLogin != null ? "Complete sign-in in your browser." :
            connected ? $"Connected to {SelectedProvider.Name}" : "Sign in with an administrator account for this tenant.";
        ManageConnect.IsEnabled = Tenants.SelectedItem is Provider;
        ManageConnect.Content = _managementLogin != null ? "Cancel sign-in" : connected ? "Continue to user" : "Connect";
        ManageView.IsEnabled = connected && ManageUsers.SelectedItem is UserCandidate;
        ManageBack.Content = _manageStep switch { 2 => "←  Choose another user", 1 => "←  Choose tenant", _ => "Back to Home" };
    }

    private async void ManageConnect_Click(object sender, RoutedEventArgs e) => await ConnectManagementAsync(ManagementSignIn.ConnectAsync);

    private async Task ConnectManagementAsync(Func<TenantSetup, CancellationToken, Task<ManagementGraph>> signIn)
    {
        if (_busy || !ManagePage.IsVisible || _manageStep != 0 || Tenants.SelectedItem is not Provider provider) return;
        if (_managementLogin is { } pending) { pending.Cancel(); return; }
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _managementLogin = request; RenderManagement();
        string? failure = null;
        try
        {
            var snapshot = File.ReadAllText(_configPath);
            ConfigReader.EnsureUnchanged(ConfigReader.Parse(snapshot), provider, null);
            if (_management?.Tenant != provider)
            {
                var graph = await signIn(TenantConfigWriter.Read(snapshot, provider.Name, requirePublicCloud: true).Validate(), request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(request, _managementLogin) || !ManagePage.IsVisible || !Equals(Tenants.SelectedItem, provider)) return;
                ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, null);
                _management = new(provider, graph);
            }
            _manageStep = 1;
        }
        catch (Exception ex) { failure = ManagementError(ex); }
        finally
        {
            if (ReferenceEquals(request, _managementLogin))
            {
                _managementLogin = null;
                if (failure != null) _management = null;
                RenderManagement();
                if (failure != null) ManageTenantHint.Text = failure;
                else ManageQuery.Focus();
            }
        }
    }

    private void ManageBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_manageStep == 0) { ShowPage(OverviewPage); return; }
        if (_manageStep == 1) { ManageQuery.Clear(); ManageUsers.ItemsSource = null; ManageSearchHint.Text = ""; }
        ClearManagedList(); ManageUsers.SelectedItem = null;
        --_manageStep; RenderManagement();
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
        if (_busy || !ManagePage.IsVisible || _manageStep != 1 || _management is not { } session) return;
        ManageUsers.ItemsSource = null; ManageSearchHint.Text = "Searching…";
        var query = ManageQuery.Text.Trim();
        await ManagementOperationAsync(session, "Search tenant users", ManageSearchHint, async ct =>
        {
            var users = await session.Graph.SearchAsync(query, ct);
            if (!Current(session) || _manageStep != 1 || query != ManageQuery.Text.Trim()) return;
            ManageUsers.ItemsSource = users;
            ManageSearchHint.Text = users.Count == 0 ? "No matching users found." : users.Count == ManagementGraph.SearchLimit ?
                $"Showing the first {users.Count} users. Refine your search if needed." : "Select a user to view their passkeys.";
        });
    }

    private async void ManageView_Click(object sender, RoutedEventArgs e) => await ReadManagedListAsync();
    private async Task ReadManagedListAsync()
    {
        if (_busy || _manageStep is not (1 or 2) || _management is not { } session || !Current(session) ||
            ManageUsers.SelectedItem is not UserCandidate user) return;
        _manageStep = 2; ClearManagedList(); RenderManagement(); ManageFeedback.Text = "Loading registrations…";
        await ManagementOperationAsync(session, "Read passkey registrations", ManageFeedback, async ct =>
        {
            var cards = await session.Graph.ListAsync(Guid.Parse(user.Id), ct);
            SetManagedList(session, user, cards);
        });
    }

    private bool Current(ManagementConnection session) => ReferenceEquals(_management, session) &&
        Equals(Tenants.SelectedItem, session.Tenant) && ManagePage.IsVisible;
    private bool Current(ManagementConnection session, UserCandidate user) => Current(session) &&
        _manageStep == 2 && ManageUsers.SelectedItem is UserCandidate selected && selected.Id == user.Id;

    private void SetManagedList(ManagementConnection session, UserCandidate user, IReadOnlyList<RegistrationCard> cards)
    {
        if (!Current(session, user)) return;
        var hardware = cards.Where(c => c.Credential.IsYubicoHardware).ToArray();
        _managedList = new(session.Tenant, user, hardware);
        ManageRows.ItemsSource = hardware;
        ManageFeedback.Text = hardware.Length == 0 ? "No recognized YubiKey registrations found for this user." :
            $"{hardware.Length} YubiKey {(hardware.Length == 1 ? "registration" : "registrations")} in Entra.";
    }

    private async Task ManagementOperationAsync(ManagementConnection session, string operation, TextBlock feedback, Func<CancellationToken, Task> action)
    {
        string? failure = null;
        bool reconnect = false;
        var ok = await RunWithResultAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                if (!Current(session)) throw new InvalidOperationException("The selected tenant changed. Connect again.");
                ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), session.Tenant, null);
                await action(timeout.Token);
            }
            catch (Exception ex)
            {
                reconnect = ex is MsalUiRequiredException or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };
                failure = ManagementError(ex);
                throw new InvalidOperationException(failure);
            }
        }, operation, false);
        if (!ok && Current(session))
        {
            if (reconnect) { ResetManagement(); ManageTenantHint.Text = failure; }
            else { ClearManagedList(); feedback.Text = failure ?? "Refresh and try again."; }
        }
    }

    private static string ManagementError(Exception ex) => ex switch
    {
        MsalUiRequiredException or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Your session requires a new sign-in. Connect again.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "Access denied. Check the delegated Graph permissions and your active Entra role.",
        MsalException { ErrorCode: "authentication_canceled" } => "Sign-in cancelled. Select Connect to try again.",
        OperationCanceledException => "The request was cancelled or timed out.",
        MsalException auth => $"Microsoft sign-in did not complete ({auth.ErrorCode}). Check the sign-in result in your browser.",
        InvalidOperationException or InvalidDataException or ArgumentException => ex.Message,
        _ => "Could not read the selected configuration or reach Microsoft Graph. Check your connection and try again."
    };

    private async void ManageRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: RegistrationCard card } || _management is not { } session ||
            _managedList is not { } list || !list.Rows.Contains(card) || !Current(session, list.User)) return;
        if (new RemoveRegistrationWindow(this, list.Tenant, list.User, card.Credential, card.DateLine).ShowDialog() != true) return;
        if (_busy || !ReferenceEquals(_managedList, list) || !Current(session, list.User)) return;
        ClearManagedList(); ManageFeedback.Text = "Checking and removing the selected registration…";
        await ManagementOperationAsync(session, "Remove passkey registration", ManageFeedback, async ct =>
        {
            var updated = await session.Graph.RemoveAsync(Guid.Parse(list.User.Id), card.Credential, ct);
            if (!Current(session, list.User)) return;
            SetManagedList(session, list.User, updated);
            ManageFeedback.Text = $"Removed “{card.Name}” from Entra. " + ManageFeedback.Text;
            Record($"Passkey registration removed from Entra · tenant {list.Tenant.Name} · user {list.User.Id}.");
        });
    }
}
