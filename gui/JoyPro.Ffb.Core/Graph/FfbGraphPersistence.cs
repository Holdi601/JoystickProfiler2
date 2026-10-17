using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoyPro.Ffb.Core.Graph;

public sealed record FfbGraphLoadResult(FfbGraph Graph, IReadOnlyList<string> Warnings);

/// <summary>Saves and loads blueprint graphs as JSON (*.ffbgraph.json).</summary>
/// <remarks>
/// Follows the Forza SignalGraphPersistence (one DTO per node, unknown connections dropped, Enabled forced off on
/// load) with these changes: a "format" and "version" field; node ids are kept as they are (Forza handed out new
/// GUIDs on every load), so other files can refer to a node; connections name their ports; node parameters live in a
/// per-node "params" object instead of one flat DTO with every field of every node type.
///
/// Format (version 1):
/// <code>
/// { "format": "joypro-ffb-graph", "version": 1, "name": "...", "enabled": false,
///   "nodes": [ { "id": "n1", "kind": "Telemetry", "title": "G", "x": 0, "y": 0,
///                "params": { "key": "il2.g", ... }, "inputs": { "b": 0.5 } } ],
///   "connections": [ { "from": "n1", "fromPort": "value", "to": "n2", "toPort": "in" } ] }
/// </code>
/// </remarks>
public static class FfbGraphPersistence
{
    public const string Format = "joypro-ffb-graph";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string ToJson(FfbGraph graph)
    {
        var nodes = new JsonArray();
        foreach (var node in graph.Nodes)
        {
            var parameters = new JsonObject();
            node.SaveParams(parameters);
            var item = new JsonObject
            {
                ["id"] = node.Id,
                ["kind"] = node.Kind.ToString(),
                ["title"] = node.Title,
                ["x"] = node.X,
                ["y"] = node.Y,
                ["params"] = parameters
            };
            if (node.InputDefaults.Count > 0)
            {
                var inputs = new JsonObject();
                foreach (var pair in node.InputDefaults.OrderBy(p => p.Key, StringComparer.Ordinal))
                    inputs[pair.Key] = pair.Value;
                item["inputs"] = inputs;
            }

            nodes.Add(item);
        }

        var connections = new JsonArray();
        foreach (var c in graph.Connections)
        {
            connections.Add(new JsonObject
            {
                ["from"] = c.FromNode,
                ["fromPort"] = c.FromPort,
                ["to"] = c.ToNode,
                ["toPort"] = c.ToPort
            });
        }

        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = CurrentVersion,
            ["name"] = graph.Name,
            ["enabled"] = graph.Enabled,
            ["nodes"] = nodes,
            ["connections"] = connections
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Reads a graph. Enabled is always false afterwards: loading never starts force output.</summary>
    /// <exception cref="InvalidDataException">Not a graph file, or written by a newer JoyPro.</exception>
    public static FfbGraphLoadResult FromJson(string json) => FromJson(json, keepEnabled: false);

    public static void Save(FfbGraph graph, string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(graph));
        File.Move(temp, path, overwrite: true);
    }

    public static FfbGraphLoadResult Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>A deep copy with the same ids and fresh node state (the runtime evaluates a copy, the editor edits the original).</summary>
    public static FfbGraph Clone(FfbGraph graph) => FromJson(ToJson(graph), keepEnabled: true).Graph;

    private static FfbGraphLoadResult FromJson(string json, bool keepEnabled)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("The graph file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The graph file is not valid JSON: " + ex.Message, ex);
        }

        var warnings = new List<string>();
        var format = root.Str("format", "");
        if (format.Length > 0 && format != Format)
            throw new InvalidDataException($"Not a JoyPro force feedback graph (format \"{format}\").");

        var version = root["version"] is JsonValue v && v.TryGetValue<int>(out var number) ? number : 0;
        if (version == 0)
        {
            warnings.Add("The file has no version; read as version " + CurrentVersion + ".");
            version = CurrentVersion;
        }

        if (version > CurrentVersion)
            throw new InvalidDataException(
                $"The graph was saved by a newer JoyPro (version {version}, this one reads up to {CurrentVersion}).");

        var graph = new FfbGraph { Name = root.Str("name", "") };
        if (root["nodes"] is JsonArray nodes)
        {
            foreach (var item in nodes.OfType<JsonObject>())
            {
                var kindText = item.Str("kind", "");
                if (!Enum.TryParse<NodeKind>(kindText, false, out var kind) || !Enum.IsDefined(kind))
                {
                    warnings.Add($"Skipped a node of unknown kind \"{kindText}\".");
                    continue;
                }

                var node = FfbNodeFactory.Create(kind);
                var id = item.Str("id", "");
                if (id.Length == 0 || graph.FindNode(id) is not null)
                    warnings.Add($"Node \"{item.Str("title", kindText)}\" had a missing or duplicate id; it got a new one.");
                else
                    node.Id = id;
                node.Title = item.Str("title", kind.ToString());
                node.X = item.Num("x", 0);
                node.Y = item.Num("y", 0);
                node.LoadParams(item["params"] as JsonObject ?? new JsonObject());
                if (item["inputs"] is JsonObject inputs)
                {
                    foreach (var pair in inputs)
                    {
                        if (node.InputIndex(pair.Key) >= 0 && pair.Value is JsonValue value &&
                            value.TryGetValue<double>(out var d) && double.IsFinite(d))
                            node.InputDefaults[pair.Key] = d;
                    }
                }

                graph.Add(node);
            }
        }

        if (root["connections"] is JsonArray connections)
        {
            foreach (var item in connections.OfType<JsonObject>())
            {
                var from = item.Str("from", "");
                var fromPort = item.Str("fromPort", "");
                var to = item.Str("to", "");
                var toPort = item.Str("toPort", "");
                var result = graph.TryConnect(from, fromPort, to, toPort);
                if (result != ConnectResult.Ok)
                    warnings.Add($"Dropped connection {from}.{fromPort} -> {to}.{toPort}: {result}.");
            }
        }

        // Loading a profile never starts force output (as in the Forza app).
        graph.Enabled = keepEnabled && root.Bool("enabled", false);
        return new FfbGraphLoadResult(graph, warnings);
    }
}
