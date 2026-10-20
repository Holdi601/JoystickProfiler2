using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace JoyPro.Engine
{
    // Optional local language model for "Group by description" (engine llm ...). Nothing is bundled and nothing is
    // downloaded until the user asks for it: the model (GGUF from Hugging Face, pinned revision + SHA-256) and the
    // llama.cpp server (official release, SHA-256 from the release metadata) land in <data>\Llm. JoyPro picks the model
    // size for the PC (RAM, GPU/VRAM, AVX2). The model runs in its own llama-server process only while one request is
    // answered: start, load, answer (grammar-constrained JSON), kill. A job object kills it with the engine, too.
    // Tests: JOYPRO_LLM_FAKE_RESPONSE makes the runner start "engine llm fake-server" instead (same process life
    // cycle, canned answer); JOYPRO_LLM_CATALOG points the downloader at a local test catalog.
    internal static partial class EngineLlm
    {
        public const string FakeResponseVariable = "JOYPRO_LLM_FAKE_RESPONSE";
        public const string FakeLogVariable = "JOYPRO_LLM_FAKE_LOG";
        public const string CatalogVariable = "JOYPRO_LLM_CATALOG";

        // ------------------------------------------------------------------------------------------------------
        // Folders and settings
        // ------------------------------------------------------------------------------------------------------

        public static string DataRoot()
        {
            string root = Environment.GetEnvironmentVariable(GameBackups.DataFolderVariable);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
            return Path.Combine(root, "Llm");
        }

        public static string ModelsFolder() { return Path.Combine(DataRoot(), "models"); }
        public static string RuntimeFolder() { return Path.Combine(DataRoot(), "runtime"); }
        public static string SettingsFile() { return Path.Combine(DataRoot(), "settings.json"); }
        public static string ProgressFile() { return Path.Combine(DataRoot(), "download-progress.json"); }
        public static string CancelFile() { return Path.Combine(DataRoot(), "download.cancel"); }

        public sealed class Settings
        {
            public bool Enabled { get; set; }
            public string ModelId { get; set; }
            // Runtime build to download: auto (Vulkan with a graphics card, else CPU), vulkan, cuda or cpu.
            public string Runtime { get; set; }
            // engine llm use-local: the user's own llama-server.exe and GGUF (ModelId "local").
            public string LocalServer { get; set; }
            public string LocalModel { get; set; }
            public string LocalGpuLayers { get; set; }
            public int LocalContextSize { get; set; }
            public string LocalTemplate { get; set; }

            public Settings()
            {
                ModelId = string.Empty;
                Runtime = string.Empty;
                LocalServer = string.Empty;
                LocalModel = string.Empty;
                LocalGpuLayers = string.Empty;
                LocalTemplate = string.Empty;
            }
        }

        public static Settings LoadSettings()
        {
            try
            {
                string file = SettingsFile();
                if (File.Exists(file))
                    return EngineSerialization.FromJson<Settings>(File.ReadAllText(file, Encoding.UTF8)) ?? new Settings();
            }
            catch
            {
            }
            return new Settings();
        }

        public static void SaveSettings(Settings settings)
        {
            Directory.CreateDirectory(DataRoot());
            File.WriteAllText(SettingsFile(), EngineSerialization.ToJson(settings), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------------------------------------------
        // Catalog (DB\Llm\models.json)
        // ------------------------------------------------------------------------------------------------------

        public sealed class ModelInfo
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public string Family = string.Empty;
            public string Parameters = string.Empty;
            public string Quantization = string.Empty;
            public string Tier = string.Empty;
            public string Url = string.Empty;
            public string File = string.Empty;
            public long SizeBytes;
            public string Sha256 = string.Empty;
            public string License = string.Empty;
            public string LicenseUrl = string.Empty;
            public string Source = string.Empty;
            public int ContextSize = 4096;
            public int MaxFunctions = 20;
            public int MaxScopes = 90;
            public int MaxTokens = 200;
            public double Temperature;
            public string Template = "chatml";
            public bool NoThink;
            public double VramGb;
            public double RamGb;
            public int LoadTimeoutSeconds = 180;
            public int RequestTimeoutSeconds = 180;
            public List<string> ServerArgs = new List<string>();
            // Prompt size: example commands per function line and few-shot examples (DB\Llm\models.json prompt.examples).
            public int ExamplesPerFunction = 2;
            public int FewShot = 3;
            // A llama.cpp and GGUF the user already has (engine llm use-local): run from their paths, the server applies
            // the GGUF's own chat template (Template "server"), GPU layers as the user set them (else llama.cpp decides).
            public bool Local;
            public string ServerPath = string.Empty;
            public string LocalModelPath = string.Empty;
            public string GpuLayers = string.Empty;
        }

        public const string LocalModelId = "local";

        public sealed class Message
        {
            public string Role = string.Empty;
            public string Content = string.Empty;
        }

        public sealed class RuntimeInfo
        {
            public string Name = "llama.cpp (llama-server)";
            public string ReleaseApi = string.Empty;
            public Dictionary<string, string> Assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // A second archive a flavor needs (CUDA: the CUDA runtime DLLs), unpacked into the same folder.
            public Dictionary<string, string> ExtraAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // The license of such a second archive when it is not llama.cpp's own (CUDA: NVIDIA's CUDA EULA), shown in
            // the consent text of that flavor.
            public Dictionary<string, string> ExtraAssetLicenses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> ApproxSizeMb = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public string License = string.Empty;
            public string LicenseUrl = string.Empty;
            public string Homepage = string.Empty;
        }

        public sealed class Catalog
        {
            public List<ModelInfo> Models = new List<ModelInfo>();
            public RuntimeInfo Runtime = new RuntimeInfo();
            public string SystemPrompt = string.Empty;
            // Few-shot examples: a user turn in the exact format of the real prompt and the answer JoyPro expects.
            public List<KeyValuePair<string, string>> Examples = new List<KeyValuePair<string, string>>();
            // The relation editor's chat (engine relations chat): its own system prompt and few-shot turns, the same
            // models, sampling and runner.
            public string ChatSystemPrompt = string.Empty;
            public List<KeyValuePair<string, string>> ChatExamples = new List<KeyValuePair<string, string>>();
            // The relation assistant (engine relations assistant): its system prompt with the tool list and a worked
            // example conversation (role, content), the same models and runner.
            public string AssistantSystemPrompt = string.Empty;
            public List<KeyValuePair<string, string>> AssistantExamples = new List<KeyValuePair<string, string>>();
            public string Path = string.Empty;
        }

        public static Catalog LoadCatalog()
        {
            string file = Environment.GetEnvironmentVariable(CatalogVariable);
            if (string.IsNullOrWhiteSpace(file))
                file = Path.Combine(MainStructure.PROGPATH ?? string.Empty, "DB", "Llm", "models.json");
            Catalog catalog = new Catalog { Path = file };
            if (!File.Exists(file))
                return catalog;
            Dictionary<string, object> root = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            Dictionary<string, object> runtime = EngineNlGroup.Get(root, "runtime") as Dictionary<string, object>;
            if (runtime != null)
            {
                catalog.Runtime.Name = Or(EngineNlGroup.Text(runtime, "name"), catalog.Runtime.Name);
                catalog.Runtime.ReleaseApi = EngineNlGroup.Text(runtime, "releaseApi");
                catalog.Runtime.License = EngineNlGroup.Text(runtime, "license");
                catalog.Runtime.LicenseUrl = EngineNlGroup.Text(runtime, "licenseUrl");
                catalog.Runtime.Homepage = EngineNlGroup.Text(runtime, "homepage");
                Dictionary<string, object> assets = EngineNlGroup.Get(runtime, "assets") as Dictionary<string, object>;
                if (assets != null)
                {
                    foreach (KeyValuePair<string, object> pair in assets)
                        catalog.Runtime.Assets[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
                Dictionary<string, object> extras = EngineNlGroup.Get(runtime, "extraAssets") as Dictionary<string, object>;
                if (extras != null)
                {
                    foreach (KeyValuePair<string, object> pair in extras)
                        catalog.Runtime.ExtraAssets[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
                Dictionary<string, object> extraLicenses = EngineNlGroup.Get(runtime, "extraAssetLicenses") as Dictionary<string, object>;
                if (extraLicenses != null)
                {
                    foreach (KeyValuePair<string, object> pair in extraLicenses)
                        catalog.Runtime.ExtraAssetLicenses[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
                Dictionary<string, object> sizes = EngineNlGroup.Get(runtime, "approxSizeMb") as Dictionary<string, object>;
                if (sizes != null)
                {
                    foreach (KeyValuePair<string, object> pair in sizes)
                        catalog.Runtime.ApproxSizeMb[pair.Key] = Convert.ToInt32(pair.Value, CultureInfo.InvariantCulture);
                }
            }
            Dictionary<string, object> prompt = EngineNlGroup.Get(root, "prompt") as Dictionary<string, object>;
            catalog.SystemPrompt = string.Join("\n", EngineNlGroup.Strings(EngineNlGroup.Get(prompt, "system")));
            foreach (Dictionary<string, object> example in EngineNlGroup.Objects(EngineNlGroup.Get(prompt, "examples")))
            {
                string user = string.Join("\n", EngineNlGroup.Strings(EngineNlGroup.Get(example, "user")));
                string assistant = EngineNlGroup.Text(example, "assistant");
                if (user.Length > 0 && assistant.Length > 0)
                    catalog.Examples.Add(new KeyValuePair<string, string>(user, assistant));
            }
            Dictionary<string, object> chat = EngineNlGroup.Get(root, "relationChat") as Dictionary<string, object>;
            catalog.ChatSystemPrompt = string.Join("\n", EngineNlGroup.Strings(EngineNlGroup.Get(chat, "system")));
            foreach (Dictionary<string, object> example in EngineNlGroup.Objects(EngineNlGroup.Get(chat, "examples")))
            {
                string user = string.Join("\n", EngineNlGroup.Strings(EngineNlGroup.Get(example, "user")));
                string assistant = EngineNlGroup.Text(example, "assistant");
                if (user.Length > 0 && assistant.Length > 0)
                    catalog.ChatExamples.Add(new KeyValuePair<string, string>(user, assistant));
            }
            Dictionary<string, object> assistantPrompt = EngineNlGroup.Get(root, "relationAssistant") as Dictionary<string, object>;
            catalog.AssistantSystemPrompt = string.Join("\n", EngineNlGroup.Strings(EngineNlGroup.Get(assistantPrompt, "system")));
            foreach (Dictionary<string, object> turn in EngineNlGroup.Objects(EngineNlGroup.Get(assistantPrompt, "example")))
            {
                string role = EngineNlGroup.Text(turn, "role");
                object raw = EngineNlGroup.Get(turn, "content");
                string content = raw is string ? ((string)raw).Trim() : string.Join("\n", EngineNlGroup.Strings(raw));
                if ((role == "user" || role == "assistant") && content.Length > 0)
                    catalog.AssistantExamples.Add(new KeyValuePair<string, string>(role, content));
            }
            foreach (Dictionary<string, object> item in EngineNlGroup.Objects(EngineNlGroup.Get(root, "models")))
            {
                ModelInfo model = new ModelInfo
                {
                    Id = EngineNlGroup.Text(item, "id"),
                    Label = EngineNlGroup.Text(item, "label"),
                    Family = EngineNlGroup.Text(item, "family"),
                    Parameters = EngineNlGroup.Text(item, "parameters"),
                    Quantization = EngineNlGroup.Text(item, "quantization"),
                    Tier = EngineNlGroup.Text(item, "tier"),
                    Url = EngineNlGroup.Text(item, "url"),
                    File = EngineNlGroup.Text(item, "file"),
                    SizeBytes = Long(item, "sizeBytes", 0),
                    Sha256 = EngineNlGroup.Text(item, "sha256").ToLowerInvariant(),
                    License = EngineNlGroup.Text(item, "license"),
                    LicenseUrl = EngineNlGroup.Text(item, "licenseUrl"),
                    Source = EngineNlGroup.Text(item, "source"),
                    ContextSize = (int)Long(item, "contextSize", 4096),
                    MaxFunctions = (int)Long(item, "maxFunctions", 20),
                    MaxScopes = (int)Long(item, "maxScopes", 90),
                    MaxTokens = (int)Long(item, "maxTokens", 200),
                    Temperature = Double(item, "temperature", 0),
                    Template = Or(EngineNlGroup.Text(item, "template"), "chatml"),
                    NoThink = string.Equals(EngineNlGroup.Text(item, "noThink"), "true", StringComparison.OrdinalIgnoreCase),
                    VramGb = Double(item, "vramGb", 0),
                    RamGb = Double(item, "ramGb", 0),
                    LoadTimeoutSeconds = (int)Long(item, "loadTimeoutSeconds", 180),
                    RequestTimeoutSeconds = (int)Long(item, "requestTimeoutSeconds", 180),
                    ServerArgs = EngineNlGroup.Strings(EngineNlGroup.Get(item, "serverArgs")).ToList(),
                    ExamplesPerFunction = (int)Long(item, "examplesPerFunction", 2),
                    FewShot = (int)Long(item, "fewShot", 3)
                };
                if (model.Id.Length > 0 && model.File.Length > 0 && model.File.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                    catalog.Models.Add(model);
            }
            return catalog;
        }

        public static string SystemPrompt()
        {
            return SystemPrompt(LoadCatalog());
        }

        public static string SystemPrompt(Catalog catalog)
        {
            string prompt = catalog.SystemPrompt;
            return prompt.Length > 0
                ? prompt
                : "Pick only ids from the lists. Answer with the JSON object only.";
        }

        // System prompt, the first few-shot examples (user turn + expected answer) and the request.
        public static List<Message> Conversation(Catalog catalog, int fewShot, string user)
        {
            List<Message> messages = new List<Message> { new Message { Role = "system", Content = SystemPrompt(catalog) } };
            foreach (KeyValuePair<string, string> example in catalog.Examples.Take(Math.Max(0, fewShot)))
            {
                messages.Add(new Message { Role = "user", Content = example.Key });
                messages.Add(new Message { Role = "assistant", Content = example.Value });
            }
            messages.Add(new Message { Role = "user", Content = user });
            return messages;
        }

        // The relation chat's conversation: its system prompt, few-shot turns and the message.
        public static List<Message> ChatConversation(Catalog catalog, int fewShot, string user)
        {
            string system = catalog.ChatSystemPrompt.Length > 0
                ? catalog.ChatSystemPrompt
                : "Pick only ids from the lists. Answer with the JSON object only: {\"note\":\"...\",\"add\":[\"C1\"],\"remove\":[\"N1\"]}";
            List<Message> messages = new List<Message> { new Message { Role = "system", Content = system } };
            foreach (KeyValuePair<string, string> example in catalog.ChatExamples.Take(Math.Max(0, fewShot)))
            {
                messages.Add(new Message { Role = "user", Content = example.Key });
                messages.Add(new Message { Role = "assistant", Content = example.Value });
            }
            messages.Add(new Message { Role = "user", Content = user });
            return messages;
        }

        // Rough token count for the context budget: about 3.2 characters per token for this text of names, ids and
        // punctuation (Qwen tokenizer), plus the chat template's markers per message. Errs on the large side.
        public static int EstimateTokens(IEnumerable<Message> messages)
        {
            int tokens = 8;
            foreach (Message message in messages)
                tokens += 6 + (int)Math.Ceiling(message.Content.Length / 3.2);
            return tokens;
        }

        private static string Or(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static long Long(Dictionary<string, object> item, string key, long fallback)
        {
            long value;
            return long.TryParse(EngineNlGroup.Text(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static double Double(Dictionary<string, object> item, string key, double fallback)
        {
            double value;
            return double.TryParse(EngineNlGroup.Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        // ------------------------------------------------------------------------------------------------------
        // Hardware
        // ------------------------------------------------------------------------------------------------------

        public sealed class Gpu
        {
            public string Name { get; set; }
            public string Vendor { get; set; }
            public long VramBytes { get; set; }
            public bool Dedicated { get; set; }
        }

        public sealed class Hardware
        {
            public string Source { get; set; }
            public long RamBytes { get; set; }
            public long RamAvailableBytes { get; set; }
            public string CpuName { get; set; }
            public int LogicalCores { get; set; }
            public int PhysicalCores { get; set; }
            public bool Avx { get; set; }
            public bool Avx2 { get; set; }
            public bool Avx512 { get; set; }
            public List<Gpu> Gpus { get; set; }

            public Hardware()
            {
                Source = "detected";
                CpuName = string.Empty;
                Gpus = new List<Gpu>();
            }

            public Gpu BestGpu
            {
                get { return Gpus.Where(g => g.Dedicated).OrderByDescending(g => g.VramBytes).FirstOrDefault(); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessorFeaturePresent(uint feature);

        private const uint PfAvx = 39;
        private const uint PfAvx2 = 40;
        private const uint PfAvx512F = 41;
        private const long GiB = 1024L * 1024L * 1024L;

        public static Hardware DetectHardware()
        {
            Hardware hw = new Hardware();
            try
            {
                MemoryStatusEx status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (GlobalMemoryStatusEx(ref status))
                {
                    hw.RamBytes = (long)status.TotalPhys;
                    hw.RamAvailableBytes = (long)status.AvailPhys;
                }
            }
            catch
            {
            }
            hw.LogicalCores = Environment.ProcessorCount;
            hw.PhysicalCores = Math.Max(1, hw.LogicalCores / 2);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    int cores = 0;
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                            cores += Convert.ToInt32(item["NumberOfCores"], CultureInfo.InvariantCulture);
                    }
                    if (cores > 0)
                        hw.PhysicalCores = cores;
                }
            }
            catch
            {
            }
            try
            {
                using (RegistryKey cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    hw.CpuName = cpu == null ? string.Empty : Convert.ToString(cpu.GetValue("ProcessorNameString") ?? string.Empty, CultureInfo.InvariantCulture).Trim();
            }
            catch
            {
            }
            try
            {
                hw.Avx = IsProcessorFeaturePresent(PfAvx);
                hw.Avx2 = IsProcessorFeaturePresent(PfAvx2);
                hw.Avx512 = IsProcessorFeaturePresent(PfAvx512F);
            }
            catch
            {
            }
            hw.Gpus = DetectGpus();
            return hw;
        }

        // Display adapters from the registry: the 64-bit memory size (WMI's AdapterRAM stops at 4 GB).
        private static List<Gpu> DetectGpus()
        {
            List<Gpu> gpus = new List<Gpu>();
            try
            {
                using (RegistryKey display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (display == null)
                        return gpus;
                    foreach (string sub in display.GetSubKeyNames())
                    {
                        if (!Regex.IsMatch(sub, @"^\d{4}$"))
                            continue;
                        try
                        {
                            using (RegistryKey key = display.OpenSubKey(sub))
                            {
                                if (key == null)
                                    continue;
                                string name = Convert.ToString(key.GetValue("DriverDesc") ?? string.Empty, CultureInfo.InvariantCulture).Trim();
                                if (name.Length == 0)
                                    continue;
                                long vram = RegistryBytes(key.GetValue("HardwareInformation.qwMemorySize"));
                                if (vram <= 0)
                                    vram = RegistryBytes(key.GetValue("HardwareInformation.MemorySize"));
                                string device = Convert.ToString(key.GetValue("MatchingDeviceId") ?? string.Empty, CultureInfo.InvariantCulture).ToLowerInvariant();
                                string vendor = VendorOf(device, name);
                                if (gpus.Any(g => g.Name == name && g.VramBytes == vram))
                                    continue;
                                gpus.Add(new Gpu { Name = name, Vendor = vendor, VramBytes = vram, Dedicated = IsDedicated(vendor, vram) && !IsIntegratedName(name) });
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            return gpus;
        }

        private static long RegistryBytes(object value)
        {
            if (value == null)
                return 0;
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                if (bytes.Length >= 8)
                    return BitConverter.ToInt64(bytes, 0);
                if (bytes.Length >= 4)
                    return BitConverter.ToUInt32(bytes, 0);
                return 0;
            }
            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture) & 0x7FFFFFFFFFFFFFFFL;
            }
            catch
            {
                return 0;
            }
        }

        internal static string VendorOf(string deviceId, string name)
        {
            string id = (deviceId ?? string.Empty).ToLowerInvariant();
            string n = (name ?? string.Empty).ToLowerInvariant();
            if (id.Contains("ven_10de") || n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro"))
                return "nvidia";
            if (id.Contains("ven_1002") || n.Contains("amd") || n.Contains("radeon"))
                return "amd";
            if (id.Contains("ven_8086") || n.Contains("intel"))
                return "intel";
            return "other";
        }

        // Integrated graphics share the system memory and are no help for a model; 2 GB of own memory is the line.
        internal static bool IsDedicated(string vendor, long vramBytes)
        {
            return vramBytes >= 2 * GiB && vendor != "other";
        }

        // Integrated graphics that report a carved-out memory size ("AMD Radeon(TM) Graphics" of Ryzen CPUs, Intel
        // UHD/Iris/HD Graphics) still share the system memory.
        internal static bool IsIntegratedName(string name)
        {
            string n = (name ?? string.Empty).Trim();
            return Regex.IsMatch(n, @"^(AMD\s+)?Radeon(\(TM\))?\s+(\d+M\s+)?Graphics$", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(n, @"Intel.*\b(UHD|Iris|HD)\b.*Graphics", RegexOptions.IgnoreCase);
        }

        public static Hardware InjectedHardware(double? ramGb, double? vramGb, string gpuName, string gpuVendor, int? cores, bool? avx2)
        {
            Hardware hw = DetectHardware();
            hw.Source = "injected";
            if (ramGb.HasValue)
            {
                hw.RamBytes = (long)(ramGb.Value * GiB);
                hw.RamAvailableBytes = hw.RamBytes / 2;
            }
            if (cores.HasValue)
            {
                hw.PhysicalCores = Math.Max(1, cores.Value);
                hw.LogicalCores = hw.PhysicalCores * 2;
            }
            if (avx2.HasValue)
            {
                hw.Avx2 = avx2.Value;
                hw.Avx = hw.Avx || avx2.Value;
            }
            if (vramGb.HasValue || !string.IsNullOrWhiteSpace(gpuName) || !string.IsNullOrWhiteSpace(gpuVendor))
            {
                long vram = (long)((vramGb ?? 0) * GiB);
                string name = string.IsNullOrWhiteSpace(gpuName) ? "Injected GPU" : gpuName.Trim();
                string vendor = string.IsNullOrWhiteSpace(gpuVendor) ? VendorOf(string.Empty, name) : gpuVendor.Trim().ToLowerInvariant();
                if (vendor == "other" && vram > 0)
                    vendor = "unknown";
                hw.Gpus = new List<Gpu>();
                if (vram > 0)
                    hw.Gpus.Add(new Gpu { Name = name, Vendor = vendor, VramBytes = vram, Dedicated = vram >= 2 * GiB });
            }
            return hw;
        }

        // ------------------------------------------------------------------------------------------------------
        // Recommendation: model size and where it runs
        // ------------------------------------------------------------------------------------------------------

        public sealed class Recommendation
        {
            public string ModelId { get; set; }
            public string Tier { get; set; }
            public string Runtime { get; set; }
            public int GpuLayers { get; set; }
            public int Threads { get; set; }
            public string Gpu { get; set; }
            public string Reason { get; set; }

            public Recommendation()
            {
                ModelId = string.Empty;
                Tier = "none";
                Runtime = "cpu";
                Gpu = string.Empty;
                Reason = string.Empty;
            }
        }

        // The table in docs/natural-language-grouping.md. The task is short (pick ids from lists of ~100 lines with a
        // JSON grammar), so a small model is enough; a bigger one understands odd phrasings better. A model runs on
        // the GPU when it fits completely (Vulkan build, every vendor), else on the CPU (needs AVX2 to be quick).
        public static Recommendation Recommend(Hardware hw, Catalog catalog)
        {
            double ram = hw.RamBytes / (double)GiB;
            Gpu gpu = hw.BestGpu;
            double vram = gpu == null ? 0 : gpu.VramBytes / (double)GiB;
            Recommendation r = new Recommendation { Threads = Math.Max(2, Math.Min(8, hw.PhysicalCores)), Gpu = gpu == null ? string.Empty : gpu.Name };
            string ramText = ram.ToString("0.#", CultureInfo.InvariantCulture) + " GB RAM";
            string vramText = gpu == null ? "no dedicated GPU" : gpu.Name + " with " + vram.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
            if (vram >= 7.5 && ram >= 7.5)
            {
                r.Tier = "large";
                r.Runtime = "vulkan";
                r.GpuLayers = 99;
                r.Reason = vramText + ": the 8B model fits completely on the GPU.";
            }
            else if (vram >= 3.5 && ram >= 7.5)
            {
                r.Tier = "medium";
                r.Runtime = "vulkan";
                r.GpuLayers = 99;
                r.Reason = vramText + ": the 4B model fits completely on the GPU.";
            }
            else if (ram >= 15.5 && hw.Avx2 && hw.PhysicalCores >= 8)
            {
                r.Tier = "medium";
                r.Reason = vramText + ", " + ramText + ", " + hw.PhysicalCores.ToString(CultureInfo.InvariantCulture) + " cores with AVX2: the 4B model on the CPU (a few seconds per request).";
            }
            else if (ram >= 7.5 && hw.Avx2)
            {
                r.Tier = "small";
                r.Reason = vramText + ", " + ramText + " with AVX2: the 1.5B model on the CPU.";
            }
            else if (ram >= 3.5)
            {
                r.Tier = "tiny";
                r.Reason = ramText + (hw.Avx2 ? string.Empty : ", no AVX2") + ": only the 0.5B model is quick enough.";
            }
            else
            {
                r.Tier = "none";
                r.Reason = ramText + ": too little memory for a local model; the offline rules do the work.";
                return r;
            }
            ModelInfo model = catalog.Models.FirstOrDefault(m => string.Equals(m.Tier, r.Tier, StringComparison.OrdinalIgnoreCase));
            if (model != null)
                r.ModelId = model.Id;
            return r;
        }

        // ------------------------------------------------------------------------------------------------------
        // Installed files
        // ------------------------------------------------------------------------------------------------------

        public static string ModelPath(ModelInfo model)
        {
            return Path.Combine(ModelsFolder(), model.File);
        }

        // A model counts as installed when its verified marker (written after the SHA-256 check) still matches.
        public static bool IsModelInstalled(ModelInfo model)
        {
            string path = ModelPath(model);
            string marker = path + ".sha256";
            if (!File.Exists(path) || !File.Exists(marker))
                return false;
            try
            {
                return string.Equals(File.ReadAllText(marker).Trim(), model.Sha256, StringComparison.OrdinalIgnoreCase) &&
                       (model.SizeBytes <= 0 || new FileInfo(path).Length == model.SizeBytes);
            }
            catch
            {
                return false;
            }
        }

        public sealed class RuntimeManifest
        {
            public string Flavor { get; set; }
            public string Tag { get; set; }
            public string Asset { get; set; }
            public string Sha256 { get; set; }
            public string Exe { get; set; }
        }

        public static RuntimeManifest InstalledRuntime(string preferredFlavor)
        {
            foreach (string flavor in new[] { preferredFlavor, "vulkan", "cuda", "cpu" }.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
            {
                string file = Path.Combine(RuntimeFolder(), flavor, "runtime.json");
                try
                {
                    if (!File.Exists(file))
                        continue;
                    RuntimeManifest manifest = EngineSerialization.FromJson<RuntimeManifest>(File.ReadAllText(file, Encoding.UTF8));
                    if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Exe) && File.Exists(manifest.Exe))
                        return manifest;
                }
                catch
                {
                }
            }
            return null;
        }

        public static bool UsesLocal(Settings settings)
        {
            return string.Equals(settings.ModelId, LocalModelId, StringComparison.OrdinalIgnoreCase);
        }

        // The user's own llama.cpp + GGUF. The prompt gets the large model's room (a model the user runs is rarely
        // tiny); the context size and GPU layers are the user's or llama.cpp's choice.
        public static ModelInfo LocalModel(Settings settings)
        {
            string model = settings.LocalModel ?? string.Empty;
            string template = string.IsNullOrWhiteSpace(settings.LocalTemplate) ? "server" : settings.LocalTemplate.Trim().ToLowerInvariant();
            long size = 0;
            try
            {
                if (File.Exists(model))
                    size = FileSize(model);
            }
            catch
            {
            }
            return new ModelInfo
            {
                Id = LocalModelId,
                Label = "Your model: " + (model.Length > 0 ? System.IO.Path.GetFileName(model) : "-"),
                Family = "local",
                Tier = "local",
                File = model.Length > 0 ? System.IO.Path.GetFileName(model) : string.Empty,
                SizeBytes = size,
                License = "your own file",
                Source = model,
                ContextSize = settings.LocalContextSize > 0 ? settings.LocalContextSize : 8192,
                MaxFunctions = 32,
                MaxScopes = 120,
                MaxTokens = 240,
                Temperature = 0,
                Template = template,
                NoThink = true,
                LoadTimeoutSeconds = 300,
                RequestTimeoutSeconds = 240,
                ExamplesPerFunction = 2,
                FewShot = 3,
                Local = true,
                ServerPath = settings.LocalServer ?? string.Empty,
                LocalModelPath = model,
                GpuLayers = settings.LocalGpuLayers ?? string.Empty
            };
        }

        public static ModelInfo SelectedModel(Catalog catalog, Settings settings, Recommendation recommendation)
        {
            if (UsesLocal(settings))
                return LocalModel(settings);
            ModelInfo model = catalog.Models.FirstOrDefault(m => string.Equals(m.Id, settings.ModelId, StringComparison.OrdinalIgnoreCase));
            if (model == null)
                model = catalog.Models.FirstOrDefault(m => string.Equals(m.Id, recommendation.ModelId, StringComparison.OrdinalIgnoreCase));
            return model;
        }

        // The model "Group by description" may use: enabled by the user, downloaded and verified, runtime present.
        public static bool TryGetUsableModel(out ModelInfo model, out string problem)
        {
            model = null;
            problem = string.Empty;
            Catalog catalog = LoadCatalog();
            Settings settings = LoadSettings();
            Recommendation recommendation = Recommend(DetectHardware(), catalog);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FakeResponseVariable)))
            {
                model = SelectedModel(catalog, settings, recommendation) ?? catalog.Models.FirstOrDefault() ?? new ModelInfo { Id = "fake", Label = "fake" };
                return true;
            }
            if (!settings.Enabled)
            {
                problem = "The local AI model is off (Relations > Group by description > Use local AI model, or engine llm enable).";
                return false;
            }
            model = SelectedModel(catalog, settings, recommendation);
            if (model == null)
            {
                problem = "No local model chosen (engine llm recommend, then engine llm download <model> --accept-license).";
                return false;
            }
            if (model.Local)
            {
                if (!File.Exists(model.ServerPath))
                {
                    problem = "Your llama-server.exe was not found: " + model.ServerPath + " (engine llm use-local --server <path> --model <gguf>).";
                    return false;
                }
                if (!File.Exists(model.LocalModelPath))
                {
                    problem = "Your GGUF model was not found: " + model.LocalModelPath + " (engine llm use-local --server <path> --model <gguf>).";
                    return false;
                }
                return true;
            }
            if (!IsModelInstalled(model))
            {
                problem = "The local model " + model.Id + " is not downloaded (engine llm download " + model.Id + " --accept-license).";
                return false;
            }
            if (InstalledRuntime(RuntimeFlavor(settings, recommendation)) == null)
            {
                problem = "The llama.cpp runtime is not downloaded (engine llm download " + model.Id + " --accept-license).";
                return false;
            }
            return true;
        }

        // The runtime build: the user's choice (settings / --runtime), else Vulkan with a graphics card, else CPU.
        public static string RuntimeFlavor(Settings settings, Recommendation recommendation)
        {
            string chosen = (settings.Runtime ?? string.Empty).Trim().ToLowerInvariant();
            return chosen.Length > 0 && chosen != "auto" ? chosen : recommendation.Runtime;
        }

        // ------------------------------------------------------------------------------------------------------
        // Status
        // ------------------------------------------------------------------------------------------------------

        public static EngineLlmStatus Status(Hardware hw)
        {
            Catalog catalog = LoadCatalog();
            Settings settings = LoadSettings();
            Recommendation recommendation = Recommend(hw, catalog);
            ModelInfo selected = SelectedModel(catalog, settings, recommendation);
            string flavor = RuntimeFlavor(settings, recommendation);
            RuntimeManifest runtime = InstalledRuntime(flavor);
            EngineLlmStatus status = new EngineLlmStatus
            {
                Enabled = settings.Enabled,
                SelectedModel = selected == null ? string.Empty : selected.Id,
                Hardware = hw,
                Recommendation = recommendation,
                DataFolder = DataRoot(),
                ProgressFile = ProgressFile(),
                CancelFile = CancelFile(),
                Catalog = catalog.Path,
                RuntimeName = catalog.Runtime.Name,
                RuntimeLicense = catalog.Runtime.License,
                RuntimeLicenseUrl = catalog.Runtime.LicenseUrl,
                RuntimeHomepage = catalog.Runtime.Homepage,
                RuntimeInstalled = runtime != null,
                RuntimeFlavor = runtime == null ? flavor : runtime.Flavor,
                RuntimeChoice = string.IsNullOrWhiteSpace(settings.Runtime) ? "auto" : settings.Runtime,
                RuntimeVersion = runtime == null ? string.Empty : runtime.Tag,
                RuntimeApproxMb = catalog.Runtime.ApproxSizeMb.ContainsKey(flavor) ? catalog.Runtime.ApproxSizeMb[flavor] : 0,
                RuntimeSizesMb = new Dictionary<string, int>(catalog.Runtime.ApproxSizeMb, StringComparer.OrdinalIgnoreCase),
                RuntimeExtraLicenses = new Dictionary<string, string>(catalog.Runtime.ExtraAssetLicenses, StringComparer.OrdinalIgnoreCase),
                UsingLocal = UsesLocal(settings),
                LocalServer = settings.LocalServer ?? string.Empty,
                LocalModel = settings.LocalModel ?? string.Empty,
                LocalGpuLayers = settings.LocalGpuLayers ?? string.Empty,
                LocalReady = File.Exists(settings.LocalServer ?? string.Empty) && File.Exists(settings.LocalModel ?? string.Empty)
            };
            if (status.UsingLocal)
            {
                status.Ready = status.Enabled && status.LocalReady;
                foreach (ModelInfo model in catalog.Models)
                    status.Models.Add(ModelStatus(model, recommendation));
                return status;
            }
            foreach (ModelInfo model in catalog.Models)
                status.Models.Add(ModelStatus(model, recommendation));
            EngineLlmModelStatus chosen = status.Models.FirstOrDefault(m => m.Id == status.SelectedModel);
            status.Ready = status.Enabled && chosen != null && chosen.Installed && status.RuntimeInstalled;
            return status;
        }

        private static EngineLlmModelStatus ModelStatus(ModelInfo model, Recommendation recommendation)
        {
            return new EngineLlmModelStatus
            {
                Id = model.Id,
                Label = model.Label,
                Tier = model.Tier,
                Parameters = model.Parameters,
                Quantization = model.Quantization,
                SizeBytes = model.SizeBytes,
                Size = FormatBytes(model.SizeBytes),
                License = model.License,
                LicenseUrl = model.LicenseUrl,
                Source = model.Source,
                Installed = IsModelInstalled(model),
                Recommended = string.Equals(model.Id, recommendation.ModelId, StringComparison.OrdinalIgnoreCase),
                Path = ModelPath(model)
            };
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "?";
            if (bytes >= GiB)
                return (bytes / (double)GiB).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            return (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        public static string ToText(EngineLlmStatus status)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Local AI model (Group by description)");
            sb.AppendLine("Enabled: " + status.Enabled);
            sb.AppendLine("Ready: " + status.Ready);
            sb.AppendLine("Selected Model: " + (status.SelectedModel.Length > 0 ? status.SelectedModel : "-"));
            sb.AppendLine("Hardware (" + status.Hardware.Source + "): " + (status.Hardware.RamBytes / (double)GiB).ToString("0.#", CultureInfo.InvariantCulture) + " GB RAM, " +
                          status.Hardware.PhysicalCores.ToString(CultureInfo.InvariantCulture) + " cores" + (status.Hardware.Avx2 ? " (AVX2)" : " (no AVX2)") +
                          (status.Hardware.CpuName.Length > 0 ? ", " + status.Hardware.CpuName : string.Empty));
            foreach (Gpu gpu in status.Hardware.Gpus)
                sb.AppendLine("  GPU: " + gpu.Name + " (" + gpu.Vendor + ", " + FormatBytes(gpu.VramBytes) + (gpu.Dedicated ? ", dedicated" : ", shared") + ")");
            sb.AppendLine("Recommended: " + (status.Recommendation.ModelId.Length > 0 ? status.Recommendation.ModelId : "none") + " [" + status.Recommendation.Tier + "] on " +
                          (status.Recommendation.GpuLayers > 0 ? "GPU (" + status.Recommendation.Runtime + ")" : "CPU") + ", " + status.Recommendation.Threads.ToString(CultureInfo.InvariantCulture) + " threads");
            sb.AppendLine("Why: " + status.Recommendation.Reason);
            sb.AppendLine("Runtime: " + status.RuntimeName + " " + status.RuntimeFlavor + (status.RuntimeInstalled ? " installed " + status.RuntimeVersion : " not downloaded (about " + status.RuntimeApproxMb.ToString(CultureInfo.InvariantCulture) + " MB)") +
                          ", license " + status.RuntimeLicense + " " + status.RuntimeLicenseUrl);
            if (status.UsingLocal || status.LocalServer.Length > 0)
            {
                sb.AppendLine("Your llama.cpp: " + (status.LocalServer.Length > 0 ? status.LocalServer : "-") + (status.UsingLocal ? " (in use)" : " (not in use)"));
                sb.AppendLine("Your model: " + (status.LocalModel.Length > 0 ? status.LocalModel : "-") + (status.LocalReady ? string.Empty : " (file missing)") +
                              (status.LocalGpuLayers.Length > 0 ? ", GPU layers " + status.LocalGpuLayers : ", GPU layers chosen by llama.cpp"));
            }
            sb.AppendLine("Models:");
            foreach (EngineLlmModelStatus model in status.Models)
            {
                sb.AppendLine("  - " + model.Id + " [" + model.Tier + "] " + model.Size + ", " + model.License + (model.Installed ? ", installed" : string.Empty) +
                              (model.Recommended ? ", recommended" : string.Empty) + (model.Id == status.SelectedModel ? ", selected" : string.Empty));
                sb.AppendLine("      " + model.Label + "  " + model.Source + "  license: " + model.LicenseUrl);
            }
            sb.AppendLine("Data Folder: " + status.DataFolder);
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------
        // Download (explicit consent, resumable, SHA-256 verified)
        // ------------------------------------------------------------------------------------------------------

        public sealed class DownloadProgress
        {
            public string State { get; set; }
            public string Item { get; set; }
            public long BytesDone { get; set; }
            public long BytesTotal { get; set; }
            public double Percent { get; set; }
            public string Message { get; set; }

            public DownloadProgress()
            {
                State = "idle";
                Item = string.Empty;
                Message = string.Empty;
            }
        }

        private sealed class CancelledException : Exception
        {
            public CancelledException() : base("Download cancelled; the part already downloaded is kept and the next download resumes it.") { }
        }

        private static void WriteProgress(DownloadProgress progress)
        {
            try
            {
                Directory.CreateDirectory(DataRoot());
                string temp = ProgressFile() + ".tmp";
                File.WriteAllText(temp, EngineSerialization.ToJson(progress), new UTF8Encoding(false));
                if (File.Exists(ProgressFile()))
                    File.Delete(ProgressFile());
                File.Move(temp, ProgressFile());
            }
            catch
            {
            }
        }

        public static string ConsentText(ModelInfo model, Catalog catalog, string flavor, bool runtimeNeeded)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(model.Label + ": " + FormatBytes(model.SizeBytes) + " from " + model.Source + ", license " + model.License + " (" + model.LicenseUrl + ")");
            if (runtimeNeeded)
            {
                int mb;
                catalog.Runtime.ApproxSizeMb.TryGetValue(flavor, out mb);
                sb.Append("; " + catalog.Runtime.Name + " " + flavor + " build (about " + mb.ToString(CultureInfo.InvariantCulture) + " MB) from " + catalog.Runtime.Homepage +
                          ", license " + catalog.Runtime.License + " (" + catalog.Runtime.LicenseUrl + ")");
                string extraLicense;
                if (catalog.Runtime.ExtraAssetLicenses.TryGetValue(flavor, out extraLicense) && !string.IsNullOrWhiteSpace(extraLicense))
                    sb.Append(", plus " + extraLicense);
            }
            return sb.ToString();
        }

        public static EngineLlmDownloadResult Download(string modelId, string flavor, bool accepted, bool skipRuntime)
        {
            EngineLlmDownloadResult result = new EngineLlmDownloadResult();
            Catalog catalog = LoadCatalog();
            Hardware hw = DetectHardware();
            Recommendation recommendation = Recommend(hw, catalog);
            Settings settings = LoadSettings();
            ModelInfo model = string.IsNullOrWhiteSpace(modelId)
                ? SelectedModel(catalog, settings, recommendation)
                : catalog.Models.FirstOrDefault(m => string.Equals(m.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (model == null)
            {
                result.Errors.Add("Unknown model '" + modelId + "'. Models: " + string.Join(", ", catalog.Models.Select(m => m.Id)) + ".");
                return result;
            }
            if (string.IsNullOrWhiteSpace(model.Url) || string.IsNullOrWhiteSpace(model.Sha256))
            {
                result.Errors.Add("Model " + model.Id + " has no download address or checksum in " + catalog.Path + ".");
                return result;
            }
            bool explicitFlavor = !string.IsNullOrWhiteSpace(flavor) && flavor.Trim().ToLowerInvariant() != "auto";
            string runtimeFlavor = explicitFlavor ? flavor.Trim().ToLowerInvariant() : RuntimeFlavor(settings, recommendation);
            if (!catalog.Runtime.Assets.ContainsKey(runtimeFlavor))
            {
                result.Errors.Add("Unknown runtime '" + runtimeFlavor + "' (use " + string.Join(", ", catalog.Runtime.Assets.Keys) + ").");
                return result;
            }
            RuntimeManifest installedRuntime = InstalledRuntime(runtimeFlavor);
            bool runtimeNeeded = !skipRuntime && (installedRuntime == null || !string.Equals(installedRuntime.Flavor, runtimeFlavor, StringComparison.OrdinalIgnoreCase));
            result.Model = model.Id;
            result.Consent = ConsentText(model, catalog, runtimeFlavor, runtimeNeeded);
            if (!accepted)
            {
                result.Errors.Add("Downloading needs your consent. " + result.Consent + ". Run again with --accept-license.");
                return result;
            }

            try
            {
                if (File.Exists(CancelFile()))
                    File.Delete(CancelFile());
            }
            catch
            {
            }
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
            }
            catch
            {
            }

            DownloadProgress progress = new DownloadProgress { State = "downloading" };
            try
            {
                if (runtimeNeeded)
                {
                    result.Runtime = InstallRuntime(catalog, runtimeFlavor, progress, result);
                }
                else if (installedRuntime != null)
                {
                    result.Runtime = installedRuntime.Flavor + " " + installedRuntime.Tag + " (already installed)";
                }

                string target = ModelPath(model);
                if (IsModelInstalled(model))
                {
                    result.Messages.Add("Model " + model.Id + " is already downloaded and verified.");
                }
                else
                {
                    progress.Item = model.Label;
                    DownloadFile(model.Url, target, model.Sha256, model.SizeBytes, progress, result);
                    File.WriteAllText(target + ".sha256", model.Sha256, new UTF8Encoding(false));
                    result.Messages.Add("Model " + model.Id + " downloaded and verified (SHA-256 " + model.Sha256 + ").");
                }
                settings.ModelId = model.Id;
                settings.Enabled = true;
                if (explicitFlavor)
                    settings.Runtime = runtimeFlavor;
                SaveSettings(settings);
                result.Path = target;
                progress.State = "done";
                progress.Message = "Ready.";
                progress.Percent = 100;
                WriteProgress(progress);
            }
            catch (CancelledException ex)
            {
                progress.State = "cancelled";
                progress.Message = ex.Message;
                WriteProgress(progress);
                result.Errors.Add(ex.Message);
            }
            catch (Exception ex)
            {
                progress.State = "failed";
                progress.Message = ex.Message;
                WriteProgress(progress);
                result.Errors.Add("Download failed: " + ex.Message);
            }
            return result;
        }

        private static string InstallRuntime(Catalog catalog, string flavor, DownloadProgress progress, EngineLlmDownloadResult result)
        {
            string pattern = catalog.Runtime.Assets[flavor];
            progress.Item = catalog.Runtime.Name + " (" + flavor + ")";
            progress.Message = "Looking up the current llama.cpp release...";
            WriteProgress(progress);
            string extraPattern;
            catalog.Runtime.ExtraAssets.TryGetValue(flavor, out extraPattern);
            Dictionary<string, object> release = FindRuntimeRelease(catalog.Runtime.ReleaseApi, pattern, extraPattern);
            string tag = EngineNlGroup.Text(release, "tag_name");
            string folder = Path.Combine(RuntimeFolder(), flavor);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            string name, sha;
            DownloadAndUnpack(release, tag, pattern, folder, progress, result, out name, out sha);
            // CUDA: the CUDA runtime DLLs come as a second archive of the same CUDA version.
            string extra;
            if (catalog.Runtime.ExtraAssets.TryGetValue(flavor, out extra) && !string.IsNullOrWhiteSpace(extra))
            {
                Match cuda = Regex.Match(name, @"cuda-([0-9.]+)", RegexOptions.IgnoreCase);
                if (cuda.Success)
                    extra = extra.Replace("cuda-*", "cuda-" + cuda.Groups[1].Value);
                string extraName, extraSha;
                DownloadAndUnpack(release, tag, extra, folder, progress, result, out extraName, out extraSha);
            }
            string exe = Directory.GetFiles(folder, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null)
                throw new InvalidOperationException(name + " has no llama-server.exe.");
            RuntimeManifest manifest = new RuntimeManifest { Flavor = flavor, Tag = tag, Asset = name, Sha256 = sha, Exe = exe };
            File.WriteAllText(Path.Combine(folder, "runtime.json"), EngineSerialization.ToJson(manifest), new UTF8Encoding(false));
            result.Messages.Add("llama.cpp " + tag + " (" + flavor + ") downloaded and verified (SHA-256 " + sha + ").");
            return flavor + " " + tag;
        }

        // The newest llama.cpp release with the flavor's archives. GitHub's "latest" is not enough: since October 2026 it
        // points to a tool release (v0.6.0, only nightly-tag.txt), and the builds (b<number>) are marked pre-release, so
        // only drafts are skipped.
        private static Dictionary<string, object> FindRuntimeRelease(string releaseApi, string pattern, string extraPattern)
        {
            const string latest = "/releases/latest";
            string listUrl = releaseApi.EndsWith(latest, StringComparison.OrdinalIgnoreCase)
                ? releaseApi.Substring(0, releaseApi.Length - latest.Length) + "/releases?per_page=30"
                : releaseApi;
            object parsed = EngineSerialization.FromJson<object>(HttpText(listUrl));
            List<Dictionary<string, object>> releases = parsed is Dictionary<string, object> single
                ? new List<Dictionary<string, object>> { single }
                : EngineNlGroup.Objects(parsed).ToList();
            foreach (Dictionary<string, object> release in releases)
            {
                if (IsTrue(EngineNlGroup.Get(release, "draft")))
                    continue;
                List<string> names = EngineNlGroup.Objects(EngineNlGroup.Get(release, "assets")).Select(a => EngineNlGroup.Text(a, "name")).ToList();
                if (names.Any(n => WildcardMatches(pattern, n)) && (string.IsNullOrWhiteSpace(extraPattern) || names.Any(n => WildcardMatches(extraPattern, n))))
                    return release;
            }
            throw new InvalidOperationException("None of the " + releases.Count + " newest llama.cpp releases has an asset like " + pattern + ".");
        }

        private static bool IsTrue(object value)
        {
            return value is bool flag && flag;
        }

        private static bool WildcardMatches(string pattern, string name)
        {
            return new Regex("^" + Regex.Escape(pattern).Replace("\\*", "[^/]*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).IsMatch(name ?? string.Empty);
        }

        // One release asset: SHA-256 from the release metadata (refused without), download, check, unpack (no paths
        // outside the folder), delete the archive.
        private static void DownloadAndUnpack(Dictionary<string, object> release, string tag, string pattern, string folder, DownloadProgress progress, EngineLlmDownloadResult result, out string name, out string sha)
        {
            Regex wildcard = new Regex("^" + Regex.Escape(pattern).Replace("\\*", "[^/]*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Dictionary<string, object> asset = EngineNlGroup.Objects(EngineNlGroup.Get(release, "assets")).FirstOrDefault(a => wildcard.IsMatch(EngineNlGroup.Text(a, "name")));
            if (asset == null)
                throw new InvalidOperationException("The llama.cpp release " + tag + " has no asset like " + pattern + ".");
            name = EngineNlGroup.Text(asset, "name");
            string url = EngineNlGroup.Text(asset, "browser_download_url");
            string digest = EngineNlGroup.Text(asset, "digest");
            long size;
            long.TryParse(EngineNlGroup.Text(asset, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 7 + 64)
                throw new InvalidOperationException("The llama.cpp release " + tag + " publishes no SHA-256 for " + name + "; not downloaded.");
            sha = digest.Substring(7).ToLowerInvariant();

            string zip = Path.Combine(RuntimeFolder(), name);
            Directory.CreateDirectory(RuntimeFolder());
            DownloadFile(url, zip, sha, size, progress, result);

            progress.Message = "Unpacking " + name + "...";
            WriteProgress(progress);
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unsafe path in " + name + ": " + entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    entry.ExtractToFile(destination, true);
                }
            }
            File.Delete(zip);
        }

        private static HttpWebRequest Request(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "JoyPro";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            request.AllowAutoRedirect = true;
            Uri uri = new Uri(url);
            if (uri.IsLoopback)
                request.Proxy = null;
            return request;
        }

        private static string HttpText(string url)
        {
            HttpWebRequest request = Request(url);
            request.Accept = "application/vnd.github+json, application/json";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        // Resumes from <file>.part (HTTP Range), checks size and SHA-256, then moves the file in place. A cancel file
        // stops it between chunks and keeps the part.
        internal static void DownloadFile(string url, string target, string sha256, long expectedSize, DownloadProgress progress, EngineLlmDownloadResult result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string part = target + ".part";
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (expectedSize > 0 && have > expectedSize)
            {
                File.Delete(part);
                have = 0;
            }
            if (expectedSize <= 0 || have < expectedSize)
            {
                HttpWebRequest request = Request(url);
                if (have > 0)
                    request.AddRange(have);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                    if (have > 0 && !resumed)
                        have = 0;
                    if (resumed)
                        result.Messages.Add("Resumed " + Path.GetFileName(target) + " at " + FormatBytes(have) + ".");
                    long total = response.ContentLength >= 0 ? have + response.ContentLength : expectedSize;
                    progress.BytesTotal = total;
                    progress.BytesDone = have;
                    using (Stream input = response.GetResponseStream())
                    using (FileStream output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1 << 20];
                        Stopwatch tick = Stopwatch.StartNew();
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            progress.BytesDone += read;
                            if (tick.ElapsedMilliseconds >= 250)
                            {
                                tick.Restart();
                                progress.Percent = total > 0 ? Math.Round(progress.BytesDone * 100.0 / total, 1) : 0;
                                progress.Message = Path.GetFileName(target) + ": " + FormatBytes(progress.BytesDone) + " of " + FormatBytes(total);
                                WriteProgress(progress);
                                if (File.Exists(CancelFile()))
                                    throw new CancelledException();
                            }
                        }
                    }
                }
            }
            long length = new FileInfo(part).Length;
            if (expectedSize > 0 && length != expectedSize)
                throw new InvalidOperationException(Path.GetFileName(target) + " has " + length.ToString(CultureInfo.InvariantCulture) + " bytes, expected " + expectedSize.ToString(CultureInfo.InvariantCulture) + " (download again).");
            progress.State = "verifying";
            progress.Message = "Checking SHA-256 of " + Path.GetFileName(target) + "...";
            WriteProgress(progress);
            string actual = Sha256Of(part);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidOperationException("SHA-256 of " + Path.GetFileName(target) + " is " + actual + ", expected " + sha256 + "; the file was deleted.");
            }
            if (File.Exists(target))
                File.Delete(target);
            File.Move(part, target);
            progress.State = "downloading";
        }

        internal static string Sha256Of(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static EngineLlmDownloadResult Remove(string modelId, bool all)
        {
            EngineLlmDownloadResult result = new EngineLlmDownloadResult();
            Catalog catalog = LoadCatalog();
            Settings settings = LoadSettings();
            IEnumerable<ModelInfo> models = all || string.IsNullOrWhiteSpace(modelId)
                ? catalog.Models
                : catalog.Models.Where(m => string.Equals(m.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!all && !string.IsNullOrWhiteSpace(modelId) && !models.Any())
            {
                result.Errors.Add("Unknown model '" + modelId + "'.");
                return result;
            }
            foreach (ModelInfo model in models.ToList())
            {
                string path = ModelPath(model);
                foreach (string file in new[] { path, path + ".part", path + ".sha256" })
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        if (file == path)
                            result.Messages.Add("Removed " + model.Id + ".");
                    }
                }
                if (string.Equals(settings.ModelId, model.Id, StringComparison.OrdinalIgnoreCase))
                    settings.ModelId = string.Empty;
            }
            if (all)
            {
                if (Directory.Exists(RuntimeFolder()))
                {
                    Directory.Delete(RuntimeFolder(), true);
                    result.Messages.Add("Removed the llama.cpp runtime.");
                }
                settings.Enabled = false;
            }
            SaveSettings(settings);
            if (result.Messages.Count == 0)
                result.Messages.Add("Nothing to remove.");
            return result;
        }

        // ------------------------------------------------------------------------------------------------------
        // A llama.cpp and GGUF the user already has (engine llm find-local / use-local): nothing is downloaded
        // ------------------------------------------------------------------------------------------------------

        // Where llama.cpp builds and GGUF files usually live: the PATH, llama.cpp/AI/models folders on the drives,
        // the Hugging Face cache, LM Studio's model folders and Downloads. Read-only, depth- and time-limited.
        // Ollama's own llama-server is internal to Ollama (its own flags), so it is skipped; its blobs carry no .gguf name.
        internal static List<string> LocalSearchRoots()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<string> roots = new List<string>();
            foreach (string drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName))
            {
                foreach (string name in new[] { "llama.cpp", "llama", "llamacpp", "AI", "LLM", "models", "tools" })
                    roots.Add(Path.Combine(drive, name));
            }
            roots.Add(Path.Combine(profile, "llama.cpp"));
            roots.Add(Path.Combine(profile, "models"));
            roots.Add(Path.Combine(profile, "Downloads"));
            roots.Add(Path.Combine(profile, ".cache", "huggingface", "hub"));
            roots.Add(Path.Combine(profile, ".cache", "lm-studio", "models"));
            roots.Add(Path.Combine(profile, ".lmstudio", "models"));
            roots.Add(Path.Combine(local, "llama.cpp"));
            roots.Add(Path.Combine(local, "Programs", "llama.cpp"));
            roots.Add(ModelsFolder());
            string extra = Environment.GetEnvironmentVariable("JOYPRO_LLM_SEARCH");
            if (!string.IsNullOrWhiteSpace(extra))
                roots.InsertRange(0, extra.Split(';').Where(p => p.Trim().Length > 0).Select(p => p.Trim()));
            return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static EngineLlmLocalScan FindLocal(Hardware hw, IEnumerable<string> rootsOverride)
        {
            EngineLlmLocalScan scan = new EngineLlmLocalScan();
            List<string> roots = rootsOverride != null ? rootsOverride.Where(Directory.Exists).ToList() : LocalSearchRoots();
            scan.Searched.AddRange(roots);
            Stopwatch time = Stopwatch.StartNew();
            HashSet<string> servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rootsOverride == null)
            {
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
                {
                    try
                    {
                        string exe = Path.Combine(dir.Trim(), "llama-server.exe");
                        if (dir.Trim().Length > 0 && File.Exists(exe))
                            servers.Add(Path.GetFullPath(exe));
                    }
                    catch
                    {
                    }
                }
            }
            foreach (string root in roots)
                Walk(root, 0, servers, models, time);
            foreach (string server in servers.Where(s => s.IndexOf("\\ollama\\", StringComparison.OrdinalIgnoreCase) < 0).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                string folder = Path.GetDirectoryName(server) ?? string.Empty;
                string backend = File.Exists(Path.Combine(folder, "ggml-cuda.dll")) ? "cuda"
                    : File.Exists(Path.Combine(folder, "ggml-vulkan.dll")) ? "vulkan"
                    : File.Exists(Path.Combine(folder, "ggml-hip.dll")) ? "hip"
                    : File.Exists(Path.Combine(folder, "ggml-sycl.dll")) ? "sycl" : "cpu";
                scan.Servers.Add(new EngineLlmLocalServer { Path = server, Backend = backend });
            }
            Gpu gpu = hw.BestGpu;
            double vram = gpu == null ? 0 : gpu.VramBytes / (double)GiB;
            double ram = hw.RamBytes / (double)GiB;
            foreach (string model in models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                long size;
                try
                {
                    size = FileSize(model);
                }
                catch
                {
                    continue;
                }
                double gb = size / (double)GiB;
                scan.Models.Add(new EngineLlmLocalModel
                {
                    Path = model,
                    Name = Path.GetFileName(model),
                    SizeBytes = size,
                    Size = FormatBytes(size),
                    FitsGpu = vram > 0 && gb * 1.1 + 1.0 <= vram,
                    FitsRam = gb * 1.2 + 2.0 <= ram
                });
            }
            // A model that fits completely on the graphics card answers in seconds: the largest of those. Else the
            // smallest one that fits the memory (a big model on the CPU takes minutes for every request).
            EngineLlmLocalModel best = scan.Models.Where(m => m.FitsGpu).OrderByDescending(m => m.SizeBytes).FirstOrDefault()
                                       ?? scan.Models.Where(m => m.FitsRam && m.SizeBytes >= 300L * 1024 * 1024).OrderBy(m => m.SizeBytes).FirstOrDefault()
                                       ?? scan.Models.OrderBy(m => m.SizeBytes).FirstOrDefault();
            string wanted = gpu == null ? "cpu" : "gpu";
            EngineLlmLocalServer bestServer = scan.Servers.OrderBy(s => wanted == "gpu" ? (s.Backend == "cpu" ? 1 : 0) : 0).ThenBy(s => s.Path.Length).FirstOrDefault();
            if (best != null)
            {
                best.Recommended = true;
                scan.BestModel = best.Path;
                scan.Reason = best.FitsGpu
                    ? best.Name + " (" + best.Size + ") fits completely on the " + gpu.Name + " (" + vram.ToString("0.#", CultureInfo.InvariantCulture) + " GB)."
                    : best.Name + " (" + best.Size + ") is the smallest of your models that fits the memory; it runs partly or fully on the CPU.";
            }
            if (bestServer != null)
            {
                bestServer.Recommended = true;
                scan.BestServer = bestServer.Path;
            }
            scan.Seconds = Math.Round(time.Elapsed.TotalSeconds, 1);
            return scan;
        }

        private static void Walk(string folder, int depth, HashSet<string> servers, HashSet<string> models, Stopwatch time)
        {
            if (depth > 7 || time.Elapsed.TotalSeconds > 25)
                return;
            try
            {
                DirectoryInfo info = new DirectoryInfo(folder);
                if (depth > 0 && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    return;
                foreach (FileInfo file in info.EnumerateFiles())
                {
                    if (string.Equals(file.Name, "llama-server.exe", StringComparison.OrdinalIgnoreCase))
                        servers.Add(file.FullName);
                    else if (file.Extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase) && IsUsableGguf(file))
                        models.Add(file.FullName);
                }
                foreach (DirectoryInfo sub in info.EnumerateDirectories())
                {
                    string name = sub.Name;
                    if (name.StartsWith(".", StringComparison.Ordinal) && depth > 0 || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || name.Equals("blobs", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Walk(sub.FullName, depth + 1, servers, models, time);
                }
            }
            catch
            {
            }
        }

        // Language models only: no vision projectors (mmproj), vocab-only or tiny files; of a split model the first part.
        private static bool IsUsableGguf(FileInfo file)
        {
            string name = file.Name.ToLowerInvariant();
            if (name.Contains("mmproj") || name.Contains("vocab") || name.Contains("embed") || name.Contains("rerank"))
                return false;
            Match split = Regex.Match(name, @"-(\d{5})-of-(\d{5})\.gguf$");
            if (split.Success && split.Groups[1].Value != "00001")
                return false;
            return FileSize(file.FullName) >= 50L * 1024 * 1024;
        }

        // The size of the file a path stands for (a Hugging Face cache entry may be a symbolic link to its blob).
        internal static long FileSize(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length > 0 || (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    return info.Length;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return stream.Length;
            }
            catch
            {
                return 0;
            }
        }

        // Uses the user's llama-server.exe and GGUF instead of a download (switches the model on).
        public static EngineLlmDownloadResult UseLocal(string server, string model, string gpuLayers, int contextSize, string template)
        {
            EngineLlmDownloadResult result = new EngineLlmDownloadResult();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(model))
            {
                EngineLlmLocalScan scan = FindLocal(DetectHardware(), null);
                server = string.IsNullOrWhiteSpace(server) ? scan.BestServer : server;
                model = string.IsNullOrWhiteSpace(model) ? scan.BestModel : model;
                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(model))
                {
                    result.Errors.Add("No " + (string.IsNullOrWhiteSpace(server) ? "llama-server.exe" : "GGUF model") + " found (searched " + string.Join(", ", scan.Searched) +
                                      "). Name them: engine llm use-local --server <llama-server.exe> --model <file.gguf>.");
                    return result;
                }
                result.Messages.Add("Found " + server + " and " + model + ".");
            }
            server = Path.GetFullPath(server.Trim().Trim('"'));
            model = Path.GetFullPath(model.Trim().Trim('"'));
            if (Directory.Exists(server))
                server = Path.Combine(server, "llama-server.exe");
            if (!File.Exists(server) || !server.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("llama-server.exe not found: " + server);
                return result;
            }
            if (!File.Exists(model) || !model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("GGUF model not found: " + model);
                return result;
            }
            string layers = (gpuLayers ?? string.Empty).Trim().ToLowerInvariant();
            int layerCount;
            if (layers.Length > 0 && layers != "auto" && layers != "all" && !int.TryParse(layers, NumberStyles.Integer, CultureInfo.InvariantCulture, out layerCount))
            {
                result.Errors.Add("Invalid --gpu-layers value: " + gpuLayers + " (auto, all or a number).");
                return result;
            }
            string chat = (template ?? string.Empty).Trim().ToLowerInvariant();
            if (chat.Length > 0 && chat != "server" && chat != "chatml")
            {
                result.Errors.Add("Invalid --template value: " + template + " (server or chatml).");
                return result;
            }
            Settings settings = LoadSettings();
            settings.LocalServer = server;
            settings.LocalModel = model;
            settings.LocalGpuLayers = layers == "auto" ? string.Empty : layers;
            settings.LocalContextSize = contextSize > 0 ? contextSize : 0;
            settings.LocalTemplate = chat;
            settings.ModelId = LocalModelId;
            settings.Enabled = true;
            SaveSettings(settings);
            result.Model = LocalModelId;
            result.Path = model;
            result.Runtime = server;
            result.Messages.Add("Using your llama.cpp (" + server + ") with " + Path.GetFileName(model) + " (" + FormatBytes(FileSize(model)) + "); the local AI model is on. Nothing was downloaded.");
            return result;
        }

        public static EngineLlmDownloadResult ClearLocal()
        {
            EngineLlmDownloadResult result = new EngineLlmDownloadResult();
            Settings settings = LoadSettings();
            bool wasLocal = UsesLocal(settings);
            settings.LocalServer = settings.LocalModel = settings.LocalGpuLayers = settings.LocalTemplate = string.Empty;
            settings.LocalContextSize = 0;
            if (wasLocal)
            {
                settings.ModelId = string.Empty;
                settings.Enabled = false;
            }
            SaveSettings(settings);
            result.Messages.Add("JoyPro no longer uses your own llama.cpp / GGUF" + (wasLocal ? "; the local AI model is off." : "."));
            return result;
        }

        public static string ToText(EngineLlmLocalScan scan)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Local llama.cpp and GGUF models (" + scan.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
            sb.AppendLine("llama-server.exe:");
            foreach (EngineLlmLocalServer server in scan.Servers)
                sb.AppendLine("  - " + server.Path + " [" + server.Backend + "]" + (server.Recommended ? " (recommended)" : string.Empty));
            if (scan.Servers.Count == 0)
                sb.AppendLine("  none");
            sb.AppendLine("Models:");
            foreach (EngineLlmLocalModel model in scan.Models)
                sb.AppendLine("  - " + model.Path + " (" + model.Size + (model.FitsGpu ? ", fits the GPU" : model.FitsRam ? ", CPU/partial GPU" : ", too big for this PC") + ")" + (model.Recommended ? " (recommended)" : string.Empty));
            if (scan.Models.Count == 0)
                sb.AppendLine("  none");
            if (scan.Reason.Length > 0)
                sb.AppendLine("Why: " + scan.Reason);
            sb.AppendLine("Searched: " + string.Join("; ", scan.Searched));
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------
        // Runner: one llama-server process per request
        // ------------------------------------------------------------------------------------------------------

        public sealed class CallResult
        {
            public string Content = string.Empty;
            public string Error = string.Empty;
            public int ProcessId;
            public bool ProcessExited;
            public long DurationMs;
            public long LoadMs;
        }

        // ChatML (Qwen2.5 / Qwen3) for the catalog models: system, few-shot turns, the request. Qwen3 answers in
        // non-thinking mode: "/no_think" and an empty think block (the grammar would not let it think anyway).
        internal static string WrapPrompt(ModelInfo model, IList<Message> messages)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                Message message = messages[i];
                bool last = i == messages.Count - 1;
                sb.Append("<|im_start|>").Append(message.Role).Append('\n').Append(message.Content);
                if (last && model.NoThink && message.Role == "user")
                    sb.Append(" /no_think");
                sb.Append("<|im_end|>\n");
            }
            sb.Append("<|im_start|>assistant\n");
            if (model.NoThink)
                sb.Append("<think>\n\n</think>\n\n");
            return sb.ToString();
        }

        internal static string WrapPrompt(ModelInfo model, string system, string user)
        {
            return WrapPrompt(model, new List<Message> { new Message { Role = "system", Content = system }, new Message { Role = "user", Content = user } });
        }

        // Greedy and repeatable: temperature 0, top-k 1, fixed seed, no repetition penalty (ids and brackets repeat on
        // purpose; older llama.cpp builds default to 1.1), no prompt cache, the answer shaped by the GBNF grammar.
        internal static Dictionary<string, object> RequestBody(ModelInfo model, IList<Message> messages, string grammar)
        {
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                { "grammar", grammar },
                { "temperature", model.Temperature },
                { "top_k", 1 },
                { "top_p", 1.0 },
                { "min_p", 0.0 },
                { "repeat_penalty", 1.0 },
                { "presence_penalty", 0.0 },
                { "frequency_penalty", 0.0 },
                { "seed", 7 },
                { "cache_prompt", false }
            };
            if (UsesServerTemplate(model))
            {
                // The server applies the GGUF's own chat template; thinking off where the template knows the switch.
                body["messages"] = messages.Select(m => new Dictionary<string, object> { { "role", m.Role }, { "content", m.Content } }).ToList();
                body["max_tokens"] = model.MaxTokens;
                body["stream"] = false;
                body["chat_template_kwargs"] = new Dictionary<string, object> { { "enable_thinking", false } };
            }
            else
            {
                body["prompt"] = WrapPrompt(model, messages);
                body["n_predict"] = model.MaxTokens;
                body["stop"] = new[] { "<|im_end|>", "<|endoftext|>" };
            }
            return body;
        }

        internal static bool UsesServerTemplate(ModelInfo model)
        {
            return string.Equals(model.Template, "server", StringComparison.OrdinalIgnoreCase);
        }

        public static CallResult Complete(ModelInfo model, string system, string user, string grammar)
        {
            return Complete(model, new List<Message> { new Message { Role = "system", Content = system }, new Message { Role = "user", Content = user } }, grammar);
        }

        // One request: start the model, answer, stop it.
        public static CallResult Complete(ModelInfo model, IList<Message> messages, string grammar)
        {
            Stopwatch watch = Stopwatch.StartNew();
            CallResult call;
            Session session = Session.Start(model, 0);
            try
            {
                call = session.Error.Length > 0 ? new CallResult { Error = session.Error } : session.Ask(messages, grammar, model.MaxTokens, false);
                call.ProcessId = session.ProcessId;
                call.LoadMs = session.LoadMs;
            }
            finally
            {
                session.Dispose();
            }
            call.ProcessExited = session.ProcessExited;
            watch.Stop();
            call.DurationMs = watch.ElapsedMilliseconds;
            return call;
        }

        // A model process that answers several requests (the relation assistant's tool loop: one process per turn, not
        // per step) and is killed by Dispose. The job object kills it with the engine, too. Start never throws: a model
        // that does not load leaves Error set (and its process stopped).
        public sealed class Session : IDisposable
        {
            private Process process;
            private IntPtr job = IntPtr.Zero;
            private readonly Queue<string> lastLines = new Queue<string>();
            private readonly object gate = new object();
            private string baseUrl = string.Empty;
            private ServerState warm;
            private bool reused;

            public ModelInfo Model { get; private set; }
            public bool Reused { get { return reused; } }
            public string Error { get; private set; }
            public int ProcessId { get; private set; }
            public bool ProcessExited { get; private set; }
            public long LoadMs { get; private set; }
            public int ContextSize { get; private set; }
            public int Requests { get; private set; }

            private Session()
            {
                Error = string.Empty;
            }

            // contextSize 0: the model's own; larger values only for the user's own llama.cpp or a catalog model run
            // with the assistant's bigger history.
            public static Session Start(ModelInfo model, int contextSize)
            {
                Session session = new Session { Model = model, ContextSize = contextSize > 0 ? contextSize : model.ContextSize };
                try
                {
                    session.warm = WarmSettings();
                    if (session.warm == null || !session.Reuse())
                        session.Launch();
                }
                catch (Exception ex)
                {
                    session.Error = ex.Message;
                    session.Stop();
                }
                return session;
            }

            // The warm server of an earlier request when it runs the same model with the same context.
            private bool Reuse()
            {
                ServerState state = ReadServerState();
                Process running = OpenServerProcess(state);
                if (running == null)
                    return false;
                string url = "http://127.0.0.1:" + state.Port.ToString(CultureInfo.InvariantCulture);
                if (state.Key != ServerKey(Model, ContextSize) || IsAlive(state.BusyPid) && state.BusyPid != Process.GetCurrentProcess().Id || !Healthy(url))
                {
                    running.Dispose();
                    if (!IsAlive(state.BusyPid))
                    {
                        int ignored;
                        StopServer(out ignored);
                    }
                    return false;
                }
                process = running;
                reused = true;
                ProcessId = state.Pid;
                baseUrl = url;
                state.BusyPid = Process.GetCurrentProcess().Id;
                state.LastUsedUtc = UtcNow();
                state.OwnerPid = warm.OwnerPid;
                state.IdleMinutes = warm.IdleMinutes;
                WriteServerState(state);
                return true;
            }

            // After the request: the warm server waits for the next one (the app stops it when idle).
            private void Release()
            {
                ServerState state = ReadServerState() ?? new ServerState();
                state.BusyPid = 0;
                state.LastUsedUtc = UtcNow();
                WriteServerState(state);
                if (process != null)
                    process.Dispose();
                process = null;
                ProcessExited = false;
            }

            private void Launch()
            {
                ModelInfo model = Model;
                // A warm server of another model or context goes first (one model in memory at a time).
                if (warm != null)
                {
                    ServerState previous = ReadServerState();
                    if (previous != null && IsAlive(previous.BusyPid) && previous.BusyPid != Process.GetCurrentProcess().Id)
                    {
                        warm = null; // another request uses it: this one runs its own server as before
                    }
                    else
                    {
                        int ignored;
                        StopServer(out ignored);
                    }
                }
                string fake = Environment.GetEnvironmentVariable(FakeResponseVariable);
                int port = FreePort();
                ProcessStartInfo start;
                if (!string.IsNullOrWhiteSpace(fake))
                {
                    List<string> fakeArgs = new List<string> { "llm", "fake-server", "--port", port.ToString(CultureInfo.InvariantCulture), "--response", fake, "--lifetime", "300" };
                    string log = Environment.GetEnvironmentVariable(FakeLogVariable);
                    if (!string.IsNullOrWhiteSpace(log))
                    {
                        fakeArgs.Add("--log");
                        fakeArgs.Add(log);
                    }
                    start = new ProcessStartInfo(typeof(EngineLlm).Assembly.Location, JoinArgs(fakeArgs));
                }
                else if (model.Local)
                {
                    // The user's own llama.cpp: only flags every llama-server knows; llama.cpp fits the GPU layers itself
                    // unless the user set them.
                    if (!File.Exists(model.ServerPath) || !File.Exists(model.LocalModelPath))
                        throw new FileNotFoundException("your llama-server.exe or GGUF file is missing");
                    List<string> args = new List<string>
                    {
                        "-m", model.LocalModelPath,
                        "--host", "127.0.0.1",
                        "--port", port.ToString(CultureInfo.InvariantCulture),
                        "-c", ContextSize.ToString(CultureInfo.InvariantCulture),
                        "-np", "1"
                    };
                    if (!string.IsNullOrWhiteSpace(model.GpuLayers) && !string.Equals(model.GpuLayers.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Add("-ngl");
                        args.Add(model.GpuLayers.Trim());
                    }
                    start = new ProcessStartInfo(model.ServerPath, JoinArgs(args));
                    start.WorkingDirectory = Path.GetDirectoryName(model.ServerPath);
                }
                else
                {
                    Hardware hw = DetectHardware();
                    Recommendation recommendation = Recommend(hw, LoadCatalog());
                    RuntimeManifest runtime = InstalledRuntime(RuntimeFlavor(LoadSettings(), recommendation));
                    if (runtime == null)
                        throw new InvalidOperationException("llama.cpp runtime missing");
                    Gpu best = hw.BestGpu;
                    bool gpuBuild = string.Equals(runtime.Flavor, "vulkan", StringComparison.OrdinalIgnoreCase) || string.Equals(runtime.Flavor, "cuda", StringComparison.OrdinalIgnoreCase);
                    bool gpu = gpuBuild && best != null && best.VramBytes / (double)GiB >= model.VramGb;
                    List<string> args = new List<string>
                    {
                        "-m", ModelPath(model),
                        "--host", "127.0.0.1",
                        "--port", port.ToString(CultureInfo.InvariantCulture),
                        "-c", ContextSize.ToString(CultureInfo.InvariantCulture),
                        "-np", "1",
                        "-t", recommendation.Threads.ToString(CultureInfo.InvariantCulture),
                        "-ngl", gpu ? "99" : "0"
                    };
                    args.AddRange(model.ServerArgs);
                    start = new ProcessStartInfo(runtime.Exe, JoinArgs(args));
                    start.WorkingDirectory = Path.GetDirectoryName(runtime.Exe);
                }
                // A warm server outlives this engine and must not inherit its handles (the app reads the engine's
                // output to the end and would wait for the server): started by the shell, hidden, no output capture.
                bool detached = warm != null;
                start.UseShellExecute = detached;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardOutput = !detached;
                start.RedirectStandardError = !detached;
                start.RedirectStandardInput = !detached;

                DataReceivedEventHandler collect = (sender, e) =>
                {
                    if (e.Data == null)
                        return;
                    lock (gate)
                    {
                        lastLines.Enqueue(e.Data);
                        while (lastLines.Count > 8)
                            lastLines.Dequeue();
                    }
                };
                process = new Process { StartInfo = start };
                if (!detached)
                {
                    process.OutputDataReceived += collect;
                    process.ErrorDataReceived += collect;
                }
                process.Start();
                ProcessId = process.Id;
                // A warm server is the app's then (its job, idle stop, Stop now).
                if (!detached)
                {
                    job = KillOnCloseJob(process);
                    process.StandardInput.Close();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }

                baseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                Stopwatch loading = Stopwatch.StartNew();
                while (true)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException("the model process ended while loading (" + LastLines(lastLines, gate) + ")");
                    if (loading.Elapsed.TotalSeconds > Math.Max(5, model.LoadTimeoutSeconds))
                        throw new TimeoutException("the model did not load within " + model.LoadTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " s");
                    if (Healthy(baseUrl))
                        break;
                    Thread.Sleep(200);
                }
                LoadMs = loading.ElapsedMilliseconds;
                if (warm != null)
                {
                    WriteServerState(new ServerState
                    {
                        Pid = process.Id,
                        Port = port,
                        Key = ServerKey(Model, ContextSize),
                        Model = Model.Label.Length > 0 ? Model.Label : Model.Id,
                        OwnerPid = warm.OwnerPid,
                        StartedUtc = process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        LastUsedUtc = UtcNow(),
                        BusyPid = Process.GetCurrentProcess().Id,
                        IdleMinutes = warm.IdleMinutes
                    });
                }
            }

            // One answer. maxTokens 0: the model's own; cachePrompt reuses the previous request's prefix (the tool loop's
            // growing conversation), still greedy.
            public CallResult Ask(IList<Message> messages, string grammar, int maxTokens, bool cachePrompt)
            {
                CallResult call = new CallResult { ProcessId = ProcessId, LoadMs = LoadMs };
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    if (Error.Length > 0)
                        throw new InvalidOperationException(Error);
                    if (process == null || process.HasExited)
                        throw new InvalidOperationException("the model process is not running (" + LastLines(lastLines, gate) + ")");
                    Requests++;
                    bool chat = UsesServerTemplate(Model);
                    Dictionary<string, object> body = RequestBody(Model, messages, grammar);
                    int tokens = maxTokens > 0 ? maxTokens : Model.MaxTokens;
                    if (chat)
                        body["max_tokens"] = tokens;
                    else
                        body["n_predict"] = tokens;
                    body["cache_prompt"] = cachePrompt;
                    HttpWebRequest request = Request(baseUrl + (chat ? "/v1/chat/completions" : "/completion"));
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = Math.Max(10, Model.RequestTimeoutSeconds) * 1000;
                    request.ReadWriteTimeout = request.Timeout;
                    byte[] payload = Encoding.UTF8.GetBytes(EngineSerialization.ToJson(body));
                    request.ContentLength = payload.Length;
                    using (Stream stream = request.GetRequestStream())
                        stream.Write(payload, 0, payload.Length);
                    string text;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        text = reader.ReadToEnd();
                    Dictionary<string, object> answer = EngineSerialization.FromJson<Dictionary<string, object>>(text);
                    if (chat)
                    {
                        Dictionary<string, object> choice = EngineNlGroup.Objects(EngineNlGroup.Get(answer, "choices")).FirstOrDefault();
                        Dictionary<string, object> message = EngineNlGroup.Get(choice, "message") as Dictionary<string, object>;
                        call.Content = EngineNlGroup.Text(message, "content");
                    }
                    else
                    {
                        call.Content = EngineNlGroup.Text(answer, "content");
                    }
                }
                catch (Exception ex)
                {
                    call.Error = ex.Message;
                }
                watch.Stop();
                call.DurationMs = watch.ElapsedMilliseconds;
                return call;
            }

            private void Stop()
            {
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                        process.WaitForExit(10000);
                    }
                    catch
                    {
                    }
                    try
                    {
                        ProcessExited = process.HasExited;
                    }
                    catch
                    {
                        ProcessExited = true;
                    }
                    process.Dispose();
                    process = null;
                }
                else
                {
                    ProcessExited = true;
                }
                if (job != IntPtr.Zero)
                {
                    CloseHandle(job);
                    job = IntPtr.Zero;
                }
            }

            public void Dispose()
            {
                if (warm != null && Error.Length == 0 && process != null && !process.HasExited)
                    Release();
                else
                    Stop();
            }
        }


        private static string LastLines(Queue<string> lines, object gate)
        {
            lock (gate)
                return lines.Count == 0 ? "no output" : string.Join(" | ", lines);
        }

        private static bool Healthy(string baseUrl)
        {
            try
            {
                HttpWebRequest request = Request(baseUrl + "/health");
                request.Timeout = 2000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        private static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        internal static string JoinArgs(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(Quote));
        }

        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return arg;
            StringBuilder sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------
        // Job object: the model process dies with the engine even if the engine is killed.
        // ------------------------------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimit
        {
            public JobBasicLimit BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static IntPtr KillOnCloseJob(Process process)
        {
            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                    return IntPtr.Zero;
                JobExtendedLimit info = new JobExtendedLimit();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf(typeof(JobExtendedLimit))) ||
                    !AssignProcessToJobObject(job, process.Handle))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
                return job;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Test stub: answers like llama-server (/health, /completion) and serves files with HTTP Range for the
        // downloader tests. Loopback only; ends after --lifetime seconds at the latest.
        // ------------------------------------------------------------------------------------------------------

        public static int RunFakeServer(int port, string portFile, string responseFile, string serveFolder, string logFile, int lifetimeSeconds)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            int actual = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!string.IsNullOrWhiteSpace(portFile))
                File.WriteAllText(portFile, actual.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
            Log(logFile, "pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + " port " + actual.ToString(CultureInfo.InvariantCulture));
            Stopwatch life = Stopwatch.StartNew();
            try
            {
                while (life.Elapsed.TotalSeconds < Math.Max(1, lifetimeSeconds))
                {
                    if (!listener.Pending())
                    {
                        Thread.Sleep(20);
                        continue;
                    }
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        try
                        {
                            Serve(client, responseFile, serveFolder, logFile);
                        }
                        catch (Exception ex)
                        {
                            Log(logFile, "error " + ex.Message);
                        }
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
            return 0;
        }

        private static int fakeRequests;

        private static void Serve(TcpClient client, string responseFile, string serveFolder, string logFile)
        {
            client.ReceiveTimeout = 10000;
            NetworkStream stream = client.GetStream();
            List<byte> head = new List<byte>();
            while (head.Count < 65536)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    break;
                head.Add((byte)b);
                int n = head.Count;
                if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
                    break;
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2)
                return;
            string method = first[0];
            string path = Uri.UnescapeDataString(first[1].Split('?')[0]);
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            byte[] body = new byte[0];
            string lengthText;
            int length;
            if (headers.TryGetValue("Content-Length", out lengthText) && int.TryParse(lengthText, out length) && length > 0)
            {
                body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int got = stream.Read(body, read, length - read);
                    if (got <= 0)
                        break;
                    read += got;
                }
            }
            Log(logFile, method + " " + path);
            if (method == "GET" && path == "/health")
            {
                Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"), null);
                return;
            }
            if (method == "POST" && (path == "/completion" || path == "/v1/chat/completions"))
            {
                Log(logFile, "body " + Encoding.UTF8.GetString(body));
                string content = !string.IsNullOrWhiteSpace(responseFile) && File.Exists(responseFile) ? File.ReadAllText(responseFile, Encoding.UTF8) : "{}";
                // A scripted conversation (the relation assistant's tool loop): answers separated by a line "#next",
                // one per request, the last one repeated.
                string[] answers = Regex.Split(content.Replace("\r\n", "\n"), "\n#next\n?");
                content = answers[Math.Min(fakeRequests, answers.Length - 1)].Trim();
                fakeRequests++;
                string json = path == "/completion"
                    ? EngineSerialization.ToJson(new Dictionary<string, object> { { "content", content }, { "stop", true } })
                    : EngineSerialization.ToJson(new Dictionary<string, object>
                    {
                        { "choices", new[] { new Dictionary<string, object> { { "index", 0 }, { "finish_reason", "stop" }, { "message", new Dictionary<string, object> { { "role", "assistant" }, { "content", content } } } } } }
                    });
                Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(json), null);
                return;
            }
            if (method == "GET" && path.StartsWith("/files/", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(serveFolder))
            {
                string name = path.Substring("/files/".Length);
                string file = Path.Combine(serveFolder, name);
                if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..") || !File.Exists(file))
                {
                    Respond(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"), null);
                    return;
                }
                byte[] data = File.ReadAllBytes(file);
                string range;
                if (headers.TryGetValue("Range", out range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    long from;
                    string spec = range.Substring(6).Split('-')[0];
                    if (long.TryParse(spec, out from) && from >= 0 && from < data.Length)
                    {
                        Log(logFile, "range " + from.ToString(CultureInfo.InvariantCulture));
                        byte[] slice = data.Skip((int)from).ToArray();
                        Respond(stream, 206, "application/octet-stream", slice,
                            "Content-Range: bytes " + from.ToString(CultureInfo.InvariantCulture) + "-" + (data.Length - 1).ToString(CultureInfo.InvariantCulture) + "/" + data.Length.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                }
                Respond(stream, 200, ServedType(name), data, null);
                return;
            }
            Respond(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"), null);
        }

        // The Content-Type of a served file (pictures and a web page for the game cover tests, docs/export.md#pictures).
        private static string ServedType(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".json": return "application/json";
                case ".jpg": return "image/jpeg";
                case ".png": return "image/png";
                case ".html": return "text/html";
                default: return "application/octet-stream";
            }
        }

        private static void Respond(NetworkStream stream, int status, string type, byte[] body, string extraHeader)
        {
            string reason = status == 200 ? "OK" : status == 206 ? "Partial Content" : "Not Found";
            StringBuilder head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
            head.Append("Content-Type: ").Append(type).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (!string.IsNullOrEmpty(extraHeader))
                head.Append(extraHeader).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static void Log(string logFile, string line)
        {
            if (string.IsNullOrWhiteSpace(logFile))
                return;
            try
            {
                File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    public sealed class EngineLlmModelStatus
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Tier { get; set; }
        public string Parameters { get; set; }
        public string Quantization { get; set; }
        public long SizeBytes { get; set; }
        public string Size { get; set; }
        public string License { get; set; }
        public string LicenseUrl { get; set; }
        public string Source { get; set; }
        public bool Installed { get; set; }
        public bool Recommended { get; set; }
        public string Path { get; set; }
    }

    internal sealed class EngineLlmStatus
    {
        public bool Enabled { get; set; }
        public bool Ready { get; set; }
        public string SelectedModel { get; set; }
        public EngineLlm.Hardware Hardware { get; set; }
        public EngineLlm.Recommendation Recommendation { get; set; }
        public List<EngineLlmModelStatus> Models { get; set; }
        public string RuntimeName { get; set; }
        public bool RuntimeInstalled { get; set; }
        public string RuntimeFlavor { get; set; }
        public string RuntimeChoice { get; set; }
        public string RuntimeVersion { get; set; }
        public int RuntimeApproxMb { get; set; }
        public Dictionary<string, int> RuntimeSizesMb { get; set; }
        // flavor -> license of the extra archive that flavor downloads (cuda: NVIDIA's CUDA EULA).
        public Dictionary<string, string> RuntimeExtraLicenses { get; set; }
        public bool UsingLocal { get; set; }
        public bool LocalReady { get; set; }
        public string LocalServer { get; set; }
        public string LocalModel { get; set; }
        public string LocalGpuLayers { get; set; }
        public string RuntimeLicense { get; set; }
        public string RuntimeLicenseUrl { get; set; }
        public string RuntimeHomepage { get; set; }
        public string DataFolder { get; set; }
        public string ProgressFile { get; set; }
        public string CancelFile { get; set; }
        public string Catalog { get; set; }

        public EngineLlmStatus()
        {
            SelectedModel = string.Empty;
            Models = new List<EngineLlmModelStatus>();
            RuntimeName = string.Empty;
            RuntimeFlavor = string.Empty;
            RuntimeChoice = "auto";
            RuntimeSizesMb = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            RuntimeExtraLicenses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LocalServer = string.Empty;
            LocalModel = string.Empty;
            LocalGpuLayers = string.Empty;
            RuntimeVersion = string.Empty;
            RuntimeLicense = string.Empty;
            RuntimeLicenseUrl = string.Empty;
            RuntimeHomepage = string.Empty;
            DataFolder = string.Empty;
            ProgressFile = string.Empty;
            CancelFile = string.Empty;
            Catalog = string.Empty;
        }
    }

    public sealed class EngineLlmLocalServer
    {
        public string Path { get; set; }
        public string Backend { get; set; }
        public bool Recommended { get; set; }

        public EngineLlmLocalServer()
        {
            Path = string.Empty;
            Backend = string.Empty;
        }
    }

    public sealed class EngineLlmLocalModel
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public long SizeBytes { get; set; }
        public string Size { get; set; }
        public bool FitsGpu { get; set; }
        public bool FitsRam { get; set; }
        public bool Recommended { get; set; }

        public EngineLlmLocalModel()
        {
            Path = string.Empty;
            Name = string.Empty;
            Size = string.Empty;
        }
    }

    public sealed class EngineLlmLocalScan
    {
        public List<EngineLlmLocalServer> Servers { get; set; }
        public List<EngineLlmLocalModel> Models { get; set; }
        public string BestServer { get; set; }
        public string BestModel { get; set; }
        public string Reason { get; set; }
        public List<string> Searched { get; set; }
        public double Seconds { get; set; }

        public EngineLlmLocalScan()
        {
            Servers = new List<EngineLlmLocalServer>();
            Models = new List<EngineLlmLocalModel>();
            BestServer = string.Empty;
            BestModel = string.Empty;
            Reason = string.Empty;
            Searched = new List<string>();
        }
    }

    public sealed class EngineLlmDownloadResult
    {
        public string Model { get; set; }
        public string Runtime { get; set; }
        public string Path { get; set; }
        public string Consent { get; set; }
        public List<string> Messages { get; set; }
        public List<string> Errors { get; set; }

        public EngineLlmDownloadResult()
        {
            Model = string.Empty;
            Runtime = string.Empty;
            Path = string.Empty;
            Consent = string.Empty;
            Messages = new List<string>();
            Errors = new List<string>();
        }
    }
}
