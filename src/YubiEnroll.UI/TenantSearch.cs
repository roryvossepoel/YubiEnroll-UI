using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Identity.Client;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private readonly ObservableCollection<TenantKeyRow> _tenantRows = [];
    private ManagementGraph? _inventoryGraph;
    private CancellationTokenSource? _tenantLoad;
    private void TenantSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TenantSearchPage.IsVisible) return;
        ShowPage(TenantSearchPage);
        if (!TenantSearchPage.IsVisible) return;
        SearchTenant.SelectedIndex = -1;
        ClearTenantSearch();
        InventoryTable.ItemsSource = _tenantRows;
    }

    private void ClearTenantSearch()
    {
        _tenantLoad?.Cancel();
        _tenantLoad = null;
        _inventoryGraph = null;
        _tenantRows.Clear();
        InventoryFilter.Clear();
        InventoryStatus.Text = "";
        LoadTenantKeys.Content = "Connect and load";
        LoadTenantKeys.IsEnabled = SearchTenant.SelectedItem is Provider;
    }

    private void SearchTenant_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (InventoryStatus != null) ClearTenantSearch();
    }

    private void InventoryFilter_Changed(object sender, TextChangedEventArgs e)
    {
        var query = InventoryFilter.Text.Trim();
        CollectionViewSource.GetDefaultView(_tenantRows).Filter = item => ((TenantKeyRow)item).Matches(query);
    }

    private async void LoadTenantKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_tenantLoad != null) { _tenantLoad.Cancel(); return; }
        if (SearchTenant.SelectedItem is not Provider tenant || !TenantSearchPage.IsVisible) return;
        using var request = new CancellationTokenSource();
        _tenantLoad = request;
        bool Current() => ReferenceEquals(_tenantLoad, request) && Equals(SearchTenant.SelectedItem, tenant) && TenantSearchPage.IsVisible;
        _tenantRows.Clear();
        LoadTenantKeys.Content = "Cancel";
        var checkedUsers = 0;
        var unavailable = 0;
        try
        {
            var snapshot = File.ReadAllText(_configPath);
            ConfigReader.EnsureUnchanged(ConfigReader.Parse(snapshot), tenant, null);
            InventoryStatus.Text = _inventoryGraph == null ? "Complete sign-in in your browser." : "Reading tenant users…";
            var graph = _inventoryGraph ?? await ManagementSignIn.ConnectAsync(
                TenantConfigWriter.Read(snapshot, tenant.Name, requirePublicCloud: true).Validate(), request.Token);
            if (!Current()) return;
            request.Token.ThrowIfCancellationRequested();
            _inventoryGraph = graph;
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), tenant, null);
            InventoryStatus.Text = "Reading tenant users…";
            await foreach (var batch in graph.ScanTenantAsync(request.Token))
            {
                if (!Current()) return;
                request.Token.ThrowIfCancellationRequested();
                foreach (var row in batch.Rows) _tenantRows.Add(row);
                checkedUsers = batch.UsersChecked;
                unavailable = batch.Unavailable;
                InventoryStatus.Text = $"{checkedUsers:N0} of {batch.TotalUsers:N0} users checked · {_tenantRows.Count:N0} registrations · {unavailable:N0} unavailable users";
            }
            if (Current()) InventoryStatus.Text = unavailable == 0
                ? $"{_tenantRows.Count:N0} registrations across {checkedUsers:N0} checked users."
                : $"Partial results · {_tenantRows.Count:N0} registrations · {unavailable:N0} of {checkedUsers:N0} users could not be read.";
        }
        catch (Exception ex) when (Current())
        {
            if (ex is MsalUiRequiredException or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }) _inventoryGraph = null;
            InventoryStatus.Text = $"Search incomplete · {checkedUsers:N0} users checked. {ManagementError(ex)}";
        }
        catch (Exception) when (!Current()) { /* A newer tenant selection owns the view. */ }
        finally
        {
            if (Current())
            {
                _tenantLoad = null;
                LoadTenantKeys.Content = _inventoryGraph == null ? "Connect and load" : "Refresh";
            }
        }
    }
}
