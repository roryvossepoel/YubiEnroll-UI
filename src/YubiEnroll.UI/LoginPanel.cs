using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

internal sealed class LoginPanel : Border
{
    private readonly TextBlock _status = new() { Text = "Preparing sign-in…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 20) };
    private readonly Button _open = new() { Content = "Open browser again", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel sign-in" };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private PseudoConsole? _session;
    private Uri? _url;
    private bool _cancelled;
    private bool _timedOut;
    private string? _failure;
    internal string FailureDetail => _failure ?? (_timedOut ? "Sign-in timed out." : _cancelled ? "Sign-in cancelled." : "Sign-in did not complete.");
    internal bool IsRunning { get; private set; }
    internal Task? Cancellation { get; private set; }
    private readonly Action<Uri> _launch;
    internal LoginPanel(string tenantName, Action<Uri>? launch = null)
    {
        _launch = launch ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        Background = Brushes.White; Padding = new Thickness(28);
        var body = new StackPanel { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(new TextBlock { Text = "Connect to tenant", Style = (Style)Application.Current.FindResource("PageTitle") });
        body.Children.Add(new TextBlock { Text = tenantName, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        body.Children.Add(_status);
        var actions = new WrapPanel(); actions.Children.Add(_open); actions.Children.Add(_cancel); body.Children.Add(actions);
        body.Children.Add(new TextBlock { Text = "Closed the browser? Open it again, or cancel to return to tenant selection.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 20), Foreground = (Brush)Application.Current.FindResource("Muted") });
        body.Children.Add(new Expander { Header = "Sign-in details", Content = _details });
        Child = body;
        _open.Click += (_, _) => Launch();
        _cancel.Click += (_, _) => Cancel();
    }
    private void Launch()
    {
        if (_url == null || _cancelled || !IsRunning) return;
        try { _launch(_url); _status.Text = "Complete sign-in in your browser. This page returns automatically when you are connected."; }
        catch { _status.Text = "The browser could not be opened. Try Open browser again, or cancel sign-in."; }
    }
    internal void Cancel()
    {
        if (!IsRunning || _cancelled) return;
        _cancelled = true; _open.IsEnabled = _cancel.IsEnabled = false;
        _status.Text = "Cancelling sign-in…";
        Cancellation = StopAsync();
    }
    private async Task StopAsync()
    {
        try { if (_session != null) await _session.CancelLoginAsync(); }
        catch { _status.Text = "Sign-in could not be stopped. Try cancelling again."; _cancelled = false; _cancel.IsEnabled = true; }
    }
    internal async Task<int> RunAsync(string executable, TenantSetup tenant, string[]? arguments = null, TimeSpan? timeout = null)
    {
        using var session = new PseudoConsole(32760); _session = session;
        var screen = new TerminalScreen(32760, 28);
        var protocol = new LoginProtocol(tenant);
        screen.Reply += session.Write;
        _details.Text = $"Tenant: {tenant.TenantId}\nClient: {tenant.ClientId}\nRedirect: {tenant.RedirectUri}";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = timeout ?? TimeSpan.FromMinutes(5) };
        timer.Tick += (_, _) => { _timedOut = true; timer.Stop(); Cancel(); };
        session.Output += text => Dispatcher.BeginInvoke(() =>
        {
            if (_cancelled || !IsRunning) return;
            try
            {
                screen.Feed(text);
                if (protocol.TryConfirm(screen.Text)) session.Write("y\r");
                if (_url == null && protocol.AuthorizationUrl(screen.CompletedText) is { } url)
                { _url = url; _open.IsEnabled = true; Launch(); }
            }
            catch (InvalidOperationException ex) { _failure = ex.Message; Cancel(); }
        });
        IsRunning = true; timer.Start();
        try
        {
            var exit = await session.RunAsync(executable, arguments ?? ["login", "--no-launch-browser"]);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (Cancellation != null) await Cancellation;
            return _cancelled ? 125 : exit;
        }
        catch { _failure ??= "The sign-in process could not be started or monitored."; return -1; }
        finally { timer.Stop(); IsRunning = false; _session = null; _url = null; screen.Clear(); }
    }
}
