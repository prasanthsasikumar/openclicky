// Ports Dictation/UI/SettingsAccountPage.swift: Settings → Account (sign in or out, with a free
// account set up in AccountSheet; the allowance and what each engine costs; the backend host and
// token from shell.json) and Settings → Keys & Plans (your own provider keys in shell.json, masked,
// each testable; and the Claude Code / Codex plans with their CLI status).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    // MARK: - Keys & Plans

    /// <summary>What the keys list remembers between redraws: the provider being added, and what the last Test or Save said.</summary>
    private sealed class ProviderKeysState
    {
        public ProviderKey NewProvider = ProviderKey.OpenAI;
        public string? AddStatus;
        public readonly Dictionary<ProviderKey, string> RowStatus = [];
        public readonly HashSet<ProviderKey> Checking = [];
    }

    private readonly ProviderKeysState keysState = new();

    private UIElement? KeysRow(SettingsItem item, string query)
    {
        switch (item.Id)
        {
            case "account.keys":
                LiveContent? list = null;
                list = new LiveContent(() => ProviderKeysList(query, () => list?.Rebuild()), Changes.Shell, Changes.Of(Settings, nameof(Core.Dictation.DictationSettings.Region)));
                return list;
            case "account.claudeCodePlan":
                return new PlanSubscriptionRow(AnswerSource.ClaudeCodePlan, item, query, services);
            case "account.codexPlan":
                return new PlanSubscriptionRow(AnswerSource.CodexPlan, item, query, services);
            default:
                return null;
        }
    }

    /// <summary>
    /// Your keys: one row per saved key (Test, Remove) and an "Add a key" row whose menu holds the
    /// providers without one, so the list stays short however many providers there are.
    /// </summary>
    private StackPanel ProviderKeysList(string query, Action redraw)
    {
        var shell = OpenClickyConfiguration.Settings;
        var saved = ProviderKeys.Saved(shell);
        var unsaved = ProviderKeys.Unsaved(shell, Settings.Region);
        var stack = new StackPanel();
        foreach (var provider in saved)
        {
            var isChecking = keysState.Checking.Contains(provider);
            var detail = keysState.RowStatus.TryGetValue(provider, out var status) ? status : $"{provider.Detail()} {ProviderKeys.Masked(provider.SavedKey(shell)!)}";
            var testButton = Ui.Button(isChecking ? "Testing…" : "Test", () => TestKey(provider, redraw), isEnabled: !isChecking,
                toolTip: provider.SpeechVoiceId() is null ? "Ask it what colour a red square is" : "Have it say a word");
            var removeButton = Ui.Button("Remove", () =>
            {
                if (!SheetWindow.Confirm(actions.OwnerWindow, Settings, $"Remove the {provider.Title()} key?",
                        "It’s deleted from %USERPROFILE%\\.openclicky\\shell.json. Keep a copy if you’ll need it again.", "Remove")) return;
                SaveKey(provider, null);
                keysState.RowStatus.Remove(provider);
                keysState.AddStatus = $"{provider.Title()} key removed.";
                keysState.NewProvider = provider;
                redraw();
            });
            stack.Children.Add(Ui.Card(Ui.Row(provider.Title(), detail, query, Ui.HStack(8, testButton, removeButton))));
        }
        if (unsaved.Count == 0)
        {
            stack.Children.Add(Ui.Card(Ui.Row("Every provider has a key", keysState.AddStatus, query, null)));
            return stack;
        }
        if (!unsaved.Contains(keysState.NewProvider)) keysState.NewProvider = unsaved[0];
        var providerPicker = Ui.Picker(unsaved.Select(provider => (provider, provider.Title())), keysState.NewProvider, provider =>
        {
            keysState.NewProvider = provider;
            keysState.AddStatus = null;
            redraw();
        }, 170);
        var keyField = new WpfUi.PasswordBox { Width = 160, PlaceholderText = keysState.NewProvider.Placeholder(), VerticalAlignment = VerticalAlignment.Center };
        Ui.AutomationProperties(keyField, $"{keysState.NewProvider.Title()} key");
        var addButton = Ui.Button("Add", () => { }, WpfUi.ControlAppearance.Primary, isEnabled: false);
        void Add()
        {
            var trimmed = keyField.Password.Trim();
            if (trimmed.Length == 0) return;
            var provider = keysState.NewProvider;
            SaveKey(provider, trimmed);
            keysState.AddStatus = null;
            if (ProviderKeys.Unsaved(OpenClickyConfiguration.Settings, Settings.Region).FirstOrDefault() is var next) keysState.NewProvider = next;
            // Find out straight away whether it works.
            TestKey(provider, redraw);
        }
        keyField.PasswordChanged += (_, _) => addButton.IsEnabled = keyField.Password.Trim().Length > 0;
        keyField.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) Add(); };
        addButton.Click += (_, _) => Add();
        stack.Children.Add(Ui.Card(Ui.Row("Add a key", keysState.AddStatus ?? keysState.NewProvider.Detail(), query,
            Ui.HStack(8, providerPicker, keyField, addButton))));
        return stack;
    }

    private static void SaveKey(ProviderKey provider, string? key)
    {
        OpenClickyConfiguration.Update(shell => provider.Store(shell, key));
        // A changed key is a changed credential: a live realtime session reconnects with it.
        // TODO(integration): the Mac posted credentialsChangedNotification here; the settings-file
        // watcher raises OpenClickyConfiguration.CredentialsChanged once it sees the write.
        ShellSettingsRevision.NoteChanged();
    }

    private void TestKey(ProviderKey provider, Action redraw)
    {
        if (provider.SavedKey(OpenClickyConfiguration.Settings) is null) return;
        keysState.Checking.Add(provider);
        keysState.RowStatus[provider] = "Checking…";
        redraw();
        _ = RunTestAsync();

        async Task RunTestAsync()
        {
            string sentence;
            try
            {
                if (provider.AnswerSource() is { } source) sentence = await services.Services.TestAnswerSourceAsync(source);
                // A voice key is checked by having it say one word.
                else if (provider.SpeechVoiceId() is { } voiceId) sentence = await services.Services.TestVoiceKeyAsync(voiceId);
                else sentence = "Saved.";
            }
            catch (Exception error)
            {
                sentence = error.Message;
            }
            keysState.RowStatus[provider] = sentence;
            keysState.Checking.Remove(provider);
            redraw();
        }
    }

    /// <summary>A plan the person already pays for: whether its CLI is here and signed in, and the switch that lets it answer.</summary>
    private sealed class PlanSubscriptionRow : ContentControl
    {
        private readonly AnswerSource plan;
        private readonly SettingsItem item;
        private readonly string query;
        private readonly MainWindowServices services;
        private PlanCliStatus? status;
        private string? checkSentence;
        private bool isChecking;

        public PlanSubscriptionRow(AnswerSource plan, SettingsItem item, string query, MainWindowServices services)
        {
            this.plan = plan;
            this.item = item;
            this.query = query;
            this.services = services;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Render();
            Loaded += async (_, _) =>
            {
                try { status = await services.Services.PlanStatusAsync(plan); }
                catch (Exception error) { AppLog.Write($"plans: status failed: {error.Message}"); status = PlanCliStatus.NotInstalled; }
                Render();
            };
        }

        private void Render()
        {
            var isOn = PlanSubscription.IsOn(plan, OpenClickyConfiguration.Settings);
            var detail = checkSentence ?? PlanSubscription.Detail(plan, status, isOn);
            var controls = Ui.HStack(8,
                status == PlanCliStatus.Ready ? Ui.Button(isChecking ? "Testing…" : "Test", Test, isEnabled: !isChecking, toolTip: "Ask it what colour a red square is") : null,
                status == PlanCliStatus.NotInstalled ? Ui.Button("Get It ↗", () => Ui.OpenExternal(PlanSubscription.GetItUrl(plan))) : null,
                Ui.Toggle(isOn, newValue =>
                {
                    OpenClickyConfiguration.Update(shell =>
                    {
                        if (plan == AnswerSource.ClaudeCodePlan) shell.UseClaudeCodePlan = newValue;
                        else shell.UseCodexPlan = newValue;
                    });
                    ShellSettingsRevision.NoteChanged();
                    Render();
                }, isEnabled: status == PlanCliStatus.Ready || isOn));
            Content = Ui.Card(Ui.Row(item.Title, detail, query, controls));
        }

        private async void Test()
        {
            isChecking = true;
            checkSentence = "Checking… a plan takes a few seconds.";
            Render();
            try { checkSentence = await services.Services.TestAnswerSourceAsync(plan); }
            catch (Exception error) { checkSentence = error.Message; }
            isChecking = false;
            Render();
        }
    }

    // MARK: - Account

    private UIElement? AccountRow(SettingsItem item, string query)
    {
        switch (item.Id)
        {
            case "account.profile":
                return new AccountProfileRow(services, actions);
            case "account.plan":
                return new PlanSummaryRow(item, query);
            case "account.usage.offline":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Tag("Free", isPositive: true)));
            case "account.usage.sarvam":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("Dashboard ↗", () => Ui.OpenExternal("https://dashboard.sarvam.ai"))));
            case "account.usage.backend":
                return new LiveContent(() =>
                {
                    var (detail, badge) = AccountRows.BackendEngineUsage(OpenClickyConfiguration.UsesOwnKeys, OpenClickyConfiguration.HasAccountToken);
                    var badgeText = Ui.Text(badge, 12, SettingsLook.TextSecondary, wrap: false);
                    badgeText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    return Ui.Card(Ui.Row(item.Title, detail, query, badgeText));
                }, Changes.Shell);
            case "account.backendHost":
                return new LiveContent(() =>
                {
                    var button = Ui.Button("Show shell.json", () => Ui.RevealInExplorer(OpenClickyConfiguration.SettingsFilePath), toolTip: OpenClickyConfiguration.BackendBaseUrl);
                    return Ui.Card(Ui.Row(item.Title, OpenClickyConfiguration.BackendHostDescription, query, button));
                }, Changes.Shell);
            case "account.backendToken":
                return new LiveContent(() =>
                {
                    var token = Ui.Text(AccountRows.MaskedToken(OpenClickyConfiguration.Token), 12, SettingsLook.TextSecondary, wrap: false);
                    token.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    return Ui.Card(Ui.Row(item.Title, item.Detail, query, token));
                }, Changes.Shell);
            default:
                return null;
        }
    }

    /// <summary>The "Free allowance" row: the meter, or why there is none.</summary>
    private sealed class PlanSummaryRow : ContentControl
    {
        private readonly BillingStatusModel billing = new();
        private readonly SettingsItem item;
        private readonly string query;
        private bool showsDetails;

        public PlanSummaryRow(SettingsItem item, string query)
        {
            this.item = item;
            this.query = query;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            billing.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Render);
            Render();
            Loaded += (_, _) =>
            {
                billing.Refresh();
                ShellSettingsRevision.Changed += OnShellChanged;
            };
            Unloaded += (_, _) => ShellSettingsRevision.Changed -= OnShellChanged;
        }

        private void OnShellChanged() => Dispatcher.BeginInvoke(() => { billing.Refresh(); Render(); });

        private void Render()
        {
            var state = AccountRows.PlanSummary(billing.Summary, billing.ErrorText, OpenClickyConfiguration.UsesOwnKeys, OpenClickyConfiguration.HasAccountToken);
            var header = new DockPanel();
            var trailing = Ui.Text(state.Trailing, 12, SettingsLook.TextSecondary, wrap: false);
            DockPanel.SetDock(trailing, Dock.Right);
            header.Children.Add(trailing);
            header.Children.Add(Ui.Highlighted(item.Title, query));
            var stack = new StackPanel();
            stack.Children.Add(header);
            if (state.FractionUsed is { } fraction)
            {
                stack.Children.Add(new ProgressBar { Maximum = 1, Value = fraction, Height = 4, Margin = new Thickness(0, 10, 0, 2) });
            }
            if (state.Caption is { } caption)
            {
                var captionText = Ui.Text(caption, SettingsLook.CaptionSize, SettingsLook.TextSecondary);
                captionText.Margin = new Thickness(0, 4, 0, 0);
                stack.Children.Add(captionText);
            }
            if (state.Details is { } details)
            {
                var expander = new Expander
                {
                    Header = Ui.Text("Details", SettingsLook.CaptionSize, SettingsLook.TextSecondary),
                    Content = Ui.Text(details, SettingsLook.CaptionSize, SettingsLook.TextSecondary),
                    IsExpanded = showsDetails,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                expander.Expanded += (_, _) => showsDetails = true;
                expander.Collapsed += (_, _) => showsDetails = false;
                stack.Children.Add(expander);
            }
            Content = Ui.Card(stack);
        }
    }
}
