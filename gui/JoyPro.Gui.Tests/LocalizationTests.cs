using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using JoyPro.Gui.Localization;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.LocTool;
using Xunit;

namespace JoyPro.Gui.Tests;

// The localization (docs/localization.md): English text as the key, fallback to English, plural rules, the system
// language, engine texts by pattern, the XAML markup extension switching live, and the strings files themselves
// (German has every text the GUI already sends through the localization; no translation breaks a placeholder).
[SupportedOSPlatform("windows")]
public sealed class LocalizationTests
{
    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("en-US", "en")]
    [InlineData("fr-CA", "fr")]
    [InlineData("pt-BR", "pt")]
    [InlineData("sr-Latn-RS", "sr-Latn")]
    [InlineData("sr-Cyrl-RS", "sr-Cyrl")]
    [InlineData("sr-Latn-ME", "sr-Latn")]
    [InlineData("bs-Latn-BA", "bs")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("nb-NO", "nb")]
    [InlineData("nn-NO", "nb")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("ga-IE", "ga")]
    [InlineData("be-BY", "be")]
    [InlineData("sw-KE", "en")]
    public void SystemLanguage_MapsToASupportedLanguage(string culture, string expected)
    {
        Assert.Equal(expected, Languages.ForCulture(CultureInfo.GetCultureInfo(culture)).Code);
    }

    [Fact]
    public void Languages_AreTheRequestedSet()
    {
        string[] codes = Languages.All.Select(l => l.Code).ToArray();
        string[] expected =
        [
            "en", "bg", "hr", "cs", "da", "nl", "et", "fi", "fr", "de", "el", "hu", "ga", "it", "lv", "lt", "mt", "pl", "pt", "ro",
            "sk", "sl", "es", "sv", "nb", "is", "sq", "bs", "sr-Latn", "sr-Cyrl", "mk", "ru", "uk", "be", "tr", "ja", "ko", "zh-Hans", "zh-Hant"
        ];
        Assert.Equal(expected.OrderBy(c => c, StringComparer.Ordinal), codes.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal("en", codes[0]);
    }

    [Theory]
    // count, then the form index per language
    [InlineData(PluralRule.OneOther, new long[] { 0, 1, 2, 5, 21 }, new[] { 1, 0, 1, 1, 1 })]
    [InlineData(PluralRule.ZeroOneOther, new long[] { 0, 1, 2 }, new[] { 0, 0, 1 })]
    [InlineData(PluralRule.EastSlavic, new long[] { 0, 1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 111, 112, 121 }, new[] { 2, 0, 1, 1, 2, 2, 2, 2, 0, 1, 2, 2, 2, 0 })]
    [InlineData(PluralRule.Polish, new long[] { 0, 1, 2, 5, 12, 22, 25 }, new[] { 2, 0, 1, 2, 2, 1, 2 })]
    [InlineData(PluralRule.CzechSlovak, new long[] { 0, 1, 2, 4, 5, 22 }, new[] { 2, 0, 1, 1, 2, 2 })]
    [InlineData(PluralRule.Slovenian, new long[] { 1, 2, 3, 4, 5, 101, 102 }, new[] { 0, 1, 2, 2, 3, 0, 1 })]
    [InlineData(PluralRule.Lithuanian, new long[] { 1, 2, 9, 10, 11, 21, 22 }, new[] { 0, 1, 1, 2, 2, 0, 1 })]
    [InlineData(PluralRule.Latvian, new long[] { 0, 1, 2, 11, 21 }, new[] { 0, 1, 2, 0, 1 })]
    [InlineData(PluralRule.Romanian, new long[] { 0, 1, 2, 19, 20, 101, 102 }, new[] { 1, 0, 1, 1, 2, 2, 1 })]
    [InlineData(PluralRule.Irish, new long[] { 1, 2, 3, 6, 7, 10, 11 }, new[] { 0, 1, 2, 2, 3, 3, 4 })]
    [InlineData(PluralRule.Maltese, new long[] { 0, 1, 2, 3, 10, 11, 19, 20 }, new[] { 2, 0, 1, 2, 2, 3, 3, 4 })]
    [InlineData(PluralRule.OneEndingOther, new long[] { 1, 11, 21, 2 }, new[] { 0, 1, 0, 1 })]
    [InlineData(PluralRule.None, new long[] { 0, 1, 2 }, new[] { 0, 0, 0 })]
    public void PluralRules_FollowCldr(PluralRule rule, long[] counts, int[] forms)
    {
        for (int i = 0; i < counts.Length; i++)
            Assert.True(forms[i] == Languages.PluralIndex(rule, counts[i]), $"{rule} {counts[i]}: expected form {forms[i]}, got {Languages.PluralIndex(rule, counts[i])}");
        Assert.True(forms.Max() < Languages.PluralCount(rule));
    }

    [Fact]
    public void T_TranslatesFormatsAndFallsBackToEnglish()
    {
        using TempStrings strings = new(new()
        {
            ["de"] = """{"Bind here": "Hier belegen", "{0} binds on {1}": "{0} Belegungen auf {1}", "Broken {0}": "Kaputt {1}", "{0} bind": ["{0} Belegung", "{0} Belegungen"]}""",
            ["ru"] = """{"{0} bind": ["{0} привязка", "{0} привязки", "{0} привязок"]}"""
        });
        Loc.SetLanguage("de");
        Assert.Equal("Hier belegen", Loc.T("Bind here"));
        Assert.Equal("Not translated", Loc.T("Not translated"));
        Assert.Equal("3 Belegungen auf Warthog", Loc.T("{0} binds on {1}", 3, "Warthog"));
        // a translation with the wrong placeholder: the English text
        Assert.Equal("Broken 7", Loc.T("Broken {0}", 7));
        Assert.Equal("1 Belegung", Loc.Plural("{0} bind", "{0} binds", 1));
        Assert.Equal("2 Belegungen", Loc.Plural("{0} bind", "{0} binds", 2));
        Assert.Equal("5 relations", Loc.Plural("{0} relation", "{0} relations", 5));
        Assert.Equal("1 relation", Loc.Plural("{0} relation", "{0} relations", 1));
        Loc.SetLanguage("ru");
        Assert.Equal("1 привязка", Loc.Plural("{0} bind", "{0} binds", 1));
        Assert.Equal("3 привязки", Loc.Plural("{0} bind", "{0} binds", 3));
        Assert.Equal("5 привязок", Loc.Plural("{0} bind", "{0} binds", 5));
        Assert.Equal("21 привязка", Loc.Plural("{0} bind", "{0} binds", 21));
        // Belarusian falls back to Russian, then English.
        Loc.SetLanguage("be");
        Assert.Equal("2 привязки", Loc.Plural("{0} bind", "{0} binds", 2));
        Assert.Equal("Bind here", Loc.T("Bind here"));
        Loc.SetLanguage("en");
        Assert.Equal("Bind here", Loc.T("Bind here"));
        Assert.Equal("2 binds", Loc.Plural("{0} bind", "{0} binds", 2));
    }

    // The chips and labels of the main pages that used to stay English: binds of a modifier, an action's kind, the
    // device kind, the Cockpits filters (English values shown translated).
    [Fact]
    public void MainPageChips_FollowTheLanguage()
    {
        try
        {
            Loc.SetLanguage("de");
            Assert.Equal("3 Belegungen", new JoyPro.Gui.Models.ModifierListItem("Shift", 3, string.Empty, string.Empty).UsageText);
            Assert.Equal("1 Belegung", new JoyPro.Gui.Models.ModifierListItem("Shift", 1, string.Empty, string.Empty).UsageText);
            Assert.EndsWith("Achse", new JoyPro.Gui.Models.RelationNodeItem { Game = "IL2Game", KindLabel = "axis" }.DetailsText);
            Assert.Equal("Pedale", DeviceReferenceViewModel.KindName("pedals"));
            Assert.Equal("Alle", TConverter.Instance.Convert("All", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Starrflügler", TConverter.Instance.Convert("Fixed-wing", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Alle Spiele (Basis)", Loc.T("All games (base)"));
            // A status line written in German is shown in the next language (live language change).
            LanguageInfo german = Loc.Current;
            string germanReady = Loc.T("Ready.");
            Loc.SetLanguage("pl");
            Assert.NotEqual(germanReady, Loc.T("Ready."));
            Assert.Equal(Loc.T("Ready."), Loc.Retranslate(germanReady, german));
            Assert.Equal("Free text", Loc.Retranslate("Free text", german));
            Assert.Equal("5 przypisań", new JoyPro.Gui.Models.ModifierListItem("Shift", 5, string.Empty, string.Empty).UsageText);
        }
        finally
        {
            Loc.SetLanguage("en");
        }
        Assert.Equal("2 binds", new JoyPro.Gui.Models.ModifierListItem("Shift", 2, string.Empty, string.Empty).UsageText);
        Assert.Equal("All", TConverter.Instance.Convert("All", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Translate_FindsThePatternOfAnEngineLine()
    {
        using TempStrings strings = new(new()
        {
            ["de"] = """{"Exported {0} binds to {1}.": "{0} Belegungen nach {1} exportiert.", "Done.": "Fertig.", "{0}: {1}": "{0}: {1}"}"""
        });
        Loc.SetLanguage("de");
        Assert.Equal("12 Belegungen nach DCS World exportiert.", Loc.Translate("Exported 12 binds to DCS World."));
        Assert.Equal("Fertig.", Loc.Translate("Done."));
        // a pattern of placeholders only is never used; an unknown line stays as it is
        Assert.Equal("Something else: here", Loc.Translate("Something else: here"));
        Assert.Equal("Fertig." + Environment.NewLine + "x", Loc.TranslateLines("Done.\nx"));
    }

    [Fact]
    public void SetLanguage_EmptyMeansTheWindowsLanguage()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fi-FI");
            Loc.SetLanguage(string.Empty);
            Assert.Equal("fi", Loc.Current.Code);
            Assert.Equal(string.Empty, Loc.Choice);
            Loc.SetLanguage("system");
            Assert.Equal("fi", Loc.Current.Code);
            Loc.SetLanguage("ja");
            Assert.Equal("ja", Loc.Choice);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void Extractor_FindsWrappedAndPlainTexts()
    {
        string xaml = """
            <Window xmlns="https://github.com/avaloniaui" xmlns:l="using:JoyPro.Gui.Localization" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
                <TextBlock Text="{l:T 'Bind here'}" />
                <TextBlock Text="{l:T 'It\'s {0} binds', Arg0={Binding N}}" />
                <TextBlock Text="{l:T '{0} bind', Plural='{0} binds', Count={Binding N}}" />
                <Button Content="Export" ToolTip.Tip="Writes the binds into the game" />
                <TextBlock Text="{Binding Count, StringFormat='Size: {0}'}" />
                <TextBlock Tag="dcs" Text="{Binding Id}" />
                <TextBlock>Some text</TextBlock>
                <x:String>strict</x:String>
              </StackPanel>
            </Window>
            """;
        List<FoundText> found = LocExtractor.ScanAxaml("a.axaml", xaml);
        Assert.Contains(found, f => f.Wrapped && f.Text == "Bind here");
        Assert.Contains(found, f => f.Wrapped && f.Text == "It's {0} binds");
        Assert.Contains(found, f => f.Wrapped && f.Text == "{0} bind" && f.Plural == "{0} binds");
        Assert.Contains(found, f => !f.Wrapped && f.Text == "Export");
        Assert.Contains(found, f => !f.Wrapped && f.Text == "Writes the binds into the game");
        Assert.Contains(found, f => !f.Wrapped && f.Text == "Size: {0}");
        Assert.Contains(found, f => !f.Wrapped && f.Text == "Some text");
        Assert.DoesNotContain(found, f => f.Text is "dcs" or "strict");

        string cs = """
            class A {
                void M(int n, string game) {
                    StatusText = Loc.T("Bind here");
                    StatusText = Loc.Plural("{0} relation", "{0} relations", n);
                    StatusText = "Exported " + n + " binds to " + game + ".";
                    StatusText = $"Imported {n} binds from {game}.";
                    Log("Engine started");   // a log line
                    if (game == "WarThunder") return;
                    string id = game switch { "dcs" => "DCS World", _ => "Other game" };
                    string path = Path.Combine("a", "Saved Games");
                    // "A comment"
                    Hint = "Press a button on the device";
                }
            }
            """;
        found = LocExtractor.ScanCSharp("a.cs", cs);
        Assert.Contains(found, f => f.Wrapped && f.Text == "Bind here");
        Assert.Contains(found, f => f.Wrapped && f.Text == "{0} relation" && f.Plural == "{0} relations");
        Assert.Contains(found, f => !f.Wrapped && f.Text == "Exported {0} binds to {1}.");
        Assert.Contains(found, f => !f.Wrapped && f.Text == "Imported {0} binds from {1}.");
        Assert.Contains(found, f => f.Text == "DCS World");
        Assert.Contains(found, f => f.Text == "Other game");
        Assert.Contains(found, f => f.Text == "Press a button on the device");
        Assert.DoesNotContain(found, f => f.Text is "Engine started" or "WarThunder" or "dcs" or "Saved Games" or "A comment" or "{0} relations");
    }

    // The .axaml rewrite: literal texts and StringFormat bindings to {l:T}, a text starting with a brace escaped, the
    // xmlns added; ids and bindings stay. The extractor reads the result back as the same keys.
    [Fact]
    public void AxamlWrapper_WrapsLiteralTexts()
    {
        string xaml = "<Window xmlns=\"https://github.com/avaloniaui\"\r\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\r\n"
            + "  <Button Content=\"Export\" ToolTip.Tip=\"Settings &gt; Games: it&apos;s here\" Tag=\"Keep me\" />\r\n"
            + "  <TextBlock Text=\"{Binding Count, StringFormat='{}{0} in your profile'}\" />\r\n"
            + "  <TextBlock Text=\"{Binding Name}\" />\r\n"
            + "  <TextBlock Tag=\"dcs\" Text=\"{Binding Id}\" />\r\n"
            + "</Window>";
        AxamlWrapper.Result result = AxamlWrapper.Wrap(xaml);
        Assert.Equal(3, result.Wrapped);
        Assert.Contains("xmlns:l=\"using:JoyPro.Gui.Localization\"", result.Content);
        Assert.Contains("Content=\"{l:T 'Export'}\"", result.Content);
        Assert.Contains("ToolTip.Tip=\"{l:T 'Settings &gt; Games: it\\'s here'}\"", result.Content);
        Assert.Contains("Text=\"{l:T '{}{0} in your profile', Arg0={Binding Count}}\"", result.Content);
        Assert.Contains("Text=\"{Binding Name}\"", result.Content);
        Assert.Contains("Tag=\"dcs\"", result.Content);
        Assert.Contains("Tag=\"Keep me\"", result.Content);
        List<FoundText> found = LocExtractor.ScanAxaml("a.axaml", result.Content);
        Assert.Equal(new[] { "Export", "Settings > Games: it's here", "{0} in your profile" }, found.Where(f => f.Wrapped).Select(f => f.Text));
    }

    // The C# rewrite: user texts to T("...", args) in the order of the values; JSON fields, log lines, ids and static
    // tables stay.
    [Fact]
    public void CsWrapper_WrapsUserTextsOnly()
    {
        string cs = """
            class A {
                private static readonly string[] Titles = ["Games", "Devices"];
                void M(int n, string game, JsonElement e) {
                    StatusText = "Exported " + n + " binds to " + game + ".";
                    Hint = $"Imported {n:N0} binds from {game}.";
                    string warning = GetJsonString(e, "Warning");
                    Log("Engine started for " + game);
                    Id = "Some Id";
                    Message = cond ? "First text" : "Second text";
                }
                string Label => "Bind here";
            }
            """;
        CsWrapper.Result result = CsWrapper.Wrap("a.cs", cs);
        Assert.Contains("StatusText = T(\"Exported {0} binds to {1}.\", n, game);", result.Content);
        Assert.Contains("Hint = T(\"Imported {0:N0} binds from {1}.\", n, game);", result.Content);
        Assert.Contains("GetJsonString(e, \"Warning\")", result.Content);
        Assert.Contains("Log(\"Engine started for \" + game);", result.Content);
        Assert.Contains("Id = \"Some Id\";", result.Content);
        Assert.Contains("Message = cond ? T(\"First text\") : T(\"Second text\");", result.Content);
        Assert.Contains("string Label => T(\"Bind here\");", result.Content);
        Assert.Contains("[\"Games\", \"Devices\"]", result.Content);
    }

    // The tray host gets its menu and notification texts from tray-strings.json, written by the GUI in its language.
    [Fact]
    public void TrayStrings_ReachTheTrayHost()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "tray-loc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Loc.SetLanguage("de");
            JoyPro.Gui.Services.TrayStrings.Write(folder);
            JoyPro.Watch.WatchText.Load(folder);
            Assert.Equal(Loc.T("Watching game configs"), JoyPro.Watch.WatchText.T("Watching game configs"));
            Assert.NotEqual("Watching game configs", JoyPro.Watch.WatchText.T("Watching game configs"));
            Assert.Equal(Loc.T("JoyPro2: watching {0} game configs", 3), JoyPro.Watch.WatchText.T("JoyPro2: watching {0} game configs", 3));
            Loc.SetLanguage("en");
            JoyPro.Gui.Services.TrayStrings.Write(folder);
            Assert.False(File.Exists(Path.Combine(folder, JoyPro.Watch.WatchText.FileName)));
        }
        finally
        {
            Loc.SetLanguage("en");
            JoyPro.Watch.WatchText.Reset();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    // Every text the GUI already sends through the localization has a German translation.
    [Fact]
    public void German_HasEveryLocalizedText()
    {
        List<InventoryEntry> wrapped = LocExtractor.Inventory(LocExtractor.ScanTree(GuiFolder())).Where(e => e.Wrapped).ToList();
        Assert.NotEmpty(wrapped);
        IReadOnlyDictionary<string, string[]> german = Loc.StringsOf("de");
        List<string> missing = wrapped.Where(e => !german.ContainsKey(e.Text)).Select(e => e.Text + "  (" + string.Join(", ", e.Locations) + ")").ToList();
        Assert.True(missing.Count == 0, "No German translation (run: dotnet run --project tools/loc/JoyPro.LocTool -- missing --lang de --wrapped):\n" + string.Join("\n", missing));
    }

    // No translation loses or adds a placeholder ({0}) or breaks string.Format; every language has its file.
    [Fact]
    public void StringsFiles_AreValid()
    {
        string folder = Path.Combine(GuiFolder(), "Localization", "Strings");
        List<string> problems = [];
        foreach (LanguageInfo language in Languages.All.Skip(1))
        {
            string file = Path.Combine(folder, language.Code + ".json");
            if (!File.Exists(file))
            {
                problems.Add(language.Code + ": no strings file");
                continue;
            }
            foreach ((string key, string[] forms) in LocFiles.Read(file))
            {
                foreach (string issue in LocFiles.Check(key, forms))
                    problems.Add($"{language.Code}: \"{key}\": {issue}");
                if (LocFiles.PluralKeys.Contains(key) && forms.Length != Languages.PluralCount(language.Plural))
                    problems.Add($"{language.Code}: \"{key}\": {forms.Length} plural forms, the language has {Languages.PluralCount(language.Plural)}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(50)));
    }

    // The XAML texts follow a language change at once (Settings page title, a format text with an argument), and
    // the language picker switches the language.
    [Fact]
    public void MarkupExtension_SwitchesLive()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new UiDesignRenderTests.DesignFakeEngine { Empty = true }, new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
            try
            {
                Loc.SetLanguage("en");
                MainWindow window = new() { DataContext = vm };
                window.Measure(new Avalonia.Size(1280, 800));
                AvaloniaRenderThread.Pump();
                TextBlock title = window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == "Settings" && t.FontSize == 22);
                TextBlock inUse = window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "LanguageInUseText");
                Assert.Equal("JoyPro shows: English", inUse.Text);

                vm.SelectedLanguageChoice = vm.LanguageChoices.First(c => c.Code == "de");
                AvaloniaRenderThread.Pump();
                Assert.Equal("de", Loc.Current.Code);
                Assert.Equal("Einstellungen", title.Text);
                Assert.Equal(Loc.T("JoyPro shows: {0}", "Deutsch"), inUse.Text);
                Assert.NotEqual("JoyPro shows: Deutsch", inUse.Text);

                vm.SelectedLanguageChoice = vm.LanguageChoices.First(c => c.Code == "en");
                AvaloniaRenderThread.Pump();
                Assert.Equal("Settings", title.Text);
                Assert.Equal("JoyPro shows: English", inUse.Text);
            }
            finally
            {
                Loc.SetLanguage("en");
                vm.StopGameConfigWatcher();
            }
        });
    }

    internal static string GuiFolder()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string gui = Path.Combine(dir.FullName, "gui", "JoyPro.Gui");
            if (Directory.Exists(Path.Combine(gui, "Localization")))
                return gui;
        }
        throw new DirectoryNotFoundException("gui/JoyPro.Gui not found");
    }

    // Strings files of a test in a temp folder (Loc.StringsFolderOverride), English again afterwards.
    private sealed class TempStrings : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "loc-" + Guid.NewGuid().ToString("N"));

        public TempStrings(Dictionary<string, string> files)
        {
            Directory.CreateDirectory(folder);
            foreach ((string code, string json) in files)
                File.WriteAllText(Path.Combine(folder, code + ".json"), json);
            Loc.StringsFolderOverride = folder;
            Loc.Reload();
        }

        public void Dispose()
        {
            Loc.StringsFolderOverride = null;
            Loc.SetLanguage("en");
            Loc.Reload();
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
