// Scenario sample (F09): dense constant matrix for forensic workflows.
using System;

namespace ConstMatrix {
    [Obsolete("kept for forensic tests")]
    public static class Strings {
        public const string Alpha = "ALPHA-KEY-0001";
        public const string Beta = "beta://endpoint/42";
        public const string Gamma = @"C:\ProgramData\ConstMatrix\cache.db";
        public const string Delta = "SELECT * FROM secrets";
    }
    public static class Numbers {
        public const int Port = 8443;
        public const long BigSize = 1099511627776;
        public const double Ratio = 0.6180339887;
        public const byte Flags = 0x5A;
    }
    public class Consumer {
        public string Join() { return Strings.Alpha + Numbers.Port.ToString(); }
        public int Sum() { return Numbers.Port + Numbers.Flags; }
        public string PathOf() { return Strings.Gamma; }
    }
}
