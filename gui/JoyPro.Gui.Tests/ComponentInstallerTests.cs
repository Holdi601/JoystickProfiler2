using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Components;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The fakes every test run uses (TestEnvironment): nothing is detected on the PC, downloaded, verified or installed.
internal static class ComponentFakes
{
    public static IComponentProbe NothingInstalledProbe { get; } = new FakeComponentProbe();

    public static IComponentDownloader NoDownloader { get; } = new ThrowingDownloader();

    public static ISignatureVerifier NoSignatures { get; } = new FakeSignatureVerifier(SignatureCheck.Invalid("No signature is checked in the tests."));

    public static IInstallerRunner NoRunner { get; } = new ThrowingRunner();

    private sealed class ThrowingDownloader : IComponentDownloader
    {
        public Task DownloadAsync(string url, string target, IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The GUI tests download nothing (" + url + ").");
    }

    private sealed class ThrowingRunner : IInstallerRunner
    {
        public Task<InstallerRun> RunElevatedAsync(string installer, string arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The GUI tests start no installer (" + installer + ").");
    }
}

internal sealed class FakeComponentProbe : IComponentProbe
{
    public Dictionary<string, ComponentState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }

    public ComponentState Probe(ComponentSpec spec)
    {
        Calls++;
        return States.TryGetValue(spec.Id, out ComponentState? state) ? state : ComponentState.Missing;
    }
}

internal sealed class FakeDownloader(byte[] content) : IComponentDownloader
{
    public List<string> Urls { get; } = [];

    public Exception? Failure { get; set; }

    public Task DownloadAsync(string url, string target, IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken)
    {
        Urls.Add(url);
        if (Failure != null)
            throw Failure;
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllBytes(target, content);
        progress?.Report((content.Length / 2, content.Length));
        progress?.Report((content.Length, content.Length));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSignatureVerifier(SignatureCheck result) : ISignatureVerifier
{
    public SignatureCheck Result { get; set; } = result;

    public List<string> Files { get; } = [];

    public SignatureCheck Verify(string file)
    {
        Files.Add(file);
        return Result;
    }
}

internal sealed class FakeInstallerRunner(InstallerRun result) : IInstallerRunner
{
    public InstallerRun Result { get; set; } = result;

    public List<(string Installer, string Arguments)> Runs { get; } = [];

    // Runs while the "installer" runs (the test changes what the probe sees, or tries to touch the file).
    public Action<string>? During { get; set; }

    public Task<InstallerRun> RunElevatedAsync(string installer, string arguments, CancellationToken cancellationToken)
    {
        Runs.Add((installer, arguments));
        During?.Invoke(installer);
        return Task.FromResult(Result);
    }
}

// The one-click component installer (docs/components.md) against fakes: no network, no signature of a real file, no
// installer, no UAC prompt.
[SupportedOSPlatform("windows")]
public sealed class ComponentInstallerTests : IDisposable
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("MZ fake installer for the JoyPro GUI tests");

    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "components-" + Guid.NewGuid().ToString("N"));
    private readonly IComponentProbe oldProbe = ComponentServices.Probe;
    private readonly IComponentDownloader oldDownloader = ComponentServices.Downloader;
    private readonly ISignatureVerifier oldVerifier = ComponentServices.SignatureVerifier;
    private readonly IInstallerRunner oldRunner = ComponentServices.Runner;
    private readonly Func<string> oldFolder = ComponentServices.DownloadFolder;

    private readonly FakeComponentProbe probe = new();
    private readonly FakeDownloader downloader = new(Payload);
    private readonly FakeSignatureVerifier verifier = new(new SignatureCheck(true, "Test Publisher e.U.", "CN=Test Publisher e.U., O=Test Publisher e.U., C=AT", string.Empty));
    private readonly FakeInstallerRunner runner = new(new InstallerRun(true, 0, false, string.Empty));

    public ComponentInstallerTests()
    {
        Directory.CreateDirectory(folder);
        ComponentServices.Probe = probe;
        ComponentServices.Downloader = downloader;
        ComponentServices.SignatureVerifier = verifier;
        ComponentServices.Runner = runner;
        ComponentServices.DownloadFolder = () => folder;
    }

    public void Dispose()
    {
        ComponentServices.Probe = oldProbe;
        ComponentServices.Downloader = oldDownloader;
        ComponentServices.SignatureVerifier = oldVerifier;
        ComponentServices.Runner = oldRunner;
        ComponentServices.DownloadFolder = oldFolder;
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    private static ComponentSpec Spec(InstallerKind kind = InstallerKind.AdvancedInstallerMsi, bool driver = true, byte[]? content = null) => ComponentCatalog.HidHide with
    {
        Id = "testdriver",
        Name = "TestDriver",
        Version = "1.2.3",
        DownloadUrl = "https://example.invalid/TestDriver_1.2.3.exe",
        FileName = "TestDriver_1.2.3.exe",
        SizeBytes = (content ?? Payload).Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(content ?? Payload)).ToLowerInvariant(),
        SignerOrganization = "Test Publisher e.U.",
        Kind = kind,
        InstallerArguments = kind == InstallerKind.InnoSetup ? "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-" : "/passive /norestart",
        IsDriver = driver,
        ServiceName = "TestDriver"
    };

    private void InstalledAfterRun(ComponentSpec spec, bool running = true, string version = "1.2.3") =>
        runner.During = _ => probe.States[spec.Id] = new ComponentState { Installed = true, Running = running, Version = version };

    [Fact]
    public async Task WithoutConsent_NothingIsDownloadedOrRun()
    {
        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(), consent: false, progress: null);

        Assert.False(result.Ok);
        Assert.Equal(ComponentInstallStage.Consent, result.FailedAt);
        Assert.Contains("I agree", result.Message);
        Assert.Empty(downloader.Urls);
        Assert.Empty(runner.Runs);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task OneClick_DownloadsVerifiesRunsOnceAndDetectsAgain()
    {
        ComponentSpec spec = Spec();
        InstalledAfterRun(spec);
        List<ComponentProgress> steps = [];

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(spec, consent: true, new SyncProgress<ComponentProgress>(steps.Add));

        Assert.True(result.Ok, result.Message);
        Assert.False(result.RestartRequired);
        Assert.Equal([spec.DownloadUrl], downloader.Urls);
        Assert.Single(verifier.Files);
        (string installer, string arguments) = Assert.Single(runner.Runs);
        Assert.Equal(Path.Combine(folder, spec.FileName), installer);
        Assert.Equal("/passive /norestart", arguments);
        Assert.Contains("TestDriver 1.2.3 is installed", result.Message);
        Assert.False(File.Exists(installer)); // the installer is deleted after a successful install
        Assert.False(File.Exists(installer + ".part"));
        List<ComponentInstallStage> order = steps.Select(s => s.Stage).Distinct().ToList();
        Assert.Equal([ComponentInstallStage.Download, ComponentInstallStage.Verify, ComponentInstallStage.Install, ComponentInstallStage.Check], order);
        Assert.Contains(steps, s => s.Stage == ComponentInstallStage.Install && s.Text.Contains("confirm Windows' prompt"));
        Assert.Contains(steps, s => s.Stage == ComponentInstallStage.Download && s.Percent >= 100);
    }

    [Fact]
    public async Task WrongChecksum_IsRefusedDeletedAndNeverRun()
    {
        ComponentSpec spec = Spec() with { Sha256 = new string('0', 64) };

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(spec, true, null);

        Assert.False(result.Ok);
        Assert.Equal(ComponentInstallStage.Verify, result.FailedAt);
        Assert.Contains("not the expected file", result.Message);
        Assert.Contains("nothing was installed", result.Message);
        Assert.Empty(runner.Runs);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task WrongSize_IsRefused()
    {
        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec() with { SizeBytes = Payload.Length + 1 }, true, null);

        Assert.Equal(ComponentInstallStage.Verify, result.FailedAt);
        Assert.Contains("bytes, expected", result.Message);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task InvalidSignature_IsRefused()
    {
        verifier.Result = SignatureCheck.Invalid("The file TestDriver_1.2.3.exe has no valid signature.");

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(), true, null);

        Assert.Equal(ComponentInstallStage.Verify, result.FailedAt);
        Assert.Contains("no valid signature", result.Message);
        Assert.Empty(runner.Runs);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task ValidSignatureOfAnotherPublisher_IsRefused()
    {
        verifier.Result = new SignatureCheck(true, "Someone Else Ltd", "CN=Someone Else Ltd, O=Someone Else Ltd", string.Empty);

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(), true, null);

        Assert.Equal(ComponentInstallStage.Verify, result.FailedAt);
        Assert.Contains("signed by \"Someone Else Ltd\", expected \"Test Publisher e.U.\"", result.Message);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task DeclinedUacPrompt_IsCancelledAndSaysSo()
    {
        runner.Result = InstallerRun.DeclinedByUser;

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(), true, null);

        Assert.False(result.Ok);
        Assert.True(result.Cancelled);
        Assert.Contains("administrator prompt was declined", result.Message);
        Assert.True(File.Exists(Path.Combine(folder, "TestDriver_1.2.3.exe"))); // kept: the next click needs no download
    }

    [Fact]
    public async Task VerifiedFile_IsReusedWithoutDownloadingAgain()
    {
        ComponentSpec spec = Spec();
        File.WriteAllBytes(Path.Combine(folder, spec.FileName), Payload);
        InstalledAfterRun(spec);

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(spec, true, null);

        Assert.True(result.Ok, result.Message);
        Assert.Empty(downloader.Urls);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task TamperedCachedFile_IsDownloadedAgain()
    {
        ComponentSpec spec = Spec();
        File.WriteAllBytes(Path.Combine(folder, spec.FileName), Encoding.ASCII.GetBytes("tampered"));
        InstalledAfterRun(spec);

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(spec, true, null);

        Assert.True(result.Ok, result.Message);
        Assert.Single(downloader.Urls);
    }

    [Fact]
    public async Task VerifiedFile_CannotBeChangedWhileTheInstallerRuns()
    {
        ComponentSpec spec = Spec();
        bool locked = false;
        runner.During = file =>
        {
            try
            {
                using FileStream write = new(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                locked = true;
            }
            probe.States[spec.Id] = new ComponentState { Installed = true, Running = true, Version = "1.2.3" };
        };

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(spec, true, null);

        Assert.True(locked, "the checked installer must stay locked against writes until the installer has ended");
        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task DownloadFailure_TellsWhatToDoAndRunsNothing()
    {
        downloader.Failure = new HttpRequestException("No such host is known.");

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(), true, null);

        Assert.Equal(ComponentInstallStage.Download, result.FailedAt);
        Assert.Contains("No such host is known.", result.Message);
        Assert.Contains("internet connection", result.Message);
        Assert.Empty(runner.Runs);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task CancelledDownload_LeavesNothingBehind()
    {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(), true, null, cancel.Token);

        Assert.True(result.Cancelled);
        Assert.Empty(runner.Runs);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task MsiRestartCode_IsSuccessWithRestart()
    {
        ComponentSpec spec = Spec();
        runner.Result = new InstallerRun(true, 3010, false, string.Empty);
        InstalledAfterRun(spec, running: false);

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(spec, true, null);

        Assert.True(result.Ok, result.Message);
        Assert.True(result.RestartRequired);
        Assert.Contains("Restart Windows", result.Message);
    }

    [Fact]
    public async Task DriverInstalledButNotRunning_AsksForARestart()
    {
        ComponentSpec spec = Spec(InstallerKind.InnoSetup);
        InstalledAfterRun(spec, running: false);

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(spec, true, null);

        Assert.True(result.Ok, result.Message);
        Assert.True(result.RestartRequired);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-", runner.Runs.Single().Arguments);
    }

    [Fact]
    public async Task SetupEndedButNothingDetected_IsNoSuccessAndAsksForARestart()
    {
        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(), true, null);

        Assert.False(result.Ok);
        Assert.True(result.RestartRequired);
        Assert.Equal(ComponentInstallStage.Check, result.FailedAt);
        Assert.Contains("Restart Windows and press Check again", result.Message);
    }

    [Theory]
    [InlineData(InstallerKind.InnoSetup, 2, "cancelled")]
    [InlineData(InstallerKind.InnoSetup, 5, "cancelled")]
    [InlineData(InstallerKind.InnoSetup, 8, "needs Windows to restart first")]
    [InlineData(InstallerKind.InnoSetup, 4, "ended with code 4")]
    [InlineData(InstallerKind.AdvancedInstallerMsi, 1602, "cancelled")]
    [InlineData(InstallerKind.AdvancedInstallerMsi, 1618, "Another installation is running")]
    [InlineData(InstallerKind.AdvancedInstallerMsi, 1603, "ended with code 1603")]
    public async Task InstallerExitCodes_AreExplained(InstallerKind kind, int code, string expected)
    {
        runner.Result = new InstallerRun(true, code, false, string.Empty);

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(Spec(kind), true, null);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the pinned catalog

    [Fact]
    public void Catalog_PinsOfficialReleasesWithChecksumSignerAndLicense()
    {
        Assert.Equal(["vjoy", "hidhide", "vigembus"], ComponentCatalog.All.Select(c => c.Id).ToArray());
        foreach (ComponentSpec spec in ComponentCatalog.All)
        {
            Assert.Matches("^[0-9a-f]{64}$", spec.Sha256);
            Assert.True(spec.SizeBytes > 1_000_000, spec.Name);
            Assert.StartsWith("https://github.com/", spec.DownloadUrl);
            Assert.Contains("/releases/download/", spec.DownloadUrl);
            Assert.StartsWith(spec.ProjectUrl + "/", spec.DownloadUrl);
            Assert.StartsWith(spec.ProjectUrl + "/", spec.ReleasePageUrl);
            Assert.StartsWith(spec.ProjectUrl + "/", spec.LicenseUrl);
            Assert.False(string.IsNullOrWhiteSpace(spec.SignerOrganization));
            Assert.Contains(spec.LicenseName, spec.LicenseText);
            Assert.Contains("\"AS IS\"", spec.LicenseText);
            Assert.True(spec.IsDriver);
            Assert.EndsWith(".exe", spec.FileName);
            Assert.DoesNotMatch(@"[\\/]", spec.FileName);
            Assert.Contains("norestart", spec.InstallerArguments, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("On-site Dental Systems (Justin Shafer)", ComponentCatalog.VJoy.SignerOrganization);
        Assert.Equal("Nefarius Software Solutions e.U.", ComponentCatalog.HidHide.SignerOrganization);
        Assert.Equal("Nefarius Software Solutions e.U.", ComponentCatalog.ViGEmBus.SignerOrganization);
        Assert.Contains("jshafer817/vJoy/releases/download/v2.1.9.1/", ComponentCatalog.VJoy.DownloadUrl);
        Assert.Equal(InstallerKind.InnoSetup, ComponentCatalog.VJoy.Kind);
        Assert.Equal(@"x64\vJoyInterface.dll", ComponentCatalog.VJoy.RequiredFile);
    }

    [Theory]
    [InlineData("CN=Nefarius Software Solutions e.U., O=Nefarius Software Solutions e.U., L=Wels, C=AT, SERIALNUMBER=505039f", "Nefarius Software Solutions e.U.")]
    [InlineData("CN=On-site Dental Systems (Justin Shafer), O=On-site Dental Systems (Justin Shafer), L=North Richland Hills, S=Texas, C=US", "On-site Dental Systems (Justin Shafer)")]
    [InlineData("CN=DigiCert, O=\"DigiCert, Inc.\", C=US", "DigiCert, Inc.")]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("CN=Someone, C=US", "")]
    public void SignerOrganization_IsTheExactOValue(string subject, string organization)
    {
        Assert.Equal(organization, AuthenticodeVerifier.OrganizationOf(subject));
    }

    [Theory]
    [InlineData("1.5.230", "1.5.230", 0)]
    [InlineData("1.5.230.0", "1.5.230", 0)]
    [InlineData("1.5.212", "1.5.230", -1)]
    [InlineData("2.2.2.0", "2.1.9.1", 1)]
    [InlineData("", "1.0", 0)]
    [InlineData("v1.22.0", "1.22.0", 0)]
    public void Versions_Compare(string installed, string pinned, int expected)
    {
        Assert.Equal(expected, ComponentVersion.Compare(installed, pinned));
    }

    // ---- the card

    [Fact]
    public async Task Card_InstallNeedsTheTickAndOffersOnlyInstallOrUpdate()
    {
        ComponentSpec spec = Spec();
        ComponentInstallGate gate = new();
        ComponentInstallResult? reported = null;
        ComponentItem item = new(spec, gate, (_, r) => reported = r);
        item.Refresh();

        Assert.Equal("Not installed.", item.StatusText);
        Assert.True(item.CanOffer);
        Assert.False(item.CanInstall); // no tick yet
        Assert.Equal("Install TestDriver", item.InstallButtonText);
        Assert.Contains("SHA-256", item.VerificationText);
        Assert.Contains("Test Publisher e.U.", item.VerificationText);

        await item.InstallCommand.ExecuteAsync(null);
        Assert.Empty(runner.Runs);
        Assert.Contains("I agree", item.Message);

        item.Consent = true;
        Assert.True(item.CanInstall);
        InstalledAfterRun(spec);
        await item.InstallCommand.ExecuteAsync(null);

        Assert.Single(runner.Runs);
        Assert.NotNull(reported);
        Assert.True(item.IsOk);
        Assert.False(item.CanOffer); // never a reinstall over the same version
        Assert.False(item.Consent);  // consent belongs to one install
        Assert.Equal("Installed 1.2.3 and running.", item.StatusText);
        Assert.Null(gate.Running);

        probe.States[spec.Id] = new ComponentState { Installed = true, Running = true, Version = "1.0.0" };
        item.Refresh();
        Assert.True(item.IsOutdated);
        Assert.Equal("Update to 1.2.3", item.InstallButtonText);

        probe.States[spec.Id] = new ComponentState { Installed = true, Running = true, Version = "9.0" };
        item.Refresh();
        Assert.False(item.CanOffer); // never a downgrade
        Assert.Contains("newer than the version JoyPro was tested with", item.StatusText);

        probe.States[spec.Id] = new ComponentState { Installed = true, Running = false, Version = "1.2.3" };
        item.Refresh();
        Assert.False(item.IsOk);
        Assert.Contains("restart Windows", item.StatusText);
    }

    [Fact]
    public async Task Card_OneInstallerAtATime()
    {
        ComponentInstallGate gate = new();
        ComponentItem first = new(Spec(), gate);
        ComponentItem second = new(Spec() with { Id = "other", Name = "Other" }, gate) { Consent = true };
        gate.Running = first;

        await second.InstallCommand.ExecuteAsync(null);

        Assert.Empty(runner.Runs);
        Assert.Contains("TestDriver is being installed", second.Message);
    }

    [Fact]
    public void Card_RepairAndUninstallOpenWindowsAppsSettings()
    {
        List<string> opened = [];
        Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?> old = ExternalProcess.Start;
        ExternalProcess.Start = info =>
        {
            opened.Add(info.FileName);
            return null;
        };
        try
        {
            ComponentItem item = new(ComponentCatalog.ViGEmBus, new ComponentInstallGate());
            item.OpenAppsSettingsCommand.Execute(null);
            item.OpenProjectCommand.Execute(null);
            item.OpenLicenseCommand.Execute(null);
        }
        finally
        {
            ExternalProcess.Start = old;
        }
        Assert.Equal(["ms-settings:appsfeatures", ComponentCatalog.ViGEmBus.ReleasePageUrl, ComponentCatalog.ViGEmBus.LicenseUrl], opened.ToArray());
    }

    // ---- the view model

    [Fact]
    public async Task ViewModel_ChecksWhenSettingsOpensAndLinksTheLocalAiModel()
    {
        probe.States["vjoy"] = new ComponentState { Installed = true, Running = true, Version = "2.1.9.1" };
        probe.States["hidhide"] = new ComponentState { Installed = true, Running = true, Version = "1.5.230" };
        DescribeGroupFakeEngine engine = new();
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };

        Assert.Equal(["vjoy", "hidhide", "vigembus"], vm.Components.Select(c => c.Id).ToArray());
        Assert.Equal(vm.Components.ToArray(), vm.VirtualJoystickComponents.ToArray()); // the same items on both pages
        Assert.Equal("Not checked yet.", vm.ComponentsSummary);
        Assert.Equal(0, probe.Calls);

        vm.CurrentStage = UiStage.Settings;

        Assert.Equal(3, probe.Calls);
        Assert.True(vm.FindComponent("vjoy")!.IsOk);
        Assert.False(vm.FindComponent("vigembus")!.IsOk);
        Assert.Contains("ViGEmBus", vm.ComponentsSummary);
        Assert.DoesNotContain("vJoy", vm.ComponentsSummary);

        await vm.OpenLlmSetupCommand.ExecuteAsync(null);

        Assert.Equal(UiStage.Relations, vm.CurrentStage);
        Assert.True(vm.IsDescribeGroupPanelOpen);
        Assert.True(vm.IsLlmSectionExpanded);
        Assert.Contains(engine.Calls, c => AxisTuningTests.Seq(c, "llm", "status"));
        Assert.DoesNotContain(engine.Calls, c => AxisTuningTests.Seq(c, "llm", "download"));
    }

    // Offscreen pictures of the Settings "Components" card and the Virtual Joystick page's "What this page needs" (opt-in
    // like the other render tests):
    //   set JOYPRO_GUI_PARITY_PREVIEW=%TEMP%\JoyPro2.Gui.Tests\gui-parity & dotnet test gui/JoyPro.Gui.Tests --filter RenderComponentCards
    [Fact]
    public void RenderComponentCards()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_GUI_PARITY_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        probe.States["hidhide"] = new ComponentState { Installed = true, Running = true, Version = "1.5.230" };
        probe.States["vigembus"] = new ComponentState { Installed = true, Running = false, Version = "1.22.0" };
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new DescribeGroupFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location
            };
            vm.CurrentStage = UiStage.Settings;
            vm.FindComponent("vjoy")!.Consent = true;
            vm.ExpandAllSettingsCommand.Execute(null);
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "settings-components.png"), "Components", 1900, page =>
            {
                List<Avalonia.Controls.Button> buttons = page.GetVisualDescendantsOfType<Avalonia.Controls.Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.Contains(buttons, b => (b.Content as string) == "Install vJoy" && b.IsEffectivelyEnabled);
                Assert.DoesNotContain(buttons, b => (b.Content as string) == "Install HidHide"); // installed: no reinstall
                Assert.Contains(buttons, b => (b.Content as string) == "Set up");
                Assert.Contains(buttons, b => (b.Content as string) == "Windows apps");
            });
            vm.CurrentStage = UiStage.VirtualJoystick;
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "virtual-joystick-components.png"), "What this page needs", 1500, page =>
            {
                List<string> texts = page.GetVisualDescendantsOfType<Avalonia.Controls.TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, t => t.Contains("restart Windows"));
                Assert.Contains(page.GetVisualDescendantsOfType<Avalonia.Controls.Button>(), b => (b.Content as string) == "Install vJoy" && b.IsEffectivelyVisible);
            });
        });
    }

    // .NET Framework 4.8 ("Needed by JoyPro") goes through the same download, signature and elevated-run seams.
    [Fact]
    public async Task Requirements_NetFrameworkInstallNeedsMicrosoftsSignature()
    {
        RequirementStatus framework = new() { Name = ".NET Framework 4.8", DownloadUrl = "https://example.invalid/ndp48-web.exe", InstallerArguments = "/passive /norestart" };
        verifier.Result = new SignatureCheck(true, "Test Publisher e.U.", "CN=Test, O=Test Publisher e.U.", string.Empty);

        string refused = await RequirementsChecker.InstallAsync(framework, folder, null);

        Assert.Contains("not signed by Microsoft", refused);
        Assert.Empty(runner.Runs);

        verifier.Result = new SignatureCheck(true, "Microsoft Corporation", "CN=Microsoft Corporation, O=Microsoft Corporation", string.Empty);
        string ok = await RequirementsChecker.InstallAsync(framework, folder, null);

        Assert.Equal(string.Empty, ok);
        Assert.Equal("/passive /norestart", runner.Runs.Single().Arguments);
    }

    // The default fakes of every test run (TestEnvironment) refuse to download or install.
    [Fact]
    public async Task TestRunDefaults_NeverDownloadOrInstall()
    {
        ComponentServices.Downloader = ComponentFakes.NoDownloader;
        ComponentServices.Runner = ComponentFakes.NoRunner;
        ComponentServices.SignatureVerifier = ComponentFakes.NoSignatures;

        ComponentInstallResult result = await ComponentInstaller.InstallAsync(ComponentCatalog.VJoy, true, null);

        Assert.False(result.Ok);
        Assert.Equal(ComponentInstallStage.Download, result.FailedAt);
        Assert.Contains("download nothing", result.Message);
        Assert.Empty(Directory.GetFiles(folder));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
