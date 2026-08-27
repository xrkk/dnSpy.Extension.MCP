#if NETFRAMEWORK
// net48 lacks the compiler-required IsExternalInit marker for `init` accessors (C# 9+).
// Standard polyfill; the type is never used at runtime.
namespace System.Runtime.CompilerServices {
	internal static class IsExternalInit {
	}
}
#endif

namespace System.Security.Cryptography {
	internal static class RandomNumberGeneratorShim {
		/// <summary>Same shape on both TFMs; on net10 this mirrors the static GetBytes API.</summary>
		public static byte[] GetBytes(int count) {
			var bytes = new byte[count];
			using (var rng = RandomNumberGenerator.Create())
				rng.GetBytes(bytes);
			return bytes;
		}
	}
}

namespace System {
	internal static class ConvertHexShim {
		/// <summary>net48 lacks Convert.ToHexString; same lowercase-uppercase semantics via BitConverter.</summary>
		public static string ToHexString(byte[] bytes) =>
			System.BitConverter.ToString(bytes).Replace("-", string.Empty);
	}
}
