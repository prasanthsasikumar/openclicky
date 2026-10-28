// Moonshine's quiet edges: a take that opens with a long quiet spell (held while the model loaded)
// is cut down to the voice plus a little air on each side before Moonshine reads it.

using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class MoonshineTrimTests
{
    private const int BytesPerSecond = 16_000 * 2;

    private static byte[] Samples(double seconds, short amplitude, int period = 40)
    {
        var bytes = new byte[(int)(BytesPerSecond * seconds)];
        for (var index = 0; index < bytes.Length / 2; index++)
        {
            var value = amplitude == 0 ? (short)0 : (short)(index % period < period / 2 ? amplitude : -amplitude);
            bytes[index * 2] = (byte)(value & 0xFF);
            bytes[index * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return bytes;
    }

    [Fact]
    public void LongQuietBeforeAndAfterTheVoiceIsCutToAShortEdge()
    {
        var take = Samples(2.0, 30).Concat(Samples(1.5, 8000)).Concat(Samples(1.0, 30)).ToArray();
        var trimmed = MoonshineDictation.TrimQuietEdges(take);
        var expected = (1.5 + 2 * MoonshineDictation.QuietEdgeSeconds) * BytesPerSecond;
        Assert.InRange(trimmed.Length, expected - 1280, expected + 1280);
    }

    [Fact]
    public void AVoiceWithLittleQuietAroundItIsLeftAlone()
    {
        var take = Samples(0.1, 0).Concat(Samples(2.0, 8000)).Concat(Samples(0.1, 0)).ToArray();
        Assert.Same(take, MoonshineDictation.TrimQuietEdges(take));
    }

    [Fact]
    public void ATakeWithNoVoiceAndAnEmptyOneAreLeftAlone()
    {
        var hum = Samples(3.0, 40);
        Assert.Same(hum, MoonshineDictation.TrimQuietEdges(hum));
        var empty = Array.Empty<byte>();
        Assert.Same(empty, MoonshineDictation.TrimQuietEdges(empty));
    }
}
