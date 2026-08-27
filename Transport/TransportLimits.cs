using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace dnSpy.Extension.MCP.Transport {
/// <summary>
/// CON-DYN-009 request-body hard limits. A known ContentLength64 above the limit is rejected
/// before reading; regardless of what the header says, the raw read is bounded to one byte past
/// the limit so a lying header can never cause an unbounded buffer. Strict UTF-8 decoding and
/// JSON parsing reject malformed input before any business dispatch.
/// </summary>
public static class BoundedBodyReader {
	public const int MaxBodyBytes = 1048576;
	public const int ReadCeilingBytes = MaxBodyBytes + 1;

	/// <summary>Decision of the raw-read stage.</summary>
	public enum BodyDecision {
		/// <summary>At most MaxBodyBytes raw bytes were read; proceed to decode.</summary>
		WithinLimit,
		/// <summary>The Content-Length header alone already exceeds the limit; reject without reading.</summary>
		HeaderTooLarge,
		/// <summary>The actual stream reached byte MaxBodyBytes+1; reject.</summary>
		StreamTooLarge,
	}

	public static bool HeaderExceedsLimit(long? contentLength) => contentLength > MaxBodyBytes;

	/// <summary>
	/// Reads at most ReadCeilingBytes raw bytes. Returns the bytes actually read (which may be
	/// MaxBodyBytes+1 when the stream is too large) and the decision.
	/// </summary>
	public static (byte[] Data, BodyDecision Decision) Read(Stream stream, long? contentLength) {
		if (HeaderExceedsLimit(contentLength))
			return (Array.Empty<byte>(), BodyDecision.HeaderTooLarge);
		var buffer = new byte[ReadCeilingBytes];
		int total = 0;
		while (total < ReadCeilingBytes) {
			int read = stream.Read(buffer, total, ReadCeilingBytes - total);
			if (read <= 0)
				break;
			total += read;
		}
		if (total > MaxBodyBytes)
			return (buffer, BodyDecision.StreamTooLarge);
		var exact = new byte[total];
		Array.Copy(buffer, exact, total);
		return (exact, BodyDecision.WithinLimit);
	}

	/// <summary>Strict UTF-8 decode: any invalid byte sequence (including lone surrogates) fails.</summary>
	public static bool TryStrictUtf8Decode(byte[] data, out string text) {
		try {
			text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
			return true;
		}
		catch (DecoderFallbackException) {
			text = string.Empty;
			return false;
		}
	}

	/// <summary>Strict JSON document parse (no comments, no trailing commas).</summary>
	public static bool TryParseJsonRpc(string text, out JsonDocument? document) {
		try {
			document = JsonDocument.Parse(text, new JsonDocumentOptions {
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
			});
			return true;
		}
		catch (JsonException) {
			document = null;
			return false;
		}
	}
}

/// <summary>
/// Non-blocking admission slots for the fixed transport concurrency limits (CON-DYN-009):
/// 16 parallel short requests, 8 long connections, 8 waits. Callers must release in a finally
/// block; reservation happens before any worker is created.
/// </summary>
public sealed class AdmissionGate : IDisposable {
	readonly SemaphoreSlim slots;

	public AdmissionGate(int capacity) => slots = new SemaphoreSlim(capacity, capacity);

	public int CurrentCount => slots.CurrentCount;

	/// <summary>Attempts to reserve one slot without blocking.</summary>
	public bool TryEnter() => slots.Wait(0);

	/// <summary>Releases exactly one slot.</summary>
	public void Release() => slots.Release();

	public void Dispose() => slots.Dispose();
}

/// <summary>
/// The fixed response-size budget (CON-DYN-009 / API-DYN-001.tool_result_bytes): the fully
/// rendered JSON-RPC response message must not exceed 8388608 strict UTF-8 bytes, counted
/// before the first network write. SSE framing is not counted.
/// </summary>
public static class ResponseBudget {
	public const int MaxResponseBytes = 8388608;

	public static int CountStrictUtf8Bytes(string rendered) => new UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(rendered);

	public static bool Fits(string rendered) => CountStrictUtf8Bytes(rendered) <= MaxResponseBytes;
}
}
