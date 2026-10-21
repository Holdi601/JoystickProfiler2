using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Tests;

// An engine for the local AI tests: a profile of many relations, the model's status as the user has it (switched on,
// downloaded Qwen3 8B or their own llama.cpp files), and relations plan ask answered by the "model" when the app asks
// with the model allowed (--llm auto/on) or by the offline rules (--llm off: no rule for a long request).
[SupportedOSPlatform("windows")]
internal sealed class LocalAiFakeEngine : IEngineCliClient
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public int RelationCount { get; set; } = 40;

    public bool Enabled { get; set; } = true;

    public bool Ready { get; set; } = true;

    public bool UsingLocal { get; set; }

    public string LocalModel { get; set; } = @"D:\models\Qwen3.8-27B-UD-Q3_K_XL.gguf";

    // The plan the "model" answers (relations plan ask with --llm auto/on).
    public string PlanAnswer { get; set; } = string.Empty;

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        RunStreamingAsync(engineExePath, arguments, new EngineRunOptions(), cancellationToken);

    public Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default)
    {
        lock (Calls)
            Calls.Add(arguments.ToArray());
        (int code, string output, string error) = Answer(arguments);
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = code,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = output,
            StandardError = error,
            Arguments = arguments.ToArray()
        });
    }

    public List<IReadOnlyList<string>> CallsOf(params string[] verb)
    {
        lock (Calls)
            return Calls.Where(c => AxisTuningTests.Seq(c, verb)).ToList();
    }

    private (int, string, string) Answer(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "relations", "list"))
        {
            StringBuilder sb = new("{\"Relations\":[");
            for (int i = 0; i < RelationCount; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"Name\":\"Relation ").Append(i.ToString("000", CultureInfo.InvariantCulture)).Append("\",\"IsAxis\":").Append(i % 7 == 0 ? "true" : "false")
                  .Append(",\"Groups\":[\"G").Append((i % 5).ToString(CultureInfo.InvariantCulture)).Append("\"],\"Nodes\":[{\"Id\":\"d").Append(3000 + i)
                  .Append("\",\"Game\":\"DCS\",\"Aircraft\":{\"F-16C_50\":true}}],\"Bind\":null}");
            }
            sb.Append("]}");
            return (0, sb.ToString(), string.Empty);
        }
        if (AxisTuningTests.Seq(args, "llm", "status"))
            return (0, Status(), string.Empty);
        if (AxisTuningTests.Seq(args, "relations", "plan", "ask"))
        {
            string mode = args.SkipWhile(a => a != "--llm").Skip(1).FirstOrDefault() ?? "auto";
            if (mode == "off")
                return (1, string.Empty, "JoyPro has no offline rule for this request; switch on the local AI model or say it more simply.");
            return (0, PlanAnswer, string.Empty);
        }
        return (0, "{}", string.Empty);
    }

    private string Status()
    {
        string model(string id, string label, bool installed, bool recommended) =>
            "{\"Id\":\"" + id + "\",\"Label\":\"" + label + "\",\"Tier\":\"large\",\"Size\":\"4.68 GB\",\"License\":\"Apache-2.0\",\"LicenseUrl\":\"https://example.invalid/LICENSE\",\"Source\":\"https://example.invalid\"," +
            "\"Installed\":" + (installed ? "true" : "false") + ",\"Recommended\":" + (recommended ? "true" : "false") + "}";
        string models = string.Join(",", model("qwen3-4b-q4_k_m", "Qwen3 4B (Q4_K_M, non-thinking)", false, false), model("qwen3-8b-q4_k_m", "Qwen3 8B (Q4_K_M, non-thinking)", Ready && !UsingLocal, true));
        string local = UsingLocal
            ? "\"UsingLocal\":true,\"LocalReady\":" + (Ready ? "true" : "false") + ",\"LocalServer\":\"C:\\\\llama.cpp\\\\llama-server.exe\",\"LocalModel\":" + System.Text.Json.JsonSerializer.Serialize(LocalModel) + ",\"LocalGpuLayers\":\"\","
            : "\"UsingLocal\":false,\"LocalReady\":false,\"LocalServer\":\"\",\"LocalModel\":\"\",\"LocalGpuLayers\":\"\",";
        return "{\"Enabled\":" + (Enabled ? "true" : "false") + ",\"Ready\":" + (Enabled && Ready ? "true" : "false") + ",\"SelectedModel\":\"" + (UsingLocal ? "local" : "qwen3-8b-q4_k_m") + "\"," + local +
               "\"RuntimeChoice\":\"auto\",\"RuntimeSizesMb\":{\"cpu\":30,\"vulkan\":60,\"cuda\":600},\"RuntimeExtraLicenses\":{}," +
               "\"Hardware\":{\"Source\":\"detected\",\"RamBytes\":68719476736,\"PhysicalCores\":16,\"Avx2\":true,\"Gpus\":[{\"Name\":\"NVIDIA GeForce RTX 5080\",\"VramBytes\":17094934528}]}," +
               "\"Recommendation\":{\"ModelId\":\"qwen3-8b-q4_k_m\",\"Tier\":\"large\",\"Runtime\":\"vulkan\",\"GpuLayers\":99,\"Threads\":8,\"Reason\":\"the 8B model fits completely on the GPU.\"}," +
               "\"Models\":[" + models + "],\"RuntimeName\":\"llama.cpp (llama-server)\",\"RuntimeInstalled\":" + (Ready ? "true" : "false") + ",\"RuntimeFlavor\":\"vulkan\",\"RuntimeApproxMb\":60," +
               "\"RuntimeLicense\":\"MIT\",\"RuntimeLicenseUrl\":\"https://example.invalid\",\"ProgressFile\":\"\",\"CancelFile\":\"\"}";
    }
}
