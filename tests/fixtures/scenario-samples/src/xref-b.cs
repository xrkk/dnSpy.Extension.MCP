// Scenario sample (F08 part B): library defining interface + helpers.
using System;

namespace XRefLib {
    public interface IShape { int Area(); }
    public class Circle : IShape {
        public int Radius = 3;
        public int Area() { return 3 * Radius * Radius; }
    }
    public static class Geometry {
        public static int Describe(IShape s) { return s.Area() * 2; }
        public static Circle Unit() { return new Circle { Radius = 1 }; }
    }
}
