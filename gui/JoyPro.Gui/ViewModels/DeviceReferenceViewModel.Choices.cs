using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// "Which one is it?" (docs/device-reference.md): asked once for a base with exchangeable grips that does not say which
// grip is on it, for one USB id that several products share, and for a device JoyPro does not know. The candidates are
// ranked by what the device reports (DeviceChoices), each with its photo (or JoyPro's drawing). An unknown device is
// looked up in the catalog, else the user picks the kind of device. The answer is kept per device by the host.
public sealed partial class DeviceReferenceViewModel
{
    public ObservableCollection<ChoiceCandidate> QuestionCandidates { get; } = [];

    // "Find your device": catalog models matching FindText.
    public ObservableCollection<ChoiceCandidate> FindResults { get; } = [];

    // The kinds of device for an unknown one (the guess first).
    public ObservableCollection<ClassChoice> ClassChoices { get; } = [];

    [ObservableProperty] private DeviceQuestion? question;
    [ObservableProperty] private string findText = string.Empty;
    // The user opened "Find your device" (also for a grip that is not among the candidates).
    [ObservableProperty] private bool isFinding;

    public bool HasQuestion => Question != null;

    public bool IsUnknownQuestion => Question?.Kind == "unknown";

    public bool ShowsCandidates => HasQuestion && QuestionCandidates.Count > 0 && !IsFinding;

    public bool ShowsFind => HasQuestion && (IsFinding || IsUnknownQuestion);

    public string QuestionTitle => Question?.Title ?? string.Empty;

    public string QuestionHint => Question?.Kind switch
    {
        "grip" => "The base reports the same USB id for every grip. Pick the one on it (the most likely first); JoyPro remembers it.",
        "model" => "Several products report this USB id. Pick yours; JoyPro remembers it.",
        "unknown" => "JoyPro does not know this device yet. Find it in the catalog, or pick what kind of device it is.",
        _ => string.Empty
    };

    private void ClearQuestion()
    {
        Question = null;
        QuestionCandidates.Clear();
        FindResults.Clear();
        ClassChoices.Clear();
        IsFinding = false;
        FindText = string.Empty;
        NotifyQuestion();
    }

    private void SetQuestion(DeviceQuestion value)
    {
        Question = value;
        QuestionCandidates.Clear();
        bool first = true;
        foreach (RankedCandidate ranked in value.Candidates.Take(12))
        {
            QuestionCandidates.Add(Candidate(ranked.Model, ranked.Score, ranked.Why, first));
            first = false;
        }
        ClassChoices.Clear();
        if (value.Kind == "unknown")
        {
            string guess = value.GuessedClass;
            foreach (string cls in DeviceClasses.All.OrderBy(c => c == guess ? 0 : 1))
                ClassChoices.Add(new ClassChoice { Class = cls, Title = DeviceClasses.Title(cls), IsGuess = cls == guess, Drawing = DeviceClasses.Drawing(cls, "right", null) });
            FindText = DeviceProbe.RemoveGuid(DeviceName);
        }
        NotifyQuestion();
        _ = LoadCandidatePicturesAsync(QuestionCandidates.ToList());
    }

    private static ChoiceCandidate Candidate(RefDevice model, int score, string why, bool best)
    {
        DevicePhoto? photo = DevicePhotoDb.UsablePhotos(model).FirstOrDefault();
        DeviceSchematic? drawing = null;
        try
        {
            DeviceSchematic schematic = DeviceReferenceService.Schematic(model);
            drawing = DeviceClasses.DrawingIsRecognizable(model, schematic) ? schematic : DeviceClasses.Drawing(DeviceClasses.Of(model), model.Hand, null);
        }
        catch (Exception)
        {
            drawing = DeviceClasses.Drawing(DeviceClasses.Of(model), model.Hand, null);
        }
        return new ChoiceCandidate
        {
            Model = model,
            Title = model.Name,
            Score = score,
            Why = why,
            IsBest = best,
            PhotoUrl = photo?.Url ?? string.Empty,
            Drawing = drawing
        };
    }

    // The candidates' photos, downloaded on this PC: only for models the user allowed photos of (an answer for the model
    // or "Always"); the others keep JoyPro's drawing (DevicePhotoConsent).
    private async Task LoadCandidatePicturesAsync(System.Collections.Generic.List<ChoiceCandidate> candidates)
    {
        foreach (ChoiceCandidate candidate in candidates.Where(c => c.PhotoUrl.Length > 0 && DevicePhotoConsent.Allows(c.Model.Id)))
        {
            try
            {
                candidate.Picture = await LoadCandidatePicture(candidate.PhotoUrl);
            }
            catch (Exception)
            {
                candidate.Picture = null;
            }
        }
    }

    // Loads a candidate's picture (replaceable for tests: no download there).
    public Func<string, Task<Avalonia.Media.IImage?>> LoadCandidatePicture { get; set; } = async url => await DeviceImageCache.LoadAsync(url, 320);

    private void NotifyQuestion()
    {
        OnPropertyChanged(nameof(HasQuestion));
        OnPropertyChanged(nameof(IsUnknownQuestion));
        OnPropertyChanged(nameof(ShowsCandidates));
        OnPropertyChanged(nameof(ShowsFind));
        OnPropertyChanged(nameof(QuestionTitle));
        OnPropertyChanged(nameof(QuestionHint));
    }

    partial void OnIsFindingChanged(bool value)
    {
        NotifyQuestion();
        if (value && FindText.Length == 0)
            FindText = Question?.Base?.Vendor ?? string.Empty;
        RefreshFindResults();
    }

    partial void OnFindTextChanged(string value) => RefreshFindResults();

    private void RefreshFindResults()
    {
        FindResults.Clear();
        if (!ShowsFind)
            return;
        string text = (FindText ?? string.Empty).Trim();
        if (text.Length < 2)
            return;
        // Every word must be found (vendor, model words), so "virpil alpha" finds the Alpha grips.
        string[] words = text.ToLowerInvariant().Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        foreach (RefDevice model in DeviceReferenceService.Catalog.Devices
                     .Where(d => d.Kind != "other" || d.Name.Length > 0)
                     .Where(d => words.All(w => (d.Name + " " + d.Vendor + " " + string.Join(" ", d.Names)).ToLowerInvariant().Contains(w)))
                     .OrderBy(d => d.Vendor).ThenBy(d => d.Name).Take(24))
            FindResults.Add(Candidate(model, 0, model.Vendor, false));
        _ = LoadCandidatePicturesAsync(FindResults.ToList());
    }

    // The user picked a candidate (or a catalog model he found): shown at once, kept by the host.
    [RelayCommand]
    private async Task ChooseCandidateAsync(ChoiceCandidate? candidate)
    {
        if (candidate == null)
            return;
        RefDevice chosen = candidate.Model;
        DeviceQuestion? asked = Question;
        ClearQuestion();
        if (chosen.IsGrip)
        {
            GripChoices.Clear();
            foreach (RefDevice grip in DeviceReferenceService.Catalog.GripsOf(chosen.Base).Concat(asked?.Candidates.Select(c => c.Model) ?? []).Distinct())
                GripChoices.Add(grip);
            OnPropertyChanged(nameof(HasGripChoices));
        }
        await ShowAsync(chosen, DeviceReferenceService.Catalog.BaseOf(chosen), DeviceName, T("the one you picked"), generic: false);
        if (chosen.IsGrip)
            GripChosen?.Invoke(this, chosen);
        else
            DeviceChosen?.Invoke(this, chosen.Id);
    }

    // The user said what kind of device an unknown one is: its class outline and the legend of its inputs.
    [RelayCommand]
    private async Task ChooseClassAsync(ClassChoice? choice)
    {
        if (choice == null)
            return;
        ClearQuestion();
        await ShowClassAsync(choice.Class, DeviceName, T("the kind of device you picked"));
        DeviceChosen?.Invoke(this, DeviceReferenceService.ClassChoicePrefix + choice.Class);
    }

    // Keeps the best fit without asking again.
    [RelayCommand]
    private async Task KeepBestCandidateAsync()
    {
        ChoiceCandidate? best = QuestionCandidates.FirstOrDefault();
        if (best != null)
            await ChooseCandidateAsync(best);
    }

    // "Choose another model..." on the device's card: the question again at any time, also after an answer, a wrong pick
    // or a weak match. The candidates: what the device's identification offers (the grips of its base, the models of its
    // USB id), the shown model and its base's grips, the models of its USB id and of the same maker and kind, ranked by
    // what the device reports. notMine ("This is not my device"): the shown model is wrong; the device is asked like an
    // unknown one (find it in the catalog, or pick its kind).
    public void AskAgain(bool notMine)
    {
        DeviceCatalog catalog = DeviceReferenceService.Catalog;
        DeviceProbe? probe = lastProbe;
        RefDevice? shown = Model == null || IsGeneric ? null : catalog.Find(Model.Id);
        if (notMine)
        {
            SetQuestion(new DeviceQuestion { Kind = "unknown", GuessedClass = probe != null ? DeviceClasses.Guess(probe) : DeviceClass });
            IsFinding = true;
            return;
        }
        List<RefDevice> models = [];
        DeviceMatch? match = probe != null ? DeviceReferenceService.Identify(probe) : null;
        if (match != null)
        {
            if (match.Model != null)
                models.Add(match.Model);
            models.AddRange(match.Choices);
        }
        RefDevice? basis = BaseModel ?? (shown != null ? catalog.BaseOf(shown) : null);
        if (shown != null)
            models.Add(shown);
        if (basis != null)
            models.AddRange(catalog.GripsOf(basis.Id));
        string vidPid = probe != null ? DeviceCatalog.NormalizeVidPid(probe.VidPid) : string.Empty;
        if (vidPid.Length > 0)
            models.AddRange(catalog.Devices.Where(d => d.Kind != "base" && d.VidPids.Contains(vidPid)));
        if (shown != null && shown.Vendor.Length > 0)
            models.AddRange(catalog.Devices.Where(d => d.Kind == shown.Kind && d.Kind != "base" && string.Equals(d.Vendor, shown.Vendor, StringComparison.OrdinalIgnoreCase)));
        List<RankedCandidate> ranked = DeviceChoices.Rank(models.Where(m => m.Kind != "base"), probe, catalog, shown);
        if (ranked.Count == 0)
        {
            SetQuestion(new DeviceQuestion { Kind = "unknown", GuessedClass = probe != null ? DeviceClasses.Guess(probe) : DeviceClass });
            IsFinding = true;
            return;
        }
        SetQuestion(new DeviceQuestion { Kind = basis != null ? "grip" : "model", Base = basis, Candidates = ranked });
    }

    [RelayCommand]
    private void ToggleFind() => IsFinding = !IsFinding;

    // Not now: the question goes away until the device is shown again.
    [RelayCommand]
    private void DismissQuestion() => ClearQuestion();
}
