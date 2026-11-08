// Urdu: in the language list, said by Gemini in a picked voice with a Pakistani accent, heard by Gemini when this PC
// can't hear it (English words kept in English letters), and drawn in Urdu with its own font. No network.

using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class UrduTests
{
    [Fact]
    public void UrduIsOfferedEverywhereAndNamedInItsOwnScript()
    {
        Assert.Contains(ReplyLanguage.Choices, choice => choice.Code == "ur" && choice.Name == "اردو");
        Assert.Equal("Urdu", ReplyLanguage.EnglishName("ur"));
        Assert.Contains("Urdu", ReplyLanguage.Instruction("ur"));
    }

    [Fact]
    public void OnlyLanguagesThisPCHearsStayOnDevice()
    {
        Assert.True(ReplyLanguage.IsHeardOnDevice("en"));
        Assert.False(ReplyLanguage.IsHeardOnDevice("ur"));
    }

    [Fact]
    public void ThePreviewAndTheAccentFollowTheLanguage()
    {
        Assert.StartsWith("hi, i'm clicky", ReplyLanguage.PreviewSentence("en"));
        Assert.Contains("کلکی", ReplyLanguage.PreviewSentence("ur"));
        Assert.Null(ReplyLanguage.GeminiSpeakingStyle("en"));
        Assert.Contains("Pakistani", ReplyLanguage.GeminiSpeakingStyle("ur"));
        Assert.Null(ReplyLanguage.TranscriptionHint("en"));
        Assert.Contains("computer", ReplyLanguage.TranscriptionHint("ur"));
    }

    [Fact]
    public void GeminiSpeaksInThePickedVoiceWithTheStyleBeforeTheWords()
    {
        var request = CloudVoiceRequests.GeminiSpeech("g", "سلام", "Sulafat", "Read this in Urdu:");
        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
        Assert.Equal("Read this in Urdu:\nسلام", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Sulafat", body["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!.GetValue<string>());

        var plain = JsonNode.Parse(CloudVoiceRequests.GeminiSpeech("g", "hi").Content!.ReadAsStringAsync().Result)!;
        Assert.Equal("hi", plain["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Kore", plain["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnknownGeminiVoiceFallsBackToKore()
    {
        Assert.Equal(30, CloudVoiceRequests.GeminiVoices.Count);
        Assert.Equal("Kore", CloudVoiceRequests.GeminiVoiceFor(new OpenClickyShellSettings()));
        Assert.Equal("Kore", CloudVoiceRequests.GeminiVoiceFor(new OpenClickyShellSettings { GeminiVoice = "Nobody" }));
        Assert.Equal("Puck", CloudVoiceRequests.GeminiVoiceFor(new OpenClickyShellSettings { GeminiVoice = " puck " }));
    }

    [Fact]
    public void GeminiIsAskedToWriteUrduInUrduAndEnglishInEnglish()
    {
        var body = GeminiAudioTranscriptionProvider.RequestBody([1, 2, 3], "ur", "hint");
        var parts = body["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("audio/wav", parts[0]!["inline_data"]!["mime_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), parts[0]!["inline_data"]!["data"]!.GetValue<string>());
        var ask = parts[1]!["text"]!.GetValue<string>();
        Assert.Contains("mostly speaks Urdu", ask);
        Assert.Contains("English words in English letters", ask);
        Assert.EndsWith(" hint", ask);
    }

    [Fact]
    public void ABusyOrSlowGeminiIsAskedAgainOnItsLighterModel()
    {
        Assert.True(GeminiAudioTranscriptionProvider.IsBusy(null));
        Assert.True(GeminiAudioTranscriptionProvider.IsBusy(503));
        Assert.True(GeminiAudioTranscriptionProvider.IsBusy(429));
        Assert.False(GeminiAudioTranscriptionProvider.IsBusy(400));
        Assert.False(GeminiAudioTranscriptionProvider.IsBusy(403));
    }

    [Fact]
    public void GeminisTranscriptIsItsTextParts()
    {
        var answer = Encoding.UTF8.GetBytes("""{"candidates":[{"content":{"parts":[{"text":" \"یہ CPU ہے\" "}]}}]}""");
        Assert.Equal("یہ CPU ہے", GeminiAudioTranscriptionProvider.ParseTranscript(answer));
        Assert.Null(GeminiAudioTranscriptionProvider.ParseTranscript("{}"u8.ToArray()));
        Assert.Null(GeminiAudioTranscriptionProvider.ParseTranscript("not json"u8.ToArray()));
    }

    [Fact]
    public void TheTranscriptionHintGoesBeforeTheKeyterms()
    {
        Assert.Null(OpenAIAudioTranscriptionProvider.TranscriptionPromptText([], "en"));
        Assert.Equal(ReplyLanguage.TranscriptionHint("ur"), OpenAIAudioTranscriptionProvider.TranscriptionPromptText([], "ur"));
        var both = OpenAIAudioTranscriptionProvider.TranscriptionPromptText(["OpenClicky"], "ur")!;
        Assert.StartsWith(ReplyLanguage.TranscriptionHint("ur")!, both);
        Assert.Contains("OpenClicky", both);
    }

    [Fact]
    public void GeminiHearsUrduOnlyInPlaceOfAnOnDeviceEngineAndWithAKey()
    {
        var onDevice = new Provider("on device", configured: true);
        var gemini = new Provider("Gemini", configured: true);
        Assert.Same(gemini, BuddyTranscriptionProviderFactory.ForLanguage(onDevice, true, "ur", gemini));
        Assert.Same(onDevice, BuddyTranscriptionProviderFactory.ForLanguage(onDevice, true, "en", gemini));
        // A cloud transcriber the person chose hears Urdu itself.
        Assert.Same(onDevice, BuddyTranscriptionProviderFactory.ForLanguage(onDevice, false, "ur", gemini));
        Assert.Same(onDevice, BuddyTranscriptionProviderFactory.ForLanguage(onDevice, true, "ur", new Provider("Gemini", configured: false)));
    }

    [Fact]
    public void ADrawingInUrduSaysAndWritesUrduWhileTheProgramStaysEnglish()
    {
        var english = DrawStageLesson.SystemPrompt(1920, 1080);
        Assert.DoesNotContain("\nLANGUAGE\n", english);
        Assert.Equal(english, DrawStageLesson.SystemPrompt(1920, 1080, languageCode: "en"));
        var urdu = DrawStageLesson.SystemPrompt(1920, 1080, languageCode: "ur");
        Assert.StartsWith(english, urdu);
        Assert.Contains(ReplyLanguage.Instruction("ur"), urdu);
        Assert.Contains("\nLANGUAGE\n", urdu);
        Assert.Contains("is in Urdu", urdu);
        Assert.Contains("The program itself stays in English", urdu);
    }

    [Fact]
    public void TheScriptFontGoesToTheStageOnlyWhenOneIsPicked()
    {
        Assert.Null(JsonNode.Parse(DrawStageMessages.BeginStream(800, 600, DrawStagePace.Speech))!["scriptFont"]);
        Assert.Equal("IndoPak Nastaleeq",
            JsonNode.Parse(DrawStageMessages.BeginStream(800, 600, DrawStagePace.Speech, scriptFont: "IndoPak Nastaleeq"))!["scriptFont"]!.GetValue<string>());
    }

    // What the fast voice wrote down for spoken Urdu (in the app's log): Hindi script, Roman letters or Urdu script.
    [Theory]
    [InlineData("kay barish kaisy hoti hai 3d may batyien")]
    [InlineData("कैसे काम करती है? 3D में बताएं।")]
    [InlineData("मेरा मतलब बारिश बारिश कैसे बरसती है?")]
    [InlineData("यह 3D में एक्सप्लेन करें मुझे 3D में।")]
    [InlineData("3D drawing bana iski.")]
    [InlineData("بارش کیسے ہوتی ہے؟")]
    [InlineData("آسمان نیلا کیوں ہے")]
    [InlineData("show the heart in 3D")]
    public void AnUrduHowOrWhyQuestionOrAskingFor3DIsDrawn(string question) => Assert.True(DrawStageLesson.WantsADrawing(question));

    // Mentioning 3D is not asking for a drawing in 3D: these get a plain answer, as before Urdu came in.
    [Theory]
    [InlineData("what's the best 3d printer to buy")]
    [InlineData("is this game 2d or 3d")]
    [InlineData("export this as a 3d model")]
    [InlineData("which 3d software should I learn")]
    public void AnEnglishQuestionThatOnlyMentions3DIsNotDrawn(string question) => Assert.False(DrawStageLesson.WantsADrawing(question));

    [Theory]
    [InlineData("main file kaise save karun")]
    [InlineData("سیٹنگز کہاں ہیں")]
    [InlineData("मैं इसे कैसे खोलूँ कहाँ है")]
    [InlineData("shukriya bhai")]
    public void AnUrduHowDoIOrSmallTalkIsNot(string question) => Assert.False(DrawStageLesson.WantsADrawing(question));

    // The free voice picked, no account, a Gemini key, and Windows has no Urdu voice (this PC, 2026-11-04): the drawing said nothing.
    [Theory]
    [InlineData("account")]
    [InlineData("mac")]
    [InlineData("automatic")]
    public void WithNoUrduVoiceOnThisPCAVoiceWithAKeySpeaksUrdu(string picked)
    {
        var settings = new OpenClickyShellSettings { SpeechVoice = picked, GeminiApiKey = "test-key" };
        Assert.Equal(SpeechVoiceChoice.Gemini,
            SpeechVoiceChoices.Resolve(settings, hasAccount: false, kokoroInstalled: false, "ur", thisPCSpeaksLanguage: () => false));
    }

    [Fact]
    public void ThisPCsVoiceStaysWhenItSpeaksTheLanguageOrNothingElseCan()
    {
        var withKey = new OpenClickyShellSettings { SpeechVoice = "mac", GeminiApiKey = "test-key" };
        Assert.Equal(SpeechVoiceChoice.Mac, SpeechVoiceChoices.Resolve(withKey, false, false, "ur", thisPCSpeaksLanguage: () => true));
        var noKeys = new OpenClickyShellSettings { SpeechVoice = "account" };
        Assert.Equal(SpeechVoiceChoice.Mac, SpeechVoiceChoices.Resolve(noKeys, false, false, "ur", thisPCSpeaksLanguage: () => false));
    }

    private sealed class Provider(string name, bool configured) : IBuddyTranscriptionProvider
    {
        public string DisplayName => name;
        public bool RequiresSpeechRecognitionPermission => false;
        public bool IsConfigured => configured;
        public string? UnavailableExplanation => null;

        public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(IReadOnlyList<string> keyterms, Action<string> onTranscriptUpdate,
            Action<string> onFinalTranscriptReady, Action<Exception> onError, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
