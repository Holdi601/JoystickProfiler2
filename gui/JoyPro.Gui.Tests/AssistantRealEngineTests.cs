using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The JoyPro assistant end to end: this view model, the real engine and a scripted model (engine llm fake-server with
// JOYPRO_LLM_FAKE_RESPONSE, answers separated by "#next"): the model looks up the collectives, creates "Collectives",
// adds the result and an invented id (rejected), and says done; the page shows the relation at once, and one Undo takes
// the whole answer back. Needs a built engine, so it runs only when JOYPRO_ASSISTANT_ENGINE names engine.exe:
//   set JOYPRO_ASSISTANT_ENGINE=<repo>\engine\bin\x64\Debug\engine.exe & dotnet test gui/JoyPro.Gui.Tests --filter AssistantRealEngineTests
[SupportedOSPlatform("windows")]
public sealed class AssistantRealEngineTests
{
    [Fact]
    public async Task Assistant_RealEngineWithScriptedModel_CreatesAddsRejectsAndUndoesInOneStep()
    {
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_ASSISTANT_ENGINE");
        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "assistant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR") ?? string.Empty;
        string previousFake = Environment.GetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE") ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(folder, "data"));
            string script = Path.Combine(folder, "script.txt");
            File.WriteAllText(script, string.Join("\n",
                "{\"tool\":\"resolve\",\"query\":\"collective\",\"kind\":\"axis\"}", "#next",
                "{\"tool\":\"create_relation\",\"name\":\"Collectives\",\"kind\":\"axis\"}", "#next",
                "{\"tool\":\"add_to_relation\",\"relation\":\"Collectives\",\"ids\":[\"S1\",\"A99999\"]}", "#next",
                "{\"tool\":\"done\",\"reply\":\"Created Collectives with the helicopter collectives.\"}"));
            Environment.SetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE", script);

            // A profile with one relation.
            string profile = Path.Combine(folder, "assistant.pr0file");
            using (Process start = Process.Start(new ProcessStartInfo(engine, "--profile \"" + profile + "\" relations add Flare --button --node DCS:d3014pnilu3014cd16vd1vpnilvu0:F-16C_50")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
            {
                await start.StandardOutput.ReadToEndAsync();
                await start.WaitForExitAsync();
                Assert.Equal(0, start.ExitCode);
            }

            MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            Assert.Single(vm.Relations);
            // Nothing is bound yet: the Bind Studio still lists the relation instead of "Nothing to bind yet".
            Assert.Equal("Flare", Assert.Single(vm.Binds).RelationName);
            Assert.False(vm.HasNoBinds);
            vm.LlmEnabled = true;
            await RelationBuilderTests.WaitForAsync(() => !vm.IsBusy);
            vm.ShowBuilderAssistantCommand.Execute(null);
            vm.AssistantInput = "Put every helicopter collective into a new relation Collectives";
            await vm.AskAssistantCommand.ExecuteAsync(null);

            AssistantMessageItem answer = vm.AssistantMessages.Last();
            Assert.Equal("Created Collectives with the helicopter collectives.", answer.Text);
            Assert.Contains("A99999", answer.Notes);
            Assert.Equal("Collectives", Assert.Single(answer.Relations).Name);
            Assert.StartsWith("created · added ", answer.Relations[0].Text);
            Assert.Equal(4, answer.Steps.Count);
            Assert.Contains(vm.Relations, r => r.Name == "Collectives" && r.IsAxis);
            Assert.Equal("Collectives", vm.SelectedRelation?.Name);
            Assert.NotEmpty(vm.RelationNodes);
            Assert.True(answer.CanUndo);

            await vm.UndoAssistantTurnCommand.ExecuteAsync(answer);
            Assert.True(answer.IsUndone);
            Assert.DoesNotContain(vm.Relations, r => r.Name == "Collectives");
            Assert.Contains(vm.Relations, r => r.Name == "Flare");
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData.Length > 0 ? previousData : null);
            Environment.SetEnvironmentVariable("JOYPRO_LLM_FAKE_RESPONSE", previousFake.Length > 0 ? previousFake : null);
        }
    }
}
