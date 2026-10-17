using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Gui.Services;

// The direction test's output: a "JoyPro.Ffb test" process of its own (started on the first command, with the GUI's
// pid so it ends with the GUI), one command line per call and one JSON answer line back ("ok", "position", "error").
// The process clamps every force to 25 % and stops pushes after a few seconds on its own; Dispose sends "quit" and
// kills the process if it does not end, which releases the stick in every case.
public sealed class FfbTestProcessOutput : IFfbTestOutput, IDisposable
{
    private readonly IFfbHostLauncher launcher;
    private readonly string exePath;
    private readonly IReadOnlyList<string> arguments;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object pendingGate = new();
    private IFfbHostProcess? process;
    private TaskCompletionSource<JsonElement>? pending;
    private bool disposed;

    public FfbTestProcessOutput(IFfbHostLauncher launcher, string exePath, string device, int guiPid)
    {
        this.launcher = launcher;
        this.exePath = exePath;
        List<string> args = ["test", "--parent-pid", guiPid.ToString(CultureInfo.InvariantCulture)];
        if (!string.IsNullOrWhiteSpace(device))
            args.AddRange(["--device", device]);
        arguments = args;
    }

    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public Task SetInvertAsync(bool invertX, bool invertY, CancellationToken token) =>
        SendAsync("invert " + (invertX ? "1" : "0") + " " + (invertY ? "1" : "0"), token);

    public Task PushAsync(FfbAxis axis, double magnitude, CancellationToken token) =>
        SendAsync("push " + axis + " " + magnitude.ToString("0.###", CultureInfo.InvariantCulture), token);

    public Task SpringAsync(double stiffness, double centreX, double centreY, CancellationToken token) =>
        SendAsync(string.Create(CultureInfo.InvariantCulture, $"spring {stiffness:0.###} {centreX:0.###} {centreY:0.###}"), token);

    public async Task<(double X, double Y)?> ReadPositionAsync(CancellationToken token)
    {
        JsonElement answer = await SendAsync("pos", token);
        if (answer.TryGetProperty("valid", out JsonElement valid) && valid.ValueKind == JsonValueKind.True &&
            answer.TryGetProperty("x", out JsonElement x) && answer.TryGetProperty("y", out JsonElement y))
            return (x.GetDouble(), y.GetDouble());
        return null;
    }

    public async Task StopAsync()
    {
        if (process == null || process.HasExited)
            return;
        try
        {
            await SendAsync("stop", CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // The process is gone or hangs: Dispose kills it, which releases the stick as well.
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        IFfbHostProcess? running = process;
        process = null;
        if (running == null)
            return;
        running.Send("quit");
        if (!running.WaitForExit(1500))
            running.Kill();
        running.Dispose();
    }

    // One command, one answer; an "error" answer throws (the wizard then stops with the message).
    private async Task<JsonElement> SendAsync(string command, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureStarted();
            TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pendingGate)
                pending = answer;
            if (!process!.Send(command))
                throw new InvalidOperationException(T("The force feedback test process has ended."));
            Task done = await Task.WhenAny(answer.Task, Task.Delay(AnswerTimeout, token));
            if (done != answer.Task)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("The force feedback test process did not answer.");
            }
            JsonElement result = await answer.Task;
            JoyPro.Logging.JoyProLog.Debug("FfbWizard", "Test process: " + command + " -> " + result.GetRawText());
            if (result.TryGetProperty("type", out JsonElement type) && type.GetString() == "error")
                throw new InvalidOperationException(result.TryGetProperty("message", out JsonElement message) ? message.GetString() : "error");
            return result;
        }
        finally
        {
            lock (pendingGate)
                pending = null;
            gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (process != null && !process.HasExited)
            return;
        process?.Dispose();
        process = launcher.Start(exePath, arguments);
        process.LineReceived += OnLine;
        process.Exited += () =>
        {
            lock (pendingGate)
                pending?.TrySetException(new InvalidOperationException(T("The force feedback test process has ended.")));
        };
    }

    private void OnLine(string line)
    {
        JsonElement json;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            json = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        string kind = json.TryGetProperty("type", out JsonElement type) ? type.GetString() ?? string.Empty : string.Empty;
        if (kind == "log")
            return;
        // A "no device" error before any command also ends the pending one.
        lock (pendingGate)
            pending?.TrySetResult(json);
    }
}
