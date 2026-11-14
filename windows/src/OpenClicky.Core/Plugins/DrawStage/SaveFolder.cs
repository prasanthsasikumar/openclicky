// Choosing where saved drawings go: Videos\OpenClicky when it can be written, a folder under LocalApplicationData
// when Windows Defender's Controlled Folder Access (or anything else) stops it. The probe is passed in, so the
// choice can be tested without touching real folders.

namespace OpenClicky.Core.Plugins.DrawStage;

public static class SaveFolder
{
    /// <summary><paramref name="preferred"/> when <paramref name="probe"/> can write in it, otherwise
    /// <paramref name="fallback"/> (which the probe makes ready too). The probe throws when the folder is no good:
    /// UnauthorizedAccessException from Controlled Folder Access, FileNotFoundException or DirectoryNotFoundException
    /// when the profile's folders don't exist, IOException for any other disk trouble.</summary>
    public static string Choose(string preferred, string fallback, Action<string> probe)
    {
        try
        {
            probe(preferred);
            return preferred;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            probe(fallback);
            return fallback;
        }
    }
}
