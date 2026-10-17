#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.VJoy
{
    // HidHide's configuration is global for the PC: a cloak switch, a list of hidden devices (device instance paths,
    // "HID\VID_231D&PID_0125\c&8abe679&0&0000") and a list of applications. Without the inverse mode the application
    // list is an allow list (only listed programs see hidden devices); with the inverse mode it is a block list (only
    // listed programs lose them, every other program keeps seeing them).
    //
    // JoyPro's virtual joystick mode (docs/virtual-joystick.md) hides the physical devices it forwards to vJoy from the
    // games that use virtual mode, and only from them:
    //   - block list (the normal case): inverse mode on, the games' executables listed. Every other program (JoyPro's
    //     forwarder, the HidHide client, other tools) keeps seeing the devices.
    //   - allow list: only when the user already uses HidHide with an allow list (inverse off and own entries), which
    //     JoyPro does not turn upside down: the forwarder and JoyPro are allowed, every other program loses the devices.
    // vJoy devices the games need are never hidden while the session runs.
    //
    // Everything JoyPro changes is written to a journal BEFORE the change (phase "applying", then "active"). Restoring
    // undoes exactly the journal's changes against the configuration found then, so entries the user added meanwhile
    // stay. When cloaking was off before, the user's own (dormant) entries are parked for the session - removed and put
    // back on restore - so switching cloaking on does not wake up an old setup. A journal whose owner process (the GUI)
    // is gone is stale: the next start restores it.
    //
    // This file is shared: the engine compiles it (engine vjoy apply-hidhide / restore-hidhide / recover) and the GUI
    // links it (status at start, tests with a fake CLI). It has no dependency on either.
    public sealed class HidHideState
    {
        public const string PartCloak = "cloak";
        public const string PartInverse = "inverse";
        public const string PartDevices = "devices";
        public const string PartApps = "apps";
        public static readonly string[] Parts = { PartCloak, PartInverse, PartDevices, PartApps };

        public bool CloakOn { get; set; }
        public bool InverseOn { get; set; }
        public List<string> Devices { get; set; }
        public List<string> Apps { get; set; }
        // Parts that could not be read (HidHideCLI timed out, access denied, no answer). An unknown part is never
        // taken as "off" or "empty": a session is not started on it and a restore does not set it.
        public bool CloakUnknown { get; set; }
        public bool InverseUnknown { get; set; }
        public bool DevicesUnknown { get; set; }
        public bool AppsUnknown { get; set; }

        public HidHideState()
        {
            Devices = new List<string>();
            Apps = new List<string>();
        }

        public bool Complete
        {
            get { return !CloakUnknown && !InverseUnknown && !DevicesUnknown && !AppsUnknown; }
        }

        public List<string> UnknownParts()
        {
            List<string> parts = new List<string>();
            if (CloakUnknown)
                parts.Add("cloaking state");
            if (InverseUnknown)
                parts.Add("inverse mode");
            if (DevicesUnknown)
                parts.Add("hidden devices");
            if (AppsUnknown)
                parts.Add("program list");
            return parts;
        }

        public void SetUnknown(string part)
        {
            switch (part)
            {
                case PartCloak: CloakUnknown = true; break;
                case PartInverse: InverseUnknown = true; break;
                case PartDevices: DevicesUnknown = true; Devices.Clear(); break;
                case PartApps: AppsUnknown = true; Apps.Clear(); break;
            }
        }

        public HidHideState Clone()
        {
            return new HidHideState
            {
                CloakOn = CloakOn,
                InverseOn = InverseOn,
                Devices = new List<string>(Devices ?? new List<string>()),
                Apps = new List<string>(Apps ?? new List<string>()),
                CloakUnknown = CloakUnknown,
                InverseUnknown = InverseUnknown,
                DevicesUnknown = DevicesUnknown,
                AppsUnknown = AppsUnknown
            };
        }

        // One HidHideCLI answer for one part ("--cloak-state" -> "--cloak-on"). False when the answer does not hold the
        // part (an error text such as "Error code 0x0005 ... Access is denied", or nothing): then the part is unknown.
        public static bool TryParsePart(string part, string output, HidHideState state, out string problem)
        {
            problem = string.Empty;
            string text = output ?? string.Empty;
            if (text.IndexOf("Error code", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Zugriff verweigert", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                problem = FirstLine(text);
                state.SetUnknown(part);
                return false;
            }
            HidHideState parsed = Parse(text);
            bool cloak = false, inverse = false;
            foreach (string raw in text.Replace("\r", string.Empty).Split('\n'))
            {
                string command;
                string value;
                SplitCommand(raw.Trim(), out command, out value);
                if (command == "--cloak-on" || command == "--cloak-off")
                    cloak = true;
                if (command == "--inv-on" || command == "--inv-off")
                    inverse = true;
            }
            switch (part)
            {
                case PartCloak:
                    if (!cloak)
                        break;
                    state.CloakOn = parsed.CloakOn;
                    state.CloakUnknown = false;
                    return true;
                case PartInverse:
                    if (!inverse)
                        break;
                    state.InverseOn = parsed.InverseOn;
                    state.InverseUnknown = false;
                    return true;
                case PartDevices:
                    // An empty list prints nothing; any other text is not an answer.
                    if (parsed.Devices.Count == 0 && text.Trim().Length > 0)
                        break;
                    state.Devices = parsed.Devices;
                    state.DevicesUnknown = false;
                    return true;
                case PartApps:
                    if (parsed.Apps.Count == 0 && text.Trim().Length > 0)
                        break;
                    state.Apps = parsed.Apps;
                    state.AppsUnknown = false;
                    return true;
            }
            problem = "unexpected answer: " + FirstLine(text);
            state.SetUnknown(part);
            return false;
        }

        private static string FirstLine(string text)
        {
            string value = (text ?? string.Empty).Trim();
            int end = value.IndexOfAny(new[] { '\r', '\n' });
            value = end < 0 ? value : value.Substring(0, end);
            return value.Length == 0 ? "no answer" : value;
        }

        public bool HasDevice(string path)
        {
            return Contains(Devices, path);
        }

        public bool HasApp(string path)
        {
            return Contains(Apps, path);
        }

        internal static bool Contains(List<string> list, string value)
        {
            if (list == null || string.IsNullOrWhiteSpace(value))
                return false;
            string wanted = value.Trim();
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals((list[i] ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // The output of "HidHideCLI --cloak-state --inv-state --dev-list --app-list": every setting is printed as the
        // command that sets it ("--cloak-off", "--inv-on", "--dev-hide \"<path>\"", "--app-reg \"<path>\"").
        public static HidHideState Parse(string output)
        {
            HidHideState state = new HidHideState();
            foreach (string raw in (output ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string command;
                string value;
                SplitCommand(line, out command, out value);
                switch (command)
                {
                    case "--cloak-on": state.CloakOn = true; break;
                    case "--cloak-off": state.CloakOn = false; break;
                    case "--inv-on": state.InverseOn = true; break;
                    case "--inv-off": state.InverseOn = false; break;
                    case "--dev-hide":
                        if (value.Length > 0 && !state.HasDevice(value))
                            state.Devices.Add(value);
                        break;
                    case "--app-reg":
                        if (value.Length > 0 && !state.HasApp(value))
                            state.Apps.Add(value);
                        break;
                }
            }
            return state;
        }

        // Like Parse, but a missing cloaking or inverse line counts as unknown (journals; never "off").
        public static HidHideState ParseKnown(string output)
        {
            HidHideState state = Parse(output);
            bool cloak = false, inverse = false;
            foreach (string raw in (output ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                string command;
                string value;
                SplitCommand(raw.Trim(), out command, out value);
                cloak |= command == "--cloak-on" || command == "--cloak-off";
                inverse |= command == "--inv-on" || command == "--inv-off";
            }
            state.CloakUnknown = !cloak;
            state.InverseUnknown = !inverse;
            return state;
        }

        // The state in the CLI's own format (Parse reads it back).
        public string Describe()
        {
            // Unknown parts are left out (Parse then reads them as missing, see ParseKnown).
            StringBuilder sb = new StringBuilder();
            if (!CloakUnknown)
                sb.Append(CloakOn ? "--cloak-on" : "--cloak-off").Append("\r\n");
            if (!InverseUnknown)
                sb.Append(InverseOn ? "--inv-on" : "--inv-off").Append("\r\n");
            foreach (string device in Devices ?? new List<string>())
                sb.Append("--dev-hide \"").Append(device).Append("\"\r\n");
            foreach (string app in Apps ?? new List<string>())
                sb.Append("--app-reg \"").Append(app).Append("\"\r\n");
            return sb.ToString();
        }

        internal static void SplitCommand(string line, out string command, out string value)
        {
            int space = line.IndexOf(' ');
            command = (space < 0 ? line : line.Substring(0, space)).Trim().ToLowerInvariant();
            value = space < 0 ? string.Empty : line.Substring(space + 1).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
        }

        // HidHide's CLI semantics, for the fake CLI and to know what a plan leads to.
        public static HidHideState Simulate(HidHideState start, IList<string> arguments)
        {
            HidHideState state = (start ?? new HidHideState()).Clone();
            if (arguments == null)
                return state;
            for (int i = 0; i < arguments.Count; i++)
            {
                string command = (arguments[i] ?? string.Empty).Trim().ToLowerInvariant();
                string value = i + 1 < arguments.Count ? (arguments[i + 1] ?? string.Empty).Trim() : string.Empty;
                switch (command)
                {
                    case "--cloak-on": state.CloakOn = true; break;
                    case "--cloak-off": state.CloakOn = false; break;
                    case "--inv-on": state.InverseOn = true; break;
                    case "--inv-off": state.InverseOn = false; break;
                    case "--dev-hide":
                        if (!state.HasDevice(value))
                            state.Devices.Add(value);
                        i++;
                        break;
                    case "--dev-unhide":
                        state.Devices.RemoveAll(d => string.Equals(d.Trim(), value, StringComparison.OrdinalIgnoreCase));
                        i++;
                        break;
                    case "--app-reg":
                        if (!state.HasApp(value))
                            state.Apps.Add(value);
                        i++;
                        break;
                    case "--app-unreg":
                        state.Apps.RemoveAll(a => string.Equals(a.Trim(), value, StringComparison.OrdinalIgnoreCase));
                        i++;
                        break;
                }
            }
            return state;
        }
    }

    // HidHide's configuration as read, with what could not be read and why.
    public sealed class HidHideRead
    {
        public HidHideState State { get; set; }
        public List<string> Problems { get; set; }

        public HidHideRead()
        {
            State = new HidHideState();
            Problems = new List<string>();
        }

        public bool Complete
        {
            get { return State.Complete; }
        }
    }

    // The HidHide command line tool (HidHideCLI.exe), or a fake for tests.
    public interface IHidHideCli
    {
        bool Installed { get; }

        string Location { get; }

        // The current configuration, one HidHideCLI call per part, each with a time limit. Parts that cannot be read
        // (time limit, access denied: the hidden-device list needs administrator rights) are marked unknown.
        HidHideRead Read();

        // All commands in one HidHideCLI call; the caller runs elevated (the engine's elevated step). Returns the exit
        // code; HidHideCli.Cancelled when the user declined the UAC prompt, HidHideCli.TimedOut when it hung.
        int Apply(IList<string> arguments);
    }

    public static class HidHideCli
    {
        public const int Cancelled = -1223;
        public const int TimedOut = -1460;
    }

    public enum HidHideStrategy
    {
        BlockList,
        AllowList
    }

    // What virtual joystick mode needs from HidHide.
    public sealed class HidHideRequest
    {
        // Physical devices whose inputs JoyPro forwards to vJoy (device instance paths).
        public List<string> HideDevices { get; set; }
        // vJoy devices the games must see (device instance paths).
        public List<string> KeepVisible { get; set; }
        // Executables of the games with virtual mode (full paths).
        public List<string> GameExes { get; set; }
        // Programs that must keep seeing the physical devices: JoyPro's forwarder, the GUI (bind capture), the engine.
        public List<string> TrustedExes { get; set; }
        public List<string> Games { get; set; }
        // The process that owns the session (the GUI); 0 = started by hand (engine CLI), never stale by itself.
        public int OwnerPid { get; set; }
        public long OwnerStartTicks { get; set; }

        public HidHideRequest()
        {
            HideDevices = new List<string>();
            KeepVisible = new List<string>();
            GameExes = new List<string>();
            TrustedExes = new List<string>();
            Games = new List<string>();
        }
    }

    public sealed class HidHideJournal
    {
        public const string Header = "# JoyPro HidHide session v1";
        public const string PhaseApplying = "applying";
        public const string PhaseActive = "active";

        public string Phase { get; set; }
        public DateTime CreatedUtc { get; set; }
        public int OwnerPid { get; set; }
        public long OwnerStartTicks { get; set; }
        public HidHideStrategy Strategy { get; set; }
        public List<string> Games { get; set; }
        // The configuration before JoyPro changed anything.
        public HidHideState Previous { get; set; }
        // JoyPro switched cloaking on (it was off).
        public bool SetCloak { get; set; }
        // JoyPro switched the inverse mode (to the opposite of Previous.InverseOn).
        public bool SetInverse { get; set; }
        public List<string> AddedDevices { get; set; }
        public List<string> AddedApps { get; set; }
        // Entries JoyPro took out for the session (vJoy devices the games need, parked dormant entries, a trusted
        // program on a block list); restore puts them back.
        public List<string> RemovedDevices { get; set; }
        public List<string> RemovedApps { get; set; }

        public HidHideJournal()
        {
            Phase = PhaseApplying;
            CreatedUtc = DateTime.UtcNow;
            Games = new List<string>();
            Previous = new HidHideState();
            AddedDevices = new List<string>();
            AddedApps = new List<string>();
            RemovedDevices = new List<string>();
            RemovedApps = new List<string>();
        }

        public bool HasChanges
        {
            get
            {
                return SetCloak || SetInverse || AddedDevices.Count > 0 || AddedApps.Count > 0 ||
                       RemovedDevices.Count > 0 || RemovedApps.Count > 0;
            }
        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Header).Append("\r\n");
            sb.Append("phase=").Append(Phase).Append("\r\n");
            sb.Append("created=").Append(CreatedUtc.ToString("o", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("owner-pid=").Append(OwnerPid.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("owner-start=").Append(OwnerStartTicks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("strategy=").Append(Strategy == HidHideStrategy.AllowList ? "allow-list" : "block-list").Append("\r\n");
            sb.Append("games=").Append(string.Join(";", Games)).Append("\r\n");
            sb.Append("set-cloak=").Append(SetCloak ? "true" : "false").Append("\r\n");
            sb.Append("set-inverse=").Append(SetInverse ? "true" : "false").Append("\r\n");
            foreach (string value in AddedDevices)
                sb.Append("added-dev=").Append(value).Append("\r\n");
            foreach (string value in AddedApps)
                sb.Append("added-app=").Append(value).Append("\r\n");
            foreach (string value in RemovedDevices)
                sb.Append("removed-dev=").Append(value).Append("\r\n");
            foreach (string value in RemovedApps)
                sb.Append("removed-app=").Append(value).Append("\r\n");
            foreach (string line in Previous.Describe().Replace("\r", string.Empty).Split('\n'))
            {
                if (line.Length > 0)
                    sb.Append("previous=").Append(line).Append("\r\n");
            }
            return sb.ToString();
        }

        // Null when the text is not a journal.
        public static HidHideJournal Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith(Header, StringComparison.Ordinal))
                return null;
            HidHideJournal journal = new HidHideJournal();
            StringBuilder previous = new StringBuilder();
            foreach (string raw in text.Replace("\r", string.Empty).Split('\n'))
            {
                int split = raw.IndexOf('=');
                if (raw.StartsWith("#", StringComparison.Ordinal) || split <= 0)
                    continue;
                string key = raw.Substring(0, split).Trim().ToLowerInvariant();
                string value = raw.Substring(split + 1).Trim();
                switch (key)
                {
                    case "phase": journal.Phase = value; break;
                    case "created":
                        DateTime created;
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created))
                            journal.CreatedUtc = created.ToUniversalTime();
                        break;
                    case "owner-pid":
                        int pid;
                        journal.OwnerPid = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) ? pid : 0;
                        break;
                    case "owner-start":
                        long ticks;
                        journal.OwnerStartTicks = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ? ticks : 0;
                        break;
                    case "strategy":
                        journal.Strategy = string.Equals(value, "allow-list", StringComparison.OrdinalIgnoreCase) ? HidHideStrategy.AllowList : HidHideStrategy.BlockList;
                        break;
                    case "games":
                        journal.Games = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(g => g.Trim()).ToList();
                        break;
                    case "set-cloak": journal.SetCloak = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                    case "set-inverse": journal.SetInverse = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                    case "added-dev": AddUnique(journal.AddedDevices, value); break;
                    case "added-app": AddUnique(journal.AddedApps, value); break;
                    case "removed-dev": AddUnique(journal.RemovedDevices, value); break;
                    case "removed-app": AddUnique(journal.RemovedApps, value); break;
                    case "previous": previous.Append(value).Append('\n'); break;
                }
            }
            journal.Previous = HidHideState.ParseKnown(previous.ToString());
            return journal;
        }

        internal static void AddUnique(List<string> list, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !HidHideState.Contains(list, value))
                list.Add(value.Trim());
        }
    }

    public sealed class HidHidePlan
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public HidHideStrategy Strategy { get; set; }
        // The CLI arguments in order ("--dev-hide", "<path>", "--cloak-on", ...).
        public List<string> Arguments { get; set; }
        public HidHideJournal Journal { get; set; }
        // Plain-language notes for the user.
        public List<string> Notes { get; set; }

        public HidHidePlan()
        {
            Ok = true;
            Error = string.Empty;
            Arguments = new List<string>();
            Notes = new List<string>();
        }

        // One line per command, as HidHideCLI prints them.
        public List<string> CommandLines()
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < Arguments.Count; i++)
            {
                string argument = Arguments[i];
                if ((argument == "--dev-hide" || argument == "--dev-unhide" || argument == "--app-reg" || argument == "--app-unreg") && i + 1 < Arguments.Count)
                {
                    lines.Add(argument + " \"" + Arguments[i + 1] + "\"");
                    i++;
                }
                else
                {
                    lines.Add(argument);
                }
            }
            return lines;
        }
    }

    public static class HidHidePlanner
    {
        // Programs HidHide registers for itself; they are not "own entries" of the user.
        private static readonly string[] HidHideOwnApps = { "HidHideClient.exe" };

        // What to change so the request holds. With an active journal (a running session) only the missing parts are
        // added and recorded on top of it; Previous stays the state before the first change.
        public static HidHidePlan PlanApply(HidHideState current, HidHideRequest request, HidHideJournal running)
        {
            HidHidePlan plan = new HidHidePlan();
            current = current ?? new HidHideState();
            request = request ?? new HidHideRequest();
            if (!current.Complete)
            {
                plan.Ok = false;
                plan.Error = "HidHide's " + string.Join(", ", current.UnknownParts()) + " could not be read; nothing is planned on a guess.";
                return plan;
            }
            HidHideJournal journal = running ?? new HidHideJournal
            {
                Previous = current.Clone(),
                OwnerPid = request.OwnerPid,
                OwnerStartTicks = request.OwnerStartTicks
            };
            if (running != null && request.OwnerPid != 0)
            {
                journal.OwnerPid = request.OwnerPid;
                journal.OwnerStartTicks = request.OwnerStartTicks;
            }
            foreach (string game in request.Games)
                HidHideJournal.AddUnique(journal.Games, game);
            plan.Journal = journal;

            List<string> hide = Distinct(request.HideDevices);
            List<string> visible = Distinct(request.KeepVisible);
            List<string> games = Distinct(request.GameExes);
            List<string> trusted = Distinct(request.TrustedExes);
            foreach (string device in visible)
            {
                if (HidHideState.Contains(hide, device))
                {
                    plan.Ok = false;
                    plan.Error = "A vJoy device cannot be hidden: " + device;
                    return plan;
                }
            }
            foreach (string exe in games)
            {
                if (HidHideState.Contains(trusted, exe))
                {
                    plan.Ok = false;
                    plan.Error = "A game cannot be a trusted program at the same time: " + exe;
                    return plan;
                }
            }
            if (hide.Count == 0)
            {
                plan.Ok = false;
                plan.Error = "No physical device to hide (no device of the mapping is connected or found in HidHide's device list).";
                return plan;
            }

            // The strategy is fixed by the first enable of a session.
            HidHideStrategy strategy;
            bool switchInverse = false;
            if (running != null)
            {
                strategy = running.Strategy;
            }
            else if (current.InverseOn)
            {
                strategy = HidHideStrategy.BlockList;
            }
            else if (!current.CloakOn || OwnApps(current).Count == 0)
            {
                // Cloaking off: the user's entries are dormant and get parked; an allow list without own entries is
                // nobody's setup either. Both may switch to the block list.
                strategy = HidHideStrategy.BlockList;
                switchInverse = true;
            }
            else
            {
                strategy = HidHideStrategy.AllowList;
            }
            plan.Strategy = strategy;
            journal.Strategy = strategy;
            if (strategy == HidHideStrategy.BlockList && games.Count == 0)
            {
                plan.Ok = false;
                plan.Error = "No game executable is known for the games with virtual mode (set one with engine vjoy exe <game> --add <path>).";
                return plan;
            }

            List<string> args = plan.Arguments;
            HidHideState state = current.Clone();

            // Cloaking off before the first enable: park the user's dormant entries so switching cloaking on wakes up
            // nothing but JoyPro's session.
            if (running == null && !current.CloakOn)
            {
                foreach (string device in current.Devices.ToList())
                {
                    if (HidHideState.Contains(hide, device))
                        continue; // stays hidden; it was the user's entry, so it is not "added" either
                    Add(args, "--dev-unhide", device);
                    HidHideJournal.AddUnique(journal.RemovedDevices, device);
                }
                foreach (string app in current.Apps.ToList())
                {
                    bool keep = strategy == HidHideStrategy.BlockList ? HidHideState.Contains(games, app) : HidHideState.Contains(trusted, app);
                    if (keep)
                        continue;
                    Add(args, "--app-unreg", app);
                    HidHideJournal.AddUnique(journal.RemovedApps, app);
                }
                if (journal.RemovedDevices.Count + journal.RemovedApps.Count > 0)
                    plan.Notes.Add("HidHide was switched off. Its own lists (" + journal.RemovedDevices.Count.ToString(CultureInfo.InvariantCulture) + " device(s), " +
                                   journal.RemovedApps.Count.ToString(CultureInfo.InvariantCulture) + " program(s)) are put aside while virtual mode runs and come back exactly when it stops.");
                state = HidHideState.Simulate(state, args);
            }

            // vJoy devices must stay visible to the games.
            foreach (string device in visible)
            {
                if (!state.HasDevice(device))
                    continue;
                Add(args, "--dev-unhide", device);
                HidHideJournal.AddUnique(journal.RemovedDevices, device);
                plan.Notes.Add("The vJoy device " + device + " was hidden in HidHide; it is shown while virtual mode runs.");
            }

            foreach (string device in hide)
            {
                if (state.HasDevice(device))
                    continue;
                Add(args, "--dev-hide", device);
                HidHideJournal.AddUnique(journal.AddedDevices, device);
            }

            if (strategy == HidHideStrategy.BlockList)
            {
                foreach (string exe in trusted)
                {
                    if (!state.HasApp(exe))
                        continue;
                    // On a block list a trusted program would lose the devices: it comes off the list for the session.
                    Add(args, "--app-unreg", exe);
                    HidHideJournal.AddUnique(journal.RemovedApps, exe);
                }
                foreach (string exe in games)
                {
                    if (state.HasApp(exe))
                        continue;
                    Add(args, "--app-reg", exe);
                    HidHideJournal.AddUnique(journal.AddedApps, exe);
                }
                plan.Notes.Add("Only the games with virtual mode stop seeing the physical devices; every other program keeps seeing them.");
            }
            else
            {
                foreach (string exe in games)
                {
                    if (!state.HasApp(exe))
                        continue;
                    // On an allow list the game would still see the physical devices.
                    Add(args, "--app-unreg", exe);
                    HidHideJournal.AddUnique(journal.RemovedApps, exe);
                }
                foreach (string exe in trusted)
                {
                    if (state.HasApp(exe))
                        continue;
                    Add(args, "--app-reg", exe);
                    HidHideJournal.AddUnique(journal.AddedApps, exe);
                }
                plan.Notes.Add("HidHide is used as an allow list on this PC (your own setup), so JoyPro keeps it: while virtual mode runs, every program that is not on the list (all games included) stops seeing the hidden devices. JoyPro's forwarder and JoyPro itself are allowed.");
            }

            if (switchInverse && running == null && !current.InverseOn)
            {
                args.Add("--inv-on");
                journal.SetInverse = true;
            }

            // Cloaking last: nothing is hidden until the lists are right. Whenever JoyPro switches it on, restore
            // switches it off again.
            if (!current.CloakOn)
            {
                args.Add("--cloak-on");
                journal.SetCloak = true;
            }

            if (args.Count == 0)
                plan.Notes.Add("HidHide is already set up as virtual mode needs it.");
            return plan;
        }

        // Undoes exactly the journal's changes, against the configuration found now.
        public static HidHidePlan PlanRestore(HidHideJournal journal, HidHideState current)
        {
            HidHidePlan plan = new HidHidePlan();
            if (journal == null)
            {
                plan.Notes.Add("No JoyPro HidHide session: nothing to restore.");
                return plan;
            }
            plan.Journal = journal;
            plan.Strategy = journal.Strategy;
            current = current ?? new HidHideState();
            List<string> args = plan.Arguments;

            // Cloaking off first: whatever happens after, nothing is hidden by JoyPro's session any more.
            if (journal.SetCloak && current.CloakOn)
                args.Add("--cloak-off");
            foreach (string app in journal.AddedApps)
            {
                if (current.HasApp(app))
                    Add(args, "--app-unreg", app);
            }
            foreach (string device in journal.AddedDevices)
            {
                if (current.HasDevice(device))
                    Add(args, "--dev-unhide", device);
            }
            foreach (string device in journal.RemovedDevices)
            {
                if (!current.HasDevice(device))
                    Add(args, "--dev-hide", device);
            }
            foreach (string app in journal.RemovedApps)
            {
                if (!current.HasApp(app))
                    Add(args, "--app-reg", app);
            }
            // Only values that were read before the session are set back; an unknown one is left as it is.
            if (journal.SetInverse && journal.Previous.InverseUnknown)
                plan.Notes.Add("The inverse mode before the session is not known; it is left as it is.");
            else if (journal.SetInverse && current.InverseOn != journal.Previous.InverseOn)
                args.Add(journal.Previous.InverseOn ? "--inv-on" : "--inv-off");
            if (args.Count == 0)
                plan.Notes.Add("HidHide already has its previous configuration.");
            return plan;
        }

        // What differs between the configuration read back and the one the plan should have led to (empty = all done).
        public static List<string> Verify(HidHideState expected, HidHideState actual)
        {
            List<string> problems = new List<string>();
            if (expected == null || actual == null)
                return problems;
            if (expected.CloakOn != actual.CloakOn)
                problems.Add("Cloaking is " + (actual.CloakOn ? "on" : "off") + ", expected " + (expected.CloakOn ? "on" : "off") + ".");
            if (expected.InverseOn != actual.InverseOn)
                problems.Add("The inverse application list is " + (actual.InverseOn ? "on" : "off") + ", expected " + (expected.InverseOn ? "on" : "off") + ".");
            foreach (string device in expected.Devices.Where(d => !actual.HasDevice(d)))
                problems.Add("Device not hidden: " + device);
            foreach (string device in actual.Devices.Where(d => !expected.HasDevice(d)))
                problems.Add("Device still hidden: " + device);
            foreach (string app in expected.Apps.Where(a => !actual.HasApp(a)))
                problems.Add("Program not listed: " + app);
            foreach (string app in actual.Apps.Where(a => !expected.HasApp(a)))
                problems.Add("Program still listed: " + app);
            return problems;
        }

        private static List<string> OwnApps(HidHideState state)
        {
            return state.Apps.Where(a => !HidHideOwnApps.Any(own => string.Equals(Path.GetFileName(a ?? string.Empty), own, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private static List<string> Distinct(IEnumerable<string> values)
        {
            List<string> output = new List<string>();
            foreach (string value in values ?? Enumerable.Empty<string>())
                HidHideJournal.AddUnique(output, value);
            return output;
        }

        private static void Add(List<string> args, string command, string value)
        {
            args.Add(command);
            args.Add(value);
        }
    }

    public sealed class HidHideResult
    {
        public bool Ok { get; set; }
        public bool DryRun { get; set; }
        public bool Changed { get; set; }
        public bool Cancelled { get; set; }
        public string Message { get; set; }
        public string Strategy { get; set; }
        public List<string> Commands { get; set; }
        public List<string> Notes { get; set; }
        public List<string> Problems { get; set; }
        public string SessionPhase { get; set; }

        public HidHideResult()
        {
            Message = string.Empty;
            Strategy = string.Empty;
            SessionPhase = string.Empty;
            Commands = new List<string>();
            Notes = new List<string>();
            Problems = new List<string>();
        }
    }

    // The session state machine: no session -> applying -> active -> (restore) -> no session. The journal file is the
    // only state; every step re-reads HidHide, so a crash at any point is repaired by the next Disable/Recover.
    public sealed class HidHideSession
    {
        private readonly IHidHideCli cli;
        private readonly string journalPath;
        private readonly Func<int, long, bool> isOwnerAlive;

        public HidHideSession(IHidHideCli cli, string journalPath, Func<int, long, bool> isOwnerAlive)
        {
            this.cli = cli;
            this.journalPath = journalPath;
            this.isOwnerAlive = isOwnerAlive ?? ((pid, ticks) => false);
        }

        public string JournalPath
        {
            get { return journalPath; }
        }

        public HidHideJournal LoadJournal()
        {
            try
            {
                return File.Exists(journalPath) ? HidHideJournal.Parse(File.ReadAllText(journalPath, Encoding.UTF8)) : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // A session whose owner (the GUI that started it) no longer runs, or one that never got past "applying".
        public bool IsStale(HidHideJournal journal)
        {
            if (journal == null)
                return false;
            if (journal.OwnerPid == 0)
                return !string.Equals(journal.Phase, HidHideJournal.PhaseActive, StringComparison.OrdinalIgnoreCase) && journal.CreatedUtc < DateTime.UtcNow.AddMinutes(-2);
            return !isOwnerAlive(journal.OwnerPid, journal.OwnerStartTicks);
        }

        public HidHideResult Enable(HidHideRequest request, bool dryRun)
        {
            HidHideResult result = new HidHideResult { DryRun = dryRun };
            if (!cli.Installed)
                return Fail(result, "HidHide is not installed (https://github.com/nefarius/HidHide).");
            HidHideJournal running = LoadJournal();
            if (running != null && IsStale(running))
                return Fail(result, "A previous virtual joystick session did not end cleanly; restore it first (engine vjoy recover).");
            HidHideRead read = cli.Read();
            HidHideState current = read.State;
            if (!read.Complete)
            {
                // A part that cannot be read is never taken as "off" or "empty". A real run stops here; a dry run plans
                // with what it could read and says what is missing.
                string missing = "HidHide's " + string.Join(", ", current.UnknownParts()) + " could not be read (" + string.Join("; ", read.Problems) + ")";
                if (!dryRun)
                    return Fail(result, missing + ". Nothing was changed.");
                result.Notes.Add(missing + ": this dry run treats them as empty, so the commands below are incomplete. The real run reads everything with administrator rights first.");
                current = current.Clone();
                current.CloakUnknown = current.InverseUnknown = current.DevicesUnknown = current.AppsUnknown = false;
            }
            HidHidePlan plan = HidHidePlanner.PlanApply(current, request, running);
            result.Strategy = plan.Strategy == HidHideStrategy.AllowList ? "allow-list" : "block-list";
            result.Commands = plan.CommandLines();
            result.Notes.AddRange(plan.Notes);
            if (!plan.Ok)
                return Fail(result, plan.Error);
            if (dryRun)
            {
                result.Ok = true;
                result.Message = (plan.Arguments.Count == 0 ? "Nothing to change." : "Dry run: HidHide was not changed.") + (read.Complete ? string.Empty : " (partial: see notes)");
                result.SessionPhase = running == null ? "none" : running.Phase;
                return result;
            }

            HidHideState expected = HidHideState.Simulate(current, plan.Arguments);
            if (plan.Arguments.Count > 0)
            {
                // The journal goes to disk before HidHide changes, so a crash in between is still repaired.
                plan.Journal.Phase = running == null ? HidHideJournal.PhaseApplying : running.Phase;
                WriteJournal(plan.Journal);
                int exit = cli.Apply(plan.Arguments);
                if (exit == HidHideCli.Cancelled)
                {
                    result.Cancelled = true;
                    HidHideRead after = cli.Read();
                    if (running == null && after.Complete && HidHidePlanner.Verify(current, after.State).Count == 0)
                        DeleteJournal();
                    else if (running != null)
                        WriteJournal(running);
                    return Fail(result, "The Windows administrator prompt was declined; HidHide was not changed.");
                }
                result.Changed = true;
                HidHideRead actual = cli.Read();
                result.Problems = actual.Complete
                    ? HidHidePlanner.Verify(expected, actual.State)
                    : new List<string> { "HidHide could not be read back (" + string.Join("; ", actual.Problems) + "); whether every change took is unknown." };
                if (exit != 0)
                    result.Problems.Insert(0, exit == HidHideCli.TimedOut ? "HidHideCLI did not answer in time and was ended." : "HidHideCLI ended with exit code " + exit.ToString(CultureInfo.InvariantCulture) + ".");
            }
            plan.Journal.Phase = HidHideJournal.PhaseActive;
            if (plan.Journal.HasChanges || running != null)
                WriteJournal(plan.Journal);
            result.SessionPhase = plan.Journal.HasChanges || running != null ? HidHideJournal.PhaseActive : "none";
            result.Ok = result.Problems.Count == 0;
            result.Message = result.Ok
                ? (plan.Arguments.Count == 0 ? "HidHide already hides the devices as needed." : "HidHide hides the physical devices from the games with virtual mode.")
                : "HidHide did not take every change (see problems); run engine vjoy restore-hidhide to undo what was done.";
            return result;
        }

        public HidHideResult Disable(bool dryRun)
        {
            HidHideResult result = new HidHideResult { DryRun = dryRun };
            HidHideJournal journal = LoadJournal();
            if (journal == null)
            {
                result.Ok = true;
                result.Message = "No JoyPro HidHide session: nothing to restore.";
                result.SessionPhase = "none";
                return result;
            }
            if (!cli.Installed)
                return Fail(result, "HidHide is not installed any more; its configuration cannot be restored (the journal stays: " + journalPath + ").");
            HidHideRead read = cli.Read();
            if (!read.Complete)
            {
                // Restoring against a configuration that is only half known could take back the wrong entries.
                return Fail(result, "HidHide's " + string.Join(", ", read.State.UnknownParts()) + " could not be read (" + string.Join("; ", read.Problems) +
                                    "); nothing was changed and the session record stays (" + journalPath + ").");
            }
            HidHideState current = read.State;
            HidHidePlan plan = HidHidePlanner.PlanRestore(journal, current);
            result.Strategy = journal.Strategy == HidHideStrategy.AllowList ? "allow-list" : "block-list";
            result.Commands = plan.CommandLines();
            result.Notes.AddRange(plan.Notes);
            result.SessionPhase = journal.Phase;
            if (dryRun)
            {
                result.Ok = true;
                result.Message = plan.Arguments.Count == 0 ? "Nothing to restore in HidHide; the session record would be removed." : "Dry run: HidHide was not changed.";
                return result;
            }

            if (plan.Arguments.Count > 0)
            {
                HidHideState expected = HidHideState.Simulate(current, plan.Arguments);
                int exit = cli.Apply(plan.Arguments);
                if (exit == HidHideCli.Cancelled)
                {
                    result.Cancelled = true;
                    return Fail(result, "The Windows administrator prompt was declined; HidHide still has JoyPro's session. Restore it later (Virtual joystick page or engine vjoy restore-hidhide).");
                }
                result.Changed = true;
                HidHideRead actual = cli.Read();
                result.Problems = actual.Complete
                    ? HidHidePlanner.Verify(expected, actual.State)
                    : new List<string> { "HidHide could not be read back (" + string.Join("; ", actual.Problems) + ")." };
                if (exit != 0)
                    result.Problems.Insert(0, exit == HidHideCli.TimedOut ? "HidHideCLI did not answer in time and was ended." : "HidHideCLI ended with exit code " + exit.ToString(CultureInfo.InvariantCulture) + ".");
                if (result.Problems.Count > 0)
                {
                    result.Ok = false;
                    result.Message = "HidHide did not take every change back (see problems); the session record stays so it can be tried again.";
                    return result;
                }
            }
            DeleteJournal();
            result.Ok = true;
            result.SessionPhase = "none";
            result.Message = plan.Arguments.Count == 0 ? "HidHide already had its previous configuration; the session record was removed." : "HidHide has its previous configuration again.";
            return result;
        }

        // Restores a stale session (or any session with force). Nothing happens while the owner still runs.
        public HidHideResult Recover(bool dryRun, bool force)
        {
            HidHideJournal journal = LoadJournal();
            if (journal == null)
                return new HidHideResult { Ok = true, DryRun = dryRun, SessionPhase = "none", Message = "No JoyPro HidHide session: nothing to recover." };
            if (!force && !IsStale(journal))
                return new HidHideResult { Ok = true, DryRun = dryRun, SessionPhase = journal.Phase, Message = "The HidHide session is still in use by process " + journal.OwnerPid.ToString(CultureInfo.InvariantCulture) + "; nothing was changed (use --force to restore anyway)." };
            return Disable(dryRun);
        }

        private void WriteJournal(HidHideJournal journal)
        {
            string directory = Path.GetDirectoryName(journalPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            string temp = journalPath + ".tmp";
            File.WriteAllText(temp, journal.Serialize(), new UTF8Encoding(false));
            if (File.Exists(journalPath))
                File.Replace(temp, journalPath, null);
            else
                File.Move(temp, journalPath);
        }

        private void DeleteJournal()
        {
            if (!File.Exists(journalPath))
                return;
            // The last journals stay as a history next to it (what JoyPro changed and when).
            try
            {
                string history = Path.Combine(Path.GetDirectoryName(journalPath) ?? string.Empty, "history");
                Directory.CreateDirectory(history);
                File.Copy(journalPath, Path.Combine(history, "hidhide-session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt"), true);
                foreach (string old in Directory.GetFiles(history, "hidhide-session-*.txt").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).Skip(20))
                    File.Delete(old);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            File.Delete(journalPath);
        }

        private static HidHideResult Fail(HidHideResult result, string message)
        {
            result.Ok = false;
            result.Message = message;
            return result;
        }
    }

    // HidHide's state in a text file, changed like the CLI would: the engine's test mode (JOYPRO_VJOY_FAKE) and the GUI
    // tests. Apply never asks for elevation; ApplyExitCode simulates a declined UAC prompt (HidHideCli.Cancelled) or a
    // failing CLI.
    public sealed class FakeHidHideCli : IHidHideCli
    {
        private readonly string statePath;

        public FakeHidHideCli(string statePath)
        {
            this.statePath = statePath;
            Installed = true;
        }

        public bool Installed { get; set; }

        public string Location
        {
            get { return "fake:" + statePath; }
        }

        public int ApplyExitCode { get; set; }

        // Every argument list Apply received (tests check that one prompt carried all commands).
        public List<IList<string>> Calls { get; private set; } = new List<IList<string>>();

        // Parts (HidHideState.PartCloak ...) whose query is denied, as the hidden-device list is without
        // administrator rights.
        public HashSet<string> Denied { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Parts whose query hangs (the real CLI is ended after its time limit).
        public HashSet<string> Hanging { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The state as stored (what HidHide really holds).
        public HidHideState ReadState()
        {
            return File.Exists(statePath) ? HidHideState.Parse(File.ReadAllText(statePath, Encoding.UTF8)) : new HidHideState();
        }

        public HidHideRead Read()
        {
            HidHideState stored = ReadState();
            HidHideRead read = new HidHideRead { State = stored };
            foreach (string part in HidHideState.Parts)
            {
                if (Hanging.Contains(part))
                {
                    read.State.SetUnknown(part);
                    read.Problems.Add(part + ": HidHideCLI did not answer in time and was ended");
                }
                else if (Denied.Contains(part))
                {
                    read.State.SetUnknown(part);
                    read.Problems.Add(part + ": Error code 0x0005 Access is denied");
                }
            }
            return read;
        }

        public void Write(HidHideState state)
        {
            string directory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(statePath, state.Describe(), new UTF8Encoding(false));
        }

        public int Apply(IList<string> arguments)
        {
            Calls.Add(new List<string>(arguments));
            if (ApplyExitCode == HidHideCli.Cancelled || ApplyExitCode == HidHideCli.TimedOut)
                return ApplyExitCode;
            Write(HidHideState.Simulate(ReadState(), arguments));
            return ApplyExitCode;
        }
    }
}
