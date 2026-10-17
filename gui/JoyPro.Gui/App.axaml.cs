using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using JoyPro.Gui.ViewModels;
using System.Runtime.Versioning;

namespace JoyPro.Gui;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    // Set by Program.Main: this JoyPro2 is the only one (a second start asks it to show its window).
    internal static Watch.SingleInstanceGuard? InstanceGuard { get; set; }

    internal static Watch.InstanceNames? Names { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // The UI font: Bahnschrift, then the CJK font of the language shown (Chinese and Japanese share code points
        // but not glyph shapes), docs/localization.md.
        ApplyUiFont();
        Localization.Loc.LanguageChanged += (_, _) => ApplyUiFont();
    }

    private void ApplyUiFont()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyUiFont);
            return;
        }
        Resources["JP.UiFont"] = new Avalonia.Media.FontFamily(Localization.Languages.FontStack(Localization.Loc.Current));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            MainWindowViewModel vm = new();
            // An error in a UI handler is recorded and reported instead of closing JoyPro2.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Services.CrashReporter.Write(e.Exception, "UI thread");
                e.Handled = true;
                vm.StatusText = T("An error occurred and was recorded; please report it (Settings > Report a Problem).");
            };
            // The window, a second start, and the tray host after the window closed (docs/sync-watcher.md#tray-and-autostart).
            AppShell shell = new(desktop, vm, InstanceGuard, Names ?? new Watch.InstanceNames(Services.AppInfo.SettingsFolder));
            shell.Start(Services.StartupOptions.Parse(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
