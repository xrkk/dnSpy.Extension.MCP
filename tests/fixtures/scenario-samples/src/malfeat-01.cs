// Scenario sample: malicious-feature static-analysis family (F03). Benign but feature-rich.
using System;
using System.Runtime.InteropServices;

namespace MalFeat {
    [AttributeUsage(AttributeTargets.All)]
    public class MarkerAttribute : Attribute {
        public string Tag { get; set; }
        public MarkerAttribute(string tag) { Tag = tag; }
    }

    [Marker("loader")]
    public static class Loader {
        public const string C2Url = "http://updates.example-c2.net/payload";
        public const string PersistenceKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string MutexName = "Global\\{4A1B2C3D-1111-2222-3333-444455556666}";
        public const int SleepMs = 30000;
        public const ushort MagicHeader = 0x4D5A;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualAlloc(IntPtr addr, uint size, uint alloc, uint protect);

        [Marker("inject")]
        public static bool AllocateStub(uint size) {
            return VirtualAlloc(IntPtr.Zero, size, 0x3000, 0x40);
        }
        public static string BuildCommand(string exe, string args) { return exe + " " + args; }
        public static string BeaconTemplate() { return C2Url + "/beacon?id="; }
    }

    [Marker("stealer")]
    public class CredGrabber {
        public string[] BrowserPaths = {
            @"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Login Data",
            @"%APPDATA%\Mozilla\Firefox\Profiles\logins.json"
        };
        public string Describe() { return Loader.PersistenceKey + Loader.MutexName; }
    }
}
