import math

import numpy as np
import pytest

from onestring_physics.input_shape import create_builtin_shape
from onestring_physics.onestring_pipeline import (
    ComputeConfig,
    DeploymentParameters,
    PipelineParameters,
    build_onestring_design,
    simulate_onestring_deployment,
)


def _has_cuda() -> bool:
    try:
        import torch

        return bool(torch.cuda.is_available())
    except Exception:
        return False


@pytest.mark.skipif(not _has_cuda(), reason="paper_style_pd requires CUDA")
def test_paper_style_pd_dome_debug_acceptance():
    target = create_builtin_shape("dome", {"amplitude": 0.45, "radius": 2.0})
    state = build_onestring_design(
        target,
        PipelineParameters(nx=3, max_3d_iterations=4, max_2d_iterations=4, compute=ComputeConfig(backend="cuda")),
    )
    params = DeploymentParameters(
        steps=4,
        solver_iterations=2,
        solver_substeps=1,
        rigid_projection_passes=3,
        store_animation_frames=True,
        max_animation_frames=4,
        compute=ComputeConfig(backend="cuda"),
    )
    setattr(params, "simulation_mode", "paper_style_pd")
    result = simulate_onestring_deployment(state, params)
    metrics = result.metrics

    assert metrics["paper_simulation_mode"] == "paper_style_pd"
    assert metrics["actual_backend"] == "cuda"
    assert metrics["generic_collision_excludes_adjacent_pairs"] is True
    assert metrics["generic_collision_excludes_hinge_connected_pairs"] is True
    assert metrics["collision_pair_count_adjacent_excluded"] >= 0
    assert metrics["collision_pair_count_nonadjacent"] >= 0
    assert metrics["paper_style_flying_tile_count"] == 0
    assert math.isfinite(float(metrics["max_penetration"]))
    assert math.isfinite(float(metrics["rigid_error_mean"]))
    assert math.isfinite(float(metrics["paper_style_max_displacement_per_step"]))

    tile_diag = float(metrics["paper_style_acceptance_tile_diag"])
    assert float(metrics["paper_style_max_displacement_per_step"]) < 8.0 * tile_diag
    assert float(metrics["paper_style_hinge_separation_max"]) < 2.0 * tile_diag
    assert np.all(np.isfinite(result.final_tiles))


@pytest.mark.skipif(not _has_cuda(), reason="paper_style_pd requires CUDA")
def test_paper_style_pd_one_step_debug_frames_and_constraint_tables():
    target = create_builtin_shape("dome", {"amplitude": 0.35, "radius": 2.0})
    state = build_onestring_design(
        target,
        PipelineParameters(nx=3, max_3d_iterations=3, max_2d_iterations=3, compute=ComputeConfig(backend="cuda")),
    )
    params = DeploymentParameters(
        steps=10,
        solver_iterations=1,
        solver_substeps=1,
        rigid_projection_passes=2,
        store_animation_frames=True,
        max_animation_frames=10,
        compute=ComputeConfig(backend="cuda"),
    )
    setattr(params, "simulation_mode", "paper_style_pd")
    setattr(params, "paper_style_debug_one_step", True)
    setattr(params, "paper_style_debug_alpha", 1.0)

    result = simulate_onestring_deployment(state, params)
    metrics = result.metrics

    assert metrics["paper_style_debug_one_step"] is True
    assert metrics["simulation_steps"] == 1
    assert metrics["animation_frames"] >= 5
    assert "after_snap" in metrics["paper_style_debug_frame_labels"]
    assert "after_lift" in metrics["paper_style_debug_frame_labels"]
    assert "after_rigid" in metrics["paper_style_debug_frame_labels"]
    assert "after_collision" in metrics["paper_style_debug_frame_labels"]
    assert metrics["paper_style_debug_snap_rows"] == metrics["snap_constraint_count"]
    assert metrics["paper_style_debug_lift_rows"] == metrics["lift_constraint_count"]
