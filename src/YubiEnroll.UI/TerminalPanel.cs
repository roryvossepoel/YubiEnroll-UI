using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

internal sealed class TerminalPanel : Border
{
    internal readonly TextBox Screen = new()
    {
        IsReadOnly = true, IsReadOnlyCaretVisible = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 13,
        Background = new SolidColorBrush(Color.FromRgb(20, 31, 46)), Foreground = Brushes.WhiteSmoke,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(16), ContextMenu = new ContextMenu()
    };
    private readonly TerminalScreen _screen = new();
    private readonly Button _done = new() { Content = "Return", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TaskCompletionSource _dismissed = new();
    private PseudoConsole? _session;
    internal string FailureDetail { get; private set; } = "";
    private bool _running;
    internal int? CompletedExitCode { get; private set; }
    private readonly bool _autoCloseOnSuccess;
    internal TerminalPanel(string title, bool enrollment, bool autoCloseOnSuccess = false)
    {
        _autoCloseOnSuccess = autoCloseOnSuccess;
        System.Windows.Documents.TextElement.SetFontSize(this, 14);
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)); Padding = new Thickness(24);
        var layout = new DockPanel(); Child = layout;
        var heading = new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var help = new TextBlock { Text = autoCloseOnSuccess ? "Complete any confirmation below, then sign in in your browser. This page returns automatically after sign-in." : "Follow the Yubico prompts below. Click the terminal to type. Keep the key connected while an operation is running.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(help, Dock.Top); layout.Children.Add(help);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        footer.Children.Add(_status);
        if (enrollment) footer.Children.Add(new TextBlock { Text = "Record any generated PIN securely before returning. Terminal output is not saved to the activity log.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        footer.Children.Add(_done);
        var output = new Grid(); output.Children.Add(Screen);
        var waiting = new TextBlock { Text = autoCloseOnSuccess ? "Waiting for sign-in…\nIf your browser opened, complete authentication there." : "Starting the interactive operation…", Foreground = Brushes.WhiteSmoke, Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
        output.Children.Add(waiting); layout.Children.Add(output);
        Screen.TextChanged += (_, _) => waiting.Visibility = string.IsNullOrWhiteSpace(Screen.Text) ? Visibility.Visible : Visibility.Collapsed;
        _done.Click += (_, _) => { _screen.Clear(); Screen.Clear(); _dismissed.TrySetResult(); };
        Screen.PreviewTextInput += (_, e) => { if (_running) _session?.Write(e.Text); e.Handled = true; };
        Screen.PreviewKeyDown += (_, e) =>
        {
            if (!_running) return;
            // Do not forward shortcuts that could paste secrets or accidentally interrupt a reset.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { e.Handled = true; return; }
            var sequence = e.Key switch { Key.Enter => "\r", Key.Back => "\x7f", Key.Tab => "\t", Key.Escape => "\x1b", Key.Up => "\x1b[A", Key.Down => "\x1b[B", Key.Right => "\x1b[C", Key.Left => "\x1b[D", Key.Home => "\x1b[H", Key.End => "\x1b[F", Key.Delete => "\x1b[3~", _ => null };
            if (sequence != null) { _session?.Write(sequence); e.Handled = true; }
        };
        DataObject.AddPastingHandler(Screen, (_, e) => e.CancelCommand());
        _screen.Reply += text => _session?.Write(text);
    }
    internal void SendInput(string text) { if (_running) _session?.Write(text); }
    internal void DismissCompleted() { if (!_running) { _screen.Clear(); Screen.Clear(); _dismissed.TrySetResult(); } }
    internal async Task<int> RunAsync(string executable, string[] args)
    {
        using var session = new PseudoConsole(); _session = session;
        session.Output += text => Dispatcher.BeginInvoke(() =>
        {
            _screen.Feed(text); Screen.Text = _screen.Text; Screen.CaretIndex = Screen.Text.Length; Screen.ScrollToEnd();
        });
        _running = true; Screen.Focus(); _status.Text = _autoCloseOnSuccess ? "Connecting · complete browser sign-in when prompted" : "Operation running";
        int exit;
        try
        {
            exit = await session.RunAsync(executable, args);
            // Process queued final output before enabling dismissal.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (exit != 0) FailureDetail = InteractiveFailure.Describe(_screen.Text);
            _status.Text = $"The Yubico tool finished with exit code {exit}." + (exit == 0 ? "" : " The operation may be incomplete. Inspect the result before retrying.");
        }
        catch
        {
            exit = -1;
            _status.Text = session.HasStarted ? "The terminal connection failed after launch. The operation's outcome is unknown. No retry was started." : "The embedded terminal could not start. Choose external console in Settings before trying again.";
        }
        finally { _running = false; _session = null; _done.IsEnabled = true; _done.Focus(); }
        CompletedExitCode = exit;
        if (_autoCloseOnSuccess && exit == 0) { _screen.Clear(); Screen.Clear(); return exit; }
        await _dismissed.Task;
        return exit;
    }
}
