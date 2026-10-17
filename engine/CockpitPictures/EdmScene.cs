#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.CockpitPictures
{
    // Puts an EDM cockpit model into a CockpitScene in its resting pose: every argument at its value (0 unless given),
    // the most detailed LOD, what the visibility arguments hide left out, the pilot's body (skinned meshes), glass,
    // light halos and other see-through layers left out.
    internal static class EdmScene
    {
        public sealed class Options
        {
            // Argument values (default 0).
            public Dictionary<int, double> Args = new Dictionary<int, double>();
            // Loads a texture by the name the material gives (no extension); null when there is none.
            public Func<string, TextureImage> Texture;
            // Material names (lower case parts) that are never drawn, besides glass and light layers.
            public string[] SkipMaterials = { "pilot", "shadow", "glass", "halo", "visor", "fur_test", "_hmd", "helmet", "hmd_", "nvg", "mirror_refl" };
        }

        public static CockpitScene Build(EdmFile edm, Options o)
        {
            o = o ?? new Options();
            CockpitScene scene = new CockpitScene();
            int n = edm.Nodes.Count;
            double[][] world = new double[n][];
            bool[] hidden = new bool[n];
            bool[] done = new bool[n];
            // LOD: only the first child (the nearest level) of every LodNode stays.
            Dictionary<int, int> firstLodChild = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int p = edm.Nodes[i].Parent;
                if (p >= 0 && p < n && edm.Nodes[p].Kind == EdmNodeKind.Lod && !firstLodChild.ContainsKey(p))
                    firstLodChild[p] = i;
            }
            for (int i = 0; i < n; i++)
                Resolve(edm, i, o, world, hidden, done, firstLodChild, 0);

            // Materials.
            Dictionary<string, TextureImage> textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
            int[] materialMap = new int[edm.Materials.Count];
            for (int m = 0; m < edm.Materials.Count; m++)
            {
                EdmMaterial em = edm.Materials[m];
                materialMap[m] = -1;
                if (!Drawn(em, o))
                    continue;
                SceneMaterial sm = new SceneMaterial
                {
                    Name = em.Name,
                    AlphaTest = em.Blending == 1 || em.Blending == 2 || em.HasAlpha,
                    SelfIllumination = em.Shader.IndexOf("self_illum", StringComparison.OrdinalIgnoreCase) >= 0
                };
                string tex = em.Texture(0);
                if (tex.Length > 0 && o.Texture != null)
                {
                    TextureImage img;
                    if (!textures.TryGetValue(tex, out img))
                    {
                        try
                        {
                            img = o.Texture(tex);
                        }
                        catch (Exception)
                        {
                            img = null;
                        }
                        textures[tex] = img;
                        if (img == null)
                            scene.Warnings.Add("texture not found: " + tex);
                    }
                    sm.Albedo = img;
                    if (img != null)
                        sm.Color = new[] { img.Mean[0], img.Mean[1], img.Mean[2] };
                }
                materialMap[m] = scene.Materials.Count;
                scene.Materials.Add(sm);
            }

            // Meshes: one per render node and parent.
            int part = 0;
            foreach (EdmRenderNode rn in edm.RenderNodes)
            {
                part++;
                if (rn.Skin || rn.Material < 0 || rn.Material >= edm.Materials.Count || materialMap[rn.Material] < 0)
                    continue;
                EdmMaterial em = edm.Materials[rn.Material];
                int stride = rn.Stride;
                if (stride <= 0 || rn.Vertices == null || rn.Indices == null || rn.Indices.Length < 3)
                    continue;
                int posOff = em.Offset(0), norOff = em.Offset(1), uvOff = em.Offset(4);
                if (posOff < 0)
                    posOff = 0;
                int vcount = rn.Vertices.Length / stride;
                // Which parent each vertex belongs to: the fourth position value (several parents) or the only one.
                int parents = rn.Parents.Count;
                List<int>[] trisByParent = new List<int>[Math.Max(1, parents)];
                for (int t = 0; t + 2 < rn.Indices.Length; t += 3)
                {
                    int a = rn.Indices[t];
                    if (a < 0 || a >= vcount)
                        continue;
                    int slot = 0;
                    if (parents > 1 && posOff + 3 < stride && em.VertexFormat.Length > 0 && em.VertexFormat[0] >= 4)
                    {
                        slot = (int)Math.Round(rn.Vertices[a * stride + posOff + 3]);
                        if (slot < 0 || slot >= parents)
                            slot = 0;
                    }
                    if (trisByParent[slot] == null)
                        trisByParent[slot] = new List<int>();
                    trisByParent[slot].Add(t);
                }
                for (int s = 0; s < trisByParent.Length; s++)
                {
                    List<int> tris = trisByParent[s];
                    if (tris == null || tris.Count == 0)
                        continue;
                    int node = parents == 0 ? -1 : rn.Parents[s];
                    if (node >= 0 && node < n && hidden[node])
                        continue;
                    double[] m = node >= 0 && node < n ? world[node] : Mat4.Identity();
                    double[] nm = Mat4.NormalMatrix(m);
                    bool flip = Mat4.Determinant3(m) < 0;
                    Dictionary<int, int> remap = new Dictionary<int, int>();
                    List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
                    List<int> idx = new List<int>(tris.Count * 3);
                    foreach (int t in tris)
                    {
                        int[] corner = { rn.Indices[t], rn.Indices[t + 1], rn.Indices[t + 2] };
                        if (corner[1] < 0 || corner[1] >= vcount || corner[2] < 0 || corner[2] >= vcount)
                            continue;
                        if (flip)
                        {
                            int tmp = corner[1];
                            corner[1] = corner[2];
                            corner[2] = tmp;
                        }
                        foreach (int v in corner)
                        {
                            int k;
                            if (!remap.TryGetValue(v, out k))
                            {
                                k = remap.Count;
                                remap[v] = k;
                                int b = v * stride;
                                Vec3 wp = Mat4.Point(m, rn.Vertices[b + posOff], rn.Vertices[b + posOff + 1], rn.Vertices[b + posOff + 2]);
                                pos.Add((float)wp.X);
                                pos.Add((float)wp.Y);
                                pos.Add((float)wp.Z);
                                if (norOff >= 0 && norOff + 2 < stride)
                                {
                                    Vec3 wn = Mat4.Direction(nm, rn.Vertices[b + norOff], rn.Vertices[b + norOff + 1], rn.Vertices[b + norOff + 2]).Normalized;
                                    nor.Add((float)wn.X);
                                    nor.Add((float)wn.Y);
                                    nor.Add((float)wn.Z);
                                }
                                if (uvOff >= 0 && uvOff + 1 < stride)
                                {
                                    uv.Add(rn.Vertices[b + uvOff]);
                                    uv.Add(rn.Vertices[b + uvOff + 1]);
                                }
                            }
                            idx.Add(k);
                        }
                    }
                    if (idx.Count < 3)
                        continue;
                    scene.Meshes.Add(new SceneMesh
                    {
                        Name = rn.Name,
                        Node = node,
                        Material = materialMap[rn.Material],
                        Part = part * 64 + s,
                        Positions = pos.ToArray(),
                        Normals = nor.Count == pos.Count ? nor.ToArray() : null,
                        Uvs = uv.Count * 3 == pos.Count * 2 ? uv.ToArray() : null,
                        Indices = idx.ToArray()
                    });
                }
            }

            // Points: the connectors (clickable controls, grip pivots) and the named transform nodes.
            foreach (EdmConnector c in edm.Connectors)
            {
                if (c.Parent < 0 || c.Parent >= n || string.IsNullOrWhiteSpace(c.Name))
                    continue;
                Vec3 at = Mat4.Point(world[c.Parent], 0, 0, 0);
                scene.Points[c.Name] = new ScenePoint { Name = c.Name, Position = at, Node = c.Parent };
            }
            // Groups: every mesh under a node, by the node's name (the grips are found from them).
            Dictionary<int, List<int>> meshesOfNode = new Dictionary<int, List<int>>();
            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                int node = scene.Meshes[i].Node;
                for (int guard = 0; node >= 0 && node < n && guard < 256; guard++)
                {
                    List<int> list;
                    if (!meshesOfNode.TryGetValue(node, out list))
                        meshesOfNode[node] = list = new List<int>();
                    list.Add(i);
                    node = edm.Nodes[node].Parent;
                }
            }
            scene.NodeMeshes = meshesOfNode;
            scene.NodeNames = edm.Nodes.Select(x => x.Name).ToList();
            scene.NodeParents = edm.Nodes.Select(x => x.Parent).ToList();
            return scene;
        }

        private static bool Drawn(EdmMaterial m, Options o)
        {
            string name = (m.Name ?? string.Empty).ToLowerInvariant();
            string shader = (m.Shader ?? string.Empty).ToLowerInvariant();
            if (m.Blending == 3 || m.Blending >= 4)
                return false; // additive light layers, glass
            if (shader.Contains("glass") || shader.Contains("additive") || shader.Contains("fake_") || shader.Contains("bano") ||
                shader.Contains("mirror") || shader.Contains("transparent") || shader.Contains("lines_material") || shader.Contains("color_material") && m.Textures.Count == 0)
                return false;
            foreach (string skip in o.SkipMaterials)
            {
                if (name.Contains(skip))
                    return false;
            }
            return true;
        }

        private static void Resolve(EdmFile edm, int i, Options o, double[][] world, bool[] hidden, bool[] done, Dictionary<int, int> firstLodChild, int depth)
        {
            if (done[i])
                return;
            EdmNode node = edm.Nodes[i];
            int p = node.Parent;
            double[] parent = Mat4.Identity();
            bool parentHidden = false;
            if (p >= 0 && p < edm.Nodes.Count && p != i && depth < 512)
            {
                Resolve(edm, p, o, world, hidden, done, firstLodChild, depth + 1);
                parent = world[p];
                parentHidden = hidden[p];
                int first;
                if (edm.Nodes[p].Kind == EdmNodeKind.Lod && firstLodChild.TryGetValue(p, out first) && first != i)
                    parentHidden = true;
            }
            world[i] = Mat4.Multiply(parent, Local(node, o));
            hidden[i] = parentHidden || !Visible(node, o);
            done[i] = true;
        }

        private static double Arg(Options o, int arg)
        {
            double v;
            return o.Args.TryGetValue(arg, out v) ? v : 0;
        }

        private static bool Visible(EdmNode node, Options o)
        {
            if (node.Kind != EdmNodeKind.Visibility)
                return true;
            foreach (EdmVisibility v in node.Visibility)
            {
                double a = Arg(o, v.Arg);
                bool any = false;
                foreach (double[] r in v.Ranges)
                {
                    if (a >= r[0] && a <= r[1])
                        any = true;
                }
                if (!any)
                    return false;
            }
            return true;
        }

        private static double[] Local(EdmNode node, Options o)
        {
            if (node.Kind == EdmNodeKind.Transform && node.Matrix != null)
                return node.Matrix;
            if (node.Kind != EdmNodeKind.Animation)
                return Mat4.Identity();
            double[] pos = (double[])node.Position.Clone();
            foreach (EdmKeys k in node.PositionKeys)
            {
                double[] v = Key(k, Arg(o, k.Arg), false);
                if (v != null)
                {
                    pos[0] += v[0];
                    pos[1] += v[1];
                    pos[2] += v[2];
                }
            }
            double[] m = Mat4.Multiply(node.Matrix, Mat4.Translation(pos[0], pos[1], pos[2]));
            m = Mat4.Multiply(m, Mat4.Rotation(node.Quat1));
            foreach (EdmKeys k in node.RotationKeys)
            {
                double[] q = Key(k, Arg(o, k.Arg), true);
                if (q != null)
                    m = Mat4.Multiply(m, Mat4.Rotation(q));
            }
            m = Mat4.Multiply(m, Mat4.Scale(node.Scale[0], node.Scale[1], node.Scale[2]));
            return m;
        }

        // A key track's value at an argument value (linear between keys, quaternions normalised).
        private static double[] Key(EdmKeys k, double a, bool quat)
        {
            int c = k.Frames.Count;
            if (c == 0)
                return null;
            if (a <= k.Frames[0])
                return k.Values[0];
            if (a >= k.Frames[c - 1])
                return k.Values[c - 1];
            for (int i = 0; i + 1 < c; i++)
            {
                double f0 = k.Frames[i], f1 = k.Frames[i + 1];
                if (a < f0 || a > f1)
                    continue;
                double t = f1 - f0 < 1e-12 ? 0 : (a - f0) / (f1 - f0);
                double[] v0 = k.Values[i], v1 = k.Values[i + 1];
                double[] r = new double[v0.Length];
                double sign = 1;
                if (quat)
                {
                    double dot = 0;
                    for (int j = 0; j < 4; j++)
                        dot += v0[j] * v1[j];
                    if (dot < 0)
                        sign = -1;
                }
                for (int j = 0; j < r.Length; j++)
                    r[j] = v0[j] * (1 - t) + sign * v1[j] * t;
                return r;
            }
            return k.Values[c - 1];
        }
    }
}
