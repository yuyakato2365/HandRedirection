using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class DeskVisualFollower : MonoBehaviour
{
    [Header("Reference")]
    [Tooltip("Tracker-driven desk origin. This object does not need to be a child of it.")]
    public Transform deskOrigin;

    [Header("Edit / Play Follow")]
    public bool followInEditMode = true;
    public bool followInPlayMode = true;
    [Tooltip("While editing, treat this 3DGS/desk visual as authoritative. Moving or rotating it applies the same rigid transform to DeskOrigin, preserving the tracker position inside the captured room.")]
    public bool moveDeskOriginWithVisualInEditMode = true;
    [Tooltip("When Play starts, use the current scene transform as the offset before following DeskOrigin. This preserves manual scene placement while still following runtime desk alignment.")]
    public bool recaptureOffsetOnPlayStart = true;

    [Header("Offset In Desk Space")]
    public Vector3 localPositionOffset = Vector3.zero;
    public Vector3 localEulerOffset = Vector3.zero;

    [Header("Scale")]
    public bool applyLocalScale = false;
    public Vector3 localScaleOverride = Vector3.one;

    private bool recapturedOffsetForThisPlay;
    private bool editPoseInitialized;
    private Vector3 lastEditVisualPosition;
    private Quaternion lastEditVisualRotation;
    private Vector3 lastEditDeskPosition;
    private Quaternion lastEditDeskRotation;

    private void OnEnable()
    {
        recapturedOffsetForThisPlay = false;
        editPoseInitialized = false;
    }

    private void Update()
    {
        if (deskOrigin == null)
            return;

        if (Application.isPlaying)
        {
            if (!followInPlayMode)
                return;

            if (recaptureOffsetOnPlayStart && !recapturedOffsetForThisPlay)
            {
                CaptureCurrentTransformAsOffset();
                recapturedOffsetForThisPlay = true;
            }
        }
        else
        {
            if (moveDeskOriginWithVisualInEditMode)
            {
                UpdateDeskOriginFromVisualEdit();
                return;
            }

            if (!followInEditMode)
                return;
        }

        ApplyOffset();
    }

    private void UpdateDeskOriginFromVisualEdit()
    {
        if (deskOrigin == null)
            return;

        if (!editPoseInitialized)
        {
            CaptureEditPoseState();
            return;
        }

        bool visualMoved = !Approximately(transform.position, lastEditVisualPosition)
            || Quaternion.Angle(transform.rotation, lastEditVisualRotation) > 0.001f;
        bool deskMoved = !Approximately(deskOrigin.position, lastEditDeskPosition)
            || Quaternion.Angle(deskOrigin.rotation, lastEditDeskRotation) > 0.001f;

        if (visualMoved)
        {
            Vector3 deskPositionInPreviousVisual =
                Quaternion.Inverse(lastEditVisualRotation) * (lastEditDeskPosition - lastEditVisualPosition);
            Quaternion deskRotationInPreviousVisual =
                Quaternion.Inverse(lastEditVisualRotation) * lastEditDeskRotation;

            Vector3 expectedDeskPosition = transform.position + transform.rotation * deskPositionInPreviousVisual;
            Quaternion expectedDeskRotation = transform.rotation * deskRotationInPreviousVisual;

            bool alreadyMovedWithVisual = deskMoved
                && Approximately(deskOrigin.position, expectedDeskPosition)
                && Quaternion.Angle(deskOrigin.rotation, expectedDeskRotation) <= 0.001f;

            if (!alreadyMovedWithVisual)
            {
#if UNITY_EDITOR
                Undo.RecordObject(deskOrigin, "Move DeskOrigin With 3DGS");
#endif
                deskOrigin.SetPositionAndRotation(expectedDeskPosition, expectedDeskRotation);
#if UNITY_EDITOR
                EditorUtility.SetDirty(deskOrigin);
#endif
            }
        }

        CaptureEditPoseState();
    }

    private void CaptureEditPoseState()
    {
        lastEditVisualPosition = transform.position;
        lastEditVisualRotation = transform.rotation;
        lastEditDeskPosition = deskOrigin.position;
        lastEditDeskRotation = deskOrigin.rotation;
        editPoseInitialized = true;
    }

    private static bool Approximately(Vector3 a, Vector3 b)
    {
        return (a - b).sqrMagnitude <= 0.0000000001f;
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
