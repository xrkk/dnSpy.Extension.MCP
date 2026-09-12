using System;

public class StrongHost
{
    static int Main()
    {
        Console.WriteLine("signed");
        return 3;
    }

    public static int Shared() { return 11; }
}
