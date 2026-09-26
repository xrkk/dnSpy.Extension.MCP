using System;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>Matches dnSpy's removal notification with its subsequent process-exit message.</summary>
internal sealed class PendingExitMessage<TProcess> where TProcess : class {
	TProcess? process;
	string? sessionId;
	int generation;

	internal readonly struct Snapshot {
		public string? SessionId { get; }
		public int Generation { get; }
		public Snapshot(string? sessionId, int generation) {
			SessionId = sessionId;
			Generation = generation;
		}
	}

	public void Record(TProcess removedProcess, string? activeSessionId, int activeGeneration) {
		process = removedProcess;
		sessionId = activeSessionId;
		generation = activeGeneration;
	}

	public bool TryTake(TProcess messageProcess, TProcess? ownedProcess,
		string? activeSessionId, int activeGeneration, out Snapshot snapshot) {
		snapshot = default;
		if (!ReferenceEquals(process, messageProcess))
			return false;
		var recordedSessionId = sessionId;
		var recordedGeneration = generation;
		process = null;
		sessionId = null;
		generation = 0;
		if (!ReferenceEquals(messageProcess, ownedProcess) || recordedSessionId is null ||
			recordedSessionId != activeSessionId || recordedGeneration != activeGeneration)
			return false;
		snapshot = new Snapshot(recordedSessionId, recordedGeneration);
		return true;
	}
}
