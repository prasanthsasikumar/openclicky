// Port of SystemPromptGuardTests.swift with Windows prompt owners.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class SystemPromptGuardTests
{
    // A UAC prompt drawn on the normal desktop (UAC set not to dim), at the Mac test's bounds.
    private static readonly PromptWindow ConsentPrompt = new("consent.exe", new ScreenRect(626, 166, 260, 208));

    [Fact]
    public void APointOnThePromptsAllowButtonIsRefused()
    {
        Assert.True(SystemPromptGuard.IsOnSystemPrompt(new ScreenPoint(712, 301), new[] { ConsentPrompt }));
    }

    [Fact]
    public void APointJustOutsideThePromptStillCounts()
    {
        Assert.True(SystemPromptGuard.IsOnSystemPrompt(new ScreenPoint(900, 390), new[] { ConsentPrompt }));
    }

    [Fact]
    public void ThePointElsewhereOnScreenIsShown()
    {
        Assert.False(SystemPromptGuard.IsOnSystemPrompt(new ScreenPoint(137, 12), new[] { ConsentPrompt }));
    }

    [Fact]
    public void OtherAppsWindowsDoNotCount()
    {
        var notepad = new PromptWindow("notepad.exe", new ScreenRect(300, 200, 700, 500));
        Assert.False(SystemPromptGuard.IsOnSystemPrompt(new ScreenPoint(712, 301), new[] { notepad }));
    }

    [Theory]
    [InlineData("CredentialUIBroker.exe", null)]
    [InlineData("credentialuibroker.exe", null)]
    [InlineData("LockApp.exe", null)]
    [InlineData("explorer.exe", "Credential Dialog Xaml Host")]
    public void EveryWindowsPromptIsGuarded(string owner, string? className)
    {
        var prompt = new PromptWindow(owner, new ScreenRect(626, 166, 260, 208), className);
        Assert.True(SystemPromptGuard.IsOnSystemPrompt(new ScreenPoint(712, 301), () => new[] { prompt }));
    }
}
