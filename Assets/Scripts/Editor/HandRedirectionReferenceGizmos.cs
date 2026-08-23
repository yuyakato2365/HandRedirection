using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class HandRedirectionReferenceGizmos
{
    [DrawGizmo(GizmoType.Active | GizmoType.Selected | GizmoType.NonSelected)]
    private static void DrawReferenceAxes(SpatialAnchorToDeskOriginBinder binder, GizmoType gizmoType)
    {
        if (Application.isPlaying || binder == null || !binder.showReferenceAxesInEditMode)
            return;

        float length = Mathf.Max(0.02f, binder.editModeAxisLength);
        float width = Mathf.Max(1f, binder.editModeAxisLineWidth);

        if (binder.deskOrigin != null)
            DrawPoseAxes(binder.deskOrigin.position, binder.deskOrigin.rotation, length, width, "DeskOrigin", Color.white, 0.18f);

        if (binder.redirectionOrigin != null)
            DrawPoseAxes(binder.redirectionOrigin.position, binder.redirectionOrigin.rotation, length, width, "RedirectOrigin", new Color(0.15f, 1f, 1f), 0.36f);

        if (binder.showDerivedSpatialAnchorInEditMode && binder.deskOrigin != null)
        {
            Quaternion anchorRotation = binder.deskOrigin.rotation * Quaternion.Inverse(Quaternion.Euler(binder.localEulerOffset));
            Vector3 anchorPosition = binder.deskOrigin.position - anchorRotation * binder.localPositionOffset;
            DrawPoseAxes(anchorPosition, anchorRotation, length, width, "Spatial Anchor (Derived)", new Color(1f, 0.85f, 0.15f), -0.22f);
        }
    }

    private static void DrawPoseAxes(
        Vector3 position,
        Quaternion rotation,
        float length,
        float width,
        string label,
        Color labelColor,
        float labelHeightRatio)
    {
        CompareFunction previousZTest = Handles.zTest;
        Color previousColor = Handles.color;
        Handles.zTest = CompareFunction.LessEqual;

        DrawAxis(position, rotation * Vector3.right, length, width, new Color(1f, 0.12f, 0.12f, 1f));
        DrawAxis(position, rotation * Vector3.up, length, width, new Color(0.15f, 1f, 0.2f, 1f));
        DrawAxis(position, rotation * Vector3.forward, length, width, new Color(0.15f, 0.4f, 1f, 1f));

        Handles.color = labelColor;
        Handles.SphereHandleCap(0, position, Quaternion.identity, length * 0.075f, EventType.Repaint);

        GUIStyle labelStyle = new GUIStyle(EditorStyles.boldLabel);
        labelStyle.normal.textColor = labelColor;
        labelStyle.fontSize = 12;
        Handles.Label(position + Vector3.up * length * labelHeightRatio, label, labelStyle);

        Handles.color = previousColor;
        Handles.zTest = previousZTest;
    }

    private static void DrawAxis(Vector3 origin, Vector3 direction, float length, float width, Color color)
    {
        Vector3 normalizedDirection = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector3.forward;
        Vector3 endpoint = origin + normalizedDirection * length;

        Handles.color = color;
        Handles.DrawAAPolyLine(width, origin, endpoint);
        Handles.ConeHandleCap(
            0,
            endpoint,
            Quaternion.LookRotation(normalizedDirection),
            length * 0.12f,
            EventType.Repaint);
    }
}
