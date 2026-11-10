import CoreGraphics
import Testing
@testable import OpenClicky

struct SpokenChunkerTests {
    @Test func theFirstSentenceIsReadyAsSoonAsItEnds() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "that number is your") == [])
        #expect(chunker.feed(accumulated: "that number is your sign-in code. it works") == ["that number is your sign-in code."])
        #expect(chunker.finish() == ["it works"])
    }

    @Test func laterSentencesAreGroupedSoTheVoiceMakesFewerRequests() {
        var chunker = SpokenChunker(laterChunkMinimum: 40)
        _ = chunker.feed(accumulated: "first one here. ")
        #expect(chunker.feed(accumulated: "first one here. short. also short. ") == [])
        #expect(chunker.feed(accumulated: "first one here. short. also short. now this makes it long enough. tail") == ["short. also short. now this makes it long enough."])
        #expect(chunker.finish() == ["tail"])
    }

    @Test func aLeadingPointTagIsHeldBackAndNeverSpoken() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "[POINT:120,4") == [])
        #expect(chunker.pointTag == nil)
        #expect(chunker.feed(accumulated: "[POINT:120,40:code] that's your code. more") == ["that's your code."])
        #expect(chunker.pointTag == "[POINT:120,40:code]")
        #expect(chunker.finish() == ["more"])
    }

    @Test func aTrailingPointTagIsFoundAtTheEndAndNotSpoken() {
        var chunker = SpokenChunker()
        _ = chunker.feed(accumulated: "see the save button. click it. [POINT:5,6:save]")
        #expect(chunker.finish() == ["click it."])
        #expect(chunker.pointTag == "[POINT:5,6:save]")
    }

    @Test func aSecondTagAfterTheLeadingOneIsNeverSpoken() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "[POINT:none] [POINT:10,20:x]") == [])
        #expect(chunker.finish() == [])
        #expect(chunker.pointTag == "[POINT:none]")

        var twoPoints = SpokenChunker()
        _ = twoPoints.feed(accumulated: "[POINT:10,20:x]\n[POINT:30,40:y]")
        #expect(twoPoints.finish() == [])
        #expect(twoPoints.pointTag == "[POINT:10,20:x]")
    }

    @Test func decimalsAndAnswersWithoutTagsStayWhole() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "it costs 3.5 dollars") == [])
        #expect(chunker.finish() == ["it costs 3.5 dollars"])
        #expect(chunker.pointTag == nil)
    }

    // F-01: every reply language the voice page offers gets its first sentence out early, with
    // that language's own sentence end.
    @Test(arguments: [
        ("hi", ["[POINT:none] यह", " एक परीक्षण वाक्य है। ", "दूसरा वाक्य है। ", "तीसरा वाक्य।"], "यह एक परीक्षण वाक्य है।"),
        ("mr", ["हे पहिले मराठी वाक्य आहे। ", "दुसरे वाक्य आहे।"], "हे पहिले मराठी वाक्य आहे।"),
        ("bn", ["এটি প্রথম বাংলা বাক্য। ", "দ্বিতীয় বাক্য।"], "এটি প্রথম বাংলা বাক্য।"),
        ("ur", ["[POINT:none] دل ایک پٹھا ہے۔", "یہ خون پمپ کرتا ہے۔"], "دل ایک پٹھا ہے۔"),
        ("ar", ["هل تريد أن تجرب مرة أخرى؟ ", "حسنا."], "هل تريد أن تجرب مرة أخرى؟"),
        ("ja", ["これは最初の文です。これは二番目の", "文です。三番目の", "文。"], "これは最初の文です。これは二番目の文です。"),
        ("zh", ["这是第一句话。这是第二句话。", "第三句。"], "这是第一句话。这是第二句话。"),
        ("zh-bang", ["这真是太好了！你要再试一次吗？", "好。"], "这真是太好了！你要再试一次吗？"),
        ("ta", ["இது முதல் வாக்கியம். ", "இரண்டாவது வாக்கியம்."], "இது முதல் வாக்கியம்."),
        ("ko", ["이것은 첫 번째 문장입니다. ", "두 번째 문장입니다."], "이것은 첫 번째 문장입니다."),
        ("en", ["that's your sign-in code. ", "it works."], "that's your sign-in code."),
    ])
    func theFirstSentenceIsReadyInEveryReplyLanguage(language: String, deltas: [String], firstPiece: String) {
        var chunker = SpokenChunker()
        var accumulated = ""
        var piecesWhileStreaming: [String] = []
        for delta in deltas.dropLast() {
            accumulated += delta
            piecesWhileStreaming += chunker.feed(accumulated: accumulated)
        }
        #expect(piecesWhileStreaming.first == firstPiece, "\(language)")
    }

    @Test func aNewLineEndsASentence() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "here is what to do first\nthen the next") == ["here is what to do first"])
    }

    // F-02: the voice must not say one word ("Dr.", "1.", "e.g."), stop, then go on.
    @Test func abbreviationsAndListNumbersDoNotEndTheFirstPiece() {
        var doctor = SpokenChunker()
        #expect(doctor.feed(accumulated: "[POINT:10,20:x] Dr. ") == [])
        #expect(doctor.feed(accumulated: "[POINT:10,20:x] Dr. Smith is here. He said hi.") == ["Dr. Smith is here."])

        var numbered = SpokenChunker()
        #expect(numbered.feed(accumulated: "1. Open the menu.\n") == ["1. Open the menu."])
        _ = numbered.feed(accumulated: "1. Open the menu.\n2. Click save.\n")
        #expect(numbered.finish() == ["2. Click save."])

        var forExample = SpokenChunker()
        #expect(forExample.feed(accumulated: "e.g. ") == [])
        #expect(forExample.feed(accumulated: "e.g. this one. And more.") == ["e.g. this one."])

        var tooShort = SpokenChunker()
        #expect(tooShort.feed(accumulated: "Yes. ") == [])
        #expect(tooShort.feed(accumulated: "Yes. That is the save button. more") == ["Yes. That is the save button."])
    }

    @Test func aYearEndingASentenceIsStillASentenceEnd() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "that came out in 1990. after") == ["that came out in 1990."])
    }

    // F-40: the backend speaks at most 400 characters of a piece (JavaScript length, UTF-16), so no
    // piece may be longer than about 350, whatever the language or punctuation.
    @Test(arguments: [
        ("english, no sentence end", String(repeating: "word ", count: 400)),
        ("english, one long sentence", String(repeating: "and then this, ", count: 140) + "end. "),
        ("hindi with dandas", String(repeating: "यह एक बहुत लंबा हिंदी वाक्य है जो चलता रहता है। ", count: 40)),
        ("hindi without dandas", String(repeating: "यह एक बहुत लंबा हिंदी वाक्य है ", count: 70)),
        ("chinese without punctuation", String(repeating: "这是一个很长的句子没有标点符号", count: 140)),
        ("japanese with full stops", String(repeating: "これはとても長い日本語の文章です。", count: 120)),
    ])
    func noPieceIsLongerThanTheVoiceSpeaks(name: String, answer: String) {
        var chunker = SpokenChunker()
        var pieces: [String] = []
        var accumulated = ""
        // Streamed in small deltas, the way the answer arrives.
        var remaining = Substring(answer)
        while !remaining.isEmpty {
            accumulated += remaining.prefix(7)
            remaining = remaining.dropFirst(7)
            pieces += chunker.feed(accumulated: accumulated)
        }
        pieces += chunker.finish()
        #expect(pieces.allSatisfy { $0.utf16.count <= 350 }, "\(name): \(pieces.map { $0.utf16.count })")
        #expect(pieces.count > 1, "\(name)")
        // Nothing is lost or reordered: the pieces are the answer, in order.
        let spokenLetters = pieces.joined().filter { !$0.isWhitespace }
        #expect(spokenLetters == answer.filter { !$0.isWhitespace }, "\(name)")
    }
}

@MainActor
struct PointTagPositionTests {
    @Test func aLeadingTagIsReadLikeATrailingOne() {
        let leading = CompanionManager.parsePointingCoordinates(from: "[POINT:120,40:code] that's your sign-in code.")
        #expect(leading.coordinate == CGPoint(x: 120, y: 40))
        #expect(leading.elementLabel == "code")
        #expect(leading.spokenText == "that's your sign-in code.")
        let trailing = CompanionManager.parsePointingCoordinates(from: "that's your sign-in code. [POINT:120,40:code]")
        #expect(trailing.coordinate == leading.coordinate)
        #expect(trailing.spokenText == leading.spokenText)
        #expect(CompanionManager.parsePointingCoordinates(from: "[POINT:none] html is markup.").spokenText == "html is markup.")
    }

    @Test func aTagOnItsOwnIsRead() {
        // The answer speaker hands over just the tag when the voice starts. This once recursed
        // forever (the moved tag was still "leading") and crashed the app.
        let tagAlone = CompanionManager.parsePointingCoordinates(from: "[POINT:120,40:code]")
        #expect(tagAlone.coordinate == CGPoint(x: 120, y: 40))
        #expect(tagAlone.elementLabel == "code")
        #expect(tagAlone.spokenText == "")
        #expect(CompanionManager.parsePointingCoordinates(from: "  [POINT:none]  ").coordinate == nil)
    }

    // Two tags with nothing else between them once recursed forever and crashed the app at the end
    // of an answer. The first tag that reads as a tag wins; neither is ever spoken.
    @Test func twoTagsWithNothingElseDoNotRecurse() {
        let pointThenNone = CompanionManager.parsePointingCoordinates(from: "[POINT:120,40:code] [POINT:none]")
        #expect(pointThenNone.coordinate == CGPoint(x: 120, y: 40))
        #expect(pointThenNone.elementLabel == "code")
        #expect(pointThenNone.spokenText == "")

        let noneThenPoint = CompanionManager.parsePointingCoordinates(from: "[POINT:none] [POINT:10,20:x]")
        #expect(noneThenPoint.coordinate == nil)
        #expect(noneThenPoint.elementLabel == "none")
        #expect(noneThenPoint.spokenText == "")

        let twoPoints = CompanionManager.parsePointingCoordinates(from: "[POINT:10,20:x]\n[POINT:30,40:y]")
        #expect(twoPoints.coordinate == CGPoint(x: 10, y: 20))
        #expect(twoPoints.spokenText == "")

        let twoNones = CompanionManager.parsePointingCoordinates(from: "[POINT:none] [POINT:none]")
        #expect(twoNones.coordinate == nil)
        #expect(twoNones.spokenText == "")
    }

    @Test func aLeadingAndATrailingTagAroundTextKeepTheTextAndTheFirstTag() {
        let both = CompanionManager.parsePointingCoordinates(from: "[POINT:10,20:save] click save. [POINT:30,40:other]")
        #expect(both.coordinate == CGPoint(x: 10, y: 20))
        #expect(both.spokenText == "click save.")
    }

    @Test func aTagInTheMiddleOrAnUnclosedTagIsLeftAsText() {
        let middle = CompanionManager.parsePointingCoordinates(from: "click [POINT:10,20:x] there.")
        #expect(middle.coordinate == nil)
        #expect(middle.spokenText == "click [POINT:10,20:x] there.")
        let unclosed = CompanionManager.parsePointingCoordinates(from: "[POINT:10,20:x")
        #expect(unclosed.coordinate == nil)
        #expect(unclosed.spokenText == "[POINT:10,20:x")
    }
}
