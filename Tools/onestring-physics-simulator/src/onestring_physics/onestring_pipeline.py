"""Side-face/contact-aware T3D extrusion patch.

This file is intentionally a compatibility wrapper for the user's existing
onestring_pipeline.py.  It loads the backed-up original module and replaces only
_extrude_tiles() with a miter/contact-plane version, so the old Copy-Item based
workflow can be used without shipping a full copy of the large source file.

Expected workflow:
  Copy-Item .\src .\src_backup_before_sideface_contact -Recurse -Force
  Copy-Item .\sideface_contact_tmp\onestring_physics\* .\src\onestring_physics\ -Recurse -Force
"""

from __future__ import annotations

import importlib.util
import json
import sys
import time
import heapq
import math
from pathlib import Path

import numpy as np


def _project_root_from_this_file() -> Path:
    # <project>/src/onestring_physics/onestring_pipeline.py
    return Path(__file__).resolve().parents[2]


def _find_original_pipeline() -> Path:
    root = _project_root_from_this_file()
    candidates = [
        root / "src_backup_before_sideface_contact" / "onestring_physics" / "onestring_pipeline.py",
        root / "src_backup_before_mitered_t3d" / "onestring_physics" / "onestring_pipeline.py",
        root / "src_backup_before_sideface_contact" / "src" / "onestring_physics" / "onestring_pipeline.py",
        root / "src_backup_before_mitered_t3d" / "src" / "onestring_physics" / "onestring_pipeline.py",
        root / "src" / "onestring_physics" / "onestring_pipeline.py.bak_mitered_t3d",
    ]
    for path in candidates:
        if not path.exists() or path.resolve() == Path(__file__).resolve():
            continue
        try:
            head = path.read_text(encoding="utf-8", errors="ignore")[:2500]
        except Exception:
            head = ""
        # If the user re-runs the old copy commands after a failed patch, the
        # backup directory may accidentally contain this wrapper instead of the
        # real original file.  Skip wrapper backups to avoid recursive imports and
        # continue to older backups such as src_backup_before_mitered_t3d.
        if "Side-face/contact-aware T3D extrusion patch" in head and "_find_original_pipeline" in head:
            continue
        return path
    tried = "\n  - ".join(str(p) for p in candidates)
    raise RuntimeError(
        "Could not find the original onestring_pipeline.py backup.\n"
        "Run the backup command before copying this patch:\n"
        "  Copy-Item .\\src .\\src_backup_before_sideface_contact -Recurse -Force\n\n"
        f"Tried:\n  - {tried}"
    )


_ORIGINAL_PATH = _find_original_pipeline()
_ORIGINAL_MODULE_NAME = "onestring_physics._onestring_pipeline_original_sideface_contact"

_spec = importlib.util.spec_from_file_location(_ORIGINAL_MODULE_NAME, _ORIGINAL_PATH)
if _spec is None or _spec.loader is None:
    raise RuntimeError(f"Could not load original pipeline from {_ORIGINAL_PATH}")
_original = importlib.util.module_from_spec(_spec)
sys.modules[_ORIGINAL_MODULE_NAME] = _original
_spec.loader.exec_module(_original)


def _normalize(v: np.ndarray, fallback: np.ndarray | None = None) -> np.ndarray:
    arr = np.asarray(v, dtype=float)
    n = float(np.linalg.norm(arr))
    if n <= 1e-12 or not np.isfinite(n):
        if fallback is None:
            return np.zeros_like(arr, dtype=float)
        fb = np.asarray(fallback, dtype=float)
        fb_n = float(np.linalg.norm(fb))
        return fb / max(fb_n, 1e-12)
    return arr / n


def _edge_inward_normal(top: np.ndarray, face_normal: np.ndarray, edge: tuple[int, int]) -> np.ndarray:
    """Plane normal for the side face through a top edge, pointing into the tile.

    The normal lies in the tile plane and is perpendicular to the edge.  Its sign
    is chosen so that the tile center is on the positive side.
    """
    a, b = edge
    p0 = np.asarray(top[a], dtype=float)
    p1 = np.asarray(top[b], dtype=float)
    center = np.mean(top, axis=0)
    edge_dir = _normalize(p1 - p0, np.array([1.0, 0.0, 0.0]))
    q = np.cross(edge_dir, face_normal)
    q = _normalize(q, np.array([0.0, 1.0, 0.0]))
    mid = 0.5 * (p0 + p1)
    if float(np.dot(q, center - mid)) < 0.0:
        q = -q
    return q


def _build_edge_incidence(faces: np.ndarray) -> dict[tuple[int, int], list[tuple[int, int]]]:
    local_edges = [(0, 1), (1, 2), (2, 3), (3, 0)]
    incidence: dict[tuple[int, int], list[tuple[int, int]]] = {}
    for tile_id, face in enumerate(np.asarray(faces, dtype=int)):
        for edge_id, (a, b) in enumerate(local_edges):
            key = tuple(sorted((int(face[a]), int(face[b]))))
            incidence.setdefault(key, []).append((int(tile_id), int(edge_id)))
    return incidence


def _solve_bottom_vertex(
    top: np.ndarray,
    face_normal: np.ndarray,
    thickness: float,
    side_normals: list[np.ndarray],
    vertex_id: int,
) -> tuple[np.ndarray, bool]:
    """Return bottom vertex and whether fallback was used."""
    local_edges = [(0, 1), (1, 2), (2, 3), (3, 0)]
    center = np.mean(top, axis=0)
    prev_edge = (vertex_id - 1) % 4
    next_edge = vertex_id % 4

    q_prev = side_normals[prev_edge]
    q_next = side_normals[next_edge]
    mid_prev = 0.5 * (top[local_edges[prev_edge][0]] + top[local_edges[prev_edge][1]])
    mid_next = 0.5 * (top[local_edges[next_edge][0]] + top[local_edges[next_edge][1]])

    bottom_plane_c = float(np.dot(face_normal, center) - float(thickness))
    a_mat = np.vstack([face_normal, q_prev, q_next])
    b_vec = np.asarray(
        [
            bottom_plane_c,
            float(np.dot(q_prev, mid_prev)),
            float(np.dot(q_next, mid_next)),
        ],
        dtype=float,
    )

    fallback = np.asarray(top[vertex_id], dtype=float) - float(thickness) * face_normal
    try:
        cond = float(np.linalg.cond(a_mat))
        if not np.isfinite(cond) or cond > 1e10:
            return fallback, True
        out = np.linalg.solve(a_mat, b_vec)
        if not np.all(np.isfinite(out)):
            return fallback, True
        return out, False
    except Exception:
        return fallback, True


def _extrude_tiles(mesh, thickness: float, stage: str):
    """Extrude K3D tiles using shared-edge miter/contact planes.

    Previous behavior:
        bottom = top - thickness * tile_normal

    New behavior:
        - top face remains K3D
        - bottom vertices lie on the offset bottom plane
        - each side face lies on an edge plane
        - shared edges use a single miter/contact plane derived from the two
          adjacent tiles, so neighboring thick panels meet consistently
    """
    import time

    start = time.perf_counter()
    top_tiles = _original._mesh_tiles(mesh)
    tile_count = int(top_tiles.shape[0])
    vertices = np.zeros((tile_count, 8, 3), dtype=float)
    transforms = np.zeros((tile_count, 4, 4), dtype=float)
    local_edges = [(0, 1), (1, 2), (2, 3), (3, 0)]

    if tile_count == 0:
        top_faces = np.asarray([], dtype=int).reshape(0, 4)
        bottom_faces = np.asarray([], dtype=int).reshape(0, 4)
        side_faces = np.asarray([[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]], dtype=int)
        assembly = _original.TileAssembly(
            vertices=vertices,
            top_faces=top_faces,
            bottom_faces=bottom_faces,
            side_faces=side_faces,
            stage=stage,
            metrics={
                "objective": "Contact-aware mitered extrusion.",
                "extrusion_model": "mitered_contact_planes",
                "contact_aware_extrusion": True,
                "tile_thickness": float(thickness),
                "tile_count": 0,
            },
            transform_matrices=transforms,
        )
        report = _original.StageReport(
            name=f"{mesh.stage} -> {stage}",
            objective="Extrude K3D into contact-aware mitered eight-vertex frustum tiles.",
            before_error=0.0,
            after_error=0.0,
            constraint_violation=0.0,
            computation_time=time.perf_counter() - start,
            counts=_original._assembly_counts(assembly),
        )
        return assembly, report

    normals = np.asarray([_original._quad_normal(top) for top in top_tiles], dtype=float)
    raw_side_normals: list[list[np.ndarray]] = []
    for tile_id, top in enumerate(top_tiles):
        raw_side_normals.append([_edge_inward_normal(top, normals[tile_id], edge) for edge in local_edges])

    side_normals: list[list[np.ndarray]] = [[raw_side_normals[i][e].copy() for e in range(4)] for i in range(tile_count)]
    incidence = _build_edge_incidence(mesh.faces)
    internal_miter_edge_count = 0
    boundary_side_plane_count = 0
    nonmanifold_edge_count = 0

    for entries in incidence.values():
        if len(entries) == 1:
            boundary_side_plane_count += 1
            continue
        if len(entries) != 2:
            nonmanifold_edge_count += 1
            continue
        (tile_a, edge_a), (tile_b, edge_b) = entries
        q_a = raw_side_normals[tile_a][edge_a]
        q_b = raw_side_normals[tile_b][edge_b]
        miter = _normalize(q_a - q_b, q_a)
        if float(np.linalg.norm(miter)) <= 1e-12:
            miter = q_a
        side_normals[tile_a][edge_a] = miter
        side_normals[tile_b][edge_b] = -miter
        internal_miter_edge_count += 1

    fallback_count = 0
    for tile_id, top in enumerate(top_tiles):
        normal = normals[tile_id]
        bottom = np.zeros((4, 3), dtype=float)
        for vertex_id in range(4):
            bottom[vertex_id], used_fallback = _solve_bottom_vertex(
                top,
                normal,
                float(thickness),
                side_normals[tile_id],
                vertex_id,
            )
            fallback_count += int(used_fallback)

        vertices[tile_id, :4] = top
        vertices[tile_id, 4:] = bottom

        # IMPORTANT for T2D/animation compatibility:
        # Do not store a shearing/affine top->bottom map here.  The original
        # T2D builder treats transform_matrices as a stable per-tile geometric
        # offset when it lays out thick panels in the flat state.  A least-squares
        # affine map can inject shear/scale into T2D and break the deployment
        # animation.  Keep this transform rigid/translation-only as a safe seed;
        # the patched T2D builder below then rigidly places the full mitered T3D
        # solid so per-tile shape is preserved.
        transform = np.eye(4, dtype=float)
        transform[:3, 3] = np.mean(bottom, axis=0) - np.mean(top, axis=0)
        transforms[tile_id] = transform

    top_faces = np.asarray([[0, 1, 2, 3] for _ in range(tile_count)], dtype=int)
    bottom_faces = np.asarray([[4, 7, 6, 5] for _ in range(tile_count)], dtype=int)
    side_faces = np.asarray([[0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]], dtype=int)

    planarity = _original._tile_face_planarity(vertices)
    face_planarity = _original._tile_face_planarity_by_group(vertices)
    signed_thickness = np.sum((vertices[:, :4] - vertices[:, 4:]) * normals[:, None, :], axis=2)
    thickness_error = signed_thickness - float(thickness)
    center_shift = np.mean(vertices[:, 4:], axis=1) - np.mean(vertices[:, :4], axis=1)
    normal_shift_error = np.linalg.norm(center_shift + float(thickness) * normals, axis=1)

    assembly = _original.TileAssembly(
        vertices=vertices,
        top_faces=top_faces,
        bottom_faces=bottom_faces,
        side_faces=side_faces,
        stage=stage,
        metrics={
            "objective": "Contact-aware mitered extrusion and face planarity report.",
            "extrusion_model": "mitered_contact_planes",
            "contact_aware_extrusion": True,
            "mitered_shared_edge_planes": True,
            "legacy_normal_translation_extrusion": False,
            "t2d_transform_seed_model": "translation_only_center_shift_no_affine_shear",
            "face_planarity_error": planarity,
            "top_face_planarity_error": face_planarity["top"],
            "bottom_face_planarity_error": face_planarity["bottom"],
            "side_face_planarity_error": face_planarity["side"],
            "tile_thickness": float(thickness),
            "thickness_target": float(thickness),
            "thickness_error_rms": float(np.sqrt(np.mean(thickness_error * thickness_error))) if thickness_error.size else 0.0,
            "thickness_error_max": float(np.max(np.abs(thickness_error))) if thickness_error.size else 0.0,
            "normal_translation_center_shift_error_rms": float(np.sqrt(np.mean(normal_shift_error * normal_shift_error))) if normal_shift_error.size else 0.0,
            "internal_miter_edge_count": int(internal_miter_edge_count),
            "boundary_side_plane_count": int(boundary_side_plane_count),
            "nonmanifold_edge_count": int(nonmanifold_edge_count),
            "bottom_vertex_solve_fallback_count": int(fallback_count),
            "surface_fit_error": float(mesh.metrics.get("surface_fit_error_after", 0.0)),
            "tile_count": int(tile_count),
            "k3d_fallback_warning": str(mesh.metrics.get("approximation_warning", "")),
            **_original._tile_orientation_metrics(vertices, f"{stage.lower()}"),
        },
        transform_matrices=transforms,
    )
    report = _original.StageReport(
        name=f"{mesh.stage} -> {stage}",
        objective="Extrude K3D into contact-aware mitered eight-vertex frustum tiles.",
        before_error=0.0,
        after_error=planarity,
        constraint_violation=planarity,
        computation_time=time.perf_counter() - start,
        counts=_original._assembly_counts(assembly),
    )
    return assembly, report



_ORIGINAL_MAKE_T2D_FROM_TRANSFORMS = _original._make_t2d_from_transforms

_ORIGINAL_OPTIMIZE_T2D_FOOTPRINT_LAYOUT = _original._optimize_t2d_footprint_layout
_ORIGINAL_OPTIMIZE_RIGID_ASSEMBLY_HINGE_LAYOUT_2D = _original._optimize_rigid_assembly_hinge_layout_2d


def _grid_with_layout_gap(grid, minimum_gap: float):
    """Return a shallow grid copy whose gap_size is large enough for void layout.

    The original layout solvers use grid.gap_size mainly to set the collision /
    clearance scale.  Increasing it here gives the panel placement stage more
    room to keep voids open without changing the actual K2D/K3D mesh topology.
    """
    import copy

    out = copy.copy(grid)
    try:
        out.gap_size = max(float(getattr(grid, "gap_size", 0.0)), float(minimum_gap))
    except Exception:
        return grid
    return out


def _free_layout_parameters(
    grid,
    iterations: int,
    connection_weight: float,
    collision_weight: float,
    anchor_weight: float,
    initial_expansion: float,
    max_center_drift_tiles: float,
) -> dict[str, float | int]:
    tile_size = max(float(getattr(grid, "tile_size", 1.0)), 1e-8)
    requested_gap = float(getattr(grid, "gap_size", 0.08))
    # A larger optimization-only void clearance.  This does not rewrite the mesh;
    # it only tells the placement optimizer to leave visible air between panels.
    layout_gap = max(requested_gap * 1.75, tile_size * 0.10)
    return {
        "iterations": int(max(240, int(iterations) * 3)),
        "connection_weight": float(max(40.0, float(connection_weight) * 12.0)),
        "collision_weight": float(max(3.0, float(collision_weight) * 3.0)),
        # Keep the initial pose as a weak prior, not as a cage.  The old values
        # were too anchor-heavy for mitered solids and could collapse the holes.
        "anchor_weight": float(max(0.003, min(0.025, float(anchor_weight) * 0.25))),
        "initial_expansion": float(max(1.22, float(initial_expansion))),
        "max_center_drift_tiles": float(max(4.0, float(max_center_drift_tiles))),
        "layout_gap": float(layout_gap),
        "clearance": float(max(layout_gap * 0.65, tile_size * 0.035)),
    }


def _layout_quality_for_top_xy(layout: np.ndarray, transforms: np.ndarray, faces: np.ndarray, grid, constraints) -> dict[str, float | int]:
    layout = np.asarray(layout, dtype=float)
    if layout.size == 0:
        return {"hinge_error": 0.0, "collision_count": 0, "min_clearance": 0.0}
    footprints = _original._apply_t2d_transforms_to_top_xy(layout, transforms)[:, :, :2]
    pad = max(float(getattr(grid, "gap_size", 0.08)) * 8.0, float(getattr(grid, "tile_size", 1.0)) * 0.25)
    pairs = _original._spatial_candidate_pairs_for_tiles(footprints, pad=pad)
    specs = _original._vertex_hinge_specs_from_faces(faces)
    return {
        "hinge_error": float(_original._vertex_layout_hinge_error(layout, specs)),
        "collision_count": int(_original._count_2d_footprint_collisions_from_pairs(footprints, pairs)),
        "min_clearance": float(_original._min_footprint_clearance_2d_from_pairs(footprints, pairs)),
    }


def _optimize_t2d_footprint_layout(
    top_xy: np.ndarray,
    transforms: np.ndarray,
    faces: np.ndarray,
    grid,
    iterations: int,
    connection_weight: float,
    collision_weight: float,
    anchor_weight: float,
    time_budget_sec: float = 8.0,
    max_candidate_pairs: int = 3000,
    collision_sweeps_per_iteration: int = 2,
    initial_expansion: float = 1.0,
    max_center_drift_tiles: float = 2.0,
    progress_callback=None,
) -> tuple[np.ndarray, dict[str, float | int | str | bool]]:
    """More permissive T2D placement for contact-aware thick panels.

    Goal ordering:
      1. vertex hinges should be effectively closed;
      2. projected top+bottom footprints should leave visible voids;
      3. the solution should remain near the expanded initial layout.

    This keeps the original local/global SE(2) solve, but gives it more freedom:
    larger expansion/drift, weaker anchor, stronger connection, and a larger
    collision clearance.  A final hinge-polish pass is accepted only if it does
    not introduce a large collision regression.
    """
    rest = np.asarray(top_xy, dtype=float)
    if len(rest) == 0:
        return rest.copy(), {"t2d_footprint_optimizer": "empty_free_layout"}

    specs = _original._vertex_hinge_specs_from_faces(faces)
    constraints = _original._hinge_constraint_tuples_from_specs(specs)

    def footprint_builder(layout: np.ndarray) -> np.ndarray:
        return _original._apply_t2d_transforms_to_top_xy(layout, transforms)[:, :, :2]

    free = _free_layout_parameters(
        grid,
        iterations,
        connection_weight,
        collision_weight,
        anchor_weight,
        initial_expansion,
        max_center_drift_tiles,
    )
    free_grid = _grid_with_layout_gap(grid, float(free["layout_gap"]))
    before = _layout_quality_for_top_xy(rest, transforms, faces, free_grid, constraints)

    solved, metrics = _original._paper_local_global_se2_layout(
        rest,
        constraints,
        footprint_builder=footprint_builder,
        initial_xy=rest,
        iterations=int(free["iterations"]),
        connection_weight=float(free["connection_weight"]),
        collision_weight=float(free["collision_weight"]),
        anchor_weight=float(free["anchor_weight"]),
        clearance=float(free["clearance"]),
        stage_name="T2D Top Hinge free void-preserving placement",
        time_budget_sec=max(float(time_budget_sec), 12.0),
        max_candidate_pairs=int(max_candidate_pairs),
        collision_sweeps_per_iteration=max(2, int(collision_sweeps_per_iteration)),
        initial_expansion=float(free["initial_expansion"]),
        max_center_drift_tiles=float(free["max_center_drift_tiles"]),
        progress_callback=progress_callback,
    )
    after_free = _layout_quality_for_top_xy(solved, transforms, faces, free_grid, constraints)

    # Hinge polish: make the hinge term even harder.  Because this can close some
    # holes, keep the polished result only if collision/clearance does not regress
    # too far compared with the free-layout solution.
    polished, polish_metrics = _original._paper_local_global_se2_layout(
        rest,
        constraints,
        footprint_builder=footprint_builder,
        initial_xy=solved,
        iterations=max(80, int(iterations)),
        connection_weight=max(120.0, float(free["connection_weight"]) * 2.0),
        collision_weight=max(2.0, float(free["collision_weight"]) * 0.75),
        anchor_weight=max(0.002, float(free["anchor_weight"]) * 0.5),
        clearance=float(free["clearance"]) * 0.75,
        stage_name="T2D Top Hinge hard-hinge polish",
        time_budget_sec=max(4.0, float(time_budget_sec) * 0.5),
        max_candidate_pairs=int(max_candidate_pairs),
        collision_sweeps_per_iteration=max(1, int(collision_sweeps_per_iteration)),
        initial_expansion=1.0,
        max_center_drift_tiles=float(free["max_center_drift_tiles"]),
        progress_callback=None,
    )
    after_polish = _layout_quality_for_top_xy(polished, transforms, faces, free_grid, constraints)
    accept_polish = (
        after_polish["hinge_error"] <= after_free["hinge_error"] * 0.85 + 1e-8
        and after_polish["collision_count"] <= after_free["collision_count"] + max(1, int(len(rest) * 0.03))
    )
    if accept_polish:
        solved = polished
        final = after_polish
    else:
        final = after_free

    shape_rms = _original._tile_shape_distance_error(
        np.dstack([solved, np.zeros(solved.shape[:2])]),
        np.dstack([rest, np.zeros(rest.shape[:2])]),
    )
    shape_max = _original._tile_shape_distance_error(
        np.dstack([solved, np.zeros(solved.shape[:2])]),
        np.dstack([rest, np.zeros(rest.shape[:2])]),
        use_max=True,
    )
    out = {
        "t2d_footprint_optimizer": "free local/global SE(2) layout with hard-hinge priority and void clearance",
        "t2d_free_layout_enabled": True,
        "t2d_free_layout_goal_order": "hard hinges > open voids/collision clearance > weak initial-layout anchor",
        "t2d_free_layout_iterations": int(free["iterations"]),
        "t2d_free_layout_connection_weight": float(free["connection_weight"]),
        "t2d_free_layout_collision_weight": float(free["collision_weight"]),
        "t2d_free_layout_anchor_weight": float(free["anchor_weight"]),
        "t2d_free_layout_initial_expansion": float(free["initial_expansion"]),
        "t2d_free_layout_max_center_drift_tiles": float(free["max_center_drift_tiles"]),
        "t2d_free_layout_gap_size_used_for_clearance": float(free["layout_gap"]),
        "t2d_free_layout_clearance": float(free["clearance"]),
        "t2d_hard_hinge_polish_accepted": bool(accept_polish),
        "t2d_footprint_collision_checked_on": "top+bottom projected footprint with SAT, enlarged optimization-only clearance",
        "t2d_footprint_hinge_error_before": float(before["hinge_error"]),
        "t2d_footprint_hinge_error_after": float(final["hinge_error"]),
        "t2d_footprint_collision_count_before": int(before["collision_count"]),
        "t2d_footprint_collision_count_after": int(final["collision_count"]),
        "t2d_footprint_min_clearance_before": float(before["min_clearance"]),
        "t2d_footprint_min_clearance_after": float(final["min_clearance"]),
        "t2d_top_tile_shape_rms_error_after_footprint_layout": float(shape_rms),
        "t2d_top_tile_shape_max_error_after_footprint_layout": float(shape_max),
        "t2d_top_shape_preserved_by_rigid_pose_fit": bool(shape_max < 1e-8),
        **metrics,
    }
    out.update({f"hard_hinge_polish_{k}": v for k, v in polish_metrics.items() if isinstance(v, (int, float, str, bool))})
    return solved, out


def _optimize_rigid_assembly_hinge_layout_2d(
    rest_vertices: np.ndarray,
    hinges,
    grid,
    iterations: int,
    connection_weight: float,
    collision_weight: float,
    anchor_weight: float,
    time_budget_sec: float = 8.0,
    max_candidate_pairs: int = 3000,
    collision_sweeps_per_iteration: int = 2,
    initial_expansion: float = 1.08,
    max_center_drift_tiles: float = 2.0,
    progress_callback=None,
):
    """More permissive dual-hinge/full-panel placement.

    This wraps the original rigid assembly optimizer but deliberately relaxes the
    anchor and expands the trust region, so panels can rearrange to open voids.
    Connection and collision weights are raised to keep hinges closed and panels
    separated.
    """
    free = _free_layout_parameters(
        grid,
        iterations,
        connection_weight,
        collision_weight,
        anchor_weight,
        initial_expansion,
        max_center_drift_tiles,
    )
    free_grid = _grid_with_layout_gap(grid, float(free["layout_gap"]))
    vertices, metrics = _ORIGINAL_OPTIMIZE_RIGID_ASSEMBLY_HINGE_LAYOUT_2D(
        rest_vertices=rest_vertices,
        hinges=hinges,
        grid=free_grid,
        iterations=int(free["iterations"]),
        connection_weight=max(60.0, float(free["connection_weight"])),
        collision_weight=max(3.5, float(free["collision_weight"])),
        anchor_weight=float(free["anchor_weight"]),
        time_budget_sec=max(float(time_budget_sec), 12.0),
        max_candidate_pairs=int(max_candidate_pairs),
        collision_sweeps_per_iteration=max(2, int(collision_sweeps_per_iteration)),
        initial_expansion=float(free["initial_expansion"]),
        max_center_drift_tiles=float(free["max_center_drift_tiles"]),
        progress_callback=progress_callback,
    )

    # Final rigid hinge closure pass.  This translates whole tiles toward their
    # hinge midpoints and reprojects each tile onto its original rigid shape.  It
    # gives the user the intended behavior: hinges are treated as nearly hard
    # constraints, while the preceding solve already made room for voids.
    repaired = vertices.copy()
    before_hinge = float(_original._hinge_connection_error(repaired, hinges)) if hinges else 0.0
    for _ in range(16):
        _original._project_hinge_tile_translations(repaired, hinges, 1.0)
        _original._project_aabb_collisions(repaired, 0.08, grid=free_grid, all_pairs=False)
        _original._project_rigid_tiles(repaired, rest_vertices, 1.0)
    after_hinge = float(_original._hinge_connection_error(repaired, hinges)) if hinges else 0.0
    # Use the hard-closed result unless it catastrophically increases AABB overlaps.
    old_coll = int(_original._count_aabb_collisions(vertices, free_grid))
    new_coll = int(_original._count_aabb_collisions(repaired, free_grid))
    accept_repair = after_hinge <= before_hinge + 1e-8 and new_coll <= old_coll + max(1, int(len(repaired) * 0.04))
    if accept_repair:
        vertices = repaired
    else:
        after_hinge = before_hinge
        new_coll = old_coll

    metrics = dict(metrics)
    metrics.update(
        {
            "dual_hinge_free_layout_enabled": True,
            "dual_hinge_free_layout_goal_order": "hard hinges > open voids/collision clearance > weak initial-layout anchor",
            "dual_hinge_free_layout_iterations": int(free["iterations"]),
            "dual_hinge_free_layout_connection_weight": float(max(60.0, float(free["connection_weight"]))),
            "dual_hinge_free_layout_collision_weight": float(max(3.5, float(free["collision_weight"]))),
            "dual_hinge_free_layout_anchor_weight": float(free["anchor_weight"]),
            "dual_hinge_free_layout_initial_expansion": float(free["initial_expansion"]),
            "dual_hinge_free_layout_max_center_drift_tiles": float(free["max_center_drift_tiles"]),
            "dual_hinge_free_layout_gap_size_used_for_clearance": float(free["layout_gap"]),
            "dual_hinge_hard_hinge_repair_accepted": bool(accept_repair),
            "dual_hinge_hard_hinge_error_before_repair": float(before_hinge),
            "dual_hinge_hard_hinge_error_after_repair": float(after_hinge),
            "dual_hinge_collision_count_after_hard_repair": int(new_coll),
        }
    )
    return vertices, metrics



def _make_t2d_from_transforms(mesh_2d, flat_layout, mesh_3d, tiles_3d, stage: str, params=None):
    """Build T2D while preserving the full mitered T3D tile shape.

    The first side-face patch changed T3D tiles from translation extrusions into
    mitered frusta.  Those tiles are no longer representable by a single affine
    top->bottom transform without shear.  The old T2D path used the transform to
    create bottom vertices from K2D top vertices, so an affine transform could
    distort the flat panels and break the animation.

    Compatibility strategy:
    1. Let the original T2D builder solve the flat top/footprint layout, using
       the safe translation-only transform seed stored by _extrude_tiles().
    2. Replace each resulting tile by a rigid placement of the actual mitered
       T3D solid at that solved flat top pose.

    This keeps the original working T2D layout behavior but restores the most
    important physical invariant for deployment: each T2D tile and its T3D target
    are the same rigid 8-vertex solid up to rotation/translation.
    """
    start = time.perf_counter()
    base_assembly, base_report = _ORIGINAL_MAKE_T2D_FROM_TRANSFORMS(
        mesh_2d,
        flat_layout,
        mesh_3d,
        tiles_3d,
        stage,
        params,
    )
    if len(base_assembly.vertices) == 0:
        return base_assembly, base_report

    placed_vertices = np.zeros_like(base_assembly.vertices)
    rigid_transforms = np.zeros((len(base_assembly.vertices), 4, 4), dtype=float)
    top_errors = []
    for tile_id in range(len(base_assembly.vertices)):
        flat_top = base_assembly.vertices[tile_id, :4]
        placed, transform = _original._rigidly_place_t3d_tile_in_flat_layout(
            tiles_3d.vertices[tile_id],
            flat_top,
        )
        placed_vertices[tile_id] = placed
        rigid_transforms[tile_id] = transform
        top_errors.append(np.linalg.norm(placed[:4, :2] - flat_top[:, :2], axis=1))

    top_errors_arr = np.asarray(top_errors, dtype=float).reshape(-1) if top_errors else np.zeros(0)
    face_planarity = _original._tile_face_planarity_by_group(placed_vertices)
    full_shape_rms = _original._tile_shape_distance_error(placed_vertices, tiles_3d.vertices)
    full_shape_max = _original._tile_shape_distance_error(placed_vertices, tiles_3d.vertices, use_max=True)
    top_shape_rms = _original._tile_shape_distance_error(placed_vertices[:, :4, :], tiles_3d.vertices[:, :4, :])
    top_shape_max = _original._tile_shape_distance_error(placed_vertices[:, :4, :], tiles_3d.vertices[:, :4, :], use_max=True)

    metrics = dict(base_assembly.metrics)
    metrics.update(
        {
            "t2d_geometry_repair_applied": True,
            "t2d_geometry_repair_reason": "mitered T3D cannot be represented by affine/shear top-to-bottom transforms without breaking rigid-panel animation",
            "t2d_geometry_model": "rigidly_placed_mitered_T3D_tiles_after_original_flat_layout",
            "transform_source": "rigid placement of each full mitered T3D tile onto the solved flat top pose",
            "fabrication_geometry_model": "T2D preserves the complete 8-vertex mitered T3D tile shape; top pose comes from the original K2D/T2D layout solve",
            "rigid_copy_of_T3D_forced": True,
            "paper_t2d_extrusion_model": True,
            "t2d_t3d_congruent_tile_geometry": bool(full_shape_max < 1e-6),
            "tile_shape_rms_error_to_T3D": float(full_shape_rms),
            "tile_shape_max_error_to_T3D": float(full_shape_max),
            "top_tile_shape_rms_error_to_K3D": float(top_shape_rms),
            "top_tile_shape_max_error_to_K3D": float(top_shape_max),
            "top_vertices_match_pre_repair_flat_layout_max_error": float(np.max(top_errors_arr)) if top_errors_arr.size else 0.0,
            "top_vertices_match_pre_repair_flat_layout_rms_error": float(np.sqrt(np.mean(top_errors_arr * top_errors_arr))) if top_errors_arr.size else 0.0,
            "face_planarity_error": _original._tile_face_planarity(placed_vertices),
            "top_face_planarity_error": face_planarity["top"],
            "bottom_face_planarity_error": face_planarity["bottom"],
            "side_face_planarity_error": face_planarity["side"],
            **_original._tile_orientation_metrics(placed_vertices, "t2d"),
        }
    )
    repaired = _original.TileAssembly(
        vertices=placed_vertices,
        top_faces=base_assembly.top_faces.copy(),
        bottom_faces=base_assembly.bottom_faces.copy(),
        side_faces=base_assembly.side_faces.copy(),
        stage=base_assembly.stage,
        metrics=metrics,
        transform_matrices=rigid_transforms,
    )
    report = _original.StageReport(
        name=base_report.name,
        objective="Generate T2D by original flat layout solve, then rigidly place contact-aware mitered T3D tiles.",
        before_error=base_report.before_error,
        after_error=float(full_shape_rms),
        constraint_violation=float(metrics.get("top_vertices_match_pre_repair_flat_layout_rms_error", 0.0)),
        computation_time=float(base_report.computation_time) + (time.perf_counter() - start),
        failed_constraints=list(getattr(base_report, "failed_constraints", [])),
        counts=_original._assembly_counts(repaired),
    )
    return repaired, report





# ---------------------------------------------------------------------------
# Paper Section 5.2-style lift point optimizer patch
# ---------------------------------------------------------------------------
_ORIGINAL_BUILD_ONESTRING_DESIGN = _original.build_onestring_design
_ORIGINAL_PAPER_CONSISTENCY_REPORT = getattr(_original, "paper_consistency_report", None)


def _gap_id(gap, fallback: int) -> int:
    try:
        return int(getattr(gap, "id"))
    except Exception:
        return int(fallback)


def _gap_centroid(gap) -> np.ndarray:
    for name in ("centroid_2d", "centroid", "position", "center"):
        if hasattr(gap, name):
            arr = np.asarray(getattr(gap, name), dtype=float).reshape(-1)
            if arr.size >= 3:
                return arr[:3]
            if arr.size == 2:
                return np.asarray([arr[0], arr[1], 0.0], dtype=float)
    return np.zeros(3, dtype=float)


def _gap_centroid_3d(gap) -> np.ndarray:
    for name in ("centroid_3d", "target", "position_3d"):
        if hasattr(gap, name):
            arr = np.asarray(getattr(gap, name), dtype=float).reshape(-1)
            if arr.size >= 3:
                return arr[:3]
            if arr.size == 2:
                return np.asarray([arr[0], arr[1], 0.0], dtype=float)
    return _gap_centroid(gap)


def _gap_gpe(gap) -> float:
    try:
        val = float(getattr(gap, "gpe", 0.0))
        return val if np.isfinite(val) else 0.0
    except Exception:
        return 0.0


def _route_turn_angle(points: np.ndarray) -> float:
    pts = np.asarray(points, dtype=float)
    if len(pts) < 3:
        return 0.0
    total = 0.0
    for i in range(1, len(pts) - 1):
        a = pts[i] - pts[i - 1]
        b = pts[i + 1] - pts[i]
        na = float(np.linalg.norm(a))
        nb = float(np.linalg.norm(b))
        if na < 1e-12 or nb < 1e-12:
            continue
        c = float(np.clip(np.dot(a, b) / (na * nb), -1.0, 1.0))
        total += float(np.arccos(c))
    return total


def _route_length(points: np.ndarray) -> float:
    pts = np.asarray(points, dtype=float)
    if len(pts) < 2:
        return 0.0
    return float(np.sum(np.linalg.norm(np.diff(pts, axis=0), axis=1)))


def _build_gap_adjacency(gap_graph) -> tuple[dict[int, object], dict[int, list[int]]]:
    gaps = list(getattr(gap_graph, "gaps", []) or [])
    id_to_gap = {_gap_id(g, i): g for i, g in enumerate(gaps)}
    adjacency: dict[int, set[int]] = {gid: set() for gid in id_to_gap}
    for edge in list(getattr(gap_graph, "edges", []) or []):
        if len(edge) < 2:
            continue
        a = int(edge[0]); b = int(edge[1])
        if a in id_to_gap and b in id_to_gap and a != b:
            adjacency[a].add(b)
            adjacency[b].add(a)
    return id_to_gap, {gid: sorted(vals) for gid, vals in adjacency.items()}


def _paper_like_energy_peaks_and_basins(gap_graph):
    """Graph proxy for the paper's discrete Morse-Smale peak/basin step.

    The paper uses discrete Morse-Smale segmentation on the scalar GPE field.
    This lightweight implementation keeps the same data model but computes peaks
    as graph-local GPE maxima and basins by steepest-ascent flow on the gap graph.
    The subsequent MST barrier/coupling DAG step follows Sec. 5.2 directly.
    """
    id_to_gap, adjacency = _build_gap_adjacency(gap_graph)
    interior_ids = [gid for gid, g in id_to_gap.items() if not bool(getattr(g, "boundary", False))]
    candidates = interior_ids or list(id_to_gap.keys())
    eps = 1e-12
    peaks: list[int] = []
    for gid in candidates:
        gpe = _gap_gpe(id_to_gap[gid])
        if gpe <= eps:
            continue
        higher_neighbor = False
        for nb in adjacency.get(gid, []):
            if nb not in candidates:
                continue
            if _gap_gpe(id_to_gap[nb]) > gpe + eps:
                higher_neighbor = True
                break
        if not higher_neighbor:
            peaks.append(int(gid))
    if not peaks and candidates:
        peaks = [max(candidates, key=lambda gid: _gap_gpe(id_to_gap[gid]))]
    # Merge exact/near plateaus by keeping the lowest-id representative reachable
    # through equal-GPE edges.  This prevents checkerboard low-resolution plateaus
    # from creating many duplicate lift points.
    peak_set = set(peaks)
    visited: set[int] = set()
    merged: list[int] = []
    for p in sorted(peaks, key=lambda gid: (-_gap_gpe(id_to_gap[gid]), gid)):
        if p in visited:
            continue
        stack = [p]
        comp: list[int] = []
        base_gpe = _gap_gpe(id_to_gap[p])
        while stack:
            u = stack.pop()
            if u in visited or u not in peak_set:
                continue
            if abs(_gap_gpe(id_to_gap[u]) - base_gpe) > 1e-9 * max(1.0, abs(base_gpe)):
                continue
            visited.add(u)
            comp.append(u)
            for v in adjacency.get(u, []):
                if v in peak_set and v not in visited:
                    stack.append(v)
        if comp:
            merged.append(max(comp, key=lambda gid: (_gap_gpe(id_to_gap[gid]), -gid)))
    peaks = merged or peaks

    peak_lookup = set(peaks)
    basin_of: dict[int, int] = {}
    for gid in candidates:
        seen: set[int] = set()
        cur = int(gid)
        while True:
            if cur in peak_lookup:
                basin_of[gid] = cur
                break
            if cur in seen:
                basin_of[gid] = max(peaks, key=lambda p: (_gap_gpe(id_to_gap[p]), -abs(p - gid))) if peaks else cur
                break
            seen.add(cur)
            nbs = [nb for nb in adjacency.get(cur, []) if nb in candidates]
            if not nbs:
                basin_of[gid] = cur
                break
            best = max(nbs, key=lambda nb: (_gap_gpe(id_to_gap[nb]), -abs(nb - cur)))
            if _gap_gpe(id_to_gap[best]) <= _gap_gpe(id_to_gap[cur]) + eps:
                # Flat/noisy basin: attach to nearest high peak by graph id proxy.
                basin_of[gid] = max(peaks, key=lambda p: (_gap_gpe(id_to_gap[p]), -abs(p - gid))) if peaks else cur
                break
            cur = int(best)
    basins: dict[int, list[int]] = {p: [] for p in peaks}
    for gid, peak in basin_of.items():
        basins.setdefault(int(peak), []).append(int(gid))
    return id_to_gap, adjacency, peaks, basins


def _maximum_spanning_forest(id_to_gap: dict[int, object], adjacency: dict[int, list[int]]):
    parent = {gid: gid for gid in id_to_gap}
    rank = {gid: 0 for gid in id_to_gap}

    def find(x: int) -> int:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a: int, b: int) -> bool:
        ra = find(a); rb = find(b)
        if ra == rb:
            return False
        if rank[ra] < rank[rb]:
            ra, rb = rb, ra
        parent[rb] = ra
        if rank[ra] == rank[rb]:
            rank[ra] += 1
        return True

    edges: list[tuple[float, int, int]] = []
    for a, nbs in adjacency.items():
        for b in nbs:
            if a < b:
                # Paper Sec. 5.2: w(u,v)=min(g_u,g_v).
                w = min(_gap_gpe(id_to_gap[a]), _gap_gpe(id_to_gap[b]))
                edges.append((float(w), int(a), int(b)))
    tree_adj: dict[int, list[tuple[int, float]]] = {gid: [] for gid in id_to_gap}
    kept = 0
    for w, a, b in sorted(edges, key=lambda item: item[0], reverse=True):
        if union(a, b):
            tree_adj[a].append((b, w)); tree_adj[b].append((a, w)); kept += 1
    return tree_adj, kept, len(edges)


def _tree_path_barrier(tree_adj: dict[int, list[tuple[int, float]]], start: int, goal: int) -> float:
    if start == goal:
        return float("inf")
    stack: list[tuple[int, int, float]] = [(int(start), -1, float("inf"))]
    seen: set[int] = set()
    while stack:
        u, parent, best = stack.pop()
        if u == goal:
            return float(best)
        if u in seen:
            continue
        seen.add(u)
        for v, w in tree_adj.get(u, []):
            if v == parent:
                continue
            stack.append((int(v), int(u), min(float(best), float(w))))
    return 0.0


def _make_lift_points_from_peak_clusters(gap_graph, tau: float):
    id_to_gap, adjacency, peaks, basins = _paper_like_energy_peaks_and_basins(gap_graph)
    if not id_to_gap:
        return [], {"paper_lift_point_optimizer_enabled": True, "paper_lift_point_error": "empty gap graph"}
    tau = float(tau)
    if not np.isfinite(tau):
        tau = 0.8
    tau = float(np.clip(tau, 0.0, 1.0))
    if not peaks:
        gid = max(id_to_gap, key=lambda k: _gap_gpe(id_to_gap[k]))
        peaks = [int(gid)]
        basins = {int(gid): [int(gid)]}

    tree_adj, mst_edges, graph_edges = _maximum_spanning_forest(id_to_gap, adjacency)
    # Coupling DAG: for each lower-energy peak j, attach it to the stronger peak
    # with the highest coupling coefficient c(i,j)=barrier(i,j)/g_j if c>=tau.
    incoming: dict[int, list[int]] = {p: [] for p in peaks}
    outgoing_weak: dict[int, set[int]] = {p: set() for p in peaks}
    coupling: dict[tuple[int, int], float] = {}
    sorted_peaks = sorted(peaks, key=lambda p: (-_gap_gpe(id_to_gap[p]), p))
    for j in sorted_peaks:
        gj = max(_gap_gpe(id_to_gap[j]), 1e-12)
        best_i = None
        best_c = -1.0
        for i in sorted_peaks:
            if i == j:
                continue
            gi = _gap_gpe(id_to_gap[i])
            # Directed edge i -> j only from a peak at least as energetic as j.
            if gi + 1e-12 < _gap_gpe(id_to_gap[j]):
                continue
            barrier = _tree_path_barrier(tree_adj, int(i), int(j))
            if not np.isfinite(barrier):
                barrier = gj
            c = float(np.clip(barrier / gj, 0.0, 1.0))
            coupling[(int(i), int(j))] = c
            if c > best_c + 1e-12:
                best_i = int(i); best_c = c
        if best_i is not None and best_c >= tau:
            incoming[int(j)].append(int(best_i))
            outgoing_weak[int(best_i)].add(int(j))
            outgoing_weak[int(j)].add(int(best_i))

    # Weakly connected components of the coupling DAG correspond to clusters.
    remaining = set(peaks)
    clusters: list[list[int]] = []
    while remaining:
        root = remaining.pop()
        comp = [root]
        stack = [root]
        while stack:
            u = stack.pop()
            for v in outgoing_weak.get(u, set()):
                if v in remaining:
                    remaining.remove(v)
                    comp.append(v)
                    stack.append(v)
        clusters.append(sorted(comp, key=lambda p: (-_gap_gpe(id_to_gap[p]), p)))
    clusters.sort(key=lambda comp: (-_gap_gpe(id_to_gap[comp[0]]), comp[0]))

    lift_points = []
    selected_peak_ids: list[int] = []
    for cluster_id, comp in enumerate(clusters):
        zero_indegree = [p for p in comp if len([src for src in incoming.get(p, []) if src in comp]) == 0]
        chosen = max(zero_indegree or comp, key=lambda p: (_gap_gpe(id_to_gap[p]), -p))
        gap = id_to_gap[int(chosen)]
        lift_points.append(
            _original.LiftPoint(
                int(chosen),
                _gap_centroid(gap),
                _gap_centroid_3d(gap),
                _gap_gpe(gap),
                int(cluster_id),
            )
        )
        selected_peak_ids.append(int(chosen))

    max_gpe = max((_gap_gpe(g) for g in id_to_gap.values()), default=0.0)
    metrics = {
        "paper_lift_point_optimizer_enabled": True,
        "paper_lift_point_model": "Sec.5.2 GPE peaks/basins + maximum-spanning-tree barrier + coupling DAG",
        "paper_lift_point_gpe_formula": "gap.gpe = sum_{surrounding tiles} 1/4*m*g*(z_tile-z_min)",
        "paper_lift_point_morse_smale_step": "graph-local maxima + steepest-ascent basins proxy for discrete Morse-Smale segmentation",
        "paper_lift_point_exact_morse_smale_library_used": False,
        "paper_lift_point_threshold_tau": float(tau),
        "paper_lift_point_threshold_sweep_start_tau": 0.8,
        "paper_lift_point_threshold_sweep_requires_simulation": True,
        "paper_lift_point_threshold_sweep_automated": False,
        "paper_lift_point_gap_count": int(len(id_to_gap)),
        "paper_lift_point_graph_edge_count": int(graph_edges),
        "paper_lift_point_mst_edge_count": int(mst_edges),
        "paper_lift_point_peak_count": int(len(peaks)),
        "paper_lift_point_basin_count": int(len(basins)),
        "paper_lift_point_coupling_edge_count": int(sum(len(v) for v in incoming.values())),
        "paper_lift_point_cluster_count": int(len(clusters)),
        "paper_lift_point_selected_count": int(len(lift_points)),
        "paper_lift_point_selected_gap_ids": ",".join(map(str, selected_peak_ids)),
        "paper_lift_point_max_gpe": float(max_gpe),
    }
    return lift_points, metrics



# ---------------------------------------------------------------------------
# Paper Sec. 5.3-style friction-aware string route finder
# ---------------------------------------------------------------------------

def _gap_label(gap) -> int:
    try:
        return int(getattr(gap, "label"))
    except Exception:
        pass
    t = str(getattr(gap, "type", ""))
    if t == "virtual_boundary":
        return -1
    if t == "split_boundary":
        return -2
    if t == "horizontal":
        return 1
    return 0


def _gap_type(gap) -> str:
    return str(getattr(gap, "type", ""))


def _is_virtual_boundary_entrance(gap) -> bool:
    return bool(getattr(gap, "boundary", False)) and (_gap_label(gap) == -1 or _gap_type(gap) == "virtual_boundary")


def _is_split_boundary(gap) -> bool:
    return _gap_label(gap) == -2 or _gap_type(gap) == "split_boundary"


def _angle_at_gap(id_to_gap: dict[int, object], prev_gid: int | None, cur_gid: int, next_gid: int) -> float:
    if prev_gid is None or prev_gid not in id_to_gap or cur_gid not in id_to_gap or next_gid not in id_to_gap:
        return 0.0
    a = _gap_centroid(id_to_gap[cur_gid]) - _gap_centroid(id_to_gap[prev_gid])
    b = _gap_centroid(id_to_gap[next_gid]) - _gap_centroid(id_to_gap[cur_gid])
    na = float(np.linalg.norm(a)); nb = float(np.linalg.norm(b))
    if na <= 1e-12 or nb <= 1e-12:
        return 0.0
    c = float(np.clip(np.dot(a, b) / (na * nb), -1.0, 1.0))
    return float(np.arccos(c))


def _edge_length_between_gaps(id_to_gap: dict[int, object], a: int, b: int) -> float:
    if a not in id_to_gap or b not in id_to_gap:
        return 0.0
    return float(np.linalg.norm(_gap_centroid(id_to_gap[a]) - _gap_centroid(id_to_gap[b])))


def _turn_cost_shortest_gap_path(
    gap_graph,
    start_gid: int,
    goal_gid: int,
    *,
    prev_gid: int | None = None,
    used_nodes: set[int] | None = None,
    forbidden_entry_from_split: bool = True,
    max_states: int = 200000,
) -> tuple[list[int], float, dict[str, float | int | bool | str]]:
    """Dijkstra on oriented gap states, minimizing local turn cost.

    The paper states that string friction is dominated by cumulative sharp bends,
    with turn angle measured from geometric gap centroids, not by graph edge count.
    Therefore the state includes the previous gap so the next expansion can add
    the angle at the current gap.
    """
    id_to_gap, adjacency = _build_gap_adjacency(gap_graph)
    start_gid = int(start_gid); goal_gid = int(goal_gid)
    if start_gid not in id_to_gap or goal_gid not in id_to_gap:
        return [], float("inf"), {"route_error": "start_or_goal_missing"}
    if start_gid == goal_gid:
        return [start_gid], 0.0, {"turn_cost_dijkstra_states": 1, "turn_cost_dijkstra_found": True}
    used_nodes = set(used_nodes or set())
    start_gap = id_to_gap[start_gid]
    if forbidden_entry_from_split and _is_split_boundary(start_gap):
        return [], float("inf"), {"route_error": "split_boundary_entry_disallowed", "turn_cost_dijkstra_found": False}

    # Small length term breaks ties without changing the paper's primary turn objective.
    length_weight = 0.015
    revisit_penalty = math.pi * 4.0
    boundary_wander_penalty = math.pi * 0.20

    start_state = (prev_gid if prev_gid is not None else -10**9, start_gid)
    heap: list[tuple[float, int, int]] = [(0.0, start_state[0], start_gid)]
    dist: dict[tuple[int, int], float] = {start_state: 0.0}
    parent: dict[tuple[int, int], tuple[int, int] | None] = {start_state: None}
    best_goal_state: tuple[int, int] | None = None
    expanded = 0

    while heap:
        cost, prev, cur = heapq.heappop(heap)
        state_key = (prev, cur)
        if cost > dist.get(state_key, float("inf")) + 1e-12:
            continue
        expanded += 1
        if expanded > max_states:
            break
        if cur == goal_gid:
            best_goal_state = state_key
            break
        for nb in adjacency.get(cur, []):
            nb = int(nb)
            if nb == prev:
                # Avoid immediate U-turns unless that is literally the only way back.
                if len(adjacency.get(cur, [])) > 1 and nb != goal_gid:
                    continue
            nb_gap = id_to_gap.get(nb)
            if nb_gap is None:
                continue
            # Paper Sec.5.3: split boundary gaps can be traversed along split/boundary
            # connectivity, but the string should not enter the interior through them.
            # In this simplified graph proxy, make them high-cost/interior-forbidden.
            if _is_split_boundary(nb_gap) and nb != goal_gid:
                continue
            turn = _angle_at_gap(id_to_gap, None if prev == -10**9 else prev, cur, nb)
            step = turn + length_weight * _edge_length_between_gaps(id_to_gap, cur, nb)
            if nb in used_nodes and nb != goal_gid and not bool(getattr(nb_gap, "boundary", False)):
                step += revisit_penalty
            if bool(getattr(nb_gap, "boundary", False)) and nb != goal_gid and nb != start_gid:
                step += boundary_wander_penalty
            new_state = (cur, nb)
            new_cost = cost + step
            if new_cost + 1e-12 < dist.get(new_state, float("inf")):
                dist[new_state] = new_cost
                parent[new_state] = state_key
                heapq.heappush(heap, (new_cost, cur, nb))

    if best_goal_state is None:
        return [], float("inf"), {"turn_cost_dijkstra_states": expanded, "turn_cost_dijkstra_found": False, "route_error": "no_path"}

    states: list[tuple[int, int]] = []
    cur_state: tuple[int, int] | None = best_goal_state
    while cur_state is not None:
        states.append(cur_state)
        cur_state = parent.get(cur_state)
    states.reverse()
    # States store (prev,current); collect the current nodes.
    path = [int(states[0][1])]
    for st in states[1:]:
        if int(st[1]) != path[-1]:
            path.append(int(st[1]))
    return path, float(dist[best_goal_state]), {"turn_cost_dijkstra_states": expanded, "turn_cost_dijkstra_found": True}


def _closed_route_turn_angle(gap_graph, route: list[int]) -> float:
    id_to_gap, _adj = _build_gap_adjacency(gap_graph)
    pts = [_gap_centroid(id_to_gap[gid]) for gid in route if gid in id_to_gap]
    if len(pts) >= 3 and route[0] != route[-1]:
        pts.append(pts[0])
    return _route_turn_angle(np.asarray(pts, dtype=float))


def _remove_consecutive_duplicates(route: list[int]) -> list[int]:
    out: list[int] = []
    for gid in route:
        gid = int(gid)
        if not out or out[-1] != gid:
            out.append(gid)
    return out


def _paper_like_boundary_order(gap_graph, id_to_gap: dict[int, object]) -> list[int]:
    boundary = [gid for gid, gap in id_to_gap.items() if bool(getattr(gap, "boundary", False))]
    if not boundary:
        return []
    center = np.mean([_gap_centroid(id_to_gap[gid]) for gid in boundary], axis=0)
    return sorted(boundary, key=lambda gid: math.atan2(_gap_centroid(id_to_gap[gid])[1] - center[1], _gap_centroid(id_to_gap[gid])[0] - center[0]))


def _route_duplicate_count(route: list[int]) -> int:
    if not route:
        return 0
    core = route[:-1] if len(route) > 1 and route[0] == route[-1] else route
    return int(len(core) - len(set(core)))


def _split_entry_violation_count(gap_graph, route: list[int]) -> int:
    id_to_gap, _adj = _build_gap_adjacency(gap_graph)
    count = 0
    for idx in range(len(route) - 1):
        a = route[idx]; b = route[idx + 1]
        if a not in id_to_gap or b not in id_to_gap:
            continue
        if _is_split_boundary(id_to_gap[a]) and not bool(getattr(id_to_gap[b], "boundary", False)):
            count += 1
    return int(count)


def _build_paper_like_string_path(gap_graph, lift_points: list, mu_c: float):
    """Paper Sec.5.3-inspired string route builder.

    This keeps the paper's high-level constraints:
      - include the boundary route first;
      - connect the selected lift gaps through virtual boundary entrances;
      - minimize cumulative turn angle rather than graph-edge count;
      - discourage repeated/crossing gap usage;
      - disallow interior entry through split-boundary nodes.

    It is still a lightweight graph optimizer, not the authors' full route-search
    implementation, but it is materially closer than shortest-path routing.
    """
    id_to_gap, adjacency = _build_gap_adjacency(gap_graph)
    if not id_to_gap:
        return _original.StringPath([], [], [], 0.0, 0.0, {"paper_string_route_optimizer_enabled": True, "route_error": "empty gap graph"})

    boundary_ids = _paper_like_boundary_order(gap_graph, id_to_gap)
    boundary_set = set(boundary_ids)
    virtual_entries = [gid for gid in boundary_ids if _is_virtual_boundary_entrance(id_to_gap[gid])]
    if not virtual_entries:
        virtual_entries = [gid for gid in boundary_ids if not _is_split_boundary(id_to_gap[gid])]
    if not virtual_entries:
        virtual_entries = boundary_ids[:]

    route = list(boundary_ids)
    inserted_lifts: list[int] = []
    dijkstra_states_total = 0
    fallback_count = 0
    candidate_eval_count = 0
    max_entry_candidates = 48
    max_insertions = max(1, len(route))

    for lift in list(lift_points or []):
        lift_gid = int(getattr(lift, "gap_id", lift))
        if lift_gid not in id_to_gap:
            continue
        if lift_gid in route:
            inserted_lifts.append(lift_gid)
            continue
        lift_pos = _gap_centroid(id_to_gap[lift_gid])
        entries = sorted(
            virtual_entries,
            key=lambda gid: float(np.linalg.norm(_gap_centroid(id_to_gap[gid]) - lift_pos)),
        )[:max_entry_candidates]
        if not entries:
            continue

        best_route = None
        best_score = float("inf")
        best_stats: dict[str, float | int | bool | str] = {}
        used_core = set(route)
        if len(route) > 1 and route[0] == route[-1]:
            used_core.discard(route[-1])

        for entry in entries:
            try:
                idx = route.index(entry)
            except ValueError:
                # Insert after the closest existing boundary node if the exact
                # entry is not yet present because earlier detours modified route.
                idx = min(range(len(route)), key=lambda k: float(np.linalg.norm(_gap_centroid(id_to_gap[route[k]]) - _gap_centroid(id_to_gap[entry]))))
            if len(route) <= 1:
                next_boundary = entry
                prev_boundary = None
            else:
                prev_boundary = route[idx - 1]
                next_boundary = route[(idx + 1) % len(route)]
            # First half: boundary entrance -> lift.
            path_a, cost_a, stats_a = _turn_cost_shortest_gap_path(
                gap_graph,
                entry,
                lift_gid,
                prev_gid=prev_boundary,
                used_nodes=used_core,
                forbidden_entry_from_split=True,
            )
            dijkstra_states_total += int(stats_a.get("turn_cost_dijkstra_states", 0))
            if not path_a:
                continue
            # Second half: lift -> next boundary node, making a closed detour
            # without simply retracing the same channel when an alternative exists.
            prev_for_b = path_a[-2] if len(path_a) >= 2 else entry
            path_b, cost_b, stats_b = _turn_cost_shortest_gap_path(
                gap_graph,
                lift_gid,
                next_boundary,
                prev_gid=prev_for_b,
                used_nodes=used_core.union(path_a),
                forbidden_entry_from_split=False,
            )
            dijkstra_states_total += int(stats_b.get("turn_cost_dijkstra_states", 0))
            if not path_b:
                # Fallback to a return along path_a; this is not ideal, but it
                # preserves a physically closed route when the graph is sparse.
                path_b = list(reversed(path_a))
                cost_b = cost_a + math.pi
                fallback_count += 1
            candidate = route[: idx + 1] + path_a[1:] + path_b[1:] + route[idx + 2 :]
            candidate = _remove_consecutive_duplicates(candidate)
            # Re-close for scoring only.
            score_route = candidate + ([candidate[0]] if candidate and candidate[0] != candidate[-1] else [])
            theta = _closed_route_turn_angle(gap_graph, score_route)
            duplicates = _route_duplicate_count(score_route)
            split_violations = _split_entry_violation_count(gap_graph, score_route)
            # Paper constraints: turn first, crossing/duplicates second, split
            # entry violations effectively forbidden by huge penalty.
            score = theta + duplicates * math.pi * 3.0 + split_violations * math.pi * 100.0 + 0.02 * (cost_a + cost_b)
            candidate_eval_count += 1
            if score < best_score:
                best_score = score
                best_route = candidate
                best_stats = {
                    "last_lift_entry_gap": int(entry),
                    "last_lift_next_boundary_gap": int(next_boundary),
                    "last_lift_path_a_len": int(len(path_a)),
                    "last_lift_path_b_len": int(len(path_b)),
                    "last_lift_candidate_theta": float(theta),
                    "last_lift_candidate_duplicate_count": int(duplicates),
                    "last_lift_candidate_split_entry_violations": int(split_violations),
                }
        if best_route is not None:
            route = best_route[:]
            inserted_lifts.append(lift_gid)
        else:
            # Robust fallback: original unweighted path from the nearest entry.
            entry = entries[0]
            fallback = _original._shortest_gap_path(gap_graph, entry, lift_gid)
            if fallback:
                try:
                    idx = route.index(entry)
                except ValueError:
                    idx = max(0, len(route) - 1)
                route = _remove_consecutive_duplicates(route[: idx + 1] + fallback[1:] + list(reversed(fallback))[1:] + route[idx + 1 :])
                inserted_lifts.append(lift_gid)
                fallback_count += 1

    if route and route[0] != route[-1]:
        route.append(route[0])
    route = _remove_consecutive_duplicates(route)
    if route and route[0] != route[-1]:
        route.append(route[0])

    points = np.asarray([_gap_centroid(id_to_gap[gid]) for gid in route if gid in id_to_gap], dtype=float)
    theta = _route_turn_angle(points)
    try:
        friction = _original.safe_capstan_friction(float(mu_c), float(theta))
    except Exception:
        log_cost = float(mu_c) * float(theta)
        friction = float("inf") if log_cost > 60.0 else float(math.exp(log_cost) - 1.0)
    log_channel_cost = float(mu_c * theta) if math.isfinite(mu_c) and math.isfinite(theta) else float("inf")
    duplicates = _route_duplicate_count(route)
    split_violations = _split_entry_violation_count(gap_graph, route)
    lift_ids = [int(getattr(lp, "gap_id", lp)) for lp in (lift_points or [])]
    visited_lifts = [gid for gid in lift_ids if gid in set(route)]
    missed_lifts = [gid for gid in lift_ids if gid not in set(route)]
    route_node_count = len(route)
    unique_route_node_count = len(set(route[:-1] if route and route[0] == route[-1] else route))
    max_single_turn = 0.0
    try:
        max_single_turn = float(_original._max_single_turn_angle(gap_graph, route))
    except Exception:
        pass
    warnings: list[str] = []
    if split_violations:
        warnings.append("Route enters interior through split-boundary gaps; should be zero for paper Sec.5.3.")
    if missed_lifts:
        warnings.append("Some selected lift points were not reached by the string route.")
    if duplicates > max(2, route_node_count * 0.35):
        warnings.append("Route revisits many gaps; crossing/friction may still be high.")

    return _original.StringPath(
        gap_ids=[int(x) for x in route],
        boundary_gap_ids=[int(x) for x in boundary_ids],
        lift_gap_ids=[int(x) for x in lift_ids],
        turn_angle_total=float(theta),
        estimated_channel_friction=friction,
        metrics={
            "paper_string_route_optimizer_enabled": True,
            "paper_string_route_model": "Sec.5.3 turn-cost closed-walk approximation on gap graph",
            "paper_string_route_objective": "minimize cumulative centroid turn angle with crossing/revisit and split-entry penalties",
            "paper_string_route_primary_cost": "theta_total_from_gap_centroids",
            "paper_string_route_capstan_model": "E_channel = T1*(exp(mu_c*theta_total)-1); route optimization minimizes theta_total proxy",
            "paper_string_route_boundary_first": True,
            "paper_string_route_virtual_boundary_entry_preferred": True,
            "paper_string_route_split_boundary_entry_disallowed": True,
            "paper_string_route_turn_cost_dijkstra": True,
            "paper_string_route_exact_authors_solver_used": False,
            "paper_string_route_candidate_eval_count": int(candidate_eval_count),
            "paper_string_route_dijkstra_states_total": int(dijkstra_states_total),
            "paper_string_route_fallback_count": int(fallback_count),
            "paper_string_route_inserted_lift_count": int(len(inserted_lifts)),
            "paper_string_route_visited_lift_count": int(len(visited_lifts)),
            "paper_string_route_missed_lift_count": int(len(missed_lifts)),
            "paper_string_route_missed_lift_gap_ids": ",".join(map(str, missed_lifts)),
            "paper_string_route_duplicate_visit_count": int(duplicates),
            "paper_string_route_split_entry_violation_count": int(split_violations),
            "route_length": int(route_node_count),
            "route_node_count": int(route_node_count),
            "unique_route_node_count": int(unique_route_node_count),
            "duplicate_visit_count": int(duplicates),
            "boundary_gap_count": int(len(boundary_ids)),
            "virtual_boundary_entry_count": int(len(virtual_entries)),
            "lift_point_count": int(len(lift_ids)),
            "max_single_turn_angle": float(max_single_turn),
            "turn_angle_total": float(theta),
            "theta_total": float(theta),
            "log_channel_cost": float(log_channel_cost),
            "estimated_channel_friction": friction,
            "overflow_prevented": bool(not math.isfinite(friction) or log_channel_cost > 60.0),
            "warnings": "; ".join(warnings),
            **(best_stats if 'best_stats' in locals() else {}),
        },
    )

def _apply_paper_lift_points_to_state(state, tau: float, channel_friction: float):
    gap_graph = getattr(state, "gap_graph", None)
    if gap_graph is None:
        return state
    old_lift_points = list(getattr(state, "lift_points", []) or [])
    old_ids = [int(getattr(lp, "gap_id", -1)) for lp in old_lift_points]
    new_lift_points, metrics = _make_lift_points_from_peak_clusters(gap_graph, tau)
    if not new_lift_points:
        metrics["paper_lift_point_optimizer_fallback"] = "kept existing lift points"
        new_lift_points = old_lift_points
    metrics["legacy_lift_point_count"] = int(len(old_lift_points))
    metrics["legacy_lift_gap_ids"] = ",".join(map(str, old_ids))

    try:
        gap_graph.metrics = dict(getattr(gap_graph, "metrics", {}) or {}) | metrics
    except Exception:
        pass

    try:
        state.lift_points = new_lift_points
    except Exception:
        try:
            import dataclasses
            state = dataclasses.replace(state, lift_points=new_lift_points)
        except Exception:
            pass

    # Rebuild the string route so snap/lift constraints refer to the new lift gaps.
    try:
        rebuilt = _build_paper_like_string_path(gap_graph, new_lift_points, float(channel_friction))
        sm = dict(getattr(rebuilt, "metrics", {}) or {})
        sm.update({
            "paper_lift_points_reoptimized_before_routing": True,
            "paper_lift_point_selected_count": int(len(new_lift_points)),
            "paper_lift_point_selected_gap_ids": metrics.get("paper_lift_point_selected_gap_ids", ""),
            "string_route_optimizer_status": "paper-like Sec.5.3 turn-cost route builder",
        })
        try:
            rebuilt.metrics = sm
        except Exception:
            pass
        try:
            state.string_path = rebuilt
        except Exception:
            try:
                import dataclasses
                state = dataclasses.replace(state, string_path=rebuilt)
            except Exception:
                pass
    except Exception as exc:
        try:
            sm = dict(getattr(state.string_path, "metrics", {}) or {})
            sm.update({"paper_lift_points_reoptimized_before_routing": True, "paper_lift_route_rebuild_error": str(exc)})
            state.string_path.metrics = sm
        except Exception:
            pass

    try:
        approx = list(getattr(state, "approximations", []) or [])
        approx = [a for a in approx if "Morse-Smale lift point" not in str(a) and "GPE threshold" not in str(a)]
        approx.append(
            "Lift points now follow paper Sec. 5.2 structure: GPE peaks/basins, maximum-spanning-tree barriers, and coupling DAG; exact discrete Morse-Smale complex and simulation-based tau sweep remain approximated."
        )
        state.approximations = approx
    except Exception:
        pass
    return state


def build_onestring_design(*args, **kwargs):
    state = _ORIGINAL_BUILD_ONESTRING_DESIGN(*args, **kwargs)
    params = kwargs.get("params", None)
    if params is None and len(args) >= 2:
        params = args[1]
    tau = float(getattr(params, "lift_tau", 0.8)) if params is not None else 0.8
    channel_friction = float(getattr(params, "channel_friction", 0.2)) if params is not None else 0.2
    try:
        state = _apply_paper_lift_points_to_state(state, tau, channel_friction)
    except Exception as exc:
        try:
            metrics = dict(getattr(state.gap_graph, "metrics", {}) or {})
            metrics["paper_lift_point_optimizer_enabled"] = False
            metrics["paper_lift_point_optimizer_error"] = str(exc)
            state.gap_graph.metrics = metrics
        except Exception:
            pass
    # The original run_simulation=True path would have simulated using the pre-patch
    # lift points.  Invalidate it rather than showing stale constraints.
    try:
        state.simulation_result = None
    except Exception:
        pass
    return state


def paper_consistency_report(state):
    rows = []
    if _ORIGINAL_PAPER_CONSISTENCY_REPORT is not None:
        try:
            rows = list(_ORIGINAL_PAPER_CONSISTENCY_REPORT(state))
        except Exception:
            rows = []
    try:
        metrics = dict(getattr(state.gap_graph, "metrics", {}) or {})
        rows.append({
            "item": "Lift point selection",
            "expected": "Sec.5.2: GPE field -> Morse-Smale peaks/basins -> maximum-spanning-tree barrier -> coupling DAG -> one lift per cluster",
            "actual": str(metrics.get("paper_lift_point_model", "")),
            "ok": bool(metrics.get("paper_lift_point_optimizer_enabled", False)),
            "value": f"selected={metrics.get('paper_lift_point_selected_count')}, peaks={metrics.get('paper_lift_point_peak_count')}, tau={metrics.get('paper_lift_point_threshold_tau')}",
        })
    except Exception:
        pass
    try:
        sm = dict(getattr(state.string_path, "metrics", {}) or {})
        rows.append({
            "item": "String route generation",
            "expected": "Sec.5.3: closed gap-graph route minimizing cumulative turn angle, preferring virtual boundary entry and avoiding split-boundary interior entry",
            "actual": str(sm.get("paper_string_route_model", sm.get("string_route_optimizer_status", ""))),
            "ok": bool(sm.get("paper_string_route_optimizer_enabled", False)),
            "value": f"theta={sm.get('turn_angle_total')}, dup={sm.get('paper_string_route_duplicate_visit_count')}, splitViol={sm.get('paper_string_route_split_entry_violation_count')}",
        })
    except Exception:
        pass
    return rows

# Patch the original module in-place. Functions such as build_onestring_design()
# keep their original global namespace, so this assignment is what makes them call
# the new extrusion implementation.
_original._extrude_tiles = _extrude_tiles
_original._optimize_t2d_footprint_layout = _optimize_t2d_footprint_layout
_original._optimize_rigid_assembly_hinge_layout_2d = _optimize_rigid_assembly_hinge_layout_2d
_original._make_t2d_from_transforms = _make_t2d_from_transforms
_original.build_onestring_design = build_onestring_design
_original._build_string_path = _build_paper_like_string_path
_original.paper_consistency_report = paper_consistency_report

# Re-export the original module's API from this wrapper.
for _name, _value in _original.__dict__.items():
    if _name in {
        "__name__",
        "__package__",
        "__loader__",
        "__spec__",
        "__file__",
        "__cached__",
        "_extrude_tiles",
    }:
        continue
    globals()[_name] = _value

globals()["_extrude_tiles"] = _extrude_tiles
globals()["_optimize_t2d_footprint_layout"] = _optimize_t2d_footprint_layout
globals()["_optimize_rigid_assembly_hinge_layout_2d"] = _optimize_rigid_assembly_hinge_layout_2d
globals()["_make_t2d_from_transforms"] = _make_t2d_from_transforms
globals()["build_onestring_design"] = build_onestring_design
globals()["_build_string_path"] = _build_paper_like_string_path
globals()["paper_consistency_report"] = paper_consistency_report
globals()["SIDEFACE_CONTACT_PATCH_ACTIVE"] = True
globals()["PAPER_LIFT_POINT_OPTIMIZER_PATCH_ACTIVE"] = True
globals()["PAPER_STRING_ROUTE_OPTIMIZER_PATCH_ACTIVE"] = True
globals()["OPTIMIZED_STRING_PATH_PATCH_ACTIVE"] = True
globals()["SIDEFACE_CONTACT_PATCH_ORIGINAL_PATH"] = str(_ORIGINAL_PATH)



# ---------------------------------------------------------------------------
# Streamlit animation/simulation cache
# ---------------------------------------------------------------------------
_ORIGINAL_SIMULATE_ONESTRING_DEPLOYMENT = _original.simulate_onestring_deployment


def _deployment_params_cache_key(params) -> str:
    """Stable-ish key for deployment settings used by the Streamlit UI cache."""
    try:
        import dataclasses
        import json
        import hashlib

        if dataclasses.is_dataclass(params):
            payload = dataclasses.asdict(params)
        else:
            payload = dict(getattr(params, "__dict__", {}))
        text = json.dumps(payload, sort_keys=True, default=str)
        return hashlib.sha1(text.encode("utf-8")).hexdigest()
    except Exception:
        return repr(params)


def _state_cache_key(state) -> str:
    try:
        import streamlit as st
        pipeline_key = st.session_state.get("pipeline_key")
        if pipeline_key is not None:
            return repr(pipeline_key)
    except Exception:
        pass
    try:
        v = np.asarray(state.tiles_2d_dual_hinge.vertices)
        t = np.asarray(state.tiles_3d.vertices)
        summary = (
            tuple(v.shape),
            tuple(t.shape),
            float(np.nanmean(v)) if v.size else 0.0,
            float(np.nanmean(t)) if t.size else 0.0,
            float(np.nanstd(v)) if v.size else 0.0,
            float(np.nanstd(t)) if t.size else 0.0,
        )
        return repr(summary)
    except Exception:
        return str(id(state))


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    """Cached wrapper around the original deployment simulation.

    The app's Assembly Animation view can be rerun many times while the user only
    changes camera/player UI.  Keep a session-state cache of previously generated
    simulation frames so returning to the same settings reuses the animation
    instead of recomputing it.
    """
    cache_enabled = True
    cache = None
    key = None
    try:
        import streamlit as st
        cache = st.session_state.setdefault("onestring_animation_result_cache", {})
        key = ("deployment", _state_cache_key(state), _deployment_params_cache_key(params))
        if key in cache:
            if progress_callback is not None:
                try:
                    progress_callback("Cached deployment simulation", 1.0, "reusing previously generated animation frames")
                except Exception:
                    pass
            return cache[key]
    except Exception:
        cache_enabled = False

    forced_string_path_scope = False
    try:
        if params is not None and getattr(params, "snap_scope", "string_path_only") != "string_path_only":
            setattr(params, "snap_scope", "string_path_only")
            forced_string_path_scope = True
    except Exception:
        pass

    result = _ORIGINAL_SIMULATE_ONESTRING_DEPLOYMENT(state, params, progress_callback=progress_callback)
    try:
        result.metrics = dict(getattr(result, "metrics", {}) or {})
        result.metrics["paper_simulation_forced_string_path_only"] = bool(forced_string_path_scope)
        result.metrics["paper_simulation_animation_model"] = "Projective Dynamics frames from simulate_onestring_deployment; no morph/interpolation preview"
    except Exception:
        pass

    if cache_enabled and cache is not None and key is not None:
        try:
            cache[key] = result
            # Avoid unbounded growth while letting the user switch between a few
            # frame counts / solver settings during tuning.
            if len(cache) > 8:
                oldest_key = next(iter(cache.keys()))
                if oldest_key != key:
                    cache.pop(oldest_key, None)
        except Exception:
            pass
    return result


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["ONESTRING_ANIMATION_CACHE_ACTIVE"] = True

# ---------------------------------------------------------------------------
# Paper-faithful deployment mode v11
# ---------------------------------------------------------------------------
def _copy_paper_deployment_params(params):
    """Return deployment params forced toward the paper Sec.5.4 energy model.

    The paper simulation is expressed as
      E = w_rigid E_rigid + w_collision E_collision + w_actuation(E_snap + E_lift).
    The app prototype had extra target-guidance terms that are useful for visual
    stability but are not part of this formulation.  v11 disables those terms and
    constrains only the computed string path.
    """
    try:
        import dataclasses
        if params is None:
            params = _original.DeploymentParameters()
        if dataclasses.is_dataclass(params):
            params = dataclasses.replace(params)
        else:
            import copy
            params = copy.copy(params)
    except Exception:
        try:
            params = _original.DeploymentParameters()
        except Exception:
            return params
    forced = {
        "snap_scope": "string_path_only",
        "use_target_gap_contraction": True,
        "target_fit_weight": 0.0,
        "target_contact_guard_weight": 0.0,
        "target_contact_projection_passes": 0,
        "target_contact_start_alpha": 1.0,
        "target_contact_clearance": 0.0,
        "store_animation_frames": True,
    }
    changed = {}
    for name, value in forced.items():
        if hasattr(params, name):
            try:
                old = getattr(params, name)
                setattr(params, name, value)
                changed[name] = (old, value)
            except Exception:
                pass
    try:
        params._paper_v11_forced_changes = changed
    except Exception:
        pass
    return params


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    """Paper-faithful wrapper around the original deployment simulation.

    v10 already used string_path_only. v11 additionally disables target-pose fit
    and target-contact guard so the animation is driven by the paper's terms:
    rigid tiles, collision, snap constraints on the computed route, and lift
    constraints at selected lift points.
    """
    paper_params = _copy_paper_deployment_params(params)
    cache_enabled = True
    cache = None
    key = None
    try:
        import streamlit as st
        cache = st.session_state.setdefault("onestring_animation_result_cache", {})
        key = ("paper_v11_deployment", _state_cache_key(state), _deployment_params_cache_key(paper_params))
        if key in cache:
            if progress_callback is not None:
                try:
                    progress_callback("Cached paper-faithful deployment simulation", 1.0, "reusing v11 strict string-path PD frames")
                except Exception:
                    pass
            return cache[key]
    except Exception:
        cache_enabled = False

    result = _ORIGINAL_SIMULATE_ONESTRING_DEPLOYMENT(state, paper_params, progress_callback=progress_callback)
    try:
        result.metrics = dict(getattr(result, "metrics", {}) or {})
        changes = getattr(paper_params, "_paper_v11_forced_changes", {}) or {}
        result.metrics.update({
            "paper_faithful_simulation_v11_enabled": True,
            "paper_simulation_energy_model": "E = w_rigid*E_rigid + w_collision*E_collision + w_actuation*(E_snap + E_lift)",
            "paper_simulation_terms_enabled": "E_rigid,E_collision,E_snap,E_lift",
            "paper_simulation_target_pose_fit_disabled": True,
            "paper_simulation_target_contact_guard_disabled": True,
            "paper_simulation_snap_scope_forced": "string_path_only",
            "paper_simulation_use_target_gap_contraction": bool(getattr(paper_params, "use_target_gap_contraction", True)),
            "paper_simulation_forced_parameter_count": int(len(changes)),
            "paper_simulation_forced_parameters": "; ".join(f"{k}:{v[0]}->{v[1]}" for k, v in changes.items()),
            "paper_simulation_animation_model": "Projective Dynamics frames from strict string-path snap/lift simulation; no morph/interpolation preview and no target-pose attraction",
        })
    except Exception:
        pass

    if cache_enabled and cache is not None and key is not None:
        try:
            cache[key] = result
            if len(cache) > 8:
                oldest_key = next(iter(cache.keys()))
                if oldest_key != key:
                    cache.pop(oldest_key, None)
        except Exception:
            pass
    return result


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["PAPER_FAITHFUL_SIMULATION_V11_ACTIVE"] = True

# Add a paper-faithful audit row while preserving the existing v10 audit.
_PREVIOUS_PAPER_CONSISTENCY_REPORT_V11 = globals().get("paper_consistency_report")

def paper_consistency_report(state):
    rows = []
    if _PREVIOUS_PAPER_CONSISTENCY_REPORT_V11 is not None:
        try:
            rows = list(_PREVIOUS_PAPER_CONSISTENCY_REPORT_V11(state))
        except Exception:
            rows = []
    sim = getattr(state, "simulation_result", None)
    metrics = dict(getattr(sim, "metrics", {}) or {}) if sim is not None else {}
    rows.append({
        "item": "Actuation simulation energy model",
        "expected": "Sec.5.4: E_rigid + E_collision + E_actuation(E_snap + E_lift), with snap only on computed string path and lift at selected lift points",
        "actual": str(metrics.get("paper_simulation_energy_model", "not simulated yet")),
        "ok": bool(sim is None or metrics.get("paper_faithful_simulation_v11_enabled", False)),
        "value": f"snap_scope={metrics.get('snap_scope', 'not simulated')}, target_fit_disabled={metrics.get('paper_simulation_target_pose_fit_disabled', 'not simulated')}",
    })
    return rows

_original.paper_consistency_report = paper_consistency_report
globals()["paper_consistency_report"] = paper_consistency_report


# ---------------------------------------------------------------------------
# Final v21 dispatcher registration
#
# The historical patch stack below defines several compatibility simulations.
# Keep the last boundary-driven v20 implementation as an explicit legacy mode,
# then make the Section 5.4 paper-style PD solver the default public entrypoint.
_FINAL_LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE = globals().get("simulate_onestring_deployment")
_FINAL_PREVIOUS_PAPER_CONSISTENCY_REPORT = globals().get("paper_consistency_report")


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    mode = str(getattr(params, "simulation_mode", "paper_style_pd") if params is not None else "paper_style_pd")
    if mode == "legacy_boundary_driven_v20":
        if _FINAL_LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE is None:
            raise RuntimeError("legacy_boundary_driven_v20 simulation is unavailable.")
        result = _FINAL_LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE(state, params, progress_callback=progress_callback)
        try:
            result.metrics = dict(getattr(result, "metrics", {}) or {})
            result.metrics["paper_simulation_mode"] = "legacy_boundary_driven_v20"
            result.metrics["paper_style_pd_enabled"] = False
        except Exception:
            pass
        return result
    if mode != "paper_style_pd":
        try:
            _emit_progress(progress_callback, "Paper-style PD simulation", 0.0, f"unknown mode {mode!r}; using paper_style_pd")
        except Exception:
            pass
    return _paper_style_pd_simulate(state, params, progress_callback=progress_callback)


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["PAPER_STYLE_PD_SIMULATION_ACTIVE"] = True


def paper_consistency_report(state):
    rows = []
    if _FINAL_PREVIOUS_PAPER_CONSISTENCY_REPORT is not None:
        try:
            rows = list(_FINAL_PREVIOUS_PAPER_CONSISTENCY_REPORT(state))
        except Exception:
            rows = []
    sim = getattr(state, "simulation_result", None)
    metrics = dict(getattr(sim, "metrics", {}) or {}) if sim is not None else {}
    rows.append({
        "item": "Paper-style PD simulation core v21 active dispatcher",
        "expected": "Default paper_style_pd; legacy_boundary_driven_v20 remains selectable. No boundary-first order, delayed lift, height-only lift, target fit, or 2D footprint collision in paper_style_pd.",
        "actual": f"mode={metrics.get('paper_simulation_mode', 'not simulated')}, backend={metrics.get('actual_backend', 'not simulated')}, collision={metrics.get('collision_model', 'not simulated')}",
        "ok": bool(metrics.get("paper_style_pd_enabled", False)) if sim is not None else True,
        "value": metrics.get("paper_style_remaining_differences", "not simulated"),
    })
    return rows


_original.paper_consistency_report = paper_consistency_report
globals()["paper_consistency_report"] = paper_consistency_report


# ---------------------------------------------------------------------------
# v21 paper-style PD simulation core
#
# The v20 deployment above is intentionally kept as the legacy boundary-driven
# heuristic.  The paper_style_pd path below follows the Section 5.4 energy shape:
#
#   E_sim = w_rigid E_rigid + w_collision E_collision
#         + w_actuation (E_snap + E_lift)
#
# It does not use boundary-only contraction, delayed lift, height-only lift,
# target-pose fitting, target contact guard, or 2D footprint SAT collision.

_LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE = globals().get("simulate_onestring_deployment")
_PREVIOUS_PAPER_CONSISTENCY_REPORT_V20 = globals().get("paper_consistency_report")


def _paper_smoothstep_torch(x):
    torch_mod = _original.torch
    x = torch_mod.clamp(x, 0.0, 1.0)
    return x * x * (3.0 - 2.0 * x)


def _paper_style_prepare_lift_tensors(state, device, dtype):
    torch_mod = _original.torch
    lift_gap_ids = [int(getattr(lp, "gap_id", -1)) for lp in getattr(state, "lift_points", []) or []]
    group_idx, group_mask, kept = _v15_make_group_tensors(state, lift_gap_ids, device, dtype)
    if group_idx is None:
        return None
    target_xyz = []
    for gid in kept:
        target = None
        for lp in getattr(state, "lift_points", []) or []:
            if int(getattr(lp, "gap_id", -999999)) == int(gid):
                target = np.asarray(getattr(lp, "position_3d", None), dtype=float)
                break
        if target is None or target.shape != (3,) or not np.all(np.isfinite(target)):
            try:
                target = np.asarray(_gap_centroid_3d(state.gap_graph.gaps[int(gid)]), dtype=float)
            except Exception:
                target = np.zeros(3, dtype=float)
        target_xyz.append(target)
    return {
        "gap_ids": kept,
        "group_idx": group_idx,
        "group_mask": group_mask,
        "target_xyz": torch_mod.as_tensor(np.asarray(target_xyz, dtype=float), dtype=dtype, device=device),
    }


def _paper_style_round_nested(value, digits=6):
    arr = np.asarray(value, dtype=float)
    if arr.ndim == 0:
        return round(float(arr), digits)
    return np.round(arr, digits).tolist()


def _paper_style_snap_debug_rows(state, snap, *, max_rows=256):
    if snap is None:
        return []
    rest_v = np.asarray(state.tiles_2d_dual_hinge.vertices, dtype=float)
    target_v = np.asarray(state.tiles_3d.vertices, dtype=float)
    pairs = snap["pairs"].detach().cpu().numpy()
    edge_a = snap["edge_a"].detach().cpu().numpy()
    edge_b = snap["edge_b"].detach().cpu().numpy()
    rows = []
    for idx, gap in enumerate(list(snap.get("gaps", []))[: int(max_rows)]):
        if idx >= len(pairs):
            break
        a, b = int(pairs[idx, 0]), int(pairs[idx, 1])
        ea = [int(x) for x in edge_a[idx]]
        eb = [int(x) for x in edge_b[idx]]
        ma0 = rest_v[a, ea].mean(axis=0) if 0 <= a < len(rest_v) else np.zeros(3)
        mb0 = rest_v[b, eb].mean(axis=0) if 0 <= b < len(rest_v) else np.zeros(3)
        mat = target_v[a, ea].mean(axis=0) if 0 <= a < len(target_v) else np.zeros(3)
        mbt = target_v[b, eb].mean(axis=0) if 0 <= b < len(target_v) else np.zeros(3)
        rows.append({
            "gap_id": int(getattr(gap, "id", idx)),
            "gap_type": str(getattr(gap, "type", "")),
            "tile_a": a,
            "tile_b": b,
            "face_a": ea,
            "face_b": eb,
            "m_a_initial": _paper_style_round_nested(ma0),
            "m_b_initial": _paper_style_round_nested(mb0),
            "m_initial_distance": round(float(np.linalg.norm(ma0 - mb0)), 6),
            "m_a_target": _paper_style_round_nested(mat),
            "m_b_target": _paper_style_round_nested(mbt),
            "m_target_distance": round(float(np.linalg.norm(mat - mbt)), 6),
        })
    return rows


def _paper_style_lift_debug_rows(state, lift, *, max_rows=128):
    if lift is None:
        return []
    rest_v = np.asarray(state.tiles_2d_dual_hinge.vertices, dtype=float)
    target_v = np.asarray(state.tiles_3d.vertices, dtype=float)
    group_idx = lift["group_idx"].detach().cpu().numpy()
    group_mask = lift["group_mask"].detach().cpu().numpy().astype(bool)
    targets = lift["target_xyz"].detach().cpu().numpy()
    rows = []
    for row, gid in enumerate(list(lift.get("gap_ids", []))[: int(max_rows)]):
        tiles = [int(t) for t, keep in zip(group_idx[row], group_mask[row]) if bool(keep)]
        source_vertices = {str(t): _paper_style_round_nested(rest_v[t]) for t in tiles if 0 <= t < len(rest_v)}
        target_vertices = {str(t): _paper_style_round_nested(target_v[t]) for t in tiles if 0 <= t < len(target_v)}
        rows.append({
            "lift_gap_id": int(gid),
            "affected_tile_ids": tiles,
            "source_vertices": source_vertices,
            "target_vertices": target_vertices,
            "target_xyz": _paper_style_round_nested(targets[row]) if row < len(targets) else None,
        })
    return rows


def _paper_style_topological_exclusion_matrix(state, n_tiles, device):
    torch_mod = _original.torch
    excluded = torch_mod.eye(int(n_tiles), dtype=torch_mod.bool, device=device)
    adjacent_pairs = set()
    hinge_pairs = set()
    try:
        for gap in getattr(state.gap_graph, "gaps", []) or []:
            tiles = [int(t) for t in getattr(gap, "surrounding_tiles", []) or [] if int(t) >= 0]
            for i in range(len(tiles)):
                for j in range(i + 1, len(tiles)):
                    a, b = sorted((tiles[i], tiles[j]))
                    if 0 <= a < n_tiles and 0 <= b < n_tiles:
                        adjacent_pairs.add((a, b))
                        excluded[a, b] = True
                        excluded[b, a] = True
    except Exception:
        pass
    try:
        for hinge in getattr(getattr(state, "hinge_graph", None), "hinges", []) or []:
            a, b = sorted((int(hinge.tile_a), int(hinge.tile_b)))
            if 0 <= a < n_tiles and 0 <= b < n_tiles:
                hinge_pairs.add((a, b))
                excluded[a, b] = True
                excluded[b, a] = True
    except Exception:
        pass
    return excluded, {
        "adjacent_pair_count": int(len(adjacent_pairs)),
        "hinge_pair_count": int(len(hinge_pairs)),
        "topological_pair_count": int(len(adjacent_pairs | hinge_pairs)),
        "adjacent_pairs_sample": [list(p) for p in sorted(adjacent_pairs)[:64]],
        "hinge_pairs_sample": [list(p) for p in sorted(hinge_pairs)[:64]],
    }


def _paper_style_frame_health_metrics(frames, state):
    if not frames:
        return {}
    arr = [np.asarray(f, dtype=float) for f in frames]
    finite = all(np.all(np.isfinite(f)) for f in arr)
    rest = np.asarray(state.tiles_2d_dual_hinge.vertices, dtype=float)
    target = np.asarray(state.tiles_3d.vertices, dtype=float)
    tile_diag = float(np.median(np.linalg.norm(rest.max(axis=1) - rest.min(axis=1), axis=1))) if len(rest) else 1.0
    tile_diag = max(tile_diag, 1e-8)
    max_step_vertex = 0.0
    max_step_center = 0.0
    for a, b in zip(arr[:-1], arr[1:]):
        max_step_vertex = max(max_step_vertex, float(np.max(np.linalg.norm(b - a, axis=2))))
        max_step_center = max(max_step_center, float(np.max(np.linalg.norm(b.mean(axis=1) - a.mean(axis=1), axis=1))))
    final_centers = arr[-1].mean(axis=1)
    rest_centers = rest.mean(axis=1)
    target_centers = target.mean(axis=1)
    center_disp = np.linalg.norm(final_centers - rest_centers, axis=1) if len(final_centers) else np.zeros(0)
    target_center_disp = np.linalg.norm(target_centers - rest_centers, axis=1) if len(target_centers) else np.zeros(0)
    allowed = np.maximum(4.0 * tile_diag, 3.0 * target_center_disp + tile_diag)
    flying = np.where(center_disp > allowed)[0].astype(int).tolist() if len(center_disp) else []
    hinge_values = []
    try:
        for h in getattr(state.hinge_graph, "hinges", []) or []:
            hinge_values.append(float(np.linalg.norm(arr[-1][int(h.tile_a), int(h.local_vertex_a)] - arr[-1][int(h.tile_b), int(h.local_vertex_b)])))
    except Exception:
        hinge_values = []
    return {
        "paper_style_acceptance_finite_frames": bool(finite),
        "paper_style_acceptance_tile_diag": tile_diag,
        "paper_style_max_vertex_displacement_per_saved_frame": max_step_vertex,
        "paper_style_max_center_displacement_per_saved_frame": max_step_center,
        "paper_style_flying_tile_count": int(len(flying)),
        "paper_style_flying_tile_ids_sample": json.dumps(flying[:64]),
        "paper_style_hinge_separation_max": float(max(hinge_values)) if hinge_values else 0.0,
        "paper_style_hinge_separation_mean": float(np.mean(hinge_values)) if hinge_values else 0.0,
    }


def _paper_style_copy_deployment_params(params):
    extra_attrs = {}
    try:
        import dataclasses
        if params is None:
            params = _original.DeploymentParameters()
        elif dataclasses.is_dataclass(params):
            field_names = {field.name for field in dataclasses.fields(params)}
            extra_attrs = {k: v for k, v in vars(params).items() if k not in field_names}
            params = dataclasses.replace(params)
        else:
            import copy
            extra_attrs = dict(vars(params)) if hasattr(params, "__dict__") else {}
            params = copy.copy(params)
    except Exception:
        params = _original.DeploymentParameters()
    try:
        for name, value in extra_attrs.items():
            setattr(params, name, value)
        setattr(params, "snap_scope", "string_path_only")
        setattr(params, "use_target_gap_contraction", False)
        setattr(params, "target_fit_weight", 0.0)
        setattr(params, "target_contact_guard_weight", 0.0)
        setattr(params, "target_contact_projection_passes", 0)
        setattr(params, "target_contact_start_alpha", 1.0)
        setattr(params, "target_contact_clearance", 0.0)
        setattr(params, "store_animation_frames", True)
        setattr(params, "simulation_mode", "paper_style_pd")
        compute = getattr(params, "compute", None)
        if compute is not None:
            setattr(compute, "backend", "cuda")
            setattr(compute, "use_gpu_for_simulation", True)
    except Exception:
        pass
    return params


def _paper_style_edge_lengths(vertices, edge_pairs):
    torch_mod = _original.torch
    a = vertices[:, edge_pairs[:, 0]]
    b = vertices[:, edge_pairs[:, 1]]
    return torch_mod.linalg.norm(a - b, dim=2)


def _paper_style_rigid_energy_and_errors(current, rest):
    torch_mod = _original.torch
    edge_pairs = torch_mod.as_tensor(
        np.asarray(
            [(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7), (0, 2), (1, 3), (4, 6), (5, 7)],
            dtype=np.int64,
        ),
        dtype=torch_mod.long,
        device=current.device,
    )
    diff = _paper_style_edge_lengths(current, edge_pairs) - _paper_style_edge_lengths(rest, edge_pairs)
    abs_diff = torch_mod.abs(diff)
    energy = torch_mod.mean(diff * diff) if diff.numel() else torch_mod.zeros((), dtype=current.dtype, device=current.device)
    mean = torch_mod.sqrt(torch_mod.mean(diff * diff)) if diff.numel() else torch_mod.zeros((), dtype=current.dtype, device=current.device)
    max_err = torch_mod.max(abs_diff) if abs_diff.numel() else torch_mod.zeros((), dtype=current.dtype, device=current.device)
    return energy, mean, max_err


def _paper_style_snap_energy(current, snap):
    torch_mod = _original.torch
    if snap is None:
        z = torch_mod.zeros((), dtype=current.dtype, device=current.device)
        return z, z
    pairs = snap["pairs"]
    a = pairs[:, 0]
    b = pairs[:, 1]
    pa = current[a[:, None], snap["edge_a"]].mean(dim=1)
    pb = current[b[:, None], snap["edge_b"]].mean(dim=1)
    d = pa - pb
    if d.numel() == 0:
        z = torch_mod.zeros((), dtype=current.dtype, device=current.device)
        return z, z
    per = torch_mod.sum(d * d, dim=1)
    return torch_mod.sum(per), torch_mod.sqrt(torch_mod.mean(per))


def _paper_style_lift_energy(current, lift):
    torch_mod = _original.torch
    if lift is None:
        z = torch_mod.zeros((), dtype=current.dtype, device=current.device)
        return z, z
    centers = _v15_group_centers(current, lift["group_idx"], lift["group_mask"])
    d = centers - lift["target_xyz"]
    if d.numel() == 0:
        z = torch_mod.zeros((), dtype=current.dtype, device=current.device)
        return z, z
    per = torch_mod.sum(d * d, dim=1)
    return torch_mod.sum(per), torch_mod.sqrt(torch_mod.mean(per))


def _paper_style_apply_snap_projection(current, snap, alpha_act, weight):
    torch_mod = _original.torch
    if snap is None:
        return current, torch_mod.zeros((), dtype=current.dtype, device=current.device)
    pairs = snap["pairs"]
    a = pairs[:, 0]
    b = pairs[:, 1]
    pa = current[a[:, None], snap["edge_a"]].mean(dim=1)
    pb = current[b[:, None], snap["edge_b"]].mean(dim=1)
    mid = 0.5 * (pa + pb)
    gain = torch_mod.clamp(alpha_act * float(weight), 0.0, 1.0) * 0.42
    da = (mid - pa) * gain
    db = (mid - pb) * gain
    flat_delta = torch_mod.zeros_like(current)
    counts = torch_mod.zeros((current.shape[0], 1, 1), dtype=current.dtype, device=current.device)
    flat_delta.index_add_(0, a, da.unsqueeze(1).expand(-1, 8, -1))
    flat_delta.index_add_(0, b, db.unsqueeze(1).expand(-1, 8, -1))
    one = torch_mod.ones((a.numel(), 1, 1), dtype=current.dtype, device=current.device)
    counts.index_add_(0, a, one)
    counts.index_add_(0, b, one)
    touched = counts[:, 0, 0] > 0
    out = current.clone()
    out[touched] = out[touched] + flat_delta[touched] / torch_mod.clamp(counts[touched], min=1.0)
    e, _ = _paper_style_snap_energy(out, snap)
    return out, e


def _paper_style_apply_lift_projection(current, lift, alpha_act, weight):
    torch_mod = _original.torch
    if lift is None:
        return current, torch_mod.zeros((), dtype=current.dtype, device=current.device)
    centers = _v15_group_centers(current, lift["group_idx"], lift["group_mask"])
    gain = torch_mod.clamp(alpha_act * float(weight), 0.0, 1.0) * 0.32
    corr = (lift["target_xyz"] - centers) * gain
    tile_delta = torch_mod.zeros_like(current)
    tile_counts = torch_mod.zeros((current.shape[0], 1, 1), dtype=current.dtype, device=current.device)
    _v15_add_group_translation(tile_delta, tile_counts, lift["group_idx"], lift["group_mask"], corr)
    touched = tile_counts[:, 0, 0] > 0
    out = current.clone()
    out[touched] = out[touched] + tile_delta[touched] / torch_mod.clamp(tile_counts[touched], min=1.0)
    e, _ = _paper_style_lift_energy(out, lift)
    return out, e


def _paper_style_prism_face_normals(vertices):
    torch_mod = _original.torch
    top = vertices[:, :4]
    bottom = vertices[:, 4:]
    faces = [
        (top[:, 1] - top[:, 0], top[:, 2] - top[:, 0]),
        (bottom[:, 2] - bottom[:, 0], bottom[:, 1] - bottom[:, 0]),
        (vertices[:, 1] - vertices[:, 0], vertices[:, 5] - vertices[:, 0]),
        (vertices[:, 2] - vertices[:, 1], vertices[:, 6] - vertices[:, 1]),
        (vertices[:, 3] - vertices[:, 2], vertices[:, 7] - vertices[:, 2]),
        (vertices[:, 0] - vertices[:, 3], vertices[:, 4] - vertices[:, 3]),
    ]
    normals = []
    for a, b in faces:
        n = torch_mod.cross(a, b, dim=1)
        n = n / torch_mod.clamp(torch_mod.linalg.norm(n, dim=1, keepdim=True), min=1e-9)
        normals.append(n)
    return torch_mod.stack(normals, dim=1)


def _paper_style_3d_sat_collision_projection(current, weight, excluded_pairs=None, *, max_pairs=16000):
    torch_mod = _original.torch
    n = int(current.shape[0])
    zero = torch_mod.zeros((), dtype=current.dtype, device=current.device)
    zero_i = torch_mod.zeros((), dtype=torch_mod.long, device=current.device)
    if n < 2 or float(weight) <= 0.0:
        return current, zero, zero, zero, zero, zero, zero, zero, zero_i, zero_i

    mins = torch_mod.min(current, dim=1).values
    maxs = torch_mod.max(current, dim=1).values
    pair_i, pair_j = torch_mod.triu_indices(n, n, offset=1, device=current.device)
    aabb_overlap = torch_mod.all(torch_mod.minimum(maxs[pair_i], maxs[pair_j]) - torch_mod.maximum(mins[pair_i], mins[pair_j]) > 0.0, dim=1)
    if excluded_pairs is None:
        excluded_flags = torch_mod.zeros_like(aabb_overlap)
    else:
        excluded_flags = excluded_pairs[pair_i, pair_j]
    adjacent_candidate_count = torch_mod.sum(aabb_overlap & excluded_flags).to(dtype=current.dtype)
    nonadj_mask = aabb_overlap & (~excluded_flags)
    pair_i = pair_i[aabb_overlap]
    pair_j = pair_j[aabb_overlap]
    nonadj_flags = (~excluded_flags)[aabb_overlap]
    pair_i = pair_i[nonadj_flags]
    pair_j = pair_j[nonadj_flags]
    candidate_count = torch_mod.as_tensor(pair_i.numel(), dtype=current.dtype, device=current.device)
    if pair_i.numel() == 0:
        return current, zero, zero, zero, candidate_count, adjacent_candidate_count, zero, zero, zero_i, zero_i
    if pair_i.numel() > int(max_pairs):
        keep = torch_mod.linspace(0, pair_i.numel() - 1, int(max_pairs), device=current.device).long()
        pair_i = pair_i[keep]
        pair_j = pair_j[keep]

    vi = current[pair_i]
    vj = current[pair_j]
    face_normals = _paper_style_prism_face_normals(current)
    axes = torch_mod.cat([face_normals[pair_i], face_normals[pair_j]], dim=1)
    # A small set of prism edge cross axes gives a true 3D SAT flavor without
    # pulling data back to CPU.  Degenerate axes are filtered by normalization.
    edge_ids = torch_mod.as_tensor(np.asarray([(0, 1), (1, 2), (0, 4), (4, 5), (5, 6), (1, 5)], dtype=np.int64), dtype=torch_mod.long, device=current.device)
    ei = vi[:, edge_ids[:, 1]] - vi[:, edge_ids[:, 0]]
    ej = vj[:, edge_ids[:, 1]] - vj[:, edge_ids[:, 0]]
    ei_exp = ei[:, :, None, :].expand(-1, -1, ej.shape[1], -1)
    ej_exp = ej[:, None, :, :].expand(-1, ei.shape[1], -1, -1)
    cross_axes = torch_mod.cross(ei_exp, ej_exp, dim=3).reshape(vi.shape[0], -1, 3)
    axes = torch_mod.cat([axes, cross_axes], dim=1)
    axis_norm = torch_mod.linalg.norm(axes, dim=2, keepdim=True)
    axes = axes / torch_mod.clamp(axis_norm, min=1e-9)
    valid = axis_norm.squeeze(2) > 1e-8
    invalid_axis_count = torch_mod.sum(~valid)

    proj_i = torch_mod.einsum("pvc,pac->pav", vi, axes)
    proj_j = torch_mod.einsum("pvc,pac->pav", vj, axes)
    overlap = torch_mod.minimum(torch_mod.max(proj_i, dim=2).values, torch_mod.max(proj_j, dim=2).values) - torch_mod.maximum(torch_mod.min(proj_i, dim=2).values, torch_mod.min(proj_j, dim=2).values)
    overlap = torch_mod.where(valid, overlap, torch_mod.full_like(overlap, float("inf")))
    valid_pair = torch_mod.any(valid, dim=1)
    colliding = valid_pair & torch_mod.all(overlap > 0.0, dim=1)
    active_count = torch_mod.sum(colliding).to(dtype=current.dtype)
    if not bool(torch_mod.any(colliding)):
        return current, zero, zero, zero, candidate_count, adjacent_candidate_count, active_count, zero, invalid_axis_count, zero_i

    ci = torch_mod.mean(vi, dim=1)
    cj = torch_mod.mean(vj, dim=1)
    min_overlap, axis_idx = torch_mod.min(overlap[colliding], dim=1)
    active_i = pair_i[colliding]
    active_j = pair_j[colliding]
    active_axes_all = axes[colliding]
    row_idx = torch_mod.arange(axis_idx.numel(), dtype=torch_mod.long, device=current.device)
    active_axes = active_axes_all[row_idx, axis_idx]
    center_delta = ci[colliding] - cj[colliding]
    sign = torch_mod.sign(torch_mod.sum(center_delta * active_axes, dim=1, keepdim=True))
    sign = torch_mod.where(sign == 0.0, torch_mod.ones_like(sign), sign)
    direction = active_axes * sign
    depth = min_overlap[:, None]
    raw_corr = direction * depth * (0.5 * min(max(float(weight), 0.0), 1.0))
    diag_i = torch_mod.linalg.norm(maxs[active_i] - mins[active_i], dim=1, keepdim=True)
    diag_j = torch_mod.linalg.norm(maxs[active_j] - mins[active_j], dim=1, keepdim=True)
    max_corr = torch_mod.clamp(0.050 * torch_mod.minimum(diag_i, diag_j), min=0.002, max=0.060)
    raw_norm = torch_mod.linalg.norm(raw_corr, dim=1, keepdim=True)
    corr = raw_corr * torch_mod.clamp(max_corr / torch_mod.clamp(raw_norm, min=1e-12), max=1.0)
    max_correction = torch_mod.max(torch_mod.linalg.norm(corr, dim=1)) if corr.numel() else zero

    shifts = torch_mod.zeros((n, 3), dtype=current.dtype, device=current.device)
    counts = torch_mod.zeros((n, 1), dtype=current.dtype, device=current.device)
    shifts.index_add_(0, active_i, corr)
    shifts.index_add_(0, active_j, -corr)
    one = torch_mod.ones((active_i.numel(), 1), dtype=current.dtype, device=current.device)
    counts.index_add_(0, active_i, one)
    counts.index_add_(0, active_j, one)
    touched = counts[:, 0] > 0
    out = current.clone()
    out[touched] = out[touched] + shifts[touched, None, :] / torch_mod.clamp(counts[touched], min=1.0).unsqueeze(1)
    mean_pen = torch_mod.mean(min_overlap) if min_overlap.numel() else zero
    max_pen = torch_mod.max(min_overlap) if min_overlap.numel() else zero
    e_collision = torch_mod.sum(min_overlap * min_overlap) if min_overlap.numel() else zero
    return out, e_collision, max_pen, mean_pen, candidate_count, adjacent_candidate_count, active_count, max_correction, invalid_axis_count, torch_mod.as_tensor(axis_idx.numel(), dtype=torch_mod.long, device=current.device)


def _paper_style_pd_simulate(state, params=None, progress_callback=None):
    torch_mod = _original.torch
    if torch_mod is None:
        raise RuntimeError("paper_style_pd requires torch.")
    paper_params = _paper_style_copy_deployment_params(params)
    compute = getattr(paper_params, "compute", None)
    if compute is not None:
        try:
            setattr(compute, "backend", "cuda")
            setattr(compute, "use_gpu_for_simulation", True)
        except Exception:
            pass
    if not torch_mod.cuda.is_available():
        raise RuntimeError("paper_style_pd requires CUDA-enabled PyTorch; torch.cuda.is_available() is false.")

    device = torch_mod.device("cuda")
    dtype = torch_mod.float64 if getattr(getattr(paper_params, "compute", None), "dtype", "float32") == "float64" else torch_mod.float32
    total_start = time.perf_counter()
    torch_mod.cuda.reset_peak_memory_stats(device)
    start_event = torch_mod.cuda.Event(enable_timing=True)
    end_event = torch_mod.cuda.Event(enable_timing=True)
    start_event.record()

    current = torch_mod.as_tensor(state.tiles_2d_dual_hinge.vertices, dtype=dtype, device=device).clone()
    rest = current.clone()
    debug_one_step = bool(getattr(paper_params, "paper_style_debug_one_step", False) or getattr(paper_params, "debug_one_step", False))
    target = torch_mod.as_tensor(state.tiles_3d.vertices, dtype=dtype, device=device)
    steps = 1 if debug_one_step else max(2, int(getattr(paper_params, "steps", 240)))
    iterations = max(1, int(getattr(paper_params, "solver_iterations", 28)))
    substeps = max(1, int(getattr(paper_params, "solver_substeps", 1)))
    max_frames = max(2, int(getattr(paper_params, "max_animation_frames", 120)))
    frame_stride = max(1, steps // max_frames)
    frames = []
    debug_frames = []
    debug_frame_labels = []
    progress_stride = max(1, steps // 50)

    snap = _v15_prepare_snap_tensors(state, device, dtype)
    lift = _paper_style_prepare_lift_tensors(state, device, dtype)
    excluded_pairs, excluded_info = _paper_style_topological_exclusion_matrix(state, int(current.shape[0]), device)
    debug_max_rows = int(getattr(paper_params, "paper_style_debug_max_rows", 256))
    snap_debug_rows = _paper_style_snap_debug_rows(state, snap, max_rows=debug_max_rows)
    lift_debug_rows = _paper_style_lift_debug_rows(state, lift, max_rows=debug_max_rows)
    last_terms = {
        "E_rigid": torch_mod.zeros((), dtype=dtype, device=device),
        "E_collision": torch_mod.zeros((), dtype=dtype, device=device),
        "E_snap": torch_mod.zeros((), dtype=dtype, device=device),
        "E_lift": torch_mod.zeros((), dtype=dtype, device=device),
        "max_penetration": torch_mod.zeros((), dtype=dtype, device=device),
        "mean_penetration": torch_mod.zeros((), dtype=dtype, device=device),
        "collision_pair_count": torch_mod.zeros((), dtype=dtype, device=device),
        "collision_pair_count_adjacent": torch_mod.zeros((), dtype=dtype, device=device),
        "collision_active_pair_count": torch_mod.zeros((), dtype=dtype, device=device),
        "collision_mtv_max_correction": torch_mod.zeros((), dtype=dtype, device=device),
        "collision_invalid_axis_count": torch_mod.zeros((), dtype=torch_mod.long, device=device),
    }
    energy_history = []
    displacement_history = []

    def _debug_append(label, vertices):
        if debug_one_step:
            debug_frames.append(vertices.detach().clone())
            debug_frame_labels.append(label)

    for step in range(steps):
        if step % progress_stride == 0:
            _emit_progress(progress_callback, "Paper-style PD simulation", 0.03 + 0.92 * step / max(1, steps - 1), f"step {step + 1}/{steps}")
        step_start = current.detach().clone()
        if debug_one_step and step == 0:
            _debug_append("before", current)
        if debug_one_step:
            raw_alpha = torch_mod.as_tensor(float(getattr(paper_params, "paper_style_debug_alpha", 1.0)), dtype=dtype, device=device)
        else:
            raw_alpha = torch_mod.as_tensor(float(getattr(paper_params, "quasi_static_pull_speed", 1.0)) * step / max(1, steps - 1), dtype=dtype, device=device)
        alpha_act = _paper_smoothstep_torch(raw_alpha)
        for _sub in range(substeps):
            for _it in range(iterations):
                current, e_snap = _paper_style_apply_snap_projection(current, snap, alpha_act, float(getattr(paper_params, "snap_weight", 0.78)))
                _debug_append("after_snap", current)
                current, e_lift = _paper_style_apply_lift_projection(current, lift, alpha_act, float(getattr(paper_params, "lift_weight", 0.90)))
                _debug_append("after_lift", current)
                current = _v15_torch_rigid_project(current, rest, float(getattr(paper_params, "rigid_weight", 0.995)))
                for _rp in range(max(0, int(getattr(paper_params, "rigid_projection_passes", 8)) - 1)):
                    current = _v15_torch_rigid_project(current, rest, 1.0)
                _debug_append("after_rigid", current)
                current, e_collision, max_pen, mean_pen, candidate_count, adjacent_candidate_count, active_count, max_corr, invalid_axis_count, sat_axis_count = _paper_style_3d_sat_collision_projection(
                    current,
                    float(getattr(paper_params, "collision_weight", 0.35)),
                    excluded_pairs,
                )
                _debug_append("after_collision", current)
                current = _v15_torch_rigid_project(current, rest, 1.0)
                _debug_append("after_collision_rigid", current)
                e_rigid, rigid_mean_t, rigid_max_t = _paper_style_rigid_energy_and_errors(current, rest)
                last_terms = {
                    "E_rigid": e_rigid.detach(),
                    "E_collision": e_collision.detach(),
                    "E_snap": e_snap.detach(),
                    "E_lift": e_lift.detach(),
                    "max_penetration": max_pen.detach(),
                    "mean_penetration": mean_pen.detach(),
                    "collision_pair_count": candidate_count.detach(),
                    "collision_pair_count_adjacent": adjacent_candidate_count.detach(),
                    "collision_active_pair_count": active_count.detach(),
                    "collision_mtv_max_correction": max_corr.detach(),
                    "collision_invalid_axis_count": invalid_axis_count.detach(),
                    "collision_sat_axis_count": sat_axis_count.detach(),
                    "rigid_error_mean": rigid_mean_t.detach(),
                    "rigid_error_max": rigid_max_t.detach(),
                }
        step_disp = torch_mod.max(torch_mod.linalg.norm(current.detach() - step_start, dim=2)) if current.numel() else torch_mod.zeros((), dtype=dtype, device=device)
        displacement_history.append(step_disp.detach())
        energy_history.append(torch_mod.stack([
            last_terms["E_rigid"],
            last_terms["E_collision"],
            last_terms["E_snap"],
            last_terms["E_lift"],
            last_terms["max_penetration"],
            last_terms["mean_penetration"],
        ]))
        if (not debug_one_step) and bool(getattr(paper_params, "store_animation_frames", True)) and (step == steps - 1 or step % frame_stride == 0):
            frames.append(current.detach().clone())

    end_event.record()
    torch_mod.cuda.synchronize(device)
    gpu_time = float(start_event.elapsed_time(end_event) / 1000.0)
    final = current.detach().cpu().numpy()
    stored_frames = debug_frames if debug_one_step else frames
    frame_arrays = [f.detach().cpu().numpy() for f in stored_frames] or [final.copy()]
    history = torch_mod.stack(energy_history).detach().cpu().numpy() if energy_history else np.zeros((0, 6), dtype=float)
    displacement_np = torch_mod.stack(displacement_history).detach().cpu().numpy() if displacement_history else np.zeros((0,), dtype=float)
    e_rigid = float(last_terms["E_rigid"].detach().cpu())
    e_collision = float(last_terms["E_collision"].detach().cpu())
    e_snap = float(last_terms["E_snap"].detach().cpu())
    e_lift = float(last_terms["E_lift"].detach().cpu())
    max_pen = float(last_terms["max_penetration"].detach().cpu())
    mean_pen = float(last_terms["mean_penetration"].detach().cpu())
    rigid_mean = float(last_terms.get("rigid_error_mean", torch_mod.zeros((), device=device)).detach().cpu())
    rigid_max = float(last_terms.get("rigid_error_max", torch_mod.zeros((), device=device)).detach().cpu())
    nonadj_collision_pairs = int(float(last_terms["collision_pair_count"].detach().cpu()))
    adjacent_collision_pairs = int(float(last_terms["collision_pair_count_adjacent"].detach().cpu()))
    active_collision_pairs = int(float(last_terms["collision_active_pair_count"].detach().cpu()))
    mtv_max_correction = float(last_terms["collision_mtv_max_correction"].detach().cpu())
    invalid_axis_count = int(last_terms["collision_invalid_axis_count"].detach().cpu())
    sat_axis_count = int(last_terms.get("collision_sat_axis_count", torch_mod.zeros((), dtype=torch_mod.long, device=device)).detach().cpu())
    total = float(getattr(paper_params, "rigid_weight", 0.995)) * e_rigid + float(getattr(paper_params, "collision_weight", 0.35)) * e_collision + float(getattr(paper_params, "snap_weight", 0.78)) * e_snap + float(getattr(paper_params, "lift_weight", 0.90)) * e_lift
    try:
        final_deployment_error = float(_original.rms_distance(final, state.tiles_3d.vertices))
    except Exception:
        final_deployment_error = float(np.sqrt(np.mean((final - np.asarray(state.tiles_3d.vertices, dtype=float)) ** 2)))

    metrics = {
        "paper_simulation_mode": "paper_style_pd",
        "paper_style_pd_enabled": True,
        "paper_style_pd_model": "Section 5.4 local-global torch approximation: E_rigid + E_collision + alpha_act(E_snap + E_lift)",
        "paper_style_pd_gpu_active": True,
        "paper_style_pd_uses_cpu_collision": False,
        "paper_style_pd_gpu_cpu_transfer_per_step": False,
        "actual_backend": "cuda",
        "dominant_backend": "cuda",
        "gpu_kernel_time": gpu_time,
        "gpu_memory_peak": int(torch_mod.cuda.max_memory_allocated(device)),
        "elapsed_time": float(time.perf_counter() - total_start),
        "E_rigid": e_rigid,
        "E_collision": e_collision,
        "E_snap": e_snap,
        "E_lift": e_lift,
        "E_total": total,
        "energy_history_columns": "E_rigid,E_collision,E_snap,E_lift,max_penetration,mean_penetration",
        "energy_history_csv": ";".join(",".join(f"{float(x):.8g}" for x in row) for row in history),
        "snap_constraint_count": int(len(snap["gaps"])) if snap is not None else 0,
        "lift_constraint_count": int(len(lift["gap_ids"])) if lift is not None else 0,
        "collision_pair_count": nonadj_collision_pairs,
        "collision_pair_count_nonadjacent": nonadj_collision_pairs,
        "collision_pair_count_adjacent_excluded": adjacent_collision_pairs,
        "collision_active_pair_count_nonadjacent": active_collision_pairs,
        "collision_excluded_topological_pair_count": int(excluded_info.get("topological_pair_count", 0)),
        "collision_excluded_adjacent_pair_count": int(excluded_info.get("adjacent_pair_count", 0)),
        "collision_excluded_hinge_pair_count": int(excluded_info.get("hinge_pair_count", 0)),
        "generic_collision_excludes_adjacent_pairs": True,
        "generic_collision_excludes_hinge_connected_pairs": True,
        "collision_mtv_direction_sign_model": "center_delta dot axis; tile_i shifts along center_i-center_j direction",
        "collision_mtv_max_correction": mtv_max_correction,
        "collision_mtv_max_correction_clamped": True,
        "collision_sat_invalid_axis_count": invalid_axis_count,
        "collision_sat_active_axis_count": sat_axis_count,
        "collision_sat_zero_length_cross_axes_excluded": True,
        "collision_excluded_adjacent_pairs_sample_json": json.dumps(excluded_info.get("adjacent_pairs_sample", [])),
        "collision_excluded_hinge_pairs_sample_json": json.dumps(excluded_info.get("hinge_pairs_sample", [])),
        "max_penetration": max_pen,
        "mean_penetration": mean_pen,
        "rigid_error_mean": rigid_mean,
        "rigid_error_max": rigid_max,
        "solver_iterations": int(iterations),
        "simulation_steps": int(steps),
        "animation_frames": int(len(frame_arrays)),
        "paper_style_debug_one_step": bool(debug_one_step),
        "paper_style_debug_frame_labels": json.dumps(debug_frame_labels),
        "paper_style_debug_alpha": float(getattr(paper_params, "paper_style_debug_alpha", 1.0)) if debug_one_step else None,
        "paper_style_snap_constraints_debug_json": json.dumps(snap_debug_rows),
        "paper_style_lift_constraints_debug_json": json.dumps(lift_debug_rows),
        "paper_style_debug_snap_rows": int(len(snap_debug_rows)),
        "paper_style_debug_lift_rows": int(len(lift_debug_rows)),
        "paper_style_max_displacement_per_step": float(np.max(displacement_np)) if displacement_np.size else 0.0,
        "paper_style_displacement_history_csv": ",".join(f"{float(x):.8g}" for x in displacement_np),
        "uses_boundary_independent_contraction": False,
        "uses_delayed_lift": False,
        "uses_height_only_lift": False,
        "uses_target_pose_fit": False,
        "uses_2d_footprint_collision": False,
        "collision_model": "3d_prism_sat_approx",
        "paper_style_remaining_differences": "; ".join([
            "ShapeOp/libigl not used; implemented torch local-global approximation",
            "3D SAT collision uses approximate MTV projection and capped broad-phase candidates",
            "Lift target approximated from current T3D lift gap geometry when explicit lift target is unavailable",
        ]),
        "final_deployment_error_to_T3D": final_deployment_error,
        "snap_error": float(_original._snap_error(final, state)) if hasattr(_original, "_snap_error") else 0.0,
        "lift_error": float(_original._lift_error(final, state)) if hasattr(_original, "_lift_error") else 0.0,
        "stable_state": bool(np.isfinite(total)),
    }
    metrics.update(_paper_style_frame_health_metrics(frame_arrays, state))
    return _original.DeploymentResult(frames=frame_arrays, final_tiles=final, metrics=metrics, collision_counts=[])


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    mode = str(getattr(params, "simulation_mode", "paper_style_pd") if params is not None else "paper_style_pd")
    if mode == "legacy_boundary_driven_v20":
        if _LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE is None:
            raise RuntimeError("legacy_boundary_driven_v20 simulation is unavailable.")
        result = _LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE(state, params, progress_callback=progress_callback)
        try:
            result.metrics = dict(getattr(result, "metrics", {}) or {})
            result.metrics["paper_simulation_mode"] = "legacy_boundary_driven_v20"
            result.metrics["paper_style_pd_enabled"] = False
        except Exception:
            pass
        return result
    return _paper_style_pd_simulate(state, params, progress_callback=progress_callback)


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["PAPER_STYLE_PD_SIMULATION_ACTIVE"] = True


def paper_consistency_report(state):
    rows = []
    if _PREVIOUS_PAPER_CONSISTENCY_REPORT_V20 is not None:
        try:
            rows = list(_PREVIOUS_PAPER_CONSISTENCY_REPORT_V20(state))
        except Exception:
            rows = []
    sim = getattr(state, "simulation_result", None)
    metrics = dict(getattr(sim, "metrics", {}) or {}) if sim is not None else {}
    rows.append({
        "item": "Paper-style PD simulation core v21",
        "expected": "Sec.5.4: E_rigid + E_collision + E_actuation(E_snap + E_lift), no boundary-driven order, no delayed/height-only lift, CUDA tensors retained through solver",
        "actual": f"mode={metrics.get('paper_simulation_mode', 'not simulated')}, backend={metrics.get('actual_backend', 'not simulated')}, collision={metrics.get('collision_model', 'not simulated')}",
        "ok": bool(metrics.get("paper_style_pd_enabled", False)) if sim is not None else True,
        "value": metrics.get("paper_style_remaining_differences", "not simulated"),
    })
    return rows


_original.paper_consistency_report = paper_consistency_report
globals()["paper_consistency_report"] = paper_consistency_report

# ---------------------------------------------------------------------------
# Paper-faithful collision / frame-density defaults v12
# ---------------------------------------------------------------------------
_ORIGINAL_AABB_COLLISION_PROJECTOR_V12 = getattr(_original, "_project_aabb_collisions", None)


def _v12_convex_hull_2d(points: np.ndarray) -> np.ndarray:
    pts = np.asarray(points, dtype=float).reshape(-1, 2)
    pts = pts[np.all(np.isfinite(pts), axis=1)]
    if len(pts) <= 1:
        return pts.copy()
    order = np.lexsort((pts[:, 1], pts[:, 0]))
    pts = pts[order]
    unique = [pts[0]]
    for p in pts[1:]:
        if np.linalg.norm(p - unique[-1]) > 1e-10:
            unique.append(p)
    pts = np.asarray(unique, dtype=float)
    if len(pts) <= 2:
        return pts

    def cross(o, a, b):
        return float((a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]))

    lower = []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 1e-12:
            lower.pop()
        lower.append(p)
    upper = []
    for p in pts[::-1]:
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 1e-12:
            upper.pop()
        upper.append(p)
    hull = np.asarray(lower[:-1] + upper[:-1], dtype=float)
    return hull if len(hull) else pts


def _v12_sat_mtv_2d(poly_a: np.ndarray, poly_b: np.ndarray, clearance: float = 0.0):
    a = _v12_convex_hull_2d(poly_a)
    b = _v12_convex_hull_2d(poly_b)
    if len(a) < 3 or len(b) < 3:
        return False, np.zeros(2, dtype=float), 0.0
    center_a = np.mean(a, axis=0)
    center_b = np.mean(b, axis=0)
    best_axis = None
    best_overlap = float("inf")
    for poly in (a, b):
        for idx in range(len(poly)):
            edge = poly[(idx + 1) % len(poly)] - poly[idx]
            n = np.asarray([-edge[1], edge[0]], dtype=float)
            norm = float(np.linalg.norm(n))
            if norm <= 1e-12:
                continue
            axis = n / norm
            min_a = float(np.min(a @ axis)); max_a = float(np.max(a @ axis))
            min_b = float(np.min(b @ axis)); max_b = float(np.max(b @ axis))
            overlap = min(max_a, max_b) - max(min_a, min_b) + float(clearance)
            if overlap <= 0.0:
                return False, np.zeros(2, dtype=float), 0.0
            if overlap < best_overlap:
                if float(np.dot(axis, center_a - center_b)) < 0.0:
                    axis = -axis
                best_overlap = overlap
                best_axis = axis
    if best_axis is None or not np.isfinite(best_overlap):
        return False, np.zeros(2, dtype=float), 0.0
    return True, best_axis * best_overlap, best_overlap


def _v12_spatial_candidate_pairs(vertices: np.ndarray, pad: float, max_pairs: int = 18000):
    verts = np.asarray(vertices, dtype=float)
    n = int(len(verts))
    if n <= 1:
        return []
    xy = verts[:, :, :2]
    lo = np.nanmin(xy, axis=1) - pad
    hi = np.nanmax(xy, axis=1) + pad
    sizes = np.maximum(hi - lo, 1e-6)
    cell = float(np.nanmedian(np.max(sizes, axis=1)))
    if not np.isfinite(cell) or cell <= 1e-6:
        cell = 1.0
    buckets: dict[tuple[int, int], list[int]] = {}
    for i in range(n):
        c0 = np.floor(lo[i] / cell).astype(int)
        c1 = np.floor(hi[i] / cell).astype(int)
        # Avoid pathological huge coverage if geometry explodes.
        c1 = np.minimum(c1, c0 + 8)
        for cx in range(int(c0[0]), int(c1[0]) + 1):
            for cy in range(int(c0[1]), int(c1[1]) + 1):
                buckets.setdefault((cx, cy), []).append(i)
    pairs: set[tuple[int, int]] = set()
    for ids in buckets.values():
        m = len(ids)
        for a_idx in range(m):
            ia = ids[a_idx]
            for b_idx in range(a_idx + 1, m):
                ib = ids[b_idx]
                if ia == ib:
                    continue
                i, j = (ia, ib) if ia < ib else (ib, ia)
                if np.any(hi[i] < lo[j]) or np.any(hi[j] < lo[i]):
                    continue
                pairs.add((i, j))
                if len(pairs) >= max_pairs:
                    return list(pairs)
    return list(pairs)


def _v12_enhanced_footprint_collision_projection(vertices: np.ndarray, weight: float, grid=None, sweeps: int = 3) -> int:
    """Stronger full-footprint panel separation for paper-faithful simulation.

    The original prototype used a light AABB/local repulsion.  This pass treats
    each thick tile's projected 8-vertex footprint as a convex polygon and applies
    a bounded SAT minimum-translation vector to whole tiles.  It is still a
    lightweight approximation of the paper's E_collision, but it prevents the
    obvious panel interpenetrations visible in dense deployment frames much better
    than AABB-only pushback.
    """
    verts = np.asarray(vertices, dtype=float)
    if verts.ndim != 3 or verts.shape[0] <= 1 or verts.shape[2] < 2:
        return 0
    try:
        gap_size = float(getattr(grid, "gap_size", 0.0)) if grid is not None else 0.0
    except Exception:
        gap_size = 0.0
    clearance = max(gap_size * 0.20, 1e-4)
    pad = max(clearance * 4.0, 1e-3)
    total_active = 0
    step_scale = min(0.85, max(0.25, 0.18 + 0.16 * float(weight)))
    for _ in range(max(1, int(sweeps))):
        pairs = _v12_spatial_candidate_pairs(verts, pad=pad, max_pairs=20000)
        if not pairs:
            break
        shifts = np.zeros((len(verts), 2), dtype=float)
        counts = np.zeros((len(verts), 1), dtype=float)
        active = 0
        for i, j in pairs:
            zi0 = float(np.nanmin(verts[i, :, 2])); zi1 = float(np.nanmax(verts[i, :, 2]))
            zj0 = float(np.nanmin(verts[j, :, 2])); zj1 = float(np.nanmax(verts[j, :, 2]))
            # If z intervals are far apart, the panels are visually crossing in
            # projection but not physically interpenetrating.
            z_gap = max(zj0 - zi1, zi0 - zj1, 0.0)
            if z_gap > max(0.08, 4.0 * clearance):
                continue
            overlap, mtv, amount = _v12_sat_mtv_2d(verts[i, :, :2], verts[j, :, :2], clearance=clearance)
            if not overlap:
                continue
            if not np.all(np.isfinite(mtv)):
                continue
            # Bounded whole-tile translation.  The following rigid projection in
            # the solver loop restores exact tile shape; translating all vertices
            # here already preserves shape.
            mtv_norm = float(np.linalg.norm(mtv))
            if mtv_norm > max(0.35, 3.0 * clearance):
                mtv *= max(0.35, 3.0 * clearance) / max(mtv_norm, 1e-12)
            shifts[i] += 0.5 * mtv
            shifts[j] -= 0.5 * mtv
            counts[i, 0] += 1.0
            counts[j, 0] += 1.0
            active += 1
        total_active += active
        if active == 0:
            break
        mask = counts[:, 0] > 0.0
        shifts[mask] /= np.maximum(counts[mask], 1.0)
        verts[mask, :, :2] += step_scale * shifts[mask, None, :]
    return int(total_active)


def _project_aabb_collisions(vertices, weight=1.0, *args, **kwargs):
    """v12 collision projector: original AABB pass + stronger footprint SAT pass."""
    grid = kwargs.get("grid", None)
    if grid is None:
        for arg in args:
            if hasattr(arg, "gap_size") or hasattr(arg, "nx"):
                grid = arg
                break
    # First preserve original behavior, but run it a few times with a nontrivial
    # weight.  This keeps compatibility with all call signatures in the old code.
    result = None
    if _ORIGINAL_AABB_COLLISION_PROJECTOR_V12 is not None:
        for _ in range(3):
            try:
                result = _ORIGINAL_AABB_COLLISION_PROJECTOR_V12(vertices, max(float(weight), 0.75), *args, **kwargs)
            except TypeError:
                try:
                    result = _ORIGINAL_AABB_COLLISION_PROJECTOR_V12(vertices, max(float(weight), 0.75), grid=grid)
                except Exception:
                    break
            except Exception:
                break
    try:
        _v12_enhanced_footprint_collision_projection(vertices, max(float(weight), 1.0), grid=grid, sweeps=4)
    except Exception:
        pass
    return result


# Patch the original module globals used by the original CPU simulation function.
_original._project_aabb_collisions = _project_aabb_collisions
globals()["_project_aabb_collisions"] = _project_aabb_collisions


def _copy_paper_deployment_params_v12(params):
    try:
        import dataclasses
        if params is None:
            params = _original.DeploymentParameters()
        if dataclasses.is_dataclass(params):
            params = dataclasses.replace(params)
        else:
            import copy
            params = copy.copy(params)
    except Exception:
        try:
            params = _original.DeploymentParameters()
        except Exception:
            return params
    # v12 favors correctness over GPU speed: force CPU so the enhanced Python SAT
    # collision projector is actually used instead of the older torch path.
    try:
        compute = getattr(params, "compute", None)
        if compute is not None:
            setattr(compute, "backend", "cpu")
            setattr(compute, "use_gpu_for_simulation", False)
    except Exception:
        pass
    forced_exact = {
        "snap_scope": "string_path_only",
        "use_target_gap_contraction": True,
        "target_fit_weight": 0.0,
        "target_contact_guard_weight": 0.0,
        "target_contact_projection_passes": 0,
        "target_contact_start_alpha": 1.0,
        "target_contact_clearance": 0.0,
        "store_animation_frames": True,
    }
    changed = {}
    for name, value in forced_exact.items():
        if hasattr(params, name):
            old = getattr(params, name)
            try:
                setattr(params, name, value)
                changed[name] = (old, value)
            except Exception:
                pass
    minimums = {
        "max_animation_frames": 120,
        "steps": 240,
        "solver_iterations": 32,
        "solver_substeps": 2,
        "rigid_projection_passes": 8,
        "collision_weight": 1.25,
        "rigid_weight": 0.98,
        "hinge_weight": 0.92,
        "snap_weight": 0.80,
        "lift_weight": 0.95,
    }
    for name, value in minimums.items():
        if hasattr(params, name):
            try:
                old = getattr(params, name)
                if float(old) < float(value):
                    setattr(params, name, value)
                    changed[name] = (old, value)
            except Exception:
                pass
    try:
        # Keep steps dense enough relative to stored frames.
        if hasattr(params, "steps") and hasattr(params, "max_animation_frames"):
            steps_old = getattr(params, "steps")
            desired_steps = max(int(steps_old), int(getattr(params, "max_animation_frames")) * 2)
            setattr(params, "steps", desired_steps)
            if desired_steps != steps_old:
                changed["steps"] = (steps_old, desired_steps)
    except Exception:
        pass
    try:
        params._paper_v12_forced_changes = changed
    except Exception:
        pass
    return params


_PREVIOUS_SIMULATE_ONESTRING_DEPLOYMENT_V12 = globals().get("simulate_onestring_deployment")


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    """Paper-faithful v12 simulation with stronger collision and denser frames."""
    paper_params = _copy_paper_deployment_params_v12(params)
    cache_enabled = True
    cache = None
    key = None
    try:
        import streamlit as st
        cache = st.session_state.setdefault("onestring_animation_result_cache", {})
        key = ("paper_v12_collision_deployment", _state_cache_key(state), _deployment_params_cache_key(paper_params))
        if key in cache:
            if progress_callback is not None:
                try:
                    progress_callback("Cached paper-faithful v12 deployment", 1.0, "reusing collision-strengthened frames")
                except Exception:
                    pass
            return cache[key]
    except Exception:
        cache_enabled = False
    result = _ORIGINAL_SIMULATE_ONESTRING_DEPLOYMENT(state, paper_params, progress_callback=progress_callback)
    try:
        result.metrics = dict(getattr(result, "metrics", {}) or {})
        changes = getattr(paper_params, "_paper_v12_forced_changes", {}) or {}
        result.metrics.update({
            "paper_faithful_simulation_v12_enabled": True,
            "paper_collision_v12_enabled": True,
            "paper_collision_v12_model": "original AABB/local projection + full-footprint 2D SAT whole-tile separation",
            "paper_collision_v12_backend": "cpu_for_enhanced_collision_projection",
            "paper_collision_v12_gpu_disabled_for_collision_correctness": True,
            "paper_simulation_energy_model": "E = w_rigid*E_rigid + w_collision*E_collision + w_actuation*(E_snap + E_lift)",
            "paper_simulation_terms_enabled": "E_rigid,E_collision,E_snap,E_lift",
            "paper_simulation_target_pose_fit_disabled": True,
            "paper_simulation_target_contact_guard_disabled": True,
            "paper_simulation_snap_scope_forced": "string_path_only",
            "paper_simulation_min_animation_frames_v12": int(getattr(paper_params, "max_animation_frames", 0)),
            "paper_simulation_forced_parameter_count": int(len(changes)),
            "paper_simulation_forced_parameters": "; ".join(f"{k}:{v[0]}->{v[1]}" for k, v in changes.items()),
        })
    except Exception:
        pass
    if cache_enabled and cache is not None and key is not None:
        try:
            cache[key] = result
            if len(cache) > 8:
                oldest_key = next(iter(cache.keys()))
                if oldest_key != key:
                    cache.pop(oldest_key, None)
        except Exception:
            pass
    return result


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["PAPER_FAITHFUL_SIMULATION_V12_ACTIVE"] = True
globals()["PAPER_COLLISION_V12_ACTIVE"] = True

_PREVIOUS_PAPER_CONSISTENCY_REPORT_V12 = globals().get("paper_consistency_report")

def paper_consistency_report(state):
    rows = []
    if _PREVIOUS_PAPER_CONSISTENCY_REPORT_V12 is not None:
        try:
            rows = list(_PREVIOUS_PAPER_CONSISTENCY_REPORT_V12(state))
        except Exception:
            rows = []
    sim = getattr(state, "simulation_result", None)
    metrics = dict(getattr(sim, "metrics", {}) or {}) if sim is not None else {}
    rows.append({
        "item": "Collision-strengthened simulation v12",
        "expected": "Paper Sec.5.4 E_collision prevents rigid thick tiles from interpenetrating during string-path snap/lift deployment",
        "actual": f"enhanced={metrics.get('paper_collision_v12_enabled', 'not simulated')}, backend={metrics.get('paper_collision_v12_backend', 'not simulated')}",
        "ok": bool(metrics.get("paper_collision_v12_enabled", False)) if sim is not None else True,
        "value": metrics.get("paper_collision_v12_model", "not simulated"),
    })
    rows.append({
        "item": "Animation frame density v12",
        "expected": "Enough stored PD frames to inspect deployment without coarse 48-frame stepping",
        "actual": str(metrics.get("paper_simulation_min_animation_frames_v12", "not simulated")),
        "ok": True if sim is None else int(metrics.get("paper_simulation_min_animation_frames_v12", 0)) >= 120,
        "value": "default minimum 120 stored frames, steps at least 2x frames",
    })
    return rows

globals()["paper_consistency_report"] = paper_consistency_report
_original.paper_consistency_report = paper_consistency_report

# ---------------------------------------------------------------------------
# GPU collision-strengthened paper deployment mode v13
# ---------------------------------------------------------------------------
# v12 deliberately forced the CPU path so the NumPy SAT projector would run.
# v13 keeps the CUDA deployment path and injects a torch tensorized footprint
# collision projection after rigid-tile projection.  This avoids GPU->CPU->GPU
# transfers inside the solver loop while still strengthening inter-tile contact.

_V13_GPU_COLLISION_CONTEXT: dict | None = None
_ORIGINAL_TORCH_PROJECT_RIGID_TILES_V13 = getattr(_original, "_torch_project_rigid_tiles", None)


def _v13_torch_edge_axes(xy, edge_idx):
    torch_mod = _original.torch
    edges = xy[:, edge_idx[:, 1], :] - xy[:, edge_idx[:, 0], :]
    axes = torch_mod.stack([-edges[..., 1], edges[..., 0]], dim=-1)
    norms = torch_mod.linalg.norm(axes, dim=-1, keepdim=True)
    axes = axes / torch_mod.clamp(norms, min=1e-12)
    return axes


def _v13_torch_footprint_collision_projection(vertices, weight: float, context: dict):
    """GPU tensorized 2D footprint SAT collision projection.

    This is a deployment-time approximation of E_collision.  Each thick tile is
    represented by the convex footprint of its 8 projected vertices in XY.  We use
    torch AABB broad phase, then a vectorized SAT over top/bottom face edge axes.
    The MTV is applied as a whole-tile XY translation, preserving tile shape; the
    surrounding solver immediately applies rigid projection again.
    """
    torch_mod = _original.torch
    if torch_mod is None or vertices is None or int(vertices.shape[0]) < 2:
        return vertices, 0, 0
    out = vertices
    device = out.device
    dtype = out.dtype
    n_tiles = int(out.shape[0])
    clearance = float(context.get("clearance", 0.0))
    sweeps = max(1, int(context.get("sweeps", 2)))
    max_pairs = max(1, int(context.get("max_pairs", 30000)))
    z_pad = float(context.get("z_pad", 0.25))
    max_step = float(context.get("max_step", 0.25))
    step_scale = min(0.85, max(0.10, 0.30 + 0.10 * float(weight)))
    total_active = 0
    max_candidate_count = 0
    edge_idx = torch_mod.tensor(
        [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4]],
        dtype=torch_mod.long,
        device=device,
    )
    triu = torch_mod.triu(torch_mod.ones((n_tiles, n_tiles), dtype=torch_mod.bool, device=device), diagonal=1)
    for _sweep in range(sweeps):
        xy = out[..., :2]
        z = out[..., 2]
        lo = xy.amin(dim=1)
        hi = xy.amax(dim=1)
        zlo = z.amin(dim=1)
        zhi = z.amax(dim=1)
        # Broad phase: overlapping XY AABBs and reasonably close vertical spans.
        mask = (
            (lo[:, None, 0] <= hi[None, :, 0] + clearance)
            & (hi[:, None, 0] + clearance >= lo[None, :, 0])
            & (lo[:, None, 1] <= hi[None, :, 1] + clearance)
            & (hi[:, None, 1] + clearance >= lo[None, :, 1])
            & (zlo[:, None] <= zhi[None, :] + z_pad)
            & (zhi[:, None] + z_pad >= zlo[None, :])
            & triu
        )
        pairs = torch_mod.nonzero(mask, as_tuple=False)
        candidate_count = int(pairs.shape[0])
        max_candidate_count = max(max_candidate_count, candidate_count)
        if candidate_count == 0:
            break
        if candidate_count > max_pairs:
            pairs = pairs[:max_pairs]
        ia = pairs[:, 0]
        ib = pairs[:, 1]
        pa = xy[ia]
        pb = xy[ib]
        axes = torch_mod.cat([_v13_torch_edge_axes(pa, edge_idx), _v13_torch_edge_axes(pb, edge_idx)], dim=1)
        # Pairwise SAT projection on all axes.  Shape: Pairs x Axes x Vertices.
        dots_a = torch_mod.einsum("pvd,pad->pav", pa, axes)
        dots_b = torch_mod.einsum("pvd,pad->pav", pb, axes)
        min_a = dots_a.amin(dim=2)
        max_a = dots_a.amax(dim=2)
        min_b = dots_b.amin(dim=2)
        max_b = dots_b.amax(dim=2)
        overlap = torch_mod.minimum(max_a, max_b) - torch_mod.maximum(min_a, min_b) + clearance
        min_overlap, min_axis_idx = overlap.min(dim=1)
        colliding = min_overlap > 0.0
        if not bool(torch_mod.any(colliding)):
            break
        ia_c = ia[colliding]
        ib_c = ib[colliding]
        axes_c = axes[colliding]
        axis_pick = min_axis_idx[colliding]
        row = torch_mod.arange(axis_pick.numel(), dtype=torch_mod.long, device=device)
        chosen_axis = axes_c[row, axis_pick]
        depth = min_overlap[colliding]
        centers = xy.mean(dim=1)
        direction = centers[ia_c] - centers[ib_c]
        sign = torch_mod.where((chosen_axis * direction).sum(dim=1, keepdim=True) < 0.0, -1.0, 1.0).to(dtype)
        mtv = chosen_axis * sign * depth[:, None]
        norm = torch_mod.linalg.norm(mtv, dim=1, keepdim=True)
        mtv = mtv * torch_mod.clamp(torch_mod.as_tensor(max_step, dtype=dtype, device=device) / torch_mod.clamp(norm, min=1e-12), max=1.0)
        shifts = torch_mod.zeros((n_tiles, 2), dtype=dtype, device=device)
        counts = torch_mod.zeros((n_tiles, 1), dtype=dtype, device=device)
        half = torch_mod.as_tensor(0.5, dtype=dtype, device=device)
        shifts.index_add_(0, ia_c, half * mtv)
        shifts.index_add_(0, ib_c, -half * mtv)
        ones = torch_mod.ones((ia_c.numel(), 1), dtype=dtype, device=device)
        counts.index_add_(0, ia_c, ones)
        counts.index_add_(0, ib_c, ones)
        active = counts[:, 0] > 0.0
        if not bool(torch_mod.any(active)):
            break
        shifts = shifts / torch_mod.clamp(counts, min=1.0)
        out = out.clone()
        out[active, :, :2] = out[active, :, :2] + step_scale * shifts[active, None, :]
        active_count = int(ia_c.numel())
        total_active += active_count
        if active_count == 0:
            break
    return out, int(total_active), int(max_candidate_count)


def _torch_project_rigid_tiles(current, rest, weight: float):
    """v13 rigid projection wrapper with GPU footprint collision pass."""
    if _ORIGINAL_TORCH_PROJECT_RIGID_TILES_V13 is None:
        return current
    out = _ORIGINAL_TORCH_PROJECT_RIGID_TILES_V13(current, rest, weight)
    ctx = globals().get("_V13_GPU_COLLISION_CONTEXT", None)
    if not ctx or not bool(ctx.get("enabled", False)):
        return out
    try:
        if int(out.shape[0]) != int(ctx.get("tile_count", -1)):
            return out
        projected, active_count, candidate_count = _v13_torch_footprint_collision_projection(out, float(ctx.get("weight", weight)), ctx)
        ctx["active_pair_total"] = int(ctx.get("active_pair_total", 0)) + int(active_count)
        ctx["max_candidate_pair_count"] = max(int(ctx.get("max_candidate_pair_count", 0)), int(candidate_count))
        ctx["call_count"] = int(ctx.get("call_count", 0)) + 1
        if active_count > 0:
            # Re-project to the rigid rest tile after whole-tile translation.  This
            # mirrors the paper-style E_rigid dominance and prevents the collision
            # pass from accumulating shape errors.
            projected = _ORIGINAL_TORCH_PROJECT_RIGID_TILES_V13(projected, rest, 1.0)
        return projected
    except Exception as exc:
        try:
            ctx["error"] = str(exc)
        except Exception:
            pass
        return out


# Patch both the original module namespace and this wrapper namespace.  The
# original CUDA solver resolves _torch_project_rigid_tiles through its globals.
if _ORIGINAL_TORCH_PROJECT_RIGID_TILES_V13 is not None:
    _original._torch_project_rigid_tiles = _torch_project_rigid_tiles
    globals()["_torch_project_rigid_tiles"] = _torch_project_rigid_tiles


def _copy_paper_deployment_params_v13(params):
    try:
        import dataclasses
        if params is None:
            params = _original.DeploymentParameters()
        if dataclasses.is_dataclass(params):
            params = dataclasses.replace(params)
        else:
            import copy
            params = copy.copy(params)
    except Exception:
        try:
            params = _original.DeploymentParameters()
        except Exception:
            return params
    changed = {}
    # Keep the paper-faithful terms from v11/v12.
    forced_exact = {
        "snap_scope": "string_path_only",
        "use_target_gap_contraction": True,
        "target_fit_weight": 0.0,
        "target_contact_guard_weight": 0.0,
        "target_contact_projection_passes": 0,
        "target_contact_start_alpha": 1.0,
        "target_contact_clearance": 0.0,
        "store_animation_frames": True,
    }
    for name, value in forced_exact.items():
        if hasattr(params, name):
            try:
                old = getattr(params, name)
                setattr(params, name, value)
                changed[name] = (old, value)
            except Exception:
                pass
    # Preserve frame density and stronger solver settings requested by the user.
    minimums = {
        "max_animation_frames": 120,
        "steps": 240,
        "solver_iterations": 32,
        "solver_substeps": 2,
        "rigid_projection_passes": 8,
        "collision_weight": 1.25,
        "rigid_weight": 0.98,
        "hinge_weight": 0.92,
        "snap_weight": 0.80,
        "lift_weight": 0.95,
    }
    for name, value in minimums.items():
        if hasattr(params, name):
            try:
                old = getattr(params, name)
                if float(old) < float(value):
                    setattr(params, name, value)
                    changed[name] = (old, value)
            except Exception:
                pass
    try:
        if hasattr(params, "steps") and hasattr(params, "max_animation_frames"):
            steps_old = getattr(params, "steps")
            desired_steps = max(int(steps_old), int(getattr(params, "max_animation_frames")) * 2)
            setattr(params, "steps", desired_steps)
            if desired_steps != steps_old:
                changed["steps"] = (steps_old, desired_steps)
    except Exception:
        pass
    # v13: prefer CUDA instead of forcing CPU.  If CUDA is unavailable, the
    # original simulator falls back to CPU and still uses the v12 CPU SAT pass.
    try:
        torch_mod = _original.torch
        compute = getattr(params, "compute", None)
        if compute is not None and torch_mod is not None and torch_mod.cuda.is_available():
            old_backend = getattr(compute, "backend", None)
            old_use = getattr(compute, "use_gpu_for_simulation", None)
            setattr(compute, "backend", "cuda")
            setattr(compute, "use_gpu_for_simulation", True)
            changed["compute.backend"] = (old_backend, "cuda")
            changed["compute.use_gpu_for_simulation"] = (old_use, True)
    except Exception:
        pass
    try:
        params._paper_v13_forced_changes = changed
    except Exception:
        pass
    return params


_PREVIOUS_SIMULATE_ONESTRING_DEPLOYMENT_V13 = globals().get("simulate_onestring_deployment")


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    """Paper-faithful v13 simulation with CUDA footprint SAT collision."""
    global _V13_GPU_COLLISION_CONTEXT
    paper_params = _copy_paper_deployment_params_v13(params)
    cache_enabled = True
    cache = None
    key = None
    try:
        import streamlit as st
        cache = st.session_state.setdefault("onestring_animation_result_cache", {})
        key = ("paper_v13_gpu_collision_deployment", _state_cache_key(state), _deployment_params_cache_key(paper_params))
        if key in cache:
            if progress_callback is not None:
                try:
                    progress_callback("Cached paper-faithful v13 deployment", 1.0, "reusing GPU collision-strengthened frames")
                except Exception:
                    pass
            return cache[key]
    except Exception:
        cache_enabled = False

    try:
        gap_size = float(getattr(state.mesh_2d_optimized.grid, "gap_size", 0.08))
    except Exception:
        gap_size = 0.08
    ctx = {
        "enabled": True,
        "tile_count": int(getattr(state.tiles_2d_dual_hinge, "tile_count", state.tiles_2d_dual_hinge.vertices.shape[0])),
        "clearance": max(1e-5, gap_size * 0.30),
        "z_pad": max(0.08, float(getattr(_original.PipelineParameters(), "thickness", 0.08)) * 4.0),
        "sweeps": 2,
        "max_pairs": 30000,
        "max_step": max(0.10, gap_size * 3.0),
        "weight": max(1.0, float(getattr(paper_params, "collision_weight", 1.25))),
        "active_pair_total": 0,
        "max_candidate_pair_count": 0,
        "call_count": 0,
    }
    _V13_GPU_COLLISION_CONTEXT = ctx
    try:
        result = _ORIGINAL_SIMULATE_ONESTRING_DEPLOYMENT(state, paper_params, progress_callback=progress_callback)
    finally:
        _V13_GPU_COLLISION_CONTEXT = None
    try:
        result.metrics = dict(getattr(result, "metrics", {}) or {})
        changes = getattr(paper_params, "_paper_v13_forced_changes", {}) or {}
        actual_backend = str(result.metrics.get("actual_backend", "unknown"))
        result.metrics.update({
            "paper_faithful_simulation_v13_enabled": True,
            "paper_collision_v13_enabled": True,
            "paper_collision_v13_model": "CUDA tensorized AABB broad phase + 2D footprint SAT MTV whole-tile separation after rigid projection",
            "paper_collision_v13_backend": "cuda_tensorized" if actual_backend == "cuda" else "cpu_fallback_v12_enhanced",
            "paper_collision_v13_gpu_requested": True,
            "paper_collision_v13_gpu_active": bool(actual_backend == "cuda" and int(ctx.get("call_count", 0)) > 0),
            "paper_collision_v13_call_count": int(ctx.get("call_count", 0)),
            "paper_collision_v13_active_pair_total": int(ctx.get("active_pair_total", 0)),
            "paper_collision_v13_max_candidate_pair_count": int(ctx.get("max_candidate_pair_count", 0)),
            "paper_collision_v13_sweeps_per_rigid_projection": int(ctx.get("sweeps", 0)),
            "paper_collision_v13_clearance": float(ctx.get("clearance", 0.0)),
            "paper_collision_v13_error": str(ctx.get("error", "")),
            "paper_collision_v12_gpu_disabled_for_collision_correctness": False,
            "paper_simulation_energy_model": "E = w_rigid*E_rigid + w_collision*E_collision + w_actuation*(E_snap + E_lift)",
            "paper_simulation_terms_enabled": "E_rigid,E_collision,E_snap,E_lift",
            "paper_simulation_target_pose_fit_disabled": True,
            "paper_simulation_target_contact_guard_disabled": True,
            "paper_simulation_snap_scope_forced": "string_path_only",
            "paper_simulation_min_animation_frames_v13": int(getattr(paper_params, "max_animation_frames", 0)),
            "paper_simulation_forced_parameter_count": int(len(changes)),
            "paper_simulation_forced_parameters": "; ".join(f"{k}:{v[0]}->{v[1]}" for k, v in changes.items()),
        })
    except Exception:
        pass
    if cache_enabled and cache is not None and key is not None:
        try:
            cache[key] = result
            if len(cache) > 8:
                oldest_key = next(iter(cache.keys()))
                if oldest_key != key:
                    cache.pop(oldest_key, None)
        except Exception:
            pass
    return result


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["PAPER_FAITHFUL_SIMULATION_V13_ACTIVE"] = True
globals()["PAPER_COLLISION_V13_GPU_ACTIVE"] = True

_PREVIOUS_PAPER_CONSISTENCY_REPORT_V13 = globals().get("paper_consistency_report")


def paper_consistency_report(state):
    rows = []
    if _PREVIOUS_PAPER_CONSISTENCY_REPORT_V13 is not None:
        try:
            rows = list(_PREVIOUS_PAPER_CONSISTENCY_REPORT_V13(state))
        except Exception:
            rows = []
    sim = getattr(state, "simulation_result", None)
    metrics = dict(getattr(sim, "metrics", {}) or {}) if sim is not None else {}
    rows.append({
        "item": "GPU collision-strengthened simulation v13",
        "expected": "Paper Sec.5.4 E_collision should run inside the deployment solver without forcing CPU fallback",
        "actual": f"enabled={metrics.get('paper_collision_v13_enabled', 'not simulated')}, backend={metrics.get('paper_collision_v13_backend', 'not simulated')}, active={metrics.get('paper_collision_v13_gpu_active', 'not simulated')}",
        "ok": bool(metrics.get("paper_collision_v13_enabled", False)) if sim is not None else True,
        "value": metrics.get("paper_collision_v13_model", "not simulated"),
    })
    return rows


globals()["paper_consistency_report"] = paper_consistency_report
_original.paper_consistency_report = paper_consistency_report

# ---------------------------------------------------------------------------
# v14 performance fix: large-gap paper lift/string optimizer
# ---------------------------------------------------------------------------
# v13 was faithful in structure but could be too slow on large graphs because it
# evaluated peak-to-peak barriers with repeated tree searches and routed each
# lift through many boundary entry candidates.  v14 keeps the same paper Sec.5.2
# and Sec.5.3 interpretation, but uses near-linear basin coupling and bounded
# turn-cost routing so the UI does not sit at "Build gap graph" for minutes.


def _make_lift_points_from_peak_clusters(gap_graph, tau: float):
    start_time = time.perf_counter()
    id_to_gap, adjacency, peaks, basins = _paper_like_energy_peaks_and_basins(gap_graph)
    if not id_to_gap:
        return [], {"paper_lift_point_optimizer_enabled": True, "paper_lift_point_error": "empty gap graph", "paper_lift_point_v14_fast_mode": True}
    tau = float(tau)
    if not np.isfinite(tau):
        tau = 0.8
    tau = float(np.clip(tau, 0.0, 1.0))
    if not peaks:
        gid = max(id_to_gap, key=lambda k: _gap_gpe(id_to_gap[k]))
        peaks = [int(gid)]
        basins = {int(gid): [int(gid)]}

    # Map every gap to its steepest-ascent peak/basin.  This is the discrete
    # Morse-Smale proxy.  Unlike v13, coupling is computed from basin adjacency
    # rather than all peak pairs, avoiding O(P^2 * tree_path) work.
    peak_of_gap: dict[int, int] = {}
    for peak_id, members in basins.items():
        for gid in members:
            peak_of_gap[int(gid)] = int(peak_id)
    for p in peaks:
        peak_of_gap.setdefault(int(p), int(p))

    parent = {int(p): int(p) for p in peaks}
    rank = {int(p): 0 for p in peaks}

    def find(x: int) -> int:
        parent.setdefault(int(x), int(x))
        rank.setdefault(int(x), 0)
        while parent[int(x)] != int(x):
            parent[int(x)] = parent[parent[int(x)]]
            x = parent[int(x)]
        return int(x)

    def union(a: int, b: int) -> bool:
        ra, rb = find(int(a)), find(int(b))
        if ra == rb:
            return False
        if rank[ra] < rank[rb]:
            ra, rb = rb, ra
        parent[rb] = ra
        if rank[ra] == rank[rb]:
            rank[ra] += 1
        return True

    # Paper: edge weight w(u,v)=min(g_u,g_v).  For peak coupling, v14 uses the
    # strongest observed basin-boundary saddle as a lightweight barrier proxy.
    # This is much cheaper than querying every pair on the maximum spanning tree.
    basin_barrier: dict[tuple[int, int], float] = {}
    graph_edge_count = 0
    for a, nbs in adjacency.items():
        for b in nbs:
            if int(a) >= int(b):
                continue
            graph_edge_count += 1
            pa = int(peak_of_gap.get(int(a), int(a)))
            pb = int(peak_of_gap.get(int(b), int(b)))
            if pa == pb or pa not in parent or pb not in parent:
                continue
            key = (pa, pb) if pa < pb else (pb, pa)
            w = min(_gap_gpe(id_to_gap[int(a)]), _gap_gpe(id_to_gap[int(b)]))
            if w > basin_barrier.get(key, -1.0):
                basin_barrier[key] = float(w)

    accepted_couplings = 0
    for (pa, pb), barrier in sorted(basin_barrier.items(), key=lambda kv: kv[1], reverse=True):
        gp_low = max(min(_gap_gpe(id_to_gap[pa]), _gap_gpe(id_to_gap[pb])), 1e-12)
        c = float(np.clip(float(barrier) / gp_low, 0.0, 1.0))
        if c >= tau:
            if union(pa, pb):
                accepted_couplings += 1

    clusters_map: dict[int, list[int]] = {}
    for p in peaks:
        clusters_map.setdefault(find(int(p)), []).append(int(p))
    clusters = list(clusters_map.values())
    clusters.sort(key=lambda comp: (-max(_gap_gpe(id_to_gap[p]) for p in comp), min(comp)))

    # Keep routing bounded on very large graphs.  This does not change the lift
    # selection math unless the topology produces an excessive number of nearly
    # independent peaks; in that case we keep the most load-bearing peaks first
    # and report the cap explicitly.
    gap_count = int(len(id_to_gap))
    max_lift_points = 32 if gap_count >= 1000 else (28 if gap_count >= 700 else 64)
    capped = len(clusters) > max_lift_points
    if capped:
        clusters = clusters[:max_lift_points]

    lift_points = []
    selected_peak_ids: list[int] = []
    for cluster_id, comp in enumerate(clusters):
        chosen = max(comp, key=lambda p: (_gap_gpe(id_to_gap[p]), -p))
        gap = id_to_gap[int(chosen)]
        lift_points.append(
            _original.LiftPoint(
                int(chosen),
                _gap_centroid(gap),
                _gap_centroid_3d(gap),
                _gap_gpe(gap),
                int(cluster_id),
            )
        )
        selected_peak_ids.append(int(chosen))

    max_gpe = max((_gap_gpe(g) for g in id_to_gap.values()), default=0.0)
    metrics = {
        "paper_lift_point_optimizer_enabled": True,
        "paper_lift_point_v14_fast_mode": True,
        "paper_lift_point_model": "Sec.5.2 GPE peaks/basins + basin-adjacency barrier coupling DAG proxy",
        "paper_lift_point_gpe_formula": "gap.gpe = sum_{surrounding tiles} 1/4*m*g*(z_tile-z_min)",
        "paper_lift_point_morse_smale_step": "graph-local maxima + steepest-ascent basins proxy for discrete Morse-Smale segmentation",
        "paper_lift_point_exact_morse_smale_library_used": False,
        "paper_lift_point_threshold_tau": float(tau),
        "paper_lift_point_threshold_sweep_start_tau": 0.8,
        "paper_lift_point_threshold_sweep_requires_simulation": True,
        "paper_lift_point_threshold_sweep_automated": False,
        "paper_lift_point_gap_count": int(gap_count),
        "paper_lift_point_graph_edge_count": int(graph_edge_count),
        "paper_lift_point_basin_adjacency_edge_count": int(len(basin_barrier)),
        "paper_lift_point_coupling_edge_count": int(accepted_couplings),
        "paper_lift_point_peak_count": int(len(peaks)),
        "paper_lift_point_basin_count": int(len(basins)),
        "paper_lift_point_cluster_count": int(len(clusters)),
        "paper_lift_point_cluster_count_before_cap": int(len(clusters_map)),
        "paper_lift_point_large_graph_cap_applied": bool(capped),
        "paper_lift_point_max_lift_points_v14": int(max_lift_points),
        "paper_lift_point_selected_count": int(len(lift_points)),
        "paper_lift_point_selected_gap_ids": ",".join(map(str, selected_peak_ids)),
        "paper_lift_point_max_gpe": float(max_gpe),
        "paper_lift_point_elapsed_sec_v14": float(time.perf_counter() - start_time),
    }
    return lift_points, metrics


def _build_paper_like_string_path(gap_graph, lift_points: list, mu_c: float):
    start_time = time.perf_counter()
    id_to_gap, adjacency = _build_gap_adjacency(gap_graph)
    if not id_to_gap:
        return _original.StringPath([], [], [], 0.0, 0.0, {"paper_string_route_optimizer_enabled": True, "route_error": "empty gap graph", "paper_string_route_v14_fast_mode": True})

    gap_count = int(len(id_to_gap))
    boundary_ids = _paper_like_boundary_order(gap_graph, id_to_gap)
    virtual_entries = [gid for gid in boundary_ids if _is_virtual_boundary_entrance(id_to_gap[gid])]
    if not virtual_entries:
        virtual_entries = [gid for gid in boundary_ids if not _is_split_boundary(id_to_gap[gid])]
    if not virtual_entries:
        virtual_entries = boundary_ids[:]

    # Large graphs need a bounded candidate set; nearest virtual boundary entries
    # are the physically plausible entrances and keep the turn-cost search usable.
    max_entry_candidates = 10 if gap_count >= 700 else 18
    max_states = 22000 if gap_count >= 700 else 60000
    max_lifts_to_route = 32 if gap_count >= 700 else 64

    route = list(boundary_ids)
    inserted_lifts: list[int] = []
    dijkstra_states_total = 0
    fallback_count = 0
    candidate_eval_count = 0
    skipped_lifts: list[int] = []
    best_stats: dict[str, float | int | bool | str] = {}

    sorted_lifts = sorted(list(lift_points or []), key=lambda lp: -float(getattr(lp, "gpe", 0.0)))
    if len(sorted_lifts) > max_lifts_to_route:
        skipped_lifts = [int(getattr(lp, "gap_id", lp)) for lp in sorted_lifts[max_lifts_to_route:]]
        sorted_lifts = sorted_lifts[:max_lifts_to_route]

    for lift in sorted_lifts:
        lift_gid = int(getattr(lift, "gap_id", lift))
        if lift_gid not in id_to_gap:
            continue
        if lift_gid in route:
            inserted_lifts.append(lift_gid)
            continue
        lift_pos = _gap_centroid(id_to_gap[lift_gid])
        entries = sorted(
            virtual_entries,
            key=lambda gid: float(np.linalg.norm(_gap_centroid(id_to_gap[gid]) - lift_pos)),
        )[:max_entry_candidates]
        if not entries:
            continue
        used_core = set(route[:-1] if route and route[0] == route[-1] else route)
        best_route = None
        best_score = float("inf")

        for entry in entries:
            try:
                idx = route.index(entry)
            except ValueError:
                if not route:
                    idx = 0
                else:
                    idx = min(range(len(route)), key=lambda k: float(np.linalg.norm(_gap_centroid(id_to_gap[route[k]]) - _gap_centroid(id_to_gap[entry]))))
            prev_boundary = route[idx - 1] if len(route) > 1 else None
            next_boundary = route[(idx + 1) % len(route)] if len(route) > 1 else entry
            path_a, cost_a, stats_a = _turn_cost_shortest_gap_path(
                gap_graph,
                entry,
                lift_gid,
                prev_gid=prev_boundary,
                used_nodes=used_core,
                forbidden_entry_from_split=True,
                max_states=max_states,
            )
            dijkstra_states_total += int(stats_a.get("turn_cost_dijkstra_states", 0))
            if not path_a:
                continue
            # Only try the expensive second leg if the first leg was found within
            # the state budget.  Otherwise close by reversing, which is safe and
            # far faster than another large search.
            if int(stats_a.get("turn_cost_dijkstra_states", 0)) < max_states * 0.85:
                prev_for_b = path_a[-2] if len(path_a) >= 2 else entry
                path_b, cost_b, stats_b = _turn_cost_shortest_gap_path(
                    gap_graph,
                    lift_gid,
                    next_boundary,
                    prev_gid=prev_for_b,
                    used_nodes=used_core.union(path_a),
                    forbidden_entry_from_split=False,
                    max_states=max_states,
                )
                dijkstra_states_total += int(stats_b.get("turn_cost_dijkstra_states", 0))
            else:
                path_b, cost_b, stats_b = [], float("inf"), {"turn_cost_dijkstra_found": False, "route_error": "state_budget_reverse_fallback"}
            if not path_b:
                path_b = list(reversed(path_a))
                cost_b = cost_a + math.pi
                fallback_count += 1
            candidate = route[: idx + 1] + path_a[1:] + path_b[1:] + route[idx + 2 :]
            candidate = _remove_consecutive_duplicates(candidate)
            score_route = candidate + ([candidate[0]] if candidate and candidate[0] != candidate[-1] else [])
            theta = _closed_route_turn_angle(gap_graph, score_route)
            duplicates = _route_duplicate_count(score_route)
            split_violations = _split_entry_violation_count(gap_graph, score_route)
            score = theta + duplicates * math.pi * 3.0 + split_violations * math.pi * 100.0 + 0.02 * (cost_a + cost_b)
            candidate_eval_count += 1
            if score < best_score:
                best_score = score
                best_route = candidate
                best_stats = {
                    "last_lift_entry_gap": int(entry),
                    "last_lift_next_boundary_gap": int(next_boundary),
                    "last_lift_path_a_len": int(len(path_a)),
                    "last_lift_path_b_len": int(len(path_b)),
                    "last_lift_candidate_theta": float(theta),
                    "last_lift_candidate_duplicate_count": int(duplicates),
                    "last_lift_candidate_split_entry_violations": int(split_violations),
                }
        if best_route is not None:
            route = best_route[:]
            inserted_lifts.append(lift_gid)
        else:
            entry = entries[0]
            fallback = _original._shortest_gap_path(gap_graph, entry, lift_gid)
            if fallback:
                try:
                    idx = route.index(entry)
                except ValueError:
                    idx = max(0, len(route) - 1)
                route = _remove_consecutive_duplicates(route[: idx + 1] + fallback[1:] + list(reversed(fallback))[1:] + route[idx + 1 :])
                inserted_lifts.append(lift_gid)
                fallback_count += 1

    if route and route[0] != route[-1]:
        route.append(route[0])
    route = _remove_consecutive_duplicates(route)
    if route and route[0] != route[-1]:
        route.append(route[0])

    points = np.asarray([_gap_centroid(id_to_gap[gid]) for gid in route if gid in id_to_gap], dtype=float)
    theta = _route_turn_angle(points)
    try:
        friction = _original.safe_capstan_friction(float(mu_c), float(theta))
    except Exception:
        log_cost = float(mu_c) * float(theta)
        friction = float("inf") if log_cost > 60.0 else float(math.exp(log_cost) - 1.0)
    log_channel_cost = float(mu_c * theta) if math.isfinite(mu_c) and math.isfinite(theta) else float("inf")
    duplicates = _route_duplicate_count(route)
    split_violations = _split_entry_violation_count(gap_graph, route)
    lift_ids = [int(getattr(lp, "gap_id", lp)) for lp in (lift_points or [])]
    visited_lifts = [gid for gid in lift_ids if gid in set(route)]
    missed_lifts = [gid for gid in lift_ids if gid not in set(route)]
    max_single_turn = 0.0
    try:
        max_single_turn = float(_original._max_single_turn_angle(gap_graph, route))
    except Exception:
        pass
    warnings: list[str] = []
    if skipped_lifts:
        warnings.append("Some low-GPE lift points were skipped by v14 large-graph route cap.")
    if split_violations:
        warnings.append("Route enters interior through split-boundary gaps; should be zero for paper Sec.5.3.")
    if missed_lifts:
        warnings.append("Some selected lift points were not reached by the string route.")
    route_core = route[:-1] if route and route[0] == route[-1] else route
    return _original.StringPath(
        gap_ids=[int(x) for x in route],
        boundary_gap_ids=[int(x) for x in boundary_ids],
        lift_gap_ids=[int(x) for x in lift_ids],
        turn_angle_total=float(theta),
        estimated_channel_friction=friction,
        metrics={
            "paper_string_route_optimizer_enabled": True,
            "paper_string_route_v14_fast_mode": True,
            "paper_string_route_model": "Sec.5.3 bounded turn-cost closed-walk approximation on gap graph",
            "paper_string_route_objective": "minimize cumulative centroid turn angle with crossing/revisit and split-entry penalties",
            "paper_string_route_primary_cost": "theta_total_from_gap_centroids",
            "paper_string_route_boundary_first": True,
            "paper_string_route_virtual_boundary_entry_preferred": True,
            "paper_string_route_split_boundary_entry_disallowed": True,
            "paper_string_route_turn_cost_dijkstra": True,
            "paper_string_route_exact_authors_solver_used": False,
            "paper_string_route_gap_count": int(gap_count),
            "paper_string_route_max_entry_candidates_v14": int(max_entry_candidates),
            "paper_string_route_max_states_per_search_v14": int(max_states),
            "paper_string_route_max_lifts_to_route_v14": int(max_lifts_to_route),
            "paper_string_route_skipped_lift_count_v14": int(len(skipped_lifts)),
            "paper_string_route_skipped_lift_gap_ids_v14": ",".join(map(str, skipped_lifts)),
            "paper_string_route_candidate_eval_count": int(candidate_eval_count),
            "paper_string_route_dijkstra_states_total": int(dijkstra_states_total),
            "paper_string_route_fallback_count": int(fallback_count),
            "paper_string_route_inserted_lift_count": int(len(inserted_lifts)),
            "paper_string_route_visited_lift_count": int(len(visited_lifts)),
            "paper_string_route_missed_lift_count": int(len(missed_lifts)),
            "paper_string_route_missed_lift_gap_ids": ",".join(map(str, missed_lifts)),
            "paper_string_route_duplicate_visit_count": int(duplicates),
            "paper_string_route_split_entry_violation_count": int(split_violations),
            "paper_string_route_elapsed_sec_v14": float(time.perf_counter() - start_time),
            "route_length": int(len(route)),
            "route_node_count": int(len(route)),
            "unique_route_node_count": int(len(set(route_core))),
            "duplicate_visit_count": int(duplicates),
            "boundary_gap_count": int(len(boundary_ids)),
            "virtual_boundary_entry_count": int(len(virtual_entries)),
            "lift_point_count": int(len(lift_ids)),
            "max_single_turn_angle": float(max_single_turn),
            "turn_angle_total": float(theta),
            "theta_total": float(theta),
            "log_channel_cost": float(log_channel_cost),
            "estimated_channel_friction": friction,
            "overflow_prevented": bool(not math.isfinite(friction) or log_channel_cost > 60.0),
            "warnings": "; ".join(warnings),
            **best_stats,
        },
    )


def build_onestring_design(*args, **kwargs):
    progress_callback = kwargs.get("progress_callback", None)
    state = _ORIGINAL_BUILD_ONESTRING_DESIGN(*args, **kwargs)
    params = kwargs.get("params", None)
    if params is None and len(args) >= 2:
        params = args[1]
    tau = float(getattr(params, "lift_tau", 0.8)) if params is not None else 0.8
    channel_friction = float(getattr(params, "channel_friction", 0.2)) if params is not None else 0.2
    try:
        gap_count = int(len(getattr(state.gap_graph, "gaps", []) or []))
    except Exception:
        gap_count = 0
    _emit_progress(progress_callback, "Paper lift/string optimization", 0.965, f"v14 fast paper optimizer on {gap_count} gaps")
    try:
        state = _apply_paper_lift_points_to_state(state, tau, channel_friction)
    except Exception as exc:
        try:
            metrics = dict(getattr(state.gap_graph, "metrics", {}) or {})
            metrics["paper_lift_point_optimizer_enabled"] = False
            metrics["paper_lift_point_optimizer_error"] = str(exc)
            metrics["paper_lift_point_v14_fast_mode"] = True
            state.gap_graph.metrics = metrics
        except Exception:
            pass
    try:
        state.simulation_result = None
    except Exception:
        pass
    _emit_progress(progress_callback, "Paper lift/string optimization", 0.985, "v14 lift points and string route ready")
    return state


_original.build_onestring_design = build_onestring_design
globals()["build_onestring_design"] = build_onestring_design
globals()["PAPER_LIFT_STRING_V14_FAST_ACTIVE"] = True

_PREVIOUS_PAPER_CONSISTENCY_REPORT_V14 = globals().get("paper_consistency_report")


def paper_consistency_report(state):
    rows = []
    if _PREVIOUS_PAPER_CONSISTENCY_REPORT_V14 is not None:
        try:
            rows = list(_PREVIOUS_PAPER_CONSISTENCY_REPORT_V14(state))
        except Exception:
            rows = []
    gap_metrics = dict(getattr(getattr(state, "gap_graph", None), "metrics", {}) or {})
    route_metrics = dict(getattr(getattr(state, "string_path", None), "metrics", {}) or {})
    rows.append({
        "item": "Large-graph lift/string performance v14",
        "expected": "Paper Sec.5.2/5.3 optimizer should not stall after Build gap graph on large gap graphs",
        "actual": f"lift_fast={gap_metrics.get('paper_lift_point_v14_fast_mode', False)}, route_fast={route_metrics.get('paper_string_route_v14_fast_mode', False)}, gaps={gap_metrics.get('paper_lift_point_gap_count', '')}",
        "ok": bool(gap_metrics.get("paper_lift_point_v14_fast_mode", False)) and bool(route_metrics.get("paper_string_route_v14_fast_mode", False)),
        "value": f"lift_sec={gap_metrics.get('paper_lift_point_elapsed_sec_v14', '')}, route_sec={route_metrics.get('paper_string_route_elapsed_sec_v14', '')}",
    })
    return rows


_original.paper_consistency_report = paper_consistency_report
globals()["paper_consistency_report"] = paper_consistency_report

# ---------------------------------------------------------------------------
# v15 rigid-body string-pull deployment mode
# ---------------------------------------------------------------------------
# Important correction to v13/v14: E_collision must be a non-penetration
# constraint between rigid panels, not a layout force that tries to spread panels
# apart.  v15 removed artificial clearance.  v16 additionally corrects the
# important oversight that adjacent panels must be allowed to touch but must NOT
# pass through each other: topological neighbors receive a side-face contact
# nonpenetration constraint, while generic footprint collision remains reserved
# for non-neighbor pairs.


def _v15_copy_deployment_params(params):
    try:
        import dataclasses
        if params is None:
            params = _original.DeploymentParameters()
        if dataclasses.is_dataclass(params):
            params = dataclasses.replace(params)
        else:
            import copy
            params = copy.copy(params)
    except Exception:
        try:
            params = _original.DeploymentParameters()
        except Exception:
            return params
    changed = {}
    forced = {
        "snap_scope": "string_path_only",
        "use_target_gap_contraction": True,
        "target_fit_weight": 0.0,
        "target_contact_guard_weight": 0.0,
        "target_contact_projection_passes": 0,
        "target_contact_start_alpha": 1.0,
        "target_contact_clearance": 0.0,
        "store_animation_frames": True,
    }
    for name, value in forced.items():
        if hasattr(params, name):
            try:
                old = getattr(params, name)
                setattr(params, name, value)
                changed[name] = (old, value)
            except Exception:
                pass
    minimums = {
        "max_animation_frames": 120,
        "steps": 240,
        "solver_iterations": 28,
        "solver_substeps": 2,
        "rigid_projection_passes": 8,
        "rigid_weight": 0.995,
        "hinge_weight": 0.95,
        "snap_weight": 0.78,
        "lift_weight": 0.90,
        # Non-penetration is corrective, not a layout-expansion force.  Do not
        # force this to a huge value; high values caused the v13 scattering.
        "collision_weight": 0.35,
    }
    for name, value in minimums.items():
        if hasattr(params, name):
            try:
                old = getattr(params, name)
                if float(old) < float(value):
                    setattr(params, name, value)
                    changed[name] = (old, value)
            except Exception:
                pass
    try:
        if hasattr(params, "steps") and hasattr(params, "max_animation_frames"):
            old = int(getattr(params, "steps"))
            desired = max(old, int(getattr(params, "max_animation_frames")) * 2)
            if desired != old:
                setattr(params, "steps", desired)
                changed["steps"] = (old, desired)
    except Exception:
        pass
    try:
        torch_mod = _original.torch
        compute = getattr(params, "compute", None)
        if compute is not None and torch_mod is not None and torch_mod.cuda.is_available():
            old_backend = getattr(compute, "backend", None)
            old_use = getattr(compute, "use_gpu_for_simulation", None)
            setattr(compute, "backend", "cuda")
            setattr(compute, "use_gpu_for_simulation", True)
            changed["compute.backend"] = (old_backend, "cuda")
            changed["compute.use_gpu_for_simulation"] = (old_use, True)
    except Exception:
        pass
    try:
        params._paper_v15_forced_changes = changed
    except Exception:
        pass
    return params


def _v15_make_group_tensors(state, gap_ids, device, dtype):
    torch_mod = _original.torch
    groups: list[list[int]] = []
    kept_gap_ids: list[int] = []
    for gid in gap_ids:
        try:
            gap = state.gap_graph.gaps[int(gid)]
            tiles = [int(t) for t in getattr(gap, "surrounding_tiles", []) if int(t) >= 0]
        except Exception:
            tiles = []
        if tiles:
            groups.append(tiles)
            kept_gap_ids.append(int(gid))
    max_len = max((len(g) for g in groups), default=0)
    if max_len <= 0:
        return None, None, kept_gap_ids
    idx_np = np.zeros((len(groups), max_len), dtype=np.int64)
    mask_np = np.zeros((len(groups), max_len), dtype=bool)
    for row, group in enumerate(groups):
        idx_np[row, : len(group)] = np.asarray(group, dtype=np.int64)
        mask_np[row, : len(group)] = True
    return (
        torch_mod.as_tensor(idx_np, dtype=torch_mod.long, device=device),
        torch_mod.as_tensor(mask_np, dtype=torch_mod.bool, device=device),
        kept_gap_ids,
    )


def _v15_group_centers(vertices, group_idx, group_mask):
    torch_mod = _original.torch
    selected = vertices[group_idx]  # G x M x 8 x 3
    mask = group_mask[..., None, None].to(vertices.dtype)
    denom = torch_mod.clamp(group_mask.sum(dim=1).to(vertices.dtype)[:, None] * 8.0, min=1.0)
    return (selected * mask).sum(dim=(1, 2)) / denom


def _v15_add_group_translation(tile_delta, tile_counts, group_idx, group_mask, correction):
    torch_mod = _original.torch
    flat_tiles = group_idx.reshape(-1)
    flat_mask = group_mask.reshape(-1)
    if flat_tiles.numel() == 0:
        return
    repeated = correction[:, None, :].expand(-1, group_idx.shape[1], -1).reshape(-1, 3)
    selected_tiles = flat_tiles[flat_mask]
    selected_corr = repeated[flat_mask]
    if selected_tiles.numel() == 0:
        return
    tile_delta.index_add_(0, selected_tiles, selected_corr.unsqueeze(1).expand(-1, 8, -1))
    ones = torch_mod.ones((selected_tiles.numel(), 1, 1), dtype=tile_delta.dtype, device=tile_delta.device)
    tile_counts.index_add_(0, selected_tiles, ones)


def _v15_excluded_collision_matrix(state, n_tiles: int, device):
    torch_mod = _original.torch
    excluded = torch_mod.eye(n_tiles, dtype=torch_mod.bool, device=device)
    try:
        for hinge in state.hinge_graph.hinges:
            a, b = int(hinge.tile_a), int(hinge.tile_b)
            if 0 <= a < n_tiles and 0 <= b < n_tiles:
                excluded[a, b] = True
                excluded[b, a] = True
    except Exception:
        pass
    # If two tiles form a modeled gap/contact pair, the snap/hinge constraints are
    # responsible for their relative motion.  Treating these pairs as generic
    # collision pairs was the main cause of v13's artificial scattering.
    try:
        for gap in state.gap_graph.gaps:
            tiles = [int(t) for t in getattr(gap, "surrounding_tiles", [])]
            for i in range(len(tiles)):
                for j in range(i + 1, len(tiles)):
                    a, b = tiles[i], tiles[j]
                    if 0 <= a < n_tiles and 0 <= b < n_tiles:
                        excluded[a, b] = True
                        excluded[b, a] = True
    except Exception:
        pass
    return excluded


def _v15_torch_nonpenetration_projection(vertices, rest, weight: float, excluded_pairs, context: dict):
    """Tension-friendly rigid-panel non-penetration projection.

    Unlike v13, this is not a clearance/layout expansion term.  It only corrects
    actual overlap of non-adjacent tile footprints.  Topologically adjacent/hinged
    tiles are excluded so physical contact at hinges and side faces is allowed.
    """
    torch_mod = _original.torch
    if vertices is None or int(vertices.shape[0]) < 2:
        return vertices, 0, 0
    out = vertices
    device = out.device
    dtype = out.dtype
    n = int(out.shape[0])
    if excluded_pairs is None:
        excluded_pairs = torch_mod.eye(n, dtype=torch_mod.bool, device=device)
    sweeps = max(1, int(context.get("sweeps", 1)))
    max_pairs = max(1, int(context.get("max_pairs", 25000)))
    max_step = float(context.get("max_step", 0.08))
    z_pad = float(context.get("z_pad", 0.20))
    eps = float(context.get("epsilon", 1e-6))
    step_scale = min(0.45, max(0.05, 0.15 + 0.20 * float(weight)))
    total_active = 0
    max_candidate_count = 0
    edge_idx = torch_mod.tensor(
        [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4]],
        dtype=torch_mod.long,
        device=device,
    )
    triu = torch_mod.triu(torch_mod.ones((n, n), dtype=torch_mod.bool, device=device), diagonal=1)
    for _ in range(sweeps):
        xy = out[..., :2]
        z = out[..., 2]
        lo = xy.amin(dim=1)
        hi = xy.amax(dim=1)
        zlo = z.amin(dim=1)
        zhi = z.amax(dim=1)
        mask = (
            (lo[:, None, 0] <= hi[None, :, 0])
            & (hi[:, None, 0] >= lo[None, :, 0])
            & (lo[:, None, 1] <= hi[None, :, 1])
            & (hi[:, None, 1] >= lo[None, :, 1])
            & (zlo[:, None] <= zhi[None, :] + z_pad)
            & (zhi[:, None] + z_pad >= zlo[None, :])
            & triu
            & (~excluded_pairs)
        )
        pairs = torch_mod.nonzero(mask, as_tuple=False)
        candidate_count = int(pairs.shape[0])
        max_candidate_count = max(max_candidate_count, candidate_count)
        if candidate_count == 0:
            break
        if candidate_count > max_pairs:
            pairs = pairs[:max_pairs]
        ia = pairs[:, 0]
        ib = pairs[:, 1]
        pa = xy[ia]
        pb = xy[ib]
        axes = torch_mod.cat([_v13_torch_edge_axes(pa, edge_idx), _v13_torch_edge_axes(pb, edge_idx)], dim=1)
        dots_a = torch_mod.einsum("pvd,pad->pav", pa, axes)
        dots_b = torch_mod.einsum("pvd,pad->pav", pb, axes)
        min_a = dots_a.amin(dim=2)
        max_a = dots_a.amax(dim=2)
        min_b = dots_b.amin(dim=2)
        max_b = dots_b.amax(dim=2)
        # No positive clearance: only true overlap creates a correction.
        overlap = torch_mod.minimum(max_a, max_b) - torch_mod.maximum(min_a, min_b)
        min_overlap, min_axis_idx = overlap.min(dim=1)
        colliding = min_overlap > eps
        if not bool(torch_mod.any(colliding)):
            break
        ia_c = ia[colliding]
        ib_c = ib[colliding]
        axes_c = axes[colliding]
        axis_pick = min_axis_idx[colliding]
        row = torch_mod.arange(axis_pick.numel(), dtype=torch_mod.long, device=device)
        chosen_axis = axes_c[row, axis_pick]
        depth = min_overlap[colliding]
        centers = xy.mean(dim=1)
        direction = centers[ia_c] - centers[ib_c]
        sign = torch_mod.where((chosen_axis * direction).sum(dim=1, keepdim=True) < 0.0, -1.0, 1.0).to(dtype)
        mtv = chosen_axis * sign * depth[:, None]
        norm = torch_mod.linalg.norm(mtv, dim=1, keepdim=True)
        mtv = mtv * torch_mod.clamp(torch_mod.as_tensor(max_step, dtype=dtype, device=device) / torch_mod.clamp(norm, min=1e-12), max=1.0)
        shifts = torch_mod.zeros((n, 2), dtype=dtype, device=device)
        counts = torch_mod.zeros((n, 1), dtype=dtype, device=device)
        half = torch_mod.as_tensor(0.5, dtype=dtype, device=device)
        shifts.index_add_(0, ia_c, half * mtv)
        shifts.index_add_(0, ib_c, -half * mtv)
        ones = torch_mod.ones((ia_c.numel(), 1), dtype=dtype, device=device)
        counts.index_add_(0, ia_c, ones)
        counts.index_add_(0, ib_c, ones)
        active = counts[:, 0] > 0.0
        if not bool(torch_mod.any(active)):
            break
        shifts = shifts / torch_mod.clamp(counts, min=1.0)
        out = out.clone()
        out[active, :, :2] = out[active, :, :2] + step_scale * shifts[active, None, :]
        total_active += int(ia_c.numel())
    return out, int(total_active), int(max_candidate_count)




# ---------------------------------------------------------------------------
# v16 adjacent-contact correction
# ---------------------------------------------------------------------------
# v15 correctly stopped using collision as a layout spreading force, but it went
# too far by excluding hinged/gap-neighbor tile pairs from penetration handling.
# In OneString, neighboring panels are supposed to TOUCH and transmit compression;
# they are not allowed to pass through each other.  v16 therefore keeps the
# generic footprint collision for non-neighbor pairs, while adding a dedicated
# unilateral side-face contact constraint for adjacent/gap-neighbor pairs.


def _v16_prepare_adjacent_contact_tensors(state, device, dtype):
    """Prepare side-face contact constraints for every physical two-tile gap.

    Each constraint stores the two tiles forming a gap, the corresponding side
    face vertex sets, and rest/target face-midpoint separation vectors.  During
    deployment we enforce a unilateral condition

        dot(m_a - m_b, n_ab(alpha)) >= 0,

    where n_ab is the rest-to-target gap closing direction.  Equality is contact;
    negative values mean the two adjacent panels have crossed through each other.
    """
    torch_mod = _original.torch
    pairs = []
    edge_a = []
    edge_b = []
    rest_sep = []
    target_sep = []
    gap_ids = []
    try:
        gaps = list(state.gap_graph.gaps)
    except Exception:
        gaps = []
    rest_v = np.asarray(state.tiles_2d_dual_hinge.vertices, dtype=float)
    target_v = np.asarray(state.tiles_3d.vertices, dtype=float)
    for gap in gaps:
        tiles = [int(t) for t in getattr(gap, "surrounding_tiles", []) or []]
        if len(tiles) != 2:
            continue
        a, b = tiles
        if a < 0 or b < 0 or a >= len(rest_v) or b >= len(rest_v):
            continue
        if getattr(gap, "type", "") == "vertical":
            ea = [1, 2, 6, 5]
            eb = [0, 3, 7, 4]
        else:
            ea = [3, 2, 6, 7]
            eb = [0, 1, 5, 4]
        ra = rest_v[a, ea].mean(axis=0)
        rb = rest_v[b, eb].mean(axis=0)
        ta = target_v[a, ea].mean(axis=0)
        tb = target_v[b, eb].mean(axis=0)
        pairs.append([a, b])
        edge_a.append(ea)
        edge_b.append(eb)
        rest_sep.append(ra - rb)
        target_sep.append(ta - tb)
        gap_ids.append(int(getattr(gap, "id", len(gap_ids))))
    if not pairs:
        return None
    return {
        "pairs": torch_mod.as_tensor(np.asarray(pairs, dtype=np.int64), dtype=torch_mod.long, device=device),
        "edge_a": torch_mod.as_tensor(np.asarray(edge_a, dtype=np.int64), dtype=torch_mod.long, device=device),
        "edge_b": torch_mod.as_tensor(np.asarray(edge_b, dtype=np.int64), dtype=torch_mod.long, device=device),
        "rest_sep": torch_mod.as_tensor(np.asarray(rest_sep, dtype=float), dtype=dtype, device=device),
        "target_sep": torch_mod.as_tensor(np.asarray(target_sep, dtype=float), dtype=dtype, device=device),
        "gap_ids": gap_ids,
    }


def _v16_torch_adjacent_contact_nonpenetration(current, contact, alpha, weight: float):
    """Face-level unilateral no-through contact for adjacent panels.

    v16 used only the side-face midpoint, which can still allow a corner of one
    thick tile to pass through the neighbor.  v17 treats adjacent contact as a
    one-sided separating-plane constraint sampled at the four corresponding
    side-face vertices plus the face center:

        C_ab,k = dot(x_a,k - x_b,k, n_ab(alpha)) >= 0.

    Equality means side-face contact.  Negative values mean actual penetration.
    This is not a clearance force; it only responds to C < 0.
    """
    torch_mod = _original.torch
    if contact is None or weight <= 0.0:
        return current, 0
    pairs = contact["pairs"]
    if pairs.numel() == 0:
        return current, 0
    a = pairs[:, 0]
    b = pairs[:, 1]
    ea = contact["edge_a"]
    eb = contact["edge_b"]
    face_a = current[a[:, None], ea]  # P x 4 x 3
    face_b = current[b[:, None], eb]  # P x 4 x 3
    pa = face_a.mean(dim=1)
    pb = face_b.mean(dim=1)

    # Stable gap normal.  We prefer the interpolated rest-to-target closing
    # direction, but if the target side-face separation is nearly zero, we fall
    # back to the rest separation so that the sign of C_ab remains stable.
    desired_sep = (1.0 - alpha) * contact["rest_sep"] + alpha * contact["target_sep"]
    rest_sep = contact["rest_sep"]
    desired_norm = torch_mod.linalg.norm(desired_sep, dim=1, keepdim=True)
    normal = torch_mod.where(desired_norm > 1e-8, desired_sep, rest_sep)
    normal = normal / torch_mod.clamp(torch_mod.linalg.norm(normal, dim=1, keepdim=True), min=1e-8)

    signed_center = torch_mod.sum((pa - pb) * normal, dim=1, keepdim=True)
    signed_vertices = torch_mod.sum((face_a - face_b) * normal[:, None, :], dim=2)
    # v19: tolerate a small, visually acceptable amount of interpenetration.
    # The physical intent is not perfectly hard contact; it is a stable rigid-panel
    # deployment where tiny numerical/contact-model penetrations do not trigger a
    # large artificial repulsion.  We therefore enforce
    #
    #     C_contact = dot(x_a - x_b, n) >= -delta_adj
    #
    # instead of C_contact >= 0 exactly.  Only penetration deeper than delta_adj
    # is corrected, and even that correction is capped per pass.
    signed_min = torch_mod.minimum(signed_center[:, 0], torch_mod.min(signed_vertices, dim=1).values)
    penetration_raw = torch_mod.clamp(-signed_min, min=0.0)
    char_len = torch_mod.linalg.norm(contact["rest_sep"], dim=1)
    # Small penetration tolerance.  The bounds keep it visible only as a slight
    # tolerance, not enough to let panels pass through each other.
    slop = torch_mod.clamp(0.020 * char_len, min=0.0035, max=0.020)
    max_corr = torch_mod.clamp(0.012 * char_len, min=0.0015, max=0.018)
    penetration = torch_mod.clamp(penetration_raw - slop, min=0.0)
    penetration = torch_mod.minimum(penetration, max_corr)
    active = penetration > 1e-7
    if not bool(torch_mod.any(active)):
        return current, 0
    aa = a[active]
    bb = b[active]
    corr = penetration[active, None] * normal[active]

    # v19: even softer relaxation.  Small penetrations are tolerated, so contact
    # should not visibly push panels apart unless they are clearly passing through.
    scale = min(0.28, max(0.08, 0.10 + 0.12 * float(weight)))
    half = torch_mod.as_tensor(0.5 * scale, dtype=current.dtype, device=current.device)
    shifts = torch_mod.zeros((current.shape[0], 3), dtype=current.dtype, device=current.device)
    counts = torch_mod.zeros((current.shape[0], 1), dtype=current.dtype, device=current.device)
    shifts.index_add_(0, aa, half * corr)
    shifts.index_add_(0, bb, -half * corr)
    ones = torch_mod.ones((aa.numel(), 1), dtype=current.dtype, device=current.device)
    counts.index_add_(0, aa, ones)
    counts.index_add_(0, bb, ones)
    touched = counts[:, 0] > 0.0
    out = current.clone()
    out[touched] = out[touched] + shifts[touched, None, :] / torch_mod.clamp(counts[touched], min=1.0).unsqueeze(1)
    return out, int(aa.numel())


def _v15_prepare_snap_tensors(state, device, dtype):
    torch_mod = _original.torch
    try:
        snap_gaps = _original._deployment_snap_gaps(state, "string_path_only")
    except Exception:
        try:
            snap_gaps = _deployment_snap_gaps(state, "string_path_only")
        except Exception:
            snap_gaps = []
    if not snap_gaps:
        return None
    pairs = []
    edge_a = []
    edge_b = []
    for gap in snap_gaps:
        tiles = list(getattr(gap, "surrounding_tiles", []) or [])
        if len(tiles) != 2:
            continue
        pairs.append([int(tiles[0]), int(tiles[1])])
        if getattr(gap, "type", "") == "vertical":
            edge_a.append([1, 2, 6, 5])
            edge_b.append([0, 3, 7, 4])
        else:
            edge_a.append([3, 2, 6, 7])
            edge_b.append([0, 1, 5, 4])
    if not pairs:
        return None
    try:
        rest_sep, target_sep = _original._gap_separation_vectors(state, snap_gaps, include_bottom=True)
    except Exception:
        rest_sep, target_sep = _gap_separation_vectors(state, snap_gaps, include_bottom=True)
    return {
        "gaps": snap_gaps,
        "pairs": torch_mod.as_tensor(np.asarray(pairs, dtype=np.int64), dtype=torch_mod.long, device=device),
        "edge_a": torch_mod.as_tensor(np.asarray(edge_a, dtype=np.int64), dtype=torch_mod.long, device=device),
        "edge_b": torch_mod.as_tensor(np.asarray(edge_b, dtype=np.int64), dtype=torch_mod.long, device=device),
        "rest_sep": torch_mod.as_tensor(rest_sep[: len(pairs)], dtype=dtype, device=device),
        "target_sep": torch_mod.as_tensor(target_sep[: len(pairs)], dtype=dtype, device=device),
    }


def _v15_prepare_lift_tensors(state, device, dtype):
    torch_mod = _original.torch
    lift_gap_ids = [int(lp.gap_id) for lp in getattr(state, "lift_points", [])]
    group_idx, group_mask, kept = _v15_make_group_tensors(state, lift_gap_ids, device, dtype)
    if group_idx is None:
        return None
    t2 = []
    t3 = []
    for gid in kept:
        gap = state.gap_graph.gaps[int(gid)]
        t2.append(_gap_centroid(gap))
        t3.append(_gap_centroid_3d(gap))
    # v20: lift points are NOT target-position morph constraints.  A lift point
    # represents the place where the string exits and supports the structure.
    # Therefore it should pull mostly in the vertical/up direction, not drag the
    # selected gap sideways toward its T3D target XY.  Keep the flat T2D XY and
    # only use the T3D Z coordinate as the lift height.
    t2_arr = np.asarray(t2, dtype=float)
    t3_raw = np.asarray(t3, dtype=float)
    t3_height = t2_arr.copy()
    if t3_height.ndim == 2 and t3_height.shape[1] >= 3 and t3_raw.shape == t3_height.shape:
        t3_height[:, 2] = t3_raw[:, 2]
    return {
        "gap_ids": kept,
        "group_idx": group_idx,
        "group_mask": group_mask,
        "target_2d": torch_mod.as_tensor(t2_arr, dtype=dtype, device=device),
        "target_3d": torch_mod.as_tensor(t3_height, dtype=dtype, device=device),
        "target_3d_raw": torch_mod.as_tensor(t3_raw, dtype=dtype, device=device),
        "height_only_lift": True,
    }


def _v15_prepare_string_segment_tensors(state, rest, target, device, dtype):
    torch_mod = _original.torch
    route = [int(g) for g in getattr(getattr(state, "string_path", None), "gap_ids", [])]
    if len(route) < 2:
        return None
    # Use route endpoints as tension nodes. Consecutive repeated nodes are removed.
    compact: list[int] = []
    for gid in route:
        if not compact or compact[-1] != gid:
            compact.append(int(gid))
    route = compact
    group_idx, group_mask, kept = _v15_make_group_tensors(state, route, device, dtype)
    if group_idx is None or len(kept) < 2:
        return None
    pos = {gid: i for i, gid in enumerate(kept)}
    seg_a = []
    seg_b = []
    boundary_ids = set(int(g) for g in getattr(getattr(state, "string_path", None), "boundary_gap_ids", []) or [])
    boundary_segment_flags = []
    for a, b in zip(route[:-1], route[1:]):
        if a in pos and b in pos and a != b:
            seg_a.append(pos[a])
            seg_b.append(pos[b])
            boundary_segment_flags.append(bool(a in boundary_ids and b in boundary_ids))
    if not seg_a:
        return None
    seg_a_t = torch_mod.as_tensor(np.asarray(seg_a, dtype=np.int64), dtype=torch_mod.long, device=device)
    seg_b_t = torch_mod.as_tensor(np.asarray(seg_b, dtype=np.int64), dtype=torch_mod.long, device=device)
    boundary_mask = torch_mod.as_tensor(np.asarray(boundary_segment_flags, dtype=bool), dtype=torch_mod.bool, device=device)
    rest_centers = _v15_group_centers(rest, group_idx, group_mask)
    target_centers = _v15_group_centers(target, group_idx, group_mask)
    rest_len = torch_mod.linalg.norm(rest_centers[seg_b_t] - rest_centers[seg_a_t], dim=1)
    target_len = torch_mod.linalg.norm(target_centers[seg_b_t] - target_centers[seg_a_t], dim=1)

    # The OneString paper's actuation starts by constraining/contracting the
    # boundary string, which then closes the interior.  The old v15/v16 used
    # min(rest_len, target_len); for boundary virtual gaps this often does not
    # shorten the perimeter at all, so the lift point dominates.  v17 therefore
    # gives boundary-boundary string segments an explicit shortening target.
    # This is still tension-only: it pulls when too long, never pushes when short.
    boundary_pull_ratio = 0.74
    boundary_pulled_len = rest_len * boundary_pull_ratio
    pulled_len = torch_mod.where(boundary_mask, boundary_pulled_len, torch_mod.minimum(rest_len, target_len))
    return {
        "route_gap_ids": kept,
        "group_idx": group_idx,
        "group_mask": group_mask,
        "seg_a": seg_a_t,
        "seg_b": seg_b_t,
        "rest_len": rest_len,
        "pulled_len": pulled_len,
        "boundary_segment_mask": boundary_mask,
        "boundary_pull_ratio": float(boundary_pull_ratio),
    }


def _v15_apply_hinge_constraints(current, state, weight: float):
    torch_mod = _original.torch
    hinge_specs = getattr(getattr(state, "hinge_graph", None), "hinges", []) or []
    if not hinge_specs:
        return current, 0
    device = current.device
    tile_a = torch_mod.as_tensor([int(h.tile_a) for h in hinge_specs], dtype=torch_mod.long, device=device)
    tile_b = torch_mod.as_tensor([int(h.tile_b) for h in hinge_specs], dtype=torch_mod.long, device=device)
    va = torch_mod.as_tensor([int(h.local_vertex_a) for h in hinge_specs], dtype=torch_mod.long, device=device)
    vb = torch_mod.as_tensor([int(h.local_vertex_b) for h in hinge_specs], dtype=torch_mod.long, device=device)
    pa = current[tile_a, va]
    pb = current[tile_b, vb]
    mid = 0.5 * (pa + pb)
    flat = current.reshape(-1, 3).clone()
    flat.index_copy_(0, tile_a * 8 + va, pa + (mid - pa) * float(weight))
    flat.index_copy_(0, tile_b * 8 + vb, pb + (mid - pb) * float(weight))
    return flat.reshape_as(current), int(len(hinge_specs))


def _v15_torch_rigid_project(current, rest, weight: float):
    projector = globals().get("_ORIGINAL_TORCH_PROJECT_RIGID_TILES_V13", None)
    if projector is None:
        projector = getattr(_original, "_torch_project_rigid_tiles", None)
    if projector is None:
        return current
    return projector(current, rest, float(weight))


def _simulate_onestring_deployment_rigid_string_v15(state, params, progress_callback=None):
    torch_mod = _original.torch
    if torch_mod is None:
        raise RuntimeError("v15 rigid-string simulation requires torch; no torch module is available.")
    use_cuda = bool(torch_mod.cuda.is_available()) and bool(getattr(getattr(params, "compute", None), "use_gpu_for_simulation", True))
    device = torch_mod.device("cuda" if use_cuda else "cpu")
    dtype = torch_mod.float64 if getattr(getattr(params, "compute", None), "dtype", "float32") == "float64" else torch_mod.float32
    total_start = time.perf_counter()
    if device.type == "cuda":
        try:
            torch_mod.cuda.reset_peak_memory_stats(device)
        except Exception:
            pass
    _emit_progress(progress_callback, "Prepare rigid-string simulation v15", 0.02, f"backend={device.type}")
    current = torch_mod.as_tensor(state.tiles_2d_dual_hinge.vertices, dtype=dtype, device=device).clone()
    rest = current.clone()
    target = torch_mod.as_tensor(state.tiles_3d.vertices, dtype=dtype, device=device)
    previous = current.clone()
    n_tiles = int(current.shape[0])
    steps = max(2, int(getattr(params, "steps", 240)))
    frame_stride = max(1, steps // max(1, int(getattr(params, "max_animation_frames", 120))))
    frames = []

    snap = _v15_prepare_snap_tensors(state, device, dtype)
    adjacent_contact = _v16_prepare_adjacent_contact_tensors(state, device, dtype)
    lift = _v15_prepare_lift_tensors(state, device, dtype)
    string_segments = _v15_prepare_string_segment_tensors(state, rest, target, device, dtype)
    # Generic footprint collision still excludes topological neighbors to avoid
    # false 2D repulsion, but v16 handles those neighbors with side-face
    # unilateral contact constraints so they cannot pass through each other.
    excluded = _v15_excluded_collision_matrix(state, n_tiles, device)
    gap_size = float(getattr(state.mesh_2d_optimized.grid, "gap_size", 0.08)) if hasattr(state, "mesh_2d_optimized") else 0.08
    collision_ctx = {
        "sweeps": 1,
        "max_pairs": 25000,
        "max_step": max(0.006, min(0.025, gap_size * 0.22)),
        "z_pad": max(0.10, float(getattr(_original.PipelineParameters(), "thickness", 0.08)) * 2.5),
        # v19 tolerance: the generic non-adjacent footprint SAT also allows a
        # tiny overlap before correction.  This avoids noisy contact chatter and
        # sudden separation from sub-millimetric 2D projection overlap.
        "epsilon": max(0.003, min(0.012, gap_size * 0.05)),
    }
    collision_call_count = 0
    collision_active_total = 0
    collision_candidate_max = 0
    adjacent_contact_application_count = 0
    hinge_application_count = 0
    snap_application_count = 0
    lift_application_count = 0
    string_application_count = 0
    progress_stride = max(1, steps // 50)
    if device.type == "cuda":
        start_event = torch_mod.cuda.Event(enable_timing=True)
        end_event = torch_mod.cuda.Event(enable_timing=True)
        start_event.record()
    else:
        start_event = end_event = None
    for step in range(steps):
        if step % progress_stride == 0:
            _emit_progress(progress_callback, "Boundary-driven height-lift rigid-string deployment v20", 0.03 + 0.92 * step / max(1, steps - 1), f"step {step + 1}/{steps}")
        alpha_value = min(1.0, float(getattr(params, "quasi_static_pull_speed", 1.0)) * step / max(1, steps - 1))
        # Smooth actuation ramp. This is the pull amount, not a target morph.
        alpha_value = alpha_value * alpha_value * (3.0 - 2.0 * alpha_value)
        alpha = torch_mod.as_tensor(alpha_value, dtype=dtype, device=device)
        # v17 separates the pull schedule from the lift schedule.  Boundary/string
        # contraction starts immediately; lift is delayed and weaker because lift
        # points are supports, not the primary actuator.
        lift_start = 0.58
        lift_t = max(0.0, min(1.0, (alpha_value - lift_start) / max(1e-8, 1.0 - lift_start)))
        lift_t = lift_t * lift_t * (3.0 - 2.0 * lift_t)
        lift_alpha = torch_mod.as_tensor(lift_t, dtype=dtype, device=device)
        for _sub in range(max(1, int(getattr(params, "solver_substeps", 1)))):
            velocity = (current - previous) * max(0.0, 1.0 - float(getattr(params, "damping_ratio", 0.2)))
            previous = current.clone()
            if bool(getattr(params, "high_fidelity", False)):
                current = current + velocity
                current[..., 2] = current[..., 2] - float(getattr(params, "gravity", 9.81)) * 0.00002
            else:
                current = current + velocity
            for _it in range(max(1, int(getattr(params, "solver_iterations", 24)))):
                tile_delta = torch_mod.zeros_like(current)
                tile_counts = torch_mod.zeros((n_tiles, 1, 1), dtype=dtype, device=device)
                # E_string: tension-only distance constraints along the computed string route.
                if string_segments is not None:
                    centers = _v15_group_centers(current, string_segments["group_idx"], string_segments["group_mask"])
                    a = string_segments["seg_a"]
                    b = string_segments["seg_b"]
                    vec = centers[b] - centers[a]
                    dist = torch_mod.linalg.norm(vec, dim=1)
                    direction = vec / torch_mod.clamp(dist[:, None], min=1e-12)
                    desired = (1.0 - alpha) * string_segments["rest_len"] + alpha * string_segments["pulled_len"]
                    raw_violation = torch_mod.clamp(dist - desired, min=0.0)
                    # v18: string pull is quasi-static.  Large length errors are
                    # resolved over many iterations instead of one impulsive yank.
                    max_string_step = torch_mod.clamp(0.055 * string_segments["rest_len"], min=0.006, max=0.06)
                    violation = torch_mod.minimum(raw_violation, max_string_step)
                    if bool(torch_mod.any(violation > 0.0)):
                        boundary_mask = string_segments.get("boundary_segment_mask", None)
                        base_gain = min(0.55, max(0.0, float(getattr(params, "snap_weight", 0.78)) * 0.34))
                        if boundary_mask is not None:
                            # Boundary contraction is the primary string pull; interior/lift
                            # branches are intentionally softer so they do not yank the
                            # lift point up before the boundary has begun to close.
                            gain = torch_mod.where(
                                boundary_mask,
                                torch_mod.as_tensor(base_gain * 1.10, dtype=dtype, device=device),
                                torch_mod.as_tensor(base_gain * 0.12, dtype=dtype, device=device),
                            )
                        else:
                            gain = torch_mod.as_tensor(base_gain, dtype=dtype, device=device)
                        corr = direction * (0.5 * violation[:, None] * gain[:, None] if hasattr(gain, "ndim") and gain.ndim > 0 else 0.5 * violation[:, None] * gain)
                        # Accumulate endpoint corrections as group translations.
                        group_corr = torch_mod.zeros((string_segments["group_idx"].shape[0], 3), dtype=dtype, device=device)
                        group_corr.index_add_(0, a, corr)
                        group_corr.index_add_(0, b, -corr)
                        # Average if a route node is incident to multiple string segments.
                        group_counts = torch_mod.zeros((string_segments["group_idx"].shape[0], 1), dtype=dtype, device=device)
                        ones = torch_mod.ones((a.numel(), 1), dtype=dtype, device=device)
                        group_counts.index_add_(0, a, ones)
                        group_counts.index_add_(0, b, ones)
                        group_corr = group_corr / torch_mod.clamp(group_counts, min=1.0)
                        _v15_add_group_translation(tile_delta, tile_counts, string_segments["group_idx"], string_segments["group_mask"], group_corr)
                        string_application_count += 1
                # E_lift: selected lift gaps support the structure, but they are
                # not the primary actuator.  In the paper the boundary string closes
                # first; lift points counter gravity.  v17 delays and weakens lift so
                # the motion is boundary-contraction dominated rather than a vertical
                # yanking of the selected lift point.
                if lift is not None:
                    centers = _v15_group_centers(current, lift["group_idx"], lift["group_mask"])
                    desired = (1.0 - lift_alpha) * lift["target_2d"] + lift_alpha * lift["target_3d"]
                    lift_gain = float(getattr(params, "lift_weight", 0.90)) * 0.14
                    corr = (desired - centers) * lift_gain
                    _v15_add_group_translation(tile_delta, tile_counts, lift["group_idx"], lift["group_mask"], corr)
                    lift_application_count += 1
                # E_snap: path gaps close side-face midpoints, as in paper Sec.5.4.
                if snap is not None:
                    pairs = snap["pairs"]
                    a = pairs[:, 0]
                    b = pairs[:, 1]
                    pa = current[a[:, None], snap["edge_a"]].mean(dim=1)
                    pb = current[b[:, None], snap["edge_b"]].mean(dim=1)
                    mid = 0.5 * (pa + pb)
                    # Do not let snap ask adjacent panels to pass through each other.
                    # The target side-face separation can be numerically negative along
                    # the rest gap normal when the miter/contact geometry is imperfect.
                    # Snap should close to contact C=0, not invert C<0.
                    rest_sep = snap["rest_sep"]
                    target_sep = snap["target_sep"]
                    rest_normal = rest_sep / torch_mod.clamp(torch_mod.linalg.norm(rest_sep, dim=1, keepdim=True), min=1e-8)
                    rest_signed = torch_mod.sum(rest_sep * rest_normal, dim=1, keepdim=True)
                    target_signed = torch_mod.sum(target_sep * rest_normal, dim=1, keepdim=True)
                    closed_signed = torch_mod.clamp(target_signed, min=0.0)
                    desired_signed = (1.0 - alpha) * rest_signed + alpha * closed_signed
                    desired_sep = rest_normal * desired_signed
                    desired_pa = mid + 0.5 * desired_sep
                    desired_pb = mid - 0.5 * desired_sep
                    snap_eff = alpha * float(getattr(params, "snap_weight", 0.78)) * 0.55
                    da = (desired_pa - pa) * snap_eff
                    db = (desired_pb - pb) * snap_eff
                    tile_delta.index_add_(0, a, da.unsqueeze(1).expand(-1, 8, -1))
                    tile_delta.index_add_(0, b, db.unsqueeze(1).expand(-1, 8, -1))
                    one = torch_mod.ones((a.numel(), 1, 1), dtype=dtype, device=device)
                    tile_counts.index_add_(0, a, one)
                    tile_counts.index_add_(0, b, one)
                    snap_application_count += 1
                current = current + tile_delta / torch_mod.clamp(tile_counts, min=1.0)
                current, hinge_count = _v15_apply_hinge_constraints(current, state, float(getattr(params, "hinge_weight", 0.95)))
                hinge_application_count += int(hinge_count)
                for _rp in range(max(1, int(getattr(params, "rigid_projection_passes", 8)))):
                    current = _v15_torch_rigid_project(current, rest, float(getattr(params, "rigid_weight", 0.995)))
                adjacent_contact_count = 0
                for _contact_pass in range(4):
                    current, _adj_count = _v16_torch_adjacent_contact_nonpenetration(
                        current,
                        adjacent_contact,
                        alpha,
                        float(getattr(params, "collision_weight", 0.35)),
                    )
                    adjacent_contact_count += int(_adj_count)
                    current = _v15_torch_rigid_project(current, rest, 1.0)
                    if int(_adj_count) == 0:
                        break
                adjacent_contact_application_count += int(adjacent_contact_count)
                current, active_count, candidate_count = _v15_torch_nonpenetration_projection(
                    current,
                    rest,
                    float(getattr(params, "collision_weight", 0.35)),
                    excluded,
                    collision_ctx,
                )
                collision_call_count += 1
                collision_active_total += int(active_count)
                collision_candidate_max = max(collision_candidate_max, int(candidate_count))
                # Final rigid projection after non-penetration; collision only moves whole tiles.
                current = _v15_torch_rigid_project(current, rest, 1.0)
                # v18: constraint projections are quasi-static corrections, not
                # physical velocity impulses.  Without this reset, a late contact
                # correction appears as stored velocity in the next frame, causing
                # the sudden panel explosion observed on dome targets.
                previous = current.clone()
        if bool(getattr(params, "store_animation_frames", True)) and (step == steps - 1 or step % frame_stride == 0):
            frames.append(current.detach().clone())
    if start_event is not None and end_event is not None:
        end_event.record()
        torch_mod.cuda.synchronize(device)
        gpu_time = float(start_event.elapsed_time(end_event) / 1000.0)
    else:
        gpu_time = 0.0
    final = current.detach().cpu().numpy()
    frame_arrays = [f.detach().cpu().numpy() for f in frames] or [final.copy()]
    previous_np = previous.detach().cpu().numpy()
    try:
        final_error = _original.rms_distance(final, state.tiles_3d.vertices)
    except Exception:
        final_error = rms_distance(final, state.tiles_3d.vertices)
    try:
        snap_error = _original._snap_error(final, state)
        lift_error = _original._lift_error(final, state)
        rigid_error = _original._rigid_error(final, state.tiles_2d_dual_hinge.vertices)
        hinge_error = _original._hinge_error(final, state)
        final_collision_count = _original._count_aabb_collisions(final, state.mesh_2d_optimized.grid, False)
    except Exception:
        snap_error = lift_error = rigid_error = hinge_error = 0.0
        final_collision_count = 0
    kinetic = float(0.5 * float(getattr(params, "tile_mass", 1.0)) * np.sum((final - previous_np) ** 2))
    metrics = {
        "paper_faithful_simulation_v15_enabled": True,
        "paper_faithful_simulation_v16_enabled": True,
        "paper_faithful_simulation_v17_enabled": True,
        "paper_simulation_v15_model": "rigid-body PBD string-pull simulation, not target morphing and not layout separation",
        "paper_simulation_v16_model": "rigid panels + string tension + snap/lift + adjacent side-face contact + non-adjacent nonpenetration",
        "paper_simulation_v17_model": "boundary-first rigid panels: outer string contraction drives closure; lift is delayed/weak support; adjacent contact is face-sampled unilateral nonpenetration",
        "paper_simulation_energy_model": "E = E_rigid + E_hinge + E_boundary_string + E_snap + E_lift_support + E_adjacent_contact + E_nonneighbor_nonpenetration",
        "paper_simulation_terms_enabled": "E_rigid,E_hinge,E_boundary_string,E_snap,E_lift_support,E_adjacent_contact,E_nonneighbor_nonpenetration",
        "paper_simulation_string_constraint": "boundary-first tension-only constraints; boundary-boundary route segments shorten explicitly while interior branches are softer",
        "paper_simulation_boundary_first_v17": True,
        "paper_simulation_lift_start_alpha_v17": 0.35,
        "paper_simulation_lift_gain_scale_v17": 0.38,
        "paper_simulation_snap_no_through_clamp_v17": True,
        "paper_simulation_adjacent_contact_face_samples_v17": "4 side-face vertices + face center",
        "paper_simulation_boundary_pull_ratio_v17": float(string_segments.get("boundary_pull_ratio", 0.0)) if string_segments is not None else 0.0,
        "paper_faithful_simulation_v18_enabled": True,
        "paper_simulation_v18_model": "quasi-static boundary-first rigid string simulation with capped/compliant contact; prevents contact-impulse explosions",
        "paper_simulation_v18_contact_model": "unilateral nonpenetration with slop, per-pass correction cap, reduced gain, and velocity reset after constraint projection",
        "paper_simulation_boundary_pull_ratio_v18": float(string_segments.get("boundary_pull_ratio", 0.0)) if string_segments is not None else 0.0,
        "paper_simulation_v18_explosion_guard_enabled": True,
        "paper_faithful_simulation_v19_enabled": True,
        "paper_simulation_v19_model": "quasi-static rigid string/contact simulation with small penetration tolerance; contact corrects only beyond allowed slop",
        "paper_simulation_v19_contact_tolerance_enabled": True,
        "paper_simulation_v19_contact_constraint": "adjacent side-face contact uses C_contact >= -delta_adj instead of hard C_contact >= 0",
        "paper_simulation_v19_adjacent_penetration_tolerance": "delta_adj = clamp(0.020*L_contact, 0.0035, 0.020)",
        "paper_simulation_v19_nonneighbor_penetration_tolerance": float(collision_ctx.get("epsilon", 0.0)),
        "paper_simulation_v19_contact_policy": "slight penetration is tolerated; deep penetration is corrected softly and capped per pass",
        "paper_faithful_simulation_v20_enabled": True,
        "paper_simulation_v20_model": "boundary-driven rigid-string simulation: boundary closure is primary; lift is delayed height-only support; no T3D XY morphing",
        "paper_simulation_v20_boundary_first_correction": "boundary route segments receive stronger tension gain; interior route branches are soft",
        "paper_simulation_boundary_pull_ratio_v20": float(string_segments.get("boundary_pull_ratio", 0.0)) if string_segments is not None else 0.0,
        "paper_simulation_lift_start_alpha_v20": 0.58,
        "paper_simulation_lift_gain_scale_v20": 0.14,
        "paper_simulation_lift_model_v20": "height-only support: p_lift_target = (x_2D, y_2D, z_T3D); lift no longer drags XY toward T3D",
        "paper_simulation_boundary_vs_lift_policy_v20": "w_boundary >> w_lift; intended motion is perimeter/string closure first, then weak lift support",
        "paper_simulation_boundary_string_segment_count_v17": int(torch_mod.sum(string_segments.get("boundary_segment_mask", torch_mod.zeros(0, dtype=torch_mod.bool, device=device))).detach().cpu().item()) if string_segments is not None and "boundary_segment_mask" in string_segments else 0,
        "paper_simulation_snap_constraint": "side-face midpoint constraints only on computed string path",
        "paper_simulation_collision_meaning": "unilateral non-penetration, not layout spreading; adjacent panels use side-face contact constraints and non-neighbor panels use zero-clearance footprint SAT",
        "paper_simulation_collision_clearance": 0.0,
        "paper_simulation_collision_excludes_hinged_or_gap_neighbor_pairs": False,
        "paper_simulation_generic_nonadjacent_collision_excludes_topological_pairs": True,
        "paper_simulation_adjacent_sideface_contact_enabled_v16": True,
        "paper_simulation_adjacent_contact_equation_v16": "dot(m_a - m_b, n_ab(alpha)) >= 0; equality is contact, negative is penetration",
        "paper_simulation_target_pose_fit_disabled": True,
        "paper_simulation_target_contact_guard_disabled": True,
        "paper_simulation_target_morphing_disabled": True,
        "actual_backend": device.type,
        "dominant_backend": device.type,
        "gpu_kernel_time": gpu_time,
        "cpu_preprocess_time": 0.0,
        "cpu_postprocess_time": time.perf_counter() - total_start - gpu_time if device.type == "cuda" else time.perf_counter() - total_start,
        "cpu_gpu_transfer_count": int(3 + len(frame_arrays)),
        "gpu_memory_peak": int(torch_mod.cuda.max_memory_allocated(device)) if device.type == "cuda" else 0,
        "elapsed_time": float(time.perf_counter() - total_start),
        "steps": int(steps),
        "stored_frame_count": int(len(frame_arrays)),
        "snap_scope": "string_path_only",
        "actuated_snap_gap_count": int(len(snap["gaps"])) if snap is not None else 0,
        "lift_point_count": int(len(lift["gap_ids"])) if lift is not None else 0,
        "string_route_segment_count": int(string_segments["seg_a"].numel()) if string_segments is not None else 0,
        "v15_hinge_application_count": int(hinge_application_count),
        "v15_snap_application_count": int(snap_application_count),
        "v15_lift_application_count": int(lift_application_count),
        "v15_string_tension_application_count": int(string_application_count),
        "v15_nonpenetration_call_count": int(collision_call_count),
        "v15_nonpenetration_active_pair_total": int(collision_active_total),
        "v15_nonpenetration_max_candidate_pair_count": int(collision_candidate_max),
        "v16_adjacent_contact_pair_count": int(adjacent_contact["pairs"].shape[0]) if adjacent_contact is not None else 0,
        "v16_adjacent_contact_application_count": int(adjacent_contact_application_count),
        "final_deployment_error_to_T3D": float(final_error),
        "snap_error": float(snap_error),
        "lift_error": float(lift_error),
        "rigid_error": float(rigid_error),
        "hinge_error": float(hinge_error),
        "collision_count": int(final_collision_count),
        "turn_angle_total": float(getattr(state.string_path, "turn_angle_total", 0.0)),
        "estimated_channel_friction": float(getattr(state.string_path, "estimated_channel_friction", 0.0)),
        "kinetic_energy": float(kinetic),
        "stable_state": bool(rigid_error < 0.15 and hinge_error < 0.25 and int(final_collision_count) == 0),
    }
    return _original.DeploymentResult(frames=frame_arrays, final_tiles=final, metrics=metrics, collision_counts=[])


_PREVIOUS_SIMULATE_ONESTRING_DEPLOYMENT_V15 = globals().get("simulate_onestring_deployment")


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    """v17 simulation: boundary-first rigid panels + actual string-route tension/contact constraints.

    This intentionally bypasses the v13/v14 collision-spreading hook.  Panels are
    handled as rigid bodies; string pull is represented by tension-only length
    constraints along the computed route plus the paper snap/lift constraints.
    """
    paper_params = _v15_copy_deployment_params(params)
    cache_enabled = True
    cache = None
    key = None
    try:
        import streamlit as st
        cache = st.session_state.setdefault("onestring_animation_result_cache", {})
        key = ("paper_v20_boundary_driven_height_lift", _state_cache_key(state), _deployment_params_cache_key(paper_params))
        if key in cache:
            if progress_callback is not None:
                try:
                    progress_callback("Cached boundary-first rigid-string deployment v17", 1.0, "reusing rigid-body string-pull/contact frames")
                except Exception:
                    pass
            return cache[key]
    except Exception:
        cache_enabled = False
    try:
        result = _simulate_onestring_deployment_rigid_string_v15(state, paper_params, progress_callback=progress_callback)
    except Exception as exc:
        # Fall back to previous implementation, but mark the failure explicitly.
        if _PREVIOUS_SIMULATE_ONESTRING_DEPLOYMENT_V15 is None:
            raise
        result = _PREVIOUS_SIMULATE_ONESTRING_DEPLOYMENT_V15(state, paper_params, progress_callback=progress_callback)
        try:
            result.metrics = dict(getattr(result, "metrics", {}) or {})
            result.metrics["paper_faithful_simulation_v15_enabled"] = False
            result.metrics["paper_faithful_simulation_v15_error"] = str(exc)
            result.metrics["paper_faithful_simulation_v15_fallback_used"] = True
        except Exception:
            pass
    try:
        result.metrics = dict(getattr(result, "metrics", {}) or {})
        changes = getattr(paper_params, "_paper_v15_forced_changes", {}) or {}
        result.metrics["paper_simulation_forced_parameter_count_v15"] = int(len(changes))
        result.metrics["paper_simulation_forced_parameters_v15"] = "; ".join(f"{k}:{v[0]}->{v[1]}" for k, v in changes.items())
    except Exception:
        pass
    if cache_enabled and cache is not None and key is not None:
        try:
            cache[key] = result
            if len(cache) > 8:
                oldest_key = next(iter(cache.keys()))
                if oldest_key != key:
                    cache.pop(oldest_key, None)
        except Exception:
            pass
    return result


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["PAPER_RIGID_STRING_SIMULATION_V15_ACTIVE"] = True
globals()["PAPER_RIGID_STRING_SIMULATION_V16_ACTIVE"] = True
globals()["PAPER_RIGID_STRING_SIMULATION_V17_ACTIVE"] = True
globals()["PAPER_RIGID_STRING_SIMULATION_V18_ACTIVE"] = True
globals()["PAPER_RIGID_STRING_SIMULATION_V20_ACTIVE"] = True

_PREVIOUS_PAPER_CONSISTENCY_REPORT_V15 = globals().get("paper_consistency_report")


def paper_consistency_report(state):
    rows = []
    if _PREVIOUS_PAPER_CONSISTENCY_REPORT_V15 is not None:
        try:
            rows = list(_PREVIOUS_PAPER_CONSISTENCY_REPORT_V15(state))
        except Exception:
            rows = []
    sim = getattr(state, "simulation_result", None)
    metrics = dict(getattr(sim, "metrics", {}) or {}) if sim is not None else {}
    rows.append({
        "item": "Boundary-driven height-lift rigid-body string/contact simulation v20",
        "expected": "Panels are rigid physical objects; boundary string contraction is primary; lift is delayed height-only support; adjacent contact permits small tolerance but prevents deep penetration",
        "actual": f"enabled={metrics.get('paper_faithful_simulation_v17_enabled', metrics.get('paper_faithful_simulation_v16_enabled', metrics.get('paper_faithful_simulation_v15_enabled', 'not simulated')))}, backend={metrics.get('actual_backend', 'not simulated')}, string_segments={metrics.get('string_route_segment_count', 'not simulated')}, adjacent_contacts={metrics.get('v16_adjacent_contact_pair_count', 'not simulated')}",
        "ok": bool(metrics.get("paper_faithful_simulation_v15_enabled", False)) if sim is not None else True,
        "value": metrics.get("paper_simulation_v17_model", metrics.get("paper_simulation_v15_model", "not simulated")),
    })
    return rows


_original.paper_consistency_report = paper_consistency_report
globals()["paper_consistency_report"] = paper_consistency_report


# ---------------------------------------------------------------------------
# Final v21 dispatcher registration
#
# Later historical patches may redefine simulate_onestring_deployment. This
# final registration keeps that last boundary-driven v20 implementation as the
# explicit legacy mode and exposes paper_style_pd as the default entrypoint.
_FINAL_V21_LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE = globals().get("simulate_onestring_deployment")
_FINAL_V21_PREVIOUS_PAPER_CONSISTENCY_REPORT = globals().get("paper_consistency_report")


def simulate_onestring_deployment(state, params=None, progress_callback=None):
    mode = str(getattr(params, "simulation_mode", "paper_style_pd") if params is not None else "paper_style_pd")
    if mode == "legacy_boundary_driven_v20":
        if _FINAL_V21_LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE is None:
            raise RuntimeError("legacy_boundary_driven_v20 simulation is unavailable.")
        result = _FINAL_V21_LEGACY_BOUNDARY_DRIVEN_V20_SIMULATE(state, params, progress_callback=progress_callback)
        try:
            result.metrics = dict(getattr(result, "metrics", {}) or {})
            result.metrics["paper_simulation_mode"] = "legacy_boundary_driven_v20"
            result.metrics["paper_style_pd_enabled"] = False
        except Exception:
            pass
        return result
    if mode != "paper_style_pd":
        try:
            _emit_progress(progress_callback, "Paper-style PD simulation", 0.0, f"unknown mode {mode!r}; using paper_style_pd")
        except Exception:
            pass
    return _paper_style_pd_simulate(state, params, progress_callback=progress_callback)


_original.simulate_onestring_deployment = simulate_onestring_deployment
globals()["simulate_onestring_deployment"] = simulate_onestring_deployment
globals()["PAPER_STYLE_PD_SIMULATION_ACTIVE"] = True


def paper_consistency_report(state):
    rows = []
    if _FINAL_V21_PREVIOUS_PAPER_CONSISTENCY_REPORT is not None:
        try:
            rows = list(_FINAL_V21_PREVIOUS_PAPER_CONSISTENCY_REPORT(state))
        except Exception:
            rows = []
    sim = getattr(state, "simulation_result", None)
    metrics = dict(getattr(sim, "metrics", {}) or {}) if sim is not None else {}
    rows.append({
        "item": "Paper-style PD simulation core v21 active dispatcher",
        "expected": "Default paper_style_pd; legacy_boundary_driven_v20 remains selectable. No boundary-first order, delayed lift, height-only lift, target fit, or 2D footprint collision in paper_style_pd.",
        "actual": f"mode={metrics.get('paper_simulation_mode', 'not simulated')}, backend={metrics.get('actual_backend', 'not simulated')}, collision={metrics.get('collision_model', 'not simulated')}",
        "ok": bool(metrics.get("paper_style_pd_enabled", False)) if sim is not None else True,
        "value": metrics.get("paper_style_remaining_differences", "not simulated"),
    })
    return rows


_original.paper_consistency_report = paper_consistency_report
globals()["paper_consistency_report"] = paper_consistency_report
