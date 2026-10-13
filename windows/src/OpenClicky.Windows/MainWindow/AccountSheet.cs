// Ports Dictation/UI/AccountSheet.swift, EmailAccountForm.swift and the AccountProfileRow of
// SettingsAccountPage.swift. The account sheet: a heading, the one email form, and "not now". The
// form: an email, then — only if that email already has an account — a 6-digit code from the inbox;
// a new email is signed in straight away. A guest (signed in with no email yet) can add one, and an
// unconfirmed address confirms with the code from the email (GuestConfirmationCodeField).

using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;
using EmailFlowState = OpenClicky.Core.Providers.OpenClickyAuthSession.EmailFlowState;

namespace OpenClicky.Windows.AppWindow;

/// <summary>The person's name as Windows knows it (NSFullUserName).</summary>
internal static class WindowsUserName
{
    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameEx(int nameFormat, StringBuilder userName, ref uint userNameSize);

    private const int NameDisplay = 3;

    public static string FullName
    {
        get
        {
            try
            {
                uint size = 256;
                var buffer = new StringBuilder((int)size);
                if (GetUserNameEx(NameDisplay, buffer, ref size) && buffer.Length > 0) return buffer.ToString();
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
            return Environment.UserName;
        }
    }
}

internal sealed class AccountSheet : SheetWindow
{
    private AccountSheet(Window owner, DictationSettings settings, OpenClickyAuthSession auth, Action? onUseOwnKey)
        : base(owner, settings, "Account", 460)
    {
        var form = new EmailAccountForm(auth, onSignedIn: Close, onUseOwnKey: onUseOwnKey is null ? null : () => { Close(); onUseOwnKey(); });
        var body = Ui.VStack(14,
            Ui.Text("your free openclicky account", 20, SettingsLook.TextPrimary, FontWeights.SemiBold),
            Ui.Text("polished dictation and spoken answers, on us: up to $10 of use a month once your email is confirmed.", SettingsLook.CaptionSize, SettingsLook.TextSecondary),
            form);
        SetBody(body, new Thickness(28, 6, 28, 20));
        AddFooterButton("not now", Close, isCancel: true);
        Closed += (_, _) => form.CancelWork();
    }

    public static void Show(Window owner, DictationSettings settings, OpenClickyAuthSession auth, Action? onUseOwnKey) =>
        new AccountSheet(owner, settings, auth, onUseOwnKey).ShowDialog();
}

/// <summary>The one account form, drawn for the flow's state; the form owns the work it starts and cancels it when it goes.</summary>
internal sealed class EmailAccountForm : ContentControl
{
    private readonly OpenClickyAuthSession auth;
    private readonly Action onSignedIn;
    private readonly Action? onUseOwnKey;
    private readonly AccountWorkSlot work = new();
    private string email = "";
    private string code = "";
    /// <summary>The address a new code is being sent to, so the code step stays up while it goes.</summary>
    private string? resendingCodeTo;

    public EmailAccountForm(OpenClickyAuthSession auth, Action onSignedIn, Action? onUseOwnKey)
    {
        this.auth = auth;
        this.onSignedIn = onSignedIn;
        this.onUseOwnKey = onUseOwnKey;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        auth.ForgetSettledFlow();
        auth.PropertyChanged += OnAuthChanged;
        Unloaded += (_, _) => auth.PropertyChanged -= OnAuthChanged;
        Render();
    }

    public void CancelWork() => work.Cancel();

    private void OnAuthChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs arguments)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (arguments.PropertyName == nameof(OpenClickyAuthSession.EmailFlow) && auth.EmailFlow is EmailFlowState.SignedInState)
            {
                ShellSettingsRevision.NoteChanged();
                onSignedIn();
                return;
            }
            Render();
        });
    }

    private void Render()
    {
        var state = auth.EmailFlow;
        Content = state switch
        {
            EmailFlowState.NeedsCodeState needsCode => CodeStep(needsCode.Email, state),
            EmailFlowState.CheckingCodeState checking => CodeStep(checking.Email, state),
            EmailFlowState.SendingState when resendingCodeTo is not null => CodeStep(resendingCodeTo, state),
            _ => EmailStep(state),
        };
    }

    private StackPanel EmailStep(EmailFlowState state)
    {
        var isSending = state is EmailFlowState.SendingState;
        var field = new WpfUi.TextBox { PlaceholderText = "your email", Text = email, MinWidth = 220 };
        var continueButton = Ui.Button(isSending ? "one moment…" : "continue", SubmitEmail, WpfUi.ControlAppearance.Primary,
            isEnabled: !isSending && EmailAccountFormRules.CanSubmitEmail(email));
        continueButton.IsDefault = true;
        field.TextChanged += (_, _) =>
        {
            email = field.Text;
            continueButton.IsEnabled = !isSending && EmailAccountFormRules.CanSubmitEmail(email);
        };
        var row = new DockPanel();
        DockPanel.SetDock(continueButton, Dock.Right);
        continueButton.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(continueButton);
        row.Children.Add(field);
        var stack = Ui.VStack(10, row, Ui.Text("no password. we'll send a link to confirm it's you.", SettingsLook.CaptionSize, SettingsLook.TextSecondary));
        if (state is EmailFlowState.FailedState failed) stack.Children.Add(Spaced(Ui.Text(failed.Message, SettingsLook.CaptionSize, SettingsLook.Danger)));
        if (onUseOwnKey is not null) stack.Children.Add(Spaced(Ui.LinkButton("use my own key instead", onUseOwnKey)));
        Dispatcher.BeginInvoke(() => field.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return stack;
    }

    private StackPanel CodeStep(string address, EmailFlowState state)
    {
        var isChecking = state is EmailFlowState.CheckingCodeState;
        var isSending = state is EmailFlowState.SendingState;
        var field = new WpfUi.TextBox { PlaceholderText = "123456", Text = code, Width = 130, MaxLength = 7 };
        var signInButton = Ui.Button(isChecking ? "checking…" : "sign in", SubmitCode, WpfUi.ControlAppearance.Primary,
            isEnabled: !isChecking && EmailAccountFormRules.CanSubmitCode(code));
        signInButton.IsDefault = true;
        field.TextChanged += (_, _) =>
        {
            code = field.Text;
            signInButton.IsEnabled = !isChecking && EmailAccountFormRules.CanSubmitCode(code);
        };
        var stack = Ui.VStack(10,
            Ui.Text($"you already have an account. we sent a 6-digit code to {address}.", 13, SettingsLook.TextPrimary),
            Ui.HStack(8, field, signInButton));
        if (auth.LastErrorText is { } codeError) stack.Children.Add(Spaced(Ui.Text(codeError, SettingsLook.CaptionSize, SettingsLook.Danger)));
        var links = Ui.HStack(14,
            Ui.LinkButton(isSending ? "sending…" : "send a new code", SendNewCode),
            Ui.LinkButton("use a different email", () =>
            {
                work.Cancel();
                auth.ResetFlow();
                code = "";
                Render();
            }));
        stack.Children.Add(Spaced(links));
        Dispatcher.BeginInvoke(() => field.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return stack;
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 10, 0, 0);
        return element;
    }

    private void SubmitEmail()
    {
        if (!EmailAccountFormRules.CanSubmitEmail(email) || work.IsRunning) return;
        var address = email.Trim();
        work.Start(token => auth.StartAsync(address, token));
    }

    /// <summary>Asks /auth/start again for the same address, which mails a fresh code.</summary>
    private void SendNewCode()
    {
        if (auth.EmailFlow is not EmailFlowState.NeedsCodeState needsCode || work.IsRunning) return;
        code = "";
        resendingCodeTo = needsCode.Email;
        work.Start(async token =>
        {
            await auth.StartAsync(needsCode.Email, token);
            await Dispatcher.InvokeAsync(() => { resendingCodeTo = null; Render(); });
        });
    }

    private void SubmitCode()
    {
        if (!EmailAccountFormRules.CanSubmitCode(code) || work.IsRunning) return;
        var digits = new string(code.Where(character => !char.IsWhiteSpace(character)).ToArray());
        work.Start(token => auth.SubmitCodeAsync(digits, token));
    }
}

/// <summary>
/// The profile row: signed in (the address, sign out, and the confirmation code while it is
/// unconfirmed), a guest trying it free, or not signed in (Sign In… opens the account sheet).
/// </summary>
internal sealed class AccountProfileRow : ContentControl
{
    private readonly MainWindowServices services;
    private readonly IMainWindowActions actions;
    private readonly BillingStatusModel billing = new();
    private readonly AccountWorkSlot resendWork = new();
    private string? resendNote;

    public AccountProfileRow(MainWindowServices services, IMainWindowActions actions)
    {
        this.services = services;
        this.actions = actions;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        void Redraw() => Dispatcher.BeginInvoke(Render);
        void OnAuthChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs arguments) => Redraw();
        void OnBillingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs arguments) => Redraw();
        billing.PropertyChanged += OnBillingChanged;
        Loaded += async (_, _) =>
        {
            services.Auth.PropertyChanged += OnAuthChanged;
            ShellSettingsRevision.Changed += Redraw;
            OpenClickyConfiguration.SettingsChanged += Redraw;
            if (OpenClickyConfiguration.HasAccountToken) billing.Refresh();
            try { await services.Auth.RefreshAccountsOpenAsync(); }
            catch (Exception error) { AppLog.Write($"account: accounts-open check failed: {error.Message}"); }
        };
        Unloaded += (_, _) =>
        {
            services.Auth.PropertyChanged -= OnAuthChanged;
            ShellSettingsRevision.Changed -= Redraw;
            OpenClickyConfiguration.SettingsChanged -= Redraw;
            resendWork.Cancel();
        };
        Render();
    }

    public static FrameworkElement Avatar(string initial, bool isSignedIn, double size)
    {
        var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), VerticalAlignment = VerticalAlignment.Center };
        circle.SetResourceReference(Border.BackgroundProperty, isSignedIn ? SettingsLook.Accent : "ControlStrongFillColorDisabledBrush");
        var letter = new TextBlock
        {
            Text = initial,
            FontSize = size * 0.42,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        circle.Child = letter;
        return circle;
    }

    private void Render()
    {
        var auth = services.Auth;
        var name = AccountRows.AccountName(auth.AccountEmail, WindowsUserName.FullName);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        UIElement action;
        if (auth.AccountEmail is { } accountEmail)
        {
            text.Children.Add(Ui.Text(accountEmail, 15, SettingsLook.TextPrimary, FontWeights.SemiBold));
            text.Children.Add(Ui.Text("Signing out keeps your history on this PC.", SettingsLook.CaptionSize, SettingsLook.TextSecondary));
            if (billing.Summary?.IsUnconfirmed == true)
            {
                var note = Ui.HStack(6,
                    Ui.Text(resendNote ?? "Unconfirmed. Check your inbox for the code.", SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false),
                    Ui.LinkButton("Resend", Resend));
                note.Margin = new Thickness(0, 6, 0, 0);
                text.Children.Add(note);
                if (auth.ConfirmByCodeSupported) text.Children.Add(new GuestConfirmationCodeField(auth, billing.Refresh) { Margin = new Thickness(0, 8, 0, 0) });
            }
            action = Ui.Button("Sign Out", () =>
            {
                auth.SignOut();
                ShellSettingsRevision.NoteChanged();
            });
        }
        else
        {
            var isGuest = auth.IsGuestWithoutEmail;
            text.Children.Add(Ui.Text(isGuest ? "Trying OpenClicky" : "Not signed in", 15, SettingsLook.TextPrimary, FontWeights.SemiBold));
            text.Children.Add(Ui.Text(isGuest
                ? "Free on OpenClicky’s credits, on this PC. Add your email to keep the account, or your own key for more."
                : "A free account adds AI polish and spoken answers. Dictation on this PC works without one.", SettingsLook.CaptionSize, SettingsLook.TextSecondary));
            // Signed out by a dead sign-in, not by the person: say why, where they look.
            if (!isGuest && auth.SessionExpiredNotice is { } expired)
            {
                var expiredText = Ui.Text(expired, SettingsLook.CaptionSize, SettingsLook.Danger);
                expiredText.Margin = new Thickness(0, 4, 0, 0);
                text.Children.Add(expiredText);
            }
            action = Ui.Button(isGuest ? "Add Email…" : "Sign In…", () =>
                AccountSheet.Show(actions.OwnerWindow, services.Settings, auth, onUseOwnKey: () => actions.ShowSettings(DictationSettingsPage.Keys)),
                WpfUi.ControlAppearance.Primary);
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Avatar(AccountRows.AccountInitial(name), auth.AccountEmail is not null, 44));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (action is FrameworkElement actionElement)
        {
            actionElement.VerticalAlignment = VerticalAlignment.Center;
            actionElement.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(actionElement, 2);
            grid.Children.Add(actionElement);
        }
        Content = Ui.Card(grid, new Thickness(16, 14, 16, 14));
    }

    private void Resend()
    {
        resendWork.Start(async _ =>
        {
            var sent = await services.Auth.ResendLinkAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                resendNote = sent ? "Sent. Check your inbox." : "Couldn’t send it. Try again in a minute.";
                Render();
            });
        });
    }
}

/// <summary>"Type the code from the email": the 6-digit code confirms the address (the link still works).</summary>
internal sealed class GuestConfirmationCodeField : StackPanel
{
    private readonly OpenClickyAuthSession auth;
    private readonly Action onConfirmed;
    private readonly AccountWorkSlot work = new();
    private readonly WpfUi.TextBox field = new() { PlaceholderText = "code from the email", Width = 170, MaxLength = 7 };
    private readonly WpfUi.Button confirmButton;
    private readonly TextBlock errorText = Ui.Text("", SettingsLook.CaptionSize, SettingsLook.Danger);

    public GuestConfirmationCodeField(OpenClickyAuthSession auth, Action onConfirmed)
    {
        this.auth = auth;
        this.onConfirmed = onConfirmed;
        confirmButton = Ui.Button("confirm", Confirm, isEnabled: false);
        field.TextChanged += (_, _) => confirmButton.IsEnabled = !work.IsRunning && EmailAccountFormRules.CanSubmitCode(field.Text);
        field.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) Confirm(); };
        Children.Add(Ui.HStack(8, field, confirmButton));
        errorText.Visibility = Visibility.Collapsed;
        errorText.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(errorText);
        Unloaded += (_, _) => work.Cancel();
    }

    private void Confirm()
    {
        var digits = new string(field.Text.Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (!EmailAccountFormRules.CanSubmitCode(digits) || work.IsRunning) return;
        confirmButton.Content = "checking…";
        confirmButton.IsEnabled = false;
        errorText.Visibility = Visibility.Collapsed;
        work.Start(async _ =>
        {
            var message = await auth.ConfirmEmailAsync(digits);
            await Dispatcher.InvokeAsync(() =>
            {
                confirmButton.Content = "confirm";
                confirmButton.IsEnabled = true;
                if (message is not null)
                {
                    errorText.Text = message;
                    errorText.Visibility = Visibility.Visible;
                }
                else if (auth.ConfirmByCodeSupported)
                {
                    field.Text = "";
                    onConfirmed();
                }
            });
        });
    }
}
