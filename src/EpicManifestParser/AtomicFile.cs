using System.Text;

using Microsoft.Win32.SafeHandles;

namespace EpicManifestParser;

/// <summary>
/// Cache files that are complete by construction: a file with the final name is never partially written, also when the process
/// is cancelled or killed while writing.
/// </summary>
/// <remarks>
/// The data is not flushed to disk before the rename (a flush per chunk would slow down every download). After a power loss or
/// an operating system crash, the file system may therefore keep the new name with data that never reached the disk.
/// </remarks>
internal static class AtomicFile
{
    // ".{16 hex digits}.tmp"
    private const int TempSuffixLength = 1 + 16 + 4;
    // the limit of a file name on the common file systems: 255 UTF-16 units on NTFS, 255 bytes on ext4 and others
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// A unique temporary file name in the directory of <paramref name="path"/>, so that renaming it onto <paramref name="path"/>
    /// stays on one volume: <c>{path}.{random}.tmp</c>, or just <c>{random}.tmp</c> when the file name of <paramref name="path"/>
    /// is so long that the suffix would exceed the file name limit.
    /// </summary>
    public static string GetTempPath(string path)
    {
        string suffix = $".{Random.Shared.NextInt64():x16}.tmp";
        ReadOnlySpan<char> fileName = Path.GetFileName(path.AsSpan());
        // counted in UTF-8 bytes, which is at least the number of UTF-16 units
        if (Encoding.UTF8.GetByteCount(fileName) + TempSuffixLength <= MaxFileNameLength)
            return path + suffix;

        return string.Concat(path.AsSpan(0, path.Length - fileName.Length), suffix.AsSpan(1));
    }

    /// <summary>
    /// Writes <paramref name="data"/> to a uniquely named temporary file next to <paramref name="path"/> and renames it onto
    /// <paramref name="path"/>. Creates the directory if it does not exist. Deliberately not cancellable, so that data that has
    /// been downloaded gets persisted. The temporary file is deleted when the write or the rename fails.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if <paramref name="path"/> now holds <paramref name="data"/>. <see langword="false"/> if the rename
    /// failed because a file at <paramref name="path"/> could not be replaced (another writer, an open reader, a read-only file):
    /// that file stays, but nothing is known about its content, so the caller must not treat it as the data just written.
    /// </returns>
    public static async ValueTask<bool> WriteAsync(string path, ReadOnlyMemory<byte> data)
    {
        string tempPath = GetTempPath(path);

        try
        {
            using (SafeFileHandle handle = CreateTempFile(path, tempPath, data.Length))
            {
                await RandomAccess.WriteAsync(handle, data, 0).ConfigureAwait(false);
            }

            try
            {
                File.Move(tempPath, path, true);
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(path))
            {
                TryDelete(tempPath);
                return false;
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// <see cref="WriteAsync"/> for a cache: a failed write (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>)
    /// only means the data is not cached, the caller has it already.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if <paramref name="path"/> now holds <paramref name="data"/>; <see langword="false"/> if it was not
    /// written, also when another file stays at <paramref name="path"/>.
    /// </returns>
    public static async ValueTask<bool> TryWriteAsync(string path, ReadOnlyMemory<byte> data)
    {
        try
        {
            return await WriteAsync(path, data).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SafeFileHandle CreateTempFile(string path, string tempPath, long preallocationSize)
    {
        try
        {
            return File.OpenHandle(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None, preallocationSize);
        }
        catch (DirectoryNotFoundException) when (Path.GetDirectoryName(path) is { Length: not 0 } directory)
        {
            // the cache directory is created on the first write, once
            Directory.CreateDirectory(directory);
            return File.OpenHandle(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None, preallocationSize);
        }
    }

    /// <summary>
    /// Deletes a file, ignoring files that are missing, in use or not accessible.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
