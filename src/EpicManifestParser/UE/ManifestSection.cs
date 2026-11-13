using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace EpicManifestParser.UE;

/// <summary>
/// Checks for the sections of binary manifest data that <see cref="ManifestReader"/> cannot make: the data size that bounds each
/// section, and the element counts of the arrays this library allocates itself.
/// </summary>
/// <remarks>
/// The reader checks every read against the data before it reads or allocates: it throws <see cref="EndOfStreamException"/>
/// when the data ends first, which the caller turns into an <see cref="InvalidDataException"/> that names the section, and
/// <see cref="InvalidDataException"/> for a negative length prefix. Its checks stop at the end of the whole data, the ones here
/// at the end of a section, so that counts of chunk parts and other elements stay proportional to the bytes of their section.
/// The checks run for every chunk part, so the exceptions are thrown by separate methods to keep the checks inlined.
/// </remarks>
internal static class ManifestSection
{
    /// <summary>Data size and version.</summary>
    public const int32 HeaderSize = sizeof(int32) + sizeof(uint8);
    /// <summary>Data size, version and element count.</summary>
    public const int32 ListHeaderSize = HeaderSize + sizeof(int32);

    public const string Meta = "meta";
    public const string ChunkDataList = "chunk data list";
    public const string FileManifestList = "file manifest list";
    public const string ChunkPart = "chunk part";
    public const string CustomFields = "custom fields";
    public const string EncryptedData = "encrypted data";

    extension(ref ManifestReader reader)
    {
        /// <summary>
        /// Reads the data size that starts a section and checks that the section lies before <paramref name="limit"/>.
        /// </summary>
        /// <param name="name">The section name for the exception message.</param>
        /// <param name="minSize">The smallest valid data size, including the data size itself.</param>
        /// <param name="limit">The end of the enclosing data.</param>
        /// <returns>The position where the section ends.</returns>
        /// <exception cref="InvalidDataException">The data size is too small or the section does not fit.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int32 BeginSection(string name, int32 minSize, int32 limit)
        {
            int32 startPos = reader.Position;
            int32 available = limit - startPos;
            if (available < sizeof(int32))
                ThrowMissingSection(name, available);

            int32 dataSize = reader.Read<int32>();
            if (dataSize < minSize || dataSize > available)
                ThrowInvalidSectionSize(name, dataSize, minSize, available);

            return startPos + dataSize;
        }

        /// <summary>
        /// Checks an element count against the bytes left in the section, before anything is allocated for the elements.
        /// </summary>
        /// <param name="name">The section name for the exception message.</param>
        /// <param name="count">The element count that was read.</param>
        /// <param name="minElementSize">The smallest number of bytes one element takes.</param>
        /// <param name="end">The end of the section.</param>
        /// <exception cref="InvalidDataException">The count is negative or the elements cannot fit.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckCount(string name, int32 count, int32 minElementSize, int32 end)
        {
            int32 available = end - reader.Position;
            if ((uint32)count > (uint32)Math.Max(available, 0) / (uint32)minElementSize)
                ThrowInvalidCount(name, count, minElementSize, available);
        }

        /// <summary>
        /// Checks that the reads stayed inside the section and moves to its end, skipping data of newer versions.
        /// </summary>
        /// <exception cref="InvalidDataException">The data ran past the end of the section.</exception>
        public void EndSection(string name, int32 end)
        {
            if (reader.Position > end)
                ThrowOverrun(name, reader.Position - end);

            reader.Position = end;
        }
    }

    [DoesNotReturn]
    private static void ThrowMissingSection(string name, int32 available) =>
        throw new InvalidDataException($"Invalid manifest {name} section: it is missing, {Math.Max(available, 0)} bytes are left");

    [DoesNotReturn]
    private static void ThrowInvalidSectionSize(string name, int32 dataSize, int32 minSize, int32 available) =>
        throw new InvalidDataException($"Invalid manifest {name} section: a size of {dataSize} bytes is less than {minSize} or more than the {available} bytes left");

    [DoesNotReturn]
    private static void ThrowInvalidCount(string name, int32 count, int32 minElementSize, int32 available) =>
        throw new InvalidDataException($"Invalid manifest {name} section: an element count of {count} is negative or needs more than " +
                                       $"the {Math.Max(available, 0)} bytes left (at least {minElementSize} bytes per element)");

    [DoesNotReturn]
    private static void ThrowOverrun(string name, int32 overrun) =>
        throw new InvalidDataException($"Invalid manifest {name} section: its data runs {overrun} bytes past its end");

    /// <exception cref="InvalidDataException">Always.</exception>
    [DoesNotReturn]
    public static void ThrowDuplicateChunk(FGuid guid) =>
        throw new InvalidDataException($"Invalid manifest {ChunkDataList} section: chunk {guid} is listed twice");

    /// <exception cref="InvalidDataException">Always.</exception>
    [DoesNotReturn]
    public static void ThrowUnknownChunk(FGuid guid) =>
        throw new InvalidDataException($"Invalid manifest {ChunkPart} section: chunk {guid} is not in the chunk data list");
}
