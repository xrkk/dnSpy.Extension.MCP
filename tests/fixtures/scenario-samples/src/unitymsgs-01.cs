// Scenario sample (F10): Unity message surface (fake engine types, name-matched).
using System;

namespace UnityMsgs {
    public class Collider { }
    public class MonoBehaviour { }
    public class PlayerController : MonoBehaviour {
        int health = 100;
        void Awake() { health = 100; }
        void Start() { health -= 5; }
        void Update() { if (health > 0) health -= 1; }
        void OnTriggerEnter(Collider other) { health -= 10; }
        public int PeekHealth() { return health; }
    }
    public class EnemySpawner : MonoBehaviour {
        void Awake() { }
        void Start() { }
    }
}
