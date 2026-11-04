// Settings → Clicky → Models: the person's own models (Ollama, LM Studio, any OpenAI-compatible server), each turned
// on by its address and connected by listing the models it serves, and who writes the drawn answers (the drawing
// stage), which may be a different source and model from the one that answers questions.

using System.Windows;
using System.Windows.Controls;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    /// <summary>What the models rows remember between redraws: each source's models and what the last Connect said.</summary>
    private sealed class SelfHostedState
    {
        public readonly Dictionary<AnswerSource, IReadOnlyList<string>> Models = [];
        public readonly Dictionary<AnswerSource, string> Status = [];
        public readonly HashSet<AnswerSource> Connecting = [];
    }

    private readonly SelfHostedState selfHosted = new();

    private UIElement? ModelsRow(SettingsItem item, string query)
    {
        switch (item.Id)
        {
            case "models.ollama":
                return SelfHostedRow(AnswerSource.Ollama, item, query);
            case "models.lmStudio":
                return SelfHostedRow(AnswerSource.LMStudio, item, query);
            case "models.custom":
                return SelfHostedRow(AnswerSource.CustomOpenAI, item, query);
            case "clicky.drawingSource":
                return new LiveContent(() => Ui.Card(DrawingSourceRow(item, query)), Changes.Shell);
            default:
                return null;
        }
    }

    private LiveContent SelfHostedRow(AnswerSource source, SettingsItem item, string query)
    {
        LiveContent? row = null;
        row = new LiveContent(() => Ui.Card(SelfHostedContent(source, item, query, () => row?.Rebuild())), Changes.Shell);
        return row;
    }

    private UIElement SelfHostedContent(AnswerSource source, SettingsItem item, string query, Action redraw)
    {
        var shell = OpenClickyConfiguration.Settings;
        var savedUrl = ProviderCredentials.BaseUrl(SavedUrl(shell, source));
        var model = shell.AnswerModels is { } models && models.TryGetValue(source.Id(), out var picked) ? picked : null;
        var isConnecting = selfHosted.Connecting.Contains(source);
        var detail = selfHosted.Status.TryGetValue(source, out var status) ? status
            : savedUrl is null ? item.Detail
            : $"On at {savedUrl} · {model ?? "its first model"}";

        var urlField = new WpfUi.TextBox
        {
            Width = 190, Text = savedUrl ?? "", PlaceholderText = source.DefaultBaseUrl() ?? "https://your-server/v1", VerticalAlignment = VerticalAlignment.Center,
        };
        Ui.AutomationProperties(urlField, $"{source.ShortName()} address");
        WpfUi.PasswordBox? keyField = null;
        if (source == AnswerSource.CustomOpenAI)
        {
            keyField = new WpfUi.PasswordBox { Width = 150, PlaceholderText = "key (optional)", Password = shell.CustomOpenAIKey ?? "", VerticalAlignment = VerticalAlignment.Center };
            Ui.AutomationProperties(keyField, "Your server's key");
        }

        async void Connect()
        {
            var typed = urlField.Text.Trim().Length > 0 ? urlField.Text.Trim() : source.DefaultBaseUrl() ?? "";
            if (ProviderCredentials.BaseUrl(typed) is not { } baseUrl)
            {
                selfHosted.Status[source] = "That isn't an http:// or https:// address.";
                redraw();
                return;
            }
            var key = keyField?.Password.Trim();
            selfHosted.Connecting.Add(source);
            selfHosted.Status[source] = $"Connecting to {baseUrl}…";
            redraw();
            try
            {
                var served = await LocalModels.ListAsync(baseUrl, key);
                selfHosted.Models[source] = served;
                OpenClickyConfiguration.Update(saved =>
                {
                    SetSavedUrl(saved, source, baseUrl);
                    if (source == AnswerSource.CustomOpenAI) saved.CustomOpenAIKey = string.IsNullOrEmpty(key) ? null : key;
                    // A model that is gone (or none yet): the one it would pick itself, so the menu shows what answers.
                    var current = saved.AnswerModels is { } chosen && chosen.TryGetValue(source.Id(), out var name) ? name : null;
                    if (current is null || !served.Contains(current))
                    {
                        saved.AnswerModels ??= [];
                        saved.AnswerModels[source.Id()] = LocalModels.DefaultModel(served)!;
                    }
                });
                ShellSettingsRevision.NoteChanged();
                selfHosted.Status[source] = $"Connected: {served.Count} model{(served.Count == 1 ? "" : "s")}. Pick it under “Answers come from” or “Drawings come from”.";
            }
            catch (LocalModelsException failure)
            {
                selfHosted.Status[source] = Capitalized(failure.Message);
            }
            // This runs from a click (async void): anything else, saving shell.json included, is shown on the row
            // instead of ending the app.
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                AppLog.Write($"settings: connecting {source.Id()} failed ({failure.GetType().Name}: {failure.Message})");
                selfHosted.Status[source] = $"Couldn't turn it on: {failure.Message}";
            }
            finally
            {
                selfHosted.Connecting.Remove(source);
                redraw();
            }
        }

        var connectButton = Ui.Button(isConnecting ? "Connecting…" : savedUrl is null ? "Turn on" : "Refresh", Connect,
            savedUrl is null ? WpfUi.ControlAppearance.Primary : WpfUi.ControlAppearance.Secondary, isEnabled: !isConnecting,
            toolTip: "Save the address and list the models it serves");
        urlField.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) Connect(); };

        UIElement? modelPicker = null;
        var known = selfHosted.Models.TryGetValue(source, out var list) ? list : null;
        if (savedUrl is not null && known is { Count: > 0 })
        {
            var options = known.Select(name => (name, LocalModels.LooksLikeItSeesImages(name) ? $"{name} (sees images)" : name)).ToList();
            modelPicker = Ui.Picker(options, model is not null && known.Contains(model) ? model : known[0], choice =>
            {
                OpenClickyConfiguration.Update(saved =>
                {
                    saved.AnswerModels ??= [];
                    saved.AnswerModels[source.Id()] = choice;
                });
                ShellSettingsRevision.NoteChanged();
            }, 170);
            Ui.AutomationProperties(modelPicker, $"{source.ShortName()} model");
        }
        var offButton = savedUrl is null ? null : Ui.Button("Turn off", () =>
        {
            OpenClickyConfiguration.Update(saved => SetSavedUrl(saved, source, null));
            ShellSettingsRevision.NoteChanged();
            selfHosted.Models.Remove(source);
            selfHosted.Status.Remove(source);
            redraw();
        });
        return Ui.Row(item.Title, detail, query, Ui.HStack(8, urlField, keyField, modelPicker, connectButton, offButton));
    }

    private static string? SavedUrl(OpenClickyShellSettings shell, AnswerSource source) => source switch
    {
        AnswerSource.Ollama => shell.OllamaUrl,
        AnswerSource.LMStudio => shell.LMStudioUrl,
        AnswerSource.CustomOpenAI => shell.CustomOpenAIUrl,
        _ => null,
    };

    private static void SetSavedUrl(OpenClickyShellSettings shell, AnswerSource source, string? url)
    {
        switch (source)
        {
            case AnswerSource.Ollama: shell.OllamaUrl = url; break;
            case AnswerSource.LMStudio: shell.LMStudioUrl = url; break;
            case AnswerSource.CustomOpenAI: shell.CustomOpenAIUrl = url; break;
        }
    }

    private static string Capitalized(string sentence) => sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];

    /// <summary>Who writes drawings: the same source as answers, or any one set up, and a model of its own (blank: its usual one).</summary>
    private static Grid DrawingSourceRow(SettingsItem item, string query)
    {
        var shell = OpenClickyConfiguration.Settings;
        var available = ProviderCredentials.Current().AnswerSourcesInOrder;
        var picked = AnswerSources.FromRawValue(shell.DrawingSource);
        var options = new List<(string Id, string Title)> { ("same", "Same as answers") };
        options.AddRange(available.Select(source => (source.Id(), source.DisplayName())));
        if (picked is { } stale && !available.Contains(stale)) options.Add((stale.Id(), $"{stale.DisplayName()} (not set up)"));
        var picker = Ui.Picker(options, picked?.Id() ?? "same", choice =>
        {
            OpenClickyConfiguration.Update(saved => saved.DrawingSource = choice == "same" ? null : choice);
            ShellSettingsRevision.NoteChanged();
        }, 200);
        var modelField = new WpfUi.TextBox
        {
            Width = 170, Text = shell.DrawingModel ?? "", PlaceholderText = "its usual model", VerticalAlignment = VerticalAlignment.Center,
        };
        Ui.AutomationProperties(modelField, "Drawing model");
        void SaveModel()
        {
            var typed = modelField.Text.Trim();
            if (typed == (OpenClickyConfiguration.Settings.DrawingModel ?? "")) return;
            OpenClickyConfiguration.Update(saved => saved.DrawingModel = typed.Length == 0 ? null : typed);
            ShellSettingsRevision.NoteChanged();
        }
        modelField.LostFocus += (_, _) => SaveModel();
        modelField.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) SaveModel(); };
        return Ui.Row(item.Title, item.Detail, query, Ui.HStack(8, picker, modelField));
    }

}
