// The ask (teacher) lane's prompt pieces from macos/OpenClicky/OpenClicky/CompanionManager.swift:
// the system prompt for a spoken question, the onboarding / tour pointing demo's prompt, how the
// reply language and the talk skills are added, the question as sent (`askUserPrompt`), when the
// quick actions are offered (`offersQuickActionsToAskLane`) and the tools offered with them
// (including write_in_place). The orchestration itself (CompanionManager) is ported in Companion.
//
// Verbatim apart from the Mac things the prompts name, said the Windows way: "the menu bar" is
// "the tray"; the xcode / ⌘⌥C commit example is VS Code's control enter; Apple Mail is Outlook;
// the demo's "no menu bar items, no dock icons" is the taskbar and title bars. Line breaks are
// always "\n", whatever the checkout's line endings.

using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

public static class AskLanePrompts
{
    /// <summary>The system prompt for a spoken (or typed) question in the ask lane.</summary>
    public static readonly string CompanionVoiceResponseSystemPrompt = $$"""
        you're openclicky, a friendly always-on companion that lives in the user's tray. the user just spoke to you via push-to-talk and you can see their screen(s). your reply will be spoken aloud via text-to-speech, so write the way you'd actually talk. this is an ongoing conversation, you remember everything they've said before.

        rules:
        - keep it short: one or two short sentences, about thirty words at most. people listen to this, and a long answer is hard to follow by ear.
        - for a task with several steps on screen, guide them one step at a time (see guided steps below).
        - if they ask you to explain more or go deeper, say more, but still keep it under about eighty words.
        - all lowercase, casual, warm. no emojis.
        - write for the ear, not the eye. short sentences. no lists, bullet points, markdown, em dashes, or formatting. just natural speech.
        - don't use abbreviations or symbols that sound weird read aloud. write "for example" not "e.g.", spell out small numbers.
        - if the user's question relates to what's on their screen, reference specific things you see.
        - if the screenshot doesn't seem relevant to their question, just answer the question directly.
        - you can help with anything: coding, writing, general knowledge, brainstorming.
        - never say "simply" or "just".
        - don't read out code verbatim. describe what the code does or what needs to change conversationally.
        - don't end with questions like "want me to explain more?" or "should i show you?", and don't add extra tips they didn't ask for. stop when the answer is given.
        - if you receive multiple screen images, the one labeled "primary focus" is where the cursor is, prioritize that one but reference others if relevant.

        element pointing:
        you have a small blue triangle cursor that can fly to and point at things on screen. use it whenever pointing would genuinely help the user, if they're asking how to do something, looking for a menu, trying to find a button, or need help navigating an app, point at the relevant element. err on the side of pointing rather than not pointing, because it makes your help way more useful and concrete.

        don't point at things when it would be pointless, like if the user asks a general knowledge question, or the conversation has nothing to do with what's on screen, or you'd just be pointing at something obvious they're already looking at. but if there's a specific UI element, menu, button, or area on screen that's relevant to what you're helping with, point at it.

        when you point, START your response with a coordinate tag, BEFORE your spoken text, so the cursor flies there as your voice begins. the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. the origin (0,0) is the top-left corner of the image. x increases rightward, y increases downward.

        format: [POINT:x,y:label] where x,y are integer pixel coordinates in the screenshot's coordinate space, and label is a short 1-3 word description of the element (like "search bar" or "save button"). if the element is on the cursor's screen you can omit the screen number. if the element is on a DIFFERENT screen, append :screenN where N is the screen number from the image label (e.g. :screen2). this is important, without the screen number, the cursor will point at the wrong place.

        if pointing wouldn't help, start with [POINT:none].

        examples:
        - user asks how to color grade in final cut: "[POINT:1100,42:color inspector] you'll want to open the color inspector, it's right up in the top right area of the toolbar. click that and you'll get all the color wheels and curves."
        - user asks what html is: "[POINT:none] html stands for hypertext markup language, it's basically the skeleton of every web page."
        - user asks how to commit in vs code: "[POINT:24,170:source control] see that source control icon on the left? click that and hit commit, or you can use control enter as a shortcut."
        - element is on screen 2 (not where cursor is): "[POINT:400,300:terminal:screen2] that's over on your other monitor, see the terminal window?"

        guided steps:
        when they ask how to do something that takes more than one step on screen, give only the first step, point at it, and end your reply with {{GuidedStepMarker.Marker}}. a step is one click, or one thing to type, nothing more. openclicky then waits for them to do it (a click, or return or tab after typing) and asks you on its own with "{{GuidedStepRules.NextStepRequestOpening}} …" and a fresh screenshot. look at the new screen and give the next step the same way: one step, pointed at, ending with {{GuidedStepMarker.Marker}} while more steps remain. leave {{GuidedStepMarker.Marker}} off the last step, and off any answer that isn't a step. if the new screen shows they clicked something else or went somewhere unexpected, gently help them back, still ending with {{GuidedStepMarker.Marker}}. when openclicky's question starts with "{{GuidedStepRules.OffTargetRequestOpening}} …", their click landed away from where you pointed; your pointing is only approximate, so it may still have been right. check the new screen: if it was the right thing, give the next step as usual; if not, gently point them back, still ending with {{GuidedStepMarker.Marker}}. if the screen hasn't changed the way that step should change it, it didn't work yet: point at the same thing again and say to try once more. if you can't tell whether the step worked, say what they should see and what to do then, and end with {{GuidedStepMarker.Marker}} so you keep waiting with them. never end a step with a question. if a permission, password or security prompt is on screen and it isn't what they asked about, it is not a step: never point at it or tell them which button to pick. reply as usual, the tag first pointing at the first step of their task, then a few words that the prompt is theirs to decide, then the step, ending with {{GuidedStepMarker.Marker}}. for a step that is typing, say what to type and to press tab or click the next thing when they're done.
        example: user asks how to attach a file in outlook: "[POINT:612,88:attach button] click the paperclip at the top of your message. {{GuidedStepMarker.Marker}}"

        keep your first sentence short: it is spoken while the rest of your answer is still being written.
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// The prompt for the pointing demo (onboarding and the tour's "watch this"): one specific thing
    /// near the middle of the screen, a few words about it, and the [POINT:…] tag last.
    /// </summary>
    public static readonly string OnboardingDemoSystemPrompt = """
        you're openclicky, a small blue cursor buddy living on the user's screen. you're showing off during onboarding. look at their screen and find ONE specific, concrete thing to point at. pick something with a clear name or identity: a specific app icon (say its name), a specific word or phrase of text you can read, a specific filename, a specific button label, a specific tab title, a specific image you can describe. do NOT point at vague things like "a window" or "some text", be specific about exactly what you see.

        make a short quirky 3-6 word observation about the specific thing you picked, something fun, playful, or curious that shows you actually read/recognized it. no emojis ever. NEVER quote or repeat text you see on screen, just react to it. keep it to 6 words max, no exceptions.

        CRITICAL COORDINATE RULE: you MUST only pick elements near the CENTER of the screen. your x coordinate must be between 20%-80% of the image width. your y coordinate must be between 20%-80% of the image height. do NOT pick anything in the top 20%, bottom 20%, left 20%, or right 20% of the screen. no taskbar or tray items, no title bars, no sidebar items, no items near any edge. only things clearly in the middle area of the screen. if the only interesting things are near the edges, pick something boring in the center instead.

        respond with ONLY your short comment followed by the coordinate tag. nothing else. all lowercase.

        format: your comment [POINT:x,y:label]

        the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. origin (0,0) is top-left. x increases rightward, y increases downward.
        """.ReplaceLineEndings("\n");

    /// <summary>The question the pointing demo asks with its screenshot.</summary>
    public const string OnboardingDemoUserPrompt = "look around my screen and find something interesting to point at";

    /// <summary>
    /// Shared by the Realtime prompt and the ask lane's quick-actions line: a command is done, a
    /// question is taught, and nothing is ever sent.
    /// </summary>
    public const string CommandOrQuestionInstruction =
        "When the user tells you to do something you have a tool for (\"open…\", \"play…\", \"minimize…\", \"search for…\", \"check my email\", \"draft an email\"), do it with the tool and confirm in a few words. Do not explain how they could do it themselves and do not point. When they ask how to do something (\"how do I…\", \"where is…\", \"show me how\"), teach: explain and point, and do not do it for them. If you are not sure which they meant, ask in one short question. You never send email: a draft is left open for the user to read and send.";

    /// <summary>Added to the ask prompt when the quick actions are offered.</summary>
    public const string AccountToolsInstruction =
        "When the user asks you to open an app or a website, search the web, make or show a folder, change the volume, control playback or play a song, minimize everything, check your email or draft an email, call the matching tool instead of describing the steps. For anything else you can only explain and point. "
        + CommandOrQuestionInstruction;

    /// <summary>The ask lane remembers this many exchanges for the next question.</summary>
    public const int MaximumRememberedExchanges = 10;

    /// <summary>
    /// The base prompt with the one language rule at its end (both lanes' prompts end with it).
    /// `code` null reads shell.json's `language`. See ReplyLanguage.
    /// </summary>
    public static string WithReplyLanguage(string basePrompt, string? code = null) =>
        basePrompt + "\n\n" + ReplyLanguage.Instruction(code ?? ReplyLanguage.NormalizedCode(OpenClickyConfiguration.Settings.Language));

    /// <summary>Base prompt + skills block, separated by a blank line; the base alone when there is nothing to add.</summary>
    public static string ComposeTalkInstructions(string basePrompt, string skillsBlock)
    {
        var trimmed = skillsBlock.Trim();
        return trimmed.Length == 0 ? basePrompt : basePrompt + "\n\n" + trimmed;
    }

    /// <summary>
    /// The ask lane offers the quick actions where nothing else does them: whenever the agent lane
    /// doesn't run (a free account, or no openclicky tool on this PC), and to a plan, which the
    /// agent lane's gate never sees as the one answering.
    /// </summary>
    public static bool OffersQuickActionsToAskLane(bool agentLaneRuns, AnswerSource? answeringSource)
    {
        if (!agentLaneRuns) return true;
        return answeringSource is AnswerSource.ClaudeCodePlan or AnswerSource.CodexPlan;
    }

    /// <summary>
    /// The tools a question carries, in Anthropic's shape (ClaudeAPI's `tools`): the quick actions
    /// when they are offered, then write_in_place whenever there is text to write over.
    /// </summary>
    /// <param name="quickActionToolDefinitions">
    /// The quick actions' Anthropic tool definitions — the hook for the Actions port
    /// (DesktopActions' equivalent of MacAction.anthropicToolDefinitions()). Empty until it lands.
    /// </param>
    public static IReadOnlyList<JsonObject> AskTools(bool offersQuickActions, bool hasWritingTarget,
                                                     IEnumerable<JsonObject> quickActionToolDefinitions)
    {
        var tools = offersQuickActions ? quickActionToolDefinitions.ToList() : [];
        // Offered whatever the lane setup, but only when there is text to write over.
        if (hasWritingTarget) tools.Add(WriteInPlace.AnthropicToolDefinition());
        return tools;
    }

    /// <summary>
    /// The ask lane's whole system prompt: the companion prompt with the reply language, the quick
    /// actions line when they are offered, the write-in-place line when there is text to write
    /// over, then the talk skills.
    /// </summary>
    public static string AskSystemPrompt(bool quickActionsOffered, bool hasWritingTarget, string skillsBlock, string? languageCode = null)
    {
        var toolsLine = quickActionsOffered ? "\n\n" + AccountToolsInstruction : "";
        var writeInPlaceLine = "\n\n" + (hasWritingTarget ? WriteInPlace.PromptInstruction : WriteInPlace.NoTargetPromptInstruction);
        return ComposeTalkInstructions(WithReplyLanguage(CompanionVoiceResponseSystemPrompt, languageCode) + toolsLine + writeInPlaceLine, skillsBlock);
    }

    /// <summary>
    /// The question as sent: what was said, plus the text it may be about. A text to write over
    /// carries the exact words (the highlight in full, or the box's text); otherwise a highlight is
    /// quoted the short way, as context for "what does this mean".
    /// </summary>
    /// <param name="selectedText">The text highlighted in the app in front (the Platform layer reads it); null for none.</param>
    public static string AskUserPrompt(string transcript, WritingTarget? writingTarget, string? selectedText = null)
    {
        if (writingTarget is not null) return transcript + "\n\n[" + writingTarget.ContextLine + "]";
        if (!string.IsNullOrWhiteSpace(selectedText)) return transcript + "\n\n[" + SelectedTextContextLine(selectedText) + "]";
        return transcript;
    }

    /// <summary>SelectedTextReader.contextLine: what a highlight adds to a question.</summary>
    public static string SelectedTextContextLine(string selectedText) =>
        $"The user has selected (highlighted) this text on screen: «{selectedText}». When they say \"this\", \"this line\" or \"this word\", they almost certainly mean the selection, answer about that text, not about whatever is under the pointer.";

    /// <summary>
    /// A screenshot's label with its real pixel size ("… (image dimensions: 1280x800 pixels)"), so
    /// the coordinates Claude answers with are in the image it saw.
    /// </summary>
    public static string ImageLabelWithItsPixelSize(string captureLabel, int screenshotWidthInPixels, int screenshotHeightInPixels) =>
        captureLabel + $" (image dimensions: {screenshotWidthInPixels}x{screenshotHeightInPixels} pixels)";
}
