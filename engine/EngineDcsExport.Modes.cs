using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JoyPro.Engine
{
    // The export modes as binds that are active in the game (docs/games/dcs.md "Export modes", 2.5.0):
    // - a device file's active binds are DCS's defaults for the device (clean profile) minus the file's "removed", plus
    //   its "added" and "changed" (what DCS's Scripts\Input\Data.lua applies);
    // - each mode decides the binds that shall be active;
    // - the .diff.lua then holds only real differences from the defaults: an added bind that is no default, a removed
    //   default, and a default whose axis tuning differs ("changed", as DCS writes it). A bind equal to a default is
    //   not written (no default is removed and added again); the .jp lists it with JoyPro's names for a re-import.
    internal static partial class EngineDcsExport
    {
        private sealed class ActiveAction
        {
            public string Name = string.Empty;
            public List<DiffAdded> Binds = new List<DiffAdded>();

            public int IndexOf(string signature)
            {
                for (int i = 0; i < Binds.Count; i++)
                    if (InputSignature(Binds[i]) == signature)
                        return i;
                return -1;
            }

            public bool Has(string signature)
            {
                return IndexOf(signature) >= 0;
            }

            // The bind takes the place of a bind on the same input (its tuning and names), else it is added.
            public void Upsert(DiffAdded bind)
            {
                int index = IndexOf(InputSignature(bind));
                if (index >= 0)
                {
                    DiffAdded old = Binds[index];
                    DiffAdded copy = CloneAdded(bind);
                    if (string.IsNullOrWhiteSpace(copy.RelationName))
                        copy.RelationName = old.RelationName;
                    if (copy.Groups == null || copy.Groups.Count == 0)
                        copy.Groups = old.Groups == null ? new List<string>() : new List<string>(old.Groups);
                    Binds[index] = copy;
                }
                else
                {
                    Binds.Add(CloneAdded(bind));
                }
            }

            public bool Remove(string signature)
            {
                return Binds.RemoveAll(b => InputSignature(b) == signature) > 0;
            }
        }

        // The written file: the .diff.lua (real differences only) and the .jp (every bind JoyPro wants active, with names).
        private sealed class FileWrite
        {
            public DiffPayload Diff = new DiffPayload();
            public DiffPayload Backup = new DiffPayload();
        }

        private static bool IsEmptyDiff(DiffPayload payload)
        {
            return payload == null || (!payload.HasFfb && payload.Axis.Count == 0 && payload.Keys.Count == 0);
        }

        private static string InputSignature(DiffAdded bind)
        {
            return RemovedSignature(bind == null ? string.Empty : bind.Key, bind == null ? null : bind.Reformers);
        }

        // DCS's defaults of one section of a device's clean profile, as binds.
        private static Dictionary<string, ActiveAction> DefaultsOf(Dictionary<string, DiffEntry> clean)
        {
            Dictionary<string, ActiveAction> defaults = new Dictionary<string, ActiveAction>(StringComparer.OrdinalIgnoreCase);
            if (clean == null)
                return defaults;
            foreach (KeyValuePair<string, DiffEntry> entry in clean)
            {
                if (entry.Value == null || entry.Value.Removed.Count == 0)
                    continue;
                ActiveAction action = new ActiveAction { Name = entry.Value.Name ?? string.Empty };
                foreach (DiffRemoved removed in entry.Value.Removed)
                {
                    DiffAdded bind = new DiffAdded { Key = removed.Key.Trim(), Reformers = NormalizeList(removed.Reformers) };
                    if (!action.Has(InputSignature(bind)))
                        action.Binds.Add(bind);
                }
                defaults[entry.Key] = action;
            }
            return defaults;
        }

        private static Dictionary<string, ActiveAction> CloneActive(Dictionary<string, ActiveAction> source)
        {
            Dictionary<string, ActiveAction> clone = new Dictionary<string, ActiveAction>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ActiveAction> item in source)
                clone[item.Key] = new ActiveAction { Name = item.Value.Name, Binds = item.Value.Binds.Select(CloneAdded).ToList() };
            return clone;
        }

        private static ActiveAction GetOrAdd(Dictionary<string, ActiveAction> section, string id, string name)
        {
            ActiveAction action;
            if (!section.TryGetValue(id, out action))
            {
                action = new ActiveAction { Name = name ?? string.Empty };
                section[id] = action;
            }
            else if (string.IsNullOrWhiteSpace(action.Name) && !string.IsNullOrWhiteSpace(name))
            {
                action.Name = name;
            }
            return action;
        }

        // The binds a device file has active: defaults minus "removed", plus "added" and "changed".
        private static Dictionary<string, ActiveAction> ActiveOf(Dictionary<string, DiffEntry> file, Dictionary<string, ActiveAction> defaults)
        {
            Dictionary<string, ActiveAction> active = CloneActive(defaults);
            if (file == null)
                return active;
            foreach (KeyValuePair<string, DiffEntry> entry in file)
            {
                if (entry.Value == null)
                    continue;
                ActiveAction action = GetOrAdd(active, entry.Key, entry.Value.Name);
                foreach (DiffRemoved removed in entry.Value.Removed)
                    action.Remove(RemovedSignature(removed.Key, removed.Reformers));
                foreach (DiffAdded added in entry.Value.Added)
                    action.Upsert(added);
                foreach (DiffAdded changed in entry.Value.Changed)
                    action.Upsert(changed);
            }
            return active;
        }

        private static Dictionary<string, ActiveAction> ActiveOfProfile(Dictionary<string, DiffEntry> generated)
        {
            Dictionary<string, ActiveAction> active = new Dictionary<string, ActiveAction>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, DiffEntry> entry in generated ?? new Dictionary<string, DiffEntry>())
            {
                if (entry.Value == null || entry.Value.Added.Count == 0)
                    continue;
                ActiveAction action = GetOrAdd(active, entry.Key, entry.Value.Name);
                foreach (DiffAdded added in entry.Value.Added)
                    action.Upsert(added);
            }
            return active;
        }

        // An axis filter that changes nothing (no invert, no slider, no deadzone, flat curve, full saturation).
        private static bool IsNeutralFilter(DiffAdded bind)
        {
            if (!HasAxisFilter(bind))
                return true;
            if (bind.HasInverted && bind.Inverted)
                return false;
            if (bind.HasSlider && bind.Slider)
                return false;
            if (bind.HasDeadzone && !NearlyEqual(bind.Deadzone, 0))
                return false;
            if (bind.Curvature != null && bind.Curvature.Any(c => !NearlyEqual(c, 0)))
                return false;
            if (bind.HasSaturationX && !NearlyEqual(bind.SaturationX, 1))
                return false;
            if (bind.HasSaturationY && !NearlyEqual(bind.SaturationY, 1))
                return false;
            return true;
        }

        // The .diff.lua and .jp sections that make 'desired' the active binds over DCS's 'defaults'.
        private static void ToDiff(
            Dictionary<string, ActiveAction> desired,
            Dictionary<string, ActiveAction> defaults,
            bool isAxis,
            Dictionary<string, DiffEntry> diff,
            Dictionary<string, DiffEntry> backup,
            EngineDcsExportResult result,
            Dictionary<string, ActiveAction> profile,
            Dictionary<string, DiffEntry> existing)
        {
            HashSet<string> ids = new HashSet<string>(desired.Keys, StringComparer.OrdinalIgnoreCase);
            if (existing != null)
                ids.UnionWith(existing.Where(e => e.Value != null && e.Value.Removed.Count > 0).Select(e => e.Key));
            ids.UnionWith(defaults.Keys);
            foreach (string id in ids.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            {
                ActiveAction want;
                ActiveAction def;
                desired.TryGetValue(id, out want);
                defaults.TryGetValue(id, out def);
                DiffEntry old = null;
                if (existing != null)
                    existing.TryGetValue(id, out old);
                string name = want != null && !string.IsNullOrWhiteSpace(want.Name) ? want.Name
                    : def != null && !string.IsNullOrWhiteSpace(def.Name) ? def.Name
                    : old != null ? old.Name ?? string.Empty : string.Empty;
                DiffEntry d = new DiffEntry { Name = name };
                DiffEntry j = new DiffEntry { Name = name };
                HashSet<string> wanted = new HashSet<string>(StringComparer.Ordinal);
                foreach (DiffAdded bind in want == null ? new List<DiffAdded>() : want.Binds)
                {
                    string signature = InputSignature(bind);
                    if (!wanted.Add(signature))
                        continue;
                    ActiveAction mine = null;
                    if (profile != null)
                        profile.TryGetValue(id, out mine);
                    // The .jp lists what JoyPro writes (with names) and the file's own binds, not the defaults the
                    // game has by itself (an import reads a .jp bind that is a default as the file's bind).
                    if (def == null || !def.Has(signature) || (mine != null && mine.Has(signature)))
                        j.Added.Add(CloneAdded(bind));
                    if (def != null && def.Has(signature))
                    {
                        if (isAxis && !IsNeutralFilter(bind))
                        {
                            d.Changed.Add(CloneAdded(bind));
                            result.DefaultsWithChangedTuning++;
                        }
                        else
                        {
                            result.DefaultsKeptUnwritten++;
                        }
                        continue;
                    }
                    d.Added.Add(CloneAdded(bind));
                }
                foreach (DiffAdded bind in def == null ? new List<DiffAdded>() : def.Binds)
                {
                    if (wanted.Contains(InputSignature(bind)))
                        continue;
                    DiffRemoved removed = new DiffRemoved { Key = bind.Key, Reformers = NormalizeList(bind.Reformers) };
                    AddRemoved(d, removed);
                    AddRemoved(j, removed);
                }
                // A removal of the file that DCS's defaults as JoyPro knows them do not have (an older clean profile, a
                // default of another device type) stays, unless the bind shall be active.
                if (old != null)
                {
                    foreach (DiffRemoved removed in old.Removed)
                    {
                        string signature = RemovedSignature(removed.Key, removed.Reformers);
                        if (wanted.Contains(signature) || (def != null && def.Has(signature)))
                            continue;
                        AddRemoved(d, removed);
                        AddRemoved(j, removed);
                    }
                }
                RebuildSignatures(d);
                RebuildSignatures(j);
                if (d.Added.Count > 0 || d.Removed.Count > 0 || d.Changed.Count > 0)
                    diff[id] = d;
                if (j.Added.Count > 0 || j.Removed.Count > 0)
                    backup[id] = j;
            }
        }

        private static FileWrite ToFileWrite(Dictionary<string, ActiveAction> axis, Dictionary<string, ActiveAction> keys, DiffPayload clean, EngineDcsExportResult result,
            DiffPayload generated, DiffPayload existing)
        {
            FileWrite write = new FileWrite();
            ToDiff(axis, DefaultsOf(clean == null ? null : clean.Axis), true, write.Diff.Axis, write.Backup.Axis, result,
                generated == null ? null : ActiveOfProfile(generated.Axis), existing == null ? null : existing.Axis);
            ToDiff(keys, DefaultsOf(clean == null ? null : clean.Keys), false, write.Diff.Keys, write.Backup.Keys, result,
                generated == null ? null : ActiveOfProfile(generated.Keys), existing == null ? null : existing.Keys);
            return write;
        }

        // The binds one written device file shall have in the game, by mode, and the files that give them.
        private static FileWrite BuildFileWrite(
            EngineDcsExportMode mode,
            DiffPayload existing,
            DiffPayload generated,
            DiffPayload clean,
            bool keyboard,
            bool keepOtherKeyboardDefaults,
            string plane,
            string device,
            HashSet<string> profileActionsOfPlane,
            EngineDcsExportResult result)
        {
            existing = existing ?? new DiffPayload();
            generated = generated ?? new DiffPayload();
            Dictionary<string, ActiveAction> axis = DesiredSection(mode, existing.Axis, generated.Axis, clean == null ? null : clean.Axis, true,
                keyboard, keepOtherKeyboardDefaults, plane, device, profileActionsOfPlane, result);
            Dictionary<string, ActiveAction> keys = DesiredSection(mode, existing.Keys, generated.Keys, clean == null ? null : clean.Keys, false,
                keyboard, keepOtherKeyboardDefaults, plane, device, profileActionsOfPlane, result);
            FileWrite write = ToFileWrite(axis, keys, clean, result, generated, existing);

            // Force feedback: clean takes the profile's, the merges keep the file's on a conflict (profile wins: the
            // profile's); the device's other settings (trimmer, shake, ...) always stay.
            DiffPayload ffb = new DiffPayload();
            if (mode == EngineDcsExportMode.Clean)
            {
                ffb.HasFfb = generated.HasFfb;
                ffb.Ffb = CloneForceFeedback(generated.Ffb);
            }
            else
            {
                MergeForceFeedbackSettings(ffb, existing, generated, keepExistingOnConflict: mode != EngineDcsExportMode.MergeOverwrite);
            }
            KeepOtherForceFeedbackSettings(ffb, existing, mode == EngineDcsExportMode.Merge || mode == EngineDcsExportMode.Add);
            write.Diff.HasFfb = write.Backup.HasFfb = ffb.HasFfb;
            write.Diff.Ffb = CloneForceFeedback(ffb.Ffb);
            write.Backup.Ffb = CloneForceFeedback(ffb.Ffb);
            return write;
        }

        private static Dictionary<string, ActiveAction> DesiredSection(
            EngineDcsExportMode mode,
            Dictionary<string, DiffEntry> existingSection,
            Dictionary<string, DiffEntry> generatedSection,
            Dictionary<string, DiffEntry> cleanSection,
            bool isAxis,
            bool keyboard,
            bool keepOtherKeyboardDefaults,
            string plane,
            string device,
            HashSet<string> profileActionsOfPlane,
            EngineDcsExportResult result)
        {
            Dictionary<string, ActiveAction> defaults = DefaultsOf(cleanSection);
            Dictionary<string, ActiveAction> profile = ActiveOfProfile(generatedSection);
            // input -> the actions the profile puts on it in this file.
            Dictionary<string, HashSet<string>> profileOnInput = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ActiveAction> action in profile)
            {
                foreach (DiffAdded bind in action.Value.Binds)
                {
                    HashSet<string> actions;
                    string signature = InputSignature(bind);
                    if (!profileOnInput.TryGetValue(signature, out actions))
                        profileOnInput[signature] = actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    actions.Add(action.Key);
                }
            }

            Dictionary<string, ActiveAction> desired;
            if (mode == EngineDcsExportMode.Clean)
            {
                desired = CloneActive(profile);
                // The keyboard is never taken over: its other default keys stay unless the profile puts another action
                // on their key (then that default use goes).
                if (keyboard && keepOtherKeyboardDefaults)
                {
                    foreach (KeyValuePair<string, ActiveAction> def in defaults)
                    {
                        foreach (DiffAdded bind in def.Value.Binds)
                        {
                            string signature = InputSignature(bind);
                            HashSet<string> users;
                            if (profileOnInput.TryGetValue(signature, out users) && !users.Contains(def.Key))
                            {
                                result.KeyboardDefaultsRemoved++;
                                continue;
                            }
                            ActiveAction target = GetOrAdd(desired, def.Key, def.Value.Name);
                            if (!target.Has(signature))
                                target.Binds.Add(CloneAdded(bind));
                        }
                    }
                }
                return desired;
            }

            desired = ActiveOf(existingSection, defaults);
            foreach (KeyValuePair<string, ActiveAction> action in profile.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (DiffAdded bind in action.Value.Binds)
                {
                    string signature = InputSignature(bind);
                    HashSet<string> profileUsers = profileOnInput[signature];
                    List<string> others = desired
                        .Where(a => !string.Equals(a.Key, action.Key, StringComparison.OrdinalIgnoreCase) && !profileUsers.Contains(a.Key) && a.Value.Has(signature))
                        .Select(a => a.Key)
                        .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    ActiveAction target = GetOrAdd(desired, action.Key, action.Value.Name);
                    if (mode == EngineDcsExportMode.Add)
                    {
                        // Only add never deletes: a bind already there stays as the game has it; an input the game uses
                        // for another function is not written and the profile marks the bind.
                        if (target.Has(signature))
                            continue;
                        if (others.Count > 0)
                        {
                            result.MergeConflictsKept++;
                            result.SkippedTakenInputs.Add(new ExportSkippedBind
                            {
                                Game = "DCS",
                                Aircraft = plane,
                                Device = keyboard ? "Keyboard" : device,
                                Input = bind.Key,
                                Modifiers = NormalizeList(bind.Reformers),
                                Relation = bind.RelationName ?? string.Empty,
                                ActionId = action.Key,
                                Action = action.Value.Name,
                                GameActionId = others[0],
                                GameAction = desired[others[0]].Name
                            });
                            continue;
                        }
                        target.Binds.Add(CloneAdded(bind));
                        result.MergeConflictsAdded++;
                        continue;
                    }
                    // Both merges: a game bind of another function on that input goes (two functions on one button).
                    foreach (string other in others)
                    {
                        if (!desired[other].Remove(signature))
                            continue;
                        result.ConflictingGameBindsRemoved++;
                        ActiveAction otherDefault;
                        if (keyboard && defaults.TryGetValue(other, out otherDefault) && otherDefault.Has(signature))
                            result.KeyboardDefaultsRemoved++;
                    }
                    if (target.Has(signature))
                        result.MergeConflictsOverwritten++;
                    target.Upsert(bind);
                }
            }

            if (mode == EngineDcsExportMode.MergeOverwrite)
                result.OtherButtonsOfProfileActionsRemoved += RemoveOtherButtons(desired, profile, profileActionsOfPlane, isAxis, keyboard ? defaults : null);

            // --clear-keyboard-defaults (command line): every other default key of the written aircraft goes too.
            if (keyboard && !keepOtherKeyboardDefaults)
            {
                foreach (KeyValuePair<string, ActiveAction> def in defaults)
                {
                    ActiveAction target;
                    if (!desired.TryGetValue(def.Key, out target))
                        continue;
                    ActiveAction mine;
                    profile.TryGetValue(def.Key, out mine);
                    foreach (DiffAdded bind in def.Value.Binds)
                    {
                        string signature = InputSignature(bind);
                        if (mine == null || !mine.Has(signature))
                            target.Remove(signature);
                    }
                }
            }
            return desired;
        }

        // Profile wins: every action the profile binds in the aircraft keeps only the profile's buttons in this file.
        // keepDefaults: the keyboard's DCS default keys stay (the keyboard is never taken over); only its own binds go.
        private static int RemoveOtherButtons(Dictionary<string, ActiveAction> desired, Dictionary<string, ActiveAction> profileHere, HashSet<string> profileActionsOfPlane, bool isAxis,
            Dictionary<string, ActiveAction> keepDefaults = null)
        {
            int removed = 0;
            if (profileActionsOfPlane == null)
                return 0;
            string prefix = isAxis ? "a|" : "k|";
            foreach (KeyValuePair<string, ActiveAction> action in desired)
            {
                if (!profileActionsOfPlane.Contains(prefix + action.Key))
                    continue;
                ActiveAction mine;
                profileHere.TryGetValue(action.Key, out mine);
                ActiveAction kept = null;
                if (keepDefaults != null)
                    keepDefaults.TryGetValue(action.Key, out kept);
                removed += action.Value.Binds.RemoveAll(b => (mine == null || !mine.Has(InputSignature(b))) && (kept == null || !kept.Has(InputSignature(b))));
            }
            return removed;
        }

        // "a|<id>" / "k|<id>" of every action the profile binds in each plane (any device).
        private static Dictionary<string, HashSet<string>> ProfileActionsByPlane(Dictionary<string, Dictionary<string, DiffPayload>> generatedPayloads, bool includeKeyboard)
        {
            Dictionary<string, HashSet<string>> byPlane = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, DiffPayload>> plane in generatedPayloads)
            {
                HashSet<string> actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, DiffPayload> device in plane.Value)
                {
                    if (!includeKeyboard && string.Equals(device.Key, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (KeyValuePair<string, DiffEntry> entry in device.Value.Axis)
                        if (entry.Value != null && entry.Value.Added.Count > 0)
                            actions.Add("a|" + entry.Key);
                    foreach (KeyValuePair<string, DiffEntry> entry in device.Value.Keys)
                        if (entry.Value != null && entry.Value.Added.Count > 0)
                            actions.Add("k|" + entry.Key);
                }
                byPlane[plane.Key] = actions;
            }
            return byPlane;
        }

        // A device the export never clears or takes binds from (the import's ignored devices, the vJoy tunnel).
        private static bool IsKeptDevice(string device, EngineDcsExportOptions options)
        {
            if (options == null || options.KeepDevices == null || options.KeepDevices.Count == 0 || string.IsNullOrWhiteSpace(device))
                return false;
            string name = DcsDeviceNames.JoyProName(device.Trim());
            string bare = StripDeviceGuidSuffix(name);
            foreach (string keep in options.KeepDevices)
            {
                string k = DcsDeviceNames.JoyProName(keep.Trim());
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) || (!k.Contains("{") && string.Equals(k, bare, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        // Profile wins: the profile's actions also lose their buttons on the plane's other devices that have a file (a device
        // without one keeps DCS's defaults; nuke is the tool for those). Not the keyboard of an aircraft without keyboard
        // binds in the profile (no keyboard file is written there), not the kept devices.
        private static void RemoveOtherButtonsElsewhere(
            string inputPath,
            Dictionary<string, Dictionary<string, DiffPayload>> generatedPayloads,
            Dictionary<string, HashSet<string>> profileActions,
            EngineDcsExportOptions options,
            Dictionary<string, DiffPayload> cleanProfiles,
            EngineDcsExportResult result)
        {
            foreach (KeyValuePair<string, Dictionary<string, DiffPayload>> plane in generatedPayloads.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                HashSet<string> actions;
                if (!profileActions.TryGetValue(plane.Key, out actions) || actions.Count == 0)
                    continue;
                string joystickDir = Path.Combine(inputPath, plane.Key, "joystick");
                HashSet<string> written = new HashSet<string>(plane.Value.Keys.Select(d => DcsDeviceNames.JoyProName(d.Trim())), StringComparer.OrdinalIgnoreCase);
                HashSet<string> devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(joystickDir))
                {
                    foreach (string file in Directory.GetFiles(joystickDir, "*.diff.lua"))
                    {
                        string name = Path.GetFileName(file);
                        devices.Add(DcsDeviceNames.JoyProName(name.Substring(0, name.Length - ".diff.lua".Length)));
                    }
                }
                List<string> targets = devices.Where(d => !written.Contains(d) && !IsKeptDevice(d, options)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

                foreach (string device in targets)
                {
                    bool keyboard = string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase);
                    string planeDir = Path.Combine(inputPath, plane.Key, keyboard ? "keyboard" : "joystick");
                    DeviceFiles files = ResolveDeviceFiles(planeDir, device, keyboard);
                    DiffPayload clean = LoadCleanProfile(plane.Key, keyboard, files.DcsDevice, cleanProfiles, result);
                    DiffPayload existing = LoadExistingPayload(files.ExistingDiff, files.ExistingBackup, result, clean);
                    Dictionary<string, ActiveAction> axis = ActiveOf(existing.Axis, DefaultsOf(clean == null ? null : clean.Axis));
                    Dictionary<string, ActiveAction> keys = ActiveOf(existing.Keys, DefaultsOf(clean == null ? null : clean.Keys));
                    Dictionary<string, ActiveAction> none = new Dictionary<string, ActiveAction>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, ActiveAction> keyboardDefaults = keyboard ? DefaultsOf(clean == null ? null : clean.Keys) : null;
                    int removed = RemoveOtherButtons(axis, none, actions, true, keyboard ? DefaultsOf(clean == null ? null : clean.Axis) : null) +
                                  RemoveOtherButtons(keys, none, actions, false, keyboardDefaults);
                    if (removed == 0)
                        continue;
                    FileWrite write = ToFileWrite(axis, keys, clean, result, null, existing);
                    write.Diff.HasFfb = write.Backup.HasFfb = existing.HasFfb;
                    write.Diff.Ffb = CloneForceFeedback(existing.Ffb);
                    write.Backup.Ffb = CloneForceFeedback(existing.Ffb);
                    WritePayloadFile(planeDir, files.Diff, write.Diff, false, result);
                    WritePayloadFile(planeDir, files.Backup, write.Backup, true, result);
                    RemoveLegacyFiles(files, result);
                    result.OtherButtonsOfProfileActionsRemoved += removed;
                }
            }
        }
    }
}
