using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
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

namespace JoyPro.Gui.Tests;

// The footage of the trailer (tools/trailer): the real GUI with the real engine and a copy of a big profile, rendered
// offscreen at 4K (1600 x 900 at 2.4x) as stills and short frame sequences, with the screen rectangles of the texts the
// trailer's camera and cursor go to. Opt-in scenario "trailer" of the user journey:
//   set JOYPRO_JOURNEY_OUT=%JOYPRO_WORK_DIR%\trailer\journey
//   set JOYPRO_JOURNEY_SCENARIOS=trailer
//   set JOYPRO_JOURNEY_PROFILE=<a copy of a big profile>
//   set JOYPRO_JOURNEY_PICTURES=<a cockpit pictures root with DCS, MSFS2024 ... folders> (optional)
//   dotnet test gui/JoyPro.Gui.Tests --filter UserJourneyTests -p:AutoStopRunningJoyProGuiOnBuild=false
// Output: <out>\trailer\shots\<name>.png and shots.json (per shot: file, size, scale, anchors = text -> pixel rect).
// Texts with a path of this PC (the profile's folder, the user's name) are blanked in every shot.
public sealed partial class UserJourneyTests
{
    private sealed partial class Journey
    {
        private const int ShotWidth = 1600;
        private const int ShotHeight = 900;
        private const double ShotScale = 2.4;
        private readonly Dictionary<string, object> shots = [];
        // The trailer takes its own shots: no 2267/1280 render after every step.
        private bool renderSteps = true;
        private string ShotDir => Path.Combine(folder, "shots");

        private void RunTrailer()
        {
            string? source = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PROFILE");
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                Failures.Add("trailer: set JOYPRO_JOURNEY_PROFILE to a copy of a big profile");
                return;
            }
            Directory.CreateDirectory(ShotDir);
            renderSteps = Environment.GetEnvironmentVariable("JOYPRO_TRAILER_STEP_RENDERS") == "1";
            string profile = Path.Combine(folder, "profiles", "My HOTAS.pr0file");
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.Copy(source, profile, overwrite: true);
            try
            {
                vm = CreateVm();
                FakeFfbLauncher ffb = new();
                vm.FfbHostLauncher = ffb;
                vm.FfbFindGameProcess = ffb.Find;
                vm.FfbTelemetryDispatch = action => action();
                vm.FfbTelemetryPollInterval = TimeSpan.FromHours(1);
                vm.FfbTelemetrySaveDelay = TimeSpan.FromHours(1);
                Step("trailer: open the profile", async () =>
                {
                    vm!.LastProfilePath = profile;
                    await vm.RunStartupChecksAsync();
                    vm.SkipGameSetup();
                    await vm.AutoOpenLastProfileOnStartupAsync();
                    vm.CurrentStage = UiStage.Start;
                    // The notice that DCS got a new version since its command list was built: not the trailer's topic.
                    vm.HasDbUpdates = false;
                });
                RedirectGameConfigs();
                // JOYPRO_TRAILER_SCENES: only these scenes (method names without "Trailer", comma list), for retakes.
                HashSet<string>? only = Environment.GetEnvironmentVariable("JOYPRO_TRAILER_SCENES") is { Length: > 0 } list
                    ? new HashSet<string>(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
                    : null;
                foreach (Action scene in TrailerScenes())
                {
                    if (only != null && !only.Contains(scene.Method.Name.Replace("Trailer", string.Empty)))
                        continue;
                    try
                    {
                        scene();
                    }
                    catch (Exception ex)
                    {
                        Failures.Add("trailer scene " + scene.Method.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                        report.AppendLine("**SCENE FAILED** " + scene.Method.Name + ": " + ex).AppendLine();
                    }
                }
            }
            finally
            {
                File.WriteAllText(Path.Combine(ShotDir, "shots.json"), JsonSerializer.Serialize(shots, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private IEnumerable<Action> TrailerScenes() =>
        [
            TrailerStart,
            TrailerHook,
            TrailerMoreGames,
            TrailerExport,
            TrailerVisualAssign,
            TrailerTables,
            TrailerHud,
            TrailerCockpits,
            TrailerCurves,
            TrailerDetent,
            TrailerFfb,
            TrailerTelemetry,
            TrailerVirtualJoystick,
            TrailerWatcher,
            TrailerExtras,
            TrailerLanguages,
            TrailerBranding,
            TrailerAutoProfile,
        ];

        // ---- scenes ----

        private void TrailerStart()
        {
            Step("trailer: start page", () => { vm!.CurrentStage = UiStage.Start; return Task.CompletedTask; });
            Shot("start", "Start", "Your games");
        }

        // One relation, bound once, grows into every game JoyPro knows (Suggest missing, Add ticked).
        private void TrailerHook()
        {
            Step("trailer: Pitch", async () =>
            {
                vm!.AircraftCheckDismissed = true;
                vm.CurrentStage = UiStage.Relations;
                vm.SelectedRelation = vm.Relations.FirstOrDefault(r => r.Name == "Pitch") ?? vm.Relations.First();
                await WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBuilderSuggestLoading, 30000);
                Note("Pitch: " + vm.RelationNodes.Count + " actions; bind " + vm.SelectedRelation?.BindLabel);
            });
            Shot("hook-pitch", "Pitch", "Suggest missing");
            WriteData("hook-pitch-before", RelationGames());
            Step("trailer: Pitch, Suggest missing", async () =>
            {
                await vm!.SuggestMissingCommand.ExecuteAsync(null);
                Note("sections: " + string.Join(" | ", vm.BuilderSuggestionSections.Select(s => s.Header + " " + s.Items.Count)));
            });
            Shot("hook-suggest", "Games not in this relation yet");
            SuggestionSectionItem? games = vm!.BuilderSuggestionSections.FirstOrDefault();
            List<BuilderSuggestionItem> rows = games?.Items.Where(i => i.IsMissing && !i.HasConflict).ToList() ?? [];
            int frame = 0;
            foreach (BuilderSuggestionItem row in rows.Take(10))
            {
                row.IsChecked = true;
                Pump();
                Shot("hook-tick-" + (frame++).ToString("D2"), BuilderAddLabelAnchor());
            }
            foreach (BuilderSuggestionItem row in rows.Skip(10))
                row.IsChecked = true;
            Pump();
            Shot("hook-ticked", BuilderAddLabelAnchor());
            Step("trailer: Pitch, Add ticked", async () =>
            {
                await vm!.AddTickedBuilderSuggestionsCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBusy, 30000);
                Note("Pitch now: " + vm.RelationNodes.Count + " actions; status " + vm.StatusText);
            });
            Shot("hook-added", "Pitch");
            WriteData("hook-pitch-after", RelationGames());
        }

        private string BuilderAddLabelAnchor() => vm!.BuilderAddTickedLabel;

        // The games and aircraft of the open relation (the trailer's fan-out graphic).
        private object RelationGames()
        {
            RelationListItem? relation = vm!.SelectedRelation;
            Dictionary<string, SortedSet<string>> byGame = new(StringComparer.OrdinalIgnoreCase);
            foreach (RelationNodeItem node in vm.RelationNodes)
            {
                if (!byGame.TryGetValue(node.Game, out SortedSet<string>? planes))
                    byGame[node.Game] = planes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string plane in node.ActivePlanesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    planes.Add(plane);
            }
            return new
            {
                Relation = relation?.Name,
                Bind = relation?.BindLabel,
                Games = byGame.Select(kv => new { Game = kv.Key, Label = DisplayText.Game(kv.Key), Aircraft = kv.Value.ToArray() }).ToArray(),
            };
        }

        private void TrailerExport()
        {
            Step("trailer: export page", async () =>
            {
                vm!.CurrentStage = UiStage.Export;
                await vm.RefreshExportGamesCommand.ExecuteAsync(null);
                vm.ExportDcsPath = Path.Combine(gameCopies, "DCS");
                vm.ExportIl2Path = Path.Combine(gameCopies, "IL2Game");
                vm.RegistryGameDryRun = true;
                foreach (ExportGameItem game in vm.ExportGames)
                    game.IsSelected = game.Exporter != "starcitizen" && game.CanExport;
                // A first pass finds the games this PC cannot export to (never started: no config yet); they stay unticked.
                foreach (ExportGameItem game in vm.ExportGames.Where(g => g.IsSelected).ToList())
                {
                    await vm.ExportGameNowCommand.ExecuteAsync(game);
                    if (game.ExportFailed)
                        game.IsSelected = false;
                    Note("first pass " + game.Name + ": " + game.ExportState);
                }
                foreach (ExportGameItem game in vm.ExportGames)
                {
                    game.ExportState = string.Empty;
                    game.ExportFailed = false;
                }
                vm.StatusText = string.Empty;
                Note("tiles: " + string.Join(" | ", vm.ExportGames.Select(g => g.Name + " " + g.Summary + (g.IsSelected ? " (ticked)" : string.Empty))));
            });
            ShotTall("export-tiles", 1700, "Export to games", "Export ticked games");
            int frame = 0;
            foreach (ExportGameItem game in vm!.ExportGames.Where(g => g.IsSelected).ToList())
            {
                AvaloniaRenderThread.Wait(vm.ExportGameNowCommand.ExecuteAsync(game));
                AvaloniaRenderThread.Wait(SettleAsync(vm, 60000));
                Note("export " + game.Name + ": " + game.ExportState);
                ShotTall("export-" + (frame++).ToString("D2"), 1700, game.Name, "Export ticked games");
            }
        }

        // ---- shots ----

        private static void Pump()
        {
            for (int i = 0; i < 3; i++)
                AvaloniaRenderThread.Pump();
        }

        private void Shot(string name, params string[] anchors) => ShotOf(name, ShotWidth, ShotHeight, anchors);

        private void ShotTall(string name, int height, params string[] anchors) => ShotOf(name, ShotWidth, height, anchors);

        private void ShotOf(string name, int width, int height, string[] anchors, Action<Control>? prepare = null, double scale = ShotScale)
        {
            MainWindow main = new() { DataContext = vm, Width = width, Height = height };
            try
            {
                ShotControl(name, main, width, height, scale, anchors, transparent: false, prepare);
            }
            catch (Exception ex)
            {
                Failures.Add("shot " + name + ": " + ex.Message);
            }
            finally
            {
                main.DataContext = null;
                vm!.ConfirmHandler = Confirm;
            }
        }

        // Renders a window's content at the scale and records where the anchor texts are (in pixels of the PNG).
        private void ShotControl(string name, Window window, int width, int height, double scale, string[] anchors, bool transparent, Action<Control>? prepare = null)
        {
            window.Width = width;
            window.Height = height;
            Size size = new(width, height);
            Control page = (Control)window.Content!;
            foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<ListBox>())
            {
                list.AutoScrollToSelectedItem = false;
                if (list.ItemsPanel?.Build() is VirtualizingPanel)
                    list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
            }
            for (int pass = 0; pass < 6; pass++)
            {
                window.Measure(size);
                window.Arrange(new Rect(size));
                page.Measure(size);
                page.Arrange(new Rect(size));
                AvaloniaRenderThread.Pump();
                if (pass == 1 && prepare != null)
                    prepare(page);
            }
            BlankPrivateTexts(page);
            // A Border with a BoxShadow draws its content scaled twice in a bitmap of more than 96 dpi (the HUD cards and
            // the dialogs came out empty, TrailerRenderProbeTests): no shadows in these shots.
            if (scale > 1.01)
            {
                foreach (Border border in page.GetVisualDescendants().OfType<Border>())
                {
                    if (border.BoxShadow.Count > 0)
                        border.BoxShadow = default;
                }
            }
            PixelSize pixels = new((int)Math.Round(width * scale), (int)Math.Round(height * scale));
            Vector dpi = new(96 * scale, 96 * scale);
            using RenderTargetBitmap content = new(pixels, dpi);
            content.Render(page);
            using RenderTargetBitmap bitmap = new(pixels, dpi);
            using (DrawingContext context = bitmap.CreateDrawingContext())
            {
                if (!transparent)
                {
                    IBrush background = window.TryFindResource("JP.PageBackgroundBrush", window.ActualThemeVariant, out object? found) && found is IBrush own
                        ? own
                        : new SolidColorBrush(Color.Parse("#202020"));
                    context.FillRectangle(background, new Rect(size));
                }
                context.DrawImage(content, new Rect(0, 0, pixels.Width, pixels.Height), new Rect(size));
            }
            string file = Path.Combine(ShotDir, name + ".png");
            bitmap.Save(file);
            Dictionary<string, double[]> found2 = [];
            foreach (string anchor in anchors.Where(a => !string.IsNullOrEmpty(a)).Distinct())
            {
                Rect? rect = FindTextRect(page, anchor, size);
                if (rect is { } r)
                    found2[anchor] = [r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale];
                else
                    report.AppendLine("- shot " + name + ": anchor not found: " + anchor);
            }
            shots[name] = new { File = name + ".png", Width = pixels.Width, Height = pixels.Height, Scale = scale, Anchors = found2 };
            File.WriteAllText(Path.Combine(ShotDir, "shots.json"), JsonSerializer.Serialize(shots, new JsonSerializerOptions { WriteIndented = true }));
            report.AppendLine("- shot " + name + " (" + pixels.Width + "x" + pixels.Height + ", anchors " + found2.Count + "/" + anchors.Length + ")");
        }

        private void WriteData(string name, object data) =>
            File.WriteAllText(Path.Combine(ShotDir, name + ".json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

        // The first visible text equal to the anchor (else the first that contains it), in window coordinates.
        // "@TypeName" = the first visible control of that type; "Text#2" = the second text equal to it.
        private static Rect? FindTextRect(Control root, string anchor, Size size)
        {
            Rect Bounds(Visual v)
            {
                Point? origin = v.TranslatePoint(new Point(0, 0), root);
                return origin == null ? default : new Rect(origin.Value, v.Bounds.Size).Intersect(new Rect(size));
            }
            if (anchor.StartsWith('@'))
            {
                foreach (Control c in root.GetVisualDescendants().OfType<Control>())
                {
                    if (c.GetType().Name == anchor[1..] && c.IsEffectivelyVisible && Bounds(c) is { Width: >= 1, Height: >= 1 } r)
                        return r;
                }
                return null;
            }
            int nth = 1;
            int hash = anchor.LastIndexOf('#');
            if (hash > 0 && int.TryParse(anchor[(hash + 1)..], out int n))
            {
                nth = n;
                anchor = anchor[..hash];
            }
            Rect? contains = null;
            int seen = 0;
            foreach (TextBlock block in root.GetVisualDescendants().OfType<TextBlock>())
            {
                string text = block.Text ?? string.Empty;
                if (text.Length == 0 || !block.IsEffectivelyVisible || block.Bounds.Width < 1 || block.Opacity < 0.05)
                    continue;
                bool equal = string.Equals(text.Trim(), anchor, StringComparison.OrdinalIgnoreCase);
                if (!equal && (contains != null || text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                Rect rect = Bounds(block);
                if (rect.Width < 1 || rect.Height < 1)
                    continue;
                if (equal && ++seen == nth)
                    return rect;
                if (!equal)
                    contains = rect;
            }
            return contains;
        }

        // Paths of this PC (profile folder, data folder, the user's name) never show in the trailer.
        private static void BlankPrivateTexts(Control root)
        {
            string user = Environment.UserName;
            // Folders that must not show: the work folder, the checkout's folder and any extra marks
            // (JOYPRO_TRAILER_BLANK, ';'-separated, e.g. a synced folder's name).
            string[] marks = new[] { Environment.GetEnvironmentVariable("JOYPRO_WORK_DIR"), AppContext.BaseDirectory.Split(new[] { @"\gui\" }, StringSplitOptions.None)[0] }
                .Concat((Environment.GetEnvironmentVariable("JOYPRO_TRAILER_BLANK") ?? string.Empty).Split(';'))
                .Where(m => !string.IsNullOrWhiteSpace(m) && m!.Length > 3).Select(m => m!.Trim()).ToArray();
            bool Private(string text, string user) =>
                marks.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)) || text.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("/Users/", StringComparison.OrdinalIgnoreCase) || (user.Length > 2 && text.Contains(user, StringComparison.OrdinalIgnoreCase)) ||
                text.Contains('@') && text.Contains('.') && text.IndexOf('@') > 0 && !text.Contains(' ');
            foreach (TextBlock block in root.GetVisualDescendants().OfType<TextBlock>())
            {
                if (block.Text is { } text && Private(text, user))
                    block.Opacity = 0;
            }
            // The trailer shows JoyPro without its optional AI helpers (their buttons and notes are hidden).
            foreach (TextBlock block in root.GetVisualDescendants().OfType<TextBlock>())
            {
                string text = block.Text ?? string.Empty;
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"\bAI\b|assistant", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;
                Visual hide = block.GetVisualAncestors().OfType<Button>().FirstOrDefault() as Visual ?? block;
                hide.Opacity = 0;
            }
            foreach (TextBox box in root.GetVisualDescendants().OfType<TextBox>())
            {
                if (box.Text is { } text && Private(text, user))
                    box.Opacity = 0;
            }
        }
    }
}
