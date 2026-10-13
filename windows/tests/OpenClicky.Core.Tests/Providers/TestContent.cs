// Test helper: an HttpContent's text read synchronously (the bodies here are in memory).

namespace OpenClicky.Core.Tests.Providers;

internal static class TestContent
{
    public static string BodyText(this HttpContent content)
    {
        using var reader = new StreamReader(content.ReadAsStream());
        return reader.ReadToEnd();
    }
}
