using System.Buffers;

namespace EpicManifestParser;

/// <summary>
/// A slice of an array rented from <see cref="ArrayPool{T}.Shared"/>. Whoever holds it owns the array and must return it once.
/// </summary>
internal readonly struct RentedBuffer
{
    public readonly byte[] Array;
    public readonly int Offset;
    public readonly int Length;

    public RentedBuffer(byte[] array, int offset, int length)
    {
        Array = array;
        Offset = offset;
        Length = length;
    }

    public Span<byte> Span => new(Array, Offset, Length);
    public ReadOnlyMemory<byte> Memory => new(Array, Offset, Length);

    public void Return() => ArrayPool<byte>.Shared.Return(Array);
}
