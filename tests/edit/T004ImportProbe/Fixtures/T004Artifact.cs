using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestIL
{
    // Shadow mirrors of the target types (a single assembly can declare the
    // same full names it references; CS0436 is suppressed).  The importer
    // matches members by full name plus structured signature.
    public class Simple
    {
        public static int Inc(int value) => value + 1;

        public static int ProbePlain(int a, int b) => a + b;

        public async Task<int> NewAsync(int seed)
        {
            await Task.Yield();
            return seed * 2 + 1;
        }

        public IEnumerable<int> NewIter(int count)
        {
            for (int index = 0; index < count; index++) yield return index * index;
        }

        public async Task<U> GMAsync<U>(U value)
        {
            await Task.Yield();
            return value;
        }
    }

    public class Machines
    {
        public static int Counter = 0;

        public System.Collections.IEnumerator DoCoroutine()
        {
            Counter += 2;
            yield return null;
            Counter += 20;
            yield return null;
            Counter += 200;
        }

        public async Task<int> DoAsync()
        {
            await Task.Yield();
            Counter += 500;
            return Counter;
        }
    }

    public class NewHost
    {
        public async Task<int> HostAsync(int seed)
        {
            await Task.Yield();
            await Task.Delay(1);
            return seed + 7;
        }

        public IEnumerable<int> HostIter()
        {
            yield return 3;
            yield return 4;
            yield return 5;
        }

        public async IAsyncEnumerable<int> HostStream()
        {
            await Task.Yield();
            yield return 10;
            await Task.Yield();
            yield return 20;
        }
    }

    public class H<T> where T : class
    {
        public async Task<T> HAsync(T value)
        {
            await Task.Yield();
            return value;
        }

        public IEnumerable<T> HIter(T value)
        {
            yield return value;
        }

        public class Inner<U>
        {
            public async Task<System.Collections.Generic.KeyValuePair<T, U>> PairAsync(T left, U right)
            {
                await Task.Yield();
                return new System.Collections.Generic.KeyValuePair<T, U>(left, right);
            }
        }
    }

    // New type implementing a target interface with an explicit method
    // override row (MethodOverride declaration synthesized via reference_add).
    public class Impl : IDamageable
    {
        int IDamageable.TakeDamage(int amount) => amount * 3;
    }
}

namespace T004Plain
{
    public class Simple
    {
        public static int Baseline() => 1;

        public async Task<int> ZeroSurfaceAsync(int seed)
        {
            await Task.Yield();
            return seed + 1000;
        }

        public IEnumerable<int> ZeroSurfaceIter(int count)
        {
            for (int index = 0; index < count; index++) yield return index + 100;
        }
    }
}
