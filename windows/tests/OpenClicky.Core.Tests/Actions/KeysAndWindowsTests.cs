// press_keys, type_text and window: which keys a spoken request may press, how chords and sequences are read, the
// tool calls parsed into actions, and what the runner says for each result.

using System.Text.Json.Nodes;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Tests.Actions;

public class KeysAndWindowsTests
{
    [Theory]
    [InlineData("tab", "tab")]
    [InlineData("Shift+Tab", "shift+tab")]
    [InlineData("ctrl c", "ctrl+c")]
    [InlineData("control-shift-t", "ctrl+shift+t")]
    [InlineData("shift+ctrl+t", "ctrl+shift+t")]
    [InlineData("alt+left arrow", "alt+left")]
    [InlineData("page down", "pagedown")]
    [InlineData("F5", "f5")]
    [InlineData("enter", "enter")]
    public void AllowedChordsAreReadAndSaidBack(string text, string spoken) => Assert.Equal(spoken, KeyChords.Parse(text)?.Spoken);

    [Theory]
    [InlineData("a")]              // words are typed, not pressed
    [InlineData("shift+a")]
    [InlineData("win+r")]          // never the Windows key
    [InlineData("alt+f4")]         // closing goes through the window tool
    [InlineData("ctrl+f4")]
    [InlineData("ctrl+w")]
    [InlineData("ctrl+shift+w")]
    [InlineData("ctrl+q")]
    [InlineData("shift+delete")]   // deleting for good
    [InlineData("ctrl+shift+delete")]
    [InlineData("ctrl+d")]
    [InlineData("ctrl+alt+delete")] // system UI
    [InlineData("ctrl+shift+esc")]
    [InlineData("ctrl+escape")]
    [InlineData("alt+esc")]
    [InlineData("alt+space")]
    [InlineData("alt+tab")]
    [InlineData("alt+shift+tab")]
    [InlineData("hyper+x")]
    [InlineData("")]
    public void OthersAreRefused(string text) => Assert.Null(KeyChords.Parse(text));

    [Fact]
    public void ChordCodesAreModifiersThenTheKey()
    {
        var chord = KeyChords.Parse("ctrl+shift+t")!;
        Assert.Equal([KeyChords.Control, KeyChords.Shift], chord.Modifiers);
        Assert.Equal('T', chord.Key);
        Assert.Equal(0x09, KeyChords.Parse("tab")!.Key);
    }

    [Theory]
    [InlineData("tab tab enter", new[] { "tab", "tab", "enter" })]
    [InlineData("ctrl+a, ctrl+c", new[] { "ctrl+a", "ctrl+c" })]
    [InlineData("shift tab", new[] { "shift+tab" })]
    [InlineData("page up, enter", new[] { "pageup", "enter" })]
    public void SequencesAreReadInOrder(string text, string[] spoken) =>
        Assert.Equal(spoken, KeyChords.ParseSequence(text)!.Select(chord => chord.Spoken));

    [Fact]
    public void ASequenceWithOneRefusedKeyIsRefusedWhole()
    {
        Assert.Null(KeyChords.ParseSequence("tab, win+r"));
        Assert.Null(KeyChords.ParseSequence(string.Join(", ", Enumerable.Repeat("tab", KeyChords.MaximumChords + 1))));
    }

    // MARK: - tool calls

    [Fact]
    public void TheToolsAreOfferedAndParsed()
    {
        var names = DesktopAction.AnthropicToolDefinitions().Select(tool => (string)tool["name"]!).ToList();
        Assert.Contains("press_keys", names);
        Assert.Contains("type_text", names);
        Assert.Contains("window", names);

        var press = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("press_keys", new JsonObject { ["keys"] = "tab", ["times"] = 3 }));
        Assert.Equal(new[] { "tab" }, ((DesktopAction.PressKeys)press.Value).Chords.Select(chord => chord.Spoken));
        Assert.Equal(3, ((DesktopAction.PressKeys)press.Value).Times);
        var many = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("press_keys", new JsonObject { ["keys"] = "enter", ["times"] = 500 }));
        Assert.Equal(KeyChords.MaximumTimes, ((DesktopAction.PressKeys)many.Value).Times);
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("press_keys", new JsonObject { ["keys"] = "win+r" }));

        var type = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("type_text", new JsonObject { ["text"] = "hello world" }));
        Assert.Equal(new DesktopAction.TypeText("hello world"), type.Value);
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("type_text", new JsonObject { ["text"] = "  " }));

        var focus = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("window", new JsonObject { ["action"] = "focus", ["app"] = "brave" }));
        Assert.Equal(new DesktopAction.ControlWindow(WindowCommand.Focus, "brave"), focus.Value);
        var maximize = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("window", new JsonObject { ["action"] = "maximize" }));
        Assert.Equal(new DesktopAction.ControlWindow(WindowCommand.Maximize, null), maximize.Value);
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("window", new JsonObject { ["action"] = "focus" }));
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("window", new JsonObject { ["action"] = "explode" }));
    }

    // MARK: - what is said

    private sealed class KeysExecutor : IDesktopActionExecutor
    {
        public KeyInputResult PressResult = KeyInputResult.Sent;
        public KeyInputResult TypeResult = KeyInputResult.Sent;
        public WindowControlResult WindowResult = new("Brave");
        public readonly List<string> Calls = [];

        public DesktopAppTarget? FindApplication(string name) => null;
        public bool LaunchApplication(DesktopAppTarget target) => false;
        public bool OpenUrl(Uri url) => false;
        public string? KnownFolderPath(string location) => null;
        public bool RevealInExplorer(string path) => false;
        public bool SetVolume(int percent) => false;
        public int? GetVolume() => null;
        public bool SendMediaKey(string action) => false;

        public KeyInputResult PressKeys(IReadOnlyList<KeyChord> chords, int times)
        {
            Calls.Add($"press {string.Join(",", chords.Select(chord => chord.Spoken))} x{times}");
            return PressResult;
        }

        public Task<KeyInputResult> TypeTextAsync(string text)
        {
            Calls.Add($"type {text}");
            return Task.FromResult(TypeResult);
        }

        public WindowControlResult ControlWindow(WindowCommand command, string? app)
        {
            Calls.Add($"window {command.RawValue()} {app}");
            return WindowResult;
        }
    }

    private static async Task<string> SayAsync(KeysExecutor executor, DesktopAction action) =>
        (await new DesktopActionRunner(executor, "C:\\Users\\me", "C:\\Users\\me\\OpenClicky").PerformAsync(action)).SpokenSentence;

    [Fact]
    public async Task EachResultIsSaidPlainly()
    {
        var executor = new KeysExecutor();
        Assert.Equal("Pressed tab 3 times.", await SayAsync(executor, new DesktopAction.PressKeys([KeyChords.Parse("tab")!], 3)));
        Assert.Equal("Pressed ctrl+a, ctrl+c.", await SayAsync(executor, new DesktopAction.PressKeys(KeyChords.ParseSequence("ctrl+a, ctrl+c")!, 1)));
        Assert.Equal("Typed it.", await SayAsync(executor, new DesktopAction.TypeText("hi")));
        Assert.Equal("Switched to Brave.", await SayAsync(executor, new DesktopAction.ControlWindow(WindowCommand.Focus, "brave")));
        Assert.Equal(["press tab x3", "press ctrl+a,ctrl+c x1", "type hi", "window focus brave"], executor.Calls);

        executor.TypeResult = KeyInputResult.PasswordField;
        Assert.Contains("password", await SayAsync(executor, new DesktopAction.TypeText("secret")));
        executor.WindowResult = new WindowControlResult(null, AppNotRunning: true);
        Assert.Equal("brave isn't open.", await SayAsync(executor, new DesktopAction.ControlWindow(WindowCommand.Focus, "brave")));
    }
}
