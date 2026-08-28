using System;

// ACC-017 satellite: a tiny library loaded at runtime by DynLoadFixture.
namespace Satellite {
    public static class Satellite {
        public static int Answer() { return 42; }
    }
}
