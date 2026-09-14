using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestIL
{
    public interface IDamageable
    {
        int TakeDamage(int amount);
    }

    public class Simple
    {
        public static int Inc(int value) => value + 1;

        public static int ProbePlain(int a, int b) => a + b;
    }

    public class Machines
    {
        public static int Counter = 0;

        public IEnumerator DoCoroutine()
        {
            Counter += 1;
            yield return null;
            Counter += 10;
        }

        public async Task<int> DoAsync()
        {
            await Task.Yield();
            Counter += 100;
            return Counter;
        }
    }

    public class G<T>
    {
        public T Echo(T value) => value;
    }
}
