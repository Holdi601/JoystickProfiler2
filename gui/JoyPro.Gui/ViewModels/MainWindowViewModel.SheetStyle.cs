using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Views.Controls;

namespace JoyPro.Gui.ViewModels;

// The cheat sheet style (docs/ui/layout-redesign.md, "Sheet style"): one global style for every sheet
// (<settings>\sheet-style.json) and, per device, a style of its own (inside the device's sheet, or "<layout>.style.json"
// next to its .layout file), edited in the style panel of the Layout page (SheetStyleEditor, with "this device uses its
// own style") and of the cheat sheet options (CheatSheetStyleEditor, global). The Layout page's canvases draw with the
// shown device's style (LayoutSheetStyle), "Create cheat sheets" passes the global style to the engine (which reads a
// device's own style from its layout), "Share layout..." carries the device's style in its sheet.
public sealed partial class MainWindowViewModel
{
    private SheetStyle globalSheetStyle = SheetStyle.Preset("joypro");
    // The shown device's own style (null = it uses the global one) and what it belongs to ("callouts|model|picture",
    // "file|<layout path>").
    private SheetStyle? deviceSheetStyle;
    private string sheetStyleScopeKey = string.Empty;
    private SheetStyleViewModel? sheetStyleEditor;
    private SheetStyleViewModel? cheatSheetStyleEditor;
    private DrawingStyleOptions? deviceDrawing;

    // The Layout page's panel (the shown device can get its own style).
    public SheetStyleViewModel SheetStyleEditor => sheetStyleEditor ??= CreateSheetStyleEditor(deviceScope: true);

    // The cheat sheet options' panel (the global style).
    public SheetStyleViewModel CheatSheetStyleEditor => cheatSheetStyleEditor ??= CreateSheetStyleEditor(deviceScope: false);

    // The style the Layout page draws the shown device's sheet with (its own, else the global style); a new object on
    // every change so the canvases repaint.
    public SheetStyle LayoutSheetStyle => (deviceSheetStyle ?? globalSheetStyle).Clone();

    public SheetStyle GlobalSheetStyle => globalSheetStyle.Clone();

    // The shown device has a style of its own.
    public bool LayoutUsesOwnSheetStyle => deviceSheetStyle != null;

    // The texts of the layout file's tables on the picture (MainWindow.axaml, LayoutEditorTables): header row and body.
    public IBrush SheetHeaderTextBrush => SheetStyleLook.BrushOf((deviceSheetStyle ?? globalSheetStyle).HeaderText, Colors.White);

    public IBrush SheetTableTextBrush => SheetStyleLook.BrushOf((deviceSheetStyle ?? globalSheetStyle).TableText, Colors.Black);

    // ---- the old single values (the engine options of before), kept as views of the global style ----

    public string LayoutFontFamily
    {
        get => globalSheetStyle.FontFamily;
        set => EditGlobalSheetStyle(s => s.FontFamily = (value ?? string.Empty).Trim());
    }

    public string LayoutFontSize
    {
        get => globalSheetStyle.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double size) && size > 0)
                EditGlobalSheetStyle(s => s.FontSize = Math.Clamp(size, 6, 96));
        }
    }

    public double LayoutEditorFontSizeValue => Math.Clamp(globalSheetStyle.FontSize > 0 ? globalSheetStyle.FontSize : 16, 6, 96);

    public string LayoutTextColor
    {
        get => globalSheetStyle.TextColor;
        set => EditGlobalSheetStyle(s => s.TextColor = SheetStyle.NormalizeColor(value) is { Length: > 0 } c ? c : s.TextColor);
    }

    public string LayoutOutlineColor
    {
        get => globalSheetStyle.OutlineColor;
        set => EditGlobalSheetStyle(s => s.OutlineColor = SheetStyle.NormalizeColor(value) is { Length: > 0 } c ? c : s.OutlineColor);
    }

    public string LayoutBackgroundColor
    {
        get => globalSheetStyle.LabelBackground;
        set => EditGlobalSheetStyle(s => s.LabelBackground = SheetStyle.NormalizeColor(value) is { Length: > 0 } c ? c : s.LabelBackground);
    }

    // "LAlt=#38BDF8,LCtrl=#F59E0B" (the engine's --modifier-colors).
    public string LayoutModifierColors
    {
        get => globalSheetStyle.ModifierCsv;
        set => EditGlobalSheetStyle(s => s.Modifiers = SheetStyle.ParseModifierCsv(value));
    }

    private void EditGlobalSheetStyle(Action<SheetStyle> edit)
    {
        SheetStyle next = globalSheetStyle.Clone();
        edit(next);
        SetGlobalSheetStyle(next, reloadEditors: true);
    }

    // ---- setup ----

    private SheetStyleViewModel CreateSheetStyleEditor(bool deviceScope)
    {
        SheetStyleViewModel editor = new(deviceScope);
        editor.Load(deviceScope && deviceSheetStyle != null ? deviceSheetStyle : globalSheetStyle);
        editor.Drawing = deviceScope && deviceSheetStyle != null ? DeviceDrawing() : ExportDrawing;
        editor.SetScope(deviceScope && SheetStyleScopeAvailable(), deviceSheetStyle != null, SheetStyleScopeText());
        editor.Changed += (_, style) =>
        {
            if (editor.DeviceScope && editor.UsesOwnStyle && deviceSheetStyle != null)
                SetDeviceSheetStyle(style);
            else
                SetGlobalSheetStyle(style, reloadEditors: true, source: editor);
        };
        editor.PresetApplied += (_, style) =>
        {
            // The preset's drawing of the device: the export drawing style (global) or the device style's.
            if (editor.DeviceScope && editor.UsesOwnStyle && deviceSheetStyle != null)
            {
                deviceSheetStyle.Drawing = style.Drawing;
                deviceDrawing?.Load();
            }
            else if (style.Drawing.Length > 0)
            {
                DeviceReferenceService.ExportDrawingStyle = DrawingStyle.Parse(style.Drawing);
                ExportDrawing.Load();
            }
        };
        editor.OwnStyleSwitched += (_, own) => SwitchOwnSheetStyle(own);
        editor.PresetsChanged += (_, _) =>
        {
            if (!ReferenceEquals(editor, sheetStyleEditor))
                sheetStyleEditor?.RefreshPresets();
            if (!ReferenceEquals(editor, cheatSheetStyleEditor))
                cheatSheetStyleEditor?.RefreshPresets();
        };
        return editor;
    }

    // At start: the global style of the last session (persisted only with the user's settings).
    private void InitializeSheetStyle(bool persisted)
    {
        globalSheetStyle = persisted ? SheetStyleStore.LoadGlobal() : SheetStyle.Preset("joypro");
        sheetStyleEditor?.Load(globalSheetStyle);
        cheatSheetStyleEditor?.Load(globalSheetStyle);
        ApplySheetStyleToLayout();
    }

    private void SetGlobalSheetStyle(SheetStyle style, bool reloadEditors, SheetStyleViewModel? source = null)
    {
        globalSheetStyle = SheetStyleStore.Complete(style);
        if (persistUiFiles)
            SheetStyleStore.SaveGlobal(globalSheetStyle);
        if (reloadEditors)
        {
            if (cheatSheetStyleEditor != null && !ReferenceEquals(source, cheatSheetStyleEditor))
                cheatSheetStyleEditor.Load(globalSheetStyle);
            if (sheetStyleEditor != null && !ReferenceEquals(source, sheetStyleEditor) && deviceSheetStyle == null)
                sheetStyleEditor.Load(globalSheetStyle);
        }
        ApplySheetStyleToLayout();
    }

    private void SetDeviceSheetStyle(SheetStyle style)
    {
        deviceSheetStyle = SheetStyleStore.Complete(style);
        PersistDeviceSheetStyle();
        ApplySheetStyleToLayout();
    }

    // "This device uses its own style": on = a copy of the global style (with the drawing of the exports) becomes the
    // device's; off = the device's style is removed, the global one applies again.
    private void SwitchOwnSheetStyle(bool own)
    {
        if (!SheetStyleScopeAvailable())
            return;
        if (own)
        {
            SheetStyle copy = globalSheetStyle.Clone();
            copy.Name = string.Empty;
            copy.Drawing = ExportDrawing.Current.ToString();
            deviceSheetStyle = copy;
            deviceDrawing = null;
            SheetStyleEditor.Drawing = DeviceDrawing();
            SheetStyleEditor.Load(deviceSheetStyle);
        }
        else
        {
            deviceSheetStyle = null;
            deviceDrawing = null;
            SheetStyleEditor.Drawing = ExportDrawing;
            SheetStyleEditor.Load(globalSheetStyle);
        }
        PersistDeviceSheetStyle();
        SheetStyleEditor.SetScope(true, own, SheetStyleScopeText());
        ApplySheetStyleToLayout();
    }

    // The device style's drawing (lines or cel-shaded, colours): kept in its style, not in the export settings.
    private DrawingStyleOptions DeviceDrawing()
    {
        if (deviceDrawing != null)
            return deviceDrawing;
        deviceDrawing = new DrawingStyleOptions(
            () => DrawingStyle.Parse(deviceSheetStyle?.Drawing ?? string.Empty),
            style =>
            {
                if (deviceSheetStyle == null)
                    return;
                deviceSheetStyle.Drawing = style.ToString();
                PersistDeviceSheetStyle();
            });
        return deviceDrawing;
    }

    // ---- where the device's own style lives ----

    private bool SheetStyleScopeAvailable() => CurrentSheetStyleScope(out _, out _) != SheetStyleScope.None;

    private enum SheetStyleScope { None, Callouts, LayoutFile }

    private SheetStyleScope CurrentSheetStyleScope(out string key, out string device)
    {
        key = string.Empty;
        device = string.Empty;
        if (layoutCallouts?.Sheet != null && LayoutShowsReference && calloutModel.Length > 0 && calloutPictureKey.Length > 0)
        {
            key = "callouts|" + calloutModel + "|" + calloutPictureKey;
            device = deviceReference?.DeviceName ?? string.Empty;
            return SheetStyleScope.Callouts;
        }
        string layout = (LayoutEditorSourcePath ?? string.Empty).Trim();
        if (LayoutShowsLayoutFile && layout.Length > 0)
        {
            key = "file|" + NormalizeUiPath(layout);
            device = ResolveLayoutEditorContextDevice();
            return SheetStyleScope.LayoutFile;
        }
        return SheetStyleScope.None;
    }

    private string SheetStyleScopeText()
    {
        SheetStyleScope scope = CurrentSheetStyleScope(out _, out string device);
        string name = device.Length > 0 ? DeviceDisplayName(device) : T("this device");
        if (scope == SheetStyleScope.None)
            return T("All cheat sheets use this style.");
        return deviceSheetStyle != null
            ? T("Only the sheets of {0} use this style; the others keep the style of all sheets.", name)
            : T("Changes apply to all cheat sheets. Tick the box to give {0} a style of its own.", name);
    }

    // The shown device changed (another device, picture or layout file): its own style or the global one.
    private void RefreshSheetStyleScope()
    {
        SheetStyleScope scope = CurrentSheetStyleScope(out string key, out _);
        if (string.Equals(key, sheetStyleScopeKey, StringComparison.Ordinal))
            return;
        sheetStyleScopeKey = key;
        SheetStyle? own = scope switch
        {
            SheetStyleScope.Callouts => layoutCallouts?.Sheet?.Style is { } style ? SheetStyleStore.Complete(style) : null,
            SheetStyleScope.LayoutFile => SheetStyleStore.LoadSidecar(LayoutEditorSourcePath),
            _ => null
        };
        deviceSheetStyle = own;
        deviceDrawing = null;
        if (sheetStyleEditor != null)
        {
            sheetStyleEditor.Drawing = own != null ? DeviceDrawing() : ExportDrawing;
            sheetStyleEditor.Load(own ?? globalSheetStyle);
            sheetStyleEditor.SetScope(scope != SheetStyleScope.None, own != null, SheetStyleScopeText());
        }
        ApplySheetStyleToLayout();
    }

    // Writes the device's style where the engine reads it: into its sheet (saved at once; other unsaved edits of the
    // sheet stay unsaved), or next to its .layout file.
    private void PersistDeviceSheetStyle()
    {
        switch (CurrentSheetStyleScope(out _, out _))
        {
            case SheetStyleScope.Callouts:
                CalloutSheet? sheet = layoutCallouts?.Sheet;
                if (sheet == null)
                    return;
                sheet.Style = deviceSheetStyle?.Clone();
                if (!LayoutCallouts.HasUnsavedChanges)
                {
                    SaveLayoutCalloutsFile(markSaved: true);
                }
                else if (DeviceReferenceService.LoadCallouts(calloutModel, calloutPictureKey) is { Callouts: { } savedSheet } saved)
                {
                    savedSheet.Style = deviceSheetStyle?.Clone();
                    try
                    {
                        DeviceReferenceService.SaveCallouts(saved, calloutPictureKey);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LayoutEditorStatus = T("The tables could not be saved: {0}", ex.Message);
                    }
                }
                break;
            case SheetStyleScope.LayoutFile:
                foreach (string path in new[] { LayoutEditorSourcePath, LayoutEditorOutputPath }
                             .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(path) || deviceSheetStyle == null)
                        SheetStyleStore.SaveSidecar(path, deviceSheetStyle);
                }
                break;
        }
    }

    // ---- the style on the Layout page ----

    private void ApplySheetStyleToLayout()
    {
        SheetStyle style = deviceSheetStyle ?? globalSheetStyle;
        string border = style.TableBorder, header = style.HeaderBackground, table = style.TableBackground;
        foreach (LayoutEditorTableItem item in LayoutEditorTables)
            item.ApplySheetStyle(border, header, table, style.TableText);
        foreach (LayoutEditorConnectorItem connector in LayoutEditorConnectors)
            connector.SheetLineColor = style.LineColor;
        // The export's page: white when the global style's page is light (--style), for the views of a page too.
        bool light = !globalSheetStyle.IsDarkPage(true);
        if (LayoutSheetLightStyle != light)
            LayoutSheetLightStyle = light;
        OnPropertyChanged(nameof(LayoutSheetStyle));
        OnPropertyChanged(nameof(GlobalSheetStyle));
        OnPropertyChanged(nameof(LayoutUsesOwnSheetStyle));
        OnPropertyChanged(nameof(SheetHeaderTextBrush));
        OnPropertyChanged(nameof(SheetTableTextBrush));
        OnPropertyChanged(nameof(LayoutPageDark));
        OnPropertyChanged(nameof(WorkspaceLabelFont));
        OnPropertyChanged(nameof(WorkspaceLabelSize));
        OnPropertyChanged(nameof(WorkspaceLabelBrush));
        OnPropertyChanged(nameof(LayoutFontFamily));
        OnPropertyChanged(nameof(LayoutFontSize));
        OnPropertyChanged(nameof(LayoutEditorFontSizeValue));
        OnPropertyChanged(nameof(LayoutTextColor));
        OnPropertyChanged(nameof(LayoutOutlineColor));
        OnPropertyChanged(nameof(LayoutBackgroundColor));
        OnPropertyChanged(nameof(LayoutModifierColors));
    }

    // Tables and lines added to the layout file get the style at once.
    private void ApplySheetStyleToNewItems(System.Collections.IList? items)
    {
        if (items == null)
            return;
        SheetStyle style = deviceSheetStyle ?? globalSheetStyle;
        foreach (object item in items)
        {
            if (item is LayoutEditorTableItem table)
                table.ApplySheetStyle(style.TableBorder, style.HeaderBackground, style.TableBackground, style.TableText);
            else if (item is LayoutEditorConnectorItem connector)
                connector.SheetLineColor = style.LineColor;
        }
    }

    // ---- the engine ----

    // The global style as "layout render" / "layout multiview" options (a device's own style is in its layout).
    internal static List<string> SheetStyleArguments(SheetStyle style)
    {
        List<string> args = [];
        if (style.FontFamily.Trim().Length > 0)
            args.AddRange(["--font", style.FontFamily.Trim()]);
        if (style.FontSize > 0)
            args.AddRange(["--font-size", style.FontSize.ToString("0.##", CultureInfo.InvariantCulture)]);
        if (style.NormalizedWeight.Length > 0)
            args.AddRange(["--font-weight", style.NormalizedWeight]);
        AddColor(args, "--text-color", style.TextColor, allowNone: false);
        AddColor(args, "--outline-color", style.OutlineColor, allowNone: true);
        AddColor(args, "--label-background", style.LabelBackground, allowNone: true);
        AddColor(args, "--page-background", style.PageBackground, allowNone: false);
        AddColor(args, "--table-background", style.TableBackground, allowNone: false);
        AddColor(args, "--table-border", style.TableBorder, allowNone: false);
        AddColor(args, "--header-background", style.HeaderBackground, allowNone: false);
        AddColor(args, "--header-text", style.HeaderText, allowNone: false);
        AddColor(args, "--table-text", style.TableText, allowNone: false);
        AddColor(args, "--line-color", style.LineColor, allowNone: false);
        AddColor(args, "--marker-color-button", style.MarkerButton, allowNone: false);
        AddColor(args, "--marker-color-axis", style.MarkerAxis, allowNone: false);
        AddColor(args, "--marker-color-hat", style.MarkerHat, allowNone: false);
        AddColor(args, "--marker-color-slider", style.MarkerSlider, allowNone: false);
        if (style.ModifierCsv.Length > 0)
            args.AddRange(["--modifier-colors", style.ModifierCsv]);
        return args;
    }

    private static void AddColor(List<string> args, string option, string color, bool allowNone)
    {
        string value = SheetStyle.NormalizeColor(color);
        if (value.Length == 0)
            return;
        // A fully transparent halo or label box is "none" (the engine's word for it).
        if (allowNone && value.StartsWith("#00", StringComparison.Ordinal))
            value = "none";
        args.AddRange([option, value]);
    }
}
