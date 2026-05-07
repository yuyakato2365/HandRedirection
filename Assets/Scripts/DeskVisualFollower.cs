using UnityEngine;

[ExecuteAlways]
public class DeskVisualFollower : MonoBehaviour
{
    [Header("Reference")]
    [Tooltip("Tracker-driven desk origin. This object does not need to be a child of it.")]
    public Transform deskOrigin;
    public ManualSpatialAnchorPlacer anchorPlacer;
    public SpatialAnchorToDeskOriginBinder deskBinder;

    [Header("Edit / Play Follow")]
    public bool followInEditMode = true;
    public bool followInPlayMode = true;

    [Header("Offset In Desk Space")]
    public Vector3 localPositionOffset = Vector3.zero;
    public Vector3 localEulerOffset = Vector3.zero;

    [Header("Scale")]
    public bool applyLocalScale = false;
    public Vector3 localScaleOverride = Vector3.one;

    [Header("Spatial Anchor Pose Memory")]
    [Tooltip("Capture this desk visual's pose relative to the confirmed Spatial Anchor, then restore it when the saved anchor is refreshed after HMD remount.")]
    public bool preservePoseRelativeToSpatialAnchor = true;
    public string savedAnchorPosePlayerPrefsKey = "HandRedirection.DeskVisualAnchorPose";

    private bool subscribed;

    private void OnEnable()
    {
        AutoAssignAnchorReferences();
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void Update()
    {
        if (Application.isPlaying && !subscribed)
            Subscribe();

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

    public void SetOffsetForWorldPose(Vector3 worldPosition, Quaternion worldRotation)
    {
        if (deskOrigin == null)
            return;

        Quaternion invDeskRot = Quaternion.Inverse(deskOrigin.rotation);
        localPositionOffset = invDeskRot * (worldPosition - deskOrigin.position);
        localEulerOffset = (invDeskRot * worldRotation).eulerAngles;
        ApplyOffset();
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

    private void AutoAssignAnchorReferences()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
    }

    private void Subscribe()
    {
        if (subscribed || !Application.isPlaying)
            return;

        AutoAssignAnchorReferences();
        if (anchorPlacer != null)
            anchorPlacer.SavedAnchorRefreshed += OnSavedAnchorRefreshed;
        if (deskBinder != null)
            deskBinder.AlignmentConfirmed += CaptureCurrentPoseRelativeToSpatialAnchor;

        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        if (anchorPlacer != null)
            anchorPlacer.SavedAnchorRefreshed -= OnSavedAnchorRefreshed;
        if (deskBinder != null)
            deskBinder.AlignmentConfirmed -= CaptureCurrentPoseRelativeToSpatialAnchor;

        subscribed = false;
    }

    [ContextMenu("Desk Visual/Capture Current Pose Relative To Spatial Anchor")]
    public void CaptureCurrentPoseRelativeToSpatialAnchor()
    {
        if (!preservePoseRelativeToSpatialAnchor)
            return;

        AutoAssignAnchorReferences();
        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        if (anchor == null)
            return;

        ApplyOffset();

        SavedAnchorPose saved = new SavedAnchorPose
        {
            localPosition = Quaternion.Inverse(anchor.rotation) * (transform.position - anchor.position),
            localEuler = (Quaternion.Inverse(anchor.rotation) * transform.rotation).eulerAngles
        };

        PlayerPrefs.SetString(GetSavedAnchorPoseKey(), JsonUtility.ToJson(saved));
        PlayerPrefs.Save();
    }

    private void OnSavedAnchorRefreshed(Transform anchor)
    {
        if (!preservePoseRelativeToSpatialAnchor || anchor == null)
            return;

        string json = PlayerPrefs.GetString(GetSavedAnchorPoseKey(), "");
        if (string.IsNullOrWhiteSpace(json))
            return;

        SavedAnchorPose saved = JsonUtility.FromJson<SavedAnchorPose>(json);
        Vector3 worldPosition = anchor.TransformPoint(saved.localPosition);
        Quaternion worldRotation = anchor.rotation * Quaternion.Euler(saved.localEuler);
        SetOffsetForWorldPose(worldPosition, worldRotation);
    }

    private string GetSavedAnchorPoseKey()
    {
        if (string.IsNullOrEmpty(savedAnchorPosePlayerPrefsKey))
            return gameObject.name;

        return savedAnchorPosePlayerPrefsKey + "." + gameObject.name;
    }

    [System.Serializable]
    private struct SavedAnchorPose
    {
        public Vector3 localPosition;
        public Vector3 localEuler;
    }
}
