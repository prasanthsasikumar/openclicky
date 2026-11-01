//
//  DrawStageLesson.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  A question answered by drawing on the stage while the answer is spoken. The answer is one
//  program in the drawing language (docs/draw-stage.md): its say() lines are what the voice
//  says, and each beat's strokes are drawn while its line is heard. The model writes it with the
//  screenshot of the screen the question was asked on, which is also the canvas, so it can circle
//  the real thing on the screen and put its diagram on a card over a calm part of it.
//
//  Which questions get a drawing: the Teacher's teaching questions ("how does a jet engine
//  work"), and also the ones about the thing on the screen that a picture answers ("how does this
//  work?", "what's inside this?"), which the Teacher leaves to pointing. Asking how to *do*
//  something on screen stays with pointing.
//

using System.Text.RegularExpressions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Teacher;

namespace OpenClicky.Core.Plugins.DrawStage;

public static partial class DrawStageLesson
{
    /// <summary>On by default while the Teacher plugin is on; off sends teaching questions to the Teacher's board.</summary>
    public const string PreferenceKey = "teacher.drawingStage";

    /// <summary>A program for a few beats is 1,000-2,500 tokens; the ask lane's 1,024 would cut most of them off mid-beat.</summary>
    public const int MaximumOutputTokens = 4096;

    public static bool IsEnabled(Preferences preferences, bool teacherIsOn) => teacherIsOn && preferences.GetBool(PreferenceKey, defaultValue: true);

    // MARK: - Which questions

    /// <summary>Whether <paramref name="question"/> is best answered with a drawing.</summary>
    public static bool WantsADrawing(string question)
    {
        if (TeacherQuestion.IsTeachingQuestion(question)) return true;
        // Words between single spaces, with a space at each end, so " word " finds whole words ("hey, how" → " hey how ").
        var words = NonWord().Replace(question.ToLowerInvariant().Replace('’', '\''), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = " " + string.Join(' ', words) + " ";
        if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return false;
        // Doing something on the screen is pointing's: "how do I export this", "where is the save button".
        if (ContainsAny(text, " how do i ", " how can i ", " how should i ", " where is ", " where do i ", " click ", " open ")) return false;
        // A curious question about how or why the world works ("how does the heart pump blood", "why is the sky blue",
        // "how do birds fly", "what happens when ice melts"): a picture explains it best. "How do you…" asks for steps.
        if (StartsAny(text, " how does ", " how do ", " how did ", " how is ", " how are ", " how can a ", " how can an ", " why ", " what happens ",
                " what makes ", " what causes ", " what is inside ", " what's inside ", " what are the parts ")
            && !StartsAny(text, " how do you ", " how do we ", " how is it going ", " how are you ", " why not "))
        {
            return true;
        }
        // About the thing on the screen, answered by a picture of how it works or what it is made of.
        return ContainsAny(text,
            " draw ", " sketch ", " diagram ", " visualize ", " visualise ", " illustrate ",
            " how does this work ", " how does that work ", " how does it work ", " how do these work ", " how do they work ",
            " how this works ", " how that works ", " how it works ", " what's inside ", " what is inside ", " how is this made ",
            " how is it made ", " how are these made ", " explain this ", " explain that ", " explain it ", " show me how ");
    }

    private static bool ContainsAny(string text, params string[] phrases) => phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));

    /// <summary>Whether the question begins with one of the phrases (after a filler like "hey", "so" or "okay").</summary>
    private static bool StartsAny(string text, params string[] phrases)
    {
        var start = text;
        foreach (var filler in new[] { " hey ", " so ", " okay ", " ok ", " um ", " and ", " but ", " wait " })
        {
            if (start.StartsWith(filler, StringComparison.Ordinal)) start = start[(filler.Length - 1)..];
        }
        return phrases.Any(phrase => start.StartsWith(phrase, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"[^\p{L}\p{N}' ]+")]
    private static partial Regex NonWord();

    // MARK: - The prompt

    /// <summary>The system prompt: the drawing language and how a good answer is built, for a canvas of
    /// <paramref name="width"/> × <paramref name="height"/> units (the screenshot sent with the question).</summary>
    public static string SystemPrompt(int width, int height) => $$"""
        You are OpenClicky, a friendly teacher who answers by drawing on the person's screen while you talk, like a teacher at a whiteboard. Your answer is spoken aloud and drawn at the same time.

        Reply with ONE ```js code block and nothing else. The block is a small program in the drawing language below. It runs statement by statement while you write it, so the picture appears as you speak.

        HOW AN ANSWER IS BUILT
        - 3 to 6 beats. Each beat is step(n); say("…") followed by the drawing calls for that beat. Everything a beat draws is drawn while its say() line is spoken, so draw exactly what that line talks about.
        - Each say() is one or two short spoken sentences (8 to 25 words), in plain words a curious 12-year-old follows. No lists, no markdown, no emojis, no symbols the voice can't read (write "equals", not "=").
        - Beat 1 sets the scene: the outline of the thing and its main parts. Later beats add what moves or happens (arrows, flows, forces), one idea per beat. The last beat sums it up in one line, with a short highlighted takeaway.
        - Make it correct: real proportions, real directions, real physics. Work out coordinates before you write them.
        - Answer in the language the question was asked in.

        THE CANVAS
        - The attached screenshot is the canvas: W = {{width}} by H = {{height}} units, origin at the top-left, y grows downward. What you draw lands on their real screen exactly where it is in the screenshot.
        - If the question is about something visible ("this", a photo, a part of the page), point at it where it really is: a circle(), an arrow() from your card to it, or a highlight().
        - Draw the explanation on one card() over a calm part of the screen (empty space or less important content), sized so the whole diagram fits: usually 40-55% of W wide and 60-80% of H tall. Keep 30 units of margin inside the card. Only marks that point at the real screen go outside it.
        - Keep the bottom 14% of the screen (below y = {{(int)(height * 0.86)}}) empty: the spoken words are shown there as captions.
        - Keep the card's box in a const and place everything relative to it: const c = {x: …, y: …, w: …, h: …}.
        - Labels are 1-4 words, size 20-28, next to the thing they name and never on top of a line or another label; use a short arrow when a label can't sit beside its thing.

        THE DRAWING LANGUAGE
        step(n)                                  start beat n
        say(text)                                the line spoken during this beat
        card(x, y, w, h, {title})                a paper card to draw the explanation on
        line(points, o)   curve(points, o)       a pen stroke through [[x, y], …]; curve() is smooth
        arrow(points, o)                         a stroke with an arrowhead at its last point
        rect(x, y, w, h, o)   circle(x, y, r, o)   ellipse(x, y, rx, ry, o)   poly(points, o)   outlines; add fill to fill them
        dot(x, y, r, o)                          a filled point
        text(x, y, words, o)                     handwriting centred on x, y (align 'start' puts its left edge at x)
        highlight(x, y, w, h, o)                 a marker sweep behind words
        Options o: {color, width, fill, fillOpacity, dashed, size, align, bold}. Colors: ink, blue, red, green, orange, purple, pink, yellow, teal, brown, grey, white, or '#rrggbb'. Default stroke width 3.2, text size 26.
        Also available: W, H, PI, sin, cos, tan, atan2, sqrt, abs, min, max, round, floor, ceil, hypot, lerp(a, b, t), mid(p, q), along(p, q, t), polar(cx, cy, r, angle), range(n).
        Plain JavaScript works too (const, let, arithmetic, for loops, arrays). Nothing else exists: no DOM, no network, no other functions. Never use Math.random. At most about 70 drawing calls.

        EXAMPLE (a complete answer to "how does a bicycle pump work?" on a 1280 × 720 screen)
        ```js
        step(1); say("A bike pump is a tube with a piston inside it, and a handle you push down.")
        const c = {x: 600, y: 50, w: 620, h: 560}
        card(c.x, c.y, c.w, c.h, {title: 'How a bike pump works'})
        const t = {x: c.x + 200, y: c.y + 140, w: 120, h: 280}
        rect(t.x, t.y, t.w, t.h, {color: 'ink'})
        line([[t.x + 8, t.y + 110], [t.x + t.w - 8, t.y + 110]], {color: 'blue', width: 7})
        line([[t.x + t.w / 2, t.y + 110], [t.x + t.w / 2, t.y - 50]], {color: 'ink', width: 5})
        line([[t.x + 10, t.y - 50], [t.x + t.w - 10, t.y - 50]], {color: 'ink', width: 9})
        text(t.x + t.w + 30, t.y + 110, 'piston', {align: 'start', color: 'blue'})

        step(2); say("Pushing the handle moves the piston down and squeezes the air below it, so its pressure rises.")
        arrow([[t.x - 40, t.y + 60], [t.x - 40, t.y + 200]], {color: 'red', width: 4})
        text(t.x - 55, t.y + 130, 'push', {align: 'end', color: 'red'})
        for (const k of range(6)) dot(t.x + 25 + (k % 3) * 35, t.y + 170 + floor(k / 3) * 35, 6, {color: 'teal'})
        text(t.x + t.w / 2, t.y + t.h - 25, 'squeezed air', {size: 22, color: 'teal'})

        step(3); say("The squeezed air pushes open a little valve at the bottom and flows down the hose into the tyre.")
        circle(t.x + t.w / 2, t.y + t.h - 8, 14, {color: 'purple'})
        text(t.x + t.w + 30, t.y + t.h - 8, 'valve', {align: 'start', color: 'purple'})
        curve([[t.x + t.w / 2, t.y + t.h + 6], [t.x + t.w / 2 + 40, t.y + t.h + 50], [c.x + c.w - 60, t.y + t.h + 40]], {color: 'ink', width: 5})
        arrow([[c.x + c.w - 160, t.y + t.h + 70], [c.x + c.w - 70, t.y + t.h + 66]], {color: 'teal'})

        step(4); say("So every push moves a tube full of air into the tyre, and it gets harder as the tyre fills up.")
        highlight(c.x + 30, c.y + c.h - 60, c.w - 60, 40, {color: 'yellow'})
        text(c.x + 45, c.y + c.h - 40, 'push, squeeze, valve opens, tyre fills', {align: 'start', size: 24})
        ```
        """;

    /// <summary>The question as the model reads it.</summary>
    public static string UserPrompt(string question) =>
        $"They asked: \"{question.Trim()}\"\nAnswer it by drawing on their screen, as one ```js program.";

    // MARK: - Reading the reply

    /// <summary>The program in a reply streamed so far: what follows the opening ``` line, up to the closing one. A reply
    /// written without a fence is taken whole once it begins like code; before that (or for prose) it is empty.</summary>
    public static string ProgramIn(string replySoFar)
    {
        var text = replySoFar.ReplaceLineEndings("\n");
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence < 0)
        {
            var start = text.TrimStart();
            return start.StartsWith("step(", StringComparison.Ordinal) || start.StartsWith("const ", StringComparison.Ordinal)
                || start.StartsWith("say(", StringComparison.Ordinal) || start.StartsWith("//", StringComparison.Ordinal)
                ? text
                : "";
        }
        var lineEnd = text.IndexOf('\n', fence);
        if (lineEnd < 0) return "";
        var body = text[(lineEnd + 1)..];
        var close = body.IndexOf("\n```", StringComparison.Ordinal);
        if (close >= 0) return body[..(close + 1)];
        // The closing fence may be arriving: hold back a trailing line that is only backticks.
        var lastLine = body.LastIndexOf('\n');
        var tail = body[(lastLine + 1)..];
        return tail.Length > 0 && tail.Trim('`').Length == 0 ? body[..(lastLine + 1)] : body;
    }
}
