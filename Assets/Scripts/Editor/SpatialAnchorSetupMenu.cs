using UnityEditor;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;

public static class SpatialAnchorSetupMenu
{
    [MenuItem("Tools/Spatial Anchor/Create Basic Setup")]
    public static void CreateBasicSetup()
    {
        XROrigin origin = Object.FindFirstObjectByType<XROrigin>();
        ARAnchorManager anchorManager = origin != null ? origin.GetComponent<ARAnchorManager>() : null;

        if (origin != null && anchorManager == null)
            anchorManager = Undo.AddComponent<ARAnchorManager>(origin.gameObject);

        GameObject root = GameObject.Find("SpatialAnchorCalibrationRoot");
        if (root == null)
        {
            root = new GameObject("SpatialAnchorCalibrationRoot");
            Undo.RegisterCreatedObjectUndo(root, "Create Spatial Anchor Calibration Root");
        }

        ManualSpatialAnchorPlacer placer = root.GetComponent<ManualSpatialAnchorPlacer>();
        if (placer == null)
            placer = Undo.AddComponent<ManualSpatialAnchorPlacer>(root);

        SpatialAnchorPlacementCommandReceiver receiver = root.GetComponent<SpatialAnchorPlacementCommandReceiver>();
        if (receiver == null)
            receiver = Undo.AddComponent<SpatialAnchorPlacementCommandReceiver>(root);

        SpatialAnchorToDeskOriginBinder binder = root.GetComponent<SpatialAnchorToDeskOriginBinder>();
        if (binder == null)
            binder = Undo.AddComponent<SpatialAnchorToDeskOriginBinder>(root);

        SpatialAnchorRedirectionToggle toggle = root.GetComponent<SpatialAnchorRedirectionToggle>();
        if (toggle == null)
            toggle = Undo.AddComponent<SpatialAnchorRedirectionToggle>(root);

        Undo.RecordObject(placer, "Configure Manual Spatial Anchor Placer");
        placer.anchorManager = anchorManager;
        placer.useOvrSpatialAnchorPersistence = true;
        placer.sourceMode = ManualSpatialAnchorPlacer.PlacementSourceMode.OvrHandJoint;
        placer.preferLiveHandPoseForPlacement = true;
        placer.placementHandJoint = ManualSpatialAnchorPlacer.OvrHandPlacementJoint.PointerPose;
        placer.enableOvrControllerInput = false;
        placer.enableOvrHandPinchInput = true;
        placer.autoFindConfirmHand = true;
        placer.createDefaultVisuals = true;
        placer.createDefaultStatusText = true;
        placer.loadSavedAnchorOnStart = false;
        placer.reloadSavedAnchorOnHmdMounted = true;
        placer.reapplyCurrentAnchorOnHmdMounted = false;
        placer.reapplyDeskOriginAfterHmdMountedAnchorReload = false;
        placer.hmdMountedAnchorReloadDelaySec = 1f;
        placer.hmdMountedAnchorReloadCooldownSec = 0f;
        placer.reapplyCurrentAnchorImmediatelyOnHmdMounted = false;
        placer.hmdMountedAnchorLoadWarmupFrames = 0;
        placer.skipSavedAnchorReloadOnHmdMountedInEditor = false;
        placer.allowPcvrSessionAnchorFallback = true;

        Undo.RecordObject(receiver, "Configure Spatial Anchor Command Receiver");
        receiver.placer = placer;
        receiver.deskBinder = binder;
        receiver.listenPort = 9101;
        receiver.statusPort = 9102;
        receiver.sendStatus = true;
        receiver.featureToggle = toggle;
        receiver.logCommands = false;

        Undo.RecordObject(binder, "Configure Spatial Anchor Desk Binder");
        binder.anchorPlacer = placer;
        binder.requireManualRotationConfirmation = true;
        binder.correctHmdRemountByKeepingDeskOriginFixed = false;
        binder.applySavedRedirectionOriginOnAlignment = false;
        binder.logHandAlignmentDebug = false;
        binder.writeHandAlignmentLogFile = false;
        binder.activePinchLogIntervalSec = 1f;
        binder.logAnchorDeskDiagnostics = false;

        Undo.RecordObject(toggle, "Configure Spatial Anchor Redirection Toggle");
        toggle.placer = placer;
        toggle.deskBinder = binder;
        toggle.commandReceiver = receiver;
        toggle.startMode = SpatialAnchorRedirectionToggle.RedirectionMode.Original;
        toggle.disableHandRedirectionUntilAnchorExists = true;

        GoGoInteractionController_NoY3 goGo = Object.FindFirstObjectByType<GoGoInteractionController_NoY3>();
        if (goGo != null && (toggle.handRedirectionBehaviours == null || toggle.handRedirectionBehaviours.Length == 0))
            toggle.handRedirectionBehaviours = new MonoBehaviour[] { goGo };

        TrackerToCubeOffsetCalibrator3 trackerCalibrator = Object.FindFirstObjectByType<TrackerToCubeOffsetCalibrator3>();
        if (trackerCalibrator != null && (toggle.originalModeBehaviours == null || toggle.originalModeBehaviours.Length == 0))
            toggle.originalModeBehaviours = new MonoBehaviour[] { trackerCalibrator };

        EditorUtility.SetDirty(root);
        if (origin != null)
            EditorUtility.SetDirty(origin.gameObject);

        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);

        if (origin == null)
        {
            Debug.LogWarning("[SpatialAnchorSetupMenu] XROrigin was not found. Assign ARAnchorManager manually after adding an XR Origin.");
        }
        else
        {
            Debug.Log("[SpatialAnchorSetupMenu] Basic Spatial Anchor setup created.");
        }
    }
}
