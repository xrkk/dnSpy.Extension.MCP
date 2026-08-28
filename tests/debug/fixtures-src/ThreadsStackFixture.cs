using System;
using System.Threading;

// ACC-013 fixture: a background thread walking a three-level call chain while the main thread
// idles, so list_threads sees >=2 threads and get_stack pages a >=4 frame stack.
internal static class ThreadsStackFixture {
    private static void Level3() { Thread.Sleep(20); }
    private static void Level2() { for (int i = 0; i < 100000; i++) Level3(); }
    private static void Level1() { for (int i = 0; i < 100000; i++) Level2(); }
    private static void Worker() { Level1(); }
    private static int Main(string[] args) {
        var t = new Thread(Worker);
        t.IsBackground = true;
        t.Start();
        while (true) { Thread.Sleep(50); }
    }
}
