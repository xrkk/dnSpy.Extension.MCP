// Scenario sample (F08 part A): consumes XRefLib (compile with /r:xref-b.dll).
using System;
using XRefLib;

namespace XRefApp {
    public class Square : IShape {
        public int Side = 4;
        public int Area() { return Side * Side; }
    }
    public class Client {
        public int TotalArea() { return Geometry.Describe(new Circle()) + Geometry.Describe(new Square()); }
        public IShape MakeUnit() { return Geometry.Unit(); }
    }
}
