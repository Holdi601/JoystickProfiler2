using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Tests.QualityGate;

// One view model on the Avalonia thread (as in the app: continuations come back to the UI thread), with the gate's data
// folder, a recording engine client and the real engine.
internal sealed class GateSession
{
    private GateSession(MainWindowViewModel vm, RecordingEngineClient engine, string profile)
    {
        Vm = vm;
        Engine = engine;
        Profile = profile;
    }

    public MainWindowViewModel Vm { get; }

    public RecordingEngineClient Engine { get; }

    public string Profile { get; }

    public bool HasProfile => Profile.Length > 0;

    private static bool windowWatch;

    // The first engine server start of this gate run was timed (cold start, its own budget).
    private static bool coldStartTimed;

    // Runs body with a fresh view model; profile: a copy to work on, or empty for none.
    public static void Run(string? profile, Func<GateSession, Task> body)
    {
        // The pages are laid out in roots that are never shown (GateRender, HiddenWindow). Should anything still open a
        // window while a check runs, it is closed at once and reported.
        if (!windowWatch)
        {
            windowWatch = true;
            AvaloniaRenderThread.Run(() => Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler(typeof(Avalonia.Controls.Window), (sender, _) =>
            {
                if (!Gate.Enabled || sender is not Avalonia.Controls.Window window)
                    return;
                window.Close();
                Gate.Fail("I10", "a window was opened while a check ran (closed at once)", string.Empty, window.GetType().Name + " '" + window.Title + "'");
            }));
        }
        string data = Gate.DataDir;
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        try
        {
            AvaloniaRenderThread.Run(() =>
            {
                RecordingEngineClient engine = new();
                MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = Gate.EnginePath,
                    ProfilePath = profile ?? string.Empty
                };
                GateSession session = new(vm, engine, profile ?? string.Empty);
                try
                {
                    // As the app does at its start (MainWindowViewModel): the engine server reads the game lists. Timed
                    // on its own; the checks start once it is ready. The first start of a gate run comes right after the
                    // build (cold disk cache, first JIT): it has its own budget, and the normal start is timed on a fresh
                    // server after it, as a normal app start sees it.
                    if (!coldStartTimed)
                    {
                        coldStartTimed = true;
                        AvaloniaRenderThread.Wait(session.MeasureAsync("engine server cold start", "first start after the build",
                            () => JoyPro.Gui.Services.EngineServerPool.PrewarmAsync(Gate.EnginePath), settle: false));
                        JoyPro.Gui.Services.EngineServerPool.Recycle("quality gate: the normal start is timed on a fresh server");
                    }
                    AvaloniaRenderThread.Wait(session.MeasureAsync("engine server start", "warm-up (once per session)",
                        () => JoyPro.Gui.Services.EngineServerPool.PrewarmAsync(Gate.EnginePath), settle: false));
                    AvaloniaRenderThread.Wait(body(session));
                }
                finally
                {
                    // Work the view model started by itself (pictures, aircraft lists) ends before the next session.
                    AvaloniaRenderThread.Wait(session.SettleAsync(() => true, 20000));
                    vm.StopGameConfigWatcher();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
        }
    }

    public bool Idle => !Vm.IsBusy && !Vm.IsBuilderSuggestLoading && !Vm.IsBuilderSearching && !Vm.IsCatalogLoading;

    // Waits until the view model is idle for a while and the condition holds (or the timeout passed); false on timeout.
    public async Task<bool> SettleAsync(Func<bool> condition, int timeoutMs = 60000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        int idle = 0;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            idle = Idle ? idle + 1 : 0;
            if (idle >= 4 && condition())
                return true;
            await Task.Delay(50);
        }
        return false;
    }

    // Times one operation of the view model: its wall time (until the view model is idle again) and its engine calls.
    public async Task<Gate.Timing> MeasureAsync(string operation, string subject, Func<Task> action, string detail = "", bool settle = true)
    {
        int before = Engine.Count;
        Stopwatch watch = Stopwatch.StartNew();
        await action();
        if (settle)
        {
            // The idle wait itself polls every 50 ms; the four idle polls are not the operation's time.
            await SettleAsync(() => true, 120000);
            watch.Stop();
            double ms = Math.Max(0, watch.Elapsed.TotalMilliseconds - 200);
            return Gate.Time(operation, subject, ms, Engine.Count - before, detail);
        }
        watch.Stop();
        return Gate.Time(operation, subject, watch.Elapsed.TotalMilliseconds, Engine.Count - before, detail);
    }

    // An empty list in the view model: the same engine call again, read like the view model reads it, so the finding
    // says where the rows got lost (engine answer, JSON, a row the view model's parser throws on).
    public async Task<string> WhyEmptyAsync(List<string> command)
    {
        List<string> args = HasProfile ? ["--profile", Profile, "--status", "false"] : ["--status", "false"];
        args.AddRange(command);
        JoyPro.Gui.Models.EngineCommandResult result = await Engine.RunAsync(Gate.EnginePath, args);
        if (result.ExitCode != 0)
            return "engine exit " + result.ExitCode + ": " + Gate.Short(result.StandardError, 120);
        string payload = MainWindowViewModel.ExtractJsonPayload(result.StandardOutput);
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("Results", out System.Text.Json.JsonElement list))
                return "engine answer has no Results";
            if (list.GetArrayLength() == 0)
                return "the engine finds nothing either";
            MethodInfo? parse = typeof(MainWindowViewModel).GetMethod("ParseBuilderSearchHit", BindingFlags.Instance | BindingFlags.NonPublic);
            int index = 0;
            foreach (System.Text.Json.JsonElement item in list.EnumerateArray())
            {
                try
                {
                    parse?.Invoke(Vm, [item]);
                }
                catch (TargetInvocationException ex)
                {
                    return "the engine has " + list.GetArrayLength() + " rows; the view model's row parser (ParseBuilderSearchHit) throws on row " + index + " (" +
                           Gate.Short(item.GetRawText(), 100) + "): " + ex.InnerException?.GetType().Name + " " + Gate.Short(ex.InnerException?.Message ?? string.Empty, 140);
                }
                index++;
            }
            return "the engine has " + list.GetArrayLength() + " rows and the row parser reads each; the view model drops them elsewhere";
        }
        catch (System.Text.Json.JsonException ex)
        {
            return "JSON does not parse (" + payload.Length + " chars): " + Gate.Short(ex.Message, 120);
        }
    }

    // A private step of the view model the GUI runs from a button with more around it (an export of one game).
    public Task<T> InvokePrivateAsync<T>(string method, params object?[] args)
    {
        MethodInfo info = typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                          ?? throw new MissingMethodException(nameof(MainWindowViewModel), method);
        return (Task<T>)info.Invoke(Vm, args)!;
    }

    public Task InvokePrivateAsync(string method, params object?[] args)
    {
        MethodInfo info = typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                          ?? throw new MissingMethodException(nameof(MainWindowViewModel), method);
        return (Task)info.Invoke(Vm, args)!;
    }
}
