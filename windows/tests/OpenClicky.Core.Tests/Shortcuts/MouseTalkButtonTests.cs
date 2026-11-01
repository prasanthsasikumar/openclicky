// Talking with a mouse button: which low-level mouse messages are which button, and the stored choice.

using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Tests.Shortcuts;

public class MouseTalkButtonTests
{
    [Theory]
    [InlineData(0x020B, 0x0001_0000u, MouseTalkButton.Back, true)]
    [InlineData(0x020C, 0x0001_0000u, MouseTalkButton.Back, false)]
    [InlineData(0x020B, 0x0002_0000u, MouseTalkButton.Forward, true)]
    [InlineData(0x020C, 0x0002_0000u, MouseTalkButton.Forward, false)]
    [InlineData(0x0207, 0u, MouseTalkButton.Middle, true)]
    [InlineData(0x0208, 0u, MouseTalkButton.Middle, false)]
    public void SideAndMiddleButtonsAreRead(int message, uint mouseData, MouseTalkButton button, bool isDown) =>
        Assert.Equal(new MouseButtonChange(button, isDown), MouseTalkButtons.Read(message, mouseData));

    [Theory]
    [InlineData(0x0201, 0u)]          // left button down
    [InlineData(0x0200, 0u)]          // a move
    [InlineData(0x020A, 0x0078_0000u)] // the wheel turning
    [InlineData(0x020B, 0x0003_0000u)] // an unknown side button
    public void EverythingElseIsLeftAlone(int message, uint mouseData) => Assert.Null(MouseTalkButtons.Read(message, mouseData));

    [Fact]
    public void TheChoiceIsStoredByName()
    {
        Assert.Equal(MouseTalkButton.Forward, MouseTalkButtons.FromRawValue("forward"));
        Assert.Equal(MouseTalkButton.Off, MouseTalkButtons.FromRawValue(null));
        Assert.Equal(MouseTalkButton.Off, MouseTalkButtons.FromRawValue("thumb"));
        Assert.Equal("back", MouseTalkButton.Back.RawValue());
    }
}
