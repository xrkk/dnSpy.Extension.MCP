using System;
using System.Threading;

public static class P01Fixture {
    static volatile bool keepRunning = true;

    public static int Compute(int value) { return value * 2 + 1; }

    public static void Main(string[] args) {
        Console.WriteLine("p01-ready-" + Compute(args.Length));
        for (int i = 0; i < 2400 && keepRunning; i++) {
            Compute(i);
            Thread.Sleep(25);
        }
    }
}
