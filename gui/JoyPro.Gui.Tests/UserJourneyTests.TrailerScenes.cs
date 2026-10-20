using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;

namespace JoyPro.Gui.Tests;

// The trailer's scenes (see UserJourneyTests.Trailer.cs): each feature as the user does it, a shot before every click
// (with the clicked text as an anchor) and after it.
public sealed partial class UserJourneyTests
{
    private sealed partial class Journey
    {
        private string trailerDevice = string.Empty;
        private string trailerThrottle = string.Empty;

        private static string? Assets => Environment.GetEnvironmentVariable("JOYPRO_TRAILER_ASSETS") is { Length: > 0 } a && Directory.Exists(a) ? a : null;

        private void Act(string name, Func<Task> action) => Step("trailer: " + name, action);

        // ---- the whole profile in more games (Suggest missing for every relation, MSFS 2024 only) ----

        private void TrailerMoreGames()
        {
            Act("suggest missing for every relation", async () =>
            {
                vm!.CurrentStage = UiStage.Relations;
                await vm.SuggestMissingForAllCommand.ExecuteAsync(null);
                Note("review: " + vm.MissingReviewStatus + "; relations " + vm.MissingReview.Count);
            });
            ShotTall("more-review", 1300, "Add ticked");
            int ticked = 0;
            foreach (MissingReviewRelationItem relation in vm!.MissingReview)
            {
                foreach (BuilderSuggestionItem item in relation.Items)
                {
                    bool take = item.Game == "MSFS2024" && item.IsHigh && !item.HasConflict;
                    item.IsChecked = take;
                    ticked += take ? 1 : 0;
                }
            }
            Pump();
            Note("ticked for MSFS 2024: " + ticked);
            ShotTall("more-ticked", 1300, vm.MissingReviewAddLabel);
            Act("add the ticked MSFS 2024 actions", async () =>
            {
                await vm!.AddTickedMissingReviewCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBusy, 60000);
                Note("status: " + vm.StatusText);
            });
            Shot("more-added", "Relations");
        }

        // ---- Visual assign on the device picture, live input, modifiers ----

        private void TrailerVisualAssign()
        {
            Act("binds page", async () =>
            {
                vm!.CurrentStage = UiStage.Binds;
                await vm.RefreshBindsCommand.ExecuteAsync(null);
                // The picker's devices fill when the picture opens (as in the visual-assign journey).
                vm.ToggleVisualAssignModeCommand.Execute(null);
                await WaitForAsync(() => vm.VisualAssignDeviceOptions.Count > 0, 10000);
                if (vm.VisualAssignDeviceOptions.Count == 0)
                    await vm.RefreshQuickBindOptionsCommand.ExecuteAsync(null);
                // The devices the profile uses most first (a connected device without binds has empty tables).
                List<string> devices = vm.Binds.Where(b => b.HasBind && !string.IsNullOrEmpty(b.Device)).GroupBy(b => b.Device).OrderByDescending(g => g.Count()).Select(g => g.Key)
                    .Concat(vm.VisualAssignDeviceOptions).Distinct().ToList();
                string stickName = Environment.GetEnvironmentVariable("JOYPRO_TRAILER_STICK") is { Length: > 0 } sn ? sn : "Gunfighter";
                string throttleName = Environment.GetEnvironmentVariable("JOYPRO_TRAILER_THROTTLE") is { Length: > 0 } tn ? tn : "Throttle";
                trailerDevice = devices.FirstOrDefault(d => d.Contains(stickName, StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault() ?? string.Empty;
                trailerThrottle = devices.FirstOrDefault(d => d.Contains(throttleName, StringComparison.OrdinalIgnoreCase)) ?? trailerDevice;
                // The options of the picker are the connected devices' names: the same device as in the binds.
                trailerDevice = vm.VisualAssignDeviceOptions.FirstOrDefault(o => OverlayResolver.SameDevice(o, trailerDevice)) ?? trailerDevice;
                trailerThrottle = vm.VisualAssignDeviceOptions.FirstOrDefault(o => OverlayResolver.SameDevice(o, trailerThrottle)) ?? trailerThrottle;
                vm.OpenVisualAssignMode = false;
                Note("devices: " + string.Join(" | ", vm.VisualAssignDeviceOptions) + "; stick " + trailerDevice + "; throttle " + trailerThrottle);
            });
            Shot("va-binds", "Visual assign");
            BindListItem? target = vm!.Binds.FirstOrDefault(b => !b.HasBind && !b.IsAxisType && b.RelationName.Contains("Gear", StringComparison.OrdinalIgnoreCase))
                                   ?? vm.Binds.FirstOrDefault(b => !b.HasBind && !b.IsAxisType);
            Act("open visual assign", async () =>
            {
                vm!.SelectedBind = target;
                vm.OpenVisualAssignMode = true;
                vm.VisualAssignDevice = trailerDevice;
                await vm.LoadVisualAssignCommand.ExecuteAsync(null);
                await WaitForAsync(() => vm.ReferenceLegendItems.Count > 0, 20000);
                Note("picture " + vm.LayoutWorkspacePictureCaption + "; legend " + vm.ReferenceLegendItems.Count + "; free " + vm.ReferenceLegendItems.Count(r => r.IsFree));
            });
            Shot("va-open", target?.RelationName ?? "Bind here", "Bind on the picture");
            ReferenceLegendRow? free = vm.ReferenceLegendItems.FirstOrDefault(r => r.IsFree && r.Input.StartsWith("JOY_BTN", StringComparison.Ordinal) && !r.Input.Contains("POV"))
                                      ?? vm.ReferenceLegendItems.FirstOrDefault(r => r.IsFree);
            Act("click a free button on the picture", () =>
            {
                vm!.DeviceReference.SelectedLegendRow = free;
                Note("picked " + free?.Input + " " + free?.Control + "; preview " + vm.VisualAssignPreview);
                return Task.CompletedTask;
            });
            Shot("va-picked", "Bind here", free?.Control ?? "SELECTED INPUT");
            Act("Bind here", async () =>
            {
                await vm!.BindSelectedLayoutMarkerCommand.ExecuteAsync(null);
                Note("status " + vm.StatusText);
            });
            Shot("va-bound", target?.RelationName ?? "Bound");
            WriteData("va", new { Device = trailerDevice, Relation = target?.RelationName, Input = free?.Input, Control = free?.Control });

            // Live: the device's buttons and stick light up where they are.
            BindListItem? bound = vm.Binds.FirstOrDefault(b => b.HasBind && !b.IsAxisType && OverlayResolver.SameDevice(b.Device, trailerDevice));
            vm.WorkspaceLiveInput.FrozenAt = vm.WorkspaceLiveInput.Now;
            string[] presses = vm.ReferenceLegendItems.Where(r => r.IsBound && r.Input.StartsWith("JOY_BTN", StringComparison.Ordinal)).Select(r => r.Input).Distinct().Take(3).ToArray();
            int frame = 0;
            foreach (string input in presses)
            {
                vm.OnLiveInput(new JoystickInputEvent(trailerDevice, input, true));
                Pump();
                Shot("va-live-" + (frame++).ToString("D2"), vm.ReferenceLegendItems.FirstOrDefault(r => r.Input == input)?.Command ?? input);
                vm.OnLiveInput(new JoystickInputEvent(trailerDevice, input, false));
            }
            for (int i = 0; i < 12; i++)
            {
                double a = i / 12.0 * Math.PI * 2;
                vm.OnLiveAxes(new JoystickAxisSnapshot(trailerDevice, [0.5 + 0.38 * Math.Sin(a), 0.5 - 0.38 * Math.Cos(a), 0.5, 0, 0, 0, 0, 0]));
                Pump();
                Shot("va-axis-" + i.ToString("D2"));
            }
            vm.WorkspaceLiveInput.FrozenAt = null;
            WriteData("va-live", new { Presses = presses });
        }

        // ---- Layout: the device tables, printable cheat sheets ----

        private void TrailerTables()
        {
            foreach ((string device, string slug) in new[] { (trailerDevice, "stick"), (trailerThrottle, "throttle") }.Where(d => d.Item1.Length > 0).DistinctBy(d => d.Item1))
            {
                Act("layout: " + slug, async () =>
                {
                    vm!.OpenVisualAssignMode = false;
                    vm.CurrentStage = UiStage.Layout;
                    await vm.ShowWorkspaceDeviceAsync(device);
                    vm.LayoutCalloutsEnabled = true;
                    await WaitForAsync(() => vm.LayoutShowsCallouts, 15000);
                    if (vm.ShowReferenceQuestion)
                        vm.DeviceReference.DismissQuestionCommand.Execute(null);
                    vm.CalloutModifier = MainWindowViewModel.AllCombinationsChoice;
                    DescribeCallouts();
                });
                Shot("tables-" + slug, "Modifier columns show");
                string? file = DeviceReferenceService.CalloutFileOf(device);
                if (file == null)
                    continue;
                foreach ((string game, string plane) in new[] { ("DCS", "F-16C_50"), ("DCS", "FA-18C_hornet") })
                {
                    string png = Path.Combine(ShotDir, "cheatsheet-" + slug + "-" + plane + ".png");
                    EngineCommandResult result = AvaloniaRenderThread.Wait(cli.RunDirectAsync(engine,
                        ["layout", "callouts", "--layout", file, "--device", device, "--game", game, "--plane", plane, "--png", png, "--print-width", "3600", "--profile", vm!.ProfilePath]));
                    Note("cheat sheet " + slug + " " + plane + " -> " + result.ExitCode + " " + Short(result.StandardError, 200));
                }
            }
            Act("layout: cheat sheets section", () => { vm!.IsCheatSheetsSection = true; return Task.CompletedTask; });
            Shot("cheatsheets", "Create cheat sheets");
            Act("layout: device pictures again", () => { vm!.IsCheatSheetsSection = false; return Task.CompletedTask; });
        }

        // ---- HUD in game (over a screenshot of MSFS 2024) and the HUD page ----

        private void TrailerHud()
        {
            Act("hud page", async () =>
            {
                vm!.CurrentStage = UiStage.Hud;
                await vm.LoadHudDevicesCommand.ExecuteAsync(null);
                if (vm.HudPicturesTask is { } pictures)
                    await pictures;
            });
            Shot("hud-page", "Start HUD");
            ArrangeHud();
            // The game runs: JoyPro finds it by its process and reads its binds (the overlay map of MSFS 2024).
            typeof(MainWindowViewModel).GetField("overlayGame", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(vm, "MSFS2024");
            vm!.OverlayOnlyInGame = false;
            AvaloniaRenderThread.Wait(vm.ReloadOverlayMapCommand.ExecuteAsync(null));
            AvaloniaRenderThread.Wait(Task.Delay(800));
            Pump();
            Note("overlay: " + vm.OverlayStatus + "; header " + vm.HudContext.HeaderText);
            EngineCommandResult map = AvaloniaRenderThread.Wait(cli.RunDirectAsync(engine, ["--profile", vm.ProfilePath, "--status", "false", "overlay", "map", "--game", "MSFS2024"]));
            List<(string Device, string Input, string Relation)> entries = [];
            try
            {
                using JsonDocument doc = JsonDocument.Parse(map.StandardOutput[map.StandardOutput.IndexOf('{')..]);
                foreach (JsonElement e in doc.RootElement.GetProperty("Entries").EnumerateArray())
                {
                    string device = e.GetProperty("Device").GetString() ?? string.Empty;
                    string input = e.GetProperty("Input").GetString() ?? string.Empty;
                    bool mods = e.TryGetProperty("Modifiers", out JsonElement m) && m.GetArrayLength() > 0;
                    if (!mods && input.StartsWith("JOY_BTN", StringComparison.Ordinal) && !device.Contains("Keyboard", StringComparison.OrdinalIgnoreCase))
                        entries.Add((device, input, e.GetProperty("Relation").GetString() ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                Note("overlay map parse: " + ex.Message);
            }
            string[] wanted = ["Gear", "Flaps", "Brake", "Light", "Canopy", "Trigger"];
            List<(string Device, string Input, string Relation)> picks = wanted
                .Select(w => entries.FirstOrDefault(e => e.Relation.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Where(e => e.Input != null).DistinctBy(e => e.Relation).Take(4).ToList();
            Note("MSFS 2024 overlay entries: " + entries.Count + "; picks " + string.Join(" | ", picks.Select(p => p.Relation + " " + p.Input)));
            RenderHud("hud-game-00");
            int frame = 1;
            foreach ((string device, string input, string _) in picks)
            {
                HudDevice? hd = vm.HudWidgets.Select(w => w.Device).FirstOrDefault(d => d != null && OverlayResolver.SameDevice(d.Name, device));
                hd?.SetInput(input, true);
                vm.OnOverlayInput(new JoystickInputEvent(device, input, true));
                Pump();
                RenderHud("hud-game-" + (frame++).ToString("D2"));
                vm.OnOverlayInput(new JoystickInputEvent(device, input, false));
                hd?.SetInput(input, false);
            }
            WriteData("hud", new { Header = vm.HudContext.HeaderText, Lines = vm.OverlayLines.Select(l => l.Text + " | " + l.Detail).ToArray() });
            vm.OverlayLines.Clear();
            typeof(MainWindowViewModel).GetField("overlayGame", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(vm, string.Empty);
        }

        private void ArrangeHud()
        {
            Grid measureRoot = new() { Width = 1920, Height = 1080 };
            HudSurface measure = new() { Fixed = true, Widgets = vm!.HudWidgets, Context = vm.HudContext };
            measureRoot.Children.Add(measure);
            Window host = new() { Content = measureRoot };
            host.Measure(new Size(1920, 1080));
            host.Arrange(new Rect(0, 0, 1920, 1080));
            vm.ArrangeHudWidgets(measure.MeasuredWidgets());
            host.Content = null;
            vm.HudStyle = "Glass";
        }

        // The HUD over the game picture, as the transparent HUD window shows it over the game.
        private void RenderHud(string name)
        {
            Grid root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#0B1220")) };
            string? backdrop = Assets is { } a ? Path.Combine(a, "hud-backdrop.png") : null;
            if (backdrop != null && File.Exists(backdrop))
                root.Children.Add(new Image { Source = new Bitmap(backdrop), Stretch = Stretch.UniformToFill });
            root.Children.Add(new HudSurface { Fixed = true, Designer = false, Widgets = vm!.HudWidgets, Context = vm.HudContext });
            Window host = new() { Content = root, SizeToContent = SizeToContent.Manual };
            ShotControl(name, host, 1920, 1080, 2.0, vm.OverlayLines.Select(l => l.Text).Take(1).ToArray(), transparent: false);
            host.Content = null;
        }

        // ---- Cockpits ----

        private void TrailerCockpits()
        {
            string? pictures = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PICTURES");
            Act("cockpits: F-16", () =>
            {
                vm!.CurrentStage = UiStage.Cockpits;
                if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures))
                    vm.Cockpits.PicturesRoot = pictures;
                vm.Cockpits.RefreshPicturesStatus();
                vm.Cockpits.PictureModeIndex = CockpitsViewModel.ModePicture;
                vm.Cockpits.Show("f-16", "f-16c-block-50");
                Note("pictures: " + vm.Cockpits.GamePicturesText + "; pages " + vm.Cockpits.Pages.Count);
                return Task.CompletedTask;
            });
            ShotTall("cockpit-f16", 1800, "F-16");
            Act("cockpits: F-16 stick and throttle", () => { vm!.Cockpits.OpenPage("device:stick"); return Task.CompletedTask; });
            ShotTall("cockpit-f16-hotas", 2200, "TMS");
            Act("cockpits: a panel", () =>
            {
                CockpitPageItem? panel = vm!.Cockpits.PanelPages.FirstOrDefault(p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal));
                if (panel != null)
                    vm.Cockpits.SelectedPage = panel;
                Note("panel " + panel?.Title);
                return Task.CompletedTask;
            });
            ShotTall("cockpit-f16-panel", 1800);
            foreach ((string id, string variant, string slug) in new[] { ("ah-64", "ah-64d-pilot", "ah64"), ("fa-18", "fa-18c-lot-20", "fa18"), ("h125", "h125-as350b3e", "h125") })
            {
                Act("cockpits: " + slug, () =>
                {
                    vm!.Cockpits.PictureModeIndex = CockpitsViewModel.ModePicture;
                    vm.Cockpits.Show(id, variant);
                    Note(slug + ": " + vm.Cockpits.GamePicturesText);
                    return Task.CompletedTask;
                });
                ShotTall("cockpit-" + slug, 1800);
            }
        }

        // ---- Axis curves and detent ----

        private void TrailerCurves()
        {
            Act("binds: Pitch, axis tuning", async () =>
            {
                vm!.OpenVisualAssignMode = false;
                vm.CurrentStage = UiStage.Binds;
                vm.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == "Pitch");
                vm.IsAxisTuningExpanded = true;
                await WaitForAsync(() => vm.AxisTuningLoaded, 20000);
                AxisTuningScopeItem? scope = vm.AxisTuningScopes.FirstOrDefault(s => s.Scope == "plane:DCS:F-16C_50") ?? vm.AxisTuningScopes.FirstOrDefault(s => s.Scope == "game:DCS");
                if (scope != null)
                    vm.SelectedAxisTuningScope = scope;
                Note("scopes: " + string.Join(", ", vm.AxisTuningScopes.Select(s => s.Scope)) + "; editors " + string.Join(", ", vm.AxisTuningEditors.Select(e => e.Key)));
            });
            AxisParameterEditor? curvature = vm!.AxisTuningEditors.FirstOrDefault(e => e.Key == "curvature");
            AxisParameterEditor? deadzone = vm.AxisTuningEditors.FirstOrDefault(e => e.Key == "deadzone");
            for (int i = 0; i <= 10; i++)
            {
                double t = i / 10.0;
                if (curvature != null)
                    curvature.Value = Math.Round(0.45 * t, 2);
                if (deadzone != null)
                    deadzone.Value = Math.Round(0.04 * t, 3);
                double stick = 0.5 + 0.45 * Math.Sin(t * Math.PI);
                vm.OnAxisTuningAxes(new JoystickAxisSnapshot(vm.SelectedBind?.Device ?? string.Empty, [0.5, stick, 0.5, 0, 0, 0, 0, 0]));
                Pump();
                ShotTall("curve-" + i.ToString("D2"), 2400, "@AxisCurveView");
            }
        }

        private void TrailerDetent()
        {
            BindListItem? throttle = vm!.Binds.FirstOrDefault(b => b.RelationName == "ThrottleLeft") ?? vm.Binds.FirstOrDefault(b => b.IsAxisType && b.RelationName.Contains("Throttle", StringComparison.OrdinalIgnoreCase));
            if (throttle == null)
            {
                Note("no throttle relation for detent mode");
                return;
            }
            Act("detent mode on " + throttle.RelationName, async () =>
            {
                vm!.IsAxisTuningExpanded = false;
                vm.SelectedBind = throttle;
                vm.IsDetentExpanded = true;
                await WaitForAsync(() => vm.DetentLoaded, 20000);
                Note("detent: " + vm.DetentStatus + " | " + vm.DetentCalibrationText);
            });
            ShotTall("detent-open", 2400, "Detent");
            Act("detent wizard", () =>
            {
                vm!.StartDetentWizardCommand.Execute(null);
                FeedThrottle(0.015);
                vm.ConfirmDetentWizardStepCommand.Execute(null);
                FeedThrottle(0.79);
                return Task.CompletedTask;
            });
            ShotTall("detent-wizard", 2400, "Detent");
            Act("detent wizard done", () =>
            {
                vm!.IsDetentWizardOpen = false;
                vm.IsDetentExpanded = false;
                return Task.CompletedTask;
            });
        }

        // ---- Force feedback ----

        private void TrailerFfb()
        {
            Act("force feedback page", async () =>
            {
                await vm!.RefreshFfbTelemetryStatusCommand.ExecuteAsync(null);
                vm.CurrentStage = UiStage.FfbTelemetry;
                await Task.Delay(500);
                FfbTelemetryGameItem? dcs = vm.FfbTelemetryGames.FirstOrDefault(g => g.Id == "DCS");
                if (dcs != null)
                    vm.SelectedFfbTelemetryGame = dcs;
            });
            ShotTall("ffb-settings", 1800, "Force Feedback");
            Act("force feedback graph editor", () =>
            {
                vm!.FfbTelemetryTab = MainWindowViewModel.FfbTelemetryEditorTab;
                vm.FfbGraphEditor.SelectedNode = vm.FfbGraphEditor.Graph.Nodes.OfType<JoyPro.Ffb.Core.Graph.CurveNode>().FirstOrDefault();
                Dictionary<string, double> live = vm.FfbGraphEditor.Graph.Nodes.Select((n, i) => (n.Id, Value: 0.25 + 0.5 * ((i * 37) % 10) / 10.0)).ToDictionary(x => x.Id, x => x.Value);
                vm.FfbGraphEditor.SetLiveValues(live);
                return Task.CompletedTask;
            });
            ShotTall("ffb-editor", 1800);
        }

        // ---- Telemetry recording and Tacview ----

        private void TrailerTelemetry()
        {
            string recordings = Path.Combine(data, "Telemetry", "Recordings");
            Act("telemetry recording", async () =>
            {
                vm!.CurrentStage = UiStage.Settings;
                vm.CollapseAllSettingsCommand.Execute(null);
                vm.MoveSettingsSectionToTopCommand.Execute(vm.FindSettingsSection("telemetry-recording"));
                vm.FindSettingsSection("telemetry-recording")!.IsExpanded = true;
                TelemetryRecordingTests.WriteSession(recordings, "DCS", "F-16C_50", DateTime.UtcNow.AddHours(-26), seconds: 900);
                TelemetryRecordingTests.WriteSession(recordings, "IL2Game", "Bf 109 G-14", DateTime.UtcNow.AddHours(-5), seconds: 420, position: false);
                TelemetryRecordingTests.WriteSession(recordings, "MSFS2024", "F/A-18E", DateTime.UtcNow.AddMinutes(-40), seconds: 1800);
                vm.TelemetryRecordingEnabled = true;
                await vm.RefreshTelemetrySessionsAsync();
                vm.SelectedTelemetrySession = vm.TelemetrySessions.FirstOrDefault(s => s.Info.Game == "DCS");
                await vm.LoadTelemetryPreviewAsync(vm.SelectedTelemetrySession);
                Note("sessions " + vm.TelemetrySessionsSummary + "; preview " + vm.TelemetryPreviewText);
            });
            ShotTall("telemetry", 1500, "Tacview");
            Act("export for Tacview", async () =>
            {
                string? acmi = await vm!.ExportSelectedTelemetryAsync("acmi-zip");
                Note("acmi " + acmi + "; " + vm.TelemetryRecordingMessage);
            });
            ShotTall("telemetry-exported", 1500, "Tacview");
        }

        // ---- Virtual joystick ----

        private void TrailerVirtualJoystick()
        {
            Act("virtual joystick", async () =>
            {
                vm!.VirtualJoystickDispatch = action => action();
                vm.VirtualJoystickPollInterval = TimeSpan.FromHours(1);
                vm.VirtualJoystickFindGame = _ => null;
                vm.VirtualJoystickProcessPath = _ => null;
                vm.VirtualJoystickDataFolder = Path.Combine(data, "VirtualJoystick");
                await vm.RefreshVirtualJoystickCommand.ExecuteAsync(null);
                vm.CurrentStage = UiStage.VirtualJoystick;
                Note("vjoy: " + vm.VirtualFunctions.Count + " functions; games " + vm.VirtualJoystickGames.Count + "; types " + string.Join(", ", vm.VirtualFunctionTypes.Select(t => t.Id)));
            });
            Shot("vjoy-overview", "Virtual Joystick");
        }

        // ---- More: every version of the profile, one-click components, DCS's own default binds ----

        private void TrailerExtras()
        {
            Act("history panel", () =>
            {
                vm!.CurrentStage = UiStage.Start;
                vm.IsHistoryPanelOpen = true;
                return Task.CompletedTask;
            });
            Shot("history", "History");
            Act("components", () =>
            {
                vm!.IsHistoryPanelOpen = false;
                vm.CurrentStage = UiStage.Settings;
                vm.CollapseAllSettingsCommand.Execute(null);
                vm.MoveSettingsSectionToTopCommand.Execute(vm.FindSettingsSection("components"));
                vm.FindSettingsSection("components")!.IsExpanded = true;
                return Task.CompletedTask;
            });
            ShotTall("components", 1500, "Components");
            Act("dcs default binds", async () =>
            {
                vm!.CollapseAllSettingsCommand.Execute(null);
                vm.MoveSettingsSectionToTopCommand.Execute(vm.FindSettingsSection("dcs-defaults"));
                vm.FindSettingsSection("dcs-defaults")!.IsExpanded = true;
                await vm.RefreshDcsDefaultsStatusCommand.ExecuteAsync(null);
                await vm.PreviewDcsStripDefaultsCommand.ExecuteAsync(null);
                Note("DCS defaults: " + vm.DcsDefaultsStatus + " | " + vm.DcsDefaultsPreviewTitle);
            });
            ShotTall("dcs-defaults", 1500, "DCS");
        }

        // ---- The game changed its controls (after the game closed) ----

        private void TrailerWatcher()
        {
            Act("a game changed its controls", () =>
            {
                vm!.CurrentStage = UiStage.Start;
                vm.GameSyncChanges.Clear();
                vm.GameSyncChanges.Add(new GameSyncChangeItem("c1", "added", "F-16C_50: 'Countermeasures Dispense' now on VKB Gunfighter BTN 21", ["CMS FWD (Start Program or just Flare)"], "CMS FWD (Start Program or just Flare)", "Countermeasures Dispense"));
                vm.GameSyncChanges.Add(new GameSyncChangeItem("c2", "changed-tuning", "F-16C_50: Pitch dead zone 0 -> 3 %, curvature 0 -> 15", ["Pitch"], "Pitch", "Pitch"));
                vm.GameSyncChanges.Add(new GameSyncChangeItem("c3", "removed", "FA-18C_hornet: 'Gear TOGGLE' no longer on WINWING TAKEOFF PANEL BTN 1", ["GearIn"], "GearIn", "Gear"));
                vm.GameSyncTitle = "DCS World: 3 changes made in the game";
                vm.HasGameSyncChanges = true;
                return Task.CompletedTask;
            });
            Shot("watcher", "Take over", "DCS World: 3 changes made in the game");
            vm!.HasGameSyncChanges = false;
            vm.GameSyncChanges.Clear();
        }

        // ---- 39 languages ----

        private void TrailerLanguages()
        {
            Act("languages: relations page", async () =>
            {
                vm!.CurrentStage = UiStage.Relations;
                vm.SelectedRelation = vm.Relations.FirstOrDefault(r => r.Name == "Pitch") ?? vm.SelectedRelation;
                await WaitForAsync(() => !vm.IsBuilderSuggestLoading, 20000);
            });
            List<string> codes = [];
            try
            {
                foreach (JoyPro.Gui.Localization.LanguageInfo language in JoyPro.Gui.Localization.Languages.All)
                {
                    vm!.ApplyLanguageChoice(language.Code);
                    Pump();
                    // Under a big title in a fast montage: half the pixels are enough.
                    ShotOf("lang-" + language.Code, ShotWidth, ShotHeight, [], scale: 1.2);
                    codes.Add(language.Code + "|" + language.NativeName);
                }
            }
            finally
            {
                vm!.ApplyLanguageChoice("en");
                Pump();
            }
            WriteData("languages", codes);
        }

        // ---- The marks (end card) ----

        private void TrailerBranding()
        {
            foreach ((string key, int w, int h) in new[] { ("Brand.EngineIcon", 1024, 1024), ("Brand.Wordmark", 2400, 600), ("Brand.AppIcon", 1024, 1024), ("Brand.Logo", 1024, 1024) })
            {
                if (Application.Current!.TryFindResource(key, out object? found) && found is IImage image)
                {
                    Size s = image.Size;
                    double scale = Math.Min(w / s.Width, h / s.Height);
                    int pw = (int)Math.Round(s.Width * scale), ph = (int)Math.Round(s.Height * scale);
                    using RenderTargetBitmap bitmap = new(new PixelSize(pw, ph));
                    using (DrawingContext context = bitmap.CreateDrawingContext())
                        context.DrawImage(image, new Rect(s), new Rect(0, 0, pw, ph));
                    bitmap.Save(Path.Combine(ShotDir, "brand-" + key[6..].ToLowerInvariant() + ".png"));
                }
                else
                {
                    Note("no resource " + key);
                }
            }
        }

        // ---- From an empty profile to a bound one: Auto Group, Auto Assign; device exchange; setup wizard ----

        private void TrailerAutoProfile()
        {
            Act("new empty profile", async () =>
            {
                await vm!.NewProfileAutoCommand.ExecuteAsync(null);
                vm.CurrentStage = UiStage.Relations;
            });
            Shot("auto-empty", "Auto-build relations from all my actions...");
            Act("auto group: open", () => { vm!.StartAutoGroupCommand.Execute(null); return Task.CompletedTask; });
            Shot("auto-group-open", "Preview");
            Act("auto group: preview", async () =>
            {
                await vm!.PreviewAutoGroupCommand.ExecuteAsync(null);
                Note("preview: " + Short(vm.AutoGroupPreview, 300));
            });
            Shot("auto-group-preview", "Apply");
            Act("auto group: apply", async () =>
            {
                await vm!.ApplyAutoGroupCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBusy, 120000);
                vm.CloseRelationToolsCommand.Execute(null);
                Note("relations: " + vm.Relations.Count + " / " + vm.ProfileRelationCount);
            });
            Shot("auto-group-applied", "Relations");
            Act("auto assign: open", async () =>
            {
                await vm!.ToggleAutoAssignPanelCommand.ExecuteAsync(null);
                await WaitForAsync(() => vm.AutoAssignDevices.Count > 0 && !vm.IsBusy, 30000);
                Note("auto assign devices: " + string.Join(" | ", vm.AutoAssignDevices.Select(d => d.Device + " " + d.ModelId)));
            });
            ShotTall("auto-assign-open", 1500, "Preview");
            Act("auto assign: preview", async () =>
            {
                await vm!.PreviewAutoAssignCommand.ExecuteAsync(null);
                Note("preview: " + Short(vm.AutoAssignPreview, 300));
            });
            ShotTall("auto-assign-preview", 1500, "Apply");
            Act("auto assign: apply", async () =>
            {
                await vm!.ApplyAutoAssignCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBusy, 120000);
                vm.IsAutoAssignPanelOpen = false;
                Note("bound: " + vm.ProfileBoundText);
            });
            Shot("auto-assign-applied", "Bind Studio");
            Act("layout of the auto-assigned throttle", async () =>
            {
                vm!.CurrentStage = UiStage.Layout;
                await vm.ShowWorkspaceDeviceAsync(trailerThrottle.Length > 0 ? trailerThrottle : trailerDevice);
                vm.LayoutCalloutsEnabled = true;
                await WaitForAsync(() => vm.LayoutShowsCallouts, 15000);
                if (vm.ShowReferenceQuestion)
                    vm.DeviceReference.DismissQuestionCommand.Execute(null);
            });
            Shot("auto-layout");

            // A new stick: every bind moves over (preview only).
            Act("exchange a device", async () =>
            {
                vm!.CurrentStage = UiStage.Settings;
                await vm.PrepareReplaceDeviceAsync(null, null);
                string? old = vm.ReplaceOldChoices.FirstOrDefault(d => d.Contains("Gunfighter", StringComparison.OrdinalIgnoreCase)) ?? vm.ReplaceOldChoices.FirstOrDefault();
                string? target = vm.ReplaceNewChoices.FirstOrDefault(d => d.Contains("WarBRD", StringComparison.OrdinalIgnoreCase) || d.Contains("Rhino", StringComparison.OrdinalIgnoreCase));
                if (old != null && target != null)
                    await vm.PrepareReplaceDeviceAsync(old, target);
                Note("replace: " + vm.ReplaceSummary + " | " + vm.ReplaceCounts);
            });
            ShotDialog("replace", new ReplaceDeviceWindow(), 1600, 900);

            // First start: the games found on this PC; the setup wizard.
            Act("game setup", async () =>
            {
                await vm!.RefreshGameInstallsCommand.ExecuteAsync(null);
                vm.BuildGameSetup(firstStart: true);
            });
            ShotDialog("game-setup", new GameSetupWindow(), 1100, 900);
            Act("setup wizard", () => { vm!.OpenSetupWizardCommand.Execute(null); return Task.CompletedTask; });
            if (vm!.SetupWizard != null)
            {
                SetupWizardWindow wizard = new() { DataContext = vm.SetupWizard };
                ShotControl("setup-wizard", wizard, 1400, 900, ShotScale, [], transparent: false);
            }
        }

        private void ShotDialog(string name, Window window, int width, int height)
        {
            window.DataContext = vm;
            try
            {
                ShotControl(name, window, width, height, ShotScale, [], transparent: false);
            }
            catch (Exception ex)
            {
                Failures.Add("dialog " + name + ": " + ex.Message);
            }
            window.DataContext = null;
        }
    }
}
