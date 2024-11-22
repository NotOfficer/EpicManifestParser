global using EpicManifestParser.UE;

#if NET9_0_OR_GREATER
global using ManifestData = System.Span<byte>;
global using ManifestRoData = System.ReadOnlySpan<byte>;
global using ManifestReader = GenericReader.GenericSpanReader;
#else
global using ManifestData = System.Memory<byte>;
global using ManifestRoData = System.ReadOnlyMemory<byte>;
global using ManifestReader = GenericReader.GenericBufferReader;
#endif
