//
//  DrawStageLesson.swift
//  OpenClicky
//
//  Which questions are answered by drawing on the stage, the prompts that ask a model for a
//  drawing (read from the shared draw-stage/prompt folder), and how its streamed reply is read.
//
//  The gate errs on the side of no drawing: a question about the person's own work (an error, a
//  file, the app in front of them) or a simple fact is answered in words with no added wait. The
//  model has the last word: its first line is NONE when a picture is not needed, and the question
//  then goes to the ordinary ask lane before anything is said. Design:
//  docs/superpowers/specs/2026-11-04-draw-and-explain-mac-design.md. Windows twin: DrawStageLesson.cs.
//

import Foundation

/// A drawing that is on the stage (or in its bubble): what was asked, the program that drew it,
/// its canvas and its spoken lines, so a follow-up question can add to it.
struct DrawnAnswer: Equatable {
    let questions: [String]
    let program: String
    let canvasWidth: Int
    let canvasHeight: Int
    let spokenLines: [String]
}

/// Anchors the bundle the prompt files are read from (the app's, not a test runner's).
private final class DrawStagePromptAnchor {}

enum DrawStageLesson {
    static let holdLine = "One moment, let me draw that for you."
    static let outputTokens = 8192
    static let notAFollowUp = "NONE"

    // MARK: - Which questions

    /// Words between single spaces, with a space at each end, so " word " finds whole words
    /// ("Hey, how" → " hey how "). Apostrophes stay, so "what's" and "i'm" keep their shape.
    private static func words(_ question: String) -> String {
        let lowered = question.lowercased()
        let cleaned = lowered.map { character -> Character in
            character.isLetter || character.isNumber || character == "'" || character == "’" ? character : " "
        }
        let collapsed = String(cleaned).replacingOccurrences(of: "’", with: "'")
            .split(separator: " ", omittingEmptySubsequences: true).joined(separator: " ")
        return " " + collapsed + " "
    }

    private static func wordCount(_ text: String) -> Int {
        text.split(separator: " ", omittingEmptySubsequences: true).count
    }

    private static func containsAny(_ text: String, _ phrases: [String]) -> Bool {
        phrases.contains { text.contains($0) }
    }

    private static func startsAny(_ text: String, _ phrases: [String]) -> Bool {
        phrases.contains { text.hasPrefix($0) }
    }

    /// Doing something on the screen is pointing's, never a drawing's.
    private static let screenActionPhrases = [" how do i ", " how can i ", " how should i ", " where is ", " where do i ", " click ", " open ", " press "]
    private static let smallTalkOpeners = [" how do you ", " how do we ", " how is it going ", " how's it going ", " how are you ", " why not "]
    /// The person's own trouble and their own work: words answer those, a drawing cannot.
    private static let troubleWords = [
        " error ", " errors ", " fail ", " fails ", " failing ", " failed ", " crash ", " crashes ", " crashing ", " broken ", " bug ", " bugs ",
        " warning ", " stuck ", " slow ", " won't ", " wont ", " doesn't ", " doesnt ", " not working ", " issue ", " problem ",
    ]
    private static let workWords = [
        " app ", " file ", " files ", " code ", " build ", " setting ", " settings ", " window ", " tab ", " button ", " menu ", " folder ",
        " email ", " message ", " page ",
    ]
    /// Ownership words only: "my build", "our code". A bare "I" ("I want to know how a jet engine
    /// works", "can you tell me…") is how people ask, so it never counts against a question.
    private static let firstPersonWords = [" my ", " mine ", " our "]
    /// Things in a cell, and the body, that a "what is …" question names: a drawing of it answers
    /// ("what is a cell", "what is mitosis", "explain chromosomes"). Not a phone's cell or a spreadsheet's.
    private static let lifeScience = [
        " cell ", " cells ", " mitosis ", " meiosis ", " cell division ", " chromosome ", " chromosomes ", " chromatid ", " chromatids ",
        " mitochondria ", " mitochondrion ", " chloroplast ", " chloroplasts ", " nucleus of a cell ", " bacteria ", " bacterium ",
        " gene ", " genes ", " double helix ", " blood circulation ", " circulatory system ",
    ]
    private static let notLifeScience = [
        " phone ", " phones ", " cellphone ", " battery ", " batteries ", " tower ", " excel ", " spreadsheet ", " sheet ", " sheets ", " table ", " prison ",
        " jail ", " solar ",
    ]
    private static let lifeOpeners = [" what is ", " what's ", " what are ", " tell me about ", " explain ", " describe "]
    /// A curious question about how or why the world works, wherever the asking words fall in the
    /// sentence ("can you tell me how an electron microscope works"): a picture explains it best.
    private static let curiousOpeners = [
        " how does ", " how do ", " how did ", " how is ", " how are ", " how can a ", " how can an ", " why ", " what happens ", " what makes ",
        " what causes ", " what is inside ", " what's inside ", " what are the parts ",
    ]
    /// Asked outright, or about the thing on the screen in a way a picture of how it works answers.
    private static let explicitAsks = [
        " draw ", " sketch ", " diagram ", " visualize ", " visualise ", " illustrate ",
        " how does this work ", " how does that work ", " how does it work ", " how do these work ", " how do they work ",
        " how this works ", " how that works ", " how it works ", " what's inside ", " what is inside ", " how is this made ",
        " how is it made ", " how are these made ", " explain this ", " explain that ", " explain it ", " show me how ",
        " teach me ", " explain how ",
        // Asked for a figure outright: "show me a rotary engine in 3d", "a 3d model of the heart", "a turbine cutaway".
        " 3d ", " 3 d ", " three d ", " in three dimensions ", " model of ", " show me what ", " show me the inside ",
        " the inside of ", " inside of ", " cutaway ", " cross section ", " looks like ",
    ]

    /// Whether `question` is best answered with a drawing. Narrow on purpose: everything about the
    /// person's own situation stays out, so it never pays for a model round trip.
    static func wantsADrawing(_ question: String) -> Bool {
        let text = words(question)
        if wordCount(text) < 2 { return false }
        if containsAny(text, screenActionPhrases) || containsAny(text, smallTalkOpeners) { return false }
        if containsAny(text, troubleWords) || containsAny(text, workWords) || containsAny(text, firstPersonWords) { return false }
        // What a part of life is ("what is a cell", "what is mitosis"): a drawing of it answers.
        if startsAny(text, lifeOpeners) && containsAny(text, lifeScience) && !containsAny(text, notLifeScience) { return true }
        if containsAny(text, curiousOpeners) || containsAny(text, explicitAsks) { return true }
        // What a thing is made of or looks like, and chemistry ("what is aspirin made of", "what does
        // caffeine look like", "what is dopamine"): a molecule or a cutaway shows it.
        if containsAny(text, whatItIsMadeOf) { return true }
        // Numbers over time and history ("how has world population grown over 300 years", "the
        // history of computers"): a graph or a timeline.
        if containsAny(text, overTime) { return true }
        if startsAny(text, [" how has ", " how have "]) && containsAny(text, changed) { return true }
        if startsAny(text, [" what is ", " what's ", " what are ", " tell me about "]) && containsAny(text, chemicals) { return true }
        // "explain how a transistor works", "tell me how the heart works", "I want to know how a jet
        // engine works": asking how a thing works, in any wording, once the screen and trouble words are out.
        return (text.contains(" explain ") || text.contains(" how ")) && (text.contains(" works ") || text.contains(" work "))
    }

    /// Whether the question is about something on the screen ("how does this work?", "what's
    /// inside that", "what is happening here"), so the pointer's place and a close-up of what is
    /// under it go with the drawing request; a question about the world at large sends neither.
    static func refersToTheScreen(_ question: String) -> Bool {
        containsAny(words(question), [" this ", " that ", " these ", " those ", " here ", " on screen ", " on the screen ", " on my screen "])
    }

    /// Words that ask what a thing is made of, what it looks like or how its atoms sit.
    private static let whatItIsMadeOf = [
        " made of ", " made from ", " look like ", " looks like ", " molecule ", " molecules ", " atom ", " atoms ",
        " chemical formula ", " chemical structure ", " structure of ", " formula of ", " formula for ",
    ]
    /// Words that ask for numbers over time or a stretch of history: a graph or a timeline answers.
    private static let overTime = [
        " graph ", " chart ", " plot ", " timeline ", " history of ", " over time ", " over the years ", " over the last ", " over the past ",
        " through history ", " through the ages ",
    ]
    /// What a "how has …" question asks happened to a thing ("how has the population grown").
    private static let changed = [" grown ", " grow ", " changed ", " change ", " increased ", " decreased ", " risen ", " fallen ", " dropped ", " evolved ",]
    /// Everyday chemicals a "what is …" question is about: their molecule answers it.
    private static let chemicals = [
        " aspirin ", " caffeine ", " dopamine ", " serotonin ", " adrenaline ", " melatonin ", " histamine ", " oxytocin ", " cortisol ",
        " glucose ", " fructose ", " sucrose ", " nicotine ", " paracetamol ", " acetaminophen ", " ibuprofen ", " penicillin ", " ethanol ",
        " methane ", " ammonia ", " ozone ", " benzene ", " carbon dioxide ", " carbon monoxide ", " hydrogen peroxide ", " sulfuric acid ",
        " sulphuric acid ", " acetic acid ", " cholesterol ", " vitamin c ", " capsaicin ", " chlorophyll ", " dna ",
    ]

    private static let followUpActionPhrases = [
        " how do i ", " how can i ", " where is ", " where do i ", " click ", " open ", " close ", " minimize ", " press ", " type ",
    ]
    private static let followUpOpenersOut = [" how are you ", " how is it going ", " how's it going ", " thank ", " thanks "]
    private static let followUpWords = [
        " 3d ", " 2d ", " 3 d ", " three d ", " zoom ", " closer ", " close up ", " bigger ", " larger ", " smaller ", " rotate ", " turn it ",
        " spin ", " inside ", " cut ", " label ", " part ", " parts ", " piece ", " what about ", " how about ", " and the ", " show ",
        " draw ", " add ", " more ", " detail ", " again ", " explain ", " this ", " that ", " those ",
        " what does ", " what do ", " what is the ", " what's the ", " which ", " where does ", " where do ", " why ", " how ", " simpler ",
        " slower ", " next ", " faster ", " colour ", " color ", " hide ", " remove ", " without ", " change ", " make it ", " make the ", " slow ", " speed ", " quiz ", " test me ",
    ]

    /// Whether `question`, asked while a drawing is open, may be about it: one a picture answers, or
    /// one that asks for more of the drawing, another view, or about a part. The model has the last word (NONE).
    static func mayBeAFollowUp(_ question: String) -> Bool {
        if wantsADrawing(question) { return true }
        let text = words(question)
        if wordCount(text) < 2 { return false }
        if containsAny(text, followUpActionPhrases) || startsAny(text, followUpOpenersOut) { return false }
        return containsAny(text, followUpWords)
    }

    // MARK: - The prompts

    private static let promptBundle = Bundle(for: DrawStagePromptAnchor.self)

    /// A prompt file from the bundled draw-stage/prompt folder; nil when it is missing (then the
    /// feature turns itself off and says so in the log, rather than sending a broken prompt).
    private static func promptText(named name: String) -> String? {
        guard let url = promptBundle.url(forResource: name, withExtension: "md", subdirectory: "draw-stage/prompt"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            AppLog.append("draw: the prompt \(name).md is missing from the bundle")
            return nil
        }
        return text
    }

    /// The system prompt that teaches the drawing language, for a canvas of that size, at the
    /// "detailed" level (3 to 6 beats, 70 calls, 15-40 parts).
    static func systemPrompt(canvasWidth: Int, canvasHeight: Int) -> String? {
        guard let template = promptText(named: "drawing-language") else { return nil }
        let captionTop = Int(Double(canvasHeight) * 0.86)
        return template
            .replacingOccurrences(of: "{{W}}", with: String(canvasWidth))
            .replacingOccurrences(of: "{{H}}", with: String(canvasHeight))
            .replacingOccurrences(of: "{{captionTop}}", with: String(captionTop))
            .replacingOccurrences(of: "{{beats}}", with: "3 to 6")
            .replacingOccurrences(of: "{{calls}}", with: "70")
            .replacingOccurrences(of: "{{parts}}", with: "15-40 parts, with the small things that make it recognisable")
            .replacingOccurrences(of: "{{partsAtMost}}", with: "50")
    }

    /// The user prompt for a first question: draw, or answer NONE when a picture is not needed.
    static func questionPrompt(_ question: String) -> String? {
        guard let template = promptText(named: "question") else { return nil }
        return template.replacingOccurrences(of: "{{question}}", with: question.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    /// The user prompt for a question asked while `drawing` is open: the model adds to it (keep()),
    /// draws it again another way, or answers NONE.
    static func followUpPrompt(question: String, drawing: DrawnAnswer) -> String? {
        guard let template = promptText(named: "follow-up") else { return nil }
        let asked = drawing.questions.map { "\"\($0.trimmingCharacters(in: .whitespacesAndNewlines))\"" }.joined(separator: ", then ")
        return template
            .replacingOccurrences(of: "{{asked}}", with: asked)
            .replacingOccurrences(of: "{{program}}", with: drawing.program.trimmingCharacters(in: .whitespacesAndNewlines))
            .replacingOccurrences(of: "{{question}}", with: question.trimmingCharacters(in: .whitespacesAndNewlines))
            .replacingOccurrences(of: "{{nextStep}}", with: String(lastStep(in: drawing.program) + 1))
            .replacingOccurrences(of: "{{marked}}", with: "")
    }

    /// What the fast voice is told after a drawing answered a question in its place, so its next
    /// turn builds on it instead of answering the question again.
    static func fastVoiceNote(question: String, spokenLines: [String]) -> String {
        let said = spokenLines.joined(separator: " ")
        if said.isEmpty {
            return "To \"\(question)\" OpenClicky started a drawing on the screen, but it was stopped before anything was said. Do not repeat the question's answer unless asked again."
        }
        return "To \"\(question)\" OpenClicky answered by drawing on the screen and said: \(said) The drawing is on the screen (or in its bubble). Do not repeat it; the next question may be about it."
    }

    /// The highest step(n) in a program, 0 when it has none.
    private static func lastStep(in program: String) -> Int {
        guard let regex = try? NSRegularExpression(pattern: #"\bstep\(\s*(\d+)\s*\)"#) else { return 0 }
        let range = NSRange(program.startIndex..., in: program)
        return regex.matches(in: program, range: range).compactMap { match -> Int? in
            guard let numberRange = Range(match.range(at: 1), in: program) else { return nil }
            return Int(program[numberRange])
        }.max() ?? 0
    }

    // MARK: - Reading the reply

    /// The program in a reply streamed so far: what follows the opening ``` line, up to the closing
    /// one. A reply written without a fence is taken whole once it begins like code; before that
    /// (or for prose) it is empty.
    static func programIn(_ replySoFar: String) -> String {
        let text = replySoFar.replacingOccurrences(of: "\r\n", with: "\n")
        guard let fence = text.range(of: "```") else {
            let start = text.drop(while: { $0.isWhitespace })
            let looksLikeCode = ["step(", "const ", "keep(", "say(", "//"].contains { start.hasPrefix($0) }
            return looksLikeCode ? text : ""
        }
        guard let lineEnd = text[fence.upperBound...].firstIndex(of: "\n") else { return "" }
        let body = text[text.index(after: lineEnd)...]
        if let close = body.range(of: "\n```") {
            return String(body[..<text.index(after: close.lowerBound)])
        }
        // The closing fence may be arriving: hold back a trailing line that is only backticks.
        if let lastLineStart = body.lastIndex(of: "\n") {
            let tail = body[body.index(after: lastLineStart)...]
            if !tail.isEmpty, tail.allSatisfy({ $0 == "`" }) { return String(body[...lastLineStart]) }
        }
        return String(body)
    }

    /// Whether a follow-up's program keeps the drawing: true when it starts with keep(), false when
    /// it starts with anything else, nil while too little of it has come to tell (and `done` is false).
    static func keepsTheDrawing(_ programSoFar: String, done: Bool) -> Bool? {
        let lines = programSoFar.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        var start = ""
        for (index, rawLine) in lines.enumerated() {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            // A comment still being written isn't one yet.
            if line.isEmpty || (line.hasPrefix("//") && (index < lines.count - 1 || done)) { continue }
            start = line
            break
        }
        let keep = "keep()"
        if start.hasPrefix(keep) { return true }
        if start.isEmpty || keep.hasPrefix(start) || start.hasPrefix("//") { return done ? false : nil }
        return false
    }

    enum ReplyDecision: Equatable {
        case undecided
        case notADrawing
        case program
    }

    private static let codeStarts = ["step(", "const ", "let ", "var ", "keep(", "say(", "//", "```"]

    /// Whether the reply's first word is NONE (the model's "not a drawing"), as opposed to prose.
    static func isNone(_ replySoFar: String) -> Bool {
        let firstLine = replySoFar.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
            .first { !$0.trimmingCharacters(in: .whitespaces).isEmpty } ?? ""
        return firstLine.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: .punctuationCharacters).uppercased() == notAFollowUp
    }

    /// What the reply is, from its first non-blank line: NONE or a line of prose means the question
    /// is not answered by a drawing; a fence or a line that starts like code means a program.
    static func decide(replySoFar: String, done: Bool) -> ReplyDecision {
        let text = replySoFar.replacingOccurrences(of: "\r\n", with: "\n")
        if text.contains("```") { return .program }
        let lines = text.components(separatedBy: "\n")
        guard let firstIndex = lines.firstIndex(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty }) else {
            return done ? .notADrawing : .undecided
        }
        let line = lines[firstIndex].trimmingCharacters(in: .whitespaces)
        if line.trimmingCharacters(in: .punctuationCharacters).uppercased() == notAFollowUp { return .notADrawing }
        if codeStarts.contains(where: { line.hasPrefix($0) }) { return .program }
        if done { return .notADrawing }
        // Prose. One short line may be a preface before the fence ("Here's a drawing of the heart:"),
        // which some models write despite being told not to, so it waits; a second complete line, or
        // a long first one, is a spoken answer and settles it without waiting for the whole reply.
        let completeProseLines = lines.dropFirst(firstIndex).dropLast().filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }.count
        let proseCharacters = text.count
        return completeProseLines >= 2 || proseCharacters >= 160 ? .notADrawing : .undecided
    }
}
