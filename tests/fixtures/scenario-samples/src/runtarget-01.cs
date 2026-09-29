// Scenario sample (F05): deterministic net48 console target for debug workflows.
using System;

namespace RunTarget {
    public class Worker {
        public int LoopCount = 6;
        public int Accumulate(int seed) {
            int total = seed;
            for (int i = 0; i < LoopCount; i++) { total = Step(total, i); }
            return total;
        }
        public int Step(int value, int index) { return value + index * 2; }
        public string Report(int total) { return "TOTAL=" + total.ToString(); }
    }
    public static class Program {
        static int Main(string[] args) {
            var w = new Worker();
            int total = w.Accumulate(10);
            string line = w.Report(total);
            if (Environment.UserInteractive) Console.WriteLine(line);
            return total % 256;
        }
    }
}
