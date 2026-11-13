namespace EpicManifestParser.UE;

internal sealed class FEncryptedData
{
    public uint8[] Data { get; }
    public FEncryptedData(uint8[] data) => Data = data;

    /// <exception cref="InvalidDataException">The section or the data length is invalid.</exception>
    internal static FEncryptedData ReadEncryptedData(ref ManifestReader reader)
    {
        // data size, version and the length of the data
        int32 end = reader.BeginSection(ManifestSection.EncryptedData, ManifestSection.ListHeaderSize, reader.Length);
        EEncryptedDataVersion dataVersion = reader.Read<EEncryptedDataVersion>();

        FEncryptedData result;

        if (dataVersion >= EEncryptedDataVersion.Original)
        {
            result = new FEncryptedData(reader.ReadArray<uint8>());
        }
        else
        {
            result = new FEncryptedData([]);
        }

        reader.EndSection(ManifestSection.EncryptedData, end);
        return result;
    }
}
