using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

public enum PhotoConsentState
{
    Ask,
    Allowed,
    Denied
}

// The user's consent to the makers' product photos (docs/privacy.md "Device photos"): JoyPro ships no photo, only the
// address of the photo on the maker's own site (DB\DevicePhotos). When JoyPro identifies a device and such a photo
// exists, it asks once per device model whether to download it (Layout page, Start page device card): Yes / No /
// Always for all my devices / Never. Without a yes only JoyPro's drawing or the class outline is shown, and nothing is
// downloaded (DeviceImageCache checks AllowsUrl before a download and before a cached photo is shown).
// Settings > Privacy changes the mode and forgets the answers. Kept in <data>\DeviceReference\settings.json.
public static class DevicePhotoConsent
{
    public const string Ask = "ask";
    public const string Always = "always";
    public const string Never = "never";

    private static readonly object Gate = new();
    private static Dictionary<string, List<string>>? modelsByUrl;
    private static DeviceCatalog? indexedCatalog;

    // Raised when the mode or an answer changed (open views refresh their pictures).
    public static event EventHandler? Changed;

    // ask, always or never. An old "Manufacturer photos: off" counts as never.
    public static string Mode
    {
        get
        {
            DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
            string mode = (settings.PhotoMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is Ask or Always or Never)
                return mode;
            return settings.VendorPhotos ? Ask : Never;
        }
        set
        {
            string mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not (Ask or Always or Never))
                mode = Ask;
            lock (Gate)
            {
                DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
                settings.PhotoMode = mode;
                settings.VendorPhotos = mode != Never;
                if (mode == Never)
                    settings.AutoDownloadPhotos = false;
                DeviceReferenceService.WriteSettings(settings);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    // "Download reference photos automatically" (Settings > Privacy, or "Always download" on the offer): the photos of
    // every device that appears are downloaded in the background. Switching it on also allows the photos (mode
    // always); the mode "never" switches it off.
    public static bool AutoDownload
    {
        get
        {
            DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
            return settings.AutoDownloadPhotos && Mode != Never;
        }
        set
        {
            lock (Gate)
            {
                DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
                settings.AutoDownloadPhotos = value;
                if (value)
                {
                    settings.PhotoMode = Always;
                    settings.VendorPhotos = true;
                }
                DeviceReferenceService.WriteSettings(settings);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    // The state for one device model: an answer for the model wins; else the mode (always / never / ask).
    public static PhotoConsentState For(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return PhotoConsentState.Denied;
        DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
        string? answer = settings.PhotoAnswers?.FirstOrDefault(a => string.Equals(a.Key, modelId.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
        if (answer == "yes")
            return PhotoConsentState.Allowed;
        if (answer == "no")
            return PhotoConsentState.Denied;
        return Mode switch
        {
            Always => PhotoConsentState.Allowed,
            Never => PhotoConsentState.Denied,
            _ => PhotoConsentState.Ask
        };
    }

    public static bool Allows(string? modelId) => For(modelId) == PhotoConsentState.Allowed;

    // The user's answer: "yes" / "no" for this model, "always" / "never" for every device (the mode; the answers given
    // per model stay, "never" clears the yes answers so no photo is downloaded any more).
    public static void Answer(string? modelId, string answer)
    {
        string a = (answer ?? string.Empty).Trim().ToLowerInvariant();
        if (a is Always or Never)
        {
            lock (Gate)
            {
                DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
                settings.PhotoMode = a;
                settings.VendorPhotos = a != Never;
                if (a == Never)
                    settings.AutoDownloadPhotos = false;
                if (a == Never)
                    settings.PhotoAnswers = (settings.PhotoAnswers ?? []).Where(p => p.Value != "yes").ToDictionary(p => p.Key, p => p.Value);
                else if (!string.IsNullOrWhiteSpace(modelId))
                    settings.PhotoAnswers = SetAnswer(settings.PhotoAnswers, modelId, "yes");
                DeviceReferenceService.WriteSettings(settings);
            }
        }
        else if ((a is "yes" or "no") && !string.IsNullOrWhiteSpace(modelId))
        {
            lock (Gate)
            {
                DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
                settings.PhotoAnswers = SetAnswer(settings.PhotoAnswers, modelId, a);
                DeviceReferenceService.WriteSettings(settings);
            }
        }
        else
            return;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Settings > Privacy: forget every per-device answer (JoyPro asks again).
    public static void ForgetAnswers()
    {
        lock (Gate)
        {
            DeviceReferenceService.ReferenceSettings settings = DeviceReferenceService.ReadSettings();
            settings.PhotoAnswers = [];
            DeviceReferenceService.WriteSettings(settings);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static int AnswerCount(string answer) =>
        (DeviceReferenceService.ReadSettings().PhotoAnswers ?? []).Count(p => p.Value == answer);

    private static Dictionary<string, string> SetAnswer(Dictionary<string, string>? answers, string modelId, string value)
    {
        Dictionary<string, string> result = (answers ?? []).Where(p => !string.Equals(p.Key, modelId.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value);
        result[modelId.Trim()] = value;
        return result;
    }

    // An internet address may be loaded: a maker's photo of the catalog only with consent for (one of) its model(s);
    // any other address (not a device photo JoyPro knows) is not this setting's business.
    public static bool AllowsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        List<string>? models;
        try
        {
            models = ModelsOf(url);
        }
        catch (Exception)
        {
            // The catalog cannot be read: the address cannot be told apart from a device photo, so it is not loaded.
            return false;
        }
        return models == null || models.Any(Allows);
    }

    // The catalog models that list this photo address (null = none).
    private static List<string>? ModelsOf(string url)
    {
        DeviceCatalog catalog = DeviceReferenceService.Catalog;
        lock (Gate)
        {
            if (modelsByUrl == null || !ReferenceEquals(indexedCatalog, catalog))
            {
                Dictionary<string, List<string>> index = new(StringComparer.OrdinalIgnoreCase);
                foreach (RefDevice model in catalog.Devices)
                {
                    IEnumerable<string> urls = model.Photos.Select(p => p.Url).Append(model.Image).Where(u => !string.IsNullOrWhiteSpace(u));
                    foreach (string u in urls.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!index.TryGetValue(u, out List<string>? list))
                            index[u] = list = [];
                        if (!list.Contains(model.Id, StringComparer.OrdinalIgnoreCase))
                            list.Add(model.Id);
                    }
                }
                modelsByUrl = index;
                indexedCatalog = catalog;
            }
            return modelsByUrl.TryGetValue(url.Trim(), out List<string>? found) ? found : null;
        }
    }

    // "vkbcontrollers.com" from a photo address (the question names the maker's site).
    public static string SiteOf(string url)
    {
        if (!Uri.TryCreate(url ?? string.Empty, UriKind.Absolute, out Uri? uri))
            return T("the maker's web site");
        string host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    public static string Question(string modelName, string url) =>
        T("JoyPro found the product photo of your {0} on {1}. Download it and show it here? (It is only stored on this PC.)", modelName, SiteOf(url));
}
