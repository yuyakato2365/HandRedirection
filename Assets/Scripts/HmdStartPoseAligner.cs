using System.Collections;
using UnityEngine;

public class HmdStartPoseAligner : MonoBehaviour
{
    [Header("References")]
    public Transform hmdTransform;
    public Transform rigRoot;
    public Transform deskOrigin;

    [Header("Target HMD Pose")]
    public Vector3 worldOffsetFromDeskOrigin = new Vector3(0f, 1.2f, 0f);
    public bool alignX = true;
    public bool alignY = true;
    public bool alignZ = true;

    [Header("Startup Rig Offset")]
    public bool applyStartupRigOffsetOnStart = true;
    public Vector3 startupRigWorldOffset = new Vector3(0f, -6.5f, 0f);

    [Header("Timing")]
    public bool alignOnStart = true;
    public int waitFramesBeforeAlign = 8;
    public int maxAdditionalWaitFramesForTrackedPose = 120;
    public float minTrackedPoseMagnitude = 0.05f;
    public bool logAlignment = true;

    private void Awake()
    {
        AutoAssignReferences();
    }

    private void Start()
    {
        if (applyStartupRigOffsetOnStart)
            ApplyStartupRigOffset();

        if (alignOnStart)
            StartCoroutine(AlignAfterTrackingStarts());
    }

    [ContextMenu("HMD Start Pose/Apply Startup Rig Offset")]
    public void ApplyStartupRigOffset()
    {
        AutoAssignReferences();

        if (rigRoot == null)
            return;

        rigRoot.position += startupRigWorldOffset;

        if (logAlignment)
            Debug.Log($"[HmdStartPoseAligner] Applied startup rig offset {startupRigWorldOffset} to {rigRoot.name}.");
    }

    [ContextMenu("HMD Start Pose/Align Now")]
    public void AlignNow()
    {
        AutoAssignReferences();

        if (hmdTransform == null || rigRoot == null || deskOrigin == null)
            return;

        Vector3 desiredHmdPosition = deskOrigin.position + worldOffsetFromDeskOrigin;
        Vector3 delta = desiredHmdPosition - hmdTransform.position;
        if (!alignX)
            delta.x = 0f;
        if (!alignY)
            delta.y = 0f;
        if (!alignZ)
            delta.z = 0f;

        rigRoot.position += delta;
        if (logAlignment)
            Debug.Log($"[HmdStartPoseAligner] Moved {rigRoot.name} by {delta} so HMD starts near {desiredHmdPosition}.");
    }

    private IEnumerator AlignAfterTrackingStarts()
    {
        for (int i = 0; i < waitFramesBeforeAlign; i++)
            yield return null;

        int additionalWaitFrames = 0;
        while (hmdTransform != null &&
               hmdTransform.position.sqrMagnitude < minTrackedPoseMagnitude * minTrackedPoseMagnitude &&
               additionalWaitFrames < maxAdditionalWaitFramesForTrackedPose)
        {
            additionalWaitFrames++;
            yield return null;
        }

        AlignNow();
    }

    private void AutoAssignReferences()
    {
        if (deskOrigin == null)
        {
            GameObject deskObject = GameObject.Find("DeskOrigin");
            if (deskObject != null)
                deskOrigin = deskObject.transform;
        }

        if (hmdTransform == null)
        {
            GoGoInteractionController_NoY3 goGo = FindAnyObjectByType<GoGoInteractionController_NoY3>();
            if (goGo != null && goGo.cameraCenter != null)
                hmdTransform = goGo.cameraCenter;
        }

        if (hmdTransform == null && Camera.main != null)
            hmdTransform = Camera.main.transform;

        if (rigRoot == null && hmdTransform != null)
            rigRoot = FindAncestorNamed(hmdTransform, "OVRCameraRig");
    }

    private static Transform FindAncestorNamed(Transform start, string objectName)
    {
        Transform current = start;
        while (current != null)
        {
            if (current.name == objectName)
                return current;
            current = current.parent;
        }

        return null;
    }
}
