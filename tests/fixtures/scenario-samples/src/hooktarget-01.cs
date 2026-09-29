// Scenario sample: hook-target family (F02). Instance/virtual methods for Harmony/BepInEx workflows.
using System;

namespace HookTarget {
    public class Combatant {
        public int Health = 100;
        public virtual int Attack() { return Damage(); }
        public virtual int Damage() { return 10; }
        public int Heal(int amount) { Health += amount; return Health; }
        public int Heal(int amount, int cap) { Health = Math.Min(Health + amount, cap); return Health; }
    }
    public class Tank : Combatant {
        public override int Damage() { return 5; }
        public void ShieldUp() { Health += 50; }
    }
    public class Scout : Combatant {
        public override int Damage() { return 15; }
        public void Evade() { Health -= 1; }
    }
    public static class Battle {
        public static int Simulate(Combatant c) { return c.Attack() * 2; }
    }
}
