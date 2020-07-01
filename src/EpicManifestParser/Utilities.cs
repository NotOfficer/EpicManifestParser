using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EpicManifestParser
{
	internal static unsafe class Utilities
	{
		public static readonly ASCIIEncoding _encoding = new ASCIIEncoding();
		private const byte _zeroChar = (byte)'0';

		public static string GetPString(this ref Utf8JsonReader reader)
		{
			var span = reader.ValueSpan;

			if (span.IsEmpty)
			{
				return string.Empty;
			}

			fixed (byte* p = &span.GetPinnableReference())
			{
				return _encoding.GetString(p, span.Length);
			}
		}

		public static T StringBlobTo<T>(ReadOnlySpan<byte> hash) where T : unmanaged
		{
			var buffer = stackalloc byte[sizeof(T)];
			var pBuffer = buffer;

			fixed (byte* p = &hash.GetPinnableReference())
			{
				var pBytes = p;

				for (var i = 0; i < hash.Length; i += 3)
				{
					var c1 = *pBytes++ - _zeroChar;
					var c2 = *pBytes++ - _zeroChar;
					var c3 = *pBytes++ - _zeroChar;

					var b = (byte)(c1 * 100 + c2 * 10 + c3);
					*pBuffer++ = b;
				}
			}

			return *(T*)buffer;
		}

		public static string StringBlobToHexString(ReadOnlySpan<byte> stringBlob)
		{
			var length = stringBlob.Length / 3;
			var buffer = stackalloc byte[length];
			var pBuffer = buffer;

			fixed (byte* p = &stringBlob.GetPinnableReference())
			{
				var pBytes = p;

				for (var i = 0; i < stringBlob.Length; i += 3)
				{
					var c1 = *pBytes++ - _zeroChar;
					var c2 = *pBytes++ - _zeroChar;
					var c3 = *pBytes++ - _zeroChar;

					var b = (byte)(c1 * 100 + c2 * 10 + c3);
					*pBuffer++ = b;
				}
			}

			var lookupP = _lookup32UnsafeP;
			var result = new string(char.MinValue, length * 2);

			fixed (char* resultP = result)
			{
				var resultP2 = (uint*)resultP;

				for (var i = 0; i < length; i++)
				{
					resultP2[i] = lookupP[buffer[i]];
				}
			}

			return result;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static byte GetByte(ReadOnlySpan<byte> stringBlob)
		{
			fixed (byte* p = &stringBlob.GetPinnableReference())
			{
				var pBytes = p;
				var b1 = *pBytes++ - _zeroChar;
				var b2 = *pBytes++ - _zeroChar;
				var b3 = *pBytes - _zeroChar;

				return (byte)(b1 * 100 + b2 * 10 + b3);
			}
		}

		private static readonly uint[] _lookup32Unsafe = CreateLookup32Unsafe();
		private static readonly uint* _lookup32UnsafeP = (uint*)GCHandle.Alloc(_lookup32Unsafe, GCHandleType.Pinned).AddrOfPinnedObject();

		private static uint[] CreateLookup32Unsafe()
		{
			var result = new uint[256];

			for (var i = 0; i < 256; i++)
			{
				var s = i.ToString("X2");

				if (BitConverter.IsLittleEndian)
				{
					result[i] = s[0] + ((uint)s[1] << 16);
				}
				else
				{
					result[i] = s[1] + ((uint)s[0] << 16);
				}
			}

			return result;
		}
	}
}