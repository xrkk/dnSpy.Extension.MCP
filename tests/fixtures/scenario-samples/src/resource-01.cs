// Scenario sample (F06): assembly with embedded .resources (built with /res:strings.resources).
using System;
using System.Resources;

namespace ResourceSample {
    public class ResHolder {
        public string Get(string key) {
            var rm = new ResourceManager("ResourceSample.strings", typeof(ResHolder).Assembly);
            return rm.GetString(key);
        }
    }
}
