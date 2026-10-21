using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Several views on one page (docs/device-reference.md "Several views on one page"): like the user's JoyPro 1 layouts
// that combined front and side pictures, a device's layout can hold several pictures of the device on one page, each
// with its own markers, tables and lines (its callout sheet). "Several views" on the Layout page opens the page: add
// JoyPro's other pictures of the device (line drawings, photos) or an own image, click a view to select it, drag it,
// scale it (corner handle, wheel), pick the page format (A4 / Letter portrait or landscape, 16:9 / 21:9 / 32:9),
// Arrange, edit a view's markers ("Edit this view" shows it in the normal editor). Saved per model (MultiViewStore);
// the cheat sheets draw the page. Without a page the device keeps its one picture as before.
public sealed partial class MainWindowViewModel
{
    private MultiViewLayout? multiView;

    [ObservableProperty]
    private bool isMultiViewOpen;

    [ObservableProperty]
    private MultiViewItemModel? selectedMultiViewItem;

    [ObservableProperty]
    private MultiViewPageChoice? selectedMultiViewPage;

    [ObservableProperty]
    private string multiViewStatus = string.Empty;

    public ObservableCollection<MultiViewItemModel> MultiViewItems { get; } = [];

    public ObservableCollection<MultiViewAddChoice> MultiViewAddChoices { get; } = [];

    public IReadOnlyList<MultiViewPageChoice> MultiViewPages { get; } =
        MultiViewLayout.Pages.Select(p => new MultiViewPageChoice(p.Id, p.Title, p.Width, p.Height)).ToList();

    public double MultiViewPageWidth => multiView?.Width ?? 3508;

    public double MultiViewPageHeight => multiView?.Height ?? 2480;

    public bool HasSelectedMultiViewItem => SelectedMultiViewItem != null;

    public bool CanEditSelectedMultiViewItem => SelectedMultiViewItem is { IsImage: false };

    public bool HasMultiViewAddChoices => MultiViewAddChoices.Count > 0;

    // The device shown has a page with several views (the button says so).
    public bool HasMultiViewPage => deviceReference?.Model is { } model && !deviceReference.IsGeneric && MultiViewStore.Load(model.Id) is { Items.Count: >= 2 };

    public string MultiViewButtonLabel => HasMultiViewPage ? T("Page with {0} views", MultiViewStore.Load(deviceReference!.Model!.Id)!.Items.Count) : T("Several views");

    public bool LayoutPageDark => !LayoutSheetLightStyle;

    partial void OnSelectedMultiViewItemChanged(MultiViewItemModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedMultiViewItem));
        OnPropertyChanged(nameof(CanEditSelectedMultiViewItem));
    }

    partial void OnIsMultiViewOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(MultiViewButtonLabel));
        OnPropertyChanged(nameof(HasMultiViewPage));
    }

    partial void OnSelectedMultiViewPageChanged(MultiViewPageChoice? value)
    {
        if (value == null || multiView == null || string.Equals(multiView.Page, value.Id, StringComparison.OrdinalIgnoreCase))
            return;
        multiView.Page = value.Id;
        multiView.Arrange();
        SyncMultiViewItems();
        SaveMultiView();
        OnPropertyChanged(nameof(MultiViewPageWidth));
        OnPropertyChanged(nameof(MultiViewPageHeight));
    }

    // "Several views": the device's page, or a new one that starts with the picture shown.
    [RelayCommand]
    private async Task OpenMultiViewAsync()
    {
        DeviceReferenceViewModel? reference = deviceReference;
        if (reference?.Model == null || reference.IsGeneric)
        {
            MultiViewStatus = T("Pick a device JoyPro knows first.");
            StatusText = MultiViewStatus;
            return;
        }
        LayoutShowsLayoutFile = false;
        MultiViewLayout? page = MultiViewStore.Load(reference.Model.Id);
        if (page == null)
        {
            page = new MultiViewLayout { Model = reference.Model.Id };
            MultiViewItem? first = CurrentPictureAsView();
            if (first == null)
            {
                MultiViewStatus = T("This picture has no tables yet: place its markers first (Edit), then add views.");
                StatusText = MultiViewStatus;
                return;
            }
            page.Items.Add(first);
            page.Arrange();
            MultiViewStore.Save(page);
        }
        multiView = page;
        SelectedMultiViewPage = MultiViewPages.FirstOrDefault(p => p.Id == page.Page) ?? MultiViewPages[0];
        SyncMultiViewItems();
        RebuildMultiViewAddChoices();
        IsMultiViewOpen = true;
        OnPropertyChanged(nameof(MultiViewPageWidth));
        OnPropertyChanged(nameof(MultiViewPageHeight));
        MultiViewStatus = page.Items.Count < 2
            ? T("Add the other pictures of the device with 'Add view', then drag and scale them on the page.")
            : T("Click a view to select it; drag it, scale it with its corner or the mouse wheel.");
        await RenderMultiViewImagesAsync();
    }

    [RelayCommand]
    private void CloseMultiView()
    {
        IsMultiViewOpen = false;
        SelectedMultiViewItem = null;
        OnPropertyChanged(nameof(MultiViewButtonLabel));
    }

    // The picture shown in the editor as a view (its sheet saved first); null when it has no sheet.
    private MultiViewItem? CurrentPictureAsView()
    {
        DeviceReferenceViewModel? reference = deviceReference;
        RefreshLayoutCallouts();
        CalloutSheet? sheet = LayoutCallouts.Sheet;
        if (reference?.Model == null || sheet == null || calloutPictureKey.Length == 0)
            return null;
        string file = SaveLayoutCalloutsFile(markSaved: true);
        if (file.Length == 0)
            return null;
        ReferencePictureOption? picture = reference.LoadedPicture;
        return new MultiViewItem
        {
            Sheet = file,
            PictureKey = calloutPictureKey,
            Title = picture == null ? reference.Model.Name : picture.Title + (picture.View.Length > 0 ? " (" + picture.View + ")" : string.Empty),
            Width = Math.Round(sheet.Width),
            Height = Math.Round(sheet.Height),
            Scale = 1
        };
    }

    // JoyPro's other pictures of the model (drawings, photos) that are not on the page yet.
    private void RebuildMultiViewAddChoices()
    {
        MultiViewAddChoices.Clear();
        if (deviceReference == null || multiView == null)
            return;
        HashSet<string> placed = new(multiView.Items.Select(i => i.PictureKey), StringComparer.OrdinalIgnoreCase);
        foreach (ReferencePictureOption option in deviceReference.Pictures.Where(p => p.Kind != "class"))
        {
            if (placed.Contains(CalloutPictureKeyOf(option)))
                continue;
            MultiViewAddChoices.Add(new MultiViewAddChoice(option, option.Title + (option.View.Length > 0 ? " (" + option.View + ")" : string.Empty)));
        }
        OnPropertyChanged(nameof(HasMultiViewAddChoices));
    }

    [RelayCommand]
    private async Task AddMultiViewAsync(MultiViewAddChoice? choice)
    {
        DeviceReferenceViewModel? reference = deviceReference;
        if (choice == null || reference == null || multiView == null)
            return;
        ReferencePictureOption? before = reference.LoadedPicture;
        // The picture's sheet comes from the editor: shown once, its tables arranged and saved.
        if (!await reference.SelectPictureAsync(choice.Picture))
        {
            MultiViewStatus = T("JoyPro could not show this picture.");
            return;
        }
        MultiViewItem? view = CurrentPictureAsView();
        if (before != null && !ReferenceEquals(before, choice.Picture))
            await reference.SelectPictureAsync(before);
        RefreshLayoutCallouts();
        if (view == null)
        {
            MultiViewStatus = T("'{0}' has no marker positions yet: show it in the editor and place its markers first.", choice.Title);
            return;
        }
        multiView.Items.Add(view);
        multiView.Arrange();
        SaveMultiView();
        SyncMultiViewItems();
        RebuildMultiViewAddChoices();
        SelectedMultiViewItem = MultiViewItems.LastOrDefault();
        MultiViewStatus = T("'{0}' is on the page. Drag it where you want it.", choice.Title);
        await RenderMultiViewImagesAsync();
    }

    // An own image as a view (no markers: a photo of the side, a label).
    public async Task AddMultiViewImageAsync(string file)
    {
        if (multiView == null || !File.Exists(file))
            return;
        string kept;
        double width = 1000, height = 1000;
        try
        {
            kept = MultiViewStore.KeepImage(multiView.Model, file);
            using FileStream stream = File.OpenRead(kept);
            using Bitmap probe = new(stream);
            width = probe.PixelSize.Width;
            height = probe.PixelSize.Height;
        }
        catch (Exception ex)
        {
            MultiViewStatus = T("The picture could not be read: {0}", ex.Message);
            return;
        }
        multiView.Items.Add(new MultiViewItem { Image = kept, Title = Path.GetFileNameWithoutExtension(file), Width = width, Height = height, Scale = 1 });
        multiView.Arrange();
        SaveMultiView();
        SyncMultiViewItems();
        SelectedMultiViewItem = MultiViewItems.LastOrDefault();
        await RenderMultiViewImagesAsync();
    }

    [RelayCommand]
    private void RemoveMultiView()
    {
        if (multiView == null || SelectedMultiViewItem == null)
            return;
        multiView.Items.Remove(SelectedMultiViewItem.Item);
        MultiViewItems.Remove(SelectedMultiViewItem);
        SelectedMultiViewItem = null;
        SaveMultiView();
        RebuildMultiViewAddChoices();
    }

    [RelayCommand]
    private void ArrangeMultiView()
    {
        if (multiView == null)
            return;
        multiView.Arrange();
        foreach (MultiViewItemModel item in MultiViewItems)
            item.OnPlaced();
        SaveMultiView();
        MultiViewRedraw?.Invoke(this, EventArgs.Empty);
    }

    // "Edit this view": the view's picture in the normal editor (markers, tables and lines); the page shows it after.
    [RelayCommand]
    private async Task EditMultiViewAsync()
    {
        DeviceReferenceViewModel? reference = deviceReference;
        MultiViewItemModel? item = SelectedMultiViewItem;
        if (reference == null || item == null || item.IsImage)
            return;
        ReferencePictureOption? picture = reference.Pictures.FirstOrDefault(p => string.Equals(CalloutPictureKeyOf(p), item.Item.PictureKey, StringComparison.OrdinalIgnoreCase));
        CloseMultiView();
        if (picture != null)
            await reference.SelectPictureAsync(picture);
        RefreshLayoutCallouts();
        SetLayoutCanvasModeCommand.Execute("edit");
    }

    [RelayCommand]
    private async Task DeleteMultiViewPageAsync()
    {
        if (multiView == null)
            return;
        if (!await ConfirmAsync(T("Remove the page with several views?"), T("The device shows its one picture again; the views' markers, tables and lines stay with their pictures."), T("Remove page")))
            return;
        MultiViewStore.Delete(multiView.Model);
        multiView = null;
        MultiViewItems.Clear();
        CloseMultiView();
        MultiViewStatus = string.Empty;
    }

    // A view was moved or scaled on the canvas.
    public void MultiViewItemChanged(MultiViewItemModel item) => SaveMultiView();

    // Asks the canvas to draw again (Arrange changed every view).
    public event EventHandler? MultiViewRedraw;

    private void SaveMultiView()
    {
        if (multiView == null)
            return;
        try
        {
            MultiViewStore.Save(multiView);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MultiViewStatus = T("The page could not be saved: {0}", ex.Message);
        }
        OnPropertyChanged(nameof(MultiViewButtonLabel));
        OnPropertyChanged(nameof(HasMultiViewPage));
    }

    private void SyncMultiViewItems()
    {
        Dictionary<MultiViewItem, Bitmap?> images = MultiViewItems.ToDictionary(i => i.Item, i => i.Image);
        MultiViewItems.Clear();
        foreach (MultiViewItem item in multiView?.Items ?? [])
            MultiViewItems.Add(new MultiViewItemModel(item) { Image = images.TryGetValue(item, out Bitmap? b) ? b : null });
    }

    // The engine draws every view (its picture with its tables, lines and the device's binds).
    private async Task RenderMultiViewImagesAsync()
    {
        if (multiView == null)
            return;
        string file = MultiViewStore.FileOf(multiView.Model);
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2", "multiview", DeviceVocabulary.Slug(multiView.Model));
        List<string> args = ["layout", "multiview", file, "--views", folder];
        string device = ResolveLayoutEditorContextDevice();
        if (device.Length > 0)
            args.AddRange(["--device", device]);
        if (LayoutSheetLightStyle)
            args.Add("--light");
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Drawing the views..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
            return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (!doc.RootElement.TryGetProperty("Views", out JsonElement views) || views.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement view in views.EnumerateArray())
            {
                int index = view.TryGetProperty("Index", out JsonElement i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
                string png = ReadJsonString(view, "File");
                if (index < 0 || index >= MultiViewItems.Count || !File.Exists(png))
                    continue;
                using FileStream stream = File.OpenRead(png);
                MultiViewItemModel item = MultiViewItems[index];
                item.Image = new Bitmap(stream);
                if (item.Width <= 0 || item.Height <= 0)
                {
                    item.Width = item.Image.PixelSize.Width;
                    item.Height = item.Image.PixelSize.Height;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
        }
        MultiViewRedraw?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record MultiViewPageChoice(string Id, string Title, double Width, double Height)
{
    public override string ToString() => Localization.Loc.T(Title);
}

public sealed record MultiViewAddChoice(ReferencePictureOption Picture, string Title);
