"""Compatibility app.py for the side-face contact patch plus stable camera animation UI.

This delegates to the backed-up original app.py so the old Copy-Item workflow can
still copy an app.py from the patch zip without requiring a full app copy.

Important fix in this version:
- Plotly's built-in animate() redraws 3D scenes and can reset scene.camera every
  frame.  For 3D figures with frames, this wrapper renders a custom browser-side
  player that updates only trace data via Plotly.restyle().  It creates one Plotly scene and then swaps only geometry arrays, so rotate/zoom/pan are preserved during playback.
"""

from __future__ import annotations

import runpy
import sys
from pathlib import Path


_ROOT = Path(__file__).resolve().parent
_SRC = _ROOT / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))
_CANDIDATES = [
    _ROOT / "app_backup_before_sideface_contact.py",
    _ROOT / "app_backup_before_mitered_t3d.py",
]


def _is_wrapper_app(path: Path) -> bool:
    try:
        head = path.read_text(encoding="utf-8", errors="ignore")[:4000]
    except Exception:
        return False
    markers = [
        "Compatibility app.py for the side-face contact patch",
        "_install_plotly_view_patch",
        "_CANDIDATES = [",
        "runpy.run_path",
    ]
    return sum(marker in head for marker in markers) >= 2


def _find_original_app() -> Path:
    for path in _CANDIDATES:
        if not path.exists() or path.resolve() == Path(__file__).resolve():
            continue
        if _is_wrapper_app(path):
            # Re-applying the patch can accidentally back up the previous wrapper.
            # Do not run it recursively; keep looking for the original app.
            continue
        return path
    tried = "\n  - ".join(str(p) for p in _CANDIDATES)
    raise RuntimeError(
        "Could not find a backed-up original app.py.\n"
        "The backup file appears to be another patch wrapper, so running it would recurse.\n"
        "Restore app.py from GitHub or an older backup, then run:\n"
        "  Copy-Item .\\app.py .\\app_backup_before_sideface_contact.py -Force\n\n"
        f"Tried:\n  - {tried}"
    )



def _install_lift_point_highlight_patch() -> None:
    """Make paper-selected lift points visually obvious in Gap/String views.

    The original app already passes lift_gap_ids into figure_tile_assembly(), but
    visualization.add_gap_graph() only colored those gap markers red at the same
    small size as all other gaps.  This patch overlays large labeled markers and
    vertical callout stems so the selected lift locations are unambiguous.
    """
    try:
        import numpy as np
        import plotly.graph_objects as go
        import onestring_physics.visualization as viz
    except Exception:
        return

    if getattr(viz, "_onestring_lift_highlight_v9_installed", False):
        return

    original_add_gap_graph = viz.add_gap_graph

    def _fmt_float(value, digits: int = 4) -> str:
        try:
            value = float(value)
        except Exception:
            return str(value)
        if not np.isfinite(value):
            return str(value)
        if abs(value) >= 1000 or (abs(value) > 0 and abs(value) < 1e-3):
            return f"{value:.{digits}e}"
        return f"{value:.{digits}g}"

    def highlighted_add_gap_graph(fig, gap_graph, string_path=None, lift_gap_ids=None):
        original_add_gap_graph(fig, gap_graph, string_path=string_path, lift_gap_ids=lift_gap_ids)
        lift_ids = [int(x) for x in (lift_gap_ids or [])]
        if not lift_ids:
            return
        id_to_rank = {gid: idx for idx, gid in enumerate(lift_ids)}
        selected = [gap for gap in getattr(gap_graph, "gaps", []) if int(gap.id) in id_to_rank]
        if not selected:
            return
        metrics = dict(getattr(gap_graph, "metrics", {}) or {})
        tau = metrics.get("paper_lift_point_threshold_tau", metrics.get("lift_tau", ""))
        model = metrics.get("paper_lift_point_model", metrics.get("lift_point_model", "paper GPE/coupling"))
        peak_count = metrics.get("paper_lift_point_peak_count", "")
        cluster_count = metrics.get("paper_lift_point_cluster_count", "")

        xs, ys, zs = [], [], []
        text_labels, hover_text = [], []
        stem_x, stem_y, stem_z = [], [], []
        for gap in selected:
            p = np.asarray(gap.centroid_2d, dtype=float)
            rank = id_to_rank[int(gap.id)]
            z0 = float(p[2]) if p.shape[0] > 2 else 0.0
            z1 = z0 + 0.30
            xs.append(float(p[0])); ys.append(float(p[1])); zs.append(z1)
            text_labels.append(f"L{rank}<br>g{int(gap.id)}")
            hover_text.append(
                "<b>Selected lift point</b>"
                f"<br>rank: L{rank}"
                f"<br>gap_id: {int(gap.id)}"
                f"<br>GPE: {_fmt_float(getattr(gap, 'gpe', 0.0))}"
                f"<br>gap type: {getattr(gap, 'type', '')}"
                f"<br>surrounding tiles: {list(getattr(gap, 'surrounding_tiles', []))}"
                f"<br>selection model: {model}"
                f"<br>tau: {tau}"
                f"<br>peak count: {peak_count}"
                f"<br>cluster count: {cluster_count}"
                "<br><br>Paper-inspired criterion: high-GPE peak selected as the representative of its coupled peak cluster."
            )
            stem_x.extend([float(p[0]), float(p[0]), None])
            stem_y.extend([float(p[1]), float(p[1]), None])
            stem_z.extend([z0 + 0.04, z1, None])

        fig.add_trace(
            go.Scatter3d(
                x=stem_x,
                y=stem_y,
                z=stem_z,
                mode="lines",
                line=dict(color="#ef4444", width=7),
                name="lift point callout stems",
                hoverinfo="skip",
                showlegend=False,
            )
        )
        fig.add_trace(
            go.Scatter3d(
                x=xs,
                y=ys,
                z=zs,
                mode="markers+text",
                marker=dict(
                    size=12,
                    color="#ef4444",
                    symbol="diamond",
                    opacity=1.0,
                    line=dict(color="#fef08a", width=5),
                ),
                text=text_labels,
                textposition="top center",
                textfont=dict(size=15, color="#b91c1c"),
                hovertext=hover_text,
                hoverinfo="text",
                name="SELECTED LIFT POINTS",
                showlegend=True,
            )
        )
        try:
            fig.update_layout(
                legend=dict(
                    orientation="h",
                    yanchor="bottom",
                    y=1.01,
                    xanchor="left",
                    x=0.0,
                )
            )
        except Exception:
            pass

    viz.add_gap_graph = highlighted_add_gap_graph
    viz._onestring_lift_highlight_v9_installed = True

def _install_plotly_view_patch() -> None:
    """Make 3D charts pan-friendly and render animations without camera reset."""
    try:
        import copy
        import json
        import uuid

        import plotly.graph_objects as go
        import plotly.io as pio
        from plotly.utils import PlotlyJSONEncoder
        import streamlit as st
        import streamlit.components.v1 as components
    except Exception:
        return

    if getattr(st, "_onestring_stable_camera_patch_installed", False):
        return

    original_plotly_chart = st.plotly_chart

    def _figure_has_3d_scene(fig) -> bool:
        try:
            if any(getattr(trace, "type", "") in {"mesh3d", "scatter3d", "surface"} for trace in getattr(fig, "data", [])):
                return True
            layout = getattr(fig, "layout", None)
            if layout is not None and getattr(layout, "scene", None) is not None:
                return True
        except Exception:
            return False
        return False

    def _figure_has_frames(fig) -> bool:
        try:
            return bool(getattr(fig, "frames", None)) and len(getattr(fig, "frames", [])) > 0
        except Exception:
            return False

    def _patch_3d_figure(fig):
        if fig is None or not _figure_has_3d_scene(fig):
            return fig
        try:
            fig.update_layout(
                uirevision="onestring-rigid-string-v15",
                transition=dict(duration=0),
            )
            fig.update_scenes(
                uirevision="onestring-rigid-string-v15",
                dragmode="orbit",
            )
        except Exception:
            pass
        return fig

    def _render_camera_stable_animation(fig, *, config: dict | None = None):
        """Render a Plotly 3D animation without Plotly.animate().

        Plotly.animate(frame.redraw=True) can rebuild the WebGL 3D scene and reset
        scene.camera once per frame.  This player preloads the same frame payloads
        but advances by calling Plotly.restyle() on the animated traces only.
        The layout is not re-applied during playback, so the current camera is
        preserved even while the user drags/zooms/pans.
        """
        try:
            _patch_3d_figure(fig)
            fig_dict = fig.to_plotly_json()
            frames = fig_dict.get("frames", []) or []
            if not frames:
                return False

            base_dict = copy.deepcopy(fig_dict)
            base_dict.pop("frames", None)
            layout = base_dict.setdefault("layout", {})
            # Remove Plotly's built-in animation controls.  They use animate(),
            # which is the source of the per-frame camera reset.
            layout.pop("updatemenus", None)
            layout.pop("sliders", None)
            layout["uirevision"] = "onestring-rigid-string-v15"
            layout.setdefault("scene", {})["uirevision"] = "onestring-rigid-string-v15"
            layout.setdefault("scene", {})["dragmode"] = "orbit"
            layout.setdefault("transition", {})["duration"] = 0

            div_id = f"onestring_stable_anim_{uuid.uuid4().hex}"
            base_fig = go.Figure(base_dict)
            chart_config = dict(config or {})
            chart_config.setdefault("scrollZoom", True)
            chart_config.setdefault("displayModeBar", True)
            chart_config.setdefault("responsive", True)
            buttons = list(chart_config.get("modeBarButtonsToAdd", []) or [])
            for name in ["pan3d", "orbitRotation", "tableRotation", "resetCameraDefault3d", "zoom3d"]:
                if name not in buttons:
                    buttons.append(name)
            chart_config["modeBarButtonsToAdd"] = buttons

            chart_html = pio.to_html(
                base_fig,
                include_plotlyjs=True,
                full_html=False,
                config=chart_config,
                div_id=div_id,
            )
            frames_json = json.dumps(frames, cls=PlotlyJSONEncoder)
            height = int(getattr(getattr(fig, "layout", None), "height", None) or 720)
            html = f"""
<div class="onestring-player-wrap">
  <div class="onestring-player-controls">
    <button id="{div_id}_play" type="button">▶ Play</button>
    <button id="{div_id}_pause" type="button">⏸ Pause</button>
    <button id="{div_id}_reset" type="button">⏮ Reset</button>
    <label>frame <span id="{div_id}_label">1</span> / <span id="{div_id}_total">{len(frames)}</span></label>
    <input id="{div_id}_slider" type="range" min="0" max="{max(0, len(frames)-1)}" value="0" step="1" />
    <label>fps <input id="{div_id}_fps" type="number" min="1" max="60" value="10" step="1" /></label>
    <span class="onestring-note">paper-style PD player: frame updates change geometry only; camera is not reset per frame</span>
  </div>
  {chart_html}
</div>
<style>
.onestring-player-wrap {{ width: 100%; }}
.onestring-player-controls {{
  display: flex; align-items: center; gap: 0.55rem; flex-wrap: wrap;
  font-family: sans-serif; font-size: 13px; padding: 0.35rem 0.1rem 0.45rem 0.1rem;
}}
.onestring-player-controls button {{
  border: 1px solid rgba(49,51,63,.22); border-radius: 6px; background: white;
  padding: 0.25rem 0.55rem; cursor: pointer;
}}
.onestring-player-controls input[type=range] {{ min-width: 220px; flex: 1; }}
.onestring-player-controls input[type=number] {{ width: 3.6rem; }}
.onestring-note {{ color: rgba(49,51,63,.62); }}
</style>
<script>
(function() {{
  const gd = document.getElementById({json.dumps(div_id)});
  const frames = {frames_json};
  const slider = document.getElementById({json.dumps(div_id + '_slider')});
  const label = document.getElementById({json.dumps(div_id + '_label')});
  const playButton = document.getElementById({json.dumps(div_id + '_play')});
  const pauseButton = document.getElementById({json.dumps(div_id + '_pause')});
  const resetButton = document.getElementById({json.dumps(div_id + '_reset')});
  const fpsInput = document.getElementById({json.dumps(div_id + '_fps')});
  let frameIndex = 0;
  let timer = null;
  let previousDragMode = 'orbit';
  let middleDrag = null;
  let userInteracting = false;
  let resumeInteractionTimer = null;

  function markUserInteracting(ms) {{
    userInteracting = true;
    if (resumeInteractionTimer) clearTimeout(resumeInteractionTimer);
    resumeInteractionTimer = setTimeout(function() {{
      userInteracting = false;
      }}, ms || 350);
  }}

  function endUserInteracting() {{
    if (resumeInteractionTimer) clearTimeout(resumeInteractionTimer);
    resumeInteractionTimer = null;
    userInteracting = false;
  }}

  function clone(obj) {{
    if (!obj) return obj;
    try {{ return JSON.parse(JSON.stringify(obj)); }} catch (err) {{ return obj; }}
  }}

  function sceneKeys() {{
    const layout = (gd && (gd._fullLayout || gd.layout)) || {{}};
    return Object.keys(layout).filter(k => /^scene[0-9]*$/.test(k));
  }}

  function relayoutDragMode(mode) {{
    if (!window.Plotly || !window.Plotly.relayout) return;
    const update = {{}};
    sceneKeys().forEach(k => {{ update[k + '.dragmode'] = mode; }});
    try {{ window.Plotly.relayout(gd, update); }} catch (err) {{}}
  }}

  function wrapForRestyle(traceData) {{
    const update = {{}};
    const allowed = new Set(['x', 'y', 'z', 'i', 'j', 'k']);
    Object.keys(traceData || {{}}).forEach(k => {{
      if (!allowed.has(k)) return;
      update[k] = [traceData[k]];
    }});
    return update;
  }}

  function applyFrame(i, opts) {{
    if (!frames.length) return;
    opts = opts || {{}};
    if (userInteracting && !opts.force) return;
    i = Math.max(0, Math.min(frames.length - 1, i));
    frameIndex = i;
    slider.value = String(i);
    label.textContent = String(i + 1);
    const frame = frames[i] || {{}};
    const frameData = frame.data || [];
    const frameTraces = frame.traces || frameData.map((_, idx) => idx);
    frameData.forEach((traceData, idx) => {{
      const traceIndex = frameTraces[idx] == null ? idx : frameTraces[idx];
      const update = wrapForRestyle(traceData);
      if (Object.keys(update).length) {{
        try {{ window.Plotly.restyle(gd, update, [traceIndex]); }} catch (err) {{}}
      }}
    }});
  }}

  function play() {{
    if (timer) clearInterval(timer);
    const fps = Math.max(1, Math.min(60, parseInt(fpsInput.value || '10', 10)));
    const interval = Math.max(16, Math.round(1000 / fps));
    timer = setInterval(() => {{
      if (userInteracting) return;
      const next = (frameIndex + 1) % Math.max(1, frames.length);
      applyFrame(next);
    }}, interval);
  }}

  function pause() {{
    if (timer) clearInterval(timer);
    timer = null;
  }}

  function cloneAsLeftMouseEvent(type, e, buttons) {{
    return new MouseEvent(type, {{
      bubbles: true, cancelable: true, view: window,
      screenX: e.screenX, screenY: e.screenY,
      clientX: e.clientX, clientY: e.clientY,
      ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey,
      button: 0, buttons: buttons
    }});
  }}

  // Pause geometry updates while the camera is being manipulated.
  // This covers normal left-drag orbit, modebar pan, wheel zoom, and touch drag.
  gd.addEventListener('mousedown', function(e) {{
    if (e.button === 0 || e.button === 1 || e.button === 2) markUserInteracting(900);
  }}, true);
  gd.addEventListener('wheel', function(e) {{
    markUserInteracting(450);
  }}, true);
  gd.addEventListener('touchstart', function(e) {{
    markUserInteracting(900);
  }}, true);
  gd.addEventListener('touchmove', function(e) {{
    markUserInteracting(450);
  }}, true);
  document.addEventListener('mousemove', function(e) {{
    if (userInteracting && !middleDrag) markUserInteracting(250);
  }}, true);
  document.addEventListener('mouseup', function(e) {{
    if (!middleDrag) endUserInteracting();
  }}, true);
  document.addEventListener('touchend', function(e) {{
    endUserInteracting();
  }}, true);

  gd.addEventListener('auxclick', function(e) {{
    if (e.button === 1) {{ e.preventDefault(); e.stopPropagation(); }}
  }}, true);

  gd.addEventListener('mousedown', function(e) {{
    if (e.button !== 1) return;
    pause();
    markUserInteracting(1200);
    e.preventDefault();
    e.stopPropagation();
    previousDragMode = ((gd.layout || {{}}).scene || {{}}).dragmode || 'orbit';
    middleDrag = {{ target: e.target }};
    relayoutDragMode('pan');
    e.target.dispatchEvent(cloneAsLeftMouseEvent('mousedown', e, 1));
  }}, true);

  document.addEventListener('mousemove', function(e) {{
    if (!middleDrag) return;
    e.preventDefault();
    e.stopPropagation();
    middleDrag.target.dispatchEvent(cloneAsLeftMouseEvent('mousemove', e, 1));
  }}, true);

  document.addEventListener('mouseup', function(e) {{
    if (!middleDrag) return;
    e.preventDefault();
    e.stopPropagation();
    middleDrag.target.dispatchEvent(cloneAsLeftMouseEvent('mouseup', e, 0));
    relayoutDragMode(previousDragMode || 'orbit');
    middleDrag = null;
    endUserInteracting();
  }}, true);

  slider.addEventListener('input', function() {{ pause(); endUserInteracting(); applyFrame(parseInt(slider.value || '0', 10), {{force: true}}); }});
  playButton.addEventListener('click', play);
  pauseButton.addEventListener('click', pause);
  resetButton.addEventListener('click', function() {{ pause(); endUserInteracting(); applyFrame(0, {{force: true}}); }});
  fpsInput.addEventListener('change', function() {{ if (timer) play(); }});

  setTimeout(function() {{ applyFrame(0, {{force: true}}); }}, 100);
}})();
</script>
"""
            components.html(html, height=height + 78, scrolling=False)
            return True
        except Exception as exc:
            try:
                st.warning(f"Camera-stable animation renderer failed; falling back to Streamlit Plotly chart: {exc}")
            except Exception:
                pass
            return False

    def patched_plotly_chart(fig, *args, **kwargs):
        _patch_3d_figure(fig)
        config = dict(kwargs.pop("config", {}) or {})
        config.setdefault("scrollZoom", True)
        config.setdefault("displayModeBar", True)
        config.setdefault("responsive", True)
        buttons = list(config.get("modeBarButtonsToAdd", []) or [])
        for name in ["pan3d", "orbitRotation", "tableRotation", "resetCameraDefault3d", "zoom3d"]:
            if name not in buttons:
                buttons.append(name)
        config["modeBarButtonsToAdd"] = buttons
        kwargs["config"] = config

        # Critical fix: never let Plotly.animate drive 3D animation frames.  It
        # resets scene.camera in this Streamlit setup.  Use our data-only player.
        if fig is not None and _figure_has_3d_scene(fig) and _figure_has_frames(fig):
            rendered = _render_camera_stable_animation(fig, config=config)
            if rendered:
                return None

        return original_plotly_chart(fig, *args, **kwargs)

    st.plotly_chart = patched_plotly_chart
    st._onestring_stable_camera_patch_installed = True

    # For non-animation Streamlit Plotly charts, keep middle-mouse pan available
    # in the parent document as well.
    try:
        components.html(
            r'''
<script>
(function () {
  const root = window.parent && window.parent.document ? window.parent.document : document;
  const win = window.parent || window;
  if (root.__onestringMiddlePanInstalledV7) return;
  root.__onestringMiddlePanInstalledV7 = true;

  function getPlotly() { return win.Plotly || window.Plotly || null; }
  function closestPlot(target) { return target && target.closest ? target.closest('.js-plotly-plot') : null; }
  function sceneKeys(gd) {
    const layout = (gd && (gd._fullLayout || gd.layout)) || {};
    const keys = Object.keys(layout).filter(k => /^scene[0-9]*$/.test(k));
    return keys.length ? keys : ['scene'];
  }
  function relayoutDragMode(gd, mode) {
    const Plotly = getPlotly();
    if (!Plotly || !Plotly.relayout || !gd) return;
    const updates = {};
    sceneKeys(gd).forEach(k => { updates[k + '.dragmode'] = mode; });
    try { Plotly.relayout(gd, updates); } catch (err) {}
  }
  function cloneAsLeft(type, e, buttons) {
    return new MouseEvent(type, {
      bubbles: true, cancelable: true, view: e.view || win,
      screenX: e.screenX, screenY: e.screenY,
      clientX: e.clientX, clientY: e.clientY,
      ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey,
      button: 0, buttons: buttons
    });
  }
  let active = null;
  root.addEventListener('auxclick', function (e) {
    if (e.button === 1 && closestPlot(e.target)) { e.preventDefault(); e.stopPropagation(); }
  }, true);
  root.addEventListener('mousedown', function (e) {
    if (e.button !== 1) return;
    const gd = closestPlot(e.target);
    if (!gd) return;
    e.preventDefault(); e.stopPropagation();
    active = { gd: gd, target: e.target, previousDragmode: ((gd.layout || {}).scene || {}).dragmode || 'orbit' };
    relayoutDragMode(gd, 'pan');
    e.target.dispatchEvent(cloneAsLeft('mousedown', e, 1));
  }, true);
  root.addEventListener('mousemove', function (e) {
    if (!active) return;
    e.preventDefault(); e.stopPropagation();
    active.target.dispatchEvent(cloneAsLeft('mousemove', e, 1));
  }, true);
  root.addEventListener('mouseup', function (e) {
    if (!active) return;
    e.preventDefault(); e.stopPropagation();
    active.target.dispatchEvent(cloneAsLeft('mouseup', e, 0));
    relayoutDragMode(active.gd, active.previousDragmode || 'orbit');
    active = null;
  }, true);
})();
</script>
            ''',
            height=0,
            width=0,
        )
    except Exception:
        return





def _install_v13_default_slider_patch() -> None:
    """Set safer defaults requested by the user for paper-faithful runs.

    This wrapper changes Streamlit widget defaults without editing the recovered
    original app.py.  It also increases the animation frame slider so deployment
    is not limited to coarse 48-frame playback.
    """
    try:
        import streamlit as st
    except Exception:
        return
    if getattr(st, "_onestring_v13_slider_defaults_installed", False):
        return
    original_slider = st.slider

    def _set_slider_args(args, kwargs, *, min_value=None, max_value=None, value=None, step=None):
        args = list(args)
        values = [min_value, max_value, value, step]
        names = ["min_value", "max_value", "value", "step"]
        for idx, val in enumerate(values):
            if val is None:
                continue
            if len(args) > idx:
                args[idx] = val
            else:
                kwargs[names[idx]] = val
        return tuple(args), kwargs

    def patched_slider(label, *args, **kwargs):
        text = str(label)
        low = text.lower()
        # Animation frame density.  The previous 48-ish frame workflow was too
        # coarse for inspecting collisions during deployment.
        if "animation frames" in low or "アニメーションフレーム" in text or "フレーム数" in text:
            args, kwargs = _set_slider_args(args, kwargs, min_value=16, max_value=240, value=120, step=4)
        elif "simulation steps" in low or "シミュレーションステップ" in text:
            args, kwargs = _set_slider_args(args, kwargs, min_value=48, max_value=600, value=240, step=12)
        # Defaults from the user's screenshot.
        elif "ヒンジ接続部の重量" in text or ("hinge" in low and "connection" in low and "weight" in low):
            args, kwargs = _set_slider_args(args, kwargs, value=8.0)
        elif "ヒンジ衝突重量" in text or ("hinge" in low and "collision" in low and "weight" in low):
            args, kwargs = _set_slider_args(args, kwargs, value=4.0)
        elif "ヒンジレイアウトアンカー重量" in text or ("hinge" in low and "anchor" in low and "weight" in low):
            args, kwargs = _set_slider_args(args, kwargs, value=0.0)
        elif "ヒンジレイアウトの初期拡張" in text or ("initial expansion" in low and "hinge" in low):
            args, kwargs = _set_slider_args(args, kwargs, value=1.60)
        elif "ヒンジレイアウト最大センタードリフト" in text or "最大センタードリフト" in text or "max center drift" in low:
            args, kwargs = _set_slider_args(args, kwargs, value=5.0)
        elif "ヒンジレイアウト時間予算" in text or "time budget" in low:
            args, kwargs = _set_slider_args(args, kwargs, value=30.0)
        return original_slider(label, *args, **kwargs)

    st.slider = patched_slider
    st._onestring_v13_slider_defaults_installed = True


_install_lift_point_highlight_patch()
_install_plotly_view_patch()
_install_v13_default_slider_patch()
runpy.run_path(str(_find_original_app()), run_name="__main__")
