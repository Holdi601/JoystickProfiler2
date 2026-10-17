using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace JoyPro.Engine
{
    public sealed class DcsWindowRect
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        public int Width
        {
            get { return Right - Left; }
        }

        public int Height
        {
            get { return Bottom - Top; }
        }

        public DcsWindowRect Clone()
        {
            return new DcsWindowRect
            {
                Left = Left,
                Top = Top,
                Right = Right,
                Bottom = Bottom
            };
        }
    }

    public sealed class DcsRefreshOptions
    {
        public string SavedGamesPath { get; set; }
        public string InstallPath { get; set; }
        public string ClicksXmlPath { get; set; }
        public string ClicksImageFolder { get; set; }
        public string DcsExecutablePath { get; set; }
        public string DefaultStickReference { get; set; }
        public bool UseOcr { get; set; }
        public bool Confirm { get; set; }
        public bool DryRun { get; set; }
        public bool PreviewClicks { get; set; }
        public string PreviewImagePath { get; set; }
        public bool LaunchDcs { get; set; }
        public bool KeepDcsRunning { get; set; }
        public int MenuWaitSeconds { get; set; }
        public int OcrMenuTimeoutSeconds { get; set; }
        public int SettleSeconds { get; set; }
        public int ModulesToScan { get; set; }
        public bool UseHeadlessLuaRefresh { get; set; }
        public List<string> PlaneFilter { get; set; }
        public double? GuiScaleOverride { get; set; }
        public DcsWindowRect WindowOverride { get; set; }
        public Action<string> StatusSink { get; set; }

        public DcsRefreshOptions()
        {
            SavedGamesPath = string.Empty;
            InstallPath = string.Empty;
            ClicksXmlPath = string.Empty;
            ClicksImageFolder = string.Empty;
            DcsExecutablePath = string.Empty;
            DefaultStickReference = string.Empty;
            UseOcr = false;
            Confirm = true;
            DryRun = false;
            PreviewClicks = false;
            PreviewImagePath = string.Empty;
            LaunchDcs = true;
            KeepDcsRunning = false;
            MenuWaitSeconds = 45;
            OcrMenuTimeoutSeconds = 600;
            SettleSeconds = 60;
            ModulesToScan = 0;
            UseHeadlessLuaRefresh = false;
            PlaneFilter = new List<string>();
            GuiScaleOverride = null;
            WindowOverride = null;
            StatusSink = null;
        }
    }

    public sealed class DcsResolvedClick
    {
        public string Name { get; set; }
        public string Method { get; set; }
        public string Anchor { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public int TimeoutMs { get; set; }
        public bool Found { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string Template { get; set; }
        public int MatchLeft { get; set; }
        public int MatchTop { get; set; }
        public int MatchWidth { get; set; }
        public int MatchHeight { get; set; }

        public DcsResolvedClick()
        {
            Name = string.Empty;
            Method = string.Empty;
            Anchor = string.Empty;
            OffsetX = 0;
            OffsetY = 0;
            TimeoutMs = 0;
            Found = false;
            X = 0;
            Y = 0;
            Template = string.Empty;
            MatchLeft = 0;
            MatchTop = 0;
            MatchWidth = 0;
            MatchHeight = 0;
        }

        public DcsResolvedClick Clone()
        {
            return new DcsResolvedClick
            {
                Name = Name,
                Method = Method,
                Anchor = Anchor,
                OffsetX = OffsetX,
                OffsetY = OffsetY,
                TimeoutMs = TimeoutMs,
                Found = Found,
                X = X,
                Y = Y,
                Template = Template,
                MatchLeft = MatchLeft,
                MatchTop = MatchTop,
                MatchWidth = MatchWidth,
                MatchHeight = MatchHeight
            };
        }
    }

    public sealed class DcsClickPreviewResult
    {
        public string Action { get; set; }
        public string SavedGamesPath { get; set; }
        public string InstallPath { get; set; }
        public string ClicksXmlPath { get; set; }
        public string ClicksImageFolder { get; set; }
        public string PreviewImagePath { get; set; }
        public bool UseOcr { get; set; }
        public bool UsedWindowOverride { get; set; }
        public bool UsedRunningDcsProcess { get; set; }
        public double GuiScale { get; set; }
        public DcsWindowRect Window { get; set; }
        public List<DcsResolvedClick> Clicks { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public DcsClickPreviewResult()
        {
            Action = "preview-clicks";
            SavedGamesPath = string.Empty;
            InstallPath = string.Empty;
            ClicksXmlPath = string.Empty;
            ClicksImageFolder = string.Empty;
            PreviewImagePath = string.Empty;
            UseOcr = false;
            UsedWindowOverride = false;
            UsedRunningDcsProcess = false;
            GuiScale = 1.0;
            Window = null;
            Clicks = new List<DcsResolvedClick>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    public sealed class DcsRefreshResult
    {
        public string Action { get; set; }
        public string SavedGamesPath { get; set; }
        public string InstallPath { get; set; }
        public string DcsExecutablePath { get; set; }
        public string ClicksXmlPath { get; set; }
        public string ClicksImageFolder { get; set; }
        public string InputPath { get; set; }
        public string InputBackupPath { get; set; }
        public string InputLayoutsPath { get; set; }
        public string PreviewImagePath { get; set; }
        public string DefaultStickReference { get; set; }
        public bool UseOcr { get; set; }
        public bool Confirm { get; set; }
        public bool DryRun { get; set; }
        public bool PreviewClicks { get; set; }
        public bool LaunchDcs { get; set; }
        public bool KeepDcsRunning { get; set; }
        public bool UsedWindowOverride { get; set; }
        public bool UsedRunningDcsProcess { get; set; }
        public bool StartedDcs { get; set; }
        public int MenuWaitSeconds { get; set; }
        public int OcrMenuTimeoutSeconds { get; set; }
        public int SettleSeconds { get; set; }
        public int ModulesToScanRequested { get; set; }
        public int ModulesToScanResolved { get; set; }
        public bool UsedHeadlessLuaRefresh { get; set; }
        public List<string> RequestedPlanes { get; set; }
        public List<string> MatchedPlanes { get; set; }
        public double GuiScale { get; set; }
        public DcsWindowRect Window { get; set; }
        public int ClicksPlanned { get; set; }
        public int ClicksExecuted { get; set; }
        public int PlanesScanned { get; set; }
        public int HtmlFilesCopied { get; set; }
        public int CleanFilesWritten { get; set; }
        public int KeyboardCleanFilesWritten { get; set; }
        public int DeviceCleanFilesWritten { get; set; }
        // Device types (DCS template names) the clean refresh cleared with a fake stick of their own.
        public List<string> CoveredDeviceTypes { get; set; } = new List<string>();
        public int InputFilesBackedUp { get; set; }
        public int InputFilesRestored { get; set; }
        public List<DcsResolvedClick> Clicks { get; set; }
        public List<string> Steps { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public DcsRefreshResult()
        {
            Action = string.Empty;
            SavedGamesPath = string.Empty;
            InstallPath = string.Empty;
            DcsExecutablePath = string.Empty;
            ClicksXmlPath = string.Empty;
            ClicksImageFolder = string.Empty;
            InputPath = string.Empty;
            InputBackupPath = string.Empty;
            InputLayoutsPath = string.Empty;
            PreviewImagePath = string.Empty;
            DefaultStickReference = string.Empty;
            UseOcr = false;
            Confirm = true;
            DryRun = false;
            PreviewClicks = false;
            LaunchDcs = true;
            KeepDcsRunning = false;
            UsedWindowOverride = false;
            UsedRunningDcsProcess = false;
            StartedDcs = false;
            MenuWaitSeconds = 45;
            OcrMenuTimeoutSeconds = 600;
            SettleSeconds = 60;
            ModulesToScanRequested = 0;
            ModulesToScanResolved = 0;
            UsedHeadlessLuaRefresh = false;
            RequestedPlanes = new List<string>();
            MatchedPlanes = new List<string>();
            GuiScale = 1.0;
            Window = null;
            ClicksPlanned = 0;
            ClicksExecuted = 0;
            PlanesScanned = 0;
            HtmlFilesCopied = 0;
            CleanFilesWritten = 0;
            KeyboardCleanFilesWritten = 0;
            InputFilesBackedUp = 0;
            InputFilesRestored = 0;
            Clicks = new List<DcsResolvedClick>();
            Steps = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    internal enum DcsClickAnchor
    {
        TOP_LEFT,
        TOP_CENTER,
        TOP_RIGHT,
        CENTER_LEFT,
        CENTER_CENTER,
        CENTER_RIGHT,
        BOTTOM_LEFT,
        BOTTOM_CENTER,
        BOTTOM_RIGHT
    }

    internal sealed class DcsClickSpec
    {
        public string Name { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public DcsClickAnchor Anchor { get; set; }
        public int TimeoutMs { get; set; }

        public DcsClickSpec()
        {
            Name = string.Empty;
            OffsetX = 0;
            OffsetY = 0;
            Anchor = DcsClickAnchor.TOP_LEFT;
            TimeoutMs = 0;
        }

        public DcsClickSpec Clone()
        {
            return new DcsClickSpec
            {
                Name = Name,
                OffsetX = OffsetX,
                OffsetY = OffsetY,
                Anchor = Anchor,
                TimeoutMs = TimeoutMs
            };
        }
    }

    internal sealed class DcsClickProfile
    {
        public DcsClickSpec GearSymbol { get; set; }
        public DcsClickSpec OptionsControls { get; set; }
        public DcsClickSpec OptionsControlsPlaneDropDown { get; set; }
        public DcsClickSpec OptionsControlsMakeHTML { get; set; }
        public DcsClickSpec OptionsControlsClearAll { get; set; }
        public DcsClickSpec OptionsControlsClearAllCheckAll { get; set; }
        public DcsClickSpec OptionsControlsClearAllYes { get; set; }

        public DcsClickProfile()
        {
            GearSymbol = new DcsClickSpec();
            OptionsControls = new DcsClickSpec();
            OptionsControlsPlaneDropDown = new DcsClickSpec();
            OptionsControlsMakeHTML = new DcsClickSpec();
            OptionsControlsClearAll = new DcsClickSpec();
            OptionsControlsClearAllCheckAll = new DcsClickSpec();
            OptionsControlsClearAllYes = new DcsClickSpec();
        }

        public List<DcsClickSpec> AllClicks()
        {
            return new List<DcsClickSpec>
            {
                GearSymbol,
                OptionsControls,
                OptionsControlsPlaneDropDown,
                OptionsControlsMakeHTML,
                OptionsControlsClearAll,
                OptionsControlsClearAllCheckAll,
                OptionsControlsClearAllYes
            };
        }
    }

    [XmlRoot("DCSDBClicks")]
    public sealed class DcsClickProfileXml
    {
        public DcsClickXml GearSymbol { get; set; }
        public DcsClickXml OptionsControls { get; set; }
        public DcsClickXml OptionsControlsPlaneDropDown { get; set; }
        public DcsClickXml OptionsControlsMakeHTML { get; set; }
        public DcsClickXml OptionsControlsClearAll { get; set; }
        public DcsClickXml OptionsControlsClearAllCheckAll { get; set; }
        public DcsClickXml OptionsControlsClearAllYes { get; set; }
    }

    public sealed class DcsClickXml
    {
        [XmlElement("x")]
        public int X { get; set; }

        [XmlElement("y")]
        public int Y { get; set; }

        [XmlElement("Anchor")]
        public string Anchor { get; set; }

        [XmlElement("TimeoutMS")]
        public int TimeoutMs { get; set; }
    }

    internal sealed class DcsOptionsSnapshot
    {
        public string OptionsLuaPath { get; set; }
        public bool FileExists { get; set; }
        public bool HasScaleGui { get; set; }
        public double ScaleGui { get; set; }
        public bool HasFullScreen { get; set; }
        public bool FullScreen { get; set; }

        public DcsOptionsSnapshot()
        {
            OptionsLuaPath = string.Empty;
            FileExists = false;
            HasScaleGui = false;
            ScaleGui = 1.0;
            HasFullScreen = false;
            FullScreen = false;
        }
    }

    public static partial class EngineDcsRefresh
    {
        private const int SW_RESTORE = 9;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const byte VK_UP = 0x26;
        private const byte VK_DOWN = 0x28;
        private const byte VK_MENU = 0x12;
        private const byte VK_F4 = 0x73;

        private static void ReportStatus(DcsRefreshOptions options, string message)
        {
            if (options == null || options.StatusSink == null || string.IsNullOrWhiteSpace(message))
                return;
            options.StatusSink(message);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        private const byte VK_CONTROL = 0x11;
        private const byte VK_W = 0x57;

        // The UI-driving refresh sends real mouse clicks and keys. Every one of them first checks that DCS is the
        // active window (and, for clicks, that the point is inside it) and that the user has not moved the mouse;
        // otherwise the run stops instead of clicking or typing into another program.
        private sealed class DcsInputGuardException : Exception
        {
            public DcsInputGuardException(string message)
                : base(message)
            {
            }
        }

        private static readonly string[] BrowserProcesses = { "msedge", "chrome", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "waterfox", "librewolf" };
        private static Process guardedDcsProcess;
        private static Point? lastSyntheticCursor;

        private static void StartInputGuard(Process dcsProcess)
        {
            guardedDcsProcess = dcsProcess;
            lastSyntheticCursor = null;
        }

        private static void StopInputGuard()
        {
            guardedDcsProcess = null;
            lastSyntheticCursor = null;
        }

        private static int ForegroundProcessId()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return 0;
            GetWindowThreadProcessId(window, out uint processId);
            return (int)processId;
        }

        private static string ForegroundProcessName()
        {
            int id = ForegroundProcessId();
            if (id == 0)
                return string.Empty;
            try
            {
                using (Process process = Process.GetProcessById(id))
                    return process.ProcessName;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }

        private static void GuardInput(string action, int x, int y, bool isClick)
        {
            Process dcs = guardedDcsProcess;
            if (dcs == null)
                return;

            dcs.Refresh();
            if (dcs.HasExited)
                throw new DcsInputGuardException("DCS closed before '" + action + "'; the refresh stopped.");

            if (lastSyntheticCursor.HasValue && GetCursorPos(out NativePoint cursor))
            {
                int dx = cursor.X - lastSyntheticCursor.Value.X;
                int dy = cursor.Y - lastSyntheticCursor.Value.Y;
                if (dx * dx + dy * dy > 40 * 40)
                    throw new DcsInputGuardException("The mouse was moved during the refresh; it stopped so JoyPro does not click into your other windows. Start it again and leave mouse and keyboard alone until it finishes.");
            }

            for (int attempt = 0; ForegroundProcessId() != dcs.Id; attempt++)
            {
                if (attempt >= 3)
                    throw new DcsInputGuardException("DCS is not the active window, so '" + action + "' was not sent (it would have gone to '" + ForegroundProcessName() + "'). Keep DCS in front while the refresh runs.");
                BringProcessToFront(dcs, new List<string>());
                SleepWithClamp(400);
            }

            if (isClick && TryGetProcessWindowRect(dcs, out DcsWindowRect rect) &&
                (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom))
                throw new DcsInputGuardException("'" + action + "' at " + x + "," + y + " is outside the DCS window; check the click profile / window position. Nothing was clicked.");
        }

        public static DcsClickPreviewResult PreviewClicks(DcsRefreshOptions options)
        {
            DcsRefreshOptions normalizedOptions = NormalizeOptions(options);
            ReportStatus(normalizedOptions, "DCS refresh preview-clicks started.");
            DcsClickPreviewResult result = new DcsClickPreviewResult();
            result.Action = "preview-clicks";
            result.SavedGamesPath = normalizedOptions.SavedGamesPath;
            result.InstallPath = normalizedOptions.InstallPath;
            result.UseOcr = normalizedOptions.UseOcr;

            string clicksXmlPath = ResolveClicksXmlPath(normalizedOptions);
            string clicksImageFolder = ResolveClicksImageFolder(normalizedOptions);
            result.ClicksXmlPath = clicksXmlPath;
            result.ClicksImageFolder = clicksImageFolder;

            DcsClickProfile clickProfile = LoadClickProfile(clicksXmlPath, result.Warnings);
            result.GuiScale = ResolveGuiScale(normalizedOptions, result.SavedGamesPath, result.Warnings);

            DcsWindowRect previewWindow = ResolvePreviewWindow(normalizedOptions, result.Warnings, out bool usedWindowOverride, out bool usedRunningDcsProcess);
            result.Window = previewWindow;
            result.UsedWindowOverride = usedWindowOverride;
            result.UsedRunningDcsProcess = usedRunningDcsProcess;

            List<DcsResolvedClick> resolved = new List<DcsResolvedClick>();
            if (normalizedOptions.UseOcr)
            {
                foreach (KeyValuePair<string, string> template in GetOcrTemplateMap())
                {
                    DcsResolvedClick click = ResolveOcrTemplate(template.Key, template.Value, clicksImageFolder, false, result.Warnings, result.Errors);
                    resolved.Add(click);
                }
            }
            else
            {
                foreach (DcsClickSpec click in clickProfile.AllClicks())
                {
                    resolved.Add(ResolveXmlClick(click, previewWindow, result.GuiScale));
                }
            }

            result.Clicks = resolved;
            result.PreviewImagePath = string.IsNullOrWhiteSpace(normalizedOptions.PreviewImagePath)
                ? BuildDefaultPreviewImagePath()
                : normalizedOptions.PreviewImagePath;

            if (!TryWritePreviewImage(result.PreviewImagePath, resolved, result.Warnings))
                result.PreviewImagePath = string.Empty;

            ReportStatus(
                normalizedOptions,
                "DCS refresh preview-clicks complete. Clicks resolved: " +
                result.Clicks.Count.ToString(CultureInfo.InvariantCulture) + ".");
            return result;
        }

        public static DcsRefreshResult RefreshClean(DcsRefreshOptions options)
        {
            DcsRefreshOptions normalizedOptions = NormalizeOptions(options);
            ReportStatus(normalizedOptions, "DCS refresh clean started.");
            DcsRefreshResult result = BuildRefreshResult("clean", normalizedOptions);

            if (!ValidateRequiredPaths(result, requireSavedGames: true, requireInstall: true))
                return result;

            string clicksXmlPath = ResolveClicksXmlPath(normalizedOptions);
            string clicksImageFolder = ResolveClicksImageFolder(normalizedOptions);
            result.ClicksXmlPath = clicksXmlPath;
            result.ClicksImageFolder = clicksImageFolder;
            DcsClickProfile clickProfile = LoadClickProfile(clicksXmlPath, result.Warnings);
            result.GuiScale = ResolveGuiScale(normalizedOptions, result.SavedGamesPath, result.Warnings);

            result.InputPath = Path.Combine(result.SavedGamesPath, "Config", "Input");
            result.InputBackupPath = Path.Combine(result.SavedGamesPath, "Config", "CLInput", "Input");
            result.InputLayoutsPath = Path.Combine(result.SavedGamesPath, "InputLayoutsTxt");
            RecoverInterruptedRefresh(result.SavedGamesPath, result.Warnings, result.Errors);
            if (result.Errors.Count > 0)
                return result;

            if (normalizedOptions.PreviewClicks)
            {
                ReportStatus(normalizedOptions, "Building click preview.");
                DcsClickPreviewResult preview = PreviewClicks(normalizedOptions);
                if (preview.Warnings.Count > 0)
                    result.Warnings.AddRange(preview.Warnings.Select(v => "[preview] " + v));
                if (preview.Errors.Count > 0)
                    result.Warnings.AddRange(preview.Errors.Select(v => "[preview] " + v));
                result.PreviewImagePath = preview.PreviewImagePath;
            }

            if (normalizedOptions.UseHeadlessLuaRefresh)
            {
                // DCS's Lua writes into a temporary folder until the clean profiles are written: the user may cancel.
                EngineProgress.SetCancelable(true);
                ReportStatus(normalizedOptions, "Discovering headless profile candidates.");
                if (!TryDiscoverAndFilterHeadlessProfileCandidates(normalizedOptions, result, out List<DcsHeadlessProfileCandidate> headlessCandidates))
                    return result;
                EngineProgress.ThrowIfCancelled("reading the DCS aircraft modules");
                if (normalizedOptions.UseOcr)
                    result.Warnings.Add("--ocr is ignored when --headless-lua is enabled.");
                if (normalizedOptions.PreviewClicks)
                    result.Warnings.Add("--preview-clicks is ignored when --headless-lua is enabled.");
                if (normalizedOptions.LaunchDcs)
                    result.Warnings.Add("--launch is ignored when --headless-lua is enabled.");
                if (normalizedOptions.KeepDcsRunning)
                    result.Warnings.Add("--keep-running is ignored when --headless-lua is enabled.");

                if (normalizedOptions.DryRun)
                {
                    result.Steps.Add("Dry-run mode: headless Lua refresh clean execution was skipped.");
                    result.PlanesScanned = Math.Max(result.PlanesScanned, headlessCandidates.Count);
                    return result;
                }

                if (!ConfirmIfNeeded(normalizedOptions, "Refresh DCS clean DB will run headless Lua clear/save flow and rebuild clean profiles. Continue?", result))
                    return result;
                // The switched-on install options first (a DCS update brought files back), so the clean profiles
                // match what DCS will load.
                EngineDcsDefaultsStrip.AutoBeforeRefresh(normalizedOptions.InstallPath, normalizedOptions.SavedGamesPath, result.Warnings, result.Steps);

                // DCS's own Lua clears every profile for a generic stick and for one fake stick per device type that
                // has a default file, writing into a temporary Saved Games folder: the user's Config\Input is never
                // read or touched, so the user's diffs cannot leak into the clean profiles.
                string scratchSavedGames = Path.Combine(Path.GetTempPath(), "JoyPro_dcs_clean_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(scratchSavedGames);
                    // One luae process per few aircraft: every profile times every fake stick is loaded at once
                    // otherwise (all modules: > 4 GB and over the time limit).
                    const int chunkSize = 8;
                    int total = headlessCandidates.Count;
                    for (int start = 0; start < headlessCandidates.Count; start += chunkSize)
                    {
                        EngineProgress.ThrowIfCancelled("building the DCS clean profiles");
                        List<DcsHeadlessProfileCandidate> chunk = headlessCandidates.Skip(start).Take(chunkSize).ToList();
                        ReportStatus(normalizedOptions, "Running headless Lua clean refresh (" + (start + chunk.Count).ToString(CultureInfo.InvariantCulture) + "/" + headlessCandidates.Count.ToString(CultureInfo.InvariantCulture) + " profiles).");
                        // DCS's Lua names the profile it clears; the count is the chunk's share of all aircraft.
                        int chunkStart = start;
                        int chunkCount = chunk.Count;
                        Action<string, int, int, string> cleanProgress = (stage, index, count, profile) =>
                        {
                            int done = count <= 0 ? 1 : (int)Math.Ceiling((double)Math.Max(1, index) / count * chunkCount);
                            string what = stage == "load" ? "DCS World, loading the default binds" : stage == "save" ? "DCS World, saving the cleared binds" : "DCS World, clearing the binds";
                            EngineProgress.Report(chunkStart + Math.Min(chunkCount, Math.Max(1, done)), total, what, profile);
                        };
                        int errorsBefore = result.Errors.Count;
                        if (RunHeadlessLuaRefresh(normalizedOptions, result, chunk, exportHtml: false, savedGamesOverride: scratchSavedGames, luaProgress: cleanProgress))
                            continue;
                        // luae.exe can crash on one module's profile: retry the chunk one profile at a time and skip
                        // only the profiles that still fail (their previous clean profiles stay).
                        result.Errors.RemoveRange(errorsBefore, result.Errors.Count - errorsBefore);
                        foreach (DcsHeadlessProfileCandidate single in chunk)
                        {
                            EngineProgress.ThrowIfCancelled("building the DCS clean profiles");
                            int singleErrors = result.Errors.Count;
                            if (RunHeadlessLuaRefresh(normalizedOptions, result, new List<DcsHeadlessProfileCandidate> { single }, exportHtml: false, savedGamesOverride: scratchSavedGames))
                                continue;
                            string reason = string.Join(" ", result.Errors.Skip(singleErrors));
                            result.Errors.RemoveRange(singleErrors, result.Errors.Count - singleErrors);
                            result.Warnings.Add("DCS clean refresh skipped '" + single.UnitName + "' (" + reason + "); its previous clean profiles stay.");
                        }
                    }

                    EngineProgress.ThrowIfCancelled("building the DCS clean profiles");
                    EngineProgress.SetCancelable(false);
                    ReportStatus(normalizedOptions, "Rebuilding clean profile files.");
                    if (!RecreateCleanProfiles(scratchSavedGames, result.DefaultStickReference, result, result.Errors, TemplatesByPlane(headlessCandidates)))
                        return result;
                    bool someAircraft = normalizedOptions.PlaneFilter != null && normalizedOptions.PlaneFilter.Count > 0;
                    WriteCoveredDeviceTypes(result, someAircraft);
                    if (!someAircraft && result.Errors.Count == 0)
                        EngineDcsDefaultsStrip.MarkCleanProfilesCurrent();
                }
                finally
                {
                    List<string> ignored = new List<string>();
                    SafeDeleteDirectory(scratchSavedGames, ignored, ignored);
                }

                return result;
            }

            if (normalizedOptions.PlaneFilter != null && normalizedOptions.PlaneFilter.Count > 0)
                result.Warnings.Add("--plane/--planes currently apply only when --headless-lua is enabled for dcs refresh clean.");

            if (normalizedOptions.DryRun)
            {
                result.Steps.Add("Dry-run mode: refresh clean execution was skipped.");
                if (!normalizedOptions.UseOcr)
                {
                    DcsWindowRect dryRunWindow;
                    bool usedOverride;
                    bool usedDcsWindow;
                    if (normalizedOptions.WindowOverride != null)
                    {
                        dryRunWindow = normalizedOptions.WindowOverride.Clone();
                        usedOverride = true;
                        usedDcsWindow = false;
                    }
                    else
                    {
                        dryRunWindow = ResolvePreviewWindow(normalizedOptions, result.Warnings, out usedOverride, out usedDcsWindow);
                    }
                    result.Window = dryRunWindow;
                    result.UsedWindowOverride = usedOverride;
                    result.UsedRunningDcsProcess = usedDcsWindow;
                    List<DcsClickSpec> dryRunSequence = BuildCleanXmlClickSequence(clickProfile);
                    foreach (DcsClickSpec click in dryRunSequence)
                        result.Clicks.Add(ResolveXmlClick(click, dryRunWindow, result.GuiScale));
                    result.ClicksPlanned = result.Clicks.Count;
                }
                else
                {
                    result.Warnings.Add("Dry-run with OCR does not execute or resolve OCR templates.");
                }
                return result;
            }

            if (!ConfirmIfNeeded(normalizedOptions, "Refresh DCS clean DB will clear all in-game bindings and rebuild clean profiles. Continue?", result))
                return result;

            DcsOptionsSnapshot optionsSnapshot = ReadDcsOptionsSnapshot(result.SavedGamesPath, result.Warnings);
            bool optionsPatched = false;
            bool backupCreated = false;
            Process dcsProcess = null;

            try
            {
                if (Directory.Exists(result.InputPath))
                {
                    ReportStatus(normalizedOptions, "Backing up current Input folder.");
                    SafeDeleteDirectory(Path.Combine(result.SavedGamesPath, "Config", "CLInput"), result.Warnings, result.Errors);
                    int backedUp = CopyDirectoryTree(result.InputPath, result.InputBackupPath, true, result.Errors);
                    if (backedUp < 0)
                        return result;
                    result.InputFilesBackedUp = backedUp;
                    backupCreated = true;
                    WriteRestoreMarker(result);
                    SafeDeleteDirectory(result.InputPath, result.Warnings, result.Errors);
                }
                else
                {
                    result.Warnings.Add("DCS input folder did not exist before refresh: " + result.InputPath);
                }

                if (PatchDcsOptionsForRefresh(optionsSnapshot, result.Warnings, result.Errors))
                    optionsPatched = true;

                ReportStatus(normalizedOptions, "Launching/acquiring DCS process.");
                dcsProcess = AcquireDcsProcess(normalizedOptions, result);
                if (dcsProcess == null || result.Errors.Count > 0)
                    return result;
                StartInputGuard(dcsProcess);

                BringProcessToFront(dcsProcess, result.Warnings);
                ReportStatus(normalizedOptions, "Waiting for DCS main menu.");
                if (!WaitForMainMenu(normalizedOptions, clicksImageFolder, result))
                    return result;

                if (normalizedOptions.UseOcr)
                {
                    Dictionary<string, DcsResolvedClick> cache = new Dictionary<string, DcsResolvedClick>(StringComparer.OrdinalIgnoreCase);
                    if (!ExecuteOcrClick("GearSymbol", "cog1.0.png", clickProfile.GearSymbol.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                    if (!ExecuteOcrClick("OptionsControls", "controls1.0.png", clickProfile.OptionsControls.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                    if (!ExecuteOcrClick("OptionsControlsClearAll", "ClearAll1.0.png", clickProfile.OptionsControlsClearAll.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                    if (!ExecuteOcrClick("OptionsControlsClearAllCheckAll", "CheckAll1.0.png", clickProfile.OptionsControlsClearAllCheckAll.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                    if (!ExecuteOcrClick("OptionsControlsClearAllYes", "yes1.0.png", clickProfile.OptionsControlsClearAllYes.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                }
                else
                {
                    if (!TryResolveActionWindowRect(normalizedOptions, dcsProcess, result, out DcsWindowRect windowRect))
                        return result;

                    result.Window = windowRect;
                    List<DcsClickSpec> sequence = BuildCleanXmlClickSequence(clickProfile);
                    result.ClicksPlanned = sequence.Count;
                    foreach (DcsClickSpec click in sequence)
                    {
                        DcsResolvedClick resolvedClick = ResolveXmlClick(click, windowRect, result.GuiScale);
                        result.Clicks.Add(resolvedClick);
                        if (!MouseLeftClick(resolvedClick.X, resolvedClick.Y, result.Warnings))
                            result.Warnings.Add("Click failed at " + resolvedClick.X + "," + resolvedClick.Y + " for " + resolvedClick.Name);
                        result.ClicksExecuted++;
                        SleepWithClamp(click.TimeoutMs);
                    }
                }

                SleepWithClamp(normalizedOptions.SettleSeconds * 1000);
                if (!normalizedOptions.KeepDcsRunning)
                    StopProcessSafe(dcsProcess, result.Warnings);

                ReportStatus(normalizedOptions, "Rebuilding clean profile files.");
                if (!RecreateCleanProfiles(result.SavedGamesPath, result.DefaultStickReference, result, result.Errors))
                    return result;
            }
            catch (DcsInputGuardException ex)
            {
                result.Errors.Add(ex.Message);
                if (!normalizedOptions.KeepDcsRunning)
                    StopProcessSafe(dcsProcess, result.Warnings);
            }
            finally
            {
                StopInputGuard();
                if (optionsPatched)
                    RestoreDcsOptions(optionsSnapshot, result.Warnings, result.Errors);

                if (backupCreated)
                    RestoreInputBackup(result, result.Errors);
            }

            ReportStatus(normalizedOptions, "DCS refresh clean complete.");
            return result;
        }

        public static DcsRefreshResult RefreshId(DcsRefreshOptions options)
        {
            DcsRefreshOptions normalizedOptions = NormalizeOptions(options);
            ReportStatus(normalizedOptions, "DCS refresh id started.");
            DcsRefreshResult result = BuildRefreshResult("id", normalizedOptions);

            if (!ValidateRequiredPaths(result, requireSavedGames: true, requireInstall: true))
                return result;

            string clicksXmlPath = ResolveClicksXmlPath(normalizedOptions);
            string clicksImageFolder = ResolveClicksImageFolder(normalizedOptions);
            result.ClicksXmlPath = clicksXmlPath;
            result.ClicksImageFolder = clicksImageFolder;
            DcsClickProfile clickProfile = LoadClickProfile(clicksXmlPath, result.Warnings);
            result.GuiScale = ResolveGuiScale(normalizedOptions, result.SavedGamesPath, result.Warnings);
            result.InputLayoutsPath = Path.Combine(result.SavedGamesPath, "InputLayoutsTxt");

            if (normalizedOptions.PreviewClicks)
            {
                ReportStatus(normalizedOptions, "Building click preview.");
                DcsClickPreviewResult preview = PreviewClicks(normalizedOptions);
                if (preview.Warnings.Count > 0)
                    result.Warnings.AddRange(preview.Warnings.Select(v => "[preview] " + v));
                if (preview.Errors.Count > 0)
                    result.Warnings.AddRange(preview.Errors.Select(v => "[preview] " + v));
                result.PreviewImagePath = preview.PreviewImagePath;
            }

            result.ModulesToScanResolved = CountInstalledAircraft(result.InstallPath, result.SavedGamesPath, result.ModulesToScanRequested);
            ReportStatus(
                normalizedOptions,
                "Resolved modules to scan: " +
                result.ModulesToScanResolved.ToString(CultureInfo.InvariantCulture) + ".");
            if (result.ModulesToScanResolved <= 0)
                result.Warnings.Add("No aircraft were detected for ID refresh export loop.");

            if (normalizedOptions.UseHeadlessLuaRefresh)
            {
                // Until the DB files are copied nothing of JoyPro's is written: the user may cancel (EngineProgress).
                EngineProgress.SetCancelable(true);
                ReportStatus(normalizedOptions, "Discovering headless profile candidates.");
                if (!TryDiscoverAndFilterHeadlessProfileCandidates(normalizedOptions, result, out List<DcsHeadlessProfileCandidate> headlessCandidates))
                    return result;
                EngineProgress.ThrowIfCancelled("reading the DCS aircraft modules");
                if (normalizedOptions.UseOcr)
                    result.Warnings.Add("--ocr is ignored when --headless-lua is enabled.");
                if (normalizedOptions.PreviewClicks)
                    result.Warnings.Add("--preview-clicks is ignored when --headless-lua is enabled.");
                if (normalizedOptions.KeepDcsRunning)
                    result.Warnings.Add("--keep-running is ignored when --headless-lua is enabled.");

                if (normalizedOptions.DryRun)
                {
                    result.Steps.Add("Dry-run mode: headless Lua refresh id execution was skipped.");
                    result.PlanesScanned = Math.Max(result.PlanesScanned, headlessCandidates.Count);
                    return result;
                }

                if (!ConfirmIfNeeded(normalizedOptions, "Refresh DCS ID DB will run headless Lua export (no DCS UI automation) and rebuild DB HTML exports. Continue?", result))
                    return result;
                EngineDcsDefaultsStrip.AutoBeforeRefresh(normalizedOptions.InstallPath, normalizedOptions.SavedGamesPath, result.Warnings, result.Steps);

                ReportStatus(normalizedOptions, "Running headless Lua id refresh.");
                EngineProgress.Report(0, headlessCandidates.Count, "DCS World, starting DCS's Lua", "luae.exe");
                if (!RunHeadlessLuaRefresh(normalizedOptions, result, headlessCandidates, exportHtml: true,
                        luaProgress: (stage, index, count, profile) => EngineProgress.Report(index, count, "DCS World, reading inputs", profile)))
                    return result;
                EngineProgress.ThrowIfCancelled("reading the DCS aircraft inputs");

                EngineProgress.SetCancelable(false);
                ReportStatus(normalizedOptions, "Copying largest exported HTML files into DB.");
                if (!CopyLargestExportedHtml(normalizedOptions, result, result.Errors))
                    return result;

                ReportStatus(normalizedOptions, "DCS refresh id complete.");
                return result;
            }

            if (normalizedOptions.PlaneFilter != null && normalizedOptions.PlaneFilter.Count > 0)
                result.Warnings.Add("--plane/--planes currently apply only when --headless-lua is enabled for dcs refresh id.");

            if (normalizedOptions.DryRun)
            {
                result.Steps.Add("Dry-run mode: refresh id execution was skipped.");
                result.ClicksPlanned = 0;
                if (!normalizedOptions.UseOcr)
                {
                    DcsWindowRect dryRunWindow;
                    bool usedOverride;
                    bool usedDcsWindow;
                    if (normalizedOptions.WindowOverride != null)
                    {
                        dryRunWindow = normalizedOptions.WindowOverride.Clone();
                        usedOverride = true;
                        usedDcsWindow = false;
                    }
                    else
                    {
                        dryRunWindow = ResolvePreviewWindow(normalizedOptions, result.Warnings, out usedOverride, out usedDcsWindow);
                    }
                    result.Window = dryRunWindow;
                    result.UsedWindowOverride = usedOverride;
                    result.UsedRunningDcsProcess = usedDcsWindow;
                    foreach (DcsClickSpec click in BuildIdXmlClickSequence(clickProfile))
                        result.Clicks.Add(ResolveXmlClick(click, dryRunWindow, result.GuiScale));
                    result.ClicksPlanned = result.Clicks.Count;
                }
                else
                {
                    result.Warnings.Add("Dry-run with OCR does not execute or resolve OCR templates.");
                }
                return result;
            }

            if (!ConfirmIfNeeded(normalizedOptions, "Refresh DCS ID DB will drive the DCS UI and rebuild DB HTML exports. Continue?", result))
                return result;

            DcsOptionsSnapshot optionsSnapshot = ReadDcsOptionsSnapshot(result.SavedGamesPath, result.Warnings);
            bool optionsPatched = false;
            Process dcsProcess = null;

            try
            {
                if (PatchDcsOptionsForRefresh(optionsSnapshot, result.Warnings, result.Errors))
                    optionsPatched = true;

                ReportStatus(normalizedOptions, "Launching/acquiring DCS process.");
                dcsProcess = AcquireDcsProcess(normalizedOptions, result);
                if (dcsProcess == null || result.Errors.Count > 0)
                    return result;
                StartInputGuard(dcsProcess);

                BringProcessToFront(dcsProcess, result.Warnings);
                ReportStatus(normalizedOptions, "Waiting for DCS main menu.");
                if (!WaitForMainMenu(normalizedOptions, clicksImageFolder, result))
                    return result;

                Dictionary<string, DcsResolvedClick> cache = new Dictionary<string, DcsResolvedClick>(StringComparer.OrdinalIgnoreCase);
                if (normalizedOptions.UseOcr)
                {
                    if (!ExecuteOcrClick("GearSymbol", "cog1.0.png", clickProfile.GearSymbol.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                    if (!ExecuteOcrClick("OptionsControls", "controls1.0.png", clickProfile.OptionsControls.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                    SleepWithClamp(6000);
                    if (!ExecuteOcrClick("OptionsControlsPlaneDropDown", "dropmenu.png", clickProfile.OptionsControlsPlaneDropDown.TimeoutMs, clicksImageFolder, cache, result))
                        return result;
                }
                else
                {
                    if (!TryResolveActionWindowRect(normalizedOptions, dcsProcess, result, out DcsWindowRect windowRect))
                        return result;
                    result.Window = windowRect;

                    List<DcsClickSpec> setupSequence = new List<DcsClickSpec>
                    {
                        clickProfile.GearSymbol.Clone(),
                        clickProfile.OptionsControls.Clone()
                    };
                    foreach (DcsClickSpec click in setupSequence)
                    {
                        DcsResolvedClick resolved = ResolveXmlClick(click, windowRect, result.GuiScale);
                        result.Clicks.Add(resolved);
                        MouseLeftClick(resolved.X, resolved.Y, result.Warnings);
                        result.ClicksExecuted++;
                        SleepWithClamp(click.TimeoutMs);
                    }
                    SleepWithClamp(6000);
                    DcsResolvedClick dropClick = ResolveXmlClick(clickProfile.OptionsControlsPlaneDropDown, windowRect, result.GuiScale);
                    result.Clicks.Add(dropClick);
                    MouseLeftClick(dropClick.X, dropClick.Y, result.Warnings);
                    result.ClicksExecuted++;
                    SleepWithClamp(clickProfile.OptionsControlsPlaneDropDown.TimeoutMs);
                }

                for (int i = 0; i < result.ModulesToScanResolved * 2; i++)
                {
                    PressArrowUp();
                    SleepWithClamp(50);
                }

                SleepWithClamp(15000);

                for (int i = 0; i < result.ModulesToScanResolved; i++)
                {
                    ReportStatus(
                        normalizedOptions,
                        "Processing module " +
                        (i + 1).ToString(CultureInfo.InvariantCulture) +
                        "/" +
                        result.ModulesToScanResolved.ToString(CultureInfo.InvariantCulture) + ".");
                    SleepWithClamp(500);
                    if (normalizedOptions.UseOcr)
                    {
                        if (!ExecuteOcrClick("OptionsControlsMakeHTML", "makehtml1.0.png", clickProfile.OptionsControlsMakeHTML.TimeoutMs, clicksImageFolder, cache, result))
                            return result;
                    }
                    else
                    {
                        DcsResolvedClick makeClick = ResolveXmlClick(clickProfile.OptionsControlsMakeHTML, result.Window, result.GuiScale);
                        result.Clicks.Add(makeClick);
                        MouseLeftClick(makeClick.X, makeClick.Y, result.Warnings);
                        result.ClicksExecuted++;
                        SleepWithClamp(clickProfile.OptionsControlsMakeHTML.TimeoutMs);
                    }

                    CloseCurrentWindow();
                    SleepWithClamp(500);

                    if (dcsProcess.HasExited)
                    {
                        result.Errors.Add("DCS exited unexpectedly during ID refresh export loop.");
                        return result;
                    }

                    BringProcessToFront(dcsProcess, result.Warnings);
                    SleepWithClamp(500);

                    if (normalizedOptions.UseOcr)
                    {
                        if (!ExecuteOcrClick("OptionsControlsPlaneDropDown", "dropmenu.png", clickProfile.OptionsControlsPlaneDropDown.TimeoutMs, clicksImageFolder, cache, result))
                            return result;
                    }
                    else
                    {
                        DcsResolvedClick dropClick = ResolveXmlClick(clickProfile.OptionsControlsPlaneDropDown, result.Window, result.GuiScale);
                        result.Clicks.Add(dropClick);
                        MouseLeftClick(dropClick.X, dropClick.Y, result.Warnings);
                        result.ClicksExecuted++;
                        SleepWithClamp(clickProfile.OptionsControlsPlaneDropDown.TimeoutMs);
                    }

                    PressArrowDown();
                    result.PlanesScanned++;
                }

                if (!normalizedOptions.KeepDcsRunning)
                    StopProcessSafe(dcsProcess, result.Warnings);

                ReportStatus(normalizedOptions, "Copying largest exported HTML files into DB.");
                if (!CopyLargestExportedHtml(normalizedOptions, result, result.Errors))
                    return result;
            }
            catch (DcsInputGuardException ex)
            {
                result.Errors.Add(ex.Message);
                if (!normalizedOptions.KeepDcsRunning)
                    StopProcessSafe(dcsProcess, result.Warnings);
            }
            finally
            {
                StopInputGuard();
                if (optionsPatched)
                    RestoreDcsOptions(optionsSnapshot, result.Warnings, result.Errors);
            }

            ReportStatus(normalizedOptions, "DCS refresh id complete.");
            return result;
        }

        private static DcsRefreshOptions NormalizeOptions(DcsRefreshOptions options)
        {
            DcsRefreshOptions normalized = options ?? new DcsRefreshOptions();
            normalized.SavedGamesPath = NormalizePath(normalized.SavedGamesPath);
            normalized.InstallPath = NormalizePath(normalized.InstallPath);
            normalized.ClicksXmlPath = NormalizePath(normalized.ClicksXmlPath);
            normalized.ClicksImageFolder = NormalizePath(normalized.ClicksImageFolder);
            normalized.DcsExecutablePath = NormalizePath(normalized.DcsExecutablePath);
            normalized.PreviewImagePath = NormalizePath(normalized.PreviewImagePath);
            normalized.DefaultStickReference = (normalized.DefaultStickReference ?? string.Empty).Trim();
            if (normalized.MenuWaitSeconds < 0)
                normalized.MenuWaitSeconds = 0;
            if (normalized.OcrMenuTimeoutSeconds < 1)
                normalized.OcrMenuTimeoutSeconds = 1;
            if (normalized.SettleSeconds < 0)
                normalized.SettleSeconds = 0;
            if (normalized.ModulesToScan < 0)
                normalized.ModulesToScan = 0;
            normalized.PlaneFilter = NormalizePlaneFilters(normalized.PlaneFilter);
            return normalized;
        }

        private static List<string> NormalizePlaneFilters(IEnumerable<string> rawFilters)
        {
            List<string> normalized = new List<string>();
            if (rawFilters == null)
                return normalized;

            foreach (string raw in rawFilters)
            {
                string value = (raw ?? string.Empty).Trim();
                if (value.Length == 0)
                    continue;

                int split = value.IndexOf(':');
                if (split > 0 && split < value.Length - 1)
                {
                    string prefix = value.Substring(0, split).Trim();
                    if (prefix.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                        value = value.Substring(split + 1).Trim();
                }

                if (value.Length == 0)
                    continue;
                if (!normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
                    normalized.Add(value);
            }

            return normalized;
        }

        private static DcsRefreshResult BuildRefreshResult(string action, DcsRefreshOptions options)
        {
            DcsRefreshResult result = new DcsRefreshResult();
            result.Action = action;
            result.SavedGamesPath = options.SavedGamesPath;
            result.InstallPath = options.InstallPath;
            result.DcsExecutablePath = options.DcsExecutablePath;
            result.PreviewImagePath = options.PreviewImagePath;
            result.DefaultStickReference = options.DefaultStickReference;
            result.UseOcr = options.UseOcr;
            result.Confirm = options.Confirm;
            result.DryRun = options.DryRun;
            result.PreviewClicks = options.PreviewClicks;
            result.LaunchDcs = options.UseHeadlessLuaRefresh ? false : options.LaunchDcs;
            result.KeepDcsRunning = options.UseHeadlessLuaRefresh ? false : options.KeepDcsRunning;
            result.MenuWaitSeconds = options.MenuWaitSeconds;
            result.OcrMenuTimeoutSeconds = options.OcrMenuTimeoutSeconds;
            result.SettleSeconds = options.SettleSeconds;
            result.ModulesToScanRequested = options.ModulesToScan;
            result.UsedHeadlessLuaRefresh = options.UseHeadlessLuaRefresh;
            result.RequestedPlanes = new List<string>((options.PlaneFilter ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase));
            return result;
        }

        private static bool ValidateRequiredPaths(DcsRefreshResult result, bool requireSavedGames, bool requireInstall)
        {
            if (requireSavedGames)
            {
                if (string.IsNullOrWhiteSpace(result.SavedGamesPath))
                    result.Errors.Add("Missing DCS Saved Games path.");
                else if (!Directory.Exists(result.SavedGamesPath))
                    result.Errors.Add("DCS Saved Games path does not exist: " + result.SavedGamesPath);
            }

            if (requireInstall)
            {
                if (string.IsNullOrWhiteSpace(result.InstallPath))
                    result.Errors.Add("Missing DCS install path.");
                else if (!Directory.Exists(result.InstallPath))
                    result.Errors.Add("DCS install path does not exist: " + result.InstallPath);
            }

            return result.Errors.Count == 0;
        }

        private static string ResolveClicksXmlPath(DcsRefreshOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.ClicksXmlPath))
                return options.ClicksXmlPath;
            return Path.Combine(MainStructure.PROGPATH, "Clicks", "Clicks.xml");
        }

        private static string ResolveClicksImageFolder(DcsRefreshOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.ClicksImageFolder))
                return options.ClicksImageFolder;
            return Path.Combine(MainStructure.PROGPATH, "Clicks");
        }

        private static DcsClickProfile LoadClickProfile(string clicksXmlPath, List<string> warnings)
        {
            DcsClickProfile profile = BuildDefaultClickProfile();
            if (string.IsNullOrWhiteSpace(clicksXmlPath))
            {
                warnings.Add("Clicks XML path not set. Using built-in defaults.");
                return profile;
            }

            if (!File.Exists(clicksXmlPath))
            {
                warnings.Add("Clicks XML not found at '" + clicksXmlPath + "'. Using built-in defaults.");
                return profile;
            }

            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(DcsClickProfileXml));
                using (FileStream stream = File.OpenRead(clicksXmlPath))
                {
                    DcsClickProfileXml xml = serializer.Deserialize(stream) as DcsClickProfileXml;
                    if (xml != null)
                        MergeClickProfile(profile, xml, warnings);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Failed to load clicks XML '" + clicksXmlPath + "': " + ex.Message + ". Using built-in defaults.");
            }

            return profile;
        }

        private static DcsClickProfile BuildDefaultClickProfile()
        {
            DcsClickProfile profile = new DcsClickProfile();
            profile.GearSymbol = NewClick("GearSymbol", 564, 26, DcsClickAnchor.TOP_LEFT, 2000);
            profile.OptionsControls = NewClick("OptionsControls", -365, 70, DcsClickAnchor.TOP_CENTER, 2000);
            profile.OptionsControlsPlaneDropDown = NewClick("OptionsControlsPlaneDropDown", 70, 100, DcsClickAnchor.TOP_LEFT, 500);
            profile.OptionsControlsMakeHTML = NewClick("OptionsControlsMakeHTML", 310, -70, DcsClickAnchor.BOTTOM_CENTER, 1500);
            profile.OptionsControlsClearAll = NewClick("OptionsControlsClearAll", 305, 111, DcsClickAnchor.TOP_CENTER, 2000);
            profile.OptionsControlsClearAllCheckAll = NewClick("OptionsControlsClearAllCheckAll", -182, -76, DcsClickAnchor.CENTER_CENTER, 2000);
            profile.OptionsControlsClearAllYes = NewClick("OptionsControlsClearAllYes", -58, 169, DcsClickAnchor.CENTER_CENTER, 60000);
            return profile;
        }

        private static DcsClickSpec NewClick(string name, int x, int y, DcsClickAnchor anchor, int timeoutMs)
        {
            return new DcsClickSpec
            {
                Name = name,
                OffsetX = x,
                OffsetY = y,
                Anchor = anchor,
                TimeoutMs = timeoutMs
            };
        }

        private static void MergeClickProfile(DcsClickProfile target, DcsClickProfileXml xml, List<string> warnings)
        {
            target.GearSymbol = MergeClickSpec(target.GearSymbol, "GearSymbol", xml.GearSymbol, warnings);
            target.OptionsControls = MergeClickSpec(target.OptionsControls, "OptionsControls", xml.OptionsControls, warnings);
            target.OptionsControlsPlaneDropDown = MergeClickSpec(target.OptionsControlsPlaneDropDown, "OptionsControlsPlaneDropDown", xml.OptionsControlsPlaneDropDown, warnings);
            target.OptionsControlsMakeHTML = MergeClickSpec(target.OptionsControlsMakeHTML, "OptionsControlsMakeHTML", xml.OptionsControlsMakeHTML, warnings);
            target.OptionsControlsClearAll = MergeClickSpec(target.OptionsControlsClearAll, "OptionsControlsClearAll", xml.OptionsControlsClearAll, warnings);
            target.OptionsControlsClearAllCheckAll = MergeClickSpec(target.OptionsControlsClearAllCheckAll, "OptionsControlsClearAllCheckAll", xml.OptionsControlsClearAllCheckAll, warnings);
            target.OptionsControlsClearAllYes = MergeClickSpec(target.OptionsControlsClearAllYes, "OptionsControlsClearAllYes", xml.OptionsControlsClearAllYes, warnings);
        }

        private static DcsClickSpec MergeClickSpec(DcsClickSpec fallback, string name, DcsClickXml xml, List<string> warnings)
        {
            if (xml == null)
                return fallback;

            DcsClickAnchor anchor = ParseAnchor(xml.Anchor, fallback.Anchor, name, warnings);
            DcsClickSpec merged = fallback.Clone();
            merged.Name = name;
            merged.OffsetX = xml.X;
            merged.OffsetY = xml.Y;
            merged.Anchor = anchor;
            merged.TimeoutMs = xml.TimeoutMs;
            return merged;
        }

        private static DcsClickAnchor ParseAnchor(string rawAnchor, DcsClickAnchor fallback, string clickName, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(rawAnchor))
                return fallback;

            string trimmed = rawAnchor.Trim();
            foreach (DcsClickAnchor anchor in Enum.GetValues(typeof(DcsClickAnchor)))
            {
                if (string.Equals(anchor.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                    return anchor;
            }

            warnings.Add("Unknown click anchor '" + rawAnchor + "' on " + clickName + ". Using " + fallback + ".");
            return fallback;
        }

        private static DcsWindowRect ResolvePreviewWindow(DcsRefreshOptions options, List<string> warnings, out bool usedWindowOverride, out bool usedRunningDcsProcess)
        {
            usedWindowOverride = false;
            usedRunningDcsProcess = false;

            if (options.WindowOverride != null)
            {
                usedWindowOverride = true;
                return options.WindowOverride.Clone();
            }

            Process running = FindRunningDcsProcess();
            if (running != null && TryGetProcessWindowRect(running, out DcsWindowRect runningRect))
            {
                usedRunningDcsProcess = true;
                return runningRect;
            }

            warnings.Add("No DCS window found. Using primary screen bounds for click preview.");
            Rectangle screenBounds = GetPrimaryScreenBounds();
            return new DcsWindowRect
            {
                Left = screenBounds.Left,
                Top = screenBounds.Top,
                Right = screenBounds.Right,
                Bottom = screenBounds.Bottom
            };
        }

        private static double ResolveGuiScale(DcsRefreshOptions options, string savedGamesPath, List<string> warnings)
        {
            if (options.GuiScaleOverride.HasValue)
                return options.GuiScaleOverride.Value;

            DcsOptionsSnapshot snapshot = ReadDcsOptionsSnapshot(savedGamesPath, warnings);
            if (snapshot.HasScaleGui && snapshot.ScaleGui > 0)
                return snapshot.ScaleGui;

            return 1.0;
        }

        private static DcsOptionsSnapshot ReadDcsOptionsSnapshot(string savedGamesPath, List<string> warnings)
        {
            DcsOptionsSnapshot snapshot = new DcsOptionsSnapshot();
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                return snapshot;

            string optionsPath = Path.Combine(savedGamesPath, "Config", "options.lua");
            snapshot.OptionsLuaPath = optionsPath;
            snapshot.FileExists = File.Exists(optionsPath);
            if (!snapshot.FileExists)
                return snapshot;

            try
            {
                foreach (string line in File.ReadAllLines(optionsPath))
                {
                    if (!snapshot.HasScaleGui && line.IndexOf("[\"scaleGui\"]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (TryParseLuaAssignmentDouble(line, out double scale))
                        {
                            snapshot.ScaleGui = scale;
                            snapshot.HasScaleGui = true;
                        }
                    }
                    else if (!snapshot.HasFullScreen && line.IndexOf("[\"fullScreen\"]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (TryParseLuaAssignmentBoolean(line, out bool fullScreen))
                        {
                            snapshot.FullScreen = fullScreen;
                            snapshot.HasFullScreen = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Failed reading options.lua at '" + optionsPath + "': " + ex.Message);
            }

            return snapshot;
        }

        private static bool TryParseLuaAssignmentDouble(string line, out double value)
        {
            value = 0;
            int index = line == null ? -1 : line.IndexOf('=');
            if (index < 0)
                return false;
            string rhs = line.Substring(index + 1).Trim().TrimEnd(',');
            return double.TryParse(rhs, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseLuaAssignmentBoolean(string line, out bool value)
        {
            value = false;
            int index = line == null ? -1 : line.IndexOf('=');
            if (index < 0)
                return false;
            string rhs = line.Substring(index + 1).Trim().TrimEnd(',').ToLowerInvariant();
            if (rhs == "true")
            {
                value = true;
                return true;
            }
            if (rhs == "false")
            {
                value = false;
                return true;
            }
            return false;
        }

        private static bool PatchDcsOptionsForRefresh(DcsOptionsSnapshot snapshot, List<string> warnings, List<string> errors)
        {
            if (snapshot == null || !snapshot.FileExists || string.IsNullOrWhiteSpace(snapshot.OptionsLuaPath))
                return false;

            try
            {
                string[] lines = File.ReadAllLines(snapshot.OptionsLuaPath);
                bool touchedScale = false;
                bool touchedFullscreen = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.IndexOf("[\"scaleGui\"]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        touchedScale = true;
                        string prefix = line.Substring(0, line.IndexOf('=') + 1);
                        lines[i] = prefix + " 1.0,";
                    }
                    else if (line.IndexOf("[\"fullScreen\"]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        touchedFullscreen = true;
                        string prefix = line.Substring(0, line.IndexOf('=') + 1);
                        lines[i] = prefix + " false,";
                    }
                }

                File.WriteAllLines(snapshot.OptionsLuaPath, lines);
                if (!touchedScale)
                    warnings.Add("options.lua has no [\"scaleGui\"] entry. GUI scale was not forced.");
                if (!touchedFullscreen)
                    warnings.Add("options.lua has no [\"fullScreen\"] entry. Fullscreen flag was not forced.");
                return true;
            }
            catch (Exception ex)
            {
                errors.Add("Failed patching DCS options.lua for refresh: " + ex.Message);
                return false;
            }
        }

        private static void RestoreDcsOptions(DcsOptionsSnapshot snapshot, List<string> warnings, List<string> errors)
        {
            if (snapshot == null || !snapshot.FileExists || string.IsNullOrWhiteSpace(snapshot.OptionsLuaPath))
                return;

            try
            {
                string[] lines = File.ReadAllLines(snapshot.OptionsLuaPath);
                bool touchedScale = false;
                bool touchedFullscreen = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (snapshot.HasScaleGui && line.IndexOf("[\"scaleGui\"]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        touchedScale = true;
                        string prefix = line.Substring(0, line.IndexOf('=') + 1);
                        lines[i] = prefix + " " + snapshot.ScaleGui.ToString("0.###", CultureInfo.InvariantCulture) + ",";
                    }
                    else if (snapshot.HasFullScreen && line.IndexOf("[\"fullScreen\"]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        touchedFullscreen = true;
                        string prefix = line.Substring(0, line.IndexOf('=') + 1);
                        lines[i] = prefix + (snapshot.FullScreen ? " true," : " false,");
                    }
                }

                File.WriteAllLines(snapshot.OptionsLuaPath, lines);
                if (snapshot.HasScaleGui && !touchedScale)
                    warnings.Add("Could not restore [\"scaleGui\"] in options.lua.");
                if (snapshot.HasFullScreen && !touchedFullscreen)
                    warnings.Add("Could not restore [\"fullScreen\"] in options.lua.");
            }
            catch (Exception ex)
            {
                errors.Add("Failed restoring DCS options.lua: " + ex.Message);
            }
        }

        private static bool ConfirmIfNeeded(DcsRefreshOptions options, string prompt, DcsRefreshResult result)
        {
            if (!options.Confirm)
                return true;

            if (!Environment.UserInteractive || Console.IsInputRedirected)
            {
                result.Warnings.Add("Confirmation was requested, but console is non-interactive. Proceeding automatically.");
                return true;
            }

            Console.WriteLine(prompt);
            Console.Write("Type 'yes' to continue: ");
            string input = Console.ReadLine();
            if (!string.Equals((input ?? string.Empty).Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("Operation cancelled by user.");
                return false;
            }

            return true;
        }

        private static bool IsAffirmativeAnswer(string answer)
        {
            string normalized = (answer ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "y" ||
                   normalized == "yes" ||
                   normalized == "true" ||
                   normalized == "1";
        }

        private static int LevenshteinDistance(string left, string right)
        {
            string a = left ?? string.Empty;
            string b = right ?? string.Empty;
            if (a.Length == 0)
                return b.Length;
            if (b.Length == 0)
                return a.Length;

            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int deletion = previous[j] + 1;
                    int insertion = current[j - 1] + 1;
                    int substitution = previous[j - 1] + cost;
                    current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }

        private static Process AcquireDcsProcess(DcsRefreshOptions options, DcsRefreshResult result)
        {
            Process startedProcess = null;
            if (options.LaunchDcs)
            {
                string executable = ResolveDcsExecutable(options);
                if (string.IsNullOrWhiteSpace(executable))
                {
                    result.Errors.Add("Could not resolve DCS executable path.");
                    return null;
                }
                if (!File.Exists(executable))
                {
                    result.Errors.Add("DCS executable does not exist: " + executable);
                    return null;
                }

                result.DcsExecutablePath = executable;
                try
                {
                    startedProcess = Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = true
                    });
                    result.StartedDcs = true;
                    result.Steps.Add("Started DCS executable: " + executable);
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Failed to start DCS executable '" + executable + "': " + ex.Message);
                    return null;
                }
            }

            Process dcsProcess = WaitForRunningDcsProcess(options.LaunchDcs ? 120 : 5);
            if (dcsProcess == null)
            {
                if (options.LaunchDcs)
                    result.Errors.Add("DCS process did not appear after launch.");
                else
                    result.Errors.Add("No running DCS process found. Use --launch true or start DCS manually.");
                return null;
            }

            if (!options.LaunchDcs)
                result.UsedRunningDcsProcess = true;

            if (startedProcess != null)
            {
                try
                {
                    if (startedProcess.HasExited)
                        result.Warnings.Add("Launcher process exited quickly; continuing with detected DCS process.");
                }
                catch
                {
                    // ignore process refresh errors
                }
            }

            return dcsProcess;
        }

        private static string ResolveDcsExecutable(DcsRefreshOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.DcsExecutablePath))
                return options.DcsExecutablePath;

            if (string.IsNullOrWhiteSpace(options.InstallPath))
                return string.Empty;

            string binFolder = Path.Combine(options.InstallPath, "bin");
            string updater = Path.Combine(binFolder, "DCS_updater.exe");
            string dcsExe = Path.Combine(binFolder, "DCS.exe");
            bool installLooksSteam = options.InstallPath.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0;

            if (installLooksSteam)
            {
                if (File.Exists(dcsExe))
                    return dcsExe;
                if (File.Exists(updater))
                    return updater;
            }
            else
            {
                if (File.Exists(updater))
                    return updater;
                if (File.Exists(dcsExe))
                    return dcsExe;
            }

            if (File.Exists(dcsExe))
                return dcsExe;
            if (File.Exists(updater))
                return updater;
            return string.Empty;
        }

        private static Process FindRunningDcsProcess()
        {
            Process[] candidates = Process.GetProcessesByName("DCS");
            if (candidates == null || candidates.Length == 0)
                return null;
            return candidates[0];
        }

        private static Process WaitForRunningDcsProcess(int timeoutSeconds)
        {
            DateTime stopAt = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
            while (DateTime.UtcNow <= stopAt)
            {
                Process process = FindRunningDcsProcess();
                if (process != null)
                    return process;
                Thread.Sleep(1000);
            }
            return null;
        }

        private static void BringProcessToFront(Process process, List<string> warnings)
        {
            if (process == null)
                return;

            try
            {
                process.Refresh();
                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    return;
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
                SleepWithClamp(200);
            }
            catch (Exception ex)
            {
                warnings.Add("Failed to bring DCS window to front: " + ex.Message);
            }
        }

        private static bool WaitForMainMenu(DcsRefreshOptions options, string clicksImageFolder, DcsRefreshResult result)
        {
            if (!options.UseOcr)
            {
                result.Steps.Add("Waiting " + options.MenuWaitSeconds.ToString(CultureInfo.InvariantCulture) + "s for DCS menu.");
                SleepWithClamp(options.MenuWaitSeconds * 1000);
                return true;
            }

            string menuTemplate = Path.Combine(clicksImageFolder, "DCSMenu1.0.png");
            if (!File.Exists(menuTemplate))
            {
                result.Errors.Add("OCR menu template not found: " + menuTemplate);
                return false;
            }

            DateTime stopAt = DateTime.UtcNow.AddSeconds(Math.Max(1, options.OcrMenuTimeoutSeconds));
            while (DateTime.UtcNow < stopAt)
            {
                if (TryFindTemplateOnCurrentScreen(menuTemplate, out Rectangle matchRect, out Rectangle screenBounds, out string findError))
                {
                    if (matchRect != Rectangle.Empty)
                    {
                        result.Steps.Add(
                            "Detected DCS menu template at " +
                            (matchRect.Left + screenBounds.Left).ToString(CultureInfo.InvariantCulture) + "," +
                            (matchRect.Top + screenBounds.Top).ToString(CultureInfo.InvariantCulture));
                        return true;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(findError))
                {
                    result.Warnings.Add(findError);
                }

                SleepWithClamp(2000);
            }

            result.Errors.Add("Could not detect DCS menu via OCR before timeout (" + options.OcrMenuTimeoutSeconds + "s).");
            return false;
        }

        private static Dictionary<string, string> GetOcrTemplateMap()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "GearSymbol", "cog1.0.png" },
                { "OptionsControls", "controls1.0.png" },
                { "OptionsControlsPlaneDropDown", "dropmenu.png" },
                { "OptionsControlsMakeHTML", "makehtml1.0.png" },
                { "OptionsControlsClearAll", "ClearAll1.0.png" },
                { "OptionsControlsClearAllCheckAll", "CheckAll1.0.png" },
                { "OptionsControlsClearAllYes", "yes1.0.png" }
            };
        }

        private static DcsResolvedClick ResolveOcrTemplate(
            string logicalName,
            string templateFileName,
            string clicksImageFolder,
            bool required,
            List<string> warnings,
            List<string> errors)
        {
            DcsResolvedClick click = new DcsResolvedClick();
            click.Name = logicalName;
            click.Method = "ocr";
            click.Template = templateFileName;

            string templatePath = Path.Combine(clicksImageFolder, templateFileName);
            if (!File.Exists(templatePath))
            {
                string message = "OCR template not found: " + templatePath;
                if (required)
                    errors.Add(message);
                else
                    warnings.Add(message);
                return click;
            }

            if (!TryFindTemplateOnCurrentScreen(templatePath, out Rectangle matchRect, out Rectangle screenBounds, out string findError))
            {
                if (!string.IsNullOrWhiteSpace(findError))
                {
                    if (required)
                        errors.Add(findError);
                    else
                        warnings.Add(findError);
                }
                return click;
            }

            if (matchRect == Rectangle.Empty)
            {
                string miss = "OCR template not found on screen: " + templateFileName;
                if (required)
                    errors.Add(miss);
                else
                    warnings.Add(miss);
                return click;
            }

            click.Found = true;
            click.MatchLeft = matchRect.Left + screenBounds.Left;
            click.MatchTop = matchRect.Top + screenBounds.Top;
            click.MatchWidth = matchRect.Width;
            click.MatchHeight = matchRect.Height;
            click.X = click.MatchLeft + (click.MatchWidth / 2);
            click.Y = click.MatchTop + (click.MatchHeight / 2);
            return click;
        }

        private static bool ExecuteOcrClick(
            string logicalName,
            string templateFileName,
            int timeoutMs,
            string clicksImageFolder,
            Dictionary<string, DcsResolvedClick> cache,
            DcsRefreshResult result)
        {
            DcsResolvedClick resolved;
            if (cache.TryGetValue(templateFileName, out DcsResolvedClick cached) && cached != null && cached.Found)
            {
                resolved = cached.Clone();
                resolved.Name = logicalName;
            }
            else
            {
                resolved = ResolveOcrTemplate(logicalName, templateFileName, clicksImageFolder, true, result.Warnings, result.Errors);
                if (resolved.Found)
                    cache[templateFileName] = resolved.Clone();
            }

            resolved.TimeoutMs = timeoutMs;
            result.Clicks.Add(resolved);
            result.ClicksPlanned++;

            if (!resolved.Found)
                return false;

            if (!MouseLeftClick(resolved.X, resolved.Y, result.Warnings))
                result.Warnings.Add("Click failed at " + resolved.X + "," + resolved.Y + " for " + logicalName);

            result.ClicksExecuted++;
            SleepWithClamp(timeoutMs);
            return result.Errors.Count == 0;
        }

        private static List<DcsClickSpec> BuildCleanXmlClickSequence(DcsClickProfile clickProfile)
        {
            return new List<DcsClickSpec>
            {
                clickProfile.GearSymbol.Clone(),
                clickProfile.OptionsControls.Clone(),
                clickProfile.OptionsControlsClearAll.Clone(),
                clickProfile.OptionsControlsClearAllCheckAll.Clone(),
                clickProfile.OptionsControlsClearAllYes.Clone()
            };
        }

        private static List<DcsClickSpec> BuildIdXmlClickSequence(DcsClickProfile clickProfile)
        {
            return new List<DcsClickSpec>
            {
                clickProfile.GearSymbol.Clone(),
                clickProfile.OptionsControls.Clone(),
                clickProfile.OptionsControlsPlaneDropDown.Clone(),
                clickProfile.OptionsControlsMakeHTML.Clone()
            };
        }

        private static DcsResolvedClick ResolveXmlClick(DcsClickSpec click, DcsWindowRect windowRect, double guiScale)
        {
            Point point = ResolveXmlClickPoint(click, windowRect, guiScale);
            DcsResolvedClick resolved = new DcsResolvedClick();
            resolved.Name = click.Name;
            resolved.Method = "xml";
            resolved.Anchor = click.Anchor.ToString();
            resolved.OffsetX = click.OffsetX;
            resolved.OffsetY = click.OffsetY;
            resolved.TimeoutMs = click.TimeoutMs;
            resolved.Found = true;
            resolved.X = point.X;
            resolved.Y = point.Y;
            return resolved;
        }

        private static Point ResolveXmlClickPoint(DcsClickSpec click, DcsWindowRect windowRect, double guiScale)
        {
            int xOffset = click.OffsetX;
            int yOffset = click.OffsetY;
            if (windowRect.Width >= 2558)
            {
                xOffset = Convert.ToInt32(xOffset * guiScale);
                yOffset = Convert.ToInt32(yOffset * guiScale);
            }

            int width = windowRect.Width;
            int height = windowRect.Height;
            int x;
            int y;

            switch (click.Anchor)
            {
                case DcsClickAnchor.TOP_LEFT:
                    x = windowRect.Left + xOffset;
                    y = (windowRect.Top > 20) ? windowRect.Top + yOffset + 30 : windowRect.Top + yOffset;
                    break;
                case DcsClickAnchor.TOP_CENTER:
                    x = windowRect.Left + xOffset + (width / 2);
                    y = windowRect.Top + yOffset;
                    break;
                case DcsClickAnchor.TOP_RIGHT:
                    x = windowRect.Right + xOffset;
                    y = windowRect.Top + yOffset;
                    break;
                case DcsClickAnchor.CENTER_LEFT:
                    x = windowRect.Left + xOffset;
                    y = windowRect.Top + yOffset + (height / 2);
                    break;
                case DcsClickAnchor.CENTER_CENTER:
                    x = windowRect.Left + xOffset + (width / 2);
                    y = windowRect.Top + yOffset + (height / 2);
                    break;
                case DcsClickAnchor.CENTER_RIGHT:
                    x = windowRect.Right + xOffset;
                    y = windowRect.Top + yOffset + (height / 2);
                    break;
                case DcsClickAnchor.BOTTOM_LEFT:
                    x = windowRect.Left + xOffset;
                    y = windowRect.Bottom + yOffset;
                    break;
                case DcsClickAnchor.BOTTOM_CENTER:
                    x = windowRect.Left + xOffset + (width / 2);
                    y = windowRect.Bottom + yOffset;
                    break;
                case DcsClickAnchor.BOTTOM_RIGHT:
                    x = windowRect.Right + xOffset;
                    y = windowRect.Bottom + yOffset;
                    break;
                default:
                    x = windowRect.Left + xOffset;
                    y = windowRect.Top + yOffset;
                    break;
            }

            return new Point(x, y);
        }

        private static bool TryResolveActionWindowRect(DcsRefreshOptions options, Process dcsProcess, DcsRefreshResult result, out DcsWindowRect windowRect)
        {
            windowRect = null;
            if (options.WindowOverride != null)
            {
                result.UsedWindowOverride = true;
                windowRect = options.WindowOverride.Clone();
                return true;
            }

            if (dcsProcess != null && TryGetProcessWindowRect(dcsProcess, out DcsWindowRect fromProcess))
            {
                result.UsedRunningDcsProcess = true;
                windowRect = fromProcess;
                return true;
            }

            result.Errors.Add("Could not resolve DCS window rectangle for XML click operations.");
            return false;
        }

        private static bool TryGetProcessWindowRect(Process process, out DcsWindowRect windowRect)
        {
            windowRect = null;
            if (process == null)
                return false;

            try
            {
                process.Refresh();
                if (process.HasExited)
                    return false;
                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    return false;
                if (!GetWindowRect(handle, out NativeRect nativeRect))
                    return false;

                windowRect = new DcsWindowRect
                {
                    Left = nativeRect.Left,
                    Top = nativeRect.Top,
                    Right = nativeRect.Right,
                    Bottom = nativeRect.Bottom
                };

                return windowRect.Width > 0 && windowRect.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool MouseLeftClick(int x, int y, List<string> warnings)
        {
            GuardInput("click", x, y, true);
            try
            {
                lastSyntheticCursor = new Point(x, y);
                if (!SetCursorPos(x, y))
                    warnings.Add("SetCursorPos failed for " + x + "," + y + ".");
                mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                SleepWithClamp(25);
                mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add("Mouse click failed at " + x + "," + y + ": " + ex.Message);
                return false;
            }
        }

        private static void PressArrowUp()
        {
            GuardInput("arrow up", 0, 0, false);
            keybd_event(VK_UP, 0, 0, UIntPtr.Zero);
            keybd_event(VK_UP, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void PressArrowDown()
        {
            GuardInput("arrow down", 0, 0, false);
            keybd_event(VK_DOWN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_DOWN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // "Make HTML" opens the export in the browser. Only that tab is closed (Ctrl+W), and only when a browser is
        // the active window; Alt+F4 used to close whatever window was in front, including the whole browser.
        private static void CloseCurrentWindow()
        {
            SleepWithClamp(1500);
            string foreground = ForegroundProcessName();
            if (guardedDcsProcess != null && ForegroundProcessId() == guardedDcsProcess.Id)
                return;
            if (!BrowserProcesses.Any(b => string.Equals(b, foreground, StringComparison.OrdinalIgnoreCase)))
                throw new DcsInputGuardException("After 'Make HTML' '" + foreground + "' is the active window instead of DCS or a browser; the refresh stopped so no keys go to it.");

            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            SleepWithClamp(25);
            keybd_event(VK_W, 0, 0, UIntPtr.Zero);
            keybd_event(VK_W, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SleepWithClamp(25);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SleepWithClamp(int milliseconds)
        {
            if (milliseconds <= 0)
                return;
            Thread.Sleep(milliseconds);
        }

        private static void StopProcessSafe(Process process, List<string> warnings)
        {
            if (process == null)
                return;

            try
            {
                process.Refresh();
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception ex)
            {
                warnings.Add("Failed to stop DCS process: " + ex.Message);
            }
        }

        private static int CopyDirectoryTree(string source, string destination, bool overwrite, List<string> errors)
        {
            try
            {
                if (!Directory.Exists(source))
                    return 0;

                if (Directory.Exists(destination))
                    Directory.Delete(destination, true);
                Directory.CreateDirectory(destination);

                string[] directories = Directory.GetDirectories(source, "*", SearchOption.AllDirectories);
                for (int i = 0; i < directories.Length; i++)
                {
                    string directory = directories[i];
                    string relative = directory.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    Directory.CreateDirectory(Path.Combine(destination, relative));
                }

                string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];
                    string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string target = Path.Combine(destination, relative);
                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(parent))
                        Directory.CreateDirectory(parent);
                    File.Copy(file, target, overwrite);
                }

                return files.Length;
            }
            catch (Exception ex)
            {
                errors.Add("Failed copying directory '" + source + "' to '" + destination + "': " + ex.Message);
                return -1;
            }
        }

        private static void SafeDeleteDirectory(string path, List<string> warnings, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                string message = "Failed deleting directory '" + path + "': " + ex.Message;
                if (errors != null)
                    errors.Add(message);
                else if (warnings != null)
                    warnings.Add(message);
            }
        }

        // Written before the user's Input folder is removed for a clean refresh and deleted after it was restored. If
        // the engine is killed in between, the next refresh (or "dcs recover") puts the backup back first.
        private static string RestoreMarkerPath(string savedGamesPath)
        {
            return Path.Combine(savedGamesPath, "Config", "JoyPro_refresh_restore.txt");
        }

        private static void WriteRestoreMarker(DcsRefreshResult result)
        {
            File.WriteAllLines(RestoreMarkerPath(result.SavedGamesPath), new[] { result.InputPath, result.InputBackupPath, DateTime.Now.ToString("o", CultureInfo.InvariantCulture) });
        }

        public static void RecoverInterruptedRefresh(string savedGamesPath, List<string> warnings, List<string> errors)
        {
            string marker = RestoreMarkerPath(savedGamesPath);
            if (string.IsNullOrWhiteSpace(savedGamesPath) || !File.Exists(marker))
                return;

            string[] lines = File.ReadAllLines(marker);
            if (lines.Length < 2 || !Directory.Exists(lines[1]))
            {
                warnings.Add("Found an old refresh marker without a backup; removed it: " + marker);
                File.Delete(marker);
                return;
            }

            string inputPath = lines[0];
            string backupPath = lines[1];
            if (Directory.Exists(inputPath))
            {
                string moved = inputPath + ".beforeRestore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                Directory.Move(inputPath, moved);
                warnings.Add("Kept the Input folder of the interrupted refresh as " + moved + ".");
            }
            if (CopyDirectoryTree(backupPath, inputPath, true, errors) < 0)
            {
                errors.Add("An earlier DCS refresh was interrupted and your Input folder could not be restored automatically. Copy '" + backupPath + "' back to '" + inputPath + "'.");
                return;
            }
            File.Delete(marker);
            warnings.Add("An earlier DCS refresh was interrupted; your DCS Input folder was restored from " + backupPath + ".");
        }

        private static void RestoreInputBackup(DcsRefreshResult result, List<string> errors)
        {
            string backupRoot = Path.Combine(result.SavedGamesPath, "Config", "CLInput");
            int errorsBefore = errors.Count;

            if (!TryDeleteDirectoryWithRetries(result.InputPath, 8, 250, out Exception deleteInputError))
            {
                errors.Add("Failed clearing generated DCS input folder before restore: " + deleteInputError.Message);
                return;
            }

            if (Directory.Exists(result.InputBackupPath))
            {
                for (int attempt = 1; attempt <= 8; attempt++)
                {
                    int errorCountBefore = errors.Count;
                    int restoredCount = CopyDirectoryTree(result.InputBackupPath, result.InputPath, true, errors);
                    if (restoredCount >= 0)
                    {
                        result.InputFilesRestored = restoredCount;
                        break;
                    }

                    bool transient = HasTransientRestoreErrors(errors, errorCountBefore);
                    if (transient && attempt < 8)
                    {
                        if (errors.Count > errorCountBefore)
                            errors.RemoveRange(errorCountBefore, errors.Count - errorCountBefore);
                        Thread.Sleep(250);
                        continue;
                    }

                    return;
                }
            }

            if (!TryDeleteDirectoryWithRetries(backupRoot, 8, 250, out Exception deleteBackupError))
            {
                errors.Add("Failed cleaning DCS input backup folder '" + backupRoot + "': " + deleteBackupError.Message);
            }

            string marker = RestoreMarkerPath(result.SavedGamesPath);
            if (errors.Count == errorsBefore && File.Exists(marker))
                File.Delete(marker);
        }

        private static bool TryDeleteDirectoryWithRetries(string path, int attempts, int delayMs, out Exception lastError)
        {
            lastError = null;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return true;

            int maxAttempts = Math.Max(1, attempts);
            int pause = Math.Max(0, delayMs);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                    lastError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt >= maxAttempts)
                        break;
                    Thread.Sleep(pause);
                }
            }

            return false;
        }

        private static bool HasTransientRestoreErrors(List<string> errors, int startIndex)
        {
            if (errors == null || errors.Count <= startIndex)
                return false;

            for (int i = startIndex; i < errors.Count; i++)
            {
                if (IsTransientIoMessage(errors[i]))
                    return true;
            }

            return false;
        }

        private static bool IsTransientIoMessage(string message)
        {
            string text = (message ?? string.Empty).ToLowerInvariant();
            if (text.Length == 0)
                return false;

            return text.Contains("being used by another process") ||
                   text.Contains("cannot access the file") ||
                   text.Contains("used by another process") ||
                   text.Contains("access to the path");
        }

        private static readonly Regex FakeDeviceDiff = new Regex(@"^(?<template>.*) \{FAKE-JOYSTICK-\d+\}\.diff\.lua$", RegexOptions.Compiled);

        // Turns the diffs DCS wrote for the fake sticks into JoyPro's clean profiles:
        // - CleanProfile\DCS\<plane>.cf: the generic stick (default.lua), used for every device type without its own;
        // - CleanProfile\DCS\<plane>\<template>.cf: a device type whose defaults differ in that plane, or that has a
        //   default file in the plane's own input folder (templatesByPlane);
        // - KeyboardCleanProfile\DCS\<plane>.cf: the keyboard.
        private static bool RecreateCleanProfiles(string savedGamesPath, string defaultStickReference, DcsRefreshResult result, List<string> errors, Dictionary<string, HashSet<string>> templatesByPlane = null)
        {
            string inputRoot = Path.Combine(savedGamesPath, "Config", "Input");
            if (!Directory.Exists(inputRoot))
            {
                errors.Add("DCS input folder does not exist after refresh: " + inputRoot);
                return false;
            }

            // Written only into the game-data folder (GameDataPaths): a JoyPro update or rebuild keeps them.
            string cleanRoot = GameDataPaths.WritableCleanProfileFolder("DCS", false);
            string keyboardRoot = GameDataPaths.WritableCleanProfileFolder("DCS", true);

            DirectoryInfo[] planeDirectories;
            try
            {
                planeDirectories = new DirectoryInfo(inputRoot).GetDirectories();
            }
            catch (Exception ex)
            {
                errors.Add("Failed listing DCS input planes in '" + inputRoot + "': " + ex.Message);
                return false;
            }

            int planeIndex = 0;
            foreach (DirectoryInfo planeDirectory in planeDirectories)
            {
                planeIndex++;
                if (planeDirectory == null)
                    continue;
                string planeName = planeDirectory.Name ?? string.Empty;
                if (planeName.Length == 0)
                    continue;
                EngineProgress.Report(planeIndex, planeDirectories.Length, "DCS World, writing the clean profiles", planeName);

                try
                {
                    string genericText = null;
                    Dictionary<string, string> deviceTexts = new Dictionary<string, string>(StringComparer.Ordinal);
                    FileInfo fallbackJoystickFile = null;
                    string joystickFolder = Path.Combine(planeDirectory.FullName, "joystick");
                    if (Directory.Exists(joystickFolder))
                    {
                        foreach (FileInfo file in new DirectoryInfo(joystickFolder).GetFiles("*.diff.lua"))
                        {
                            Match fake = FakeDeviceDiff.Match(file.Name);
                            if (fake.Success)
                            {
                                string template = fake.Groups["template"].Value;
                                string text = File.ReadAllText(file.FullName);
                                if (string.Equals(template, HeadlessGenericDevice, StringComparison.Ordinal))
                                    genericText = text;
                                else
                                    deviceTexts[template] = text;
                                continue;
                            }
                            if (file.Name.IndexOf("mfd", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;
                            if (fallbackJoystickFile == null || file.Length > fallbackJoystickFile.Length)
                                fallbackJoystickFile = file;
                        }

                        // Older flow (no generic fake stick): the reference stick, else the largest real-device diff.
                        if (genericText == null)
                        {
                            string preferred = string.IsNullOrWhiteSpace(defaultStickReference)
                                ? string.Empty
                                : Path.Combine(joystickFolder, defaultStickReference + ".diff.lua");
                            if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
                                genericText = File.ReadAllText(preferred);
                            else if (fallbackJoystickFile != null)
                                genericText = File.ReadAllText(fallbackJoystickFile.FullName);
                        }
                    }

                    string cleanTarget = Path.Combine(cleanRoot, planeName + ".cf");
                    File.WriteAllText(cleanTarget, genericText ?? string.Empty);
                    result.CleanFilesWritten++;

                    string deviceRoot = Path.Combine(cleanRoot, planeName);
                    if (Directory.Exists(deviceRoot))
                    {
                        foreach (string stale in Directory.GetFiles(deviceRoot, "*.cf"))
                            File.Delete(stale);
                    }
                    HashSet<string> ownFiles;
                    if (templatesByPlane == null || !templatesByPlane.TryGetValue(planeName, out ownFiles))
                        ownFiles = new HashSet<string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, string> device in deviceTexts.OrderBy(d => d.Key, StringComparer.Ordinal))
                    {
                        if (string.Equals(device.Value, genericText, StringComparison.Ordinal) && !ownFiles.Contains(device.Key))
                            continue;
                        Directory.CreateDirectory(deviceRoot);
                        File.WriteAllText(Path.Combine(deviceRoot, device.Key + ".cf"), device.Value);
                        result.DeviceCleanFilesWritten++;
                    }
                    if (Directory.Exists(deviceRoot) && Directory.GetFileSystemEntries(deviceRoot).Length == 0)
                        Directory.Delete(deviceRoot);

                    string keyboardSource = Path.Combine(planeDirectory.FullName, "Keyboard", "Keyboard.diff.lua");
                    if (File.Exists(keyboardSource))
                    {
                        string keyboardTarget = Path.Combine(keyboardRoot, planeName + ".cf");
                        File.Copy(keyboardSource, keyboardTarget, true);
                        result.KeyboardCleanFilesWritten++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Failed recreating clean profile for plane '" + planeName + "': " + ex.Message);
                }
            }

            return errors.Count == 0;
        }

        public const string CoveredDeviceTypesFile = "device-types.txt";

        // CleanProfile\DCS\device-types.txt: the device types the clean profiles were built for (one per line). A
        // type listed there without a CleanProfile\DCS\<plane>\<type>.cf has exactly the generic stick's defaults in
        // that plane. A refresh of some aircraft only adds to the list.
        private static void WriteCoveredDeviceTypes(DcsRefreshResult result, bool merge)
        {
            if (result == null || result.CoveredDeviceTypes.Count == 0)
                return;
            try
            {
                string path = Path.Combine(GameDataPaths.WritableCleanProfileFolder("DCS", false), CoveredDeviceTypesFile);
                SortedSet<string> types = new SortedSet<string>(result.CoveredDeviceTypes, StringComparer.Ordinal);
                if (merge)
                    foreach (string known in ReadCoveredDeviceTypes())
                        types.Add(known);
                StringBuilder text = new StringBuilder();
                text.AppendLine("# Device types (DCS template names) JoyPro's DCS clean profiles were built for: dcs refresh clean.");
                foreach (string type in types)
                    text.AppendLine("=" + type);
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                result.Steps.Add("Device types covered by the clean profiles: " + types.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not write the list of covered device types: " + ex.Message);
            }
        }

        // The covered device types (null-free; empty when the clean profiles predate the list). Each line is "=<type>"
        // so a type's spaces at the ends survive.
        internal static HashSet<string> ReadCoveredDeviceTypes()
        {
            HashSet<string> types = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string path = Path.Combine(GameDataPaths.CleanProfileFolder("DCS", false), CoveredDeviceTypesFile);
                if (!File.Exists(path))
                    return types;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    if (line.StartsWith("=", StringComparison.Ordinal) && line.Length > 1)
                        types.Add(line.Substring(1));
            }
            catch (Exception)
            {
            }
            return types;
        }

        // Per profile (unit) name: the device types that have a default file in that unit's own joystick folder.
        private static Dictionary<string, HashSet<string>> TemplatesByPlane(List<DcsHeadlessProfileCandidate> candidates)
        {
            Dictionary<string, HashSet<string>> map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null)
                return map;
            foreach (DcsHeadlessProfileCandidate candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.UnitName) || string.IsNullOrWhiteSpace(candidate.FolderPath))
                    continue;
                HashSet<string> set;
                if (!map.TryGetValue(candidate.UnitName, out set))
                    map[candidate.UnitName] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string template in CollectDeviceTemplates(null, new List<DcsHeadlessProfileCandidate> { candidate }))
                    set.Add(template);
            }
            return map;
        }

        private static int CountInstalledAircraft(string installPath, string savedGamesPath, int modulesToScan)
        {
            int count = Math.Max(0, modulesToScan);
            string installAircraft = Path.Combine(installPath ?? string.Empty, "Mods", "aircraft");
            string instanceAircraft = Path.Combine(savedGamesPath ?? string.Empty, "Mods", "aircraft");

            if (Directory.Exists(installAircraft))
                count += new DirectoryInfo(installAircraft).GetDirectories().Length;
            if (Directory.Exists(instanceAircraft))
                count += new DirectoryInfo(instanceAircraft).GetDirectories().Length;

            return count;
        }

        private sealed class DcsHeadlessProfileCandidate
        {
            public string UnitName { get; set; }
            public string FolderPath { get; set; }
            public List<string> Aliases { get; set; }

            public DcsHeadlessProfileCandidate()
            {
                UnitName = string.Empty;
                FolderPath = string.Empty;
                Aliases = new List<string>();
            }
        }

        private sealed class DcsEntryInputProfileMapping
        {
            public string UnitName { get; set; }
            public string FolderPath { get; set; }
        }

        private static readonly Regex EntryInputProfileLineRegex =
            new Regex("\\[\\s*[\"'](?<unit>[^\"']+)[\"']\\s*\\]\\s*=\\s*(?<expr>[^,]+)", RegexOptions.Compiled);

        // Matches "/Input", "/Input/" and "/Input/<sub>" (an empty sub path means the module Input root).
        private static readonly Regex EntryInputPathRegex =
            new Regex("[/\\\\]Input(?:[/\\\\](?<sub>[^'\\\"]*))?['\\\"]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> DcsInputDeviceFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "joystick", "keyboard", "mouse", "trackir", "headtracker"
        };

        private static bool RunHeadlessLuaRefresh(
            DcsRefreshOptions options,
            DcsRefreshResult result,
            List<DcsHeadlessProfileCandidate> candidates,
            bool exportHtml,
            string savedGamesOverride = null,
            Action<string, int, int, string> luaProgress = null)
        {
            string modeLabel = exportHtml ? "ID" : "clean";
            ReportStatus(
                options,
                "Headless Lua " +
                modeLabel +
                " refresh started for " +
                (candidates == null ? 0 : candidates.Count).ToString(CultureInfo.InvariantCulture) +
                " profile candidate(s).");
            string luaePath = ResolveLuaExecutable(options);
            if (string.IsNullOrWhiteSpace(luaePath) || !File.Exists(luaePath))
            {
                result.Errors.Add("luae.exe was not found for headless " + modeLabel + " refresh. Expected under DCS install bin folder.");
                return false;
            }

            string workingDirectory = NormalizePath(options.InstallPath);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                result.Errors.Add("DCS install path is missing for headless " + modeLabel + " refresh.");
                return false;
            }

            List<DcsHeadlessProfileCandidate> selectedCandidates = candidates ?? new List<DcsHeadlessProfileCandidate>();
            if (selectedCandidates.Count == 0)
            {
                result.Errors.Add("No DCS profile candidates were selected for headless " + modeLabel + " refresh.");
                return false;
            }

            if (exportHtml)
            {
                SafeDeleteDirectory(result.InputLayoutsPath, result.Warnings, result.Errors);
                if (result.Errors.Count > 0)
                    return false;
            }

            List<string> deviceTemplates = null;
            if (!exportHtml)
            {
                deviceTemplates = CollectDeviceTemplates(options.InstallPath, selectedCandidates);
                // The user's own devices, also those not connected now: every device DCS wrote a file for.
                foreach (string template in CollectSavedGamesDeviceTemplates(options.SavedGamesPath))
                    if (!deviceTemplates.Contains(template))
                        deviceTemplates.Add(template);
            }
            if (deviceTemplates != null)
                result.Steps.Add("Device types with their own DCS default files: " + deviceTemplates.Count.ToString(CultureInfo.InvariantCulture) + ".");
            string scriptContent = BuildHeadlessLuaRefreshScript(savedGamesOverride ?? options.SavedGamesPath, selectedCandidates, exportHtml, deviceTemplates);
            string scriptPath = Path.Combine(Path.GetTempPath(), "joypro_dcs_headless_refresh_" + (exportHtml ? "id_" : "clean_") + Guid.NewGuid().ToString("N") + ".lua");

            try
            {
                // Lua 5.1 opens files with the ANSI code page, so embedded paths must use it too.
                File.WriteAllText(scriptPath, scriptContent, Encoding.Default);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed writing headless Lua " + modeLabel + " refresh script: " + ex.Message);
                return false;
            }

            List<string> outputLines = new List<string>();
            List<string> errorLines = new List<string>();
            object outputLock = new object();

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(luaePath)
                {
                    Arguments = "\"" + scriptPath + "\"",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default,
                    CreateNoWindow = true
                };

                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args != null && args.Data != null)
                        {
                            // "JOYPRO_PROGRESS|<i>|<n>|<stage>|<profile>": the profile luae works on now.
                            if (args.Data.StartsWith("JOYPRO_PROGRESS|", StringComparison.Ordinal))
                            {
                                string[] progress = args.Data.Split(new[] { '|' }, 5);
                                if (luaProgress != null && progress.Length >= 5 &&
                                    int.TryParse(progress[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
                                    int.TryParse(progress[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                                    luaProgress(progress[3], index, count, progress[4]);
                                return;
                            }
                            lock (outputLock)
                                outputLines.Add(args.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args != null && args.Data != null)
                        {
                            lock (outputLock)
                                errorLines.Add(args.Data);
                        }
                    };

                    try
                    {
                        process.Start();
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add("Failed launching luae.exe for headless " + modeLabel + " refresh: " + ex.Message);
                        return false;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    ReportStatus(options, "Headless Lua process launched. Waiting for completion.");

                    const int timeoutMs = 15 * 60 * 1000;
                    if (!WaitForExitOrCancel(process, timeoutMs, exportHtml ? "reading the DCS aircraft inputs" : "building the DCS clean profiles"))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // ignore kill exceptions
                        }

                        result.Errors.Add("Headless Lua " + modeLabel + " refresh timed out after 900 seconds.");
                        return false;
                    }

                    process.WaitForExit();

                    int profileSucceeded = 0;
                    int profileFailed = 0;
                    int unclassifiedOutputLines = 0;
                    bool sawDoneMarker = false;

                    lock (outputLock)
                    {
                        for (int i = 0; i < outputLines.Count; i++)
                        {
                            string line = outputLines[i] ?? string.Empty;
                            if (line.StartsWith("JOYPRO_PROFILE_OK|", StringComparison.Ordinal))
                            {
                                profileSucceeded++;
                                continue;
                            }
                            if (line.StartsWith("JOYPRO_PROFILE_ERR|", StringComparison.Ordinal))
                            {
                                profileFailed++;
                                result.Warnings.Add("[headless-lua] " + line.Substring("JOYPRO_PROFILE_ERR|".Length));
                                continue;
                            }
                            if (line.StartsWith("JOYPRO_TEMPLATE|", StringComparison.Ordinal))
                            {
                                string template = line.Substring("JOYPRO_TEMPLATE|".Length);
                                if (!result.CoveredDeviceTypes.Contains(template))
                                    result.CoveredDeviceTypes.Add(template);
                                continue;
                            }
                            if (line.StartsWith("JOYPRO_DEVICES|", StringComparison.Ordinal))
                            {
                                result.Steps.Add("Headless Lua device count: " + line.Substring("JOYPRO_DEVICES|".Length));
                                continue;
                            }
                            if (line.StartsWith("JOYPRO_DONE|", StringComparison.Ordinal))
                            {
                                sawDoneMarker = true;
                                result.Steps.Add("Headless Lua " + modeLabel + " summary: " + line.Substring("JOYPRO_DONE|".Length));
                                continue;
                            }
                            if (line.StartsWith("JOYPRO_FATAL|", StringComparison.Ordinal))
                            {
                                result.Errors.Add("Headless Lua fatal error: " + line.Substring("JOYPRO_FATAL|".Length));
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(line) && unclassifiedOutputLines < 20)
                            {
                                unclassifiedOutputLines++;
                                result.Warnings.Add("[headless-lua] " + line);
                            }
                        }

                        for (int i = 0; i < errorLines.Count; i++)
                        {
                            string err = errorLines[i];
                            if (!string.IsNullOrWhiteSpace(err))
                                result.Warnings.Add("[headless-lua stderr] " + err);
                        }
                    }

                    result.PlanesScanned = Math.Max(result.PlanesScanned, profileSucceeded);
                    result.ModulesToScanResolved = Math.Max(result.ModulesToScanResolved, profileSucceeded);
                    if (exportHtml)
                    {
                        result.Steps.Add("Headless Lua exported HTML for " + profileSucceeded.ToString(CultureInfo.InvariantCulture) + " profile(s).");
                    }
                    else
                    {
                        result.Steps.Add("Headless Lua cleared/saved assignments for " + profileSucceeded.ToString(CultureInfo.InvariantCulture) + " profile(s).");
                    }

                    if (!sawDoneMarker)
                        result.Warnings.Add("Headless Lua run did not emit completion marker.");

                    if (process.ExitCode != 0 && result.Errors.Count == 0)
                        result.Errors.Add("Headless Lua " + modeLabel + " refresh failed with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");

                    if (profileSucceeded <= 0 && result.Errors.Count == 0)
                    {
                        if (exportHtml)
                            result.Errors.Add("Headless Lua refresh did not export any profile HTML.");
                        else
                            result.Errors.Add("Headless Lua clean refresh did not clear/save any profile assignments.");
                    }

                    if (profileFailed > 0)
                        result.Warnings.Add("Headless Lua " + modeLabel + " refresh reported " + profileFailed.ToString(CultureInfo.InvariantCulture) + " profile error(s).");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(scriptPath))
                        File.Delete(scriptPath);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Failed deleting temporary headless " + modeLabel + " refresh script: " + ex.Message);
                }
            }

            ReportStatus(options, "Headless Lua " + modeLabel + " refresh completed.");
            return result.Errors.Count == 0;
        }

        // Waits for a luae.exe run; false on timeout. When the user cancels (EngineProgress), the run is stopped and the
        // command ends with EngineCancelledException: luae only writes into temporary or export folders, never the DB.
        private static bool WaitForExitOrCancel(Process process, int timeoutMs, string what)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!process.WaitForExit(250))
            {
                if (EngineProgress.CancelRequested)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch
                    {
                        // ignore kill exceptions
                    }
                    EngineProgress.ThrowIfCancelled(what);
                }
                if (clock.ElapsedMilliseconds >= timeoutMs)
                    return false;
            }
            return true;
        }

        private static string ResolveLuaExecutable(DcsRefreshOptions options)
        {
            if (options == null)
                return string.Empty;

            string explicitPath = NormalizePath(options.DcsExecutablePath);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string fileName = Path.GetFileName(explicitPath);
                if (string.Equals(fileName, "luae.exe", StringComparison.OrdinalIgnoreCase))
                    return explicitPath;

                string parent = Path.GetDirectoryName(explicitPath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    string sibling = Path.Combine(parent, "luae.exe");
                    if (File.Exists(sibling))
                        return sibling;
                }
            }

            string installPath = NormalizePath(options.InstallPath);
            if (string.IsNullOrWhiteSpace(installPath))
                return string.Empty;

            return Path.Combine(installPath, "bin", "luae.exe");
        }

        // Mirrors DCS.getInputProfiles(): every module declares its input profiles in entry.lua via
        // declare_plugin(..., { InputProfiles = { [unitName] = folder } }). The unit name is what DCS
        // uses for InputLayoutsTxt\<unit> and Config\Input\<unit>, so it must never be guessed.
        private static List<DcsHeadlessProfileCandidate> DiscoverHeadlessProfileCandidates(DcsRefreshOptions options, DcsRefreshResult refreshResult)
        {
            List<string> warnings = refreshResult.Warnings;
            List<DcsHeadlessProfileCandidate> result = new List<DcsHeadlessProfileCandidate>();
            HashSet<string> seenUnitFolder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<string> moduleDirectories = EnumerateDcsModuleDirectories(options.InstallPath, options.SavedGamesPath, warnings);
            List<string> entryModules = moduleDirectories
                .Where(d => File.Exists(Path.Combine(d, "entry.lua")))
                .ToList();

            Dictionary<string, List<DcsEntryInputProfileMapping>> probed = null;
            if (entryModules.Count > 0)
            {
                string luaePath = ResolveLuaExecutable(options);
                if (!string.IsNullOrWhiteSpace(luaePath) && File.Exists(luaePath))
                    probed = ProbeEntryInputProfilesWithLua(luaePath, options.InstallPath, entryModules, warnings);
                else
                    warnings.Add("luae.exe was not found; DCS input profile names are read from entry.lua text and may be incomplete.");
            }

            int fromLua = 0;
            int fromText = 0;
            int fromFolders = 0;
            for (int i = 0; i < moduleDirectories.Count; i++)
            {
                string moduleDirectory = moduleDirectories[i];
                string entryFile = Path.Combine(moduleDirectory, "entry.lua");
                if (!File.Exists(entryFile))
                {
                    fromFolders += CollectHeadlessProfileCandidatesFromInputFolders(moduleDirectory, seenUnitFolder, result, warnings);
                    continue;
                }

                List<DcsEntryInputProfileMapping> mappings = null;
                if (probed != null)
                    probed.TryGetValue(NormalizeDirectoryKey(moduleDirectory), out mappings);

                bool usedLua = mappings != null && mappings.Count > 0;
                if (!usedLua)
                {
                    if (!TryExtractInputProfilesFromEntryLua(entryFile, moduleDirectory, out mappings, warnings))
                        continue;
                    if (probed != null)
                        warnings.Add("DCS module '" + Path.GetFileName(moduleDirectory) + "': InputProfiles were read from entry.lua text because Lua evaluation returned none.");
                }

                for (int j = 0; j < mappings.Count; j++)
                {
                    DcsEntryInputProfileMapping mapping = mappings[j];
                    if (mapping == null || string.IsNullOrWhiteSpace(mapping.UnitName) || !LooksLikeInputProfileDirectory(mapping.FolderPath))
                        continue;

                    if (AddHeadlessProfileCandidate(mapping.UnitName, mapping.FolderPath, seenUnitFolder, result))
                    {
                        if (usedLua)
                            fromLua++;
                        else
                            fromText++;
                    }
                }
            }

            refreshResult.Steps.Add(
                "DCS input profiles found: " + fromLua.ToString(CultureInfo.InvariantCulture) + " via Lua, " +
                fromText.ToString(CultureInfo.InvariantCulture) + " via entry.lua text, " +
                fromFolders.ToString(CultureInfo.InvariantCulture) + " via folder names (modules without entry.lua).");

            return DeduplicateHeadlessCandidatesByUnit(result, warnings);
        }

        private static List<string> EnumerateDcsModuleDirectories(string installPath, string savedGamesPath, List<string> warnings)
        {
            List<string> roots = new List<string>
            {
                Path.Combine(installPath ?? string.Empty, "Mods", "aircraft"),
                Path.Combine(installPath ?? string.Empty, "Mods", "tech"),
                Path.Combine(savedGamesPath ?? string.Empty, "Mods", "aircraft"),
                Path.Combine(savedGamesPath ?? string.Empty, "Mods", "tech")
            };

            List<string> modules = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                try
                {
                    foreach (DirectoryInfo moduleDirectory in new DirectoryInfo(root).GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (seen.Add(NormalizeDirectoryKey(moduleDirectory.FullName)))
                            modules.Add(moduleDirectory.FullName);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("Failed enumerating DCS module root '" + root + "': " + ex.Message);
                }
            }

            return modules;
        }

        private static string NormalizeDirectoryKey(string path)
        {
            string normalized = NormalizePath(path);
            if (normalized.Length > 3)
                normalized = normalized.TrimEnd('\\', '/');
            return normalized;
        }

        private static Dictionary<string, List<DcsEntryInputProfileMapping>> ProbeEntryInputProfilesWithLua(
            string luaePath,
            string installPath,
            List<string> moduleDirectories,
            List<string> warnings)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "joypro_dcs_profile_probe_" + Guid.NewGuid().ToString("N") + ".lua");
            try
            {
                // Lua 5.1 opens files with the ANSI code page, so the script (and its embedded paths) must use it too.
                File.WriteAllText(scriptPath, BuildEntryInputProfileProbeScript(moduleDirectories), Encoding.Default);

                ProcessStartInfo startInfo = new ProcessStartInfo(luaePath)
                {
                    Arguments = "\"" + scriptPath + "\"",
                    WorkingDirectory = Directory.Exists(installPath ?? string.Empty) ? installPath : Path.GetDirectoryName(luaePath),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default,
                    CreateNoWindow = true
                };

                List<string> outputLines = new List<string>();
                object outputLock = new object();
                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args != null && args.Data != null)
                        {
                            // "JOYPRO_PROBE_PROGRESS|<module>|<i>|<n>": one line per evaluated entry.lua.
                            if (args.Data.StartsWith("JOYPRO_PROBE_PROGRESS|", StringComparison.Ordinal))
                            {
                                string[] progress = args.Data.Split('|');
                                if (progress.Length >= 4 &&
                                    int.TryParse(progress[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
                                    int.TryParse(progress[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                                    EngineProgress.Report(index, count, "DCS World, reading aircraft modules", Path.GetFileName(progress[1].TrimEnd('\\', '/')));
                                return;
                            }
                            lock (outputLock)
                                outputLines.Add(args.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, args) => { };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!WaitForExitOrCancel(process, 120 * 1000, "reading the DCS aircraft modules"))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // ignore kill exceptions
                        }
                        warnings.Add("DCS input profile probe (luae.exe) timed out; falling back to entry.lua text parsing.");
                        return null;
                    }
                    process.WaitForExit();
                }

                Dictionary<string, List<DcsEntryInputProfileMapping>> result =
                    new Dictionary<string, List<DcsEntryInputProfileMapping>>(StringComparer.OrdinalIgnoreCase);
                bool done = false;
                lock (outputLock)
                {
                    foreach (string line in outputLines)
                    {
                        string[] parts = (line ?? string.Empty).Split(new[] { '|' }, 4);
                        if (parts.Length == 0)
                            continue;

                        if (parts[0] == "JOYPRO_PROBE_DONE")
                        {
                            done = true;
                            continue;
                        }

                        if (parts.Length < 3)
                            continue;

                        string moduleKey = NormalizeDirectoryKey(parts[1]);
                        if (!result.TryGetValue(moduleKey, out List<DcsEntryInputProfileMapping> mappings))
                        {
                            mappings = new List<DcsEntryInputProfileMapping>();
                            result[moduleKey] = mappings;
                        }

                        if (parts[0] == "JOYPRO_INPUTPROFILE" && parts.Length == 4)
                        {
                            mappings.Add(new DcsEntryInputProfileMapping
                            {
                                UnitName = parts[2].Trim(),
                                FolderPath = NormalizeDirectoryKey(parts[3])
                            });
                        }
                        else if (parts[0] == "JOYPRO_ENTRY_ERR")
                        {
                            warnings.Add("DCS module '" + Path.GetFileName(moduleKey) + "': entry.lua could not be evaluated: " + parts[2]);
                        }
                    }
                }

                if (!done)
                {
                    warnings.Add("DCS input profile probe (luae.exe) did not finish; falling back to entry.lua text parsing.");
                    return null;
                }

                return result;
            }
            catch (EngineCancelledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add("DCS input profile probe (luae.exe) failed: " + ex.Message + ". Falling back to entry.lua text parsing.");
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(scriptPath))
                        File.Delete(scriptPath);
                }
                catch
                {
                    // ignore temp cleanup failures
                }
            }
        }

        // Runs each entry.lua in a sandbox where declare_plugin() captures the plugin table and every unknown
        // global resolves to an inert dummy value. Only InputProfiles is read back.
        private static string BuildEntryInputProfileProbeScript(List<string> moduleDirectories)
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("-- JoyPro DCS input profile probe");
            script.AppendLine("local modules = {");
            for (int i = 0; i < moduleDirectories.Count; i++)
                script.AppendLine("  [==[" + moduleDirectories[i] + "]==],");
            script.AppendLine("}");
            script.AppendLine(@"local DUMMY = {}
local dummyMeta = {}
local function dummy_value() return DUMMY end
local function zero() return 0 end
dummyMeta.__index = dummy_value
dummyMeta.__newindex = function() end
dummyMeta.__call = dummy_value
dummyMeta.__concat = function(a, b)
  local function s(v) if v == DUMMY then return '' end return tostring(v) end
  return s(a) .. s(b)
end
dummyMeta.__add = zero
dummyMeta.__sub = zero
dummyMeta.__mul = zero
dummyMeta.__div = zero
dummyMeta.__unm = zero
dummyMeta.__len = zero
dummyMeta.__tostring = function() return '' end
setmetatable(DUMMY, dummyMeta)

local SAFE = {
  string = string, table = table, math = math,
  pairs = pairs, ipairs = ipairs, next = next, type = type, select = select, unpack = unpack,
  tostring = tostring, tonumber = tonumber, pcall = pcall, error = error, assert = assert,
  setmetatable = setmetatable, getmetatable = getmetatable, rawget = rawget, rawset = rawset,
  _ = function(s) return s end,
}

local function clean(s)
  return (tostring(s or ''):gsub('[|\r\n]', ' '))
end

local function probe(modPath)
  local entryPath = modPath .. '\\entry.lua'
  local plugins = {}
  local env = {}
  setmetatable(env, { __index = function(_, k)
    local v = SAFE[k]
    if v ~= nil then return v end
    return DUMMY
  end })
  env._G = env
  env.current_mod_path = modPath
  env.declare_plugin = function(id, t)
    table.insert(plugins, t)
    return DUMMY
  end
  env.require = dummy_value
  env.print = function() end
  local function sandbox_load(path)
    local f = loadfile(path)
    if not f then return nil end
    setfenv(f, env)
    return f
  end
  env.loadfile = sandbox_load
  env.dofile = function(path)
    local f = sandbox_load(path)
    if not f then return DUMMY end
    local ok, r = pcall(f)
    if ok then return r end
    return DUMMY
  end

  local f, err = loadfile(entryPath)
  if not f then
    print('JOYPRO_ENTRY_ERR|' .. modPath .. '|' .. clean(err))
    return
  end
  setfenv(f, env)
  pcall(f)

  local tables = {}
  for i = 1, #plugins do
    local t = plugins[i]
    if type(t) == 'table' and type(rawget(t, 'InputProfiles')) == 'table' then
      table.insert(tables, rawget(t, 'InputProfiles'))
    end
  end
  if #tables == 0 and type(rawget(env, 'InputProfiles')) == 'table' then
    table.insert(tables, rawget(env, 'InputProfiles'))
  end

  print('JOYPRO_MODULE|' .. modPath .. '|' .. tostring(#tables))
  for i = 1, #tables do
    for key, value in pairs(tables[i]) do
      local path = value
      if type(value) == 'table' then path = rawget(value, 'path') or rawget(value, 1) end
      if type(key) == 'string' and type(path) == 'string' then
        print('JOYPRO_INPUTPROFILE|' .. modPath .. '|' .. clean(key) .. '|' .. clean(path))
      end
    end
  end
end

for i = 1, #modules do
  local ok, err = pcall(probe, modules[i])
  if not ok then
    print('JOYPRO_ENTRY_ERR|' .. modules[i] .. '|' .. clean(err))
  end
  -- A redirected stdout is fully buffered: flush per module so the engine can report progress live.
  print('JOYPRO_PROBE_PROGRESS|' .. modules[i] .. '|' .. tostring(i) .. '|' .. tostring(#modules))
  if io and io.stdout then pcall(function() io.stdout:flush() end) end
end
print('JOYPRO_PROBE_DONE|' .. tostring(#modules))");
            return script.ToString();
        }

        // Two modules can declare the same unit (e.g. legacy "F-5E" and "F-5E 2024" both declare F-5E-3).
        // DCS keeps one of them, so export only one; prefer the folder with the most recently updated files.
        private static List<DcsHeadlessProfileCandidate> DeduplicateHeadlessCandidatesByUnit(
            List<DcsHeadlessProfileCandidate> candidates,
            List<string> warnings)
        {
            List<DcsHeadlessProfileCandidate> result = new List<DcsHeadlessProfileCandidate>();
            foreach (IGrouping<string, DcsHeadlessProfileCandidate> group in candidates.GroupBy(c => c.UnitName, StringComparer.OrdinalIgnoreCase))
            {
                List<DcsHeadlessProfileCandidate> items = group.ToList();
                if (items.Count == 1)
                {
                    result.Add(items[0]);
                    continue;
                }

                DcsHeadlessProfileCandidate chosen = items
                    .OrderByDescending(c => GetNewestLuaWriteTimeUtc(c.FolderPath))
                    .First();
                result.Add(chosen);
                warnings.Add(
                    "DCS input profile '" + group.Key + "' is declared by " + items.Count.ToString(CultureInfo.InvariantCulture) +
                    " modules; using '" + chosen.FolderPath + "'.");
            }

            return result;
        }

        private static DateTime GetNewestLuaWriteTimeUtc(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder, "*.lua", SearchOption.AllDirectories)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool TryDiscoverAndFilterHeadlessProfileCandidates(
            DcsRefreshOptions options,
            DcsRefreshResult result,
            out List<DcsHeadlessProfileCandidate> filteredCandidates)
        {
            filteredCandidates = DiscoverHeadlessProfileCandidates(options, result);
            result.Steps.Add("Headless Lua profile candidates discovered: " + filteredCandidates.Count.ToString(CultureInfo.InvariantCulture));

            if (!TryApplyHeadlessPlaneFilter(options, result, filteredCandidates, out filteredCandidates))
                return false;

            result.MatchedPlanes = filteredCandidates
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.UnitName))
                .Select(c => c.UnitName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (options.PlaneFilter != null && options.PlaneFilter.Count > 0)
            {
                result.Steps.Add("Headless Lua profile candidates after plane filter: " + filteredCandidates.Count.ToString(CultureInfo.InvariantCulture));
                if (filteredCandidates.Count == 0)
                {
                    result.Errors.Add("Plane filter was provided, but no DCS headless profile candidates matched.");
                    return false;
                }
            }

            return true;
        }

        private static bool TryApplyHeadlessPlaneFilter(
            DcsRefreshOptions options,
            DcsRefreshResult result,
            List<DcsHeadlessProfileCandidate> source,
            out List<DcsHeadlessProfileCandidate> filtered)
        {
            filtered = source ?? new List<DcsHeadlessProfileCandidate>();
            List<string> requestedFilters = NormalizePlaneFilters(options?.PlaneFilter);
            if (requestedFilters.Count == 0)
                return true;

            if (filtered.Count == 0)
            {
                result.Errors.Add("No DCS headless profile candidates were found, so --plane/--planes cannot be resolved.");
                return false;
            }

            List<DcsHeadlessProfileCandidate> resolved = new List<DcsHeadlessProfileCandidate>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < requestedFilters.Count; i++)
            {
                string token = requestedFilters[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                List<DcsHeadlessProfileCandidate> exactMatches = filtered
                    .Where(candidate => HeadlessPlaneCandidateMatchesToken(candidate, token))
                    .ToList();

                DcsHeadlessProfileCandidate selected;
                if (exactMatches.Count > 0)
                {
                    selected = exactMatches.FirstOrDefault(c => string.Equals(c.UnitName, token, StringComparison.OrdinalIgnoreCase)) ??
                               ChooseBestHeadlessPlaneCandidate(token, exactMatches);
                }
                else
                {
                    // A wrong unit name writes a wrongly named DB file, so never auto-pick a fuzzy match.
                    DcsHeadlessProfileCandidate suggestion = ChooseBestHeadlessPlaneCandidate(token, filtered);
                    bool accepted = suggestion != null &&
                                    options != null &&
                                    options.Confirm &&
                                    ConfirmHeadlessPlaneSuggestion("Plane '" + token + "' is not a DCS input profile", suggestion);
                    if (!accepted)
                    {
                        result.Warnings.Add(
                            "Plane '" + token + "' is not a DCS input profile and was skipped" +
                            (suggestion == null ? "." : " (closest profile: '" + DescribeHeadlessPlaneCandidate(suggestion) + "')."));
                        continue;
                    }

                    selected = suggestion;
                }

                string key = BuildHeadlessPlaneCandidateKey(selected);
                if (seen.Add(key))
                    resolved.Add(selected);
            }

            if (resolved.Count == 0)
            {
                result.Errors.Add("None of the requested planes (" + string.Join(", ", requestedFilters) + ") is a DCS input profile.");
                return false;
            }

            filtered = resolved;
            return true;
        }

        private static bool HeadlessPlaneCandidateMatchesToken(DcsHeadlessProfileCandidate candidate, string token)
        {
            if (candidate == null)
                return false;

            string requested = (token ?? string.Empty).Trim();
            if (requested.Length == 0)
                return false;

            foreach (string alias in EnumerateHeadlessPlaneCandidateAliases(candidate))
            {
                if (HeadlessPlaneTokensEqual(alias, requested))
                    return true;
            }

            return false;
        }

        private static IEnumerable<string> EnumerateHeadlessPlaneCandidateAliases(DcsHeadlessProfileCandidate candidate)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (candidate == null)
                yield break;

            string unitName = (candidate.UnitName ?? string.Empty).Trim();
            if (unitName.Length > 0 && seen.Add(unitName))
                yield return unitName;

            string folderName = Path.GetFileName((candidate.FolderPath ?? string.Empty).Trim()) ?? string.Empty;
            if (folderName.Length > 0 && seen.Add(folderName))
                yield return folderName;

            List<string> aliases = candidate.Aliases ?? new List<string>();
            for (int i = 0; i < aliases.Count; i++)
            {
                string alias = (aliases[i] ?? string.Empty).Trim();
                if (alias.Length == 0)
                    continue;
                if (seen.Add(alias))
                    yield return alias;
            }
        }

        private static void AddHeadlessPlaneAlias(List<string> aliases, string value)
        {
            if (aliases == null)
                return;

            string alias = (value ?? string.Empty).Trim();
            if (alias.Length == 0)
                return;
            if (!aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                aliases.Add(alias);
        }

        private static bool HeadlessPlaneTokensEqual(string left, string right)
        {
            string a = (left ?? string.Empty).Trim();
            string b = (right ?? string.Empty).Trim();
            if (a.Length == 0 || b.Length == 0)
                return false;

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;

            string normalizedA = NormalizePlaneTokenForSimilarity(a);
            string normalizedB = NormalizePlaneTokenForSimilarity(b);
            if (normalizedA.Length == 0 || normalizedB.Length == 0)
                return false;

            return string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase);
        }

        private static DcsHeadlessProfileCandidate ChooseBestHeadlessPlaneCandidate(
            string requestedToken,
            IEnumerable<DcsHeadlessProfileCandidate> candidates)
        {
            if (candidates == null)
                return null;

            return candidates
                .Where(c => c != null)
                .OrderByDescending(c => ScoreHeadlessPlaneCandidate(requestedToken, c))
                .ThenBy(c => c.UnitName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int ScoreHeadlessPlaneCandidate(string requestedToken, DcsHeadlessProfileCandidate candidate)
        {
            if (candidate == null)
                return int.MinValue;

            int best = int.MinValue;
            foreach (string alias in EnumerateHeadlessPlaneCandidateAliases(candidate))
            {
                int score = HeadlessPlaneSimilarityScore(requestedToken, alias);
                if (score > best)
                    best = score;
            }

            return best;
        }

        private static int HeadlessPlaneSimilarityScore(string requestedToken, string candidateToken)
        {
            string requested = NormalizePlaneTokenForSimilarity(requestedToken);
            string candidate = NormalizePlaneTokenForSimilarity(candidateToken);
            if (requested.Length == 0 || candidate.Length == 0)
                return int.MinValue;

            int score = 0;
            if (string.Equals(requested, candidate, StringComparison.OrdinalIgnoreCase))
                score += 100000;

            if (candidate.Contains(requested) || requested.Contains(candidate))
                score += 15000 - Math.Abs(candidate.Length - requested.Length) * 5;

            if (ContainsOrderedSubsequence(candidate, requested))
                score += 22000 - Math.Max(0, candidate.Length - requested.Length) * 40;
            if (ContainsOrderedSubsequence(requested, candidate))
                score += 6000 - Math.Max(0, requested.Length - candidate.Length) * 20;

            int distance = LevenshteinDistance(requested, candidate);
            score += Math.Max(0, 15000 - (distance * 300));
            return score;
        }

        private static string NormalizePlaneTokenForSimilarity(string value)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;

            char[] filtered = raw
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();
            if (filtered.Length == 0)
                return raw.ToUpperInvariant();
            return new string(filtered);
        }

        private static bool ContainsOrderedSubsequence(string container, string requested)
        {
            string source = container ?? string.Empty;
            string query = requested ?? string.Empty;
            if (query.Length == 0)
                return true;
            if (source.Length == 0 || source.Length < query.Length)
                return false;

            int queryIndex = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == query[queryIndex])
                {
                    queryIndex++;
                    if (queryIndex >= query.Length)
                        return true;
                }
            }

            return false;
        }

        private static bool ConfirmHeadlessPlaneSuggestion(
            string prefix,
            DcsHeadlessProfileCandidate candidate)
        {
            string candidateText = DescribeHeadlessPlaneCandidate(candidate);
            if (!Environment.UserInteractive || Console.IsInputRedirected)
                return false;

            Console.Error.Write(prefix + ". Use '" + candidateText + "'? [y/N]: ");
            string answer = Console.ReadLine();
            return IsAffirmativeAnswer(answer);
        }

        private static string DescribeHeadlessPlaneCandidate(DcsHeadlessProfileCandidate candidate)
        {
            if (candidate == null)
                return "<none>";

            string unit = (candidate.UnitName ?? string.Empty).Trim();
            string folderName = Path.GetFileName((candidate.FolderPath ?? string.Empty).Trim()) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(unit))
                unit = folderName;
            if (string.IsNullOrWhiteSpace(folderName))
                return unit;

            if (string.Equals(unit, folderName, StringComparison.OrdinalIgnoreCase))
                return unit;

            return unit + " (Input/" + folderName + ")";
        }

        private static string BuildHeadlessPlaneCandidateKey(DcsHeadlessProfileCandidate candidate)
        {
            if (candidate == null)
                return string.Empty;
            return ((candidate.UnitName ?? string.Empty).Trim() + "|" + NormalizePath(candidate.FolderPath)).Trim();
        }

        // Fallback for modules without entry.lua (loose mods, test fixtures): use Input sub folders that
        // look like real profiles. Device folders (joystick, keyboard, ...) are never profiles themselves;
        // when they sit directly under Input, the Input root is the profile and the module folder names it.
        private static int CollectHeadlessProfileCandidatesFromInputFolders(
            string moduleDirectory,
            HashSet<string> seenUnitFolder,
            List<DcsHeadlessProfileCandidate> output,
            List<string> warnings)
        {
            string inputRoot = Path.Combine(moduleDirectory, "Input");
            if (!Directory.Exists(inputRoot))
                return 0;

            DirectoryInfo[] profileDirectories;
            try
            {
                profileDirectories = new DirectoryInfo(inputRoot).GetDirectories();
            }
            catch (Exception ex)
            {
                warnings.Add("Failed enumerating DCS input directory '" + inputRoot + "': " + ex.Message);
                return 0;
            }

            int added = 0;
            bool inputRootIsProfile = false;
            for (int i = 0; i < profileDirectories.Length; i++)
            {
                DirectoryInfo profileDirectory = profileDirectories[i];
                if (DcsInputDeviceFolderNames.Contains(profileDirectory.Name))
                {
                    inputRootIsProfile = true;
                    continue;
                }

                if (!LooksLikeInputProfileDirectory(profileDirectory.FullName))
                    continue;

                if (AddHeadlessProfileCandidate(profileDirectory.Name, profileDirectory.FullName, seenUnitFolder, output))
                    added++;
            }

            if (inputRootIsProfile &&
                LooksLikeInputProfileDirectory(inputRoot) &&
                AddHeadlessProfileCandidate(Path.GetFileName(moduleDirectory), inputRoot, seenUnitFolder, output))
            {
                added++;
            }

            return added;
        }

        private static bool AddHeadlessProfileCandidate(
            string unitName,
            string folderPath,
            HashSet<string> seenUnitFolder,
            List<DcsHeadlessProfileCandidate> output)
        {
            string normalizedUnit = (unitName ?? string.Empty).Trim();
            string normalizedFolder = NormalizeDirectoryKey(folderPath);
            if (string.IsNullOrWhiteSpace(normalizedFolder) || string.IsNullOrWhiteSpace(normalizedUnit))
                return false;
            if (DcsInputDeviceFolderNames.Contains(normalizedUnit))
                return false;

            string composite = normalizedUnit + "|" + normalizedFolder;
            if (!seenUnitFolder.Add(composite))
                return false;

            List<string> candidateAliases = new List<string>();
            AddHeadlessPlaneAlias(candidateAliases, normalizedUnit);
            AddHeadlessPlaneAlias(candidateAliases, Path.GetFileName(normalizedFolder));

            output.Add(new DcsHeadlessProfileCandidate
            {
                UnitName = normalizedUnit,
                FolderPath = normalizedFolder,
                Aliases = candidateAliases
            });
            return true;
        }

        private static bool TryExtractInputProfilesFromEntryLua(
            string entryPath,
            string moduleDirectory,
            out List<DcsEntryInputProfileMapping> mappings,
            List<string> warnings)
        {
            mappings = new List<DcsEntryInputProfileMapping>();
            if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
                return false;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(entryPath);
            }
            catch (Exception ex)
            {
                warnings.Add("Failed reading entry.lua '" + entryPath + "': " + ex.Message);
                return false;
            }

            bool inInputProfiles = false;
            bool tableOpened = false;
            int braceDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripLuaLineComment(lines[i]);

                if (!inInputProfiles)
                {
                    if (line.IndexOf("InputProfiles", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.IndexOf("=", StringComparison.Ordinal) >= 0)
                    {
                        inInputProfiles = true;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (!tableOpened && line.IndexOf("{", StringComparison.Ordinal) >= 0)
                    tableOpened = true;

                if (tableOpened)
                {
                    TryParseEntryInputProfileLine(line, moduleDirectory, mappings);
                    braceDepth += CountCharacter(line, '{');
                    braceDepth -= CountCharacter(line, '}');
                    if (braceDepth <= 0)
                    {
                        inInputProfiles = false;
                        tableOpened = false;
                        braceDepth = 0;
                    }
                }
            }

            return mappings.Count > 0;
        }

        private static string StripLuaLineComment(string line)
        {
            string value = line ?? string.Empty;
            char quote = '\0';
            for (int i = 0; i < value.Length - 1; i++)
            {
                char c = value[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                        i++;
                    else if (c == quote)
                        quote = '\0';
                    continue;
                }

                if (c == '"' || c == '\'')
                    quote = c;
                else if (c == '-' && value[i + 1] == '-')
                    return value.Substring(0, i);
            }

            return value;
        }

        private static void TryParseEntryInputProfileLine(string line, string moduleDirectory, List<DcsEntryInputProfileMapping> mappings)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            Match profileMatch = EntryInputProfileLineRegex.Match(line);
            if (!profileMatch.Success)
                return;

            string unitName = (profileMatch.Groups["unit"].Value ?? string.Empty).Trim();
            string expr = profileMatch.Groups["expr"].Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(unitName) || string.IsNullOrWhiteSpace(expr))
                return;

            Match inputPathMatch = EntryInputPathRegex.Match(expr);
            if (!inputPathMatch.Success)
                return;

            string inputSubPath = (inputPathMatch.Groups["sub"].Value ?? string.Empty).Trim().Trim('/', '\\');
            string folder = inputSubPath.Length == 0
                ? Path.Combine(moduleDirectory, "Input")
                : Path.Combine(moduleDirectory, "Input", inputSubPath.Replace('/', Path.DirectorySeparatorChar));

            mappings.Add(new DcsEntryInputProfileMapping
            {
                UnitName = unitName,
                FolderPath = NormalizeDirectoryKey(folder)
            });
        }

        private static int CountCharacter(string value, char needle)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == needle)
                    count++;
            }
            return count;
        }

        private static bool LooksLikeInputProfileDirectory(string profileDirectory)
        {
            if (string.IsNullOrWhiteSpace(profileDirectory) || !Directory.Exists(profileDirectory))
                return false;

            try
            {
                if (Directory.EnumerateFiles(profileDirectory, "*.lua", SearchOption.AllDirectories).Any())
                    return true;
            }
            catch
            {
                return false;
            }

            return false;
        }

        internal const string HeadlessGenericDevice = "JoyPro Headless Device";

        // Device type names (DCS template names) that have their own default file in some aircraft's joystick input
        // folder: "<template>.lua" or the template diff "<template>.diff.lua". default.lua is the generic file.
        private static List<string> CollectDeviceTemplates(string installPath, List<DcsHeadlessProfileCandidate> candidates)
        {
            List<string> folders = new List<string>();
            if (candidates != null)
                folders.AddRange(candidates.Where(c => c != null && !string.IsNullOrWhiteSpace(c.FolderPath)).Select(c => Path.Combine(c.FolderPath, "joystick")));
            string aircrafts = Path.Combine(installPath ?? string.Empty, "Config", "Input", "Aircrafts");
            try
            {
                if (Directory.Exists(aircrafts))
                    folders.AddRange(Directory.GetDirectories(aircrafts).Select(d => Path.Combine(d, "joystick")));
            }
            catch (Exception)
            {
            }

            SortedSet<string> templates = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string[] files;
                try
                {
                    if (!Directory.Exists(folder))
                        continue;
                    files = Directory.GetFiles(folder, "*.lua");
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - ".diff.lua".Length);
                    else
                        name = name.Substring(0, name.Length - ".lua".Length);
                    if (name.Length == 0 || string.Equals(name, "default", StringComparison.OrdinalIgnoreCase) || name.Trim().Length == 0)
                        continue;
                    templates.Add(name);
                }
            }
            return templates.ToList();
        }

        // Device types of the joystick files in the user's Saved Games\DCS\Config\Input (read only): DCS writes a
        // "<device> {GUID}.diff.lua" per device it saw, so these are the user's devices, connected or not.
        internal static List<string> CollectSavedGamesDeviceTemplates(string savedGamesPath)
        {
            SortedSet<string> templates = new SortedSet<string>(StringComparer.Ordinal);
            string inputRoot = string.IsNullOrWhiteSpace(savedGamesPath) ? null : Path.Combine(savedGamesPath, "Config", "Input");
            if (inputRoot == null || !Directory.Exists(inputRoot))
                return templates.ToList();
            try
            {
                foreach (string profile in Directory.GetDirectories(inputRoot))
                {
                    string joystick = Path.Combine(profile, "joystick");
                    if (!Directory.Exists(joystick))
                        continue;
                    foreach (string file in Directory.GetFiles(joystick, "*.diff.lua"))
                    {
                        string name = Path.GetFileName(file);
                        name = name.Substring(0, name.Length - ".diff.lua".Length);
                        if (name.IndexOf("{FAKE-JOYSTICK-", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        string template = DcsDeviceNames.TemplateName(name);
                        if (!string.IsNullOrEmpty(template) && template.Trim().Length > 0 && !string.Equals(template, name, StringComparison.Ordinal))
                            templates.Add(template);
                    }
                }
            }
            catch (Exception)
            {
            }
            return templates.ToList();
        }

        private static string BuildHeadlessLuaRefreshScript(
            string savedGamesPath,
            List<DcsHeadlessProfileCandidate> candidates,
            bool exportHtml,
            List<string> deviceTemplates = null)
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("-- JoyPro headless DCS " + (exportHtml ? "ID" : "clean") + " refresh");
            AppendHeadlessPrelude(script, savedGamesPath, candidates, deviceTemplates, exportHtml);
            return AppendHeadlessRefreshBody(script, exportHtml);
        }

        // The shared prelude without aircraft profiles (for scripts that only use DCS's input loader).
        internal static void AppendHeadlessPreludeWithoutProfiles(StringBuilder script, string savedGamesPath)
        {
            AppendHeadlessPrelude(script, savedGamesPath, new List<DcsHeadlessProfileCandidate>(), null, false);
        }

        // The part every headless luae script shares: paths, DCS's own Input modules, the keyboard patch, the
        // stripped-file redirect (a file the "strip DCS default binds" option patched is read from its
        // .nocombo.bak, so JoyPro always sees DCS's real defaults) and the list of input profiles ("merged").
        // It opens "local ok, fatal = pcall(function()"; the caller writes the body and closes it.
        private static void AppendHeadlessPrelude(
            StringBuilder script,
            string savedGamesPath,
            List<DcsHeadlessProfileCandidate> candidates,
            List<string> deviceTemplates,
            bool requireMakeLayout)
        {
            bool exportHtml = requireMakeLayout;
            string savedGames = savedGamesPath ?? string.Empty;
            savedGames = savedGames.Replace('\\', '/');
            if (!savedGames.EndsWith("/", StringComparison.Ordinal))
                savedGames += "/";

            script.AppendLine("local savedGamesPath = '" + EscapeLuaString(savedGames) + "'");
            script.AppendLine("local extraProfiles = {");
            if (candidates != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    DcsHeadlessProfileCandidate candidate = candidates[i];
                    if (candidate == null)
                        continue;

                    string unitName = EscapeLuaString((candidate.UnitName ?? string.Empty).Replace('\\', '/'));
                    string folderPath = EscapeLuaString((candidate.FolderPath ?? string.Empty).Replace('\\', '/'));
                    script.AppendLine("  { unit = '" + unitName + "', folder = '" + folderPath + "' },");
                }
            }
            script.AppendLine("}");
            script.AppendLine("local deviceTemplates = {");
            if (deviceTemplates != null)
                foreach (string template in deviceTemplates)
                    script.AppendLine("  '" + EscapeLuaString(template) + "',");
            script.AppendLine("}");
            // Progress per profile ("JOYPRO_PROGRESS|i|n|stage|name"), flushed: a redirected stdout is fully buffered.
            script.AppendLine("local function jp_progress(i, n, stage, name)");
            script.AppendLine("  print('JOYPRO_PROGRESS|' .. tostring(i) .. '|' .. tostring(n) .. '|' .. stage .. '|' .. (tostring(name or ''):gsub('[|\\r\\n]', ' ')))");
            script.AppendLine("  if io and io.stdout then pcall(function() io.stdout:flush() end) end");
            script.AppendLine("end");
            script.AppendLine("local cwd = io.popen('cd'):read('*l') or '.'");
            script.AppendLine("local function to_slash(p) return (p or ''):gsub('\\\\\\\\','/') end");
            script.AppendLine("local function abs_norm(path)");
            script.AppendLine("  path = to_slash(path)");
            script.AppendLine("  local prefix = ''");
            script.AppendLine("  if path:match('^%a:/') then");
            script.AppendLine("    prefix = path:sub(1,3)");
            script.AppendLine("    path = path:sub(4)");
            script.AppendLine("  else");
            script.AppendLine("    local c = to_slash(cwd)");
            script.AppendLine("    if not c:match('/$') then c = c .. '/' end");
            script.AppendLine("    prefix = c:sub(1,3)");
            script.AppendLine("    path = c:sub(4) .. path");
            script.AppendLine("  end");
            script.AppendLine("  local parts = {}");
            script.AppendLine("  for part in path:gmatch('[^/]+') do");
            script.AppendLine("    if part == '..' then");
            script.AppendLine("      if #parts > 0 then table.remove(parts) end");
            script.AppendLine("    elseif part ~= '.' and part ~= '' then");
            script.AppendLine("      table.insert(parts, part)");
            script.AppendLine("    end");
            script.AppendLine("  end");
            script.AppendLine("  return prefix .. table.concat(parts, '/')");
            script.AppendLine("end");
            script.AppendLine("package.preload['i_18n'] = function()");
            script.AppendLine("  local f, err = package.loadlib('edCore.dll', 'luaopen_i_18n')");
            script.AppendLine("  if not f then error('cannot load i_18n: ' .. tostring(err)) end");
            script.AppendLine("  return f()");
            script.AppendLine("end");
            script.AppendLine("package.preload['log'] = function()");
            script.AppendLine("  return {");
            script.AppendLine("    error = function(...) end,");
            script.AppendLine("    warning = function(...) end,");
            script.AppendLine("    info = function(...) end,");
            script.AppendLine("    debug = function(...) end,");
            script.AppendLine("  }");
            script.AppendLine("end");
            script.AppendLine("package.preload['me_utilities'] = function()");
            script.AppendLine("  local function deep_copy(dst, src, seen)");
            script.AppendLine("    seen = seen or {}");
            script.AppendLine("    if type(src) ~= 'table' then return src end");
            script.AppendLine("    if seen[src] then return seen[src] end");
            script.AppendLine("    dst = dst or {}");
            script.AppendLine("    seen[src] = dst");
            script.AppendLine("    for k, v in pairs(src) do");
            script.AppendLine("      local kk = (type(k) == 'table') and deep_copy(nil, k, seen) or k");
            script.AppendLine("      dst[kk] = deep_copy(nil, v, seen)");
            script.AppendLine("    end");
            script.AppendLine("    return dst");
            script.AppendLine("  end");
            script.AppendLine("  return {");
            script.AppendLine("    copyTable = function(dst, src)");
            script.AppendLine("      if src == nil then return dst end");
            script.AppendLine("      return deep_copy(dst, src, {})");
            script.AppendLine("    end");
            script.AppendLine("  }");
            script.AppendLine("end");
            script.AppendLine("package.preload['lfs'] = function()");
            script.AppendLine("  local M = {}");
            script.AppendLine("  function M.writedir() return savedGamesPath end");
            script.AppendLine("  function M.realpath(p) return abs_norm(p) end");
            script.AppendLine("  function M.mkdir(p)");
            script.AppendLine("    os.execute(string.format('mkdir \"%s\" >nul 2>nul', to_slash(p):gsub('/', '\\\\\\\\')))");
            script.AppendLine("    return true");
            script.AppendLine("  end");
            script.AppendLine("  function M.attributes(p)");
            script.AppendLine("    local f = io.open(p, 'rb')");
            script.AppendLine("    if f then f:close(); return { mode = 'file' } end");
            script.AppendLine("    local ok, _, code = os.rename(p, p)");
            script.AppendLine("    if ok or code == 13 then return { mode = 'directory' } end");
            script.AppendLine("    return nil");
            script.AppendLine("  end");
            script.AppendLine("  return M");
            script.AppendLine("end");
            script.AppendLine("if not _G._ then _G._ = function(s) return s end end");
            script.AppendLine("package.path = table.concat({");
            script.AppendLine("  './Scripts/?.lua',");
            script.AppendLine("  './Scripts/?/?.lua',");
            script.AppendLine("  './Scripts/?/?/?.lua',");
            script.AppendLine("  package.path,");
            script.AppendLine("}, ';')");
            script.AppendLine("local rawLoadfile = loadfile");
            script.AppendLine("loadfile = function(name, ...)");
            script.AppendLine("  if type(name) == 'string' then");
            script.AppendLine("    local f = io.open(name, 'rb')");
            script.AppendLine("    if f then");
            script.AppendLine("      local head = f:read(4096) or ''");
            script.AppendLine("      f:close()");
            script.AppendLine("      if string.find(head, '" + EngineDcsDefaultsStrip.MarkerToken + "', 1, true) then");
            script.AppendLine("        local b = io.open(name .. '" + EngineDcsDefaultsStrip.BackupSuffix + "', 'rb')");
            script.AppendLine("        if b then b:close(); return rawLoadfile(name .. '" + EngineDcsDefaultsStrip.BackupSuffix + "', ...) end");
            script.AppendLine("      end");
            script.AppendLine("    end");
            script.AppendLine("  end");
            script.AppendLine("  return rawLoadfile(name, ...)");
            script.AppendLine("end");
            script.AppendLine("local ok, fatal = pcall(function()");
            script.AppendLine("  local Input = require('Input')");
            script.AppendLine("  Input.initialize()");
            // luae.exe has no window, so DCS never enumerates the keyboard. Register it by name so keyboard
            // defaults are loaded (ID export) and keyboard diffs are written (clean export).
            script.AppendLine("  local keyboardName = Input.getKeyboardDeviceName and Input.getKeyboardDeviceName() or 'Keyboard'");
            script.AppendLine("  local rawGetDevices = Input.getDevices");
            script.AppendLine("  local rawGetDeviceTypeName = Input.getDeviceTypeName");
            script.AppendLine("  Input.getDevices = function()");
            script.AppendLine("    local list = rawGetDevices() or {}");
            script.AppendLine("    for i = 1, #list do if list[i] == keyboardName then return list end end");
            script.AppendLine("    table.insert(list, 1, keyboardName)");
            script.AppendLine("    return list");
            script.AppendLine("  end");
            script.AppendLine("  Input.getDeviceTypeName = function(name)");
            script.AppendLine("    if name == keyboardName then return Input.getKeyboardDeviceTypeName() end");
            script.AppendLine("    return rawGetDeviceTypeName(name)");
            script.AppendLine("  end");
            script.AppendLine("  local InputData = require('Input.Data')");
            script.AppendLine("  local ProfileDatabase = require('Input.ProfileDatabase')");
            script.AppendLine("  local InputUtils = require('Input.Utils')");
            if (exportHtml)
                script.AppendLine("  local MakeLayoutText = require('Input.MakeLayoutText')");
            script.AppendLine("  local userConfigPath = savedGamesPath .. 'Config/Input/'");
            script.AppendLine("  local sysConfigPath = './Config/Input/'");
            script.AppendLine("  InputData.initialize(userConfigPath, sysConfigPath)");
            script.AppendLine("  InputData.enablePrintToLog(false)");
            script.AppendLine("  local merged = {}");
            script.AppendLine("  local seen = {}");
            script.AppendLine("  local function push_profile(info)");
            // Hidden profiles (GeoTerrain, JFT, Explorations, ...) never appear in the DCS controls dropdown.
            script.AppendLine("    if not info or info.visible == false then return end");
            script.AppendLine("    local key = tostring(info.name or '') .. '|' .. tostring(info.folder or '')");
            script.AppendLine("    if not seen[key] then");
            script.AppendLine("      seen[key] = true");
            script.AppendLine("      table.insert(merged, info)");
            script.AppendLine("    end");
            script.AppendLine("  end");
            script.AppendLine("  local defaults = ProfileDatabase.createDefaultProfilesSet(sysConfigPath)");
            script.AppendLine("  for i = 1, #defaults do push_profile(defaults[i]) end");
            script.AppendLine("  for i = 1, #extraProfiles do");
            script.AppendLine("    local p = extraProfiles[i]");
            script.AppendLine("    local okProfile, profileOrErr = pcall(ProfileDatabase.createUnitProfileInfo, p.unit, p.folder)");
            script.AppendLine("    if okProfile and profileOrErr then");
            script.AppendLine("      push_profile(profileOrErr)");
            script.AppendLine("    else");
            script.AppendLine("      print('JOYPRO_PROFILE_ERR|' .. tostring(p.unit) .. '|createUnitProfileInfo failed: ' .. tostring(profileOrErr))");
            script.AppendLine("    end");
            script.AppendLine("  end");
        }

        private static string AppendHeadlessRefreshBody(StringBuilder script, bool exportHtml)
        {
            if (exportHtml)
            {
                script.AppendLine("  local devices = InputUtils.getDevices() or {}");
                script.AppendLine("  if #devices == 0 then");
                script.AppendLine("    Input.addFakeJoystick('JoyPro Headless Device')");
                script.AppendLine("    devices = InputUtils.getDevices() or {}");
                script.AppendLine("  end");
            }
            else
            {
                // A generic stick (no default file of its own -> default.lua) and one fake stick per device type
                // whose defaults can differ from the generic stick's. DCS names a fake stick
                // "<name> {FAKE-JOYSTICK-nnnn}", so its template name is the type name and DCS loads that type's
                // defaults exactly as for the real device. The types:
                // - a default file of its own in some aircraft (deviceTemplates, from the install and the user's
                //   Saved Games device files);
                // - every type of Scripts/Input/DefaultAssignments.lua: default.lua asks
                //   defaultDeviceAssignmentFor('pitch'|'rudder'|'left_wheel_brake'|...), which answers per type
                //   (e.g. MFG Crosswind V2: wheel brakes on JOY_X/JOY_Y), so these devices differ from the generic
                //   stick even without a file of their own;
                // - every joystick connected now (luae.exe sees the PC's DirectInput devices).
                // Each type is printed (JOYPRO_TEMPLATE), so JoyPro knows which types the clean profiles cover.
                script.AppendLine("  Input.addFakeJoystick('" + HeadlessGenericDevice + "')");
                script.AppendLine("  local fakeSeen = {}");
                script.AppendLine("  local function add_fake(t)");
                script.AppendLine("    if type(t) ~= 'string' or t == '' or fakeSeen[t] or t == '" + HeadlessGenericDevice + "' then return end");
                script.AppendLine("    fakeSeen[t] = true");
                script.AppendLine("    if pcall(Input.addFakeJoystick, t) then print('JOYPRO_TEMPLATE|' .. (t:gsub('[|\\r\\n]', ' '))) end");
                script.AppendLine("  end");
                script.AppendLine("  for i = 1, #deviceTemplates do add_fake(deviceTemplates[i]) end");
                script.AppendLine("  local fda = rawLoadfile('./Scripts/Input/DefaultAssignments.lua')");
                script.AppendLine("  if fda then");
                script.AppendLine("    setfenv(fda, {})");
                script.AppendLine("    local okda, da = pcall(fda)");
                script.AppendLine("    if okda and type(da) == 'table' then");
                script.AppendLine("      local names = {}");
                script.AppendLine("      for k in pairs(da) do if type(k) == 'string' and k ~= 'default' and k ~= 'Keyboard' then table.insert(names, k) end end");
                script.AppendLine("      table.sort(names)");
                script.AppendLine("      for i = 1, #names do add_fake(names[i]) end");
                script.AppendLine("    end");
                script.AppendLine("  end");
                script.AppendLine("  for _, d in ipairs(rawGetDevices() or {}) do");
                script.AppendLine("    if d ~= keyboardName and rawGetDeviceTypeName(d) == 'joystick' and not string.find(d, '{FAKE-JOYSTICK-', 1, true) then add_fake(InputUtils.getDeviceTemplateName(d)) end");
                script.AppendLine("  end");
                script.AppendLine("  local devices = InputUtils.getDevices() or {}");
            }
            script.AppendLine("  local hasKeyboard = false");
            script.AppendLine("  for i = 1, #devices do");
            script.AppendLine("    if devices[i] == 'Keyboard' then hasKeyboard = true; break end");
            script.AppendLine("  end");
            script.AppendLine("  if not hasKeyboard then table.insert(devices, 'Keyboard') end");
            // Clean: only the fake sticks and the keyboard are cleared; the real devices' diffs are not used.
            script.AppendLine("  local clearDevices = {}");
            script.AppendLine("  for i = 1, #devices do");
            script.AppendLine("    local d = devices[i]");
            script.AppendLine("    if d == 'Keyboard' or string.find(d, '{FAKE-JOYSTICK-', 1, true) then table.insert(clearDevices, d) end");
            script.AppendLine("  end");
            script.AppendLine("  print('JOYPRO_DEVICES|' .. tostring(#devices))");
            script.AppendLine("  local success = 0");
            script.AppendLine("  local failed = 0");
            if (exportHtml)
            {
                script.AppendLine("  for i = 1, #merged do");
                script.AppendLine("    local info = merged[i]");
                script.AppendLine("    jp_progress(i, #merged, 'export', info and (info.name or info.unitName))");
                script.AppendLine("    local okCreate, createErr = pcall(InputData.createProfile, info)");
                script.AppendLine("    if not okCreate then");
                script.AppendLine("      failed = failed + 1");
                script.AppendLine("      print('JOYPRO_PROFILE_ERR|' .. tostring(info and (info.name or info.unitName) or '<unknown>') .. '|createProfile failed: ' .. tostring(createErr))");
                script.AppendLine("    else");
                script.AppendLine("      local okHtml, htmlOrErr = pcall(MakeLayoutText.makeHtml, info.name, devices)");
                script.AppendLine("      if okHtml and htmlOrErr then");
                script.AppendLine("        success = success + 1");
                script.AppendLine("        print('JOYPRO_PROFILE_OK|' .. tostring(info.name or '<unknown>') .. '|' .. tostring(htmlOrErr))");
                script.AppendLine("      else");
                script.AppendLine("        failed = failed + 1");
                script.AppendLine("        print('JOYPRO_PROFILE_ERR|' .. tostring(info.name or '<unknown>') .. '|makeHtml failed: ' .. tostring(htmlOrErr))");
                script.AppendLine("      end");
                script.AppendLine("    end");
                script.AppendLine("  end");
                script.AppendLine("  print('JOYPRO_DONE|' .. tostring(#merged) .. '|' .. tostring(success) .. '|' .. tostring(failed))");
            }
            else
            {
                script.AppendLine("  for i = 1, #merged do");
                script.AppendLine("    local info = merged[i]");
                script.AppendLine("    jp_progress(i, #merged, 'load', info and (info.name or info.unitName))");
                script.AppendLine("    local okCreate, createErr = pcall(InputData.createProfile, info)");
                script.AppendLine("    if not okCreate then");
                script.AppendLine("      failed = failed + 1");
                script.AppendLine("      print('JOYPRO_PROFILE_ERR|' .. tostring(info and (info.name or info.unitName) or '<unknown>') .. '|createProfile failed: ' .. tostring(createErr))");
                script.AppendLine("    end");
                script.AppendLine("  end");
                script.AppendLine("  local profileNames = InputData.getProfileNames() or {}");
                script.AppendLine("  local uiProfileName = InputData.getUiProfileName() or ''");
                script.AppendLine("  local clearTargets = {}");
                script.AppendLine("  for i = 1, #profileNames do");
                script.AppendLine("    if profileNames[i] ~= uiProfileName then");
                script.AppendLine("      table.insert(clearTargets, profileNames[i])");
                script.AppendLine("    end");
                script.AppendLine("  end");
                script.AppendLine("  for i = 1, #clearTargets do");
                script.AppendLine("    local profileName = clearTargets[i]");
                script.AppendLine("    jp_progress(i, #clearTargets, 'clear', profileName)");
                script.AppendLine("    local okClear, clearErr = pcall(InputData.clearProfile, profileName, clearDevices)");
                script.AppendLine("    if okClear then");
                script.AppendLine("      success = success + 1");
                script.AppendLine("      print('JOYPRO_PROFILE_OK|' .. tostring(profileName) .. '|cleared')");
                script.AppendLine("    else");
                script.AppendLine("      failed = failed + 1");
                script.AppendLine("      print('JOYPRO_PROFILE_ERR|' .. tostring(profileName) .. '|clearProfile failed: ' .. tostring(clearErr))");
                script.AppendLine("    end");
                script.AppendLine("  end");
                script.AppendLine("  jp_progress(#clearTargets, #clearTargets, 'save', '')");
                script.AppendLine("  local okSave, saveErr = pcall(InputData.saveChanges)");
                script.AppendLine("  if not okSave then");
                script.AppendLine("    error('saveChanges failed: ' .. tostring(saveErr))");
                script.AppendLine("  end");
                script.AppendLine("  pcall(InputData.unloadProfiles)");
                script.AppendLine("  print('JOYPRO_DONE|' .. tostring(#clearTargets) .. '|' .. tostring(success) .. '|' .. tostring(failed))");
            }
            script.AppendLine("end)");
            script.AppendLine("if not ok then");
            script.AppendLine("  print('JOYPRO_FATAL|' .. tostring(fatal))");
            script.AppendLine("  os.exit(1)");
            script.AppendLine("end");
            return script.ToString();
        }

        private static string EscapeLuaString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("'", "\\'");
        }

        // DCS's "Make HTML" page names the install folder (stylesheet link) and the device with its instance GUID (h1).
        // Neither belongs in JoyPro's DB: only the command table is read, so both are replaced by neutral text.
        internal static string ScrubLayoutHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html ?? string.Empty;
            string scrubbed = Regex.Replace(html, "<link\\s+href=\"[^\"]*\"", "<link href=\"layout.css\"", RegexOptions.IgnoreCase);
            return Regex.Replace(scrubbed, "<h1>[^<]*</h1>", "<h1>DCS input layout</h1>", RegexOptions.IgnoreCase);
        }

        private static bool CopyLargestExportedHtml(DcsRefreshOptions options, DcsRefreshResult result, List<string> errors)
        {
            string sourceRoot = result.InputLayoutsPath;
            if (!Directory.Exists(sourceRoot))
            {
                errors.Add("DCS HTML export folder not found: " + sourceRoot);
                return false;
            }

            // Written only into the game-data folder (GameDataPaths): a JoyPro update or rebuild keeps them.
            string targetRoot = GameDataPaths.WritableDbFolder("DCS");

            try
            {
                DirectoryInfo[] planeDirs = new DirectoryInfo(sourceRoot).GetDirectories();
                ReportStatus(options, "Scanning exported HTML folders: " + planeDirs.Length.ToString(CultureInfo.InvariantCulture) + " plane folder(s).");
                int planeIndex = 0;
                foreach (DirectoryInfo plane in planeDirs)
                {
                    planeIndex++;
                    if (plane == null)
                        continue;
                    EngineProgress.Report(planeIndex, planeDirs.Length, "DCS World, writing the command lists", plane.Name);
                    FileInfo[] files = plane.GetFiles("*.html");
                    FileInfo largest = null;
                    foreach (FileInfo file in files)
                    {
                        if (file == null)
                            continue;
                        string lowerName = file.Name.ToLowerInvariant();
                        if (lowerName == "keyboard.html" || lowerName == "mouse.html" || lowerName == "trackir.html")
                            continue;
                        if (largest == null || file.Length > largest.Length)
                            largest = file;
                    }

                    if (largest != null)
                    {
                        string targetFile = Path.Combine(targetRoot, plane.Name + ".html");
                        File.WriteAllText(targetFile, ScrubLayoutHtml(File.ReadAllText(largest.FullName, Encoding.UTF8)), new UTF8Encoding(false));
                        result.HtmlFilesCopied++;
                    }
                }
                ReportStatus(
                    options,
                    "Copied " +
                    result.HtmlFilesCopied.ToString(CultureInfo.InvariantCulture) +
                    " HTML file(s) to DCS DB.");
            }
            catch (Exception ex)
            {
                errors.Add("Failed processing DCS exported HTML files: " + ex.Message);
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(sourceRoot))
                        Directory.Delete(sourceRoot, true);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Failed deleting InputLayoutsTxt after ID refresh: " + ex.Message);
                }
            }

            return true;
        }

        private static bool TryFindTemplateOnCurrentScreen(string templatePath, out Rectangle matchRect, out Rectangle screenBounds, out string error)
        {
            matchRect = Rectangle.Empty;
            screenBounds = Rectangle.Empty;
            error = string.Empty;

            Bitmap screenshot = null;
            Bitmap template = null;
            try
            {
                if (!CaptureCurrentScreen(out screenshot, out screenBounds, out error))
                    return false;

                template = new Bitmap(templatePath);
                matchRect = FindTemplateExtremeContrast(screenshot, template);
                return true;
            }
            catch (Exception ex)
            {
                error = "OCR screen search failed for template '" + templatePath + "': " + ex.Message;
                return false;
            }
            finally
            {
                if (screenshot != null)
                    screenshot.Dispose();
                if (template != null)
                    template.Dispose();
            }
        }

        private static Rectangle FindTemplateExtremeContrast(Bitmap screen, Bitmap template)
        {
            if (screen == null || template == null)
                return Rectangle.Empty;
            if (template.Width <= 0 || template.Height <= 0)
                return Rectangle.Empty;
            if (template.Width > screen.Width || template.Height > screen.Height)
                return Rectangle.Empty;

            byte[] screenQuantized = QuantizeBitmap(screen, out int screenWidth, out int screenHeight);
            byte[] templateQuantized = QuantizeBitmap(template, out int templateWidth, out int templateHeight);

            int maxX = screenWidth - templateWidth;
            int maxY = screenHeight - templateHeight;

            for (int y = 0; y <= maxY; y++)
            {
                for (int x = 0; x <= maxX; x++)
                {
                    bool mismatch = false;
                    for (int ty = 0; ty < templateHeight && !mismatch; ty++)
                    {
                        int screenRow = (y + ty) * screenWidth + x;
                        int templateRow = ty * templateWidth;
                        for (int tx = 0; tx < templateWidth; tx++)
                        {
                            if (screenQuantized[screenRow + tx] != templateQuantized[templateRow + tx])
                            {
                                mismatch = true;
                                break;
                            }
                        }
                    }

                    if (!mismatch)
                        return new Rectangle(x, y, templateWidth, templateHeight);
                }
            }

            return Rectangle.Empty;
        }

        private static byte[] QuantizeBitmap(Bitmap source, out int width, out int height)
        {
            width = source.Width;
            height = source.Height;
            Rectangle rect = new Rectangle(0, 0, width, height);
            Bitmap converted = source.PixelFormat == PixelFormat.Format24bppRgb
                ? source
                : source.Clone(rect, PixelFormat.Format24bppRgb);

            BitmapData data = converted.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int byteCount = Math.Abs(data.Stride) * height;
                byte[] raw = new byte[byteCount];
                Marshal.Copy(data.Scan0, raw, 0, byteCount);

                byte[] quantized = new byte[width * height];
                int index = 0;
                for (int y = 0; y < height; y++)
                {
                    int row = y * data.Stride;
                    for (int x = 0; x < width; x++)
                    {
                        int pixel = row + x * 3;
                        byte blue = raw[pixel];
                        byte green = raw[pixel + 1];
                        byte red = raw[pixel + 2];
                        int luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                        quantized[index++] = luminance < 65
                            ? (byte)0
                            : luminance < 128
                                ? (byte)1
                                : luminance < 192
                                    ? (byte)2
                                    : (byte)3;
                    }
                }

                return quantized;
            }
            finally
            {
                converted.UnlockBits(data);
                if (!ReferenceEquals(converted, source))
                    converted.Dispose();
            }
        }

        private static bool CaptureCurrentScreen(out Bitmap screenshot, out Rectangle bounds, out string error)
        {
            screenshot = null;
            bounds = Rectangle.Empty;
            error = string.Empty;

            try
            {
                foreach (Screen screen in Screen.AllScreens)
                    bounds = Rectangle.Union(bounds, screen.Bounds);
                if (bounds == Rectangle.Empty)
                    bounds = Screen.PrimaryScreen.Bounds;

                screenshot = new Bitmap(bounds.Width, bounds.Height);
                using (Graphics graphics = Graphics.FromImage(screenshot))
                {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, screenshot.Size);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed capturing screen image: " + ex.Message;
                return false;
            }
        }

        private static bool TryWritePreviewImage(string previewImagePath, List<DcsResolvedClick> clicks, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(previewImagePath))
                return false;

            if (!CaptureCurrentScreen(out Bitmap screenshot, out Rectangle bounds, out string captureError))
            {
                warnings.Add(captureError);
                return false;
            }

            try
            {
                using (Graphics g = Graphics.FromImage(screenshot))
                using (Pen pointPen = new Pen(Color.Lime, 2))
                using (Pen rectPen = new Pen(Color.Gold, 2))
                using (Brush textBrush = new SolidBrush(Color.OrangeRed))
                using (Brush pointBrush = new SolidBrush(Color.Red))
                using (Font font = new Font("Consolas", 10, FontStyle.Bold))
                {
                    for (int i = 0; i < clicks.Count; i++)
                    {
                        DcsResolvedClick click = clicks[i];
                        if (click == null || !click.Found)
                            continue;

                        int drawX = click.X - bounds.Left;
                        int drawY = click.Y - bounds.Top;

                        g.FillEllipse(pointBrush, drawX - 5, drawY - 5, 10, 10);
                        g.DrawEllipse(pointPen, drawX - 10, drawY - 10, 20, 20);
                        string label = (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + click.Name;
                        g.DrawString(label, font, textBrush, drawX + 12, drawY - 8);

                        if (click.MatchWidth > 0 && click.MatchHeight > 0)
                        {
                            Rectangle matchRect = new Rectangle(
                                click.MatchLeft - bounds.Left,
                                click.MatchTop - bounds.Top,
                                click.MatchWidth,
                                click.MatchHeight);
                            g.DrawRectangle(rectPen, matchRect);
                        }
                    }
                }

                string parent = Path.GetDirectoryName(previewImagePath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
                screenshot.Save(previewImagePath, ImageFormat.Png);
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add("Failed writing preview image '" + previewImagePath + "': " + ex.Message);
                return false;
            }
            finally
            {
                screenshot.Dispose();
            }
        }

        private static string BuildDefaultPreviewImagePath()
        {
            string name = "joypro_dcs_click_preview_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png";
            return Path.Combine(Path.GetTempPath(), name);
        }

        private static Rectangle GetPrimaryScreenBounds()
        {
            try
            {
                if (Screen.PrimaryScreen != null)
                    return Screen.PrimaryScreen.Bounds;
                foreach (Screen screen in Screen.AllScreens)
                    return screen.Bounds;
            }
            catch
            {
                // ignore
            }

            return new Rectangle(0, 0, 1920, 1080);
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim().Trim('"');
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed));
            }
            catch
            {
                return trimmed;
            }
        }
    }
}
