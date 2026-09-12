// P06 ACC-005 full-chain fixture: a net48 console host whose Machines class is
// the edit_import target (iterator + async state machines for generated-subtree
// replacement) and whose Main executes the replaced async kickoff so the real
// debugger can stop on the imported method at IL 0.
using System;
using System.Collections;
using System.Threading.Tasks;

namespace ImportHost
{
    public class Machines
    {
        public static int counter;

        public IEnumerator DoCoroutine()
        {
            counter++;
            yield return null;
            counter += 10;
        }

        public async void DoAsync()
        {
            await Task.Yield();
            counter += 100;
        }
    }

    public static class Program
    {
        public static int Main()
        {
            var machines = new Machines();
            machines.DoCoroutine().MoveNext();
            machines.DoAsync();
            return Machines.counter;
        }
    }
}
