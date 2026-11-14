// Where saved drawings go: Videos\OpenClicky when it can be written, LocalApplicationData when Controlled Folder
// Access (or a folder that vanished) stops it. The probe is faked, so nothing here touches the real Videos folder.

using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Plugins;

public sealed class SaveFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencicky-savefolder-" + Guid.NewGuid().ToString("N"));

    private string Preferred => Path.Combine(_root, "Videos", "OpenClicky");
    private string Fallback => Path.Combine(_root, "Local", "OpenClicky", "Saved drawings");

    /// <summary>What the real probe does: make the folder, write a file in it, take the file away.</summary>
    private static void EnsureWritable(string folder)
    {
        Directory.CreateDirectory(folder);
        var probe = Path.Combine(folder, ".write-test");
        File.WriteAllText(probe, "");
        File.Delete(probe);
    }

    [Fact]
    public void A_writable_first_folder_is_used()
    {
        Assert.Equal(Preferred, SaveFolder.Choose(Preferred, Fallback, EnsureWritable));
        Assert.True(Directory.Exists(Preferred));
        Assert.False(Directory.Exists(Fallback));
    }

    [Fact]
    public void A_blocked_first_folder_falls_back()
    {
        var probed = new List<string>();
        var chosen = SaveFolder.Choose(Preferred, Fallback, folder =>
        {
            probed.Add(folder);
            if (folder == Preferred) throw new UnauthorizedAccessException("blocked by Controlled Folder Access");
            EnsureWritable(folder);
        });

        Assert.Equal(Fallback, chosen);
        Assert.Equal([Preferred, Fallback], probed);
        Assert.True(Directory.Exists(Fallback));
    }

    [Fact]
    public void A_first_folder_that_vanished_falls_back()
    {
        var chosen = SaveFolder.Choose(Preferred, Fallback, folder =>
        {
            if (folder == Preferred) throw new FileNotFoundException("Could not find file", folder);
            EnsureWritable(folder);
        });

        Assert.Equal(Fallback, chosen);
        Assert.True(Directory.Exists(Fallback));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
