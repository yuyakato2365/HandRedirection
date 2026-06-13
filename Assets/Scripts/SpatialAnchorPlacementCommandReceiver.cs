using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class SpatialAnchorPlacementCommandReceiver : MonoBehaviour
{
    [Header("Command UDP")]
    public int listenPort = 9101;
    public bool forceIPv4 = true;

    [Header("Status UDP")]
    public bool sendStatus = true;
    public int statusPort = 9102;
    public string statusHostOverride = "";

    [Header("Anchor Placement")]
    public ManualSpatialAnchorPlacer placer;
    public SpatialAnchorToDeskOriginBinder deskBinder;
    public SpatialAnchorRedirectionToggle featureToggle;

    [Header("Exhibition Reset")]
    public ExhibitionExperienceResetter experienceResetter;

    [Header("Debug")]
    public bool logCommands = false;

    private UdpClient udp;
    private Thread recvThread;
    private volatile bool running;
    private readonly object queueLock = new object();
    private readonly Queue<string> pendingCommands = new Queue<string>();
    private IPEndPoint lastSender;

    private void Awake()
    {
        if (placer == null)
            placer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (featureToggle == null)
            featureToggle = FindAnyObjectByType<SpatialAnchorRedirectionToggle>();
        EnsureExperienceResetter();
    }

    private void OnEnable()
    {
        SubscribePlacerEvents();
        StartReceiver();
    }

    private void OnDisable()
    {
        StopReceiver();
        UnsubscribePlacerEvents();
    }

    private void Update()
    {
        while (TryDequeueCommand(out string command))
            HandleCommand(command);
    }

    private void SubscribePlacerEvents()
    {
        if (placer == null)
            return;

        placer.PlacementStarted += OnPlacementStarted;
        placer.PlacementCanceled += OnPlacementCanceled;
        placer.AnchorCreated += OnAnchorCreated;
        placer.AnchorTransformCreated += OnAnchorTransformCreated;
        placer.AnchorCleared += OnAnchorCleared;
        placer.AnchorCreateFailed += OnAnchorCreateFailed;
    }

    private void UnsubscribePlacerEvents()
    {
        if (placer == null)
            return;

        placer.PlacementStarted -= OnPlacementStarted;
        placer.PlacementCanceled -= OnPlacementCanceled;
        placer.AnchorCreated -= OnAnchorCreated;
        placer.AnchorTransformCreated -= OnAnchorTransformCreated;
        placer.AnchorCleared -= OnAnchorCleared;
        placer.AnchorCreateFailed -= OnAnchorCreateFailed;
    }

    private void StartReceiver()
    {
        try
        {
            udp = forceIPv4 ? new UdpClient(AddressFamily.InterNetwork) : new UdpClient(AddressFamily.Unspecified);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            running = true;
            recvThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "SpatialAnchorPlacementCommandReceiver"
            };
            recvThread.Start();
            SendStatus("QUEST_COMMAND_RECEIVER_READY");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpatialAnchorPlacementCommandReceiver] UDP init failed: {e.Message}");
            running = false;
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        running = false;

        try { udp?.Close(); } catch { }
        udp = null;

        try
        {
            if (recvThread != null && recvThread.IsAlive)
                recvThread.Join(200);
        }
        catch { }

        recvThread = null;
    }

    private void ReceiveLoop()
    {
        while (running && udp != null)
        {
            try
            {
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udp.Receive(ref sender);
                string command = Encoding.UTF8.GetString(data).Trim();

                lock (queueLock)
                {
                    lastSender = sender;
                    pendingCommands.Enqueue(command);
                }
            }
            catch (SocketException)
            {
                if (!running)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SpatialAnchorPlacementCommandReceiver] Receive error: {e.Message}");
            }
        }
    }

    private bool TryDequeueCommand(out string command)
    {
        lock (queueLock)
        {
            if (pendingCommands.Count == 0)
            {
                command = null;
                return false;
            }

            command = pendingCommands.Dequeue();
            return true;
        }
    }

    private void HandleCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        string normalized = command.Trim().ToUpperInvariant();
        if (logCommands)
            Debug.Log($"[SpatialAnchorPlacementCommandReceiver] Command: {normalized}");

        if (placer == null)
        {
            SendStatus("ERROR placer_not_assigned");
            return;
        }

        switch (normalized)
        {
            case "BEGIN_ANCHOR_PLACEMENT":
                placer.BeginPlacement();
                if (featureToggle != null)
                    featureToggle.UseSpatialAnchorMode();
                break;
            case "CANCEL_ANCHOR_PLACEMENT":
                placer.CancelPlacement();
                break;
            case "CONFIRM_ANCHOR_PLACEMENT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder placementConfirmBinder) &&
                    placementConfirmBinder.IsAdjustingAlignment)
                {
                    placementConfirmBinder.ConfirmManualRotationAlignment();
                }
                if (placer.IsPlacementMode)
                    placer.ConfirmPlacement();
                break;
            case "CLEAR_ANCHOR":
                placer.ClearAnchor();
                SendStatus("ANCHOR_CLEARED");
                break;
            case "LOAD_SAVED_ANCHOR":
            case "LOAD_PERSISTENT_ANCHOR":
                placer.LoadSavedAnchor();
                if (featureToggle != null)
                    featureToggle.UseSpatialAnchorMode();
                SendStatus("LOAD_SAVED_ANCHOR_REQUESTED");
                break;
            case "CLEAR_SAVED_ANCHOR":
            case "ERASE_SAVED_ANCHOR":
                placer.ClearSavedAnchor();
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder savedClearBinder))
                    savedClearBinder.ClearSavedOffsetPrefs();
                SendStatus("CLEAR_SAVED_ANCHOR_REQUESTED");
                break;
            case "HAS_SAVED_ANCHOR":
                SendStatus(placer.HasSavedAnchor ? "HAS_SAVED_ANCHOR true" : "HAS_SAVED_ANCHOR false");
                break;
            case "BEGIN_DESK_ROTATION_ADJUSTMENT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder beginBinder))
                {
                    beginBinder.BeginManualRotationAlignment();
                    SendStatus("DESK_ALIGNMENT_STARTED");
                }
                break;
            case "ROTATE_DESK_LEFT":
            case "ADJUST_DESK_YAW_LEFT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder leftBinder))
                {
                    leftBinder.AdjustYawLeft();
                    SendStatus($"DESK_YAW_ADJUSTED {leftBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "ROTATE_DESK_RIGHT":
            case "ADJUST_DESK_YAW_RIGHT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder rightBinder))
                {
                    rightBinder.AdjustYawRight();
                    SendStatus($"DESK_YAW_ADJUSTED {rightBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "ROTATE_DESK_LEFT_LARGE":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder leftLargeBinder))
                {
                    leftLargeBinder.AdjustYawLeftLarge();
                    SendStatus($"DESK_YAW_ADJUSTED {leftLargeBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "ROTATE_DESK_RIGHT_LARGE":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder rightLargeBinder))
                {
                    rightLargeBinder.AdjustYawRightLarge();
                    SendStatus($"DESK_YAW_ADJUSTED {rightLargeBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "RESET_DESK_ROTATION":
            case "RESET_DESK_YAW":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder resetBinder))
                {
                    resetBinder.ResetYawAdjustment();
                    SendStatus("DESK_YAW_RESET");
                }
                break;
            case "CONFIRM_DESK_ALIGNMENT":
            case "CONFIRM_DESK_ROTATION":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder confirmBinder))
                {
                    confirmBinder.ConfirmManualRotationAlignment();
                    SendStatus("DESK_ALIGNMENT_CONFIRMED");
                }
                break;
            case "SET_REDIRECTION_ORIGIN":
            case "REARM_REDIRECTION_ORIGIN":
            case "REARM_RIGHT_PINCH_REDIRECTION_ORIGIN":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder redirectionOriginBinder))
                {
                    redirectionOriginBinder.RearmRightPinchRedirectionOrigin();
                    if (placer != null)
                        placer.SetStatusMessage("Right pinch will set redirection origin");
                    SendStatus("REDIRECTION_ORIGIN_ARMED_RIGHT_PINCH");
                }
                break;
            case "RESET_REDIRECTION_ORIGIN":
            case "RESET_REDIRECTION_ORIGIN_TO_DESK":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder resetRedirectionOriginBinder))
                {
                    resetRedirectionOriginBinder.ResetRedirectionOriginToDesk();
                    if (placer != null)
                        placer.SetStatusMessage("Redirection origin reset to desk origin");
                    SendStatus("REDIRECTION_ORIGIN_RESET_TO_DESK");
                }
                break;
            case "ENABLE_ANCHOR_REDIRECTION":
            case "USE_SPATIAL_ANCHOR_REDIRECTION":
                if (featureToggle != null)
                {
                    featureToggle.UseSpatialAnchorMode();
                    SendStatus("SPATIAL_ANCHOR_REDIRECTION_MODE");
                }
                else
                {
                    SendStatus("ERROR toggle_not_assigned");
                }
                break;
            case "DISABLE_ANCHOR_REDIRECTION":
            case "RESTORE_ORIGINAL_HAND_REDIRECTION":
                if (featureToggle != null)
                {
                    featureToggle.UseOriginalMode();
                    SendStatus("ORIGINAL_HAND_REDIRECTION_MODE");
                }
                else
                {
                    SendStatus("ERROR toggle_not_assigned");
                }
                break;
            case "NEXT_PARTICIPANT":
            case "RESET_FOR_NEXT_PARTICIPANT":
            case "RESET_EXPERIENCE_FOR_NEXT_PARTICIPANT":
            case "RESET_OBJECT_SCALES":
                int restoredCount = ResetExperienceForNextParticipant();
                SendStatus($"NEXT_PARTICIPANT_RESET_DONE {restoredCount}");
                break;
            case "PING":
                placer.SetStatusMessage("PING received from PC");
                SendStatus("PONG");
                break;
            default:
                SendStatus($"ERROR unknown_command {normalized}");
                break;
        }
    }

    private void OnPlacementStarted()
    {
        SendStatus("ANCHOR_PLACEMENT_STARTED");
    }

    private void OnPlacementCanceled()
    {
        SendStatus("ANCHOR_PLACEMENT_CANCELED");
    }

    private void OnAnchorCreated(UnityEngine.XR.ARFoundation.ARAnchor anchor)
    {
        SendStatus($"ANCHOR_CREATED {anchor.trackableId}");
    }

    private void OnAnchorTransformCreated(Transform anchorTransform)
    {
        SendStatus("ANCHOR_TRANSFORM_CREATED_BEGIN_DESK_ALIGNMENT");
    }

    private void OnAnchorCleared()
    {
        SendStatus("ANCHOR_CLEARED");
    }

    private void OnAnchorCreateFailed(string reason)
    {
        SendStatus($"ANCHOR_CREATE_FAILED {reason}");
    }

    private bool TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder resolvedBinder)
    {
        resolvedBinder = deskBinder != null ? deskBinder : FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (resolvedBinder != null)
            return true;

        SendStatus("ERROR desk_binder_not_assigned");
        return false;
    }

    private void EnsureExperienceResetter()
    {
        if (experienceResetter != null)
            return;

        experienceResetter = FindAnyObjectByType<ExhibitionExperienceResetter>();
        if (experienceResetter != null)
            return;

        GameObject resetterObject = new GameObject("ExhibitionExperienceResetter");
        experienceResetter = resetterObject.AddComponent<ExhibitionExperienceResetter>();
    }

    private int ResetExperienceForNextParticipant()
    {
        EnsureExperienceResetter();
        if (experienceResetter == null)
            return 0;

        return experienceResetter.ResetForNextParticipant();
    }

    private void SendStatus(string message)
    {
        if (!sendStatus)
            return;

        try
        {
            string host = statusHostOverride;
            if (string.IsNullOrWhiteSpace(host))
            {
                lock (queueLock)
                {
                    host = lastSender != null ? lastSender.Address.ToString() : null;
                }
            }

            if (string.IsNullOrWhiteSpace(host))
                return;

            using (UdpClient statusClient = new UdpClient())
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                statusClient.Send(data, data.Length, host, statusPort);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpatialAnchorPlacementCommandReceiver] Status send failed: {e.Message}");
        }
    }
}
