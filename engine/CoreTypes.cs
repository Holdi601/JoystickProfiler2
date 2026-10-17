using System;
using System.Collections.Generic;

namespace JoyPro
{
    public enum ModExists
    {
        NOT_EXISTENT,
        BINDNAME_EXISTS,
        KEYBIND_EXISTS,
        ALL_EXISTS,
        ERROR
    }

    public enum PlaneState
    {
        ACTIVE,
        DISABLED,
        NOT_EXISTENT,
        ERROR
    }

    [Serializable]
    public sealed class ForceFeedbackS
    {
        public bool invertX = false;
        public bool invertY = false;
        public bool swapAxis = false;
    }

    [Serializable]
    public sealed class DCSInput
    {
        public string ID;
        public string Plane;
        public string Title;
        public bool IsAxis;
        public string Cat;

        public DCSInput()
        {
            ID = string.Empty;
            Plane = string.Empty;
            Title = string.Empty;
            Cat = string.Empty;
        }

        public DCSInput Copy()
        {
            return new DCSInput
            {
                ID = ID,
                Plane = Plane,
                Title = Title,
                IsAxis = IsAxis,
                Cat = Cat
            };
        }
    }

    [Serializable]
    public sealed class OtherGameInput
    {
        public string ID;
        public string Game;
        public string Title;
        public bool IsAxis;
        public string Plane;
        public string categ;
        public string default_keyboard = string.Empty;
        public string default_gamepad = string.Empty;
        public string default_joystick = string.Empty;
        public string default_input = string.Empty;

        public string Category
        {
            get { return categ ?? string.Empty; }
            set { categ = value; }
        }

        public OtherGameInput()
        {
            ID = string.Empty;
            Game = "DCS";
            Title = string.Empty;
            Plane = string.Empty;
            categ = string.Empty;
        }

        public OtherGameInput Copy()
        {
            return new OtherGameInput
            {
                ID = ID,
                Game = Game,
                Title = Title,
                IsAxis = IsAxis,
                Plane = Plane,
                categ = categ,
                default_keyboard = default_keyboard,
                default_gamepad = default_gamepad,
                default_joystick = default_joystick,
                default_input = default_input
            };
        }
    }

    [Serializable]
    public sealed class Modifier
    {
        public string name;
        public string device;
        public string JPN;
        public string key;
        public bool sw;

        public Modifier()
        {
            name = string.Empty;
            device = string.Empty;
            JPN = string.Empty;
            key = string.Empty;
            sw = false;
        }

        public Modifier Copy()
        {
            return new Modifier
            {
                name = name,
                device = device,
                JPN = JPN,
                key = key,
                sw = sw
            };
        }

        public string toReformerString()
        {
            return (name ?? string.Empty) + "§" +
                   (device ?? string.Empty) + "§" +
                   (key ?? string.Empty) + "§" +
                   sw.ToString();
        }

        public static Modifier ReformerToMod(string reformer)
        {
            if (string.IsNullOrWhiteSpace(reformer))
                return null;

            string[] parts = reformer.Split('§');
            if (parts.Length < 3)
                return null;

            Modifier modifier = new Modifier();
            modifier.name = parts[0];
            modifier.device = parts[1];
            modifier.key = parts[2];
            modifier.sw = parts.Length > 3 && ParseBool(parts[3]);
            return modifier;
        }

        private static bool ParseBool(string value)
        {
            bool parsed;
            if (bool.TryParse(value, out parsed))
                return parsed;

            int number;
            if (int.TryParse(value, out number))
                return number != 0;

            return false;
        }
    }
}
