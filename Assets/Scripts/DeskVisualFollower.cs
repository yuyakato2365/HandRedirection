using UnityEngine;

[ExecuteAlways]
public class DeskVisualFollower : MonoBehaviour
{
    [Header("Reference")]
    [Tooltip("Tracker-driven desk origin. This object does not need to be a child of it.")]
    public Transform deskOrigin;

    [Header("Edit / Play Follow")]
    public bool followInEditMode = true;
    public bool followInPlayMode = true;

    [Header("Offset In Desk Space")]
    public Vector3 localPositionOffset = Vector3.zero;
    public Vector3 localEulerOffset = Vector3.zero;

    [Header("Scale")]
    public bool applyLocalScale = false;
    public Vector3 localScaleOverride = Vector3.one;

    private void Update()
    {
        if (deskOrigin == null)
            return;

        if (Application.isPlaying)
        {
            if (!followInPlayMode)
                return;
        }
        else
        {
            if (!followInEditMode)
                return;
        }

        ApplyOffset();
    }

    [ContextMenu("Desk Visual/Capture Current Transform As Offset")]
    public void CaptureCurrentTransformAsOffset()
    {
        if (deskOrigin == null)
            return;

        Quaternion invDeskRot = Quaternion.Inverse(deskOrigin.rotation);
        localPositionOffset = invDeskRot * (transform.position - deskOrigin.position);
        localEulerOffset = (invDeskRot * transform.rotation).eulerAngles;
    }

    [ContextMenu("Desk Visual/Apply Offset Now")]
    public void ApplyOffsetNow()
    {
        ApplyOffset();
    }

    [ContextMenu("Desk Visual/Reset Offset")]
    public void ResetOffset()
    {
        localPositionOffset = Vector3.zero;
        localEulerOffset = Vector3.zero;
        ApplyOffset();
    }

    private void ApplyOffset()
    {
        if (deskOrigin == null)
            return;

        Quaternion offsetRot = Quaternion.Euler(localEulerOffset);
        transform.position = deskOrigin.TransformPoint(localPositionOffset);
        transform.rotation = deskOrigin.rotation * offsetRot;

        if (applyLocalScale)
        {
            transform.localScale = localScaleOverride;
        }
    }
}
