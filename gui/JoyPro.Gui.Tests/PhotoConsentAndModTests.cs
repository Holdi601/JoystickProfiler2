using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The consent before a maker's product photo is downloaded (docs/privacy.md "Device photos", DevicePhotoConsent) and
// the one-click install of VTOL VR's BYOJoystick mod (docs/games/vtol-vr.md). Fakes only: no photo is downloaded (the
// downloads are counted fakes), no Steam page is opened (ExternalProcess is captured), the engine is a fake.
[SupportedOSPlatform("windows")]
public sealed class PhotoConsentAndModTests : IDisposable
{
    private const string Model = "winwing-f18-takeoff-panel";
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "photo-consent-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public PhotoConsentAndModTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string PhotoUrl(string modelId)
    {
        RefDevice model = DeviceReferenceService.Catalog.Find(modelId)!;
        return DevicePhotoDb.OfficialPhotos(model).First().Url;
    }

    [Fact]
    public void Consent_AsksByDefault_RemembersPerDevice_AndNeverWins()
    {
        Assert.Equal(DevicePhotoConsent.Ask, DevicePhotoConsent.Mode);
        Assert.Equal(PhotoConsentState.Ask, DevicePhotoConsent.For(Model));
        string url = PhotoUrl(Model);
        // A maker's photo of the catalog: not without consent. Any other address is not this setting's business.
        Assert.False(DevicePhotoConsent.AllowsUrl(url));
        Assert.True(DevicePhotoConsent.AllowsUrl("https://example.org/not-a-device-photo.png"));
        Assert.Contains("on " + DevicePhotoConsent.SiteOf(url) + ".", DevicePhotoConsent.Question("WinWing panel", url));
        Assert.DoesNotContain("www.", DevicePhotoConsent.SiteOf("https://www.example.com/a.png"));

        DevicePhotoConsent.Answer(Model, "yes");
        Assert.Equal(PhotoConsentState.Allowed, DevicePhotoConsent.For(Model));
        Assert.True(DevicePhotoConsent.AllowsUrl(url));
        Assert.Equal(PhotoConsentState.Ask, DevicePhotoConsent.For("vkb-gladiator-nxt-evo-base"));

        DevicePhotoConsent.Answer(Model, "no");
        Assert.Equal(PhotoConsentState.Denied, DevicePhotoConsent.For(Model));

        // Always for all my devices; Never takes every yes back.
        DevicePhotoConsent.Answer("vkb-gladiator-nxt-evo-base", DevicePhotoConsent.Always);
        Assert.Equal(PhotoConsentState.Allowed, DevicePhotoConsent.For("thrustmaster-t16000m"));
        Assert.Equal(PhotoConsentState.Denied, DevicePhotoConsent.For(Model));
        DevicePhotoConsent.Answer(Model, "yes");
        DevicePhotoConsent.Answer(null, DevicePhotoConsent.Never);
        Assert.Equal(PhotoConsentState.Denied, DevicePhotoConsent.For(Model));
        Assert.Equal(PhotoConsentState.Denied, DevicePhotoConsent.For("thrustmaster-t16000m"));
        Assert.False(DevicePhotoConsent.AllowsUrl(url));
        Assert.False(DeviceReferenceService.VendorPhotosEnabled);
        Assert.Contains("--no-device-photos", MainWindowViewModel.HudDevicesArguments());

        // Settings > Privacy: back to asking, answers forgotten.
        DevicePhotoConsent.Mode = DevicePhotoConsent.Ask;
        DevicePhotoConsent.ForgetAnswers();
        Assert.Equal(PhotoConsentState.Ask, DevicePhotoConsent.For(Model));
        Assert.DoesNotContain("--no-device-photos", MainWindowViewModel.HudDevicesArguments());
    }

    [Fact]
    public void Consent_TheOldManufacturerPhotosSwitchOff_CountsAsNever()
    {
        DeviceReferenceService.WriteSettings(new DeviceReferenceService.ReferenceSettings { VendorPhotos = false });
        Assert.Equal(DevicePhotoConsent.Never, DevicePhotoConsent.Mode);
        Assert.Equal(PhotoConsentState.Denied, DevicePhotoConsent.For(Model));
        // Game covers have their own setting.
        Assert.True(DeviceReferenceService.GameCoversEnabled);
        DeviceReferenceService.GameCoversEnabled = false;
        Assert.False(DeviceReferenceService.GameCoversEnabled);
        Assert.Equal(DevicePhotoConsent.Never, DevicePhotoConsent.Mode);
    }

    [Fact]
    public void LayoutPage_DownloadsNothingWithoutConsent_ThenAfterYes_AndRespectsNever()
    {
        AvaloniaRenderThread.Run(() =>
        {
            string photo = WritePhoto(Path.Combine(temp, "vendor.png"));
            int fetches = 0;
            DeviceReferenceViewModel vm = new() { FetchVendorPhoto = _ => { fetches++; return Task.FromResult<string?>(photo); } };
            AvaloniaRenderThread.Wait(Done(vm.ShowModelAsync(Model)));
            // Asked, nothing downloaded, JoyPro's drawing shown.
            Assert.True(vm.AsksPhotoConsent);
            Assert.StartsWith("JoyPro found the product photo of your", vm.PhotoConsentText);
            Assert.Contains("(It is only stored on this PC.)", vm.PhotoConsentText);
            Assert.Equal(0, fetches);
            Assert.NotEqual("vendor", vm.PictureKind);
            Assert.DoesNotContain(vm.Pictures, p => p.Kind == "vendor");

            // No: still nothing, and not asked again for this device.
            vm.AnswerPhotoConsentCommand.Execute("no");
            AvaloniaRenderThread.Pump();
            Assert.Equal(0, fetches);
            Assert.False(vm.AsksPhotoConsent);
            DeviceReferenceViewModel again = new() { FetchVendorPhoto = _ => { fetches++; return Task.FromResult<string?>(photo); } };
            AvaloniaRenderThread.Wait(Done(again.ShowModelAsync(Model)));
            Assert.False(again.AsksPhotoConsent);
            Assert.Equal(0, fetches);

            // Yes (the switch next to the picture): downloaded once, the maker's photo is shown.
            again.VendorPhotosEnabled = true;
            AvaloniaRenderThread.Pump();
            Assert.Equal(1, fetches);
            Assert.Equal("vendor", again.PictureKind);
            Assert.Equal(PhotoConsentState.Allowed, DevicePhotoConsent.For(Model));

            // Never: no question and no download for any device, also not for the one answered yes before.
            DeviceReferenceViewModel other = new() { FetchVendorPhoto = _ => { fetches++; return Task.FromResult<string?>(photo); } };
            AvaloniaRenderThread.Wait(Done(other.ShowModelAsync("vkb-gladiator-nxt-evo-base")));
            Assert.True(other.AsksPhotoConsent);
            other.AnswerPhotoConsentCommand.Execute("never");
            AvaloniaRenderThread.Pump();
            int before = fetches;
            DeviceReferenceViewModel third = new() { FetchVendorPhoto = _ => { fetches++; return Task.FromResult<string?>(photo); } };
            AvaloniaRenderThread.Wait(Done(third.ShowModelAsync(Model)));
            Assert.False(third.AsksPhotoConsent);
            Assert.NotEqual("vendor", third.PictureKind);
            Assert.Equal(before, fetches);

            // Always for all my devices (asked on another device): the photo comes without a question.
            DevicePhotoConsent.Mode = DevicePhotoConsent.Ask;
            DeviceReferenceViewModel fourth = new() { FetchVendorPhoto = _ => { fetches++; return Task.FromResult<string?>(photo); } };
            AvaloniaRenderThread.Wait(Done(fourth.ShowModelAsync("vkb-gladiator-nxt-evo-base")));
            fourth.AnswerPhotoConsentCommand.Execute("always");
            AvaloniaRenderThread.Pump();
            Assert.Equal("vendor", fourth.PictureKind);
            // Shown again: JoyPro's own drawing of the model with its positions comes before the maker's photo (no
            // download needed); the photo is offered next to it.
            DeviceReferenceViewModel fifth = new() { FetchVendorPhoto = _ => { fetches++; return Task.FromResult<string?>(photo); } };
            AvaloniaRenderThread.Wait(Done(fifth.ShowModelAsync(Model)));
            Assert.False(fifth.AsksPhotoConsent);
            // (a drawing with JoyPro's positions comes before the photo, one without after it)
            Assert.Equal(fifth.Pictures.First(p => p.Kind is "vendor" or "lineart").Kind, fifth.PictureKind);
            Assert.Contains(fifth.Pictures, p => p.Kind == "vendor");
        });
    }

    [Fact]
    public async Task StartPageCard_AsksFirst_LoadsOnlyAfterYes()
    {
        MainWindowViewModel vm = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        List<string> loads = [];
        vm.LoadCardPhoto = url => { loads.Add(url); return Task.FromResult<Bitmap?>(null); };
        string url = PhotoUrl(Model);
        DeviceCardItem card = new("WINWING TAKEOFF PANEL {00000000-0000-0000-0000-0000000000b1}", "WINWING TAKEOFF PANEL", Model, "panel", 0, 20, 0, connected: true, inProfile: false)
        {
            ModelName = "WinWing F/A-18 Take Off Panel",
            ModelId = Model
        };
        List<(DeviceCardItem, DevicePicturePlan)> plans = [(card, new DevicePicturePlan { ModelId = Model, VendorPhoto = url })];

        await vm.LoadDeviceCardPicturesAsync(plans);
        Assert.True(card.AsksPhotoConsent);
        Assert.Contains(DevicePhotoConsent.SiteOf(url), card.PhotoConsentText);
        Assert.Empty(loads);

        await vm.CardPhotoNoCommand.ExecuteAsync(card);
        Assert.False(card.AsksPhotoConsent);
        Assert.Empty(loads);

        await vm.CardPhotoYesCommand.ExecuteAsync(card);
        Assert.Equal([url], loads);

        await vm.CardPhotoNeverCommand.ExecuteAsync(card);
        loads.Clear();
        await vm.LoadDeviceCardPicturesAsync(plans);
        Assert.False(card.AsksPhotoConsent);
        Assert.Empty(loads);
        Assert.Equal("Never", vm.PhotoConsentMode);
        vm.PhotoConsentMode = "Ask for each device";
        Assert.Equal(DevicePhotoConsent.Ask, DevicePhotoConsent.Mode);
    }

    // ------------------------------------------------------------------ BYOJoystick

    [Fact]
    public async Task Byoj_ShowsLicenseAndSource_InstallsOnlyAfterConsent_ThroughSteam()
    {
        ByojFakeEngine fake = new();
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        GameCardItem vtol = new("VTOLVR", "VTOL VR (BYOJoystick mod)", "registry", "vtolvr", installed: true, canRefresh: true);
        Assert.True(vtol.OffersModInstall);
        Assert.False(new GameCardItem("DCS", "DCS World", "dcs", "dcs", installed: true, canRefresh: true).OffersModInstall);

        await vm.OpenByojCommand.ExecuteAsync(vtol);
        Assert.True(vm.ByojPanelVisible);
        Assert.False(vm.ByojInstalled);
        Assert.Contains("no license", vm.ByojLicense);
        Assert.Contains("3276352374", vm.ByojSource);
        Assert.StartsWith("1.4 MB", vm.ByojSize);
        // Without the tick nothing happens.
        Assert.False(vm.InstallByojCommand.CanExecute(null));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("prepare"));

        List<string> opened = [];
        Func<ProcessStartInfo, Process?> previous = ExternalProcess.Start;
        ExternalProcess.Start = info => { opened.Add(info.FileName); return null; };
        try
        {
            vm.ByojConsent = true;
            Assert.True(vm.InstallByojCommand.CanExecute(null));
            await vm.InstallByojCommand.ExecuteAsync(null);
        }
        finally
        {
            ExternalProcess.Start = previous;
        }
        IReadOnlyList<string> prepare = Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "vtolvr", "mod", "prepare"));
        Assert.Contains("--accept-license", prepare);
        Assert.Equal(["steam://install/3018410", "steam://url/CommunityFilePage/3276352374"], opened);
        Assert.Contains("backed up", vm.ByojStatus);
        Assert.False(vm.ByojConsent);
        Assert.DoesNotContain(fake.Calls, c => NoviceGuiTests.Seq(c, "vtolvr", "refresh-db"));

        // Steam installed it: Check again builds the command list from the installed mod.
        fake.Installed = true;
        await vm.CheckByojCommand.ExecuteAsync(null);
        Assert.True(vm.ByojInstalled);
        Assert.Contains("SHA-256: " + ByojFakeEngine.Sha, vm.ByojDetails);
        Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "vtolvr", "refresh-db"));
        Assert.Contains("built from it", vm.ByojStatus);
    }

    // ------------------------------------------------------------------ Hell Let Loose: Vietnam's Oodle library

    private const string ForeignOodle = "D:\\Steam\\steamapps\\common\\Fake Shooter\\oo2core_9_win64.dll";

    [Fact]
    public async Task HllvOodle_NoLoadWithoutConsent_LoadAfterYes_AndOwnLibraryNeedsNoQuestion()
    {
        ByojFakeEngine fake = new();
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        GameCardItem hllv = new("HLLVietnam", "Hell Let Loose: Vietnam", "registry", "hllv", installed: true, canRefresh: true);
        IReadOnlyList<string> Refresh() => fake.Calls.Last(c => NoviceGuiTests.Seq(c, "hllv", "refresh-db"));

        // Nobody to ask (headless): nothing is assumed, the fallback list.
        await vm.UpdateGameCommandListCommand.ExecuteAsync(hllv);
        Assert.DoesNotContain("--oodle-dll", Refresh());
        Assert.Equal(string.Empty, vm.HllvOodleAnswer);

        // Asked once, with the facts; No: the fallback, remembered.
        List<ConfirmRequest> asked = [];
        vm.ConfirmHandler = request => { asked.Add(request); return Task.FromResult(false); };
        await vm.UpdateGameCommandListCommand.ExecuteAsync(hllv);
        ConfirmRequest question = Assert.Single(asked);
        Assert.Contains("Fake Shooter", question.Message);
        Assert.Contains("oo2core_9_win64.dll", question.Message);
        Assert.Contains("only to read Hell Let Loose: Vietnam's files", question.Message);
        Assert.Contains("RAD Game Tools / Epic Games", question.Message);
        Assert.Contains("private and for yourself only", question.Message);
        Assert.Contains("never copies or ships it", question.Message);
        Assert.DoesNotContain("--oodle-dll", Refresh());
        Assert.Equal("no", vm.HllvOodleAnswer);
        await vm.UpdateGameCommandListCommand.ExecuteAsync(hllv);
        Assert.Single(asked);
        Assert.DoesNotContain("--oodle-dll", Refresh());

        // Settings > Games: ask again, Yes: the library is passed (the engine loads it from where it is).
        vm.AskHllvOodleAgainCommand.Execute(null);
        vm.ConfirmHandler = request => { asked.Add(request); return Task.FromResult(true); };
        await vm.UpdateGameCommandListCommand.ExecuteAsync(hllv);
        Assert.Equal(2, asked.Count);
        Assert.True(NoviceGuiTests.Seq(Refresh(), "--oodle-dll", ForeignOodle));
        Assert.True(vm.HllvOodleAllowed);
        vm.HllvOodleAllowed = false;
        await vm.UpdateGameCommandListCommand.ExecuteAsync(hllv);
        Assert.DoesNotContain("--oodle-dll", Refresh());

        // The game ships its own library: no question, nothing to pass.
        fake.OwnOodle = true;
        vm.AskHllvOodleAgainCommand.Execute(null);
        await vm.UpdateGameCommandListCommand.ExecuteAsync(hllv);
        Assert.Equal(2, asked.Count);
        Assert.DoesNotContain("--oodle-dll", Refresh());
    }

    private sealed class ByojFakeEngine : IEngineCliClient
    {
        public const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool Installed { get; set; }
        public bool OwnOodle { get; set; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            string output = "ok";
            if (NoviceGuiTests.Seq(arguments, "hllv", "oodle"))
            {
                output = OwnOodle
                    ? "{\"OwnDll\":\"D:\\\\HLLV\\\\oo2core_9_win64.dll\",\"ForeignDll\":\"\",\"ForeignGame\":\"\",\"Maker\":\"RAD Game Tools / Epic Games\"}"
                    : "{\"OwnDll\":\"\",\"ForeignDll\":\"" + ForeignOodle.Replace("\\", "\\\\") + "\",\"ForeignGame\":\"Fake Shooter\",\"Maker\":\"RAD Game Tools / Epic Games\"}";
            }
            if (NoviceGuiTests.Seq(arguments, "vtolvr", "mod"))
            {
                bool prepare = arguments.Contains("prepare");
                output = "{\"ModLoaderInstalled\":" + (Installed ? "true" : "false") + ",\"ModInstalled\":" + (Installed ? "true" : "false") +
                         ",\"DllPath\":\"" + (Installed ? "D:\\\\Steam\\\\steamapps\\\\workshop\\\\content\\\\3018410\\\\3276352374\\\\BYOJoystick.dll" : string.Empty) + "\"" +
                         ",\"DllSha256\":\"" + (Installed ? Sha : string.Empty) + "\",\"Targets\":" + (Installed ? "433" : "0") +
                         ",\"WorkshopPage\":\"https://steamcommunity.com/sharedfiles/filedetails/?id=3276352374\",\"ListedSize\":1435000" +
                         ",\"License\":\"BYOJoystick by sicsix has no license: all rights reserved.\",\"Verification\":\"Steam checks it.\"" +
                         ",\"OpenUrls\":" + (prepare ? "[\"steam://install/3018410\",\"steam://url/CommunityFilePage/3276352374\"]" : "[]") +
                         ",\"BackedUp\":" + (prepare ? "[\"D:\\\\VTOL VR\\\\SaveData\\\\BYOJ_FA26B.xml\"]" : "[]") + ",\"Notes\":[],\"Warnings\":[]}";
            }
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }
    }

    private static string WritePhoto(string file)
    {
        using SKBitmap bitmap = new(400, 300);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.White);
            using SKPaint paint = new() { Color = new SKColor(30, 40, 60) };
            canvas.DrawRect(new SKRect(80, 30, 320, 270), paint);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(file, data.ToArray());
        return file;
    }

    private static async Task<bool> Done(Task task)
    {
        await task;
        return true;
    }
}
