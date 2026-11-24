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
    private readonly TextBlock _pinHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
    private readonly TerminalScreen _screen = new();
    private readonly Button _done = new() { Content = "Return", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TaskCompletionSource _dismissed = new();
    private PseudoConsole? _session;
    internal string FailureDetail { get; private set; } = "";
    private bool _running, _cancelRequested;
    private readonly Button _cancel = new() { Content = "Cancel operation", Margin = new Thickness(0,0,8,0) };
    internal string? GeneratedPin { get; private set; }
    private readonly string? _captureSerial;
    private readonly bool _enrollment, _addPasskey;
    internal int? CompletedExitCode { get; private set; }
    private readonly bool _autoCloseOnSuccess, _maskPin;
    internal TerminalPanel(string title, bool enrollment, bool autoCloseOnSuccess = false, bool addPasskey = false, string? captureSerial = null, bool nfc = false, bool single = false, bool savePin = false, string? profile = null)
    {
        _done.Content = single ? "View registration summary" : enrollment ? "Continue" : autoCloseOnSuccess ? "Continue" : "View reset result";
        _maskPin = single;
        _autoCloseOnSuccess = autoCloseOnSuccess; _captureSerial = captureSerial; _enrollment = enrollment; _addPasskey = addPasskey;
        System.Windows.Documents.TextElement.SetFontSize(this, 14);
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)); Padding = new Thickness(24);
        var layout = new DockPanel(); Child = layout;
        var heading = new TextBlock { Text = title + (enrollment && !addPasskey && !string.IsNullOrEmpty(profile) ? " · " + profile : ""), TextWrapping = TextWrapping.Wrap, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var help = new TextBlock { Text = autoCloseOnSuccess ? "Complete any confirmation below, then sign in in your browser. This page returns automatically after sign-in." : nfc ? "Follow the Yubico prompts below. Click the terminal to type. Keep the key flat on the NFC reader; remove and replace it only when prompted." : "Follow the Yubico prompts below. Click the terminal to type. Keep the key connected while an operation is running.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(help, Dock.Top); layout.Children.Add(help);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        footer.Children.Add(_status);
        if (enrollment && !addPasskey && (!single || captureSerial != null))
        {
            _pinHint.Text = single ? "If a temporary PIN is generated, you can view it in the registration summary."
                : savePin ? "Generated initial PINs will be saved in the registration log. Terminal output is not saved."
                : "Record any generated PIN before returning. Terminal output is not saved to the activity log.";
        }
        else _pinHint.Visibility = Visibility.Collapsed;
        footer.Children.Add(_pinHint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_cancel); buttons.Children.Add(_done); footer.Children.Add(buttons);
        _cancel.Click += async (_, _) =>
        {
            if (!_running || _session is not { HasStarted: true } session) return;
            if (MessageBox.Show(Window.GetWindow(this), "Stop this operation? Changes already made to the key or in Entra cannot be undone.", "Cancel operation", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes || !_running) return;
            _cancelRequested = true; _cancel.IsEnabled = false; _status.Text = "Stopping the operation…";
            try { await session.CancelOperationAsync(); }
            catch (System.ComponentModel.Win32Exception) { if (_running) { _cancel.IsEnabled = true; _status.Text = "Could not stop the operation. Do not touch the wrong key. Try cancelling again."; } }
        };
        var output = new Grid(); output.Children.Add(Screen);
        var waiting = new TextBlock { Text = autoCloseOnSuccess ? "Waiting for sign-in…\nIf your browser opened, complete authentication there." : "Starting the interactive operation…", Foreground = Brushes.WhiteSmoke, Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
        output.Children.Add(waiting); layout.Children.Add(output);
        Screen.TextChanged += (_, _) => waiting.Visibility = string.IsNullOrWhiteSpace(Screen.Text) ? Visibility.Visible : Visibility.Collapsed;
        _done.Click += (_, _) => { _screen.Clear(); Screen.Clear(); _dismissed.TrySetResult(); };
        Screen.PreviewTextInput += (_, e) => { if (_running && !_cancelRequested) _session?.Write(e.Text); e.Handled = true; };
        Screen.PreviewKeyDown += (_, e) =>
        {
            if (!_running || _cancelRequested) return;
            // Do not forward shortcuts that could paste secrets or accidentally interrupt a reset.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { e.Handled = true; return; }
            var sequence = e.Key switch { Key.Enter => "\r", Key.Back => "\x7f", Key.Tab => "\t", Key.Escape => "\x1b", Key.Up => "\x1b[A", Key.Down => "\x1b[B", Key.Right => "\x1b[C", Key.Left => "\x1b[D", Key.Home => "\x1b[H", Key.End => "\x1b[F", Key.Delete => "\x1b[3~", _ => null };
            if (sequence != null) { _session?.Write(sequence); e.Handled = true; }
        };
        DataObject.AddPastingHandler(Screen, (_, e) => e.CancelCommand());
        _screen.Reply += text => _session?.Write(text);
    }
    internal static string MaskPin(string output) => System.Text.RegularExpressions.Regex.Replace(output,
        @"(?m)^([ \t]*(?:Temporary PIN|Generated PIN):)[^\r\n]*", "$1 ••••••");
    internal static string PromptStatus(string output)
    {
        var line = output.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
        if (line.Equals("Enter PIN:", StringComparison.OrdinalIgnoreCase)) return "Enter your YubiKey PIN in the terminal";
        if (line.EndsWith("[y/N]:", StringComparison.OrdinalIgnoreCase)) return "Waiting for your confirmation in the terminal";
        if (line.StartsWith("Remove ", StringComparison.OrdinalIgnoreCase) && line.Contains("NFC reader", StringComparison.OrdinalIgnoreCase)) return "Remove this YubiKey from the NFC reader";
        if (line.StartsWith("Place ", StringComparison.OrdinalIgnoreCase) && line.Contains("NFC reader", StringComparison.OrdinalIgnoreCase)) return "Place the same YubiKey back on the NFC reader";
        return "Operation running";
    }

    internal void SendInput(string text) { if (_running && !_cancelRequested) _session?.Write(text); }
    internal void DismissCompleted() { if (!_running) { _screen.Clear(); Screen.Clear(); _dismissed.TrySetResult(); } }
    internal Func<int, string?, string?, Task<string?>>? NativeFinished { get; set; }
    internal async Task<int> RunAsync(string executable, string[] args)
    {
        using var session = new PseudoConsole(); _session = session;
        session.Output += text => Dispatcher.BeginInvoke(() =>
        {
            _screen.Feed(text); var output = _screen.Text;
            Screen.Text = _maskPin ? MaskPin(output) : output;
            if (_running && !_autoCloseOnSuccess && !_cancelRequested) _status.Text = PromptStatus(output); Screen.CaretIndex = Screen.Text.Length; Screen.ScrollToEnd();
        });
        _running = true; Screen.Focus(); _status.Text = _autoCloseOnSuccess ? "Connecting · complete browser sign-in when prompted" : "Operation running";
        int exit;
        try
        {
            exit = await session.RunAsync(executable, args);
            // Process queued final output before enabling dismissal.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (exit == 0 && !_cancelRequested && _captureSerial != null) GeneratedPin = GeneratedPinSummary.Read(_screen.Text, _captureSerial);
            if (exit != 0) FailureDetail = InteractiveFailure.Describe(_screen.Text);
            _status.Text = exit == 0 ? (_enrollment ? (_addPasskey ? "Passkey successfully registered (reported by YubiEnroll CLI)." : "Key successfully enrolled (reported by YubiEnroll CLI).") : "FIDO reset completed (reported by YubiKey Manager CLI).") : $"The tool finished with exit code {exit}. The operation may be incomplete. Inspect the result before retrying.";
        }
        catch
        {
            exit = -1;
            _status.Text = session.HasStarted ? "The terminal connection failed after launch. The operation's outcome is unknown. No retry was started." : "The embedded terminal could not start. Choose external console in Settings before trying again.";
        }
        finally { _running = false; _session = null; }
        _cancel.Visibility = Visibility.Collapsed;
        if (_cancelRequested)
        {
            exit = 125; GeneratedPin = null;
            FailureDetail = InteractiveFailure.OperatorInterrupted;
            _status.Text = "Operation interrupted. Check the key and any Entra registration before retrying.";
        }
        if (_maskPin)
        {
            _pinHint.Visibility = exit == 0 && GeneratedPin != null ? Visibility.Visible : Visibility.Collapsed;
            _pinHint.Text = "Your temporary PIN is available in the registration summary.";
        }
        if (NativeFinished != null)
        {
            var notice = await NativeFinished(exit, GeneratedPin, GeneratedPinSummary.ResetReceiptSerial(_screen.Text));
            if (notice != null)
            {
                _pinHint.Text = notice;
                _pinHint.Visibility = Visibility.Visible;
                // The operator must be able to record an unsaved PIN before dismissal.
                if (_maskPin && GeneratedPin == null) Screen.Text = _screen.Text;
            }
        }
        _done.IsEnabled = true;
        _done.Focus();
        CompletedExitCode = exit;
        if (_autoCloseOnSuccess && exit == 0) { _screen.Clear(); Screen.Clear(); return exit; }
        await _dismissed.Task;
        return exit;
    }
}
