using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Official pictures (engine media): game covers (Steam library art on this PC, else downloaded once from Steam's store or
// the publisher's site, else the game's own icon) and DCS aircraft pictures from the installed modules. The engine is
// asked once per session in the background; the pictures are loaded when a view needs them. With "Game covers from the
// internet" off (Settings > Privacy; or in the tests) the engine gets --no-internet-pictures: no download and no
// downloaded cover. Device photos have their own consent (DevicePhotoConsent).
public sealed partial class MainWindowViewModel
{
    private sealed class MediaIndex
    {
        public Dictionary<string, string> GameCovers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> GameNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        // steam-cache, download or exe-icon.
        public Dictionary<string, string> GameCoverSources { get; } = new(StringComparer.OrdinalIgnoreCase);

        // cover (fills the tile) or logo (the publisher's mark or the executable's icon: drawn on JoyPro's badge).
        public Dictionary<string, string> GameCoverKinds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> AircraftIcons { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> AircraftBackgrounds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> AircraftNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private Task<MediaIndex>? mediaIndex;

    private Task<MediaIndex> GetMediaIndexAsync() => mediaIndex ??= LoadMediaIndexAsync();

    // The engine call: covers from the web only while internet pictures are on and not blocked (tests).
    internal static List<string> MediaArguments(bool internetPicturesEnabled, bool internetPicturesBlocked)
    {
        List<string> arguments = ["--status", "false", "media"];
        if (!internetPicturesEnabled || internetPicturesBlocked)
            arguments.Add("--no-internet-pictures");
        return arguments;
    }

    // Game covers have their own setting (Settings > Privacy), apart from the device photos' consent.
    private static bool CoverDownloadsAllowed()
    {
        try
        {
            return DeviceReferenceService.GameCoversEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<MediaIndex> LoadMediaIndexAsync()
    {
        MediaIndex index = new();
        try
        {
            string engine = ResolveEnginePath();
            if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
                return index;
            // Read-only and slow on the first run (covers are downloaded): not queued with the user's commands.
            EngineCommandResult result = await cliClient.RunAsync(engine, MediaArguments(CoverDownloadsAllowed(), DeviceImageCache.InternetPicturesBlocked));
            if (result.ExitCode != 0)
                return index;
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement game in games.EnumerateArray())
                {
                    string id = ReadJsonString(game, "GameId");
                    if (id.Length == 0)
                        continue;
                    index.GameNames[id] = ReadJsonString(game, "Name");
                    string cover = ReadJsonString(game, "Cover");
                    if (cover.Length > 0)
                    {
                        index.GameCovers[id] = cover;
                        index.GameCoverSources[id] = ReadJsonString(game, "CoverSource");
                        index.GameCoverKinds[id] = ReadJsonString(game, "CoverKind");
                    }
                }
            }
            if (doc.RootElement.TryGetProperty("Aircraft", out JsonElement aircraft) && aircraft.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement plane in aircraft.EnumerateArray())
                {
                    string name = ReadJsonString(plane, "Plane");
                    if (name.Length == 0)
                        continue;
                    string icon = ReadJsonString(plane, "Icon");
                    string background = ReadJsonString(plane, "Background");
                    string display = ReadJsonString(plane, "DisplayName");
                    if (icon.Length > 0)
                        index.AircraftIcons[name] = icon;
                    if (background.Length > 0)
                        index.AircraftBackgrounds[name] = background;
                    if (display.Length > 0)
                        index.AircraftNames[name] = display;
                }
            }
        }
        catch (Exception)
        {
            // No pictures is fine; everything else keeps working.
        }
        return index;
    }

    // The module icon of a DCS aircraft (or its mission editor picture when the module has no icon).
    private async Task<Bitmap?> LoadAircraftPictureAsync(string plane)
    {
        MediaIndex index = await GetMediaIndexAsync();
        if (index.AircraftIcons.TryGetValue(plane, out string? icon))
            return await DeviceImageCache.LoadAsync(icon, 64);
        if (index.AircraftBackgrounds.TryGetValue(plane, out string? background))
            return await DeviceImageCache.LoadAsync(background, 160);
        return null;
    }

    // The game's cover for tiles (Steam's picture on this PC or the downloaded one); width in pixels of the decoded
    // picture. Without one the tiles show JoyPro's badge of the game (Views/Controls/GameBadge).
    private async Task<Bitmap?> LoadGameCoverAsync(string gameId, int width = 120)
    {
        MediaIndex index = await GetMediaIndexAsync();
        return index.GameCovers.TryGetValue(gameId, out string? cover) && !IsIconSource(index, gameId) ? await DeviceImageCache.LoadAsync(cover, width) : null;
    }

    // The publisher's mark or the icon of the game's executable (games without a cover), drawn on the badge.
    private async Task<Bitmap?> LoadGameLogoAsync(string gameId, int width = 96)
    {
        MediaIndex index = await GetMediaIndexAsync();
        return index.GameCovers.TryGetValue(gameId, out string? cover) && IsIconSource(index, gameId) ? await DeviceImageCache.LoadAsync(cover, width) : null;
    }

    private static bool IsIconSource(MediaIndex index, string gameId) =>
        (index.GameCoverKinds.TryGetValue(gameId, out string? kind) && string.Equals(kind, "logo", StringComparison.OrdinalIgnoreCase)) ||
        (index.GameCoverSources.TryGetValue(gameId, out string? source) && string.Equals(source, "exe-icon", StringComparison.OrdinalIgnoreCase));

    private bool relationAircraftNamesLoaded;

    // The readable aircraft names for the "19 aircraft" chips of the relation cards (once per run).
    private async Task LoadRelationAircraftNamesAsync()
    {
        if (relationAircraftNamesLoaded || !allRelations.Any(r => r.PlaneIds.Count > 0))
            return;
        relationAircraftNamesLoaded = true;
        MediaIndex index = await GetMediaIndexAsync();
        if (index.AircraftNames.Count == 0)
            return;
        RelationListItem.AircraftNames = new Dictionary<string, string>(index.AircraftNames, StringComparer.OrdinalIgnoreCase);
        foreach (RelationListItem relation in allRelations)
            relation.RefreshAircraftNames();
        // The search's match lines name the aircraft too ("AH-64D BLK.II", not "AH-64D_BLK_II"): again with the names.
        UpdateRelationSearchHits(Relations.ToList());
        UpdateBindSearchHits(Binds.ToList());
    }

    // The aircraft's name as the module calls it ("F/A-18C Hornet" for FA-18C_hornet), or the DB name.
    private async Task<string> AircraftDisplayNameAsync(string plane)
    {
        MediaIndex index = await GetMediaIndexAsync();
        return index.AircraftNames.TryGetValue(plane, out string? name) ? name : plane;
    }
}
