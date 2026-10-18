using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using JoyPro.DeviceReference;
using Xunit;

namespace JoyPro.Gui.Tests;

// JoyPro's curated marker positions on the makers' photos (EngineOnly_Runtime\DB\DeviceLayouts, docs/device-reference.md
// "Curating positions"): every set belongs to one referenced photo whose hash is on record, names only parts and inputs
// the catalog gives the model, and keeps its groups (a hat with its directions) as the catalog has them.
[SupportedOSPlatform("windows")]
public sealed class CuratedDeviceLayoutsTests
{
    private static readonly string[] Confidences = ["high", "medium", "low"];

    [Fact]
    public void EveryCuratedSet_ReferencesARecordedPhotoHashAndCatalogInputs()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);
        List<string> problems = [];
        List<DeviceLayoutSet> sets = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(DeviceReferenceTests.ShippedCatalog), problems);
        List<DeviceLineArt> arts = DeviceLineArtDb.LoadFolder(DeviceLineArtDb.FolderOf(DeviceReferenceTests.ShippedCatalog), null);
        foreach (DeviceLayoutSet set in sets)
        {
            string name = Path.GetFileName(set.File);
            RefDevice? model = catalog.Find(set.Model);
            if (model == null)
            {
                problems.Add(name + ": unknown model " + set.Model);
                continue;
            }
            if (set.PhotoKind == "lineart")
            {
                // Positions on JoyPro's own drawing (tools/device_drawings.py): the set names the drawing's key, no photo.
                if (!arts.Any(a => a.Model == model.Id && a.Key == set.PhotoSha256))
                    problems.Add(name + ": no shipped drawing of " + model.Id + " with the key " + set.PhotoSha256);
                if (set.PhotoUrl.Length > 0 || set.PhotoFileBase64.Length > 0)
                    problems.Add(name + ": a set on JoyPro's drawing names no photo");
            }
            else
            {
                // The photo: one of the model's official photos, its content hash on record in DB\DevicePhotos.
                DevicePhoto? photo = DevicePhotoDb.OfficialPhotos(model).FirstOrDefault(p => string.Equals(p.Url, set.PhotoUrl, StringComparison.OrdinalIgnoreCase));
                if (photo == null)
                    problems.Add(name + ": the photo is not an official photo of " + model.Id);
                else if (photo.Sha256.Length != 64 || photo.Sha256 != set.PhotoSha256)
                    problems.Add(name + ": the photo hash is not on record (or another one) in DB\\DevicePhotos");
                if (set.PhotoKind != "vendor" || set.PhotoFileBase64.Length > 0)
                    problems.Add(name + ": a curated set holds a maker's photo by address only");
            }

            Dictionary<string, SchematicMarker> parts = SchematicBuilder.Build(model, catalog.BaseOf(model)).Markers
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (string part in set.Markers.Keys.Concat(set.Candidates.Keys).Concat(set.Unplaced.Keys).Where(k => !parts.ContainsKey(k)))
                problems.Add(name + ": no part " + part);
            foreach (string part in set.Markers.Keys.Where(set.Unplaced.ContainsKey))
                problems.Add(name + ": " + part + " is placed and not placed");
            foreach (string part in set.Markers.Keys.Where(set.Candidates.ContainsKey))
                problems.Add(name + ": " + part + " is placed and a candidate");

            // JoyPro's callout sheet of the photo (tables and lines, editable by the user as his own copy): clean, and
            // only tables of the model's parts, each placed part linked.
            if (set.Callouts != null)
            {
                CalloutCheck check = CalloutLayouter.Check(set.Callouts);
                if (!check.Ok)
                    problems.Add(name + ": callout sheet not clean: " + check);
                foreach (CalloutTable table in set.Callouts.Tables.Where(t => t.Parts.Any(p => !parts.ContainsKey(p))))
                    problems.Add(name + ": callout table " + table.Id + " names a part the model does not have");
                if (set.Callouts.PictureWidth < 50 || set.Callouts.PictureHeight < 50)
                    problems.Add(name + ": callout sheet without the photo's size");
            }

            // The detail of a set made with tools/device_positions.py: every part with the catalog's own inputs.
            if (set.Parts.Count == 0)
                continue;
            foreach (string part in parts.Keys.Where(k => !set.Parts.ContainsKey(k)))
                problems.Add(name + ": part " + part + " is missing from 'parts'");
            foreach (KeyValuePair<string, DeviceLayoutPart> pair in set.Parts)
            {
                if (!parts.TryGetValue(pair.Key, out SchematicMarker? marker))
                    continue;
                HashSet<string> own = new(marker.Inputs.Select(i => i.Input + "|" + i.Control), StringComparer.OrdinalIgnoreCase);
                HashSet<string> listed = new(pair.Value.Inputs.Select(i => i.Input + "|" + i.Control), StringComparer.OrdinalIgnoreCase);
                if (!own.SetEquals(listed))
                    problems.Add(name + ": " + pair.Key + " lists other inputs than the catalog: " + string.Join(", ", listed.Except(own).Concat(own.Except(listed))));
                foreach (DeviceLayoutPartInput input in pair.Value.Inputs.Where(i => !InputExists(catalog, model, i.Input)))
                    problems.Add(name + ": " + pair.Key + ": " + input.Input + " is no input of " + model.Id);
                if (!Confidences.Contains(pair.Value.Mapping))
                    problems.Add(name + ": " + pair.Key + " has no mapping confidence");
                bool placed = set.Markers.ContainsKey(pair.Key);
                if (placed && (pair.Value.Confidence is not ("high" or "medium") || pair.Value.Mapping == "low"))
                    problems.Add(name + ": " + pair.Key + " is shown although its position or mapping is uncertain");
                if (!placed && !set.Unplaced.ContainsKey(pair.Key))
                    problems.Add(name + ": " + pair.Key + " is neither placed nor says why not");
            }
            if (set.MappingSource.Length == 0)
                problems.Add(name + ": no mappingSource");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(sets.Count >= 290);
    }

    // An input of the model: its catalog controls and axes, or what the base adds (a grip's gimbal axes).
    private static bool InputExists(DeviceCatalog catalog, RefDevice model, string input)
    {
        if (string.IsNullOrEmpty(input))
            return true;
        RefDevice? baseModel = catalog.BaseOf(model);
        return new[] { model, baseModel }.Where(m => m != null).Any(m => m!.Controls.Any(c => string.Equals(c.Input, input, StringComparison.OrdinalIgnoreCase)) ||
                                                                      m!.Axes.Any(a => string.Equals(a.Input, input, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CuratedSet_ExtraFields_RoundTrip()
    {
        DeviceLayoutSet set = new()
        {
            Model = "thrustmaster-t16000m",
            PhotoUrl = "https://www.thrustmaster.com/x.webp",
            PhotoSha256 = new string('a', 64),
            View = "main",
            MappingSource = "manual fig. 3"
        };
        set.Markers["stick.hat.tms"] = [0.5, 0.1];
        set.Candidates["stick.base.button.1"] = [0.2, 0.7];
        set.Unplaced["stick.base.button.1"] = "order in the cluster not certain";
        set.Parts["stick.hat.tms"] = new DeviceLayoutPart
        {
            Type = "hat", Name = "TMS hat", Mapping = "high", Confidence = "high",
            Inputs = [new DeviceLayoutPartInput { Input = "JOY_BTN_POV1_U", Control = "stick.hat.tms.up" }]
        };
        DeviceLayoutSet copy = DeviceLayoutSet.Parse(set.ToJson(), "x.json");
        Assert.Equal("manual fig. 3", copy.MappingSource);
        Assert.Equal([0.2, 0.7], copy.Candidates["stick.base.button.1"]);
        Assert.Equal("order in the cluster not certain", copy.Unplaced["stick.base.button.1"]);
        Assert.Equal("hat", copy.Parts["stick.hat.tms"].Type);
        Assert.Equal("JOY_BTN_POV1_U", copy.Parts["stick.hat.tms"].Inputs.Single().Input);
        Assert.Equal("high", copy.Parts["stick.hat.tms"].Mapping);
    }
}
