using System.Globalization;
using System.Text;

namespace OpenClicky.Core.Platform;

public enum TextInsertionOutcome
{
    /// <summary>Ctrl+V was sent; the app in front received the text.</summary>
    Typed,
    /// <summary>Keystrokes could not be sent (nothing in front, or the app in front runs elevated and Windows drops our input): the text is on the clipboard.</summary>
    LeftOnClipboard,
}

/// <summary>Puts text into whatever app is in front (FrontAppTextInserter on the Mac).</summary>
public interface ITextInserter
{
    /// <summary>When the last paste or copy was made; the clipboard history monitor ignores the clipboard for 1.5 s after it.</summary>
    DateTimeOffset LastInsertedAt { get; }

    /// <summary>
    /// Clipboard + Ctrl+V. The previous clipboard (every format that can be copied) comes back 0.6 s
    /// later, but only if the clipboard still holds <paramref name="text"/>. Held Alt/Win/Shift are
    /// released (with the menu-suppressing tap first) so the paste is a plain Ctrl+V.
    /// </summary>
    TextInsertionOutcome Insert(string text);

    /// <summary>Puts text on the clipboard and leaves it there (cancels a pending restore).</summary>
    void CopyToClipboard(string text);

    /// <summary>Ctrl+A to the app in front (write-in-place selects the whole box first).</summary>
    bool SelectAll();

    /// <summary>
    /// Types text as KEYEVENTF_UNICODE keystrokes (≤ 20 UTF-16 units per SendInput batch, never
    /// splitting a character), stamped with <see cref="LiveTypingMarker"/>, leaving the clipboard
    /// alone. Line breaks go as Enter. False when input cannot be sent.
    /// <para>
    /// Held modifiers: on the Mac the events carry no flags. Windows merges injected keys with the
    /// physical modifier state, and Ctrl/Alt + a unicode key can trigger app shortcuts, so a
    /// physically held Ctrl, Alt or Win (Right Ctrl / Right Alt dictation keys) gets a synthetic
    /// key-up first (after the menu-suppressing tap for Alt/Win). The person's later physical release
    /// is then an unmatched key-up, which Windows ignores.
    /// </para>
    /// </summary>
    bool TypeAsKeystrokes(string text);
}

/// <summary>The pure part of live typing: cutting text into keyboard batches.</summary>
public static class LiveTypingChunker
{
    /// <summary>The most UTF-16 units one batch carries (the Mac's per-event limit, kept for parity).</summary>
    public const int MaximumUtf16UnitsPerKeyboardEvent = 20;

    /// <summary>Pieces of at most 20 UTF-16 units without splitting a character (surrogate pair, base + combining marks).</summary>
    public static IReadOnlyList<string> KeyboardChunks(string text)
    {
        var chunks = new List<string>();
        var currentChunk = new StringBuilder();
        var graphemes = StringInfo.GetTextElementEnumerator(text);
        while (graphemes.MoveNext())
        {
            var character = (string)graphemes.Current;
            if (currentChunk.Length + character.Length > MaximumUtf16UnitsPerKeyboardEvent && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
            }
            if (character.Length > MaximumUtf16UnitsPerKeyboardEvent)
            {
                // A grapheme longer than one batch (a long emoji sequence): its scalars go separately,
                // which still never splits a surrogate pair.
                foreach (var scalar in character.EnumerateRunes())
                {
                    var scalarText = scalar.ToString();
                    if (currentChunk.Length + scalarText.Length > MaximumUtf16UnitsPerKeyboardEvent && currentChunk.Length > 0)
                    {
                        chunks.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }
                    currentChunk.Append(scalarText);
                }
            }
            else
            {
                currentChunk.Append(character);
            }
        }
        if (currentChunk.Length > 0) chunks.Add(currentChunk.ToString());
        return chunks;
    }
}
