//
//  TeacherSketchWriter.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherSketchWriter.swift)
//
//  The Realtime voice model talks well and draws badly: asked for refraction it ran the incoming
//  ray straight down the normal. So on that lane it only says what to draw, and a text model
//  writes the drawing while the voice is already explaining. On an own OpenAI key that is gpt-5.5
//  with reasoning off (refraction correct in ~3.5 s, where gpt-4.1 at 2 s piled labels on each
//  other and gpt-5.4-mini barely bent the ray; measured on the Mac 2026-10-23).
//
//  It also writes the lessons the app starts itself (TeacherPlugin.StartLesson), so it has to
//  work without OpenAI too, since most people have no key: an Anthropic key gets Claude Sonnet 5.5
//  with thinking off (3-4 s and the clearest drawings of those measured; Haiku 4.5 overlapped
//  its labels), a free account gets the backend's /chat (which keeps the request for no thinking
//  in the form its model accepts), and another API key its own provider. Plans (the Claude Code
//  and Codex CLIs) take 15 s or more to start, so they never start a lesson early.
//

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Plugins.Teacher;

public static class TeacherSketchWriter
{
    public const string OpenAIModel = "gpt-5.5";
    public const string AnthropicModel = "claude-sonnet-5-5";
    /// <summary>The output a lesson needs: 25 commands is 400-700 tokens. The backend caps an account at this.</summary>
    public const int MaximumOutputTokens = 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Where a lesson is written, best first.</summary>
    public abstract record Route
    {
        public sealed record OpenAI(string Key) : Route;
        public sealed record Anthropic(string Key) : Route;
        /// <summary>The free account: the backend's /chat on the account token.</summary>
        public sealed record Account : Route;
        /// <summary>Another API key the ask lane answers with (OpenRouter, Gemini, Groq, Mistral, xAI).</summary>
        public sealed record AnswerSourceRoute(AnswerSource Source) : Route;
    }

    /// <summary>Null when the only way to write is a plan, or nothing at all.</summary>
    public static Route? RouteFor(ProviderCredentials credentials)
    {
        if (credentials.OpenAIKey is { } openAIKey) return new Route.OpenAI(openAIKey);
        if (credentials.AnthropicKey is { } anthropicKey) return new Route.Anthropic(anthropicKey);
        if (credentials.AccountToken is not null) return new Route.Account();
        var fastSources = credentials.AnswerSourcesInOrder.Where(source => source is not (AnswerSource.ClaudeCodePlan or AnswerSource.CodexPlan
            or AnswerSource.OpenClickyAccount or AnswerSource.Anthropic or AnswerSource.OpenAI));
        return fastSources.Select(source => (Route?)new Route.AnswerSourceRoute(source)).FirstOrDefault();
    }

    public static string SystemPrompt =>
        "You draw teaching diagrams on a whiteboard for a school student. Reply with only the drawing commands, nothing else: no code fences, no explanation.\n"
        + TeacherPlugin.SketchGrammar;

    public static string UserPrompt(string title, string whatToDraw) => $"Draw: {whatToDraw}\nTitle: {title}";

    /// <summary>For a question nobody has described a drawing for yet (TeacherPlugin.StartLesson).</summary>
    public static string UserPrompt(string question, bool withScreenshot) =>
        $"The student asked: \"{question}\""
        + (withScreenshot ? "\nThe attached screenshot is what they are looking at; \"this\" means what is on it." : "")
        + "\nDraw the one diagram a good teacher would put on the whiteboard to answer it, with a short title at the top. "
        + "If they ask to solve or work something out, write the worked solution instead: the problem first, then one "
        + "short line per step, in order, left-aligned under each other (text with the option left), the answer last in red. "
        + "If no simple diagram can show it well (a real animal, an organ, a place, a painting, a photo of a machine), "
        + "reply with exactly one line instead: PICTURE: <a short search phrase in English>";

    /// <summary>What the writer answered: drawing commands, or a picture to search for.</summary>
    public abstract record Reply
    {
        public sealed record Sketch(string Commands) : Reply;
        public sealed record Picture(string Query) : Reply;

        public static Reply Parse(string text)
        {
            var trimmed = WithoutCodeFences(text).Trim();
            if (trimmed.StartsWith("PICTURE:", StringComparison.OrdinalIgnoreCase)) return new Picture(trimmed["PICTURE:".Length..].Trim());
            return new Sketch(trimmed);
        }
    }

    /// <summary>The drawing commands for what the voice lane described, or null when no model could write them.</summary>
    public static async Task<string?> WriteAsync(string title, string whatToDraw) =>
        await CompleteAsync(UserPrompt(title, whatToDraw), screenshotJpeg: null) is { } reply ? WithoutCodeFences(reply) : null;

    /// <summary>The lesson for a question: drawing commands or a picture search; null when no model answered.
    /// <paramref name="screenshotJpeg"/> goes along when the question is about the screen ("solve this").</summary>
    public static async Task<Reply?> LessonAsync(string question, byte[]? screenshotJpeg) =>
        await CompleteAsync(UserPrompt(question, screenshotJpeg is not null), screenshotJpeg) is { } reply ? Reply.Parse(reply) : null;

    /// <summary>Wherever a lesson can be written in a few seconds; a plan alone would race the model's own drawing for nothing.</summary>
    public static bool CanStartLessonsEarly => RouteFor(ProviderRouter.CurrentCredentials()) is not null;

    private static async Task<string?> CompleteAsync(string userPrompt, byte[]? screenshotJpeg)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var route = RouteFor(ProviderRouter.CurrentCredentials());
        string? reply;
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            switch (route)
            {
                case Route.OpenAI openAI:
                    reply = await SendAsync(DirectProviderRequests.OpenAIChatCompletions(openAI.Key, OpenAIBody(userPrompt, screenshotJpeg)),
                        goesToBackend: false, ChatCompletionText, timeout.Token);
                    break;
                case Route.Anthropic anthropic:
                    reply = await SendAsync(DirectProviderRequests.AnthropicMessages(anthropic.Key, ClaudeBody(userPrompt, screenshotJpeg)),
                        goesToBackend: false, DirectProviderRequests.AnthropicReplyText, timeout.Token);
                    break;
                case Route.Account:
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{OpenClickyConfiguration.BackendBaseUrl}/chat")
                    {
                        Content = DirectProviderRequests.JsonContent(ClaudeBody(userPrompt, screenshotJpeg)),
                    };
                    OpenClickyConfiguration.Authorize(request);
                    // Null at max_tokens: half a drawing is not drawn.
                    reply = await SendAsync(request, goesToBackend: true, DirectProviderRequests.AnthropicReplyText, timeout.Token);
                    break;
                }
                default:
                {
                    // ClaudeAPI hands these to the source's answer provider (1,024 tokens); a plan is slow but
                    // still draws what the Realtime lane described.
                    var answers = new ClaudeAPI(() => $"{OpenClickyConfiguration.BackendBaseUrl}/chat");
                    IReadOnlyList<(byte[] Data, string Label)> images = screenshotJpeg is null ? [] : [(screenshotJpeg, "the student's screen")];
                    var (text, _) = await answers.AnalyzeImageAsync(images, SystemPrompt, null, userPrompt, timeout.Token);
                    reply = string.IsNullOrWhiteSpace(text) ? null : text;
                    break;
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"teacher: sketch writer failed ({error.GetType().Name})");
            reply = null;
        }
        AppLog.Write($"teacher: sketch {(reply is null ? "not written" : "written")} via {RouteName(route)} in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s");
        return reply;
    }

    private static async Task<string?> SendAsync(HttpRequestMessage request, bool goesToBackend, Func<byte[], string?> readReply, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = goesToBackend
                ? await BackendSignIn.SendAsync(request, sent => HttpClients.Shared.SendAsync(sent, cancellationToken))
                : await HttpClients.Shared.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode != 200)
            {
                AppLog.Write($"teacher: sketch writer refused ({(int)response.StatusCode})");
                return null;
            }
            return readReply(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }
    }

    public static string RouteName(Route? route) => route switch
    {
        Route.OpenAI => $"openai {OpenAIModel}",
        Route.Anthropic => $"anthropic {AnthropicModel}",
        Route.Account => "the account",
        Route.AnswerSourceRoute answerSource => answerSource.Source.RawValue(),
        _ => "the answer source",
    };

    /// <summary>The Messages body for a lesson. Thinking is turned off: Sonnet 5.5 thinks by default and spent the whole
    /// budget before writing a stroke (it takes "between_tools", not "disabled"). On the account the backend picks the
    /// model and maps this to what that model accepts.</summary>
    public static JsonObject ClaudeBody(string userPrompt, byte[]? screenshotJpeg)
    {
        var content = new JsonArray();
        if (screenshotJpeg is not null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(screenshotJpeg) },
            });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt });
        return new JsonObject
        {
            ["model"] = AnthropicModel,
            ["max_tokens"] = MaximumOutputTokens,
            ["thinking"] = new JsonObject { ["type"] = "between_tools" },
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
    }

    /// <summary>The Chat Completions body for a lesson on an own OpenAI key: gpt-5.5 with reasoning off.</summary>
    public static JsonObject OpenAIBody(string userPrompt, byte[]? screenshotJpeg) => new()
    {
        ["model"] = OpenAIModel,
        ["reasoning_effort"] = "none",
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = UserContent(userPrompt, screenshotJpeg) }),
    };

    /// <summary>Plain text, or text plus the screenshot as a data URL (Chat Completions' image input).</summary>
    public static JsonNode UserContent(string userPrompt, byte[]? screenshotJpeg)
    {
        if (screenshotJpeg is null) return JsonValue.Create(userPrompt);
        return new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = userPrompt },
            new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:image/jpeg;base64,{Convert.ToBase64String(screenshotJpeg)}" },
            });
    }

    public static string? ChatCompletionText(byte[] data)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return null; }
        if ((root?["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice) return null;
        if ((choice["message"] as JsonObject)?["content"] is not JsonValue value || !value.TryGetValue<string>(out var content)) return null;
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>Models sometimes wrap the commands in ``` fences anyway.</summary>
    public static string WithoutCodeFences(string text) =>
        string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Where(line => !line.Trim().StartsWith("```", StringComparison.Ordinal)));
}
