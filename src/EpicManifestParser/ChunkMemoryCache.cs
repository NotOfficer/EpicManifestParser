using System.Buffers;

namespace EpicManifestParser;

/// <summary>
/// A small least-recently-used cache of decompressed chunks, used when there is no chunk cache directory.
/// </summary>
/// <remarks>
/// A linear search under one lock: the capacity is small and the alternative to a hit is a download.
/// Buffers are only returned to the pool while the lock is held, so no reader can still be copying from them.
/// </remarks>
internal sealed class ChunkMemoryCache
{
    private readonly Entry[] _entries;
    private readonly Lock _lock = new();
    private int _count;
    private long _clock;

    public ChunkMemoryCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _entries = new Entry[capacity];
    }

    public int Capacity => _entries.Length;

    public int Count
    {
        get
        {
            lock (_lock)
                return _count;
        }
    }

    /// <summary>
    /// Copies <c>destination.Length</c> bytes starting at <paramref name="chunkOffset"/> of the chunk, if it is cached.
    /// </summary>
    /// <exception cref="InvalidDataException">The range lies outside the chunk data.</exception>
    public bool TryCopy(FGuid guid, int chunkOffset, Span<byte> destination)
    {
        lock (_lock)
        {
            int index = IndexOf(guid);
            if (index < 0)
                return false;

            ref Entry entry = ref _entries[index];
            FChunkInfo.CopyChunkRange(new ReadOnlySpan<byte>(entry.Buffer, entry.Offset, entry.Length), chunkOffset, destination);
            entry.LastUse = ++_clock;
            return true;
        }
    }

    /// <summary>
    /// Adds a chunk and takes ownership of <paramref name="data"/>, evicting the least recently used chunk when full.
    /// </summary>
    public void Add(FGuid guid, RentedBuffer data)
    {
        lock (_lock)
        {
            if (IndexOf(guid) >= 0)
            {
                data.Return();
                return;
            }

            int index;
            if (_count < _entries.Length)
            {
                index = _count++;
            }
            else
            {
                index = 0;
                for (int i = 1; i < _entries.Length; i++)
                {
                    if (_entries[i].LastUse < _entries[index].LastUse)
                        index = i;
                }
                ArrayPool<byte>.Shared.Return(_entries[index].Buffer);
            }

            _entries[index] = new Entry(guid, data.Array, data.Offset, data.Length, ++_clock);
        }
    }

    internal bool Contains(FGuid guid)
    {
        lock (_lock)
            return IndexOf(guid) >= 0;
    }

    private int IndexOf(FGuid guid)
    {
        ReadOnlySpan<Entry> entries = new(_entries, 0, _count);
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Guid == guid)
                return i;
        }
        return -1;
    }

    private struct Entry
    {
        public readonly FGuid Guid;
        public readonly byte[] Buffer;
        public readonly int Offset;
        public readonly int Length;
        public long LastUse;

        public Entry(FGuid guid, byte[] buffer, int offset, int length, long lastUse)
        {
            Guid = guid;
            Buffer = buffer;
            Offset = offset;
            Length = length;
            LastUse = lastUse;
        }
    }
}
