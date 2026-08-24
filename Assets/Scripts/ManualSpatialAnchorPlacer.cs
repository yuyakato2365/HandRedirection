using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ManualSpatialAnchorPlacer : MonoBehaviour
{
    public enum PlacementSourceMode
    {
        PointerTransform,
        OvrHandJoint,
        RaycastFromPointer,
        CameraForwardFallback
    }

    public enum OvrHandPlacementJoint
    {
        PointerPose,
        HandRoot,
        Wrist,
        IndexTip,
        PinchPoint
    }

    [Header("AR Anchor")]
    public ARAnchorManager anchorManager;
    [Tooltip("Use Meta OVRSpatialAnchor for persistent anchors on Quest. Falls back to AR/session anchors if unavailable.")]
    public bool useOvrSpatialAnchorPersistence = true;
    public bool loadSavedAnchorOnStart = false;
    public string savedAnchorPlayerPrefsKey = "HandRedirection.DeskSpatialAnchorUuid";
    public double savedAnchorLocalizationTimeoutSec = 10.0;
    public float savedAnchorStabilizeSeconds = 0.75f;
    public float savedAnchorMaxStabilizeWaitSeconds = 3.0f;
    public float savedAnchorStablePositionThresholdMeters = 0.003f;
    public float savedAnchorStableRotationThresholdDegrees = 0.25f;
    [Tooltip("Reload the saved persistent anchor after the headset is worn again.")]
    public bool reloadSavedAnchorOnHmdMounted = true;
    [Tooltip("If HMD remount reload is disabled or busy, ask desk binding to reapply the current anchor pose as a fallback.")]
    public bool reapplyCurrentAnchorOnHmdMounted = false;
    [Tooltip("After the HMD-mounted anchor reload finishes, notify desk binding once so DeskOrigin snaps back to the fixed anchor.")]
    public bool reapplyDeskOriginAfterHmdMountedAnchorReload = false;
    public float hmdMountedAnchorReloadDelaySec = 1.0f;
    [Tooltip("Ignore repeated HMD-mounted anchor reload requests inside this interval. The current anchor pose is still reapplied as a lightweight fallback.")]
    public float hmdMountedAnchorReloadCooldownSec = 0f;
    [Tooltip("Before the heavier saved-anchor reload, immediately reapply the current runtime anchor pose when available.")]
    public bool reapplyCurrentAnchorImmediatelyOnHmdMounted = false;
    public int hmdMountedAnchorLoadWarmupFrames = 0;
    [Tooltip("In Unity Editor / PCVR Play mode, skip OVRSpatialAnchor reloads on HMD remount and only reapply the current runtime anchor pose.")]
    public bool skipSavedAnchorReloadOnHmdMountedInEditor = false;
    [Tooltip("Use a Unity-world session anchor when Meta/AR anchors are unavailable, e.g. Quest Link / PCVR.")]
    public bool allowPcvrSessionAnchorFallback = true;
    public float anchorCreateTimeoutSec = 4f;

    [Header("Placement Source")]
    public PlacementSourceMode sourceMode = PlacementSourceMode.RaycastFromPointer;
    public Transform placementPointer;
    public Camera fallbackCamera;
    public LayerMask raycastMask = ~0;
    public float raycastMaxDistance = 5f;
    public float fallbackDistance = 1.0f;
    public bool keepRotationLevel = true;

    [Header("Visuals")]
    public GameObject previewObject;
    public GameObject anchorMarkerPrefab;
    public bool hidePreviewWhenIdle = true;
    public bool createDefaultVisuals = true;
    [Tooltip("Length in metres of each automatically generated XYZ axis.")]
    public float defaultAxisLength = 0.12f;
    [Tooltip("Line width in metres of automatically generated XYZ axes.")]
    public float defaultAxisLineWidth = 0.006f;
    public bool defaultAxisShowLabels = true;
    [Tooltip("Show the Spatial Anchor preview and confirmed-anchor XYZ axes at runtime.")]
    public bool showAnchorCoordinateAxes = true;

    [Header("VR Status")]
    public TextMesh statusText;
    public bool createDefaultStatusText = true;
    public Vector3 statusTextCameraOffset = new Vector3(0f, -0.25f, 1.2f);

    [Header("Quest Controller Input")]
    public bool enableOvrControllerInput = true;
    public OVRInput.RawButton confirmButton = OVRInput.RawButton.RIndexTrigger;
    public OVRInput.RawButton cancelButton = OVRInput.RawButton.B;

    [Header("Quest Hand Input")]
    public bool enableOvrHandPinchInput = true;
    public OVRHand confirmHand;
    public OVRHand.HandFinger confirmFinger = OVRHand.HandFinger.Index;
    [Range(0f, 1f)] public float pinchConfirmThreshold = 0.7f;
    [Range(0f, 1f)] public float pinchReleaseThreshold = 0.35f;
    public bool autoFindConfirmHand = true;
    public bool showHandDebugInStatus = true;
    public bool preferLiveHandPoseForPlacement = true;
    public OvrHandPlacementJoint placementHandJoint = OvrHandPlacementJoint.PointerPose;
    public Vector3 handPlacementLocalOffset = Vector3.zero;
    [Tooltip("Place the anchor directly below the tracked right-hand root, ignoring PointerPose and the legacy placement offsets.")]
    public bool placeDirectlyBelowHandRoot = true;
    [Min(0f)]
    [Tooltip("World-space distance below the tracked right-hand root used by direct hand placement.")]
    public float directHandVerticalOffsetMeters = 0.05f;
    [Tooltip("Keep the candidate and DeskOrigin following the right hand even after the first pinch locks the pose. Placement still confirms with the configured confirmation action.")]
    public bool keepFollowingRightHandUntilConfirmed = true;
    [Tooltip("World-space offset applied to the final anchor placement pose. Use negative Y to place the anchor below the hand marker.")]
    public Vector3 anchorPlacementWorldOffset = new Vector3(0f, -0.08f, 0f);

    [Header("Hand Placement Confirmation")]
    [Tooltip("Right-hand pinch once locks the candidate pose, hold while locked fine-adjusts it, and two quick pinches confirm placement.")]
    public bool useTwoPinchPlacementConfirmation = true;
    public float doublePinchConfirmMaxGapSec = 0.45f;
    public float tapPinchMaxDurationSec = 0.25f;
    public float holdToFineAdjustDelaySec = 0.35f;

    [Header("Editor Debug Input")]
    public bool enableKeyboardInput = true;
    public KeyCode beginKey = KeyCode.B;
    public KeyCode confirmKey = KeyCode.C;
    public KeyCode cancelKey = KeyCode.Escape;

    public ARAnchor CurrentAnchor { get; private set; }
    public Transform CurrentAnchorTransform => CurrentAnchor != null
        ? CurrentAnchor.transform
        : (currentOvrSpatialAnchor != null ? currentOvrSpatialAnchor.transform : sessionAnchorTransform);
    public Pose CandidatePose => candidatePose;
    public bool IsPlacementMode { get; private set; }
    public bool IsCreatingAnchor => isCreatingAnchor;
    public bool IsCandidatePoseLocked => candidatePoseLocked;
    public bool IsCandidatePoseBeingAdjusted => candidatePoseLocked && adjustingLockedPoseWithHold;
    public bool HasAnchor => CurrentAnchor != null || currentOvrSpatialAnchor != null || sessionAnchorTransform != null;
    public bool HasSavedAnchor => Guid.TryParse(PlayerPrefs.GetString(savedAnchorPlayerPrefsKey, ""), out Guid uuid) && uuid != Guid.Empty;
    public bool LastAnchorWasLoadedSavedAnchor { get; private set; }

    public event Action PlacementStarted;
    public event Action PlacementCanceled;
    public event Action<Pose> CandidatePoseUpdated;
    public event Action<bool, bool> CandidatePoseLockStateChanged;
    public event Action CandidatePoseConfirmRequested;
    public event Action<ARAnchor> AnchorCreated;
    public event Action<Transform> AnchorTransformCreated;
    public event Action<Transform> SavedAnchorRefreshed;
    public event Action AnchorCleared;
    public event Action<string> AnchorCreateFailed;

    private Pose candidatePose;
    private GameObject anchorMarkerInstance;
    private OVRSpatialAnchor currentOvrSpatialAnchor;
    private Transform sessionAnchorTransform;
    private bool wasPinching;
    private bool candidatePoseLocked;
    private bool adjustingLockedPoseWithHold;
    private Coroutine clearStatusMessageCoroutine;
    private float pinchStartTime;
    private float lastTapPinchReleaseTime = -999f;
    private bool hasRelativeAdjustStartPose;
    private Pose lockedCandidatePoseAtAdjustStart;
    private Pose handCandidatePoseAtAdjustStart;
    private string placementStatusHint = "";
    private bool isCreatingAnchor;
    private bool isCreatingPersistentOvrAnchor;
    private bool isLoadingSavedAnchor;
    private int statusMessageVersion;
    private float anchorCreateStartTime;
    private bool pendingHmdMountedAnchorReload;
    private float hmdMountedAnchorReloadTime;
    private float lastHmdMountedAnchorReloadStartTime = -999f;

    private void Awake()
    {
        if (fallbackCamera == null)
            fallbackCamera = Camera.main;

        EnsureDefaultVisuals();
        EnsureDefaultStatusText();
        TryAutoAssignHand();
        SetPreviewActive(false);
        SetStatusMessage("Anchor placer ready");

        if (loadSavedAnchorOnStart)
            LoadSavedAnchor();
    }

    private void OnEnable()
    {
        OVRManager.HMDMounted += OnHmdMounted;
    }

    private void OnDisable()
    {
        OVRManager.HMDMounted -= OnHmdMounted;
    }

    private void Update()
    {
        if (pendingHmdMountedAnchorReload && Time.realtimeSinceStartup >= hmdMountedAnchorReloadTime)
        {
            pendingHmdMountedAnchorReload = false;
            if (!IsPlacementMode && !isCreatingAnchor && !isLoadingSavedAnchor && HasSavedAnchor)
            {
                if (ShouldSkipSavedAnchorReloadOnHmdMounted())
                {
                    ReapplyCurrentAnchorAfterHmdMounted();
                }
                else if (reloadSavedAnchorOnHmdMounted)
                {
                    if (Time.realtimeSinceStartup - lastHmdMountedAnchorReloadStartTime >= Mathf.Max(0f, hmdMountedAnchorReloadCooldownSec))
                    {
                        lastHmdMountedAnchorReloadStartTime = Time.realtimeSinceStartup;
                        if (reapplyDeskOriginAfterHmdMountedAnchorReload)
                            ReloadSavedAnchorAndReapplyDeskOrigin();
                        else
                            ReloadSavedAnchorOnly();
                    }
                    else
                    {
                        ReapplyCurrentAnchorAfterHmdMounted();
                    }
                }
                else if (reapplyCurrentAnchorOnHmdMounted && CurrentAnchorTransform != null)
                {
                    ReapplyCurrentAnchorAfterHmdMounted();
                }
            }
            else if (reapplyCurrentAnchorOnHmdMounted && !isLoadingSavedAnchor && CurrentAnchorTransform != null)
            {
                ReapplyCurrentAnchorAfterHmdMounted();
            }
        }

        if (isCreatingAnchor)
        {
            float elapsed = Time.realtimeSinceStartup - anchorCreateStartTime;
            SetStatusMessage(isCreatingPersistentOvrAnchor
                ? $"Saving persistent Spatial Anchor...\nWaiting {elapsed:0.0}s"
                : $"Creating Spatial Anchor...\nWaiting {elapsed:0.0}s");

            if (!isCreatingPersistentOvrAnchor && elapsed >= anchorCreateTimeoutSec)
            {
                isCreatingAnchor = false;
                if (allowPcvrSessionAnchorFallback)
                    CreatePcvrSessionAnchor("Anchor creation timeout");
                else
                    SetStatusMessage("Anchor creation timeout");
            }
            return;
        }

        if (enableKeyboardInput && Input.GetKeyDown(beginKey))
            BeginPlacement();

        if (!IsPlacementMode)
            return;

        TryAutoAssignHand();
        if (ShouldUpdateCandidatePoseFromSource())
            UpdateCandidatePose();

        if (enableKeyboardInput && Input.GetKeyDown(confirmKey))
            ConfirmPlacement();

        if (enableKeyboardInput && Input.GetKeyDown(cancelKey))
            CancelPlacement();

        if (enableOvrControllerInput)
        {
            if (OVRInput.GetDown(confirmButton))
                ConfirmPlacement();

            if (OVRInput.GetDown(cancelButton))
                CancelPlacement();
        }

        if (enableOvrHandPinchInput && confirmHand != null)
        {
            float pinchStrength = confirmHand.GetFingerPinchStrength(confirmFinger);
            bool pinching = confirmHand.IsTracked &&
                            (confirmHand.GetFingerIsPinching(confirmFinger) || pinchStrength >= pinchConfirmThreshold);
            if (useTwoPinchPlacementConfirmation)
            {
                UpdateTwoPinchPlacementInput(pinching, pinchStrength);
            }
            else
            {
                if (pinching && !wasPinching)
                    ConfirmPlacement();
                if (!pinching && pinchStrength <= pinchReleaseThreshold)
                    wasPinching = false;
                else if (pinching)
                    wasPinching = true;
            }
        }

        UpdatePlacementStatusHint();
    }

    private void LateUpdate()
    {
        UpdateStatusTextPose();
    }

    public void BeginPlacement()
    {
        TryAutoAssignHand();
        RefreshFallbackCamera();
        IsPlacementMode = true;
        wasPinching = false;
        SetCandidatePoseLockState(false, false);
        pinchStartTime = 0f;
        lastTapPinchReleaseTime = -999f;
        UpdateCandidatePose();
        SetPreviewActive(true);
        placementStatusHint = "Anchor placement started";
        UpdatePlacementStatusHint();
        PlacementStarted?.Invoke();
    }

    public void CancelPlacement()
    {
        if (!IsPlacementMode)
            return;

        IsPlacementMode = false;
        SetCandidatePoseLockState(false, false);
        SetPreviewActive(false);
        SetStatusMessage("Anchor placement canceled");
        PlacementCanceled?.Invoke();
    }

    public async void ConfirmPlacement()
    {
        if (!IsPlacementMode)
        {
            SetStatusMessage("Confirm ignored\nAnchor placement is not active");
            return;
        }

        IsPlacementMode = false;
        SetCandidatePoseLockState(false, false);
        SetPreviewActive(false);
        SetStatusMessage("Creating Spatial Anchor...");
        isCreatingAnchor = true;
        anchorCreateStartTime = Time.realtimeSinceStartup;

        if (useOvrSpatialAnchorPersistence)
        {
            await CreateAndSaveOvrSpatialAnchorAsync();
            return;
        }

        if (anchorManager == null)
        {
            isCreatingAnchor = false;
            FailAnchorCreation("ARAnchorManager is not assigned.");
            return;
        }

        if (anchorManager.subsystem == null || !anchorManager.subsystem.running)
        {
            isCreatingAnchor = false;
            string reason = "ARAnchorManager subsystem is not running.";
            if (allowPcvrSessionAnchorFallback)
                CreatePcvrSessionAnchor(reason);
            else
                FailAnchorCreation(reason);
            return;
        }

        if (CurrentAnchor != null)
        {
            anchorManager.TryRemoveAnchor(CurrentAnchor);
            CurrentAnchor = null;
        }

        ClearSessionAnchor();

        var result = await anchorManager.TryAddAnchorAsync(candidatePose);
        if (!isCreatingAnchor)
            return;

        isCreatingAnchor = false;
        if (!result.status.IsSuccess())
        {
            FailAnchorCreation(result.status.ToString());
            return;
        }

        CurrentAnchor = result.value;
        LastAnchorWasLoadedSavedAnchor = false;
        AttachOrCreateMarker();
        SetStatusMessage("Spatial Anchor created");
        AnchorCreated?.Invoke(CurrentAnchor);
        AnchorTransformCreated?.Invoke(CurrentAnchor.transform);
    }

    public async void LoadSavedAnchor()
    {
        await LoadSavedAnchorAsync(true, true);
    }

    public async void ReloadSavedAnchorOnly()
    {
        await LoadSavedAnchorAsync(false, false);
    }

    public async void ReloadSavedAnchorAndReapplyDeskOrigin()
    {
        await LoadSavedAnchorAsync(true, false);
    }

    private async System.Threading.Tasks.Task LoadSavedAnchorAsync(bool notifyDeskOrigin, bool visibleStatus)
    {
        if (isLoadingSavedAnchor)
        {
            ReportSavedAnchorLoadStatus("Saved Spatial Anchor load already in progress", visibleStatus);
            return;
        }

        isLoadingSavedAnchor = true;
        try
        {
            int warmupFrames = Mathf.Max(0, hmdMountedAnchorLoadWarmupFrames);
            for (int i = 0; i < warmupFrames; i++)
                await System.Threading.Tasks.Task.Yield();

            if (!useOvrSpatialAnchorPersistence)
            {
                ReportSavedAnchorLoadStatus("OVR Spatial Anchor persistence is disabled", visibleStatus);
                return;
            }

            string savedUuid = PlayerPrefs.GetString(savedAnchorPlayerPrefsKey, "");
            if (!Guid.TryParse(savedUuid, out Guid uuid) || uuid == Guid.Empty)
            {
                ReportSavedAnchorLoadStatus("No saved Spatial Anchor UUID", visibleStatus);
                return;
            }

            ReportSavedAnchorLoadStatus("Loading saved Spatial Anchor...", visibleStatus);
            List<OVRSpatialAnchor.UnboundAnchor> unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            var loadResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unboundAnchors);
            if (!loadResult.Success || unboundAnchors.Count == 0)
            {
                ReportSavedAnchorLoadStatus($"Saved Spatial Anchor load failed\n{loadResult.Status}", visibleStatus, true);
                AnchorCreateFailed?.Invoke(loadResult.Status.ToString());
                return;
            }

            OVRSpatialAnchor.UnboundAnchor unboundAnchor = unboundAnchors[0];
            bool localized = await unboundAnchor.LocalizeAsync(savedAnchorLocalizationTimeoutSec);
            if (!localized)
            {
                ReportSavedAnchorLoadStatus("Saved Spatial Anchor localization failed", visibleStatus, true);
                AnchorCreateFailed?.Invoke("Saved Spatial Anchor localization failed");
                return;
            }

            ClearRuntimeAnchors();

            GameObject anchorObject = new GameObject("LoadedPersistentDeskAnchor");
            if (unboundAnchor.TryGetPose(out Pose anchorPose))
                anchorObject.transform.SetPositionAndRotation(anchorPose.position, anchorPose.rotation);
            OVRSpatialAnchor ovrAnchor = anchorObject.AddComponent<OVRSpatialAnchor>();
            unboundAnchor.BindTo(ovrAnchor);
            currentOvrSpatialAnchor = ovrAnchor;
            LastAnchorWasLoadedSavedAnchor = true;

            CreateMarkerUnder(currentOvrSpatialAnchor.transform, "LoadedPersistentDeskAnchorMarker");
            ReportSavedAnchorLoadStatus("Saved Spatial Anchor localized\nWaiting for stable pose...", visibleStatus);
            await WaitForLoadedAnchorPoseStableAsync(currentOvrSpatialAnchor.transform);
            ReportSavedAnchorLoadStatus(notifyDeskOrigin ? "Saved Spatial Anchor loaded" : "Saved Spatial Anchor refreshed", visibleStatus);
            if (notifyDeskOrigin)
            {
                AnchorTransformCreated?.Invoke(currentOvrSpatialAnchor.transform);
            }
            else
            {
                SavedAnchorRefreshed?.Invoke(currentOvrSpatialAnchor.transform);
            }
        }
        finally
        {
            isLoadingSavedAnchor = false;
        }
    }

    private void ReapplyCurrentAnchorAfterHmdMounted()
    {
        if (!reapplyCurrentAnchorOnHmdMounted || CurrentAnchorTransform == null)
            return;

        SavedAnchorRefreshed?.Invoke(CurrentAnchorTransform);
    }

    private bool ShouldSkipSavedAnchorReloadOnHmdMounted()
    {
        if (!skipSavedAnchorReloadOnHmdMountedInEditor)
            return false;

#if UNITY_EDITOR
        return true;
#else
        return false;
#endif
    }

    private void ReportSavedAnchorLoadStatus(string message, bool visibleStatus, bool warning = false)
    {
        if (visibleStatus)
        {
            SetStatusMessage(message);
            return;
        }

        if (warning)
            Debug.LogWarning($"[ManualSpatialAnchorPlacer] {message}");
        else
            Debug.Log($"[ManualSpatialAnchorPlacer] {message}");
    }

    public async void ClearSavedAnchor()
    {
        string savedUuid = PlayerPrefs.GetString(savedAnchorPlayerPrefsKey, "");
        PlayerPrefs.DeleteKey(savedAnchorPlayerPrefsKey);
        PlayerPrefs.Save();

        if (currentOvrSpatialAnchor != null && currentOvrSpatialAnchor.Created)
        {
            var eraseResult = await currentOvrSpatialAnchor.EraseAnchorAsync();
            if (!eraseResult.Success)
                Debug.LogWarning($"[ManualSpatialAnchorPlacer] Failed to erase current OVR Spatial Anchor: {eraseResult.Status}");
        }
        else if (Guid.TryParse(savedUuid, out Guid uuid) && uuid != Guid.Empty)
        {
            var eraseResult = await OVRSpatialAnchor.EraseAnchorsAsync((IEnumerable<OVRSpatialAnchor>)null, new[] { uuid });
            if (!eraseResult.Success)
                Debug.LogWarning($"[ManualSpatialAnchorPlacer] Failed to erase saved OVR Spatial Anchor {uuid}: {eraseResult.Status}");
        }

        ClearRuntimeAnchors();
        SetStatusMessage("Saved Spatial Anchor cleared");
        AnchorCleared?.Invoke();
    }

    public void ClearAnchor()
    {
        if (anchorMarkerInstance != null)
        {
            Destroy(anchorMarkerInstance);
            anchorMarkerInstance = null;
        }

        ClearSessionAnchor();
        ClearOvrSpatialAnchor();

        if (CurrentAnchor != null && anchorManager != null)
        {
            anchorManager.TryRemoveAnchor(CurrentAnchor);
            CurrentAnchor = null;
        }

        SetStatusMessage("Anchor cleared");
        AnchorCleared?.Invoke();
    }

    public void SetStatusMessage(string message)
    {
        statusMessageVersion++;
        if (clearStatusMessageCoroutine != null)
        {
            StopCoroutine(clearStatusMessageCoroutine);
            clearStatusMessageCoroutine = null;
        }

        placementStatusHint = message;
        if (statusText != null)
        {
            statusText.gameObject.SetActive(!string.IsNullOrEmpty(message));
            statusText.text = message;
        }
    }

    public void SetStatusMessage(string message, float clearAfterSeconds)
    {
        SetStatusMessage(message);
        if (clearAfterSeconds > 0f && !string.IsNullOrEmpty(message))
            clearStatusMessageCoroutine = StartCoroutine(ClearStatusMessageAfterDelay(statusMessageVersion, clearAfterSeconds));
    }

    public void ClearStatusMessage()
    {
        statusMessageVersion++;
        if (clearStatusMessageCoroutine != null)
        {
            StopCoroutine(clearStatusMessageCoroutine);
            clearStatusMessageCoroutine = null;
        }

        placementStatusHint = "";
        if (statusText == null)
            return;

        statusText.text = "";
        statusText.gameObject.SetActive(false);
    }

    private IEnumerator ClearStatusMessageAfterDelay(int expectedVersion, float delaySeconds)
    {
        yield return new WaitForSeconds(delaySeconds);
        clearStatusMessageCoroutine = null;
        if (statusMessageVersion == expectedVersion)
            ClearStatusMessage();
    }

    private void UpdatePlacementStatusHint()
    {
        if (!IsPlacementMode || statusText == null)
            return;

        string handLine = "";
        if (showHandDebugInStatus)
        {
            if (!enableOvrHandPinchInput)
            {
                handLine = "Hand pinch input: disabled";
            }
            else if (confirmHand == null)
            {
                handLine = "confirmHand is not assigned";
            }
            else
            {
                float strength = confirmHand.GetFingerPinchStrength(confirmFinger);
                handLine = $"Hand: {confirmHand.name}\nTracked: {confirmHand.IsTracked}  pinch: {strength:0.00}";
            }
        }

        string instructionLine = useTwoPinchPlacementConfirmation
            ? (candidatePoseLocked
                ? "Pose locked\nHold right pinch to fine-adjust\nDouble pinch to confirm"
                : "Move the hand marker\nRight pinch once to lock")
            : $"Pinch to confirm, threshold {pinchConfirmThreshold:0.00}";

        statusText.text =
            $"{placementStatusHint}\n" +
            instructionLine + "\n" +
            handLine;
    }

    private bool ShouldUpdateCandidatePoseFromSource()
    {
        if (placeDirectlyBelowHandRoot && keepFollowingRightHandUntilConfirmed)
            return true;

        return !candidatePoseLocked || adjustingLockedPoseWithHold;
    }

    private void UpdateTwoPinchPlacementInput(bool pinching, float pinchStrength)
    {
        float now = Time.realtimeSinceStartup;

        if (pinching && !wasPinching)
        {
            pinchStartTime = now;
            SetCandidatePoseLockState(candidatePoseLocked, false);
        }

        if (pinching && candidatePoseLocked && !adjustingLockedPoseWithHold &&
            now - pinchStartTime >= Mathf.Max(0.01f, holdToFineAdjustDelaySec))
        {
            BeginRelativePoseAdjustment();
            SetCandidatePoseLockState(true, true);
            lastTapPinchReleaseTime = -999f;
            SetStatusMessage("Fine-adjusting locked anchor pose");
        }

        if (!pinching && wasPinching && pinchStrength <= pinchReleaseThreshold)
        {
            float pinchDuration = now - pinchStartTime;
            bool wasFineAdjusting = adjustingLockedPoseWithHold;

            if (wasFineAdjusting)
            {
                hasRelativeAdjustStartPose = false;
                SetCandidatePoseLockState(true, false);
                lastTapPinchReleaseTime = -999f;
                SetStatusMessage("Anchor pose locked");
            }
            else if (!candidatePoseLocked)
            {
                SetCandidatePoseLockState(true, false);
                lastTapPinchReleaseTime = now;
                SetStatusMessage("Anchor pose locked\nDouble pinch to confirm");
            }
            else if (pinchDuration <= Mathf.Max(0.01f, tapPinchMaxDurationSec) &&
                     now - lastTapPinchReleaseTime <= Mathf.Max(0.05f, doublePinchConfirmMaxGapSec))
            {
                RequestCandidatePoseConfirmation();
            }
            else
            {
                lastTapPinchReleaseTime = now;
                SetStatusMessage("Anchor pose locked\nDouble pinch to confirm");
            }
        }

        if (!pinching && pinchStrength <= pinchReleaseThreshold)
            wasPinching = false;
        else if (pinching)
            wasPinching = true;
    }

    private void RequestCandidatePoseConfirmation()
    {
        if (CandidatePoseConfirmRequested != null)
            CandidatePoseConfirmRequested.Invoke();
        else
            ConfirmPlacement();
    }

    private void BeginRelativePoseAdjustment()
    {
        lockedCandidatePoseAtAdjustStart = candidatePose;
        hasRelativeAdjustStartPose = TryComputeCandidatePoseFromSource(out handCandidatePoseAtAdjustStart);
    }

    private void SetCandidatePoseLockState(bool locked, bool adjusting)
    {
        adjusting = locked && adjusting;
        if (candidatePoseLocked == locked && adjustingLockedPoseWithHold == adjusting)
            return;

        candidatePoseLocked = locked;
        adjustingLockedPoseWithHold = adjusting;
        if (!adjustingLockedPoseWithHold)
            hasRelativeAdjustStartPose = false;
        CandidatePoseLockStateChanged?.Invoke(candidatePoseLocked, adjustingLockedPoseWithHold);
    }

    private void TryAutoAssignHand()
    {
        if (!autoFindConfirmHand || confirmHand != null)
            return;

        OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        if (hands == null || hands.Length == 0)
            return;

        for (int i = 0; i < hands.Length; i++)
        {
            if (hands[i] != null && hands[i].name.ToLowerInvariant().Contains("right"))
            {
                confirmHand = hands[i];
                return;
            }
        }

        confirmHand = hands[0];
    }

    private void UpdateCandidatePose()
    {
        if (placeDirectlyBelowHandRoot && keepFollowingRightHandUntilConfirmed)
        {
            if (TryComputeCandidatePoseFromSource(out Pose directHandPose))
            {
                candidatePose = directHandPose;
                PublishCandidatePose();
            }
            return;
        }

        if (candidatePoseLocked && adjustingLockedPoseWithHold)
        {
            if (hasRelativeAdjustStartPose && TryComputeCandidatePoseFromSource(out Pose currentHandCandidatePose))
            {
                Vector3 relativeMove = currentHandCandidatePose.position - handCandidatePoseAtAdjustStart.position;
                candidatePose = new Pose(
                    lockedCandidatePoseAtAdjustStart.position + relativeMove,
                    lockedCandidatePoseAtAdjustStart.rotation);
                PublishCandidatePose();
            }
            return;
        }

        if (!TryComputeCandidatePoseFromSource(out Pose resolvedCandidatePose))
            return;

        candidatePose = resolvedCandidatePose;
        PublishCandidatePose();
    }

    private bool TryComputeCandidatePoseFromSource(out Pose pose)
    {
        pose = default;
        RefreshFallbackCamera();

        if (!preferLiveHandPoseForPlacement && placementPointer == null && confirmHand != null)
            placementPointer = confirmHand.transform;

        if (TryGetLiveHandPlacementPose(out Pose handPose))
        {
            pose = handPose;
            if (!placeDirectlyBelowHandRoot)
                ApplyCandidateWorldOffset(ref pose);
            return true;
        }

        Transform pointer = placementPointer != null ? placementPointer : (fallbackCamera != null ? fallbackCamera.transform : null);
        if (pointer == null)
            return false;

        Vector3 position;
        Vector3 forward;

        if (sourceMode == PlacementSourceMode.OvrHandJoint)
        {
            position = pointer.position + pointer.forward * fallbackDistance;
            forward = pointer.forward;
            pose = new Pose(position, MakeRotation(forward, Vector3.up));
        }
        else if (sourceMode == PlacementSourceMode.RaycastFromPointer &&
            Physics.Raycast(pointer.position, pointer.forward, out RaycastHit hit, raycastMaxDistance, raycastMask, QueryTriggerInteraction.Ignore))
        {
            position = hit.point;
            forward = Vector3.ProjectOnPlane(pointer.forward, hit.normal);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(pointer.up, hit.normal);
            pose = new Pose(position, MakeRotation(forward, hit.normal));
        }
        else if (sourceMode == PlacementSourceMode.CameraForwardFallback)
        {
            position = pointer.position + pointer.forward * fallbackDistance;
            forward = pointer.forward;
            pose = new Pose(position, MakeRotation(forward, Vector3.up));
        }
        else
        {
            bool raycastMissed = sourceMode == PlacementSourceMode.RaycastFromPointer;
            position = raycastMissed ? pointer.position + pointer.forward * fallbackDistance : pointer.position;
            forward = pointer.forward;
            pose = new Pose(position, MakeRotation(forward, Vector3.up));
        }

        ApplyCandidateWorldOffset(ref pose);
        return true;
    }

    private void PublishCandidatePose()
    {
        if (previewObject != null)
            previewObject.transform.SetPositionAndRotation(candidatePose.position, candidatePose.rotation);
        CandidatePoseUpdated?.Invoke(candidatePose);
    }

    private void ApplyCandidateWorldOffset(ref Pose pose)
    {
        pose.position += anchorPlacementWorldOffset;
    }

    private void RefreshFallbackCamera()
    {
        if (fallbackCamera == null)
            fallbackCamera = Camera.main;
        if (fallbackCamera == null)
            fallbackCamera = FindAnyObjectByType<Camera>();
    }

    private bool TryGetLiveHandPlacementPose(out Pose pose)
    {
        pose = default;
        if (sourceMode != PlacementSourceMode.OvrHandJoint && !preferLiveHandPoseForPlacement)
            return false;
        if (confirmHand == null || !confirmHand.IsTracked)
            return false;

        if (placeDirectlyBelowHandRoot)
        {
            // OVRHand can live on a stationary parent. Prefer the tracked
            // wrist/root bone, then the tracked pointer pose, and use the
            // component transform only as a last-resort compatibility path.
            Pose trackedRootPose;
            if (!TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_WristRoot, out trackedRootPose) &&
                !TryGetPointerPose(out trackedRootPose))
            {
                trackedRootPose = new Pose(confirmHand.transform.position, confirmHand.transform.rotation);
            }

            Vector3 trackedRootPosition = trackedRootPose.position;
            Quaternion trackedRootRotation = trackedRootPose.rotation;
            trackedRootPosition += Vector3.down * Mathf.Max(0f, directHandVerticalOffsetMeters);
            Vector3 handForward = trackedRootRotation * Vector3.forward;
            pose = new Pose(trackedRootPosition, MakeRotation(handForward, Vector3.up));
            return true;
        }

        Transform handTransform = confirmHand.transform;
        Vector3 position = handTransform.position;
        Quaternion rotation = handTransform.rotation;

        if (placementHandJoint == OvrHandPlacementJoint.PointerPose && TryGetPointerPose(out Pose pointerPose))
        {
            position = pointerPose.position;
            rotation = pointerPose.rotation;
        }
        else if (placementHandJoint == OvrHandPlacementJoint.PinchPoint &&
            TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_IndexTip, out Pose indexPose) &&
            TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_ThumbTip, out Pose thumbPose))
        {
            position = (indexPose.position + thumbPose.position) * 0.5f;
            rotation = indexPose.rotation;
        }
        else if (placementHandJoint == OvrHandPlacementJoint.IndexTip &&
                 TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_IndexTip, out Pose indexTipPose))
        {
            position = indexTipPose.position;
            rotation = indexTipPose.rotation;
        }
        else if (placementHandJoint == OvrHandPlacementJoint.Wrist &&
                 TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_WristRoot, out Pose wristPose))
        {
            position = wristPose.position;
            rotation = wristPose.rotation;
        }

        position += rotation * handPlacementLocalOffset;
        Vector3 forward = rotation * Vector3.forward;
        pose = new Pose(position, MakeRotation(forward, Vector3.up));
        return true;
    }

    private bool TryGetSkeletonBonePose(OVRSkeleton.BoneId boneId, out Pose pose)
    {
        pose = default;
        if (confirmHand == null)
            return false;

        OVRSkeleton skeleton = confirmHand.GetComponent<OVRSkeleton>();
        if (skeleton == null || skeleton.Bones == null)
            return false;

        foreach (var bone in skeleton.Bones)
        {
            if (bone.Id != boneId || bone.Transform == null)
                continue;

            pose = new Pose(bone.Transform.position, bone.Transform.rotation);
            return true;
        }

        return false;
    }

    private bool TryGetPointerPose(out Pose pose)
    {
        pose = default;
        if (confirmHand == null || !confirmHand.IsTracked || !confirmHand.IsPointerPoseValid)
            return false;

        Transform pointer = confirmHand.GetPointerRayTransform();
        if (pointer == null)
            return false;

        pose = new Pose(pointer.position, pointer.rotation);
        return true;
    }

    private Quaternion MakeRotation(Vector3 forward, Vector3 up)
    {
        Vector3 resolvedUp = keepRotationLevel ? Vector3.up : up;
        Vector3 resolvedForward = keepRotationLevel ? Vector3.ProjectOnPlane(forward, Vector3.up) : Vector3.ProjectOnPlane(forward, resolvedUp);

        if (resolvedForward.sqrMagnitude < 1e-6f)
            resolvedForward = Vector3.forward;

        return Quaternion.LookRotation(resolvedForward.normalized, resolvedUp.normalized);
    }

    private void AttachOrCreateMarker()
    {
        if (CurrentAnchor == null)
            return;

        CreateMarkerUnder(CurrentAnchor.transform, "SpatialAnchorMarker");
    }

    private async System.Threading.Tasks.Task CreateAndSaveOvrSpatialAnchorAsync()
    {
        isCreatingPersistentOvrAnchor = true;
        ClearRuntimeAnchors();

        GameObject anchorObject = new GameObject("PersistentDeskAnchor");
        anchorObject.transform.SetPositionAndRotation(candidatePose.position, candidatePose.rotation);
        OVRSpatialAnchor ovrAnchor = anchorObject.AddComponent<OVRSpatialAnchor>();

        bool localized = await ovrAnchor.WhenLocalizedAsync();
        if (!isCreatingAnchor)
        {
            if (ovrAnchor != null)
                Destroy(ovrAnchor.gameObject);
            isCreatingPersistentOvrAnchor = false;
            return;
        }

        if (!localized || ovrAnchor == null || !ovrAnchor.Created)
        {
            isCreatingAnchor = false;
            isCreatingPersistentOvrAnchor = false;
            if (ovrAnchor != null)
                Destroy(ovrAnchor.gameObject);
            FailAnchorCreation("OVR Spatial Anchor creation/localization failed.");
            return;
        }

        var saveResult = await ovrAnchor.SaveAnchorAsync();
        if (!isCreatingAnchor)
        {
            if (ovrAnchor != null)
                Destroy(ovrAnchor.gameObject);
            isCreatingPersistentOvrAnchor = false;
            return;
        }

        isCreatingAnchor = false;
        isCreatingPersistentOvrAnchor = false;
        if (!saveResult.Success)
        {
            Destroy(ovrAnchor.gameObject);
            FailAnchorCreation($"OVR Spatial Anchor save failed: {saveResult.Status}");
            return;
        }

        currentOvrSpatialAnchor = ovrAnchor;
        LastAnchorWasLoadedSavedAnchor = false;
        PlayerPrefs.SetString(savedAnchorPlayerPrefsKey, currentOvrSpatialAnchor.Uuid.ToString());
        PlayerPrefs.Save();

        CreateMarkerUnder(currentOvrSpatialAnchor.transform, "PersistentDeskAnchorMarker");
        SetStatusMessage("Persistent Spatial Anchor saved");
        AnchorTransformCreated?.Invoke(currentOvrSpatialAnchor.transform);
    }

    private async System.Threading.Tasks.Task WaitForLoadedAnchorPoseStableAsync(Transform anchorTransform)
    {
        if (anchorTransform == null || savedAnchorStabilizeSeconds <= 0f)
            return;

        float startTime = Time.realtimeSinceStartup;
        float stableStartTime = Time.realtimeSinceStartup;
        Vector3 lastPosition = anchorTransform.position;
        Quaternion lastRotation = anchorTransform.rotation;

        while (anchorTransform != null)
        {
            await System.Threading.Tasks.Task.Yield();
            if (anchorTransform == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (now - startTime >= savedAnchorMaxStabilizeWaitSeconds)
                return;

            float positionDelta = Vector3.Distance(anchorTransform.position, lastPosition);
            float rotationDelta = Quaternion.Angle(anchorTransform.rotation, lastRotation);
            bool stable = positionDelta <= savedAnchorStablePositionThresholdMeters &&
                          rotationDelta <= savedAnchorStableRotationThresholdDegrees;

            if (!stable)
            {
                stableStartTime = now;
                lastPosition = anchorTransform.position;
                lastRotation = anchorTransform.rotation;
                continue;
            }

            if (now - stableStartTime >= savedAnchorStabilizeSeconds)
                return;

            lastPosition = anchorTransform.position;
            lastRotation = anchorTransform.rotation;
        }
    }

    private void FailAnchorCreation(string reason)
    {
        Debug.LogError($"[ManualSpatialAnchorPlacer] Failed to create anchor: {reason}");
        SetStatusMessage($"Anchor create failed\n{reason}");
        if (allowPcvrSessionAnchorFallback)
            CreatePcvrSessionAnchor(reason);
        AnchorCreateFailed?.Invoke(reason);
    }

    private void OnHmdMounted()
    {
        if (!reloadSavedAnchorOnHmdMounted && !reapplyCurrentAnchorOnHmdMounted)
            return;

        if (isLoadingSavedAnchor)
            return;

        if (reapplyCurrentAnchorImmediatelyOnHmdMounted)
            ReapplyCurrentAnchorAfterHmdMounted();

        pendingHmdMountedAnchorReload = true;
        hmdMountedAnchorReloadTime = Time.realtimeSinceStartup + Mathf.Max(0f, hmdMountedAnchorReloadDelaySec);
    }

    private void CreatePcvrSessionAnchor(string reason)
    {
        ClearSessionAnchor();

        GameObject sessionAnchor = new GameObject("PCVRSessionAnchor");
        sessionAnchor.transform.SetPositionAndRotation(candidatePose.position, candidatePose.rotation);
        sessionAnchorTransform = sessionAnchor.transform;
        LastAnchorWasLoadedSavedAnchor = false;

        CreateMarkerUnder(sessionAnchorTransform, "PCVRSessionAnchorMarker");
        SetStatusMessage($"PCVR session anchor created\n{reason}");
        AnchorTransformCreated?.Invoke(sessionAnchorTransform);
    }

    private void ClearRuntimeAnchors()
    {
        if (anchorMarkerInstance != null)
        {
            Destroy(anchorMarkerInstance);
            anchorMarkerInstance = null;
        }

        ClearSessionAnchor();
        ClearOvrSpatialAnchor();

        if (CurrentAnchor != null && anchorManager != null)
        {
            anchorManager.TryRemoveAnchor(CurrentAnchor);
            CurrentAnchor = null;
        }

        LastAnchorWasLoadedSavedAnchor = false;
    }

    private void ClearOvrSpatialAnchor()
    {
        if (currentOvrSpatialAnchor == null)
            return;

        Destroy(currentOvrSpatialAnchor.gameObject);
        currentOvrSpatialAnchor = null;
        LastAnchorWasLoadedSavedAnchor = false;
    }

    private void CreateMarkerUnder(Transform parent, string markerName)
    {
        if (anchorMarkerInstance != null)
            Destroy(anchorMarkerInstance);

        anchorMarkerInstance = anchorMarkerPrefab != null
            ? Instantiate(anchorMarkerPrefab, parent)
            : RuntimeCoordinateAxes.Create(
                markerName,
                parent,
                defaultAxisLength,
                defaultAxisLineWidth,
                defaultAxisShowLabels).gameObject;

        anchorMarkerInstance.name = markerName;

        if (anchorMarkerPrefab == null)
        {
            anchorMarkerInstance.transform.SetParent(parent, false);
        }

        anchorMarkerInstance.transform.localPosition = Vector3.zero;
        anchorMarkerInstance.transform.localRotation = Quaternion.identity;
        anchorMarkerInstance.SetActive(showAnchorCoordinateAxes);
    }

    public void SetAnchorCoordinateAxesVisible(bool visible)
    {
        showAnchorCoordinateAxes = visible;
        if (previewObject != null)
            previewObject.SetActive(visible && (IsPlacementMode || !hidePreviewWhenIdle));
        if (anchorMarkerInstance != null)
            anchorMarkerInstance.SetActive(visible);
    }

    private void SetPreviewActive(bool active)
    {
        if (previewObject != null)
            previewObject.SetActive(showAnchorCoordinateAxes && (active || !hidePreviewWhenIdle));
    }

    private void EnsureDefaultVisuals()
    {
        if (!createDefaultVisuals)
            return;

        if (previewObject == null)
        {
            previewObject = RuntimeCoordinateAxes.Create(
                "SpatialAnchorPreviewAxes",
                null,
                defaultAxisLength,
                defaultAxisLineWidth,
                defaultAxisShowLabels).gameObject;
        }
    }

    private void EnsureDefaultStatusText()
    {
        if (!createDefaultStatusText || statusText != null)
            return;

        GameObject textObject = new GameObject("SpatialAnchorStatusText");
        statusText = textObject.AddComponent<TextMesh>();
        statusText.anchor = TextAnchor.MiddleCenter;
        statusText.alignment = TextAlignment.Center;
        statusText.characterSize = 0.045f;
        statusText.fontSize = 48;
        statusText.color = Color.white;
    }

    private void UpdateStatusTextPose()
    {
        if (statusText == null)
            return;

        Transform cam = fallbackCamera != null ? fallbackCamera.transform : (Camera.main != null ? Camera.main.transform : null);
        if (cam == null)
            return;

        statusText.transform.position =
            cam.position +
            cam.right * statusTextCameraOffset.x +
            cam.up * statusTextCameraOffset.y +
            cam.forward * statusTextCameraOffset.z;
        statusText.transform.rotation = Quaternion.LookRotation(statusText.transform.position - cam.position, cam.up);
    }

    private void ClearSessionAnchor()
    {
        if (sessionAnchorTransform == null)
            return;

        Destroy(sessionAnchorTransform.gameObject);
        sessionAnchorTransform = null;
    }
}
