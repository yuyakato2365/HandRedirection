using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Receives tracker poses over UDP.
/// - DSK0: desk tracker pose in head space (head <- desk)
/// - REL0: object tracker pose, usually in head-tracker space (head <- object)
/// In Spatial Anchor mode, DSK0 is used only as the desk-tracker reference for REL0;
/// the Unity DeskOrigin itself can remain driven by the spatial anchor.
/// </summary>
public class TrackerToCubeOffsetCalibrator3 : MonoBehaviour
{
    public enum ObjectPoseSpace
    {
        HeadTracker,
        DeskTracker
    }

    [Serializable]
    public class TargetEntry
    {
        [Header("Identity")]
        [Tooltip("Python side OBJECT_SERIALS assigns this objectId.")]
        public uint objectId = 1;

        [Tooltip("Unity object that should follow this tracker/object.")]
        public Transform objTransform;

        [Header("Tracker -> Object Center Offset")]
        [Tooltip("Fixed offset from tracker origin to object center in tracker local coordinates.")]
        public Vector3 centerOffsetInTracker = Vector3.zero;

        [Tooltip("Fixed extra rotation from tracker orientation to object orientation in degrees.")]
        public Vector3 centerEulerOffset = Vector3.zero;

        [Header("DeskOrigin Offset")]
        [Tooltip("DeskOrigin-local position offset applied only to this object after tracker-specific offsets.")]
        public Vector3 objectDeskPositionOffset = Vector3.zero;
    }

    [Serializable]
    private class SavedTargetOffset
    {
        public uint objectId;
        public Vector3 centerOffsetInTracker;
        public Vector3 centerEulerOffset;
    }

    [Serializable]
    private class SavedTargetOffsetCollection
    {
        public List<SavedTargetOffset> entries = new List<SavedTargetOffset>();
    }

    private struct RelativePose
    {
        public long nowMs;
        public uint objId;
        public Vector3 pos;
        public Quaternion rot;
    }

    private struct DeskPose
    {
        public long nowMs;
        public Vector3 pos;
        public Quaternion rot;
    }

    [Header("UDP")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Header("Reference Transforms")]
    [Tooltip("Unity-side HMD transform. Used to convert head-relative desk pose into world pose.")]
    public Transform hmdTransform;

    [Tooltip("Unity transform that represents the moving desk origin. GoGo's deskOrigin should reference this same transform.")]
    public Transform deskTransform;

    [Tooltip("When false, DSK0 is not allowed to move deskTransform. REL0 objects can still be converted through the latest DSK0 pose and placed under deskTransform.")]
    public bool updateDeskTransformFromPackets = true;

    [Header("Head -> HMD Offset")]
    [Tooltip("Translation from head tracker origin to Unity HMD origin, expressed in head coordinates.")]
    public Vector3 headToHmdPos = Vector3.zero;

    [Tooltip("Rotation from head tracker frame to Unity HMD frame, expressed in head coordinates (deg).")]
    public Vector3 headToHmdEuler = Vector3.zero;

    [Header("Desk Origin Smoothing")]
    [Tooltip("0 disables smoothing. Larger values follow desk position faster.")]
    public float deskPositionLerp = 0f;

    [Tooltip("0 disables smoothing. Larger values follow desk rotation faster.")]
    public float deskRotationSlerp = 0f;

    [Header("Desk Origin Offset")]
    [Tooltip("Additional desk-origin position offset in detected desk local space.")]
    public Vector3 deskPositionOffset = Vector3.zero;

    [Tooltip("Additional desk-origin rotation offset in detected desk local space (deg).")]
    public Vector3 deskEulerOffset = Vector3.zero;

    [Header("Targets")]
    [Tooltip("Coordinate space of REL0 packets. Use HeadTracker when REL0 is still measured relative to the head tracker; it will be converted through DSK0 into DeskOrigin space.")]
    public ObjectPoseSpace objectPoseSpace = ObjectPoseSpace.DeskTracker;

    [Tooltip("If REL0 is in HeadTracker space, skip object updates until a DSK0 desk-tracker packet has arrived.")]
    public bool requireDeskPoseForHeadTrackerObjects = true;

    [Tooltip("Objects driven by REL0 packets.")]
    public List<TargetEntry> targets = new List<TargetEntry>();

    [Header("Runtime Offset Persistence")]
    [Tooltip("Save per-object tracker offsets changed from the PC control window.")]
    public bool persistRuntimeTargetOffsets = true;

    public string targetOffsetsPlayerPrefsKey = "HandRedirection.TrackerTargetOffsets";

    [Header("DeskOrigin Object Group Offset")]
    [Tooltip("DeskOrigin-local position offset applied to every target object after tracker-specific offsets. Use this to move the whole object set relative to DeskOrigin axes.")]
    public Vector3 objectGroupDeskPositionOffset = Vector3.zero;

    [Header("Object Smoothing")]
    [Tooltip("0 disables smoothing. Larger values follow object position faster.")]
    public float positionLerp = 0f;

    [Tooltip("0 disables smoothing. Larger values follow object rotation faster.")]
    public float rotationSlerp = 0f;

    [Header("Debug")]
    public bool logBadPackets = false;
    public bool logReceivedIds = false;
    public bool logDeskPackets = false;

    private UdpClient udp;
    private Thread recvThread;
    private volatile bool running;

    private readonly object latestLock = new object();
    private readonly Dictionary<uint, RelativePose> latestById = new Dictionary<uint, RelativePose>();
    private readonly HashSet<uint> loggedIds = new HashSet<uint>();
    private bool hasLatestDeskPose;
    private DeskPose latestDeskPose;

    private void Start()
    {
        LoadTargetOffsetsFromPrefs();
        ValidateConfiguration();

        try
        {
            if (forceIPv4)
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            }
            else
            {
                udp = new UdpClient(listenPort);
            }

            running = true;
            recvThread = new Thread(RecvLoop)
            {
                IsBackground = true,
                Name = "UdpDeskAndObjectReceiver"
            };
            recvThread.Start();

            Debug.Log($"[TrackerToCubeOffsetCalibrator3] UDP listening on :{listenPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TrackerToCubeOffsetCalibrator3] UDP init failed: {e}");
            running = false;
        }
    }

    public bool TrySetTargetOffset(uint objectId, Vector3 positionOffset, Vector3 eulerOffset, bool save = true)
    {
        TargetEntry target = FindTarget(objectId);
        if (target == null)
            return false;

        target.centerOffsetInTracker = positionOffset;
        target.centerEulerOffset = eulerOffset;
        if (save)
            SaveTargetOffsetsToPrefs();
        return true;
    }

    public bool TryGetTargetOffset(uint objectId, out Vector3 positionOffset, out Vector3 eulerOffset)
    {
        TargetEntry target = FindTarget(objectId);
        if (target == null)
        {
            positionOffset = Vector3.zero;
            eulerOffset = Vector3.zero;
            return false;
        }

        positionOffset = target.centerOffsetInTracker;
        eulerOffset = target.centerEulerOffset;
        return true;
    }

    public IEnumerable<TargetEntry> EnumerateTargetOffsets()
    {
        return targets ?? new List<TargetEntry>();
    }

    public void SaveTargetOffsetsToPrefs()
    {
        if (!persistRuntimeTargetOffsets || string.IsNullOrWhiteSpace(targetOffsetsPlayerPrefsKey))
            return;

        var saved = new SavedTargetOffsetCollection();
        if (targets != null)
        {
            foreach (TargetEntry target in targets)
            {
                if (target == null)
                    continue;
                saved.entries.Add(new SavedTargetOffset
                {
                    objectId = target.objectId,
                    centerOffsetInTracker = target.centerOffsetInTracker,
                    centerEulerOffset = target.centerEulerOffset
                });
            }
        }

        PlayerPrefs.SetString(targetOffsetsPlayerPrefsKey, JsonUtility.ToJson(saved));
        PlayerPrefs.Save();
    }

    public void LoadTargetOffsetsFromPrefs()
    {
        if (!persistRuntimeTargetOffsets || string.IsNullOrWhiteSpace(targetOffsetsPlayerPrefsKey))
            return;

        string json = PlayerPrefs.GetString(targetOffsetsPlayerPrefsKey, "");
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            SavedTargetOffsetCollection saved = JsonUtility.FromJson<SavedTargetOffsetCollection>(json);
            if (saved?.entries == null)
                return;

            foreach (SavedTargetOffset entry in saved.entries)
            {
                TargetEntry target = FindTarget(entry.objectId);
                if (target == null)
                    continue;
                target.centerOffsetInTracker = entry.centerOffsetInTracker;
                target.centerEulerOffset = entry.centerEulerOffset;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TrackerToCubeOffsetCalibrator3] Failed to load target offsets: {e.Message}");
        }
    }

    private TargetEntry FindTarget(uint objectId)
    {
        return targets?.Find(target => target != null && target.objectId == objectId);
    }

    private void OnDestroy()
    {
        running = false;

        try { udp?.Close(); } catch { }
        udp = null;

        try
        {
            if (recvThread != null && recvThread.IsAlive)
            {
                recvThread.Join(200);
            }
        }
        catch { }

        recvThread = null;
    }

    private void Update()
    {
        if (deskTransform == null)
            return;

        if (updateDeskTransformFromPackets && hmdTransform != null)
            UpdateDeskTransform();

        UpdateTargetObjects();
    }

    private void UpdateDeskTransform()
    {
        DeskPose deskPose;
        bool hasDesk;

        lock (latestLock)
        {
            hasDesk = hasLatestDeskPose;
            deskPose = latestDeskPose;
        }

        if (!hasDesk)
            return;

        Quaternion headToHmdRot = Quaternion.Euler(headToHmdEuler);
        Quaternion invHeadToHmdRot = Quaternion.Inverse(headToHmdRot);

        Vector3 deskInHmdPos = invHeadToHmdRot * (deskPose.pos - headToHmdPos);
        Quaternion deskInHmdRot = invHeadToHmdRot * deskPose.rot;

        Quaternion deskOffsetRot = Quaternion.Euler(deskEulerOffset);
        Quaternion finalDeskInHmdRot = deskInHmdRot * deskOffsetRot;
        Vector3 offsetInHmd = finalDeskInHmdRot * deskPositionOffset;
        deskInHmdPos += offsetInHmd;
        deskInHmdRot = finalDeskInHmdRot;

        Vector3 targetPosW = hmdTransform.position + (hmdTransform.rotation * deskInHmdPos);
        Quaternion targetRotW = hmdTransform.rotation * deskInHmdRot;

        ApplyPose(deskTransform, targetPosW, targetRotW, deskPositionLerp, deskRotationSlerp);
    }

    private void UpdateTargetObjects()
    {
        if (targets == null || targets.Count == 0)
            return;

        Vector3 deskPosW = deskTransform.position;
        Quaternion deskRotW = deskTransform.rotation;
        DeskPose deskPose;
        bool hasDeskPose;
        lock (latestLock)
        {
            hasDeskPose = hasLatestDeskPose;
            deskPose = latestDeskPose;
        }

        if (objectPoseSpace == ObjectPoseSpace.HeadTracker &&
            requireDeskPoseForHeadTrackerObjects &&
            !hasDeskPose)
        {
            return;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            TargetEntry target = targets[i];
            if (target == null || target.objTransform == null)
                continue;

            RelativePose rel;
            bool found;
            lock (latestLock)
            {
                found = latestById.TryGetValue(target.objectId, out rel);
            }

            if (!found)
                continue;

            if (!TryResolveObjectPoseInDeskOrigin(rel, hasDeskPose, deskPose, out Vector3 relPosInDeskOrigin, out Quaternion relRotInDeskOrigin))
                continue;

            Quaternion centerRotOffset = Quaternion.Euler(target.centerEulerOffset);
            Vector3 centerInDeskPos = relPosInDeskOrigin + (relRotInDeskOrigin * target.centerOffsetInTracker);
            Quaternion centerInDeskRot = relRotInDeskOrigin * centerRotOffset;

            Vector3 targetPosW = deskPosW + (deskRotW * (centerInDeskPos + objectGroupDeskPositionOffset + target.objectDeskPositionOffset));
            Quaternion targetRotW = deskRotW * centerInDeskRot;

            ApplyPose(target.objTransform, targetPosW, targetRotW, positionLerp, rotationSlerp);
        }
    }

    private bool TryResolveObjectPoseInDeskOrigin(
        RelativePose rel,
        bool hasDeskPose,
        DeskPose deskPose,
        out Vector3 positionInDeskOrigin,
        out Quaternion rotationInDeskOrigin)
    {
        Quaternion deskOffsetRot = Quaternion.Euler(deskEulerOffset);

        if (objectPoseSpace == ObjectPoseSpace.DeskTracker)
        {
            Quaternion deskOffsetRotInv = Quaternion.Inverse(deskOffsetRot);
            positionInDeskOrigin = deskOffsetRotInv * (rel.pos - deskPositionOffset);
            rotationInDeskOrigin = deskOffsetRotInv * rel.rot;
            return true;
        }

        if (!hasDeskPose)
        {
            positionInDeskOrigin = default;
            rotationInDeskOrigin = default;
            return false;
        }

        Quaternion deskOriginRotInHead = deskPose.rot * deskOffsetRot;
        Vector3 deskOriginPosInHead = deskPose.pos + (deskOriginRotInHead * deskPositionOffset);
        Quaternion invDeskOriginRotInHead = Quaternion.Inverse(deskOriginRotInHead);

        positionInDeskOrigin = invDeskOriginRotInHead * (rel.pos - deskOriginPosInHead);
        rotationInDeskOrigin = invDeskOriginRotInHead * rel.rot;
        return true;
    }

    private static void ApplyPose(Transform target, Vector3 targetPosW, Quaternion targetRotW, float posLerp, float rotSlerp)
    {
        if (posLerp > 0f)
        {
            float k = 1f - Mathf.Exp(-posLerp * Time.deltaTime * 60f);
            target.position = Vector3.Lerp(target.position, targetPosW, k);
        }
        else
        {
            target.position = targetPosW;
        }

        if (rotSlerp > 0f)
        {
            float k = 1f - Mathf.Exp(-rotSlerp * Time.deltaTime * 60f);
            target.rotation = Quaternion.Slerp(target.rotation, targetRotW, k);
        }
        else
        {
            target.rotation = targetRotW;
        }
    }

    private void ValidateConfiguration()
    {
        if (hmdTransform == null)
        {
            Debug.LogWarning("[TrackerToCubeOffsetCalibrator3] hmdTransform is null. DeskOrigin packet updates will be skipped, but object updates can still run in Spatial Anchor mode.");
        }

        if (deskTransform == null)
        {
            Debug.LogWarning("[TrackerToCubeOffsetCalibrator3] deskTransform is null.");
        }

        if (targets == null || targets.Count == 0)
        {
            Debug.LogWarning("[TrackerToCubeOffsetCalibrator3] targets is empty.");
            return;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            TargetEntry target = targets[i];
            if (target == null)
                continue;

            if (target.objTransform == null)
            {
                Debug.LogWarning($"[TrackerToCubeOffsetCalibrator3] targets[{i}].objTransform is null.");
            }
        }
    }

    private void RecvLoop()
    {
        const int relPacketSize = 44;
        const int deskPacketSize = 40;

        while (running)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udp.Receive(ref ep);

                if (data == null || data.Length < 4)
                {
                    if (logBadPackets) Debug.LogWarning("[UDP] packet too small");
                    continue;
                }

                if (IsHeader(data, 'R', 'E', 'L', '0'))
                {
                    if (data.Length < relPacketSize)
                    {
                        if (logBadPackets) Debug.LogWarning("[UDP] REL0 packet too small");
                        continue;
                    }

                    int offset = 4;
                    RelativePose rel = new RelativePose
                    {
                        nowMs = ReadInt64LE(data, ref offset),
                        objId = ReadUInt32LE(data, ref offset),
                        pos = new Vector3(
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset)),
                        rot = new Quaternion(
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset))
                    };

                    lock (latestLock)
                    {
                        latestById[rel.objId] = rel;
                    }

                    if (logReceivedIds)
                    {
                        lock (latestLock)
                        {
                            if (!loggedIds.Contains(rel.objId))
                            {
                                loggedIds.Add(rel.objId);
                                Debug.Log($"[TrackerToCubeOffsetCalibrator3] first REL0 for objId={rel.objId} t={rel.nowMs}");
                            }
                        }
                    }

                    continue;
                }

                if (IsHeader(data, 'D', 'S', 'K', '0'))
                {
                    if (data.Length < deskPacketSize)
                    {
                        if (logBadPackets) Debug.LogWarning("[UDP] DSK0 packet too small");
                        continue;
                    }

                    int offset = 4;
                    DeskPose desk = new DeskPose
                    {
                        nowMs = ReadInt64LE(data, ref offset),
                        pos = new Vector3(
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset)),
                        rot = new Quaternion(
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset),
                            ReadFloatLE(data, ref offset))
                    };

                    lock (latestLock)
                    {
                        latestDeskPose = desk;
                        hasLatestDeskPose = true;
                    }

                    if (logDeskPackets)
                    {
                        Debug.Log($"[TrackerToCubeOffsetCalibrator3] DSK0 t={desk.nowMs} pos={desk.pos}");
                    }

                    continue;
                }

                if (logBadPackets)
                {
                    Debug.LogWarning("[UDP] unknown header");
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
                if (logBadPackets)
                {
                    Debug.LogWarning($"[UDP] exception: {e.Message}");
                }
            }
        }
    }

    private static bool IsHeader(byte[] data, char a, char b, char c, char d)
    {
        return data[0] == (byte)a && data[1] == (byte)b && data[2] == (byte)c && data[3] == (byte)d;
    }

    private static long ReadInt64LE(byte[] b, ref int o)
    {
        long v =
            ((long)b[o + 0]) |
            ((long)b[o + 1] << 8) |
            ((long)b[o + 2] << 16) |
            ((long)b[o + 3] << 24) |
            ((long)b[o + 4] << 32) |
            ((long)b[o + 5] << 40) |
            ((long)b[o + 6] << 48) |
            ((long)b[o + 7] << 56);
        o += 8;
        return v;
    }

    private static uint ReadUInt32LE(byte[] b, ref int o)
    {
        uint v =
            ((uint)b[o + 0]) |
            ((uint)b[o + 1] << 8) |
            ((uint)b[o + 2] << 16) |
            ((uint)b[o + 3] << 24);
        o += 4;
        return v;
    }

    private static float ReadFloatLE(byte[] b, ref int o)
    {
        uint u = ReadUInt32LE(b, ref o);
        byte[] tmp = new byte[4];
        tmp[0] = (byte)(u & 0xFF);
        tmp[1] = (byte)((u >> 8) & 0xFF);
        tmp[2] = (byte)((u >> 16) & 0xFF);
        tmp[3] = (byte)((u >> 24) & 0xFF);
        return BitConverter.ToSingle(tmp, 0);
    }
}
