using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // engine hud devices [--grip <device>=<catalog id>]...: everything the GUI's HUD draws for each connected device:
    //   - what DirectInput reports (axes, buttons, hats),
    //   - the catalog model (official product picture, kind, hand, what each axis and button is),
    //   - the layout picture with the button positions: the layout the profile assigned to the device, else the best
    //     real template (catalog / the user's layout folders). Templates are never generated here.
    internal static class EngineHud
    {
        private const int MinTemplateScore = 55;

        public static EngineHudResult Devices(IDictionary<string, string> grips, List<string> warnings)
        {
            EngineHudResult result = new EngineHudResult();
            GameDeviceDirectory directory = GameDeviceDirectory.Discover(warnings);
            List<RosterDevice> roster = EngineDeviceKb.BuildRoster(directory, grips, null, null, warnings);
            List<GameDevice> connected = directory.Devices.Where(d => d.Connected && !string.IsNullOrWhiteSpace(d.JoyProName)).ToList();

            Dictionary<string, LayoutDeviceSuggestion> templates = new Dictionary<string, LayoutDeviceSuggestion>(StringComparer.OrdinalIgnoreCase);
            try
            {
                templates = EngineLayoutSuggestion.Templates(connected.Select(d => d.JoyProName), directory, MinTemplateScore, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("Layout templates: " + ex.Message);
            }

            foreach (GameDevice device in connected)
            {
                directory.EnsureCapabilities(device, warnings);
                EngineHudDevice hud = new EngineHudDevice
                {
                    Device = device.JoyProName,
                    Name = !string.IsNullOrWhiteSpace(device.HidProductString) ? device.HidProductString.Trim() : device.InstanceName.Trim(),
                    VidPid = device.HasVidPid ? GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId) : string.Empty,
                    Buttons = device.ButtonCount,
                    Hats = device.PovCount
                };

                RosterDevice entry = roster.FirstOrDefault(r => string.Equals(r.JoyProName, device.JoyProName, StringComparison.OrdinalIgnoreCase));
                KbDevice model = entry?.Model;
                KbDevice baseModel = entry?.BaseModel;
                if (model != null)
                {
                    // A generic model is named after the device string (with its GUID): the plain name reads better.
                    hud.Model = entry.Generic ? hud.Name : model.Name;
                    hud.ModelId = model.Id;
                    hud.Kind = model.Kind;
                    hud.Hand = model.Hand;
                    hud.Generic = entry.Generic;
                    hud.Image = model.Image.Length > 0 ? model.Image : baseModel?.Image ?? string.Empty;
                }
                else
                {
                    hud.Kind = GuessKind(hud.Name);
                }
                if (entry != null)
                {
                    foreach (KbDevice grip in entry.GripChoices)
                        hud.GripChoices.Add(new EngineHudChoice { Id = grip.Id, Name = grip.Name, Image = grip.Image });
                    // A base that takes several grips is a stick until the grip is picked.
                    if (model == null && entry.GripChoices.Count > 0)
                        hud.Kind = entry.GripChoices[0].Kind.Length > 0 ? entry.GripChoices[0].Kind : "stick";
                }

                // Axes as DirectInput reports them, with their role from the catalog (grip first, then the base).
                List<KbControl> axisInfo = new List<KbControl>();
                if (model != null)
                    axisInfo.AddRange(model.Axes);
                if (baseModel != null)
                    axisInfo.AddRange(baseModel.Axes);
                foreach (int slot in (device.AxisSlots ?? new List<int>()).Distinct().OrderBy(s => s))
                {
                    string token = GameInputTokens.AxisToken(slot);
                    if (token.Length == 0)
                        continue;
                    KbControl info = axisInfo.FirstOrDefault(a => string.Equals(a.Input, token, StringComparison.OrdinalIgnoreCase));
                    hud.Axes.Add(new EngineHudAxis
                    {
                        Input = token,
                        Control = info?.Control ?? string.Empty,
                        Label = info != null && info.Label.Length > 0 ? info.Label : AxisLabel(info?.Control, token)
                    });
                }

                List<KbControl> controls = new List<KbControl>();
                if (model != null)
                    controls.AddRange(model.Controls);
                if (baseModel != null)
                    controls.AddRange(baseModel.Controls);
                foreach (KbControl control in controls.Where(c => c.Label.Length > 0))
                {
                    if (!hud.Labels.ContainsKey(control.Input))
                        hud.Labels[control.Input] = control.Label;
                }

                hud.Layout = Layout(device.JoyProName, templates, warnings);
                result.Devices.Add(hud);
            }
            result.Warnings = warnings;
            return result;
        }

        // The profile's own layout for the device (assigned in Visual Assign / JoyPro 1) wins over the catalog template.
        private static EngineHudLayout Layout(string device, Dictionary<string, LayoutDeviceSuggestion> templates, List<string> warnings)
        {
            string path = null;
            string name = string.Empty;
            bool own = false;
            string assigned;
            if (InternalDataManagement.JoystickFileImages != null &&
                InternalDataManagement.JoystickFileImages.TryGetValue(device, out assigned) &&
                !string.IsNullOrWhiteSpace(assigned) && File.Exists(assigned))
            {
                path = assigned;
                name = Path.GetFileNameWithoutExtension(assigned);
                own = true;
            }
            else
            {
                LayoutDeviceSuggestion template;
                if (templates.TryGetValue(device, out template) && !string.IsNullOrWhiteSpace(template.LayoutPath) && File.Exists(template.LayoutPath))
                {
                    path = template.LayoutPath;
                    name = template.TemplateName;
                    own = IsOwnLayoutSource(template.Source);
                }
            }
            if (path == null)
                return null;

            LayoutTemplateInspectResult inspect;
            try
            {
                inspect = EngineLayoutTemplateEditor.Inspect(path, 5000);
            }
            catch (Exception ex)
            {
                warnings.Add("Layout '" + path + "': " + ex.Message);
                return null;
            }
            if (inspect == null || inspect.Errors.Count > 0 || inspect.Width <= 0 || inspect.Height <= 0 || string.IsNullOrWhiteSpace(inspect.PreviewImagePath))
                return null;

            EngineHudLayout layout = new EngineHudLayout
            {
                Template = name,
                Source = path,
                Own = own,
                Picture = inspect.PreviewImagePath,
                Width = inspect.Width,
                Height = inspect.Height
            };
            // The physical buttons: markers without a modifier (one per layer otherwise). The point is where JoyPro 1 put
            // the button's label, next to or on the button.
            foreach (LayoutTemplateMarkerDto marker in inspect.Markers.Where(m => string.IsNullOrWhiteSpace(m.Modifier)))
            {
                if (!marker.Token.StartsWith("JOY_BTN", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (layout.Markers.Any(m => string.Equals(m.Input, marker.Token, StringComparison.OrdinalIgnoreCase)))
                    continue;
                layout.Markers.Add(new EngineHudMarker
                {
                    Input = marker.Token.ToUpperInvariant(),
                    X = Math.Round(marker.X / inspect.Width, 4),
                    Y = Math.Round(marker.Y / inspect.Height, 4)
                });
            }
            return layout;
        }

        // Same rule as the GUI's LayoutSuggestionItem.IsOwnLayout: shipped templates, JoyPro's drawing and placeholders
        // are not the user's own layout.
        internal static bool IsOwnLayoutSource(string source)
        {
            switch ((source ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "catalog":
                case "reference-schematic":
                case "fallback":
                case "fallback-placeholder":
                case "fallback-local-picture":
                    return false;
                default:
                    return true;
            }
        }

        private static string AxisLabel(string control, string token)
        {
            if (!string.IsNullOrWhiteSpace(control))
            {
                string role = control.Substring(control.IndexOf(".axis.", StringComparison.Ordinal) >= 0 ? control.IndexOf(".axis.", StringComparison.Ordinal) + 6 : 0);
                role = role.Replace("ministick.", "mini ").Replace('_', ' ').Replace('.', ' ').Trim();
                if (role == "main")
                    role = control.StartsWith("throttle", StringComparison.Ordinal) ? "throttle" : control.StartsWith("collective", StringComparison.Ordinal) ? "collective" : role;
                if (role.Length > 0)
                    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(role);
            }
            return token.StartsWith("JOY_", StringComparison.Ordinal) ? token.Substring(4).Replace("SLIDER", "Slider ") : token;
        }

        private static string GuessKind(string name)
        {
            string lower = (name ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("pedal") || lower.Contains("rudder"))
                return "pedals";
            if (lower.Contains("throttle") || lower.Contains("twcs") || lower.Contains("mt-50"))
                return "throttle";
            if (lower.Contains("panel") || lower.Contains("box") || lower.Contains("mfd") || lower.Contains("icp"))
                return "panel";
            if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("xbox"))
                return "controller";
            if (lower.Contains("stick") || lower.Contains("joystick") || lower.Contains("grip"))
                return "stick";
            return "other";
        }

        public static string ToText(EngineHudResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("HUD Devices: " + result.Devices.Count.ToString(CultureInfo.InvariantCulture));
            foreach (EngineHudDevice device in result.Devices)
            {
                sb.AppendLine("  - " + device.Device);
                sb.AppendLine("    Model: " + (device.Model.Length > 0 ? device.Model + " [" + device.ModelId + "]" : "<not in the catalog>") + ", kind " + device.Kind +
                              (device.Hand.Length > 0 && device.Hand != "none" ? ", " + device.Hand + " hand" : string.Empty));
                sb.AppendLine("    Inputs: " + device.Buttons.ToString(CultureInfo.InvariantCulture) + " buttons, " + device.Hats.ToString(CultureInfo.InvariantCulture) + " hats, axes " +
                              (device.Axes.Count == 0 ? "<none>" : string.Join(", ", device.Axes.Select(a => a.Input + (a.Control.Length > 0 ? "=" + a.Label : string.Empty)))));
                if (device.Image.Length > 0)
                    sb.AppendLine("    Picture: " + device.Image);
                if (device.Layout != null)
                    sb.AppendLine("    Layout: " + device.Layout.Template + " (" + device.Layout.Markers.Count.ToString(CultureInfo.InvariantCulture) + " button positions)");
                if (device.GripChoices.Count > 0)
                    sb.AppendLine("    Grips: " + string.Join(", ", device.GripChoices.Select(g => g.Id)));
            }
            foreach (string warning in result.Warnings)
                sb.AppendLine("  warning: " + warning);
            return sb.ToString().TrimEnd();
        }
    }

    public sealed class EngineHudResult
    {
        public List<EngineHudDevice> Devices { get; set; }
        public List<string> Warnings { get; set; }

        public EngineHudResult()
        {
            Devices = new List<EngineHudDevice>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineHudDevice
    {
        public string Device { get; set; }
        public string Name { get; set; }
        public string VidPid { get; set; }
        public string Model { get; set; }
        public string ModelId { get; set; }
        // stick, throttle, pedals, base, panel, collective, yoke, hotas, quadrant, mfd, controller, other.
        public string Kind { get; set; }
        public string Hand { get; set; }
        public bool Generic { get; set; }
        public string Image { get; set; }
        public int Buttons { get; set; }
        public int Hats { get; set; }
        public List<EngineHudAxis> Axes { get; set; }
        public Dictionary<string, string> Labels { get; set; }
        public List<EngineHudChoice> GripChoices { get; set; }
        public EngineHudLayout Layout { get; set; }

        public EngineHudDevice()
        {
            Device = Name = VidPid = Model = ModelId = Image = Hand = string.Empty;
            Kind = "other";
            Axes = new List<EngineHudAxis>();
            Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            GripChoices = new List<EngineHudChoice>();
        }
    }

    public sealed class EngineHudAxis
    {
        public string Input { get; set; }
        // Catalog role, e.g. stick.axis.roll, throttle.axis.left, pedals.axis.rudder (empty when unknown).
        public string Control { get; set; }
        public string Label { get; set; }

        public EngineHudAxis()
        {
            Input = Control = Label = string.Empty;
        }
    }

    public sealed class EngineHudChoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Image { get; set; }

        public EngineHudChoice()
        {
            Id = Name = Image = string.Empty;
        }
    }

    public sealed class EngineHudLayout
    {
        public string Template { get; set; }
        public string Source { get; set; }
        // The user's own layout (assigned in the profile, or a layout of his layout folders), not a shipped template:
        // the Layout page shows it instead of the device reference, and so does the Start page's device card.
        public bool Own { get; set; }
        // PNG of the layout picture; Width / Height in pixels; marker X / Y are 0..1 of the picture.
        public string Picture { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<EngineHudMarker> Markers { get; set; }

        public EngineHudLayout()
        {
            Template = Source = Picture = string.Empty;
            Markers = new List<EngineHudMarker>();
        }
    }

    public sealed class EngineHudMarker
    {
        public string Input { get; set; }
        public double X { get; set; }
        public double Y { get; set; }

        public EngineHudMarker()
        {
            Input = string.Empty;
        }
    }
}
