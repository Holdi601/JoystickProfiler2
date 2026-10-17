// A stand-in for the BYOJoystick mod's DLL, written for JoyPro's tests (no code of the mod): the smoke test compiles it
// into BYOJoystick.dll and lets "vtolvr refresh-db" read its targets. It has the shape JoyPro's reader expects
// (engine/Games/VtolVrMod.cs): vehicle manager classes whose ShortName property returns the seat and whose methods add
// targets through <Category><Axis|AxisC|Button>(name, control name, ...) helper calls; the base manager adds one
// target to every vehicle with StaticButton. Target names are made up and start with "Fake".
using System;

namespace FakeByoj
{
    public abstract class Manager
    {
        public abstract string ShortName { get; }

        protected void Setup()
        {
            StaticButton("Fake Recenter", 9, (c, b, s) => { });
            CreateControls();
        }

        protected abstract void CreateControls();

        protected void StaticButton(string name, int category, Action<object, object, int> action) { }
        protected void FlightAxis<T>(string name, string control, Func<string, string, bool, bool, int, T> mapper, Action<T, object, int> action, int s = -1) { }
        protected void FlightAxisC<T>(string name, string control, Func<string, string, bool, bool, int, T> mapper, Action<T, object, int> action, int s = -1) { }
        protected void FlightButton<T>(string name, string control, Func<string, string, bool, bool, int, T> mapper, Action<T, object, int> action, int s = -1) { }
        protected void LightsButton<T>(string name, string control, Func<string, string, bool, bool, int, T> mapper, Action<T, object, int> action, int s = -1) { }
        protected void DisplayButton<T>(string name, string control, Func<string, string, bool, bool, int, T> mapper, Action<T, object, int> action, int s = -1) { }
        protected void Log(string text) { }

        protected static object Find(string name, string root, bool nullable, bool checkName, int idx) { return null; }
        protected static void Set(object control, object binding, int state) { }
    }

    public class FA26BManager : Manager
    {
        public override string ShortName { get { return "FA26B"; } }

        protected override void CreateControls()
        {
            Log("not a target");
            FlightAxisC("Fake Stick Pitch", "Joystick", Find, Set);
            FlightAxis("Fake Throttle", "Throttle", Find, Set);
            FlightButton("Fake Gear Toggle", "Landing Gear", Find, Set, 1);
            LightsButton("Fake Nav Lights", "Nav Lights", Find, Set);
            for (int i = 0; i < 2; i++)
                DisplayButton("Fake MFD Button", "MFD", Find, Set, i);
        }
    }

    public class AH94FrontManager : Manager
    {
        public override string ShortName { get { return "AH94Front"; } }

        protected override void CreateControls()
        {
            FlightAxisC("Fake Stick Pitch", "Cyclic", Find, Set);
            FlightAxis("Fake Collective", "Collective", Find, Set);
        }
    }
}
