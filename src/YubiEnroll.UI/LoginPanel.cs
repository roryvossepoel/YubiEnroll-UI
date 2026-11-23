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
    internal LoginPanel(string tenantName, Action<Uri>? launch = null, string eyebrow = "TENANT SIGN-IN", string title = "Connect to a tenant", Action<Grid>? drawSteps = null)
    {
        _launch = launch ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        Style Resource(string name) => (Style)Application.Current.FindResource(name);
        Background = Brushes.White;
        var page = new DockPanel();
        var actions = new DockPanel(); _cancel.Style = Resource("QuietButton"); _open.Style = Resource("PrimaryButton");
        DockPanel.SetDock(_open, Dock.Right); actions.Children.Add(_open);
        _cancel.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(_cancel);
        var footer = new Border { Padding = new Thickness(28,16,28,16), BorderThickness = new Thickness(0,1,0,0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(221,226,228)), Child = actions };
        DockPanel.SetDock(footer,Dock.Bottom); page.Children.Add(footer);
        var frame = new DockPanel { Style = Resource("WizardFrame") }; page.Children.Add(frame);
        var header = new StackPanel(); DockPanel.SetDock(header,Dock.Top); frame.Children.Add(header);
        if (drawSteps != null) { var steps = new Grid { Style = Resource("WizardSteps") }; header.Children.Add(steps); drawSteps(steps); }
        header.Children.Add(new TextBlock { Text = eyebrow, Style = Resource("WizardEyebrow") });
        header.Children.Add(new TextBlock { Text = title, Style = Resource("PageTitle") });
        _status.Style = Resource("WizardIntro"); _status.ClearValue(MarginProperty); header.Children.Add(_status);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = tenantName, Style = Resource("SectionTitle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        body.Children.Add(new Expander { Header = "Sign-in details", Content = _details });
        frame.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Child = page;
        _open.Click += (_, _) => Launch();
        _cancel.Click += (_, _) => Cancel();
    }
    internal void Launch()
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
        try { if (_session != null) await _session.CancelOperationAsync(); }
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
