// Ports SettingsTourTests.swift (the PointerPresenceDefault half lives in PointerPresenceTests):
// every step has its recording, the tour ends where it can be replayed, sidebar targets are named
// by page. Plus the controller, which the Mac left to manual runs: it clicks each new page in the
// sidebar, points at each row, says each line, and stops on request.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Settings;

namespace OpenClicky.Core.Tests.Settings;

public sealed class SettingsTourTests
{
    private static string? RecordedVoiceFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "macos", "OpenClicky", "OpenClicky", "RecordedVoice");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    [Fact]
    public void EveryStepHasItsRecording()
    {
        var folder = RecordedVoiceFolder();
        Assert.NotNull(folder);
        foreach (var step in SettingsTourStep.All)
        {
            Assert.True(File.Exists(Path.Combine(folder!, step.Line + ".mp3")), $"{step.Line}.mp3 is missing");
        }
    }

    [Fact]
    public void TheTourEndsWhereItCanBeReplayed()
    {
        Assert.Equal("general.settingsTour", SettingsTourStep.All[^1].TargetId);
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.General), SettingsTourStep.All[^1].Destination);
    }

    [Fact]
    public void EveryStepPointsAtARowTheCatalogHas()
    {
        var catalog = new SettingsCatalog(new OpenClicky.Core.Dictation.DictationSettings(Preferences.InMemory()),
            () => new OpenClickyShellSettings(), () => new OpenClicky.Core.Providers.AccountCapabilities(OpenClicky.Core.Providers.AccountKind.OwnKeys));
        var ids = catalog.AllItems.Select(item => item.Id).ToHashSet();
        foreach (var step in SettingsTourStep.All.Where(step => step.TargetId is not null)) Assert.Contains(step.TargetId!, ids);
    }

    [Fact]
    public void SidebarTargetsAreNamedByPage()
    {
        Assert.Equal("sidebar.page.voice", SettingsTourTargets.SidebarTargetId(DictationDestination.Of(DictationSettingsPage.Voice)));
        Assert.Equal("sidebar.section.words", SettingsTourTargets.SidebarTargetId(DictationDestination.Of(DictationSection.Words)));
    }

    [Fact]
    public void ThePointerRestsALittleInFromARowsLeftEdge()
    {
        Assert.Equal(new ScreenPoint(130, 25), SettingsTourTargets.PointerSpot(new ScreenRect(100, 10, 100, 30)));
        Assert.Equal(new ScreenPoint(220, 25), SettingsTourTargets.PointerSpot(new ScreenRect(100, 10, 800, 30)));
    }

    [Fact]
    public void ATargetThatUnregisteredOrHasNoSizeIsNotOnScreen()
    {
        var targets = new SettingsTourTargets();
        var registration = targets.Register("a", () => new ScreenRect(0, 0, 10, 10));
        targets.Register("empty", () => new ScreenRect(0, 0, 0, 10));
        Assert.NotNull(targets.ScreenRectFor("a"));
        Assert.Null(targets.ScreenRectFor("empty"));
        registration.Dispose();
        Assert.Null(targets.ScreenRectFor("a"));
    }

    private sealed class RecordingPointer : ISettingsTourPointer
    {
        public List<string> Events { get; } = [];
        public void BeginTour() => Events.Add("begin");
        public Task FlyTo(ScreenRect target, string caption) { Events.Add($"fly {caption}"); return Task.CompletedTask; }
        public Task Click(ScreenRect target) { Events.Add("click"); return Task.CompletedTask; }
        public void EndTour() => Events.Add("end");
    }

    private sealed class RecordingNarrator(Action<string>? onLine = null) : ISettingsTourNarrator
    {
        public List<string> Lines { get; } = [];
        public Task<bool> SayAsync(string line, CancellationToken cancellationToken)
        {
            Lines.Add(line);
            onLine?.Invoke(line);
            return Task.FromResult(true);
        }
        public void Stop() { }
    }

    private static SettingsTourTargets TargetsForEveryRow()
    {
        var targets = new SettingsTourTargets();
        foreach (var destination in DictationDestination.SidebarOrder)
            targets.Register(SettingsTourTargets.SidebarTargetId(destination), () => new ScreenRect(0, 0, 200, 30));
        foreach (var step in SettingsTourStep.All.Where(step => step.TargetId is not null))
            targets.Register(step.TargetId!, () => new ScreenRect(300, 100, 500, 40));
        return targets;
    }

    [Fact]
    public async Task TheTourVisitsEveryStepAndRemembersItPlayed()
    {
        var preferences = Preferences.InMemory();
        var pointer = new RecordingPointer();
        var narrator = new RecordingNarrator();
        var visited = new List<DictationDestination>();
        var model = new DictationWindowModel();
        model.Select(DictationSettingsPage.General);
        model.PropertyChanged += (_, arguments) => { if (arguments.PropertyName == nameof(DictationWindowModel.Destination)) visited.Add(model.Destination); };
        var controller = new SettingsTourController(preferences, pointer, narrator, TargetsForEveryRow(), (_, _) => Task.CompletedTask);

        Assert.True(controller.ShouldStartOnItsOwn(model, welcomeTourIsDone: true));
        await controller.Start(model);

        Assert.True(controller.HasPlayed);
        Assert.False(controller.IsRunning);
        Assert.False(controller.ShouldStartOnItsOwn(model, welcomeTourIsDone: true));
        Assert.Equal(SettingsTourStep.All.Select(step => step.Line), narrator.Lines);
        Assert.Equal("begin", pointer.Events[0]);
        Assert.Equal("end", pointer.Events[^1]);
        Assert.Contains("fly Dictation", pointer.Events);
        Assert.Contains("fly the key you hold", pointer.Events);
        // Dictation, Voice, Clicky, Your words, Shortcuts, Keys & Plans, then back to General.
        Assert.Equal(7, pointer.Events.Count(eventName => eventName == "click"));
        Assert.Equal(DictationDestination.Of(DictationSettingsPage.General), visited[^1]);
    }

    [Fact]
    public async Task StoppingEndsTheTourPartway()
    {
        var pointer = new RecordingPointer();
        SettingsTourController? controller = null;
        var narrator = new RecordingNarrator(line => { if (line == "settings-tour-dictation") controller!.Stop(); });
        controller = new SettingsTourController(Preferences.InMemory(), pointer, narrator, TargetsForEveryRow(), (_, token) => Task.Delay(0, token));
        var model = new DictationWindowModel();
        await controller.Start(model);
        Assert.False(controller.IsRunning);
        Assert.Equal(["settings-tour-intro", "settings-tour-dictation"], narrator.Lines);
        Assert.Equal("end", pointer.Events[^1]);
    }
}
