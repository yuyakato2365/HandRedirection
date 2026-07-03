# OneString paper alignment patch v21

This tree starts from the v20 side-face/contact patch, but v20's deployment
simulation is now treated as a legacy heuristic.  The default deployment mode is
`paper_style_pd`, a CUDA/Torch local-global approximation of the Section 5.4
energy form:

```text
E_sim(v) =
  w_rigid E_rigid(v)
  + w_collision E_collision(v)
  + w_actuation (E_snap(v) + E_lift(v))
```

## Simulation Modes

- `paper_style_pd`: default.  Solves snap, lift, rigid, and collision terms
  together with one smooth actuation ramp.
- `legacy_boundary_driven_v20`: comparison mode.  Preserves the v20 behavior in
  which perimeter closure is primary and lift is delayed/height-only.

## What Paper-Style PD Does Not Use

The `paper_style_pd` path deliberately bypasses these v20 heuristics:

- independent boundary string contraction
- boundary pull ratio
- route-segment tension propagation
- delayed lift schedule
- height-only lift
- target pose fitting
- target contact guard
- arbitrary visual morphing from T2D to T3D
- 2D footprint SAT collision

Boundary-first routing is still used for string path generation, but not as a
time ordering rule inside the simulator.

## Implemented Terms

`E_snap` closes side-face midpoint pairs on gaps traversed by the computed string
path:

```text
E_snap = sum ||m_a - m_b||^2
```

`E_lift` moves selected lift gap groups toward prescribed 3D lift targets.  It
uses full xyz targets, not `(x_2D, y_2D, z_T3D)`.

`E_rigid` is enforced with the existing per-tile Kabsch/rigid projection and is
reported as `rigid_error_mean` and `rigid_error_max`.

`E_collision` uses a GPU-side approximate 3D convex-prism SAT:

- broad phase: 3D AABB overlap on CUDA tensors
- narrow phase: face normals plus sampled edge-cross-edge axes
- MTV: minimum-overlap SAT axis
- topological exclusion: hinge-connected and gap-adjacent tile pairs are
  excluded from generic SAT and are left to snap/contact constraints
- MTV safety: zero-length cross axes are ignored, the MTV sign follows tile
  center separation, and per-pair correction is clamped

The implementation is marked as `collision_model = "3d_prism_sat_approx"`.

Collision metrics distinguish adjacent and non-adjacent candidates:

```text
collision_pair_count_nonadjacent
collision_pair_count_adjacent_excluded
collision_active_pair_count_nonadjacent
generic_collision_excludes_adjacent_pairs = True
generic_collision_excludes_hinge_connected_pairs = True
collision_mtv_max_correction
collision_sat_invalid_axis_count
```

## Debug Mode

The Streamlit UI now exposes `paper-style 1-step debug frames`.  When enabled,
`paper_style_pd` runs one step using `paper-style debug alpha` and saves phase
frames in this order:

```text
before
after_snap
after_lift
after_rigid
after_collision
after_collision_rigid
```

Metrics also include JSON tables for checking constraint wiring:

```text
paper_style_snap_constraints_debug_json
paper_style_lift_constraints_debug_json
paper_style_debug_frame_labels
```

The snap table records `gap_id`, `tile_a`, `tile_b`, `face_a`, `face_b`,
`m_a_initial`, and `m_b_initial`.  The lift table records `lift_gap_id`,
`affected_tile_ids`, source vertices, target vertices, and `target_xyz`.

## GPU Policy

The `paper_style_pd` solver requires CUDA-enabled PyTorch.  Vertices, targets,
constraint pairs, and collision candidates stay as Torch tensors during the
solver loop.  CPU transfer is reserved for final metrics and visualization frame
export.

Expected metrics:

```text
paper_style_pd_enabled = True
actual_backend = cuda
paper_style_pd_gpu_active = True
paper_style_pd_uses_cpu_collision = False
paper_style_pd_gpu_cpu_transfer_per_step = False
uses_boundary_independent_contraction = False
uses_delayed_lift = False
uses_height_only_lift = False
uses_target_pose_fit = False
uses_2d_footprint_collision = False
```

## Remaining Differences From The Paper

- ShapeOp/libigl is not used; this is a Torch local-global approximation.
- 3D SAT collision uses an approximate MTV projection and capped broad-phase
  candidates for interactive performance.
- Lift targets are taken from the current generated T3D/lift-gap geometry when
  no richer physical target is available.

These differences are also reported in `paper_style_remaining_differences`.

## Dome Acceptance Checks

`tests/test_paper_style_pd_debug.py` builds a small Dome target on CUDA and
checks that:

- isolated/flying tiles are not detected
- hinge-connected tiles do not tear beyond a tile-scale threshold
- max per-step displacement is finite and bounded
- `max_penetration` and `rigid_error_mean` remain finite
- 1-step debug frames and snap/lift debug tables are emitted
