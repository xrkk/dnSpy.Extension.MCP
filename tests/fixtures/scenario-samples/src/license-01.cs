// Scenario sample: license-gate family (F01). Deterministic, no external deps.
using System;

namespace LicenseSample {
    public static class LicenseStrings {
        public const string ProductName = "SuperApp Pro";
        public const string RegKeyPath = @"SOFTWARE\SuperApp\License";
        public const string TrialNotice = "Trial expired. Purchase a license.";
    }

    public class LicenseGate {
        const int KeyLength = 29;
        static readonly string[] KnownKeys = { "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", "ZZZZZ-YYYYY-XXXXX-WWWWW-VVVVV" };

        public bool Check(string key) {
            if (key == null) return false;
            if (key.Length != KeyLength) return false;
            foreach (var known in KnownKeys)
                if (key == known) return true;
            return false;
        }

        public string Validate(string key) { return Check(key) ? "VALID" : "INVALID"; }

        public int DaysRemaining(string key) { return Check(key) ? 365 : 0; }
    }

    public class AppMain {
        public string Run(string key) {
            var gate = new LicenseGate();
            return gate.Validate(key);
        }
        public string Banner(string key) {
            return LicenseGateCheckTwice(key) + "|" + LicenseStrings.TrialNotice.Length;
        }
        string LicenseGateCheckTwice(string key) {
            var gate = new LicenseGate();
            return (gate.Check(key) ? "1" : "0") + (gate.Check(key) ? "1" : "0");
        }
    }
}
