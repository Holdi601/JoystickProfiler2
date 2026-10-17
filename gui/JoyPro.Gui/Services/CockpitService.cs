using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// The cockpit database for the Cockpits page (docs/cockpits.md): DB\Cockpits next to the device catalog the GUI uses
// (the engine's DB folder), read with the engine's own reader (linked CockpitCatalog), plus JoyPro's function names
// (DB\Functions\slots.json) for the tooltips.
public static class CockpitService
{
    private static readonly object Gate = new();
    private static CockpitCatalog? catalog;
    private static string catalogKey = string.Empty;
    private static Dictionary<string, string>? functionNames;

    // Tests set a folder of their own.
    public static Func<string?> FolderOverride { get; set; } = () => null;

    public static string Folder()
    {
        string? fixedFolder = FolderOverride();
        if (!string.IsNullOrWhiteSpace(fixedFolder))
            return fixedFolder;
        string? env = Environment.GetEnvironmentVariable(CockpitCatalog.FolderVariable);
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        string devices = DeviceReferenceService.CatalogFolder();
        string? db = Path.GetDirectoryName(devices.TrimEnd('\\', '/'));
        string candidate = Path.Combine(db ?? string.Empty, "Cockpits");
        if (Directory.Exists(candidate))
            return candidate;
        DirectoryInfo? walk = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && walk != null; i++, walk = walk.Parent)
        {
            string repo = Path.Combine(walk.FullName, "EngineOnly_Runtime", "DB", "Cockpits");
            if (Directory.Exists(repo))
                return repo;
        }
        return candidate;
    }

    public static CockpitCatalog Catalog
    {
        get
        {
            lock (Gate)
            {
                string key = Folder();
                if (catalog == null || catalogKey != key)
                {
                    catalog = CockpitCatalog.Load(key);
                    catalogKey = key;
                }
                return catalog;
            }
        }
    }

    public static void Reload()
    {
        lock (Gate)
        {
            catalog = null;
            functionNames = null;
        }
    }

    // JoyPro function id -> its name.
    public static IReadOnlyDictionary<string, string> FunctionNames
    {
        get
        {
            lock (Gate)
            {
                if (functionNames != null)
                    return functionNames;
                Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string? db = Path.GetDirectoryName(Folder().TrimEnd('\\', '/'));
                    string file = Path.Combine(db ?? string.Empty, "Functions", "slots.json");
                    if (File.Exists(file))
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                        foreach (JsonElement slot in doc.RootElement.EnumerateArray())
                        {
                            if (slot.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                                names[id.GetString()!] = slot.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : id.GetString()!;
                        }
                    }
                }
                catch (Exception)
                {
                    // Without the function list the tooltips show the ids only.
                }
                functionNames = names;
                return names;
            }
        }
    }
}

// The user's consent to the cockpit photos (docs/privacy.md "Cockpit photos"): JoyPro ships only the photos'
// addresses. It asks once per aircraft before it downloads any (Yes / No / Always for cockpits / Never for cockpits).
// The answers live with the device photo answers (DevicePhotoConsent) under "cockpit:<aircraft id>" and "cockpit:*";
// the device photos' global "Never" also stops cockpit photos.
public static class CockpitPhotoConsent
{
    public const string AllKey = "cockpit:*";

    public static string KeyOf(string aircraftId) => "cockpit:" + (aircraftId ?? string.Empty).Trim().ToLowerInvariant();

    public static PhotoConsentState For(string? aircraftId)
    {
        if (string.IsNullOrWhiteSpace(aircraftId) || DevicePhotoConsent.Mode == DevicePhotoConsent.Never)
            return PhotoConsentState.Denied;
        Dictionary<string, string> answers = DeviceReferenceService.ReadSettings().PhotoAnswers ?? [];
        string? own = answers.FirstOrDefault(a => string.Equals(a.Key, KeyOf(aircraftId), StringComparison.OrdinalIgnoreCase)).Value;
        if (own == "yes")
            return PhotoConsentState.Allowed;
        if (own == "no")
            return PhotoConsentState.Denied;
        string? all = answers.FirstOrDefault(a => string.Equals(a.Key, AllKey, StringComparison.OrdinalIgnoreCase)).Value;
        if (all == "yes")
            return PhotoConsentState.Allowed;
        if (all == "no")
            return PhotoConsentState.Denied;
        return PhotoConsentState.Ask;
    }

    public static bool Allows(string? aircraftId) => For(aircraftId) == PhotoConsentState.Allowed;

    // "yes" / "no" for this aircraft, "always" / "never" for every cockpit.
    public static void Answer(string? aircraftId, string answer)
    {
        switch ((answer ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "yes":
            case "no":
                if (!string.IsNullOrWhiteSpace(aircraftId))
                    DevicePhotoConsent.Answer(KeyOf(aircraftId), answer.Trim().ToLowerInvariant());
                break;
            case "always":
                DevicePhotoConsent.Answer(AllKey, "yes");
                break;
            case "never":
                DevicePhotoConsent.Answer(AllKey, "no");
                break;
        }
    }

    public static string Question(string aircraftName, IEnumerable<string> urls)
    {
        List<string> sites = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(DevicePhotoConsent.SiteOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string where = sites.Count == 0 ? T("the web") : string.Join(", ", sites.Take(3)) + (sites.Count > 3 ? T(" and others") : string.Empty);
        return T("JoyPro has photos of the {0} cockpit on {1}. Download them and show them here? (They are only stored on this PC.)", aircraftName, where);
    }
}
