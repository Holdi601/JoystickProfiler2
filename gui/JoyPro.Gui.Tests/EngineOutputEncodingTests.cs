using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The engine's output reaches the app unchanged: a title with curly quotes (MSFS 2024's 'Must be toggled ON by
// “GYROSCOPE TOGGLE”.') came through the console's code page as plain '"' and broke the JSON of "relations suggest";
// the editor then showed no suggestions and "JoyPro does not know this function yet". The engine writes UTF-8 for the
// app (JOYPRO_STDIO=utf8) and its JSON is ASCII (\uXXXX) for every other reader.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class EngineOutputEncodingTests
{
    private const string Name = "Pitch “gyro” – ä 日本語";

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    [Fact]
    public async Task RelationNamesOutsideTheCodePage_ComeBackUnchanged_InJsonAndText()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            DataDirEngineClient client = new(Path.Combine(folder, "data"));
            string profile = Path.Combine(folder, "encoding.pr0file");
            EngineCommandResult add = await client.RunAsync(engine, ["--profile", profile, "relations", "add", Name, "--axis", "--node", "DCS:a2001cdnil:FA-18C_hornet"]);
            Assert.True(add.ExitCode == 0, add.StandardError);

            // JSON: parses, and the name is the same.
            EngineCommandResult list = await client.RunAsync(engine, ["--profile", profile, "relations", "list"]);
            Assert.Equal(0, list.ExitCode);
            string json = MainWindowViewModel.ExtractJsonPayload(list.StandardOutput);
            Assert.DoesNotContain("“", json);
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                List<string> names = [];
                foreach (JsonElement relation in doc.RootElement.GetProperty("Relations").EnumerateArray())
                    names.Add(relation.GetProperty("Name").GetString() ?? string.Empty);
                Assert.Contains(Name, names);
            }

            // Text output (the app reads "relations show --text") keeps the characters too.
            EngineCommandResult show = await client.RunAsync(engine, ["--profile", profile, "--text", "relations", "show", Name]);
            Assert.True(show.ExitCode == 0, show.StandardError);
            Assert.Contains(Name, show.StandardOutput);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
