using System;

namespace SpikeFixture {
    public sealed class Target {
        public int Field = 4;

        public int Helper(int value) => value + Field;

        public int Compute(int value) {
            try {
                return Helper(value) * 2 + 1;
            }
            catch (Exception) {
                return -1;
            }
        }
    }

    static class Program {
        static void Main() {
            // Keep the throwaway process alive long enough for both dnSpy debugger
            // architectures to complete their startup handshake. Repeated calls make the
            // post-bind breakpoint hit deterministic without changing the edited method.
            for (var i = 0; i < 120; i++) {
                System.Threading.Thread.Sleep(500);
                Console.WriteLine(new Target().Compute(3));
            }
        }
    }
}
