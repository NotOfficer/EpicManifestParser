namespace EpicManifestParser.Tests.Fixtures;

internal static class FileAssert
{
    /// <summary>
    /// Fails if a handle to <paramref name="path"/> is still open. Opening with <see cref="FileShare.None"/> fails while any
    /// other handle is open, on Linux too (.NET takes an exclusive lock there); deleting the file would succeed on Linux.
    /// </summary>
    public static void IsClosed(string path)
    {
        try
        {
            using FileStream _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            Assert.Fail($"{path} is still open: {ex.Message}");
        }
    }
}
