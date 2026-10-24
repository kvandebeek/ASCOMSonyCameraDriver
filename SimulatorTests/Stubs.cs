// Test-only stand-ins for the parts of ASCOM.Utilities / ASCOM.Astrometry that need Windows + VB runtime.
// The driver is separately compiled against the real assemblies to check signatures.
using System;
using System.Collections.Generic;

namespace ASCOM.Utilities
{
    public class TraceLogger : IDisposable
    {
        public TraceLogger(string file, string type) { }
        public bool Enabled { get; set; }
        public void LogMessage(string identifier, string message) { }
        public void Dispose() { }
    }

    public class Util : IDisposable { public void Dispose() { } }

    public class Profile : IDisposable
    {
        public static Dictionary<string, string> Values = new Dictionary<string, string>();
        public string DeviceType { get; set; }
        public string GetValue(string driverId, string name, string subKey, string defaultValue)
        {
            string v; return Values.TryGetValue(name, out v) ? v : defaultValue;
        }
        public void WriteValue(string driverId, string name, string value) { Values[name] = value; }
        public void Register(string driverId, string description) { }
        public void Unregister(string driverId) { }
        public void Dispose() { }
    }
}

namespace ASCOM.Astrometry { class Placeholder { } }

namespace ASCOM.Astrometry.AstroUtils
{
    public class AstroUtils : IDisposable { public void Dispose() { } }
}
