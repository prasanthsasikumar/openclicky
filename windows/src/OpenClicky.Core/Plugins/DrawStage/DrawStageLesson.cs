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
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Plugins.DrawStage;

public static partial class DrawStageLesson
{
    /// <summary>On by default while the Teacher plugin is on; off sends teaching questions to the Teacher's board.</summary>
    public const string PreferenceKey = "teacher.drawingStage";

    /// <summary>A program for a few beats is 1,000-2,500 tokens and a detailed 3D figure several thousand more; the ask lane's
    /// 1,024 would cut most of them off mid-beat.</summary>
    public const int MaximumOutputTokens = 8192;

    public static bool IsEnabled(Preferences preferences, bool teacherIsOn) => teacherIsOn && preferences.GetBool(PreferenceKey, defaultValue: true);

    // MARK: - Who writes the drawing

    /// <summary>
    /// The model that writes drawn answers: shell.json's `drawingSource` (when it can answer) and `drawingModel`, so a
    /// person can answer quickly with a small local model and draw with a stronger one; with neither set, the same
    /// <paramref name="answers"/> that answers questions.
    /// </summary>
    public static ClaudeAPI WriterFor(OpenClickyShellSettings settings, ClaudeAPI answers)
    {
        var source = AnswerSources.FromRawValue(settings.DrawingSource);
        var model = OpenClickyConfiguration.Cleaned(settings.DrawingModel);
        if (source is null && model is null) return answers;
        var modelOverrides = new Dictionary<string, string>(settings.AnswerModels ?? []);
        return new ClaudeAPI(
            () => $"{OpenClickyConfiguration.BackendBaseUrl}/chat",
            model ?? answers.Model,
            currentCredentials: source is { } chosen ? () => ProviderRouter.CurrentCredentials() with { PreferredAnswerSource = chosen } : null,
            makeAnswerProvider: (answering, credentials, cancellationToken) =>
            {
                // The drawing model is for whichever source ends up writing the drawing.
                var overrides = model is null ? modelOverrides : new Dictionary<string, string>(modelOverrides) { [answering.Id()] = model };
                return AnswerProviderFactory.MakeAsync(answering, credentials, overrides, cancellationToken: cancellationToken);
            },
            warmUpConnection: false);
    }

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
    public static string SystemPrompt(int width, int height) => AssistantName.Introduce($$"""
        You are OpenClicky, a friendly teacher who answers by drawing on the person's screen while you talk, like a teacher at a whiteboard. Your answer is spoken aloud and drawn at the same time.

        Reply with ONE ```js code block and nothing else. The block is a small program in the drawing language below. It runs statement by statement while you write it, so the picture appears as you speak.

        HOW AN ANSWER IS BUILT
        - 3 to 6 beats. Each beat is step(n); say("…") followed by the drawing calls for that beat. Everything a beat draws is drawn while its say() line is spoken, so draw exactly what that line talks about.
        - Each say() is one or two short spoken sentences (8 to 25 words), in plain words a curious 12-year-old follows. No lists, no markdown, no emojis, no symbols the voice can't read (write "equals", not "=").
        - Beat 1 sets the scene: the outline of the thing and its main parts. When beat 1 builds a detailed 3D figure, its line says so first, in a natural way ("Let me build a 3D model of the engine for you."), since the parts appear while it is built. Later beats add what moves or happens (arrows, flows, forces), one idea per beat. The last beat sums it up in one line, with a short highlighted takeaway.
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
        photo(x, y, w, h, {query, caption})      a real photo from Wikimedia Commons in a polaroid frame: for real things a drawing can't show well (an animal, an organ, a place, a real machine). query is a short English search ('human heart anatomy'); use at most one or two, beside the drawing, never instead of it
        Options o: {color, width, fill, fillOpacity, dashed, size, align, bold}. Colors: ink, blue, red, green, orange, purple, pink, yellow, teal, brown, grey, white, or '#rrggbb'. Default stroke width 3.2, text size 26.
        Also available: W, H, PI, sin, cos, tan, atan2, sqrt, abs, min, max, round, floor, ceil, hypot, lerp(a, b, t), mid(p, q), along(p, q, t), polar(cx, cy, r, angle), range(n).
        Plain JavaScript works too (const, let, arithmetic, for loops, arrays). Nothing else exists: no DOM, no network, no other functions. Never use Math.random. At most about 70 drawing calls, plus up to 50 parts for a 3D figure.

        3D FIGURES (use one when the thing has a real shape or is a machine: an engine, a plane, a rocket, a circuit board, a 3D printer, a planet, the heart, a gear train; stay 2D for processes, cycles, graphs and comparisons)
        const f = figure(x, y, w, h, {tilt, turn, zoom, spin, ground})   a turning 3D stage in that box, usually filling your card below its title. The camera frames the parts by itself; ground: false for things that float (planets, atoms).
        Parts, in world units (about 1-5 across), each returns a part:
          f.box({w, h, d, round}) (round: corner radius, for cases, chips, buttons, bodies) f.cyl({r, h, r2, open}) f.cone({r, h}) f.sphere({r}) f.torus({r, tube}) f.capsule({r, h})
          f.lathe({points: [[radius, y], …]}) (a turned profile: casings, nozzles, bottles, bells, a fuselage on axis: 'x')
          f.extrude({points: [[x, y], …], depth, holes: [[[x, y], …]], bevel}) (a flat outline made thick: wings, fins, brackets, a plane seen from above)
          f.tube({points: [[x, y, z], …], r}) (smooth: cables, hoses, veins) f.pipe({points, r, bend}) (straight runs with bends: plumbing, exhausts)
          f.spring({r, h, coils, wire}) f.gear({r, teeth, depth}) f.arrow({from: [x, y, z], to: [x, y, z], color}) (flows and forces)
          f.blob({balls: [[x, y, z, r], [x, y, z, rx, ry, rz], …], sticks: [{from, to, r, r2}], paths: [{points, r, r2}], holes: [[x, y, z, r]], smooth}) one smooth organic surface over its balls and sticks, blended where they touch (smooth 0.1-0.6): organs, bodies, animals, teeth, leaves, rocks, clouds. Use it for anything alive; spheres and capsules side by side look like toys.
        Every part also takes at: [x, y, z], rot: [degrees x, y, z], axis: 'x' | 'y' | 'z' (which way a cyl, cone, capsule, spring, lathe or gear points; default y, gear and extrude default z), scale, parent: anotherPart (moves with it), color, opacity, and
          mat: 'steel' | 'alu' | 'titanium' | 'chrome' | 'iron' | 'gold' | 'copper' | 'brass' | 'plastic:<color>' | 'paint:<color>' | 'rubber' | 'wood' | 'stone' | 'ceramic' | 'glass' | 'water' | 'glow:<color>' (flames, lights, hot things)
               | 'tissue' | 'muscle' | 'skin' | 'bone' | 'leaf' | 'fabric' | 'pcb' (a green circuit board); any of them takes a colour: 'tissue:#4A6FB5'.
          cut: 'z' cuts the part open along the figure's middle and takes away its front half (z > 0), so what is inside shows: use it on a casing, a body or a shell with the working parts inside it. '-z', 'x', 'y' … cut other halves; {axis: 'z', at: 0.3} cuts elsewhere.
          copies: {n, step: [dx, dy, dz]} a row of the same part (pins, keys, compressor stages, windows); around: {n, axis, center} the same part repeated round an axis (fan and turbine blades, gear teeth, bolts on a flange, petals); mirror: 'x' adds its mirror image across x = 0 (wings, legs, ears).
        Motion, chained on a part: .spin({axis, period}) .stroke({axis, amount, period}) (a piston, a moving print head) .bob({axis, amount, period}) .pulse({amount, period}) (a beating heart) .orbit({center, axis, period}) .move({to, secs}) (once, in its beat).
        Labels: part.label('words', {color}) or f.label(part, 'words', {color}); they are small tags tied to the part by a leader line. f.view({tilt, turn, zoom}) changes the camera in a beat.
        Name each part's const after what it is (const liftFan = f.cyl(…)): a click on the part asks about it by that name.
        MAKE IT LOOK REAL: a figure is built from many parts, like a good model kit: usually 15-40 parts, with the small things that make it recognisable (bolts, ribs, ports, chips, blades, windows, seams, a lens). Get the proportions right, keep parts touching (nothing floats unless it really does), and use the real materials and colours. Show the inside with cut instead of drawing two separate pictures. Build the body in beat 1 (it may take a dozen parts), then add the moving and inner parts, motion and labels as the voice explains them. Put the figure's parts near the origin, y up, x along its length.

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

        3D EXAMPLE (the first beats of "how does a jet engine work?" on a 1280 × 720 screen)
        ```js
        step(1); say("A jet engine is a tube that sucks air in at the front with a big spinning fan.")
        const c = {x: 380, y: 40, w: 860, h: 560}
        card(c.x, c.y, c.w, c.h, {title: 'Inside a jet engine'})
        const f = figure(c.x + 30, c.y + 70, c.w - 60, c.h - 100, {tilt: 22, turn: -35})
        const casing = f.lathe({points: [[1.05, -2.3], [1.12, -1.9], [1.1, -0.6], [0.95, 0.6], [0.95, 1.4], [0.7, 2.2]], axis: 'x', mat: 'alu', cut: 'z'})
        const shaft = f.cyl({r: 0.09, h: 4.2, axis: 'x', mat: 'steel'})
        const spinner = f.cone({r: 0.32, h: 0.55, axis: '-x', at: [-2.15, 0, 0], mat: 'paint:#30343B'})
        const fanBlade = f.box({w: 0.05, h: 0.78, d: 0.3, at: [-1.75, 0.5, 0], rot: [0, 30, 0], around: {n: 18, axis: 'x', center: [-1.75, 0, 0]}, mat: 'titanium'}).spin({axis: 'x', period: 0.8})
        fanBlade.label('fan', {color: 'blue'})

        step(2); say("Rows of smaller blades squeeze that air tighter and tighter: that part is the compressor.")
        const compressor = f.cyl({r: 0.62, r2: 0.48, h: 0.06, axis: 'x', at: [-1.2, 0, 0], copies: {n: 7, step: [0.16, 0, 0]}, mat: 'steel'}).spin({axis: 'x', period: 0.6})
        compressor.label('compressor', {color: 'purple'})

        step(3); say("Fuel burns in the squeezed air, and the hot gas rushes out of the back.")
        const burner = f.torus({r: 0.55, tube: 0.16, axis: 'x', at: [0.25, 0, 0], mat: 'glow:orange'})
        burner.label('burn fuel', {color: 'orange'})
        ```

        ORGANIC EXAMPLE (one beat of "how does the heart pump blood?": a blob for the muscle, blobs for the big vessels)
        ```js
        const heart = f.blob({balls: [[0.35, -0.35, 0, 0.85, 1.1, 0.8], [-0.35, -0.2, 0.15, 0.75, 0.95, 0.7], [0.2, -1.25, 0.05, 0.3]], smooth: 0.45, mat: 'tissue'}).pulse({amount: 0.035, period: 1})
        const aorta = f.blob({paths: [{points: [[0.1, 0.5, 0], [0.12, 1.5, -0.05], [0.55, 2.05, -0.1], [1.1, 1.75, -0.15], [1.2, 1.0, -0.2]], r: 0.3, r2: 0.26}], mat: 'tissue:#C0392B'})
        aorta.label('to the body', {color: 'red'})
        ```
        """);

    /// <summary>The question as the model reads it.</summary>
    public static string UserPrompt(string question, string? markedAreaLine = null) =>
        $"They asked: \"{question.Trim()}\"\n" + (markedAreaLine is null ? "" : $"[{markedAreaLine}]\n")
        + "Answer it by drawing on their screen, as one ```js program.";

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
