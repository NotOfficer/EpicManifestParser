using System.Numerics;

namespace EpicManifestParser.UE;

// Port of FRollingHashConst::Init and FRollingHash::GetHashForDataSet, BuildPatchHash.cpp:
// https://github.com/EpicGames/UnrealEngine/blob/release/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchHash.cpp
/// <summary>
/// The rolling hash UE stores for every chunk (<see cref="FChunkInfo.Hash"/>, <c>RollingHash</c> in the chunk header).
/// </summary>
internal static class FRollingHash
{
    // the polynomial of CRC64 as defined in ECMA 182, in its reflected form
    private const uint64 HashPoly64 = 0xC96C5795D7870F42;

    private static readonly uint64[] HashTable = CreateHashTable();

    private static uint64[] CreateHashTable()
    {
        var table = new uint64[256];
        for (uint32 tableIdx = 0; tableIdx < 256; tableIdx++)
        {
            uint64 val = tableIdx;
            for (int32 shiftCount = 0; shiftCount < 8; shiftCount++)
                val = (val & 1) == 1 ? (val >> 1) ^ HashPoly64 : val >> 1;
            table[tableIdx] = val;
        }
        return table;
    }

    /// <summary>
    /// The hash of a whole data set: for each byte, the state is rotated left by one bit and combined with the byte's table entry.
    /// </summary>
    public static uint64 GetHashForDataSet(ReadOnlySpan<uint8> dataSet)
    {
        ReadOnlySpan<uint64> table = HashTable;
        uint64 hashState = 0;
        foreach (uint8 value in dataSet)
            hashState = BitOperations.RotateLeft(hashState, 1) ^ table[value];
        return hashState;
    }
}
