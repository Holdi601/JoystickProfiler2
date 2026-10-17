using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// A node type of the palette.
public sealed record FfbPaletteItem(NodeKind Kind, string Category, string Title)
{
    public string Label => Category + " - " + Title;

    public override string ToString() => Label;
}

// The blueprint editor of the telemetry force feedback page (the Avalonia port of the Forza app's BlueprintEditor and
// BezierCurveEditor): the edited graph, selection, wiring with the graph's own checks, node properties, Bezier points,
// files in the Graphs folder and the live values the canvas shows. The canvas (Views/Controls/FfbGraphCanvas.cs) only
// draws and turns pointer input into the calls below, so everything here is testable without a window.
public sealed partial class FfbGraphEditorViewModel : ObservableObject
{
    private readonly Dictionary<string, double> liveValues = new(StringComparer.Ordinal);
    private FfbGraphEvaluator? evaluator;
    private int evaluatorVersion = -1;
    private int addCount;

    public FfbGraphEditorViewModel()
    {
        Graph = FfbPresets.Flight();
        SaveAsName = "my-flight";
    }

    // Every node kind, grouped like the Forza palette: sources, combining, maths, stateful, vectors, effects.
    public static IReadOnlyList<FfbPaletteItem> PaletteItems { get; } =
    [
        new(NodeKind.Constant, "Source", "Constant"),
        new(NodeKind.Telemetry, "Source", "Telemetry"),
        new(NodeKind.DeviceButton, "Input", "Device button / hat"),
        new(NodeKind.StickPosition, "Input", "Stick position"),
        new(NodeKind.Trim, "Input", "Trim (spring centre)"),
        new(NodeKind.Invert, "Direction", "Invert (flip direction)"),
        new(NodeKind.Group, "Combine", "Group (several wires)"),
        new(NodeKind.Add, "Maths", "Add"),
        new(NodeKind.Multiply, "Maths", "Multiply"),
        new(NodeKind.ScaleOffset, "Maths", "Scale and offset"),
        new(NodeKind.Clamp, "Maths", "Clamp"),
        new(NodeKind.Abs, "Maths", "Absolute value"),
        new(NodeKind.Min, "Maths", "Minimum"),
        new(NodeKind.Max, "Maths", "Maximum"),
        new(NodeKind.Curve, "Maths", "Bezier curve"),
        new(NodeKind.Select, "Maths", "Select (switch)"),
        new(NodeKind.LowPass, "Stateful", "Low-pass filter"),
        new(NodeKind.SlewLimit, "Stateful", "Slew limit"),
        new(NodeKind.Derivative, "Stateful", "Derivative (rate)"),
        new(NodeKind.CounterDeltaToEvent, "Stateful", "Counter change to events"),
        new(NodeKind.Envelope, "Stateful", "Envelope (kick per event)"),
        new(NodeKind.Threshold, "Stateful", "Threshold (on/off)"),
        new(NodeKind.MakeVec2, "Vector", "Make vector"),
        new(NodeKind.SplitVec2, "Vector", "Split vector"),
        new(NodeKind.ConstantForce, "Effect", "Constant force"),
        new(NodeKind.Spring, "Effect", "Spring"),
        new(NodeKind.Damper, "Effect", "Damper"),
        new(NodeKind.Friction, "Effect", "Friction"),
        new(NodeKind.Periodic, "Effect", "Periodic (vibration, pulses)")
    ];

    public IReadOnlyList<FfbPaletteItem> Palette => PaletteItems;

    public FfbGraph Graph { get; private set; }

    // Bumped by every change of nodes, wires, parameters or positions: the canvas rebuilds its cached texts and wires,
    // the local preview its evaluator.
    public int Version { get; private set; }

    public IReadOnlyDictionary<string, double> LiveValues => liveValues;

    public ObservableCollection<FfbNodePropertyItem> Properties { get; } = [];

    public ObservableCollection<string> GraphFiles { get; } = [];

    // Telemetry keys for the Telemetry node's key picker (normalised keys, the game's raw keys, keys seen live).
    public ObservableCollection<string> TelemetryKeyOptions { get; } = [];

    // Min/max defaults of a key (set by the page for the current game).
    public Func<string, TelemetryDescriptor?> DescribeKey { get; set; } = AircraftTelemetry.Find;

    internal Func<string> GraphFolderProvider { get; set; } = () => FfbTelemetryServices.GraphFolder();

    // The world point in the middle of the canvas (new nodes go there).
    public double ViewCenterX { get; set; } = 400;

    public double ViewCenterY { get; set; } = 300;

    [ObservableProperty]
    private FfbPaletteItem? selectedPaletteItem;

    [ObservableProperty]
    private FfbNode? selectedNode;

    [ObservableProperty]
    private FfbConnection? selectedConnection;

    [ObservableProperty]
    private int selectedCurvePoint = -1;

    [ObservableProperty]
    private string message = "Drag from an output (right) to an input (left) to connect. Click a wire and press Delete to remove it.";

    // File name in the Graphs folder ("" = not saved yet).
    [ObservableProperty]
    private string currentFile = string.Empty;

    [ObservableProperty]
    private string saveAsName = string.Empty;

    [ObservableProperty]
    private bool isDirty;

    [ObservableProperty]
    private string? selectedGraphFile;

    public string DocumentTitle => (CurrentFile.Length == 0 ? T("{0} (not saved)", Graph.Name) : CurrentFile) + (IsDirty ? " *" : string.Empty);

    public bool HasSelectedNode => SelectedNode != null;

    public bool SelectedIsCurve => SelectedNode is CurveNode;

    public BezierCurve? SelectedCurve => (SelectedNode as CurveNode)?.Curve;

    public string SelectedNodeHeader => SelectedNode == null
        ? (SelectedConnection != null ? T("Wire selected: press Delete to remove it.") : T("Select a node to edit it."))
        : SelectedNode.Title + "  (" + SelectedNode.Kind + ")";

    public event EventHandler? Changed;

    public event EventHandler? FitRequested;

    // A graph file was saved (its name in the Graphs folder).
    public event Action<string>? Saved;

    // ---- "Press the button now": assign buttons and hats to input nodes ----

    // Set by the page: waits up to the given time for the next button / hat press on any device and answers
    // (device as the capture names it, "Name {guid}"; input "JOY_BTN5" / "JOY_BTN_POV1_U"), null when nothing came.
    public Func<TimeSpan, CancellationToken, Task<(string Device, string Input)?>>? CaptureInput { get; set; }

    // The force feedback stick's instance guid: a press on the stick itself is saved as "" (works for any stick).
    public Func<string>? ForceFeedbackDevice { get; set; }

    public TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public bool CanCaptureInput => SelectedNode is DeviceButtonNode && !Capturing;

    public bool CanAssignTrimButtons => SelectedNode is TrimNode && !Capturing;

    [ObservableProperty]
    private bool capturing;

    private CancellationTokenSource? captureCts;
    private bool captureCancelled;

    partial void OnCapturingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCaptureInput));
        OnPropertyChanged(nameof(CanAssignTrimButtons));
    }

    // The selected button node: the next press on any device becomes its device and input.
    [RelayCommand]
    private async Task CaptureSelectedInputAsync()
    {
        if (SelectedNode is not DeviceButtonNode node)
            return;
        Message = T("Press the button or hat for \"{0}\" now (on any device)...", node.Title);
        bool assigned = await CaptureIntoAsync(node);
        if (!assigned && !captureCancelled && CaptureInput != null)
            Message = T("Nothing pressed: \"{0}\" is unchanged.", node.Title);
    }

    // The selected Trim node: one press per trim input (to stick position, nose down, nose up, left, right, reset); a
    // button node is added for an input without one. Waiting out the time skips an input.
    [RelayCommand]
    private async Task AssignTrimButtonsAsync()
    {
        if (SelectedNode is not TrimNode trim)
            return;
        (string Port, string Title)[] ports =
        [
            ("here", T("trim to the stick position (hold to move the stick freely)")), ("up", T("nose down / hat up")), ("down", T("nose up / hat down")),
            ("left", "trim left"), ("right", "trim right"), ("reset", "reset trim")
        ];
        int done = 0;
        for (int i = 0; i < ports.Length; i++)
        {
            (string port, string title) = ports[i];
            DeviceButtonNode button = ButtonFor(trim, port, i);
            Message = T("Trim {0} of 6: press the button for \"{1}\" now (wait {2} s to skip it).", (i + 1).ToString(CultureInfo.InvariantCulture), title, CaptureTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
            if (await CaptureIntoAsync(button))
                done++;
            if (captureCancelled)
                return;
        }
        SelectedNode = trim;
        Message = T("Trim buttons: {0} of 6 assigned. Save the graph to keep them.", done.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void CancelCapture()
    {
        captureCts?.Cancel();
    }

    // The button node wired into a trim input, or a new one placed left of the trim node.
    private DeviceButtonNode ButtonFor(TrimNode trim, string port, int index)
    {
        FfbConnection? wire = Graph.Connections.FirstOrDefault(c => c.ToNode == trim.Id && c.ToPort == port);
        if (wire != null && Graph.FindNode(wire.FromNode) is DeviceButtonNode existing)
            return existing;
        DeviceButtonNode button = Graph.Add(new DeviceButtonNode
        {
            Title = T("Trim: {0}", port),
            X = trim.X - FfbGraphLayout.NodeWidth - 40,
            Y = trim.Y + index * 90
        });
        Graph.TryConnect(button.Id, "held", trim.Id, port);
        MarkEdited();
        return button;
    }

    private async Task<bool> CaptureIntoAsync(DeviceButtonNode node)
    {
        if (CaptureInput == null)
        {
            Message = T("Capture is not available here: type the device and input (JOY_BTN5, JOY_BTN_POV1_U) instead.");
            return false;
        }
        captureCts?.Dispose();
        captureCts = new CancellationTokenSource();
        captureCancelled = false;
        Capturing = true;
        try
        {
            (string Device, string Input)? result = await CaptureInput(CaptureTimeout, captureCts.Token);
            if (result is not { } pressed || JoyPro.Ffb.Core.Input.FfbInputRef.Parse(pressed.Input).IsNone)
                return false;
            string stick = ForceFeedbackDevice?.Invoke() ?? string.Empty;
            bool onStick = JoyPro.Ffb.Core.Input.FfbDeviceMatch.GuidOf(stick) is { } guid && JoyPro.Ffb.Core.Input.FfbDeviceMatch.GuidOf(pressed.Device) == guid;
            node.Device = onStick ? string.Empty : pressed.Device;
            node.Input = pressed.Input;
            MarkEdited();
            if (SelectedNode == node)
                RebuildProperties();
            Message = T("{0}: {1} on {2}.", node.Title, pressed.Input, (onStick ? T("the force feedback stick") : pressed.Device));
            return true;
        }
        catch (OperationCanceledException)
        {
            Message = T("Capture cancelled.");
            captureCancelled = true;
            return false;
        }
        finally
        {
            Capturing = false;
        }
    }

    partial void OnSelectedNodeChanged(FfbNode? value)
    {
        if (value != null)
            SelectedConnection = null;
        SelectedCurvePoint = -1;
        RebuildProperties();
        OnPropertyChanged(nameof(CanCaptureInput));
        OnPropertyChanged(nameof(CanAssignTrimButtons));
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(SelectedIsCurve));
        OnPropertyChanged(nameof(SelectedCurve));
        OnPropertyChanged(nameof(SelectedNodeHeader));
        RaiseChanged();
    }

    partial void OnSelectedConnectionChanged(FfbConnection? value)
    {
        if (value != null)
            SelectedNode = null;
        OnPropertyChanged(nameof(SelectedNodeHeader));
        RaiseChanged();
    }

    partial void OnCurrentFileChanged(string value) => OnPropertyChanged(nameof(DocumentTitle));

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DocumentTitle));

    public FfbNode AddNode(NodeKind kind, double? x = null, double? y = null)
    {
        FfbNode node = FfbNodeFactory.Create(kind);
        int step = addCount++ % 6;
        node.X = Math.Round(x ?? ViewCenterX - FfbGraphLayout.NodeWidth / 2 + step * 24);
        node.Y = Math.Round(y ?? ViewCenterY - 40 + step * 24);
        if (PaletteItems.FirstOrDefault(p => p.Kind == kind) is { } item)
            node.Title = item.Title;
        Graph.Add(node);
        MarkEdited();
        SelectedNode = node;
        Message = T("Added {0}.", node.Title);
        return node;
    }

    [RelayCommand]
    private void AddSelectedPaletteNode()
    {
        if (SelectedPaletteItem == null)
        {
            Message = T("Pick a node type in the list first.");
            return;
        }
        AddNode(SelectedPaletteItem.Kind);
    }

    // What connecting would answer (the canvas marks the inputs while a wire is dragged).
    public ConnectResult CheckConnection(string fromNode, string fromPort, string toNode, string toPort) =>
        Graph.Check(fromNode, fromPort, toNode, toPort);

    public ConnectResult Connect(string fromNode, string fromPort, string toNode, string toPort)
    {
        ConnectResult result = Graph.TryConnect(fromNode, fromPort, toNode, toPort);
        if (result == ConnectResult.Ok)
        {
            MarkEdited();
            Message = T("Connected {0}.{1} -> {2}.{3}.", NodeTitle(fromNode), fromPort, NodeTitle(toNode), toPort);
        }
        else
        {
            Message = RefusalText(result);
        }
        RaiseChanged();
        return result;
    }

    public static string RefusalText(ConnectResult result) => result switch
    {
        ConnectResult.Cycle => T("Refused (Cycle): this wire would close a loop - a node would feed itself."),
        ConnectResult.TypeMismatch => T("Refused (TypeMismatch): the port types do not fit (a vector only goes into a vector input; use a Threshold to turn a number into events)."),
        ConnectResult.UnknownPort => T("Refused (UnknownPort): that port does not exist."),
        ConnectResult.UnknownNode => T("Refused (UnknownNode): that node does not exist."),
        _ => T("Connected.")
    };

    public bool RemoveConnection(FfbConnection connection)
    {
        if (!Graph.Disconnect(connection))
            return false;
        if (SelectedConnection == connection)
            SelectedConnection = null;
        MarkEdited();
        Message = T("Removed the wire {0}.{1} -> {2}.{3}.", NodeTitle(connection.FromNode), connection.FromPort, NodeTitle(connection.ToNode), connection.ToPort);
        return true;
    }

    public bool RemoveNode(FfbNode node)
    {
        if (!Graph.RemoveNode(node.Id))
            return false;
        if (SelectedNode == node)
            SelectedNode = null;
        liveValues.Remove(node.Id);
        MarkEdited();
        Message = T("Deleted {0}.", node.Title);
        return true;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedConnection is { } connection)
            RemoveConnection(connection);
        else if (SelectedNode is { } node)
            RemoveNode(node);
        else
            Message = T("Select a node or a wire first.");
    }

    public void MoveNode(FfbNode node, double x, double y)
    {
        node.X = Math.Round(x);
        node.Y = Math.Round(y);
        MarkEdited();
    }

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NewFromBuiltIn()
    {
        SetGraph(FfbPresets.Flight(), string.Empty);
        SaveAsName = "my-flight";
        IsDirty = true;
        Message = T("A copy of the built-in flight graph. Save it under a name to use it for a game or aircraft.");
    }

    [RelayCommand]
    private void RefreshGraphFiles()
    {
        string? keep = SelectedGraphFile;
        GraphFiles.Clear();
        string folder = GraphFolderProvider();
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*" + TelemetryFfbPaths.GraphExtension)
                             .Select(Path.GetFileName)
                             .OfType<string>()
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    GraphFiles.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = T("Cannot read the Graphs folder: {0}", ex.Message);
        }
        SelectedGraphFile = GraphFiles.FirstOrDefault(f => string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                            ?? GraphFiles.FirstOrDefault(f => string.Equals(f, CurrentFile, StringComparison.OrdinalIgnoreCase))
                            ?? GraphFiles.FirstOrDefault();
    }

    [RelayCommand]
    private void LoadSelectedGraph()
    {
        if (string.IsNullOrWhiteSpace(SelectedGraphFile))
        {
            Message = T("No graph file picked (they live in {0}).", GraphFolderProvider());
            return;
        }
        LoadFile(SelectedGraphFile);
    }

    // A file name in the Graphs folder or a full path.
    public bool LoadFile(string file)
    {
        string path = Path.IsPathRooted(file) ? file : Path.Combine(GraphFolderProvider(), file);
        try
        {
            FfbGraphLoadResult result = FfbGraphPersistence.Load(path);
            string name = Path.GetFileName(path);
            SetGraph(result.Graph, string.Equals(Path.GetDirectoryName(path), GraphFolderProvider(), StringComparison.OrdinalIgnoreCase) ? name : string.Empty);
            SaveAsName = StripGraphExtension(name);
            Message = T("Loaded {0}.{1}", name, (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : string.Empty));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Message = T("Cannot load {0}: {1}", Path.GetFileName(path), ex.Message);
            return false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (CurrentFile.Length == 0)
            SaveAs();
        else
            SaveTo(CurrentFile);
    }

    [RelayCommand]
    private void SaveAs()
    {
        string? name = NormaliseFileName(SaveAsName);
        if (name == null)
        {
            Message = T("Type a file name to save the graph under.");
            return;
        }
        SaveTo(name);
    }

    private void SaveTo(string fileName)
    {
        string folder = GraphFolderProvider();
        string path = Path.Combine(folder, fileName);
        try
        {
            Directory.CreateDirectory(folder);
            if (string.IsNullOrWhiteSpace(Graph.Name) || Graph.Name == FfbPresets.FlightName)
                Graph.Name = StripGraphExtension(fileName);
            FfbGraphPersistence.Save(Graph, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = T("Cannot save {0}: {1}", fileName, ex.Message);
            return;
        }
        CurrentFile = fileName;
        SaveAsName = StripGraphExtension(fileName);
        IsDirty = false;
        RefreshGraphFiles();
        Message = T("Saved {0}.", path);
        OnPropertyChanged(nameof(DocumentTitle));
        Saved?.Invoke(fileName);
    }

    // "my graph" -> "my graph.jpffb.json"; null when nothing usable is left.
    internal static string? NormaliseFileName(string? text)
    {
        string name = (text ?? string.Empty).Trim();
        name = StripGraphExtension(name);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        StringBuilder clean = new();
        foreach (char c in name)
            clean.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        name = clean.ToString().Trim().Trim('.');
        return name.Length == 0 ? null : name + TelemetryFfbPaths.GraphExtension;
    }

    private static string StripGraphExtension(string name) =>
        name.EndsWith(TelemetryFfbPaths.GraphExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^TelemetryFfbPaths.GraphExtension.Length]
            : name;

    public void SetGraph(FfbGraph graph, string fileName)
    {
        Graph = graph;
        SelectedNode = null;
        SelectedConnection = null;
        CurrentFile = fileName;
        IsDirty = false;
        liveValues.Clear();
        evaluator = null;
        Version++;
        OnPropertyChanged(nameof(Graph));
        OnPropertyChanged(nameof(DocumentTitle));
        RaiseChanged();
        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- Bezier curve of the selected Curve node (the Forza BezierCurveEditor's rules) ----

    public int AddCurvePoint(double x, double y)
    {
        if (SelectedCurve is not { } curve)
            return -1;
        int index = curve.AddNode(new CurvePoint(x, y));
        SelectedCurvePoint = index;
        CurveEdited();
        return index;
    }

    public bool RemoveCurvePoint(int index)
    {
        if (SelectedCurve is not { } curve || !curve.RemoveNode(index))
            return false;
        SelectedCurvePoint = -1;
        CurveEdited();
        return true;
    }

    [RelayCommand]
    private void RemoveSelectedCurvePoint()
    {
        if (!RemoveCurvePoint(SelectedCurvePoint))
            Message = T("Select a middle point of the curve first (the end points stay).");
    }

    [RelayCommand]
    private void ResetCurveLinear()
    {
        if (SelectedCurve is not { } curve)
            return;
        curve.ResetLinear();
        SelectedCurvePoint = -1;
        CurveEdited();
    }

    // Moves a point; its handles move along, the ends stay at x 0 and 1, neighbours keep their order.
    public void MoveCurveAnchor(int index, double x, double y)
    {
        if (SelectedCurve is not { } curve || index < 0 || index >= curve.Nodes.Count)
            return;
        List<BezierNode> nodes = curve.Nodes;
        BezierNode node = nodes[index];
        CurvePoint old = node.Position;
        double nx = index == 0 ? 0 : index == nodes.Count - 1 ? 1
            : Math.Clamp(x, nodes[index - 1].Position.X + 0.01, nodes[index + 1].Position.X - 0.01);
        CurvePoint next = new(nx, Math.Clamp(y, 0, 1));
        double dx = next.X - old.X;
        double dy = next.Y - old.Y;
        node.Position = next;
        node.InHandle = Clamp01(new CurvePoint(node.InHandle.X + dx, node.InHandle.Y + dy));
        node.OutHandle = Clamp01(new CurvePoint(node.OutHandle.X + dx, node.OutHandle.Y + dy));
        if (index > 0)
        {
            BezierNode previous = nodes[index - 1];
            previous.OutHandle = new CurvePoint(Math.Min(previous.OutHandle.X, next.X), previous.OutHandle.Y);
            node.InHandle = new CurvePoint(Math.Clamp(node.InHandle.X, previous.Position.X, next.X), node.InHandle.Y);
        }
        if (index < nodes.Count - 1)
        {
            BezierNode following = nodes[index + 1];
            following.InHandle = new CurvePoint(Math.Max(following.InHandle.X, next.X), following.InHandle.Y);
            node.OutHandle = new CurvePoint(Math.Clamp(node.OutHandle.X, next.X, following.Position.X), node.OutHandle.Y);
        }
        CurveEdited();
    }

    // A handle stays between its point and the neighbouring point on x.
    public void MoveCurveHandle(int index, bool inHandle, double x, double y)
    {
        if (SelectedCurve is not { } curve || index < 0 || index >= curve.Nodes.Count)
            return;
        List<BezierNode> nodes = curve.Nodes;
        BezierNode node = nodes[index];
        y = Math.Clamp(y, 0, 1);
        if (inHandle && index > 0)
            node.InHandle = new CurvePoint(Math.Clamp(x, nodes[index - 1].Position.X, node.Position.X), y);
        else if (!inHandle && index < nodes.Count - 1)
            node.OutHandle = new CurvePoint(Math.Clamp(x, node.Position.X, nodes[index + 1].Position.X), y);
        else
            return;
        CurveEdited();
    }

    private static CurvePoint Clamp01(CurvePoint point) => new(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1));

    private void CurveEdited()
    {
        MarkEdited();
        Message = T("Curve: {0} points.", (SelectedCurve?.Nodes.Count ?? 0).ToString(CultureInfo.InvariantCulture));
    }

    // ---- Live values ----

    // The values of a "nodes" line of the running JoyPro.Ffb (node id -> first output).
    public void SetLiveValues(IReadOnlyDictionary<string, double> values)
    {
        liveValues.Clear();
        foreach (KeyValuePair<string, double> pair in values)
            liveValues[pair.Key] = pair.Value;
        RaiseChanged();
    }

    public void ClearLiveValues()
    {
        if (liveValues.Count == 0)
            return;
        liveValues.Clear();
        RaiseChanged();
    }

    // Without the game: one tick of a private copy of the graph (no telemetry; constants and their maths still work).
    public void EvaluateLocally(double time)
    {
        if (evaluator == null || evaluatorVersion != Version)
        {
            evaluator = new FfbGraphEvaluator(FfbGraphPersistence.Clone(Graph));
            evaluatorVersion = Version;
        }
        evaluator.Evaluate(null, time);
        liveValues.Clear();
        evaluator.CopyNodeValues(liveValues);
        RaiseChanged();
    }

    // ---- Node properties ----

    private static readonly HashSet<string> HiddenProperties = new(StringComparer.Ordinal) { nameof(FfbNode.Id), nameof(FfbNode.X), nameof(FfbNode.Y) };

    private void RebuildProperties()
    {
        Properties.Clear();
        FfbNode? node = SelectedNode;
        if (node == null)
            return;
        List<PropertyInfo> list = node.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && !HiddenProperties.Contains(p.Name))
            .OrderBy(p => p.Name == nameof(FfbNode.Title) ? 0 : 1)
            .ToList();
        foreach (PropertyInfo property in list)
        {
            if (CreateItem(node, property) is { } item)
                Properties.Add(item);
        }
        foreach (PortSpec port in node.Inputs.Where(p => p.Type == PortType.Scalar))
        {
            string name = port.Name;
            Properties.Add(new FfbNodePropertyItem(
                "input:" + name,
                T("Input \"{0}\" when unconnected", name),
                FfbPropertyEditorKind.Number,
                typeof(double),
                [],
                () => node.DefaultFor(port),
                value => node.InputDefaults[name] = (double)value!,
                OnPropertyEdited));
        }
    }

    // Plain words for the direction switches (the property names stay as saved in the files).
    internal static string? LabelOverride(FfbNode node, string property) => (node, property) switch
    {
        (ConstantForceNode, nameof(ConstantForceNode.Negative)) => "Invert direction (push the other way)",
        (SpringNode, nameof(SpringNode.InvertCentre)) => "Invert centre (trim moves the other way)",
        (InvertNode, nameof(InvertNode.Enabled)) => "Invert (flip the direction)",
        (TrimNode, nameof(TrimNode.InvertX)) => "Invert left/right trim buttons",
        (TrimNode, nameof(TrimNode.InvertY)) => "Invert up/down trim buttons",
        (TrimNode, nameof(TrimNode.Step)) => "Step per press (fraction of the travel)",
        (TrimNode, nameof(TrimNode.RepeatRate)) => "Repeat while held (steps per second, 0 = off)",
        (TrimNode, nameof(TrimNode.RepeatDelay)) => "Repeat after (seconds held)",
        (TrimNode, nameof(TrimNode.MoveRate)) => "Centre moves at most (travel per second, 0 = at once)",
        (DeviceButtonNode, nameof(DeviceButtonNode.Device)) => "Device (empty = the force feedback stick)",
        (DeviceButtonNode, nameof(DeviceButtonNode.Input)) => "Button or hat (JOY_BTN5, JOY_BTN_POV1_U)",
        (StickPositionNode, nameof(StickPositionNode.Device)) => "Device (empty = the force feedback stick)",
        _ => null
    };

    private FfbNodePropertyItem? CreateItem(FfbNode node, PropertyInfo property)
    {
        Type type = property.PropertyType;
        string label = LabelOverride(node, property.Name) ?? Label(property.Name);
        Func<object?> get = () => property.GetValue(node);
        Action<object?> set = value => property.SetValue(node, value);
        if (node is EffectSinkNode && property.Name == nameof(EffectSinkNode.Effect))
        {
            List<string> options = [FfbNodePropertyItem.NoneOption, .. FfbEffectNames.All.Select(e => e.Name)];
            return new FfbNodePropertyItem(property.Name, T("Effect (per-game switch)"), FfbPropertyEditorKind.Choice, type, options, get, set, OnPropertyEdited);
        }
        if (node is TelemetryNode && property.Name == nameof(TelemetryNode.Key))
            return new FfbNodePropertyItem(property.Name, T("Telemetry key"), FfbPropertyEditorKind.TelemetryKey, type, TelemetryKeyOptions, get, set, OnPropertyEdited);
        if (type == typeof(bool))
            return new FfbNodePropertyItem(property.Name, label, FfbPropertyEditorKind.Toggle, type, [], get, set, OnPropertyEdited);
        if (type.IsEnum)
            return new FfbNodePropertyItem(property.Name, label, FfbPropertyEditorKind.Choice, type, Enum.GetNames(type), get, set, OnPropertyEdited);
        if (type == typeof(double) || type == typeof(int))
            return new FfbNodePropertyItem(property.Name, label, FfbPropertyEditorKind.Number, type, [], get, set, OnPropertyEdited);
        if (type == typeof(string))
            return new FfbNodePropertyItem(property.Name, label, FfbPropertyEditorKind.Text, type, [], get, set, OnPropertyEdited);
        return null;
    }

    // "MinFrequencyHz" -> "Min frequency Hz".
    internal static string Label(string name)
    {
        StringBuilder text = new();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool wordStart = i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (wordStart)
                text.Append(' ');
            bool keep = i == 0 || (c == 'H' && i + 1 < name.Length && name[i + 1] == 'z');
            text.Append(keep ? c : char.ToLowerInvariant(c));
        }
        return text.ToString();
    }

    private void OnPropertyEdited(FfbNodePropertyItem item)
    {
        if (SelectedNode is TelemetryNode telemetry && item.Name == nameof(TelemetryNode.Key) && DescribeKey(telemetry.Key) is { } descriptor)
        {
            telemetry.Minimum = descriptor.Min;
            telemetry.Maximum = descriptor.Max;
            foreach (FfbNodePropertyItem other in Properties.Where(p => p.Name is nameof(TelemetryNode.Minimum) or nameof(TelemetryNode.Maximum)))
                other.Refresh();
            Message = T("{0}: {1}{2}, range {3} .. {4}.", telemetry.Key, descriptor.Title, (descriptor.Unit.Length > 0 ? " (" + descriptor.Unit + ")" : string.Empty), descriptor.Min.ToString(CultureInfo.InvariantCulture), descriptor.Max.ToString(CultureInfo.InvariantCulture));
        }
        if (item.Name == nameof(FfbNode.Title))
            OnPropertyChanged(nameof(SelectedNodeHeader));
        MarkEdited();
    }

    private void MarkEdited()
    {
        Version++;
        IsDirty = true;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private string NodeTitle(string id) => Graph.FindNode(id)?.Title ?? id;
}

public enum FfbPropertyEditorKind
{
    Number,
    Toggle,
    Choice,
    Text,
    TelemetryKey
}

// One editable setting of the selected node (any public settable property, or an unconnected input's value).
public sealed partial class FfbNodePropertyItem : ObservableObject
{
    public const string NoneOption = "(none)";

    private readonly Type valueType;
    private readonly Func<object?> getter;
    private readonly Action<object?> setter;
    private readonly Action<FfbNodePropertyItem> edited;
    private bool syncing;

    public FfbNodePropertyItem(string name, string label, FfbPropertyEditorKind kind, Type valueType, IEnumerable<string> options,
        Func<object?> getter, Action<object?> setter, Action<FfbNodePropertyItem> edited)
    {
        Name = name;
        Label = label;
        Kind = kind;
        this.valueType = valueType;
        Options = options as IReadOnlyList<string> ?? options.ToList();
        this.getter = getter;
        this.setter = setter;
        this.edited = edited;
        Refresh();
    }

    public string Name { get; }

    public string Label { get; }

    public FfbPropertyEditorKind Kind { get; }

    public IReadOnlyList<string> Options { get; }

    public bool IsNumber => Kind == FfbPropertyEditorKind.Number;

    public bool IsToggle => Kind == FfbPropertyEditorKind.Toggle;

    public bool IsChoice => Kind == FfbPropertyEditorKind.Choice;

    public bool IsText => Kind == FfbPropertyEditorKind.Text;

    public bool IsTelemetryKey => Kind == FfbPropertyEditorKind.TelemetryKey;

    [ObservableProperty]
    private string text = string.Empty;

    [ObservableProperty]
    private bool isOn;

    [ObservableProperty]
    private string? selectedOption;

    [ObservableProperty]
    private string error = string.Empty;

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    // Reads the node's value again (after another setting changed it).
    public void Refresh()
    {
        syncing = true;
        try
        {
            object? value = getter();
            switch (Kind)
            {
                case FfbPropertyEditorKind.Number:
                    Text = Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.#####", CultureInfo.InvariantCulture);
                    break;
                case FfbPropertyEditorKind.Toggle:
                    IsOn = value is true;
                    break;
                case FfbPropertyEditorKind.Choice:
                    string current = value?.ToString() ?? string.Empty;
                    SelectedOption = current.Length == 0 && Options.Contains(NoneOption) ? NoneOption : current;
                    break;
                default:
                    Text = value?.ToString() ?? string.Empty;
                    break;
            }
            Error = string.Empty;
        }
        finally
        {
            syncing = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (syncing)
            return;
        if (Kind == FfbPropertyEditorKind.Number)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
                !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number) || !double.IsFinite(number))
            {
                Error = T("Not a number.");
                return;
            }
            Error = string.Empty;
            setter(valueType == typeof(int) ? (object)(int)Math.Round(number) : number);
        }
        else if (Kind is FfbPropertyEditorKind.Text or FfbPropertyEditorKind.TelemetryKey)
        {
            setter((value ?? string.Empty).Trim());
        }
        else
        {
            return;
        }
        edited(this);
    }

    partial void OnIsOnChanged(bool value)
    {
        if (syncing || Kind != FfbPropertyEditorKind.Toggle)
            return;
        setter(value);
        edited(this);
    }

    partial void OnSelectedOptionChanged(string? value)
    {
        if (syncing || Kind != FfbPropertyEditorKind.Choice || value == null)
            return;
        if (valueType.IsEnum)
        {
            if (!Enum.TryParse(valueType, value, out object? parsed))
                return;
            setter(parsed);
        }
        else
        {
            setter(value == NoneOption ? string.Empty : value);
        }
        edited(this);
    }
}

// Node box geometry shared by the canvas and the editor (world units; the canvas zooms).
public static class FfbGraphLayout
{
    public const double NodeWidth = 210;
    public const double HeaderHeight = 26;
    public const double RowHeight = 18;
    public const double BodyPadding = 6;
    public const double FooterHeight = 30;

    public static double Height(FfbNode node) =>
        HeaderHeight + BodyPadding + Math.Max(1, Math.Max(node.Inputs.Count, node.Outputs.Count)) * RowHeight + FooterHeight;

    public static (double X, double Y) InputPort(FfbNode node, int index) =>
        (node.X, node.Y + HeaderHeight + BodyPadding + index * RowHeight + RowHeight / 2);

    public static (double X, double Y) OutputPort(FfbNode node, int index) =>
        (node.X + NodeWidth, node.Y + HeaderHeight + BodyPadding + index * RowHeight + RowHeight / 2);
}
