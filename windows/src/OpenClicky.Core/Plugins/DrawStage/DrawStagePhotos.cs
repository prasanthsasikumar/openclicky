// Photo cards on the drawing stage: a real photo of the thing (a heart, a jet engine, a volcano) beside the drawing,
// found by the Teacher's Wikimedia Commons search (TeacherPictureFinder) and handed to the page as a data URL, since
// the page itself has no network.

using OpenClicky.Core.Plugins.Teacher;

namespace OpenClicky.Core.Plugins.DrawStage;

public static class DrawStagePhotos
{
    /// <summary>A finder for DrawStageController.FindPhoto; <paramref name="surface"/> measures images (null: any size).</summary>
    public static Func<string, string, CancellationToken, Task<DrawStagePhoto?>> Finder(Func<ITeacherBoardSurface?> surface) =>
        async (query, caption, cancellationToken) =>
        {
            var picture = await TeacherPictureFinder.FindAsync(query, caption.Length > 0 ? caption : query,
                data => surface()?.ImageSize(data) ?? (1, 1), cancellationToken).ConfigureAwait(false);
            return picture is null ? null : new DrawStagePhoto(picture.ImageData, DrawStageMessages.MediaTypeOf(picture.ImageData), picture.Credit);
        };
}
