"""3D views of devices from a 3D model file (.glb / .gltf / .stl / .obj) instead of a game's cockpit model: the same
turntable as the engine's mesh-render (docs/device-reference.md, "3D view"): frames around the vertical at a few
pitches, cel-shaded (flat colours in two tones, light outlines between the model's parts), transparent background, and
where the device's parts land on every frame and whether they face the camera.

A recipe (tools/device_drawings/mesh/<model>.json):
    { "model": "<catalog id>", "mesh": { "file": "<name>.glb", "url": "<where it was downloaded>",
      "select": { "x": [min, max], "y": [min, max], "z": [min, max], "skip": [mesh index, ...] },   meshes whose centre lies inside
      "yaw0": 0 },                                                             yaw of the device's front view
      "source": "<credit>", "licence": "<licence of the 3D model>",
      "keepTop": 1, "anchorViews": [ { "view": [yaw, pitch], "size": 600, "points": { "<part>": [x, y] } } ] }
The model file is looked up in <3D work folder>/.models/ (never in the repository). Anchor points are pixels of the
anchor view as `python tools/device_drawings.py turntable <recipe> --views <dir>` draws it (with a pixel grid); each is
placed in 3D on the surface under it.

    python tools/device_drawings.py turntable tools/device_drawings/mesh/<model>.json [--views <dir>] [--out <dir>]
"""
import json
import math
import os

import numpy as np


def _load(path, select):
    import trimesh
    scene = trimesh.load(path, force="scene")
    meshes = []
    for i, (name, geom) in enumerate(_dump(scene)):
        meshes.append((i, name, geom))
    keep = []
    skip = set(select.get("skip", []))
    for i, name, g in meshes:
        if i in skip or "shadow" in name.lower() or len(g.faces) < 4:
            continue
        c = g.bounds.mean(axis=0)
        ok = True
        for k, axis in (("x", 0), ("y", 1), ("z", 2)):
            if k in select and not (select[k][0] <= c[axis] <= select[k][1]):
                ok = False
        if ok:
            keep.append((i, g))
    return keep


def _dump(scene):
    out = []
    for node in scene.graph.nodes_geometry:
        transform, gname = scene.graph[node]
        g = scene.geometry[gname].copy()
        g.apply_transform(transform)
        out.append((gname, g))
    return out


def _colours(g, idx, bary):
    """Surface colour at sampled points (texture or material colour), RGB 0..255."""
    vis = g.visual
    try:
        image = getattr(vis.material, "baseColorTexture", None) if vis.kind == "texture" else None
        if image is None and vis.kind == "texture":
            image = getattr(vis.material, "image", None)
        if vis.kind == "texture" and getattr(vis, "uv", None) is not None and image is not None:
            uv = (vis.uv[g.faces[idx]] * bary[:, :, None]).sum(axis=1)
            img = np.asarray(image.convert("RGB"))
            h, w = img.shape[:2]
            x = np.clip((uv[:, 0] % 1.0) * (w - 1), 0, w - 1).astype(int)
            y = np.clip((1 - uv[:, 1] % 1.0) * (h - 1), 0, h - 1).astype(int)
            col = img[y, x].astype(float)
            f = getattr(vis.material, "baseColorFactor", None)
            if f is not None:
                col = col * (np.asarray(f[:3], float) / (255.0 if max(f[:3]) > 1 else 1.0))
            return col
        if vis.kind == "texture":
            m = vis.material
            f = getattr(m, "baseColorFactor", None)
            if f is None:
                f = getattr(m, "main_color", None)
            if f is not None:
                f = np.asarray(f[:3], float)
                if f.max() <= 1.0:
                    f = f * 255
                return np.tile(f, (len(idx), 1))
        if vis.kind == "vertex":
            vc = vis.vertex_colors[:, :3].astype(float)
            return (vc[g.faces[idx]] * bary[:, :, None]).sum(axis=1)
        if vis.kind == "face":
            return vis.face_colors[idx, :3].astype(float)
    except Exception:
        pass
    return np.full((len(idx), 3), 90.0)


def _sample(meshes, pixel, budget=6_000_000):
    areas = np.array([g.area for _, g in meshes])
    want = areas / (pixel * pixel) * 6.0
    scale = min(1.0, budget / max(1.0, want.sum()))
    P, N, C, K = [], [], [], []
    rng = np.random.default_rng(7)
    for (i, g), n in zip(meshes, want * scale):
        n = int(max(200, n))
        fa = g.area_faces
        idx = rng.choice(len(fa), size=n, p=fa / fa.sum())
        r = rng.random((n, 2))
        flip = r.sum(axis=1) > 1
        r[flip] = 1 - r[flip]
        bary = np.column_stack([1 - r[:, 0] - r[:, 1], r[:, 0], r[:, 1]])
        tri = g.triangles[idx]
        P.append((tri * bary[:, :, None]).sum(axis=1))
        N.append(g.face_normals[idx])
        C.append(_colours(g, idx, bary))
        K.append(np.full(n, i))
    return np.vstack(P), np.vstack(N), np.vstack(C), np.concatenate(K)


def _camera(yaw, pitch):
    y, p = math.radians(yaw), math.radians(pitch)
    f = np.array([math.cos(p) * math.cos(y), -math.sin(p), math.cos(p) * math.sin(y)])
    up = np.array([0.0, 1.0, 0.0])
    r = np.cross(f, up)
    r /= np.linalg.norm(r)
    u = np.cross(r, f)
    return f, r, u


class Buffers:
    pass


def _render(P, N, C, K, centre, radius, yaw, pitch, size):
    f, r, u = _camera(yaw, pitch)
    d = P - centre
    sx = (d @ r / radius * 0.5 + 0.5) * size
    sy = (0.5 - d @ u / radius * 0.5) * size
    dep = d @ f
    ix, iy = sx.astype(np.int64), sy.astype(np.int64)
    sel = np.arange(len(P))
    idx_all, src_all = [], []
    for ox, oy in ((0, 0), (1, 0), (0, 1), (1, 1)):
        x, y = ix + ox, iy + oy
        ok = (x >= 0) & (y >= 0) & (x < size) & (y < size)
        idx_all.append((y * size + x)[ok])
        src_all.append(sel[ok])
    idx, src = np.concatenate(idx_all), np.concatenate(src_all)
    order = np.argsort(-dep[src], kind="stable")
    idx, src = idx[order], src[order]
    owner = np.full(size * size, -1, np.int64)
    owner[idx] = src
    b = Buffers()
    b.size, b.f, b.r, b.u = size, f, r, u
    b.covered = owner >= 0
    # Hole filling: an uncovered pixel with most neighbours covered takes the nearest neighbour's sample.
    o2 = owner.reshape(size, size)
    for _ in range(3):
        o2 = _fill(o2, dep)
    owner = o2.reshape(-1)
    b.covered = owner >= 0
    o = np.maximum(owner, 0)
    b.depth = np.where(b.covered, dep[o], np.inf)
    b.part = np.where(b.covered, K[o], -1)
    b.normal = N[o]
    b.albedo = C[o]
    return b


def _fill(o2, dep):
    size = o2.shape[0]
    cov = o2 >= 0
    count = np.zeros((size, size), int)
    best = np.full((size, size), -1, np.int64)
    bestd = np.full((size, size), np.inf)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dx == 0 and dy == 0:
                continue
            sh = np.roll(np.roll(o2, dy, 0), dx, 1)
            c = sh >= 0
            count += c
            dd = np.where(c, dep[np.maximum(sh, 0)], np.inf)
            better = dd < bestd
            best = np.where(better, sh, best)
            bestd = np.where(better, dd, bestd)
    fill = (~cov) & (count >= 5)
    return np.where(fill, best, o2)


def _cel(b, ss, size, radius):
    w = b.size
    cov = b.covered.reshape(w, w)
    part = b.part.reshape(w, w)
    depth = b.depth.reshape(w, w)
    edge = np.zeros((w, w), bool)
    for k in range(1, ss + 1):
        for dy, dx in ((k, 0), (-k, 0), (0, k), (0, -k)):
            sc = np.roll(np.roll(cov, dy, 0), dx, 1)
            sp = np.roll(np.roll(part, dy, 0), dx, 1)
            sd = np.roll(np.roll(depth, dy, 0), dx, 1)
            edge |= cov & ((~sc) | (sp != part) | (np.abs(np.where(sc, sd, 0) - np.where(cov, depth, 0)) > radius * 0.04))
    light = np.array([0.3, 1.0, -0.2])
    light /= np.linalg.norm(light)
    n = b.normal.copy()
    flip = (n @ b.f) > 0
    n[flip] *= -1
    lit = np.maximum(0, n @ light) * 0.7 + np.maximum(0, -(n @ b.f)) * 0.3
    tone = np.where(lit >= 0.42, 1.0, 0.72)
    alb = np.maximum(34, (b.albedo.astype(int) // 40) * 40 + 20)
    rgb = np.minimum(255, alb * tone[:, None]).reshape(w, w, 3)
    rgb[edge] = 236
    a = cov.astype(float)
    rgb = rgb * a[:, :, None]
    rgb = rgb.reshape(size, ss, size, ss, 3).sum(axis=(1, 3))
    a = a.reshape(size, ss, size, ss).sum(axis=(1, 3))
    out = np.zeros((size, size, 4), np.uint8)
    nz = a > 0
    out[nz, :3] = (rgb[nz] / a[nz, None]).astype(np.uint8)
    out[:, :, 3] = np.round(a / (ss * ss) * 255).astype(np.uint8)
    return out


def _frame_of(meshes, keep_top):
    lo = np.min([g.bounds[0] for _, g in meshes], axis=0)
    hi = np.max([g.bounds[1] for _, g in meshes], axis=0)
    bottom = hi[1] - (hi[1] - lo[1]) * max(0.05, min(1.0, keep_top))
    l2, h2 = lo.copy(), hi.copy()
    l2[1] = bottom
    return (l2 + h2) / 2, np.linalg.norm(h2 - l2) / 2 * 1.02


def run(recipe_path, models_dir, out_dir, steps=24, pitches=(5, 25, 45), size=420, views_dir=None):
    from PIL import Image, ImageDraw
    with open(recipe_path, encoding="utf-8") as handle:
        rc = json.load(handle)
    model = rc["model"]
    mesh_file = os.path.join(models_dir, rc["mesh"]["file"])
    if not os.path.isfile(mesh_file):
        raise SystemExit("%s: 3D model not found: %s (download it from %s)" % (model, mesh_file, rc["mesh"].get("url", "?")))
    meshes = _load(mesh_file, rc["mesh"].get("select", {}))
    if not meshes:
        raise SystemExit(model + ": no mesh selected")
    yaw0 = float(rc["mesh"].get("yaw0", 0))
    centre, radius = _frame_of(meshes, float(rc.get("keepTop", 1)))
    ss = 2
    P, N, C, K = _sample(meshes, 2 * radius / (size * ss))
    if views_dir:
        # Pick sheets: the anchor views (and four default ones) with a pixel grid, for placing the parts by eye.
        os.makedirs(views_dir, exist_ok=True)
        vs = [(v["view"][0], v["view"][1], int(v.get("size", 600))) for v in rc.get("anchorViews", [])] or [(0, 20, 600), (90, 20, 600), (180, 20, 600), (270, 20, 600), (0, 70, 600)]
        dense = {}
        for (vy, vp, vsize) in vs:
            if vsize not in dense:
                dense[vsize] = _sample(meshes, 2 * radius / vsize, budget=12_000_000)
            b = _render(*dense[vsize], centre, radius, yaw0 + vy, vp, vsize)
            im = Image.fromarray(_cel(b, 1, vsize, radius), "RGBA")
            bg = Image.new("RGBA", im.size, (40, 44, 52, 255))
            bg.alpha_composite(im)
            d = ImageDraw.Draw(bg)
            step = 50 if vsize <= 600 else 20
            for t in range(0, vsize, step):
                major = t % (step * 5) == 0
                d.line([(t, 0), (t, vsize)], fill=(110, 160, 220, 255) if major else (60, 90, 130, 255))
                d.line([(0, t), (vsize, t)], fill=(110, 160, 220, 255) if major else (60, 90, 130, 255))
                if major or vsize <= 600:
                    d.text((t + 2, 2), str(t), fill=(255, 220, 120, 255))
                    d.text((2, t + 2), str(t), fill=(255, 220, 120, 255))
            for v in rc.get("anchorViews", []):
                if tuple(v["view"]) == (vy, vp):
                    for k, (x, y) in v["points"].items():
                        d.ellipse([x - 4, y - 4, x + 4, y + 4], fill=(255, 60, 60, 255))
                        d.text((x + 6, y - 6), k.split(".", 1)[-1], fill=(255, 255, 255, 255))
            name = os.path.join(views_dir, "%s_y%d_p%d.png" % (model, vy, vp))
            bg.convert("RGB").save(name)
            print("  view " + name)
        return
    anchors = {}
    for v in rc.get("anchorViews", []):
        vy, vp = v["view"]
        vsize = int(v.get("size", 600))
        b = _render(P, N, C, K, centre, radius, yaw0 + vy, vp, vsize * ss)
        cov = b.covered.reshape(vsize * ss, vsize * ss)
        dep = b.depth.reshape(vsize * ss, vsize * ss)
        for key, (px, py) in v["points"].items():
            qx, qy = int(round(px * ss)), int(round(py * ss))
            best = None
            for rr in range(0, 21):
                ys, xs = np.mgrid[max(0, qy - rr):qy + rr + 1, max(0, qx - rr):qx + rr + 1]
                ys, xs = ys.ravel(), xs.ravel()
                ok = (ys < cov.shape[0]) & (xs < cov.shape[1])
                ys, xs = ys[ok], xs[ok]
                hit = cov[ys, xs]
                if hit.any():
                    j = np.argmin(np.where(hit, (ys - qy) ** 2 + (xs - qx) ** 2, 1e9))
                    best = (xs[j], ys[j])
                    break
            if best is None:
                print("  %s: no surface under %s in view %s" % (model, key, v["view"]))
                continue
            s = vsize * ss
            vx = (best[0] / s - 0.5) * 2 * radius
            vyy = (0.5 - best[1] / s) * 2 * radius
            anchors[key] = centre + b.r * vx + b.u * vyy + b.f * dep[best[1], best[0]]
    os.makedirs(os.path.join(out_dir, "frames"), exist_ok=True)
    frames = []
    for pi, pitch in enumerate(pitches):
        for yi in range(steps):
            yaw = yi * 360.0 / steps
            b = _render(P, N, C, K, centre, radius, yaw0 + yaw, pitch, size * ss)
            name = "p%d_y%02d.png" % (pi, yi)
            Image.fromarray(_cel(b, ss, size, radius), "RGBA").save(os.path.join(out_dir, "frames", name), optimize=True)
            points = {}
            S = size * ss
            cov = b.covered.reshape(S, S)
            dep = b.depth.reshape(S, S)
            for key, a in anchors.items():
                d = a - centre
                sx = (d @ b.r / radius * 0.5 + 0.5) * S
                sy = (0.5 - d @ b.u / radius * 0.5) * S
                dd = d @ b.f
                x0, y0 = int(round(sx)), int(round(sy))
                win = (slice(max(0, y0 - 5), max(0, y0 + 6)), slice(max(0, x0 - 5), max(0, x0 + 6)))
                seen = bool(np.any(cov[win] & (dep[win] >= dd - radius * 0.03)))
                points[key] = [round(sx / S, 4), round(sy / S, 4), seen]
            frames.append({"pitch": pitch, "yaw": yaw, "file": "frames/" + name, "points": points})
    tt = {"format": "joypro-mesh-turntable", "aircraft": "", "grip": "", "size": size, "yawSteps": steps,
          "pitches": list(pitches), "anchors": sorted(anchors), "frames": frames, "model": model,
          "source": rc.get("source", ""), "licence": rc.get("licence", "")}
    with open(os.path.join(out_dir, "turntable.json"), "w", encoding="utf-8") as handle:
        json.dump(tt, handle, indent=1)
    print("%s: %d frames, %d parts in 3D" % (model, len(frames), len(anchors)))
