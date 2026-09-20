using System;
using System.Threading;

// T038 fixture for tests/python/test_live_debug_output_contract.py
// (test_live_locals_and_two_level_expansion_conform_to_published_schemas).
//
// The shared test expands a local named `expandPayload` two levels deep and asserts the
// semantic relationship between the numbers and texts at each level. The original run used
// an authoring-time binary whose method token and IL offset were hard-coded into the test;
// that binary is not recoverable from the repository. This fixture is the rebuildable
// replacement: a tight loop whose body keeps a fully assigned `expandPayload` local live
// across a call boundary, so the manifest-pinned breakpoint (the `call KeepLive` sequence
// point) lands with every field assigned and the object still reachable.
//
// Build (framework csc, debug semantics so locals/sequence points survive):
//   csc /nologo /optimize- /debug:pdbonly /platform:x64|anycpu /out:ExpandValuesFixture.exe ExpandValuesFixture.cs
// The consuming test resolves token/IL offset from a manifest bound to the exact build's
// SHA-256 (tests/debug/fixtures-src/build-expand-manifest) instead of hard-coding them.

namespace ExpandValuesFixtureNs
{
    /// <summary>Node shape the shared test expands: Number/Text plus a self-typed Child.</summary>
    public sealed class ExpandNode
    {
        public int Number;
        public string Text;
        public ExpandNode Child;
    }

    internal static class ExpandValuesFixture
    {
        // The breakpoint anchor: at KeepLive's entry (IL offset 0) the parameter
        // `expandPayload` is the fully built object — Main assigned Number, Text and the
        // complete Child before the call — so the shared test finds its local by name and
        // the two-level expansion sees every field assigned. Breakpoints at a method's
        // entry offset bind deterministically; mid-method offsets of a not-yet-JITted
        // frame were observed not to bind on the CorDebug path.
        private static void KeepLive(ExpandNode expandPayload)
        {
            // Never true for the values Main builds; the branch only keeps the argument
            // (and the caller's locals) observably live at the call site under /optimize-.
            if (expandPayload.Number == int.MinValue)
            {
                Console.WriteLine(expandPayload.Text);
            }
        }

        private static int Main(string[] args)
        {
            int n = 0;
            while (true)
            {
                var expandPayload = new ExpandNode
                {
                    Number = n,
                    Text = "expand-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Child = new ExpandNode
                    {
                        Number = n + 1,
                        Text = "child-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Child = null,
                    },
                };
                KeepLive(expandPayload);
                Thread.Sleep(15);
                n++;
            }
        }
    }
}
