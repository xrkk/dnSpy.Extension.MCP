using dnSpy.Extension.MCP.Debugger;

static class Program {
    static int Main() {
        var gate = new PendingExitMessage<object>();
        var first = new object();
        var second = new object();
        int checks = 0;
        void Check(bool value, string label) {
            if (!value) throw new Exception(label);
            checks++;
        }
        gate.Record(first, "session-a", 1);
        Check(gate.TryTake(first, first, "session-a", 1, out var accepted)
            && accepted.SessionId == "session-a" && accepted.Generation == 1, "normal removed then message");
        Check(!gate.TryTake(first, first, "session-a", 1, out _), "duplicate message");

        gate.Record(first, "session-a", 1);
        Check(!gate.TryTake(first, second, "session-a", 2, out _), "old process after new owner");
        gate.Record(second, "session-a", 2);
        Check(!gate.TryTake(first, second, "session-a", 2, out _), "old message must preserve new pending");
        Check(gate.TryTake(second, second, "session-a", 2, out accepted)
            && accepted.Generation == 2, "current generation still settles");

        gate.Record(first, "session-a", 1);
        Check(!gate.TryTake(first, first, "session-a", 2, out _), "same session new generation");
        gate.Record(second, "session-a", 2);
        Check(gate.TryTake(second, second, "session-a", 2, out _), "new generation valid after stale");

        gate.Record(first, "session-a", 1);
        Check(!gate.TryTake(first, first, null, 1, out _), "terminal session rejects residue");
        gate.Record(second, "session-b", 1);
        Check(!gate.TryTake(first, second, "session-b", 1, out _), "terminal residue cannot consume new pending");
        Check(gate.TryTake(second, second, "session-b", 1, out accepted)
            && accepted.SessionId == "session-b", "new session still settles");
        Console.WriteLine($"PASS {checks} pending callback identity/generation checks");
        return 0;
    }
}
