#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ifc_to_analytical.py
====================

Extract the load-bearing (structural) elements of an IFC model and export a
simplified **analytical model** to DXF (and, optionally, DWG):

    * Columns  -> centre-line  (LINE)
    * Beams    -> centre-line  (LINE)
    * Slabs    -> mid-surface outline  (closed 3D POLYLINE)
    * Walls    -> mid-surface outline  (closed 3D POLYLINE)   [optional]

The result is a "stick / plate" model of axes and mid-planes that can be
imported into a structural analysis package (SCAD, Lira, Robot, SAP2000,
Revit analytical, ...) as the starting point for a calculation model.

How it works
------------
Instead of triangulating the solids and drawing their surfaces, the tool
reads the *parametric* representation of every element and reduces it to an
analytical primitive:

    IfcExtrudedAreaSolid  ->  profile + axis-placement + direction + depth
        linear member  ->  a line through the profile centroid, from the
                           base of the extrusion to base + depth
        planar member  ->  the profile outline, shifted to mid-thickness

Mapped representations (IfcMappedItem) and CSG / boolean clipping results
(IfcBooleanClippingResult) are unwrapped recursively.  Elements whose body
is a boundary representation (IfcFacetedBrep) or is otherwise not a clean
extrusion fall back to a mesh-based reduction (oriented bounding box via
principal component analysis).

Because Revit exports the model in real-world survey coordinates (which can
be hundreds of kilometres from the origin) all geometry is recentred on the
model bounding-box minimum by default; the applied offset is written into
the DXF as a text note and printed to the console.

Usage
-----
    python ifc_to_analytical.py model.ifc -o analytical.dxf
    python ifc_to_analytical.py model.ifc --elements columns,beams,slabs,walls
    python ifc_to_analytical.py model.ifc --unit m --no-recenter
    python ifc_to_analytical.py model.ifc -o out.dxf --dwg   # needs ODAFileConverter

Dependencies
------------
    pip install ifcopenshell ezdxf numpy
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from typing import List, Optional, Sequence, Tuple

import numpy as np

try:
    import ifcopenshell
    import ifcopenshell.geom
    import ifcopenshell.util.placement as placement
except Exception as exc:  # pragma: no cover
    sys.exit("ifcopenshell is required:  pip install ifcopenshell\n(%s)" % exc)

try:
    import ezdxf
except Exception as exc:  # pragma: no cover
    sys.exit("ezdxf is required:  pip install ezdxf\n(%s)" % exc)


# --------------------------------------------------------------------------- #
#  Configuration                                                              #
# --------------------------------------------------------------------------- #

# IFC classes grouped by the analytical shape they collapse to.
LINEAR_CLASSES = ("IfcColumn", "IfcBeam", "IfcMember")
WALL_CLASSES = ("IfcWall", "IfcWallStandardCase")
# Planar members whose body is extruded along its (thin) thickness -> the
# profile outline shifted to mid-thickness is the analytical surface.
PLATE_CLASSES = ("IfcSlab", "IfcPlate", "IfcFooting")

# Mapping of a user keyword -> the IFC classes it selects.
ELEMENT_GROUPS = {
    "columns": ("IfcColumn",),
    "beams": ("IfcBeam", "IfcMember"),
    "slabs": ("IfcSlab", "IfcPlate", "IfcFooting"),
    "walls": ("IfcWall", "IfcWallStandardCase"),
}

# One DXF layer + AutoCAD colour index (ACI) per category.
LAYERS = {
    "columns": ("S_COLUMNS", 1),   # red
    "beams":   ("S_BEAMS",   3),   # green
    "slabs":   ("S_SLABS",   5),   # blue
    "walls":   ("S_WALLS",   6),   # magenta
}

# Number of segments used to approximate a circular profile.
CIRCLE_SEGMENTS = 24


# --------------------------------------------------------------------------- #
#  Small linear-algebra helpers                                               #
# --------------------------------------------------------------------------- #

def axis2_matrix(placement_ent) -> np.ndarray:
    """4x4 matrix of an IfcAxis2Placement2D/3D (identity when absent)."""
    if placement_ent is None:
        return np.eye(4)
    return np.array(placement.get_axis2placement(placement_ent), dtype=float)


def apply(mat: np.ndarray, x: float, y: float, z: float = 0.0) -> np.ndarray:
    """Transform a point by a 4x4 matrix, returning the xyz part."""
    return (mat @ np.array([x, y, z, 1.0]))[:3]


# --------------------------------------------------------------------------- #
#  Analytical result container                                                #
# --------------------------------------------------------------------------- #

@dataclass
class AnalyticalElement:
    category: str                    # columns / beams / slabs / walls
    ifc_type: str
    guid: str
    name: str
    kind: str                        # "line" or "outline"
    points: List[np.ndarray]         # line: 2 pts; outline: closed loop
    thickness: float = 0.0           # section depth (line) or plate thickness
    source: str = "extrusion"        # extrusion | mesh (fallback)


# --------------------------------------------------------------------------- #
#  Representation resolver: unwrap to IfcExtrudedAreaSolid                     #
# --------------------------------------------------------------------------- #

@dataclass
class Extrusion:
    profile: object
    matrix: np.ndarray               # world matrix of the extrusion position
    direction: np.ndarray            # extrusion direction (local, 3)
    depth: float


def find_extrusions(item, mat: np.ndarray) -> List[Extrusion]:
    """
    Recursively walk a representation item, accumulating the transform `mat`,
    and return every IfcExtrudedAreaSolid found underneath it.
    """
    t = item.is_a()

    if t == "IfcExtrudedAreaSolid":
        m = mat @ axis2_matrix(item.Position)
        d = np.array(item.ExtrudedDirection.DirectionRatios, dtype=float)
        if d.shape[0] == 2:
            d = np.array([d[0], d[1], 0.0])
        return [Extrusion(item.SweptArea, m, d, float(item.Depth))]

    if t in ("IfcBooleanClippingResult", "IfcBooleanResult"):
        # Analytical model ignores the cut: keep the base solid only.
        return find_extrusions(item.FirstOperand, mat)

    if t == "IfcMappedItem":
        src = item.MappingSource
        m_map = np.array(placement.get_mappeditem_transformation(item), dtype=float)
        out: List[Extrusion] = []
        for it in src.MappedRepresentation.Items:
            out += find_extrusions(it, mat @ m_map)
        return out

    return []


def axis_polyline_world(element) -> Optional[List[np.ndarray]]:
    """
    World-space points of the element's 'Axis' representation (the plan
    centre-line that Revit exports for walls and beams).
    """
    rep = element.Representation
    if not rep:
        return None
    world = np.array(placement.get_local_placement(element.ObjectPlacement), dtype=float)
    axis = None
    for r in rep.Representations:
        if r.RepresentationIdentifier == "Axis":
            axis = r
            break
    if axis is None:
        return None
    for it in axis.Items:
        pts2d = curve_points(it) if it.is_a("IfcBoundedCurve") else None
        if pts2d:
            return [apply(world, x, y) for x, y in pts2d]
    return None


def body_extrusions(element) -> List[Extrusion]:
    """Extrusions of the element's 'Body' representation, in world space."""
    rep = element.Representation
    if not rep:
        return []
    world = np.array(placement.get_local_placement(element.ObjectPlacement), dtype=float)
    body = None
    for r in rep.Representations:
        if r.RepresentationIdentifier == "Body":
            body = r
            break
    if body is None:
        return []
    out: List[Extrusion] = []
    for it in body.Items:
        out += find_extrusions(it, world)
    return out


# --------------------------------------------------------------------------- #
#  Profile -> 2D outline in the profile plane                                  #
# --------------------------------------------------------------------------- #

def profile_outline(profile) -> Optional[List[Tuple[float, float]]]:
    """Return the outer outline of a profile as ordered (x, y) points."""
    t = profile.is_a()
    pos = axis2_matrix(profile.Position) if getattr(profile, "Position", None) else np.eye(4)

    def placed(pts):
        return [tuple(apply(pos, x, y)[:2]) for x, y in pts]

    if t == "IfcRectangleProfileDef":
        hx, hy = profile.XDim / 2.0, profile.YDim / 2.0
        return placed([(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)])

    if t == "IfcCircleProfileDef":
        r = profile.Radius
        ang = np.linspace(0.0, 2.0 * np.pi, CIRCLE_SEGMENTS, endpoint=False)
        return placed([(r * np.cos(a), r * np.sin(a)) for a in ang])

    if t in ("IfcArbitraryClosedProfileDef", "IfcArbitraryProfileDefWithVoids"):
        return curve_points(profile.OuterCurve)

    # I-shape, L-shape, T-shape, U-shape ... -> use the bounding rectangle,
    # which is an adequate footprint for an analytical stick model.
    bb = getattr(profile, "OverallWidth", None), getattr(profile, "OverallDepth", None)
    if all(v is not None for v in bb):
        hx, hy = bb[0] / 2.0, bb[1] / 2.0
        return placed([(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)])
    return None


def curve_points(curve) -> Optional[List[Tuple[float, float]]]:
    """Extract 2D points from a bounded curve (polyline / composite)."""
    if curve is None:
        return None
    if curve.is_a("IfcPolyline"):
        return [(p.Coordinates[0], p.Coordinates[1]) for p in curve.Points]
    if curve.is_a("IfcCompositeCurve"):
        pts: List[Tuple[float, float]] = []
        for seg in curve.Segments:
            sub = curve_points(seg.ParentCurve)
            if sub:
                pts.extend(sub)
        return pts or None
    return None


# --------------------------------------------------------------------------- #
#  Extrusion -> analytical primitive                                          #
# --------------------------------------------------------------------------- #

def extrusion_centreline(ext: Extrusion) -> Optional[Tuple[np.ndarray, np.ndarray, float]]:
    """Centre-line (start, end) of a linear extrusion + its section depth."""
    outline = profile_outline(ext.profile)
    if not outline:
        return None
    local = np.array([apply(ext.matrix, x, y) for x, y in outline])
    centroid = local.mean(axis=0)
    dir_world = ext.matrix[:3, :3] @ ext.direction
    n = np.linalg.norm(dir_world)
    if n == 0:
        return None
    dir_world = dir_world / n
    start = centroid
    end = centroid + dir_world * ext.depth
    # Approximate section depth from the profile bounding box (for reference).
    depth = float(np.ptp(local, axis=0).max())
    return start, end, depth


def extrusion_midsurface(ext: Extrusion) -> Optional[Tuple[List[np.ndarray], float]]:
    """Mid-thickness outline of a planar extrusion + the plate thickness."""
    outline = profile_outline(ext.profile)
    if not outline:
        return None
    dir_world = ext.matrix[:3, :3] @ ext.direction
    n = np.linalg.norm(dir_world)
    if n == 0:
        return None
    dir_world = dir_world / n
    shift = dir_world * (ext.depth / 2.0)
    pts = [apply(ext.matrix, x, y) + shift for x, y in outline]
    return pts, float(ext.depth)


# --------------------------------------------------------------------------- #
#  Mesh fallback (Brep bodies, unresolved CSG, ...)                           #
# --------------------------------------------------------------------------- #

_GEOM_SETTINGS = None


def geom_settings():
    global _GEOM_SETTINGS
    if _GEOM_SETTINGS is None:
        s = ifcopenshell.geom.settings()
        s.set(s.USE_WORLD_COORDS, True)
        _GEOM_SETTINGS = s
    return _GEOM_SETTINGS


def mesh_vertices(element, length_scale: float) -> Optional[np.ndarray]:
    """World-space vertices (in file units) from the geometry engine."""
    try:
        shape = ifcopenshell.geom.create_shape(geom_settings(), element)
    except Exception:
        return None
    v = np.array(shape.geometry.verts, dtype=float).reshape(-1, 3)
    if v.size == 0:
        return None
    # The geometry engine returns SI metres; scale back to file length unit.
    return v / length_scale


def obb_axes(verts: np.ndarray) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Return (centre, sorted-axes(3x3), extents(3)) via PCA of the vertices."""
    c = verts.mean(axis=0)
    cov = np.cov((verts - c).T)
    _, vecs = np.linalg.eigh(cov)
    proj = (verts - c) @ vecs                       # coords in principal frame
    extents = np.ptp(proj, axis=0)
    order = np.argsort(extents)[::-1]               # long -> short
    return c, vecs[:, order].T, extents[order]


def mesh_centreline(verts: np.ndarray) -> Tuple[np.ndarray, np.ndarray, float]:
    """Longest-axis centre-line of a (roughly prismatic) point cloud."""
    c, axes, ext = obb_axes(verts)
    axis = axes[0]
    t = (verts - c) @ axis
    start = c + axis * t.min()
    end = c + axis * t.max()
    return start, end, float(ext[1])


def mesh_midsurface(verts: np.ndarray) -> Tuple[List[np.ndarray], float]:
    """
    Mid-plane outline of a thin point cloud: drop the shortest OBB axis,
    take the convex hull of the projection (approximate footprint).
    """
    c, axes, ext = obb_axes(verts)
    u, v, normal = axes[0], axes[1], axes[2]
    coords2d = np.column_stack(((verts - c) @ u, (verts - c) @ v))
    hull = _convex_hull(coords2d)
    pts = [c + u * a + v * b for a, b in hull]      # already at OBB centre => mid
    return pts, float(ext[2])


def _convex_hull(points: np.ndarray) -> List[Tuple[float, float]]:
    """Andrew's monotone chain convex hull (no SciPy dependency)."""
    pts = sorted(set(map(tuple, np.round(points, 6))))
    if len(pts) <= 2:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower: List[Tuple[float, float]] = []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    upper: List[Tuple[float, float]] = []
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


# --------------------------------------------------------------------------- #
#  Element extraction                                                         #
# --------------------------------------------------------------------------- #

def category_of(ifc_type: str) -> Optional[str]:
    for cat, classes in ELEMENT_GROUPS.items():
        if ifc_type in classes:
            return cat
    return None


def wall_panels(element) -> Optional[Tuple[List[List[np.ndarray]], float]]:
    """
    Vertical mid-plane panel(s) of a wall: the plan centre-line (Axis
    representation) extruded upward by the wall height.  Returns one closed
    quad per axis segment plus the wall thickness.
    """
    axis = axis_polyline_world(element)
    exts = body_extrusions(element)
    if not axis or len(axis) < 2 or not exts:
        return None
    ext = exts[0]
    height_vec = ext.matrix[:3, :3] @ ext.direction
    n = np.linalg.norm(height_vec)
    if n == 0:
        return None
    height_vec = height_vec / n * ext.depth
    thickness = wall_thickness(ext.profile)
    panels = []
    for a, b in zip(axis[:-1], axis[1:]):
        panels.append([a, b, b + height_vec, a + height_vec])
    return panels, thickness


def wall_thickness(profile) -> float:
    outline = profile_outline(profile)
    if not outline:
        return 0.0
    pts = np.array(outline)
    return float(np.ptp(pts, axis=0).min())


def extract_element(element, category: str, length_scale: float,
                    report) -> List[AnalyticalElement]:
    ifc_type = element.is_a()
    guid = getattr(element, "GlobalId", "") or ""
    name = getattr(element, "Name", "") or ""
    is_linear = ifc_type in LINEAR_CLASSES
    is_wall = ifc_type in WALL_CLASSES

    results: List[AnalyticalElement] = []

    # 0) Walls: analytical surface is the vertical plane through the axis.
    if is_wall:
        wp = wall_panels(element)
        if wp:
            panels, thick = wp
            for quad in panels:
                results.append(AnalyticalElement(
                    category, ifc_type, guid, name, "outline", quad, thick))
            if results:
                return results

    # 1) Preferred path: parametric extrusion(s).
    if not is_wall:
        for ext in body_extrusions(element):
            if is_linear:
                cl = extrusion_centreline(ext)
                if cl:
                    s, e, depth = cl
                    results.append(AnalyticalElement(
                        category, ifc_type, guid, name, "line", [s, e], depth))
            else:
                ms = extrusion_midsurface(ext)
                if ms:
                    pts, thick = ms
                    results.append(AnalyticalElement(
                        category, ifc_type, guid, name, "outline", pts, thick))
        if results:
            return results

    # 2) Fallback: reduce the triangulated body to an OBB primitive.
    verts = mesh_vertices(element, length_scale)
    if verts is None or len(verts) < 3:
        report.skipped.append((ifc_type, guid, name))
        return results
    if is_linear:
        s, e, depth = mesh_centreline(verts)
        results.append(AnalyticalElement(
            category, ifc_type, guid, name, "line", [s, e], depth, source="mesh"))
    else:
        pts, thick = mesh_midsurface(verts)
        if len(pts) >= 3:
            results.append(AnalyticalElement(
                category, ifc_type, guid, name, "outline", pts, thick, source="mesh"))
        else:
            report.skipped.append((ifc_type, guid, name))
    return results


# --------------------------------------------------------------------------- #
#  Report                                                                     #
# --------------------------------------------------------------------------- #

@dataclass
class Report:
    per_category: dict = field(default_factory=dict)
    mesh_fallback: int = 0
    skipped: list = field(default_factory=list)
    offset: np.ndarray = field(default_factory=lambda: np.zeros(3))
    bbox_min: Optional[np.ndarray] = None
    bbox_max: Optional[np.ndarray] = None


# --------------------------------------------------------------------------- #
#  DXF writer                                                                 #
# --------------------------------------------------------------------------- #

INSUNITS = {"mm": 4, "cm": 5, "m": 6}


def write_dxf(elements: Sequence[AnalyticalElement], path: str,
              offset: np.ndarray, scale: float, unit: str, report: Report) -> None:
    doc = ezdxf.new("R2010")
    doc.header["$INSUNITS"] = INSUNITS.get(unit, 4)
    msp = doc.modelspace()

    for cat, (layer, color) in LAYERS.items():
        doc.layers.add(name=layer, color=color)
    doc.layers.add(name="S_NODES", color=7)
    doc.layers.add(name="S_INFO", color=8)

    def xf(p: np.ndarray):
        q = (p - offset) * scale
        return (float(q[0]), float(q[1]), float(q[2]))

    nodes = set()
    for el in elements:
        layer = LAYERS[el.category][0]
        attribs = {"layer": layer}
        if el.kind == "line":
            a, b = xf(el.points[0]), xf(el.points[1])
            msp.add_line(a, b, dxfattribs=attribs)
            nodes.add(tuple(round(c, 3) for c in a))
            nodes.add(tuple(round(c, 3) for c in b))
        else:
            pts = [xf(p) for p in el.points]
            poly = msp.add_polyline3d(pts, dxfattribs=attribs)
            poly.close(True)

    # Structural nodes (endpoints) as points, useful when meshing.
    for n in nodes:
        msp.add_point(n, dxfattribs={"layer": "S_NODES"})

    # Recentre note so the model can be pushed back to its true location.
    if np.any(offset):
        note = ("Recentred analytical model. True world origin offset "
                "(file units): X=%.3f Y=%.3f Z=%.3f" % tuple(offset))
        msp.add_text(note, dxfattribs={"layer": "S_INFO", "height": 250.0 * scale}
                     ).set_placement((0, 0, 0))

    doc.saveas(path)


def try_make_dwg(dxf_path: str) -> Optional[str]:
    """Convert DXF -> DWG with ODA File Converter if it is on PATH."""
    exe = shutil.which("ODAFileConverter") or shutil.which("ODAFileConverter.exe")
    if not exe:
        return None
    out_dir = tempfile.mkdtemp()
    in_dir = os.path.dirname(os.path.abspath(dxf_path)) or "."
    base = os.path.basename(dxf_path)
    try:
        subprocess.run([exe, in_dir, out_dir, "ACAD2018", "DWG", "0", "1", base],
                       check=True, timeout=120)
    except Exception:
        return None
    produced = os.path.join(out_dir, os.path.splitext(base)[0] + ".dwg")
    if os.path.exists(produced):
        dwg_path = os.path.splitext(dxf_path)[0] + ".dwg"
        shutil.move(produced, dwg_path)
        return dwg_path
    return None


# --------------------------------------------------------------------------- #
#  Driver                                                                     #
# --------------------------------------------------------------------------- #

def length_scale_to_metre(ifc_file) -> float:
    """Metres per one file length unit (mm -> 0.001)."""
    for u in ifc_file.by_type("IfcSIUnit"):
        if u.UnitType == "LENGTHUNIT":
            prefix = {
                "MILLI": 1e-3, "CENTI": 1e-2, "DECI": 1e-1,
                "KILO": 1e3, None: 1.0,
            }.get(u.Prefix, 1.0)
            return prefix
    return 1.0  # assume metres


def convert(ifc_path: str, out_path: str, categories: Sequence[str],
            unit: str, recenter: bool, make_dwg: bool) -> Report:
    model = ifcopenshell.open(ifc_path)
    length_unit_m = length_scale_to_metre(model)     # e.g. 0.001 for mm files
    # The geometry engine returns metres; divide its verts by this to get
    # file units.  For a mm-file length_unit_m == 0.001 so verts_m / 0.001.
    mesh_scale = length_unit_m

    # Output scale: convert file units -> requested output unit.
    out_unit_m = {"mm": 1e-3, "cm": 1e-2, "m": 1.0}[unit]
    scale = length_unit_m / out_unit_m               # file-units -> out-units

    report = Report()
    elements: List[AnalyticalElement] = []
    wanted_classes = {c: cat for cat in categories for c in ELEMENT_GROUPS[cat]}

    # by_type() is inclusive of subtypes (e.g. IfcWall -> IfcWallStandardCase),
    # so dedupe by STEP id and keep the most specific requested category.
    seen: set = set()
    for cls, cat in sorted(wanted_classes.items()):
        for element in model.by_type(cls):
            if element.id() in seen:
                continue
            seen.add(element.id())
            extracted = extract_element(element, cat, mesh_scale, report)
            for e in extracted:
                if e.source == "mesh":
                    report.mesh_fallback += 1
            elements.extend(extracted)

    if not elements:
        raise SystemExit("No structural elements were extracted.")

    # Bounding box + recentre offset (in file units).
    allpts = np.array([p for e in elements for p in e.points])
    report.bbox_min = allpts.min(axis=0)
    report.bbox_max = allpts.max(axis=0)
    offset = report.bbox_min.copy() if recenter else np.zeros(3)
    report.offset = offset

    # Per-category tally.
    for cat in categories:
        report.per_category[cat] = sum(1 for e in elements if e.category == cat)

    write_dxf(elements, out_path, offset, scale, unit, report)

    if make_dwg:
        dwg = try_make_dwg(out_path)
        report.dwg_path = dwg  # type: ignore[attr-defined]

    return report


def parse_args(argv=None):
    p = argparse.ArgumentParser(
        description="Export IFC structural elements as an analytical DXF/DWG "
                    "(axes for columns/beams, mid-surfaces for slabs/walls).")
    p.add_argument("ifc", help="input IFC file")
    p.add_argument("-o", "--output", help="output DXF (default: <ifc>.dxf)")
    p.add_argument("--elements", default="columns,beams,slabs",
                   help="comma list of: columns,beams,slabs,walls "
                        "(default: columns,beams,slabs)")
    p.add_argument("--unit", choices=("mm", "cm", "m"), default="mm",
                   help="output unit (default: mm)")
    p.add_argument("--no-recenter", action="store_true",
                   help="keep original (survey) coordinates")
    p.add_argument("--dwg", action="store_true",
                   help="also write DWG (requires ODAFileConverter on PATH)")
    return p.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    categories = [c.strip() for c in args.elements.split(",") if c.strip()]
    bad = [c for c in categories if c not in ELEMENT_GROUPS]
    if bad:
        sys.exit("Unknown element group(s): %s (choose from %s)"
                 % (", ".join(bad), ", ".join(ELEMENT_GROUPS)))

    out = args.output or (os.path.splitext(args.ifc)[0] + ".dxf")
    report = convert(args.ifc, out, categories, args.unit,
                     not args.no_recenter, args.dwg)

    print("Analytical export complete ->", out)
    print("-" * 52)
    for cat in categories:
        print("  %-9s %5d element(s)" % (cat, report.per_category.get(cat, 0)))
    print("  %-9s %5d (Brep/CSG reduced to OBB)" % ("fallback", report.mesh_fallback))
    if report.skipped:
        print("  %-9s %5d (no usable geometry)" % ("skipped", len(report.skipped)))
    if np.any(report.offset):
        print("  recentre offset (file units): "
              "X=%.1f Y=%.1f Z=%.1f" % tuple(report.offset))
    dwg = getattr(report, "dwg_path", None)
    if args.dwg:
        print("  DWG:", dwg if dwg else "ODAFileConverter not found -> DXF only")


if __name__ == "__main__":
    main()
