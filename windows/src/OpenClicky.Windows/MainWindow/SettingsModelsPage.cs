// Settings → Clicky → Models: the person's own models (Ollama, LM Studio, any OpenAI-compatible server), each turned
// on by its address and connected by listing the models it serves. Settings → Clicky → Drawings: who writes the drawn
// answers (the drawing stage), which may be a different source and model from the one that answers questions, how
// detailed they are, and the person's own instructions for them.

using System.Windows;
using System.Windows.Controls;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.DrawStage;
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
            case "clicky.drawingDetail":
                return Ui.Card(DrawingDetailRow(item, query));
            case "clicky.drawingQuiz":
                return Ui.Card(DrawingQuizRow(item, query));
            case "clicky.drawingInstructions":
                return Ui.Card(DrawingInstructionsRow(item, query));
            case "clicky.markerShortcut":
                return Ui.Card(MarkerShortcutRow(item, query));
            case "clicky.drawingScriptFont":
                return new LiveContent(() => Ui.Card(ScriptFontRow(item, query)), Changes.Shell);
            case "clicky.pieceMaker":
                return new LiveContent(() => Ui.Card(PieceMakerRow(item, query)), Changes.Shell);
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

    /// <summary>How detailed drawings are: simple, detailed or very detailed (DrawStageLesson.DetailFrom).</summary>
    private static Grid DrawingDetailRow(SettingsItem item, string query)
    {
        var current = DrawStageLesson.DetailFrom(OpenClickyConfiguration.Settings.DrawingDetail);
        var picker = Ui.Picker(
            [(DrawingDetail.Simple, "Simple"), (DrawingDetail.Detailed, "Detailed"), (DrawingDetail.VeryDetailed, "Very detailed")],
            current,
            choice =>
            {
                OpenClickyConfiguration.Update(saved => saved.DrawingDetail = choice == DrawingDetail.Detailed ? null : DrawStageLesson.RawValue(choice));
                ShellSettingsRevision.NoteChanged();
            },
            200);
        return Ui.Row(item.Title, item.Detail, query, picker);
    }

    /// <summary>A quiz after a drawing: off, a Quiz me button on it, or always (DrawStageLesson.QuizFrom).</summary>
    private static Grid DrawingQuizRow(SettingsItem item, string query)
    {
        var current = DrawStageLesson.QuizFrom(OpenClickyConfiguration.Settings.DrawingQuiz);
        var picker = Ui.Picker(
            [(DrawingQuiz.Off, "Off"), (DrawingQuiz.Offer, "Offer (a Quiz me button)"), (DrawingQuiz.Always, "Always")],
            current,
            choice =>
            {
                OpenClickyConfiguration.Update(saved => saved.DrawingQuiz = choice == DrawingQuiz.Offer ? null : DrawStageLesson.RawValue(choice));
                ShellSettingsRevision.NoteChanged();
            },
            220);
        return Ui.Row(item.Title, item.Detail, query, picker);
    }

    /// <summary>The font Urdu words are drawn in: the bundled Noto Nastaliq Urdu, Windows' own, or one installed on this PC
    /// (its name in a box beside the picker).</summary>
    private static Grid ScriptFontRow(SettingsItem item, string query)
    {
        var saved = OpenClickyConfiguration.Settings.DrawingScriptFont?.Trim();
        var choice = saved switch { null or "" => "noto", "system" => "system", _ => "installed" };
        var nameField = new WpfUi.TextBox
        {
            Width = 190, Text = choice == "installed" ? saved! : "", PlaceholderText = "IndoPak Nastaleeq", VerticalAlignment = VerticalAlignment.Center,
            Visibility = choice == "installed" ? Visibility.Visible : Visibility.Collapsed,
        };
        Ui.AutomationProperties(nameField, "Installed font's name");
        void SaveName()
        {
            var typed = nameField.Text.Trim();
            if (typed.Length == 0 || typed == OpenClickyConfiguration.Settings.DrawingScriptFont) return;
            OpenClickyConfiguration.Update(shell => shell.DrawingScriptFont = typed);
            ShellSettingsRevision.NoteChanged();
        }
        nameField.LostFocus += (_, _) => SaveName();
        nameField.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) SaveName(); };
        var picker = Ui.Picker([("noto", "Noto Nastaliq Urdu"), ("system", "Windows' own (Segoe UI)"), ("installed", "A font on this PC")], choice, picked =>
        {
            nameField.Visibility = picked == "installed" ? Visibility.Visible : Visibility.Collapsed;
            if (picked == "installed") { nameField.Focus(); return; }
            OpenClickyConfiguration.Update(shell => shell.DrawingScriptFont = picked == "system" ? "system" : null);
            ShellSettingsRevision.NoteChanged();
        }, 200);
        return Ui.Row(item.Title, item.Detail, query, Ui.HStack(8, picker, nameField));
    }

    /// <summary>The keys that start a mark from anywhere, or none (the island's marker button always works).</summary>
    private static Grid MarkerShortcutRow(SettingsItem item, string query)
    {
        var picker = Ui.Picker(MarkerShortcuts.Choices.Select(each => (each.Shortcut, each.Title)),
            MarkerShortcuts.From(OpenClickyConfiguration.Settings.MarkerShortcut),
            choice =>
            {
                OpenClickyConfiguration.Update(saved => saved.MarkerShortcut = MarkerShortcuts.RawValue(choice));
                ShellSettingsRevision.NoteChanged();
            },
            200);
        return Ui.Row(item.Title, item.Detail, query, picker);
    }

    /// <summary>The graphics card, read once (the registry doesn't change while Settings is open).</summary>
    private static readonly Lazy<(string Name, double MemoryGB)?> GraphicsCard = new(Platform.GraphicsCards.Biggest);

    /// <summary>Who makes 3D pieces: the picker, its address and model (for the makers that need them), and what this PC's
    /// graphics card suits.</summary>
    private static UIElement PieceMakerRow(SettingsItem item, string query)
    {
        var shell = OpenClickyConfiguration.Settings;
        var kind = PieceMakers.KindFrom(shell.PieceMaker);
        var choice = PieceMakers.Choices.First(each => each.Kind == kind);
        var picker = Ui.Picker(PieceMakers.Choices.Select(each => (each.Kind, each.Title)), kind, picked =>
        {
            OpenClickyConfiguration.Update(saved =>
            {
                saved.PieceMaker = picked == PieceMakerKind.DrawingModel ? null : PieceMakers.RawValue(picked);
                // Each maker has its own address and model: a new one starts from its own defaults.
                saved.PieceMakerUrl = null;
                saved.PieceMakerModel = null;
            });
            ShellSettingsRevision.NoteChanged();
        }, 220);
        var stack = new StackPanel();
        stack.Children.Add(Ui.Row(item.Title, item.Detail, query, picker));
        stack.Children.Add(Caption(choice.Needs));
        if (kind != PieceMakerKind.DrawingModel)
        {
            var urlField = new WpfUi.TextBox
            {
                Width = 240, Text = shell.PieceMakerUrl ?? "", PlaceholderText = PieceMakers.DefaultUrl(kind), VerticalAlignment = VerticalAlignment.Center,
            };
            Ui.AutomationProperties(urlField, "3D piece maker address");
            void SaveUrl()
            {
                var typed = urlField.Text.Trim();
                if (typed == (OpenClickyConfiguration.Settings.PieceMakerUrl ?? "")) return;
                OpenClickyConfiguration.Update(saved => saved.PieceMakerUrl = typed.Length == 0 ? null : typed);
                ShellSettingsRevision.NoteChanged();
            }
            urlField.LostFocus += (_, _) => SaveUrl();
            urlField.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) SaveUrl(); };
            var fields = Ui.HStack(8, urlField);
            if (kind is PieceMakerKind.LlamaMeshOllama or PieceMakerKind.LlamaMeshLMStudio)
            {
                var modelField = new WpfUi.TextBox
                {
                    Width = 280, Text = shell.PieceMakerModel ?? "", VerticalAlignment = VerticalAlignment.Center,
                    PlaceholderText = kind == PieceMakerKind.LlamaMeshOllama ? PieceMakers.DefaultLlamaMeshModel : "its model name in LM Studio",
                };
                Ui.AutomationProperties(modelField, "LLaMA-Mesh model");
                void SaveModel()
                {
                    var typed = modelField.Text.Trim();
                    if (typed == (OpenClickyConfiguration.Settings.PieceMakerModel ?? "")) return;
                    OpenClickyConfiguration.Update(saved => saved.PieceMakerModel = typed.Length == 0 ? null : typed);
                    ShellSettingsRevision.NoteChanged();
                }
                modelField.LostFocus += (_, _) => SaveModel();
                modelField.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) SaveModel(); };
                fields.Children.Add(modelField);
            }
            fields.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(fields);
        }
        var card = GraphicsCard.Value;
        var grade = PieceMakers.GradeFor(card?.MemoryGB);
        var (suggested, why) = PieceMakers.Recommend(grade);
        var gradeName = grade switch { GraphicsGrade.High => "high", GraphicsGrade.Medium => "medium", _ => "low" };
        var cardLine = card is { } found ? $"This PC: {found.Name}, {found.MemoryGB:0.#} GB ({gradeName})." : "This PC: no graphics card with its own memory found (low).";
        var suggestion = suggested == kind ? "That's the one suggested for it." : $"Suggested: {PieceMakers.Choices.First(each => each.Kind == suggested).Title}.";
        stack.Children.Add(Caption($"{cardLine} {why} {suggestion} A maker on another PC (its address above) uses that PC's card instead."));
        return stack;
    }

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = SettingsLook.CaptionSize, Margin = new Thickness(0, 6, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, SettingsLook.TextSecondary);
        return block;
    }

    /// <summary>The person's own words added to every drawing's instructions, saved when the box loses focus.</summary>
    private static UIElement DrawingInstructionsRow(SettingsItem item, string query)
    {
        var box = new WpfUi.TextBox
        {
            Text = OpenClickyConfiguration.Settings.DrawingInstructions ?? "",
            PlaceholderText = "e.g. Use bright colours. Show what's inside with a cutaway. Explain for a 9-year-old.",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = DrawStageLesson.LongestInstructions,
            Margin = new Thickness(0, 10, 0, 0),
        };
        Ui.AutomationProperties(box, "Your drawing instructions");
        box.LostFocus += (_, _) =>
        {
            var typed = box.Text.Trim();
            if (typed == (OpenClickyConfiguration.Settings.DrawingInstructions ?? "")) return;
            OpenClickyConfiguration.Update(saved => saved.DrawingInstructions = typed.Length == 0 ? null : typed);
            ShellSettingsRevision.NoteChanged();
        };
        var stack = new StackPanel();
        stack.Children.Add(Ui.Row(item.Title, item.Detail, query, null));
        stack.Children.Add(box);
        return stack;
    }
}
