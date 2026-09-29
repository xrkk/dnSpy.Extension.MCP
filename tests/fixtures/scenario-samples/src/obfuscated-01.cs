// Scenario sample: obfuscation-identification family (F04). Unreadable symbol names + delegate chains.
using System;

namespace a {
    public class b {
        public int c;
        public int d(int e) { return e ^ 0x55; }
        public int f(int e) { return d(e) + d(e + 1); }
    }
    public class g : b {
        public override bool Equals(object obj) { return false; }
        public int h(Func<int, int> i, int j) { return i(j); }
    }
    public static class k {
        public static Func<int, int> l = x => x + 7;
        public static int m(int n) { return new g().h(l, n); }
    }
}
