/*
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDPで受信したTracker Pose(OpenVR 3x4)をUnity座標へ変換し、
/// ArUco cubeCenterWorld のPoseを使って「世界合わせキャリブレーション」を行う。
///
/// キャリブ方式（安定重視）:
///   T_worldFromTracker = aruco0 * inverse(tracker0)
///   cubeWorld          = T_worldFromTracker * trackerPose
///
/// 目的:
/// - 「軸が怪しい/スケールが膨らむ」系の破綻を避け、まず確実に合わせる
/// - ArUcoは worldPose 入力、relativeToHmd は出力としてのみ使う
/// </summary>
public class UdpTrackerToCube_Calibrated : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Header("Packet filter (optional)")]
    public int targetDeviceIndex = -1; // -1: 最初に来たデバイスで固定

    [Header("Coordinate options")]
    [Tooltip("QuestNoLog と同じ: S*M*S (S.m22=-1) を適用する")]
    public bool applyUnityZFlip = true;

    [Header("Tracker -> Cube center offset")]
    public bool enableTrackerOffset = true;

    [Header("Pivot (rotation axis) offset")]
    public bool enablePivotOffset = true;

    [Tooltip("回転中心をずらすベクトル（Cubeのローカル座標系, m）。例: (0,-0.075,0) で軸を7.5cm下げる")]
    public Vector3 pivotOffsetLocal = Vector3.zero;


    [Tooltip("トラッカーのローカル座標で、トラッカー原点→Cube中心へのオフセット(m)")]
    public Vector3 cubeCenterOffsetInTracker = Vector3.zero;

    [Header("ArUco input (WORLD pose)")]
    [Tooltip("ArUco推定のCube中心（必ず world 側を入れる）")]
    public Transform cubeCenterWorld;

    [Header("HMD (relative output base)")]
    public Transform hmdTransform; // CenterEyeAnchor推奨

    [Header("Outputs")]
    [Tooltip("キャリブ後のCube world pose 出力先（見た目に使うならこれ）")]
    public Transform cubeWorldOut;

    [Tooltip("HMD相対のデータ置き場（HMD子で local を更新）。見た目には使わない。")]
    public Transform cubeRelativeOut;

    public bool parentRelativeOutToHmd = true;

    [Header("Calibration")]
    public bool autoCalibrateOnStart = true;

    [Tooltip("キャリブに必要な条件: ArUcoが有効＆UDP受信済みであること")]
    public bool requireArucoAndTracker = true;

    [Tooltip("キャリブをやり直したいときに呼ぶ")]
    public bool calibrateNow = false; // Inspectorトグル用（Updateで検知してfalseに戻す）

    [Header("State thresholds")]
    public float noPacketTimeoutSec = 1.0f;
    public float bindRetryIntervalSec = 1.0f;

    [Header("Visual behavior (no logs)")]
    public Vector3 bindFailedPosition = new Vector3(0f, 0.2f, 0.5f);
    public Vector3 idleBasePosition = new Vector3(0f, 1.2f, 1.5f);
    public float idleBobAmplitude = 0.08f;
    public float idleBobSpeed = 2.0f;
    public float bindFailJitter = 0.03f;

    // --- UDP thread ---
    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;
    private volatile bool _boundOk;

    private readonly object _poseLock = new object();
    private bool _hasPose;
    private Vector3 _trkPosW;
    private Quaternion _trkRotW;
    private float _lastPacketTime; // main thread time

    private float _nextBindTryTime;

    // --- calibration result ---
    private bool _hasWorldFromTracker = false;
    private Vector3 _pWT = Vector3.zero;           // world = qWT * tracker + pWT
    private Quaternion _qWT = Quaternion.identity;  // world rot = qWT * tracker rot

    void Start()
    {
        _running = true;
        _boundOk = false;
        _nextBindTryTime = Time.realtimeSinceStartup;

        if (parentRelativeOutToHmd && cubeRelativeOut != null && hmdTransform != null)
            cubeRelativeOut.SetParent(hmdTransform, worldPositionStays: false);

        if (autoCalibrateOnStart)
            calibrateNow = true;
    }

    void OnDestroy()
    {
        _running = false;
        StopReceiver();
    }

    void Update()
    {
        // bind retry
        if (!_boundOk && Time.realtimeSinceStartup >= _nextBindTryTime)
        {
            TryBindAndStartReceiver();
            _nextBindTryTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, bindRetryIntervalSec);
        }

        // read latest tracker pose
        bool hasPoseLocal;
        Vector3 trkP;
        Quaternion trkQ;
        float lastPkt;
        lock (_poseLock)
        {
            hasPoseLocal = _hasPose;
            trkP = _trkPosW;
            trkQ = _trkRotW;
            lastPkt = _lastPacketTime;
            _hasPose = false;
        }

        // state visuals (optional world out)
        if (!_boundOk)
        {
            Visual_BindFailed();
            return;
        }

        float since = Time.realtimeSinceStartup - lastPkt;
        if (since > noPacketTimeoutSec)
        {
            Visual_BoundButNoPacket();
            return;
        }

        // キャリブ要求が来たら実行（Inspectorトグル対応）
        if (calibrateNow)
        {
            calibrateNow = false;
            TryCalibrate(trkP, trkQ);
        }

        // 追従（キャリブ済みなら tracker -> world に変換）
        if (_hasWorldFromTracker)
        {
            // --- tracker pose (Unity space) ---
            Vector3 trkP2 = trkP;
            Quaternion trkQ2 = trkQ;

            // ★オフセット適用（トラッカーローカル → ワールド相当へ）
            if (enableTrackerOffset)
                trkP2 = trkP2 + (trkQ2 * cubeCenterOffsetInTracker);

            Vector3 cubePosW = _qWT * trkP2 + _pWT;
            Quaternion cubeRotW = _qWT * trkQ2;

            // ★回転軸（ピボット）オフセット：ローカル→ワールドへ回して位置補正
            if (enablePivotOffset)
            {
                // cubePosW を「ピボットの位置」として扱うために、モデル中心をずらす
                // ＝モデル中心位置 = ピボット位置 - (回転後のオフセット)
                cubePosW = cubePosW - (cubeRotW * pivotOffsetLocal);
            }

            ApplyOutputs(cubePosW, cubeRotW);

        }
        else
        {
            // キャリブ未完了：見える挙動としては、ArUcoがあればそれを出す（なければ何もしない）
            if (cubeCenterWorld != null)
                ApplyOutputs(cubeCenterWorld.position, cubeCenterWorld.rotation);
        }
    }

    // ---------------------------
    // Calibration
    // ---------------------------
    private void TryCalibrate(Vector3 trackerPosW, Quaternion trackerRotW)
    {
        if (requireArucoAndTracker)
        {
            if (cubeCenterWorld == null) return;
            if (!_boundOk) return;
            // 「受信済み」の判定：lastPacketTimeが新しいかどうか（Updateの時点でtimeout通過してるので概ねOK）
        }
        if (cubeCenterWorld == null) return;

        // aruco0 (world)
        Vector3 aruP = cubeCenterWorld.position;
        Quaternion aruQ = cubeCenterWorld.rotation;

        // tracker0 (assumed world-like after conversion)
        Vector3 trkP = trackerPosW;
        Quaternion trkQ = trackerRotW;

        // T_worldFromTracker = T_aruco0 * inverse(T_trk0)
        // rot: qWT = aruQ * inv(trkQ)
        Quaternion qWT = aruQ * Quaternion.Inverse(trkQ);

        // pos: pWT = aruP - qWT * trkP
        Vector3 pWT = aruP - (qWT * trkP);

        _qWT = qWT;
        _pWT = pWT;
        _hasWorldFromTracker = true;
    }

    // ---------------------------
    // Output helpers
    // ---------------------------
    private void ApplyOutputs(Vector3 cubePosW, Quaternion cubeRotW)
    {
        if (cubeWorldOut != null)
        {
            cubeWorldOut.position = cubePosW;
            cubeWorldOut.rotation = cubeRotW;
        }

        if (cubeRelativeOut != null && hmdTransform != null)
        {
            if (parentRelativeOutToHmd && cubeRelativeOut.parent != hmdTransform)
                cubeRelativeOut.SetParent(hmdTransform, worldPositionStays: false);

            Quaternion invH = Quaternion.Inverse(hmdTransform.rotation);
            cubeRelativeOut.localPosition = invH * (cubePosW - hmdTransform.position);
            cubeRelativeOut.localRotation = invH * cubeRotW;
        }
    }

    // ---------------------------
    // UDP bind / receive
    // ---------------------------
    private void TryBindAndStartReceiver()
    {
        if (_udp != null) return;

        try
        {
            var family = forceIPv4 ? AddressFamily.InterNetwork : AddressFamily.Unspecified;
            _udp = new UdpClient(family);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));

            _boundOk = true;

            _rxThread = new Thread(ReceiveLoop) { IsBackground = true };
            _rxThread.Start();

            lock (_poseLock) { _lastPacketTime = -999f; }
        }
        catch
        {
            _boundOk = false;
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        try { _udp?.Close(); } catch { }
        _udp = null;

        try { _rxThread?.Join(100); } catch { }
        _rxThread = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running && _udp != null)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);
                if (data == null || data.Length != 64) continue;

                if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
                    continue;

                int off = 4;
                _ = BitConverter.ToInt64(data, off); off += 8;
                uint devIndex = BitConverter.ToUInt32(data, off); off += 4;

                if (targetDeviceIndex >= 0 && devIndex != (uint)targetDeviceIndex)
                    continue;

                float[] f = new float[12];
                for (int i = 0; i < 12; i++)
                {
                    f[i] = BitConverter.ToSingle(data, off);
                    off += 4;
                }

                // 3x4 row-major -> 4x4
                Matrix4x4 M = Matrix4x4.identity;
                M.m00 = f[0]; M.m01 = f[1]; M.m02 = f[2]; M.m03 = f[3];
                M.m10 = f[4]; M.m11 = f[5]; M.m12 = f[6]; M.m13 = f[7];
                M.m20 = f[8]; M.m21 = f[9]; M.m22 = f[10]; M.m23 = f[11];

                if (applyUnityZFlip)
                {
                    Matrix4x4 S = Matrix4x4.identity;
                    S.m22 = -1f;
                    M = S * M * S;
                }

                // ★追加：XとZが逆転する場合の補正（M = F*M*F）
                Matrix4x4 F = Matrix4x4.identity;
                F.m00 = -1f;  // flip X
                F.m22 = -1f;  // flip Z
                M = F * M * F;

                Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
                Vector3 up = new Vector3(M.m01, M.m11, M.m21);
                Vector3 fwd = new Vector3(M.m02, M.m12, M.m22);
                Quaternion rot = Quaternion.LookRotation(fwd, up);

                lock (_poseLock)
                {
                    _trkPosW = pos;
                    _trkRotW = rot;
                    _hasPose = true;
                    _lastPacketTime = float.NaN;

                    if (targetDeviceIndex < 0) targetDeviceIndex = (int)devIndex;
                }
            }
            catch
            {
                // swallow
            }
        }
    }

    void LateUpdate()
    {
        lock (_poseLock)
        {
            if (float.IsNaN(_lastPacketTime))
                _lastPacketTime = Time.realtimeSinceStartup;
        }
    }

    // ---------------------------
    // Visuals (optional)
    // ---------------------------
    private void Visual_BindFailed()
    {
        if (cubeWorldOut == null) return;

        Vector3 jitter = new Vector3(
            (Mathf.PerlinNoise(Time.time * 13f, 1f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 17f, 2f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 19f, 3f) - 0.5f) * 2f
        ) * bindFailJitter;

        cubeWorldOut.position = bindFailedPosition + jitter;
        cubeWorldOut.rotation = Quaternion.Euler(0f, Time.time * 180f, 0f);
    }

    private void Visual_BoundButNoPacket()
    {
        if (cubeWorldOut == null) return;

        float bob = Mathf.Sin(Time.time * idleBobSpeed) * idleBobAmplitude;
        cubeWorldOut.position = idleBasePosition + new Vector3(0f, bob, 0f);
        cubeWorldOut.rotation = Quaternion.Euler(0f, Time.time * 30f, 0f);
    }
}
*/

/*
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDPで受信したTracker Pose(OpenVR 3x4)をUnity座標へ変換し、
/// ArUco cubeCenterWorld のPoseを使って「世界合わせキャリブレーション」を行う。
///
/// キャリブ方式（安定重視）:
///   T_worldFromTracker = aruco0 * inverse(tracker0)
///   cubeWorld          = T_worldFromTracker * trackerPose
///
/// 追加:
/// - トラッカー座標系でオフセット指定（Cube中心オフセット / 回転軸ピボットオフセット）
///   → 追跡点 = trackerPos + trackerRot * (cubeCenterOffsetInTracker - pivotOffsetInTracker)
///
/// 目的:
/// - 「軸が怪しい/スケールが膨らむ」系の破綻を避け、まず確実に合わせる
/// - ArUcoは worldPose 入力、relativeToHmd は出力としてのみ使う
/// </summary>
public class UdpTrackerToCube_Calibrated : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Header("Packet filter (optional)")]
    public int targetDeviceIndex = -1; // -1: 最初に来たデバイスで固定

    [Header("Coordinate options")]
    [Tooltip("QuestNoLog と同じ: S*M*S (S.m22=-1) を適用する")]
    public bool applyUnityZFlip = true;

    [Header("Tracker-space offsets")]
    public bool enableTrackerOffset = true;

    [Tooltip("トラッカーのローカル座標で、トラッカー原点→Cube中心へのオフセット(m)")]
    public Vector3 cubeCenterOffsetInTracker = Vector3.zero;

    [Header("Pivot (rotation axis) offset (TRACKER space)")]
    public bool enablePivotOffset = true;

    [Tooltip("トラッカーのローカル座標で、トラッカー原点→回転軸(ピボット)へのオフセット(m)。例: (0,-0.075,0) で軸を7.5cm下げる")]
    public Vector3 pivotOffsetInTracker = Vector3.zero;

    [Header("ArUco input (WORLD pose)")]
    [Tooltip("ArUco推定のCube中心（必ず world 側を入れる）")]
    public Transform cubeCenterWorld;

    [Header("HMD (relative output base)")]
    public Transform hmdTransform; // CenterEyeAnchor推奨

    [Header("Outputs")]
    [Tooltip("キャリブ後のCube world pose 出力先（見た目に使うならこれ）")]
    public Transform cubeWorldOut;

    [Tooltip("HMD相対のデータ置き場（HMD子で local を更新）。見た目には使わない。")]
    public Transform cubeRelativeOut;

    public bool parentRelativeOutToHmd = true;

    [Header("Calibration")]
    public bool autoCalibrateOnStart = true;

    [Tooltip("キャリブに必要な条件: ArUcoが有効＆UDP受信済みであること")]
    public bool requireArucoAndTracker = true;

    [Tooltip("キャリブをやり直したいときに呼ぶ")]
    public bool calibrateNow = false; // Inspectorトグル用（Updateで検知してfalseに戻す）

    [Header("State thresholds")]
    public float noPacketTimeoutSec = 1.0f;
    public float bindRetryIntervalSec = 1.0f;

    [Header("Visual behavior (no logs)")]
    public Vector3 bindFailedPosition = new Vector3(0f, 0.2f, 0.5f);
    public Vector3 idleBasePosition = new Vector3(0f, 1.2f, 1.5f);
    public float idleBobAmplitude = 0.08f;
    public float idleBobSpeed = 2.0f;
    public float bindFailJitter = 0.03f;

    // --- UDP thread ---
    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;
    private volatile bool _boundOk;

    private readonly object _poseLock = new object();
    private bool _hasPose;
    private Vector3 _trkPosW;
    private Quaternion _trkRotW;
    private float _lastPacketTime; // main thread time

    private float _nextBindTryTime;

    // --- calibration result ---
    private bool _hasWorldFromTracker = false;
    private Vector3 _pWT = Vector3.zero;           // world = qWT * tracker + pWT
    private Quaternion _qWT = Quaternion.identity;  // world rot = qWT * tracker rot

    void Start()
    {
        _running = true;
        _boundOk = false;
        _nextBindTryTime = Time.realtimeSinceStartup;

        if (parentRelativeOutToHmd && cubeRelativeOut != null && hmdTransform != null)
            cubeRelativeOut.SetParent(hmdTransform, worldPositionStays: false);

        if (autoCalibrateOnStart)
            calibrateNow = true;
    }

    void OnDestroy()
    {
        _running = false;
        StopReceiver();
    }

    void Update()
    {
        // bind retry
        if (!_boundOk && Time.realtimeSinceStartup >= _nextBindTryTime)
        {
            TryBindAndStartReceiver();
            _nextBindTryTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, bindRetryIntervalSec);
        }

        // read latest tracker pose
        bool hasPoseLocal;
        Vector3 trkP;
        Quaternion trkQ;
        float lastPkt;
        lock (_poseLock)
        {
            hasPoseLocal = _hasPose;
            trkP = _trkPosW;
            trkQ = _trkRotW;
            lastPkt = _lastPacketTime;
            _hasPose = false;
        }

        // state visuals
        if (!_boundOk)
        {
            Visual_BindFailed();
            return;
        }

        float since = Time.realtimeSinceStartup - lastPkt;
        if (since > noPacketTimeoutSec)
        {
            Visual_BoundButNoPacket();
            return;
        }

        // キャリブ要求が来たら実行（Inspectorトグル対応）
        if (calibrateNow)
        {
            calibrateNow = false;
            // ★キャリブも「追跡点（オフセット適用後）」で行う
            Vector3 trkFollowP;
            Quaternion trkFollowQ;
            ComputeTrackerFollowPose(trkP, trkQ, out trkFollowP, out trkFollowQ);
            TryCalibrate(trkFollowP, trkFollowQ);
        }

        // 追従
        if (_hasWorldFromTracker)
        {
            // ★追跡点（トラッカー基準オフセット適用後）を作る
            Vector3 trkFollowP;
            Quaternion trkFollowQ;
            ComputeTrackerFollowPose(trkP, trkQ, out trkFollowP, out trkFollowQ);

            // --- map into world (ArUco world) ---
            Vector3 cubePosW = _qWT * trkFollowP + _pWT;
            Quaternion cubeRotW = _qWT * trkFollowQ;

            ApplyOutputs(cubePosW, cubeRotW);
        }
        else
        {
            // キャリブ未完了：ArUcoがあればそれを出す
            if (cubeCenterWorld != null)
                ApplyOutputs(cubeCenterWorld.position, cubeCenterWorld.rotation);
        }
    }

    /// <summary>
    /// トラッカー原点から「追いかけたい点（Cube中心/回転軸補正込み）」を作る
    /// 追跡点 = trackerPos + trackerRot * (cubeCenterOffsetInTracker - pivotOffsetInTracker)
    /// 回転は trackerRot をそのまま使う（追跡点が同じ剛体上の点である前提）
    /// </summary>
    private void ComputeTrackerFollowPose(Vector3 trackerPos, Quaternion trackerRot, out Vector3 followPos, out Quaternion followRot)
    {
        Vector3 followOffset = Vector3.zero;

        if (enableTrackerOffset)
            followOffset += cubeCenterOffsetInTracker;

        if (enablePivotOffset)
            followOffset -= pivotOffsetInTracker;

        followPos = trackerPos + (trackerRot * followOffset);
        followRot = trackerRot;
    }

    // ---------------------------
    // Calibration
    // ---------------------------
    private void TryCalibrate(Vector3 trackerFollowPosW, Quaternion trackerFollowRotW)
    {
        if (requireArucoAndTracker)
        {
            if (cubeCenterWorld == null) return;
            if (!_boundOk) return;
        }
        if (cubeCenterWorld == null) return;

        // aruco0 (world)
        Vector3 aruP = cubeCenterWorld.position;
        Quaternion aruQ = cubeCenterWorld.rotation;

        // tracker0 (follow point pose)
        Vector3 trkP = trackerFollowPosW;
        Quaternion trkQ = trackerFollowRotW;

        // T_worldFromTracker = T_aruco0 * inverse(T_trk0)
        Quaternion qWT = aruQ * Quaternion.Inverse(trkQ);
        Vector3 pWT = aruP - (qWT * trkP);

        _qWT = qWT;
        _pWT = pWT;
        _hasWorldFromTracker = true;
    }

    // ---------------------------
    // Output helpers
    // ---------------------------
    private void ApplyOutputs(Vector3 cubePosW, Quaternion cubeRotW)
    {
        if (cubeWorldOut != null)
        {
            cubeWorldOut.position = cubePosW;
            cubeWorldOut.rotation = cubeRotW;
        }

        if (cubeRelativeOut != null && hmdTransform != null)
        {
            if (parentRelativeOutToHmd && cubeRelativeOut.parent != hmdTransform)
                cubeRelativeOut.SetParent(hmdTransform, worldPositionStays: false);

            Quaternion invH = Quaternion.Inverse(hmdTransform.rotation);
            cubeRelativeOut.localPosition = invH * (cubePosW - hmdTransform.position);
            cubeRelativeOut.localRotation = invH * cubeRotW;
        }
    }

    // ---------------------------
    // UDP bind / receive
    // ---------------------------
    private void TryBindAndStartReceiver()
    {
        if (_udp != null) return;

        try
        {
            var family = forceIPv4 ? AddressFamily.InterNetwork : AddressFamily.Unspecified;
            _udp = new UdpClient(family);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));

            _boundOk = true;

            _rxThread = new Thread(ReceiveLoop) { IsBackground = true };
            _rxThread.Start();

            lock (_poseLock) { _lastPacketTime = -999f; }
        }
        catch
        {
            _boundOk = false;
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        try { _udp?.Close(); } catch { }
        _udp = null;

        try { _rxThread?.Join(100); } catch { }
        _rxThread = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running && _udp != null)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);
                if (data == null || data.Length != 64) continue;

                if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
                    continue;

                int off = 4;
                _ = BitConverter.ToInt64(data, off); off += 8;
                uint devIndex = BitConverter.ToUInt32(data, off); off += 4;

                if (targetDeviceIndex >= 0 && devIndex != (uint)targetDeviceIndex)
                    continue;

                float[] f = new float[12];
                for (int i = 0; i < 12; i++)
                {
                    f[i] = BitConverter.ToSingle(data, off);
                    off += 4;
                }

                // 3x4 row-major -> 4x4
                Matrix4x4 M = Matrix4x4.identity;
                M.m00 = f[0]; M.m01 = f[1]; M.m02 = f[2]; M.m03 = f[3];
                M.m10 = f[4]; M.m11 = f[5]; M.m12 = f[6]; M.m13 = f[7];
                M.m20 = f[8]; M.m21 = f[9]; M.m22 = f[10]; M.m23 = f[11];

                if (applyUnityZFlip)
                {
                    Matrix4x4 S = Matrix4x4.identity;
                    S.m22 = -1f;
                    M = S * M * S;
                }

                // ★追加：XとZが逆転する場合の補正（M = F*M*F）
                Matrix4x4 F = Matrix4x4.identity;
                F.m00 = -1f;  // flip X
                F.m22 = -1f;  // flip Z
                M = F * M * F;

                Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
                Vector3 up = new Vector3(M.m01, M.m11, M.m21);
                Vector3 fwd = new Vector3(M.m02, M.m12, M.m22);
                Quaternion rot = Quaternion.LookRotation(fwd, up);

                lock (_poseLock)
                {
                    _trkPosW = pos;
                    _trkRotW = rot;
                    _hasPose = true;
                    _lastPacketTime = float.NaN;

                    if (targetDeviceIndex < 0) targetDeviceIndex = (int)devIndex;
                }
            }
            catch
            {
                // swallow
            }
        }
    }

    void LateUpdate()
    {
        lock (_poseLock)
        {
            if (float.IsNaN(_lastPacketTime))
                _lastPacketTime = Time.realtimeSinceStartup;
        }
    }

    // ---------------------------
    // Visuals (optional)
    // ---------------------------
    private void Visual_BindFailed()
    {
        if (cubeWorldOut == null) return;

        Vector3 jitter = new Vector3(
            (Mathf.PerlinNoise(Time.time * 13f, 1f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 17f, 2f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 19f, 3f) - 0.5f) * 2f
        ) * bindFailJitter;

        cubeWorldOut.position = bindFailedPosition + jitter;
        cubeWorldOut.rotation = Quaternion.Euler(0f, Time.time * 180f, 0f);
    }

    private void Visual_BoundButNoPacket()
    {
        if (cubeWorldOut == null) return;

        float bob = Mathf.Sin(Time.time * idleBobSpeed) * idleBobAmplitude;
        cubeWorldOut.position = idleBasePosition + new Vector3(0f, bob, 0f);
        cubeWorldOut.rotation = Quaternion.Euler(0f, Time.time * 30f, 0f);
    }
}
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDPで受信したTracker Pose(OpenVR 3x4)をUnity座標へ変換し、
/// ArUco cubeCenterWorld のPoseを使って「世界合わせキャリブレーション」を行う。
///
/// キャリブ方式（安定重視）:
///   T_worldFromTracker = aruco0 * inverse(tracker0)
///   cubeWorld          = T_worldFromTracker * trackerPose
///
/// 追加:
/// - トラッカー座標系でオフセット指定（Cube中心オフセット / 回転軸ピボットオフセット）
///   → 追跡点 = trackerPos + trackerRot * (cubeCenterOffsetInTracker - pivotOffsetInTracker)
///
/// ★追加:
/// - ワールド座標系での出力オフセット（位置/回転）
///   → cubeWorld に対して「回転→平行移動」を合成して最終出力
///
/// 目的:
/// - 「軸が怪しい/スケールが膨らむ」系の破綻を避け、まず確実に合わせる
/// - ArUcoは worldPose 入力、relativeToHmd は出力としてのみ使う
/// </summary>
public class UdpTrackerToCube_Calibrated : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Header("Packet filter (optional)")]
    public int targetDeviceIndex = -1; // -1: 最初に来たデバイスで固定

    [Header("Coordinate options")]
    [Tooltip("QuestNoLog と同じ: S*M*S (S.m22=-1) を適用する")]
    public bool applyUnityZFlip = true;

    [Header("Tracker-space offsets")]
    public bool enableTrackerOffset = true;

    [Tooltip("トラッカーのローカル座標で、トラッカー原点→Cube中心へのオフセット(m)")]
    public Vector3 cubeCenterOffsetInTracker = Vector3.zero;

    [Header("Pivot (rotation axis) offset (TRACKER space)")]
    public bool enablePivotOffset = true;

    [Tooltip("トラッカーのローカル座標で、トラッカー原点→回転軸(ピボット)へのオフセット(m)。例: (0,-0.075,0) で軸を7.5cm下げる")]
    public Vector3 pivotOffsetInTracker = Vector3.zero;

    // ★追加：ワールド座標系での出力オフセット
    [Header("World-space output offsets (★NEW)")]
    public bool enableWorldOffset = false;

    [Tooltip("ワールド座標系で、最終出力位置に足す平行移動(m)")]
    public Vector3 worldPositionOffset = Vector3.zero;

    [Tooltip("ワールド座標系で、最終出力回転に合成する回転(度)")]
    public Vector3 worldRotationOffsetEuler = Vector3.zero;

    [Tooltip("回転オフセットを先に適用し、その回転後に位置オフセットを足す（推奨）")]
    public bool applyWorldRotThenPos = true;

    [Header("ArUco input (WORLD pose)")]
    [Tooltip("ArUco推定のCube中心（必ず world 側を入れる）")]
    public Transform cubeCenterWorld;

    [Header("HMD (relative output base)")]
    public Transform hmdTransform; // CenterEyeAnchor推奨

    [Header("Outputs")]
    [Tooltip("キャリブ後のCube world pose 出力先（見た目に使うならこれ）")]
    public Transform cubeWorldOut;

    [Tooltip("HMD相対のデータ置き場（HMD子で local を更新）。見た目には使わない。")]
    public Transform cubeRelativeOut;

    public bool parentRelativeOutToHmd = true;

    [Header("Calibration")]
    public bool autoCalibrateOnStart = true;

    [Tooltip("キャリブに必要な条件: ArUcoが有効＆UDP受信済みであること")]
    public bool requireArucoAndTracker = true;

    [Tooltip("キャリブをやり直したいときに呼ぶ")]
    public bool calibrateNow = false; // Inspectorトグル用（Updateで検知してfalseに戻す）

    [Header("State thresholds")]
    public float noPacketTimeoutSec = 1.0f;
    public float bindRetryIntervalSec = 1.0f;

    [Header("Visual behavior (no logs)")]
    public Vector3 bindFailedPosition = new Vector3(0f, 0.2f, 0.5f);
    public Vector3 idleBasePosition = new Vector3(0f, 1.2f, 1.5f);
    public float idleBobAmplitude = 0.08f;
    public float idleBobSpeed = 2.0f;
    public float bindFailJitter = 0.03f;

    // --- UDP thread ---
    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;
    private volatile bool _boundOk;

    private readonly object _poseLock = new object();
    private bool _hasPose;
    private Vector3 _trkPosW;
    private Quaternion _trkRotW;
    private float _lastPacketTime; // main thread time

    private float _nextBindTryTime;

    // --- calibration result ---
    private bool _hasWorldFromTracker = false;
    private Vector3 _pWT = Vector3.zero;           // world = qWT * tracker + pWT
    private Quaternion _qWT = Quaternion.identity;  // world rot = qWT * tracker rot

    void Start()
    {
        _running = true;
        _boundOk = false;
        _nextBindTryTime = Time.realtimeSinceStartup;

        if (parentRelativeOutToHmd && cubeRelativeOut != null && hmdTransform != null)
            cubeRelativeOut.SetParent(hmdTransform, worldPositionStays: false);

        if (autoCalibrateOnStart)
            calibrateNow = true;
    }

    void OnDestroy()
    {
        _running = false;
        StopReceiver();
    }

    void Update()
    {
        // bind retry
        if (!_boundOk && Time.realtimeSinceStartup >= _nextBindTryTime)
        {
            TryBindAndStartReceiver();
            _nextBindTryTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, bindRetryIntervalSec);
        }

        // read latest tracker pose
        bool hasPoseLocal;
        Vector3 trkP;
        Quaternion trkQ;
        float lastPkt;
        lock (_poseLock)
        {
            hasPoseLocal = _hasPose;
            trkP = _trkPosW;
            trkQ = _trkRotW;
            lastPkt = _lastPacketTime;
            _hasPose = false;
        }

        // state visuals
        if (!_boundOk)
        {
            Visual_BindFailed();
            return;
        }

        float since = Time.realtimeSinceStartup - lastPkt;
        if (since > noPacketTimeoutSec)
        {
            Visual_BoundButNoPacket();
            return;
        }

        // キャリブ要求が来たら実行（Inspectorトグル対応）
        if (calibrateNow)
        {
            calibrateNow = false;
            // ★キャリブも「追跡点（オフセット適用後）」で行う
            Vector3 trkFollowP;
            Quaternion trkFollowQ;
            ComputeTrackerFollowPose(trkP, trkQ, out trkFollowP, out trkFollowQ);
            TryCalibrate(trkFollowP, trkFollowQ);
        }

        // 追従
        if (_hasWorldFromTracker)
        {
            // ★追跡点（トラッカー基準オフセット適用後）を作る
            Vector3 trkFollowP;
            Quaternion trkFollowQ;
            ComputeTrackerFollowPose(trkP, trkQ, out trkFollowP, out trkFollowQ);

            // --- map into world (ArUco world) ---
            Vector3 cubePosW = _qWT * trkFollowP + _pWT;
            Quaternion cubeRotW = _qWT * trkFollowQ;

            // ★追加：ワールド座標系での出力オフセットを適用
            ApplyWorldOffset(ref cubePosW, ref cubeRotW);

            ApplyOutputs(cubePosW, cubeRotW);
        }
        else
        {
            // キャリブ未完了：ArUcoがあればそれを出す
            if (cubeCenterWorld != null)
            {
                Vector3 p = cubeCenterWorld.position;
                Quaternion q = cubeCenterWorld.rotation;

                // ★追加：ワールド座標系での出力オフセットを適用
                ApplyWorldOffset(ref p, ref q);

                ApplyOutputs(p, q);
            }
        }
    }

    /// <summary>
    /// トラッカー原点から「追いかけたい点（Cube中心/回転軸補正込み）」を作る
    /// 追跡点 = trackerPos + trackerRot * (cubeCenterOffsetInTracker - pivotOffsetInTracker)
    /// 回転は trackerRot をそのまま使う（追跡点が同じ剛体上の点である前提）
    /// </summary>
    private void ComputeTrackerFollowPose(Vector3 trackerPos, Quaternion trackerRot, out Vector3 followPos, out Quaternion followRot)
    {
        Vector3 followOffset = Vector3.zero;

        if (enableTrackerOffset)
            followOffset += cubeCenterOffsetInTracker;

        if (enablePivotOffset)
            followOffset -= pivotOffsetInTracker;

        followPos = trackerPos + (trackerRot * followOffset);
        followRot = trackerRot;
    }

    // ★追加：ワールド座標系オフセット適用
    private void ApplyWorldOffset(ref Vector3 posW, ref Quaternion rotW)
    {
        if (!enableWorldOffset) return;

        Quaternion qOff = Quaternion.Euler(worldRotationOffsetEuler);

        if (applyWorldRotThenPos)
        {
            // world回転を合成 → そのあと平行移動を足す
            rotW = qOff * rotW;
            posW = (qOff * posW) + worldPositionOffset;
        }
        else
        {
            // 平行移動を足す → そのあと回転を合成（必要なら使う）
            posW = posW + worldPositionOffset;
            rotW = qOff * rotW;
        }
    }

    // ---------------------------
    // Calibration
    // ---------------------------
    private void TryCalibrate(Vector3 trackerFollowPosW, Quaternion trackerFollowRotW)
    {
        if (requireArucoAndTracker)
        {
            if (cubeCenterWorld == null) return;
            if (!_boundOk) return;
        }
        if (cubeCenterWorld == null) return;

        // aruco0 (world)
        Vector3 aruP = cubeCenterWorld.position;
        Quaternion aruQ = cubeCenterWorld.rotation;

        // tracker0 (follow point pose)
        Vector3 trkP = trackerFollowPosW;
        Quaternion trkQ = trackerFollowRotW;

        // T_worldFromTracker = T_aruco0 * inverse(T_trk0)
        Quaternion qWT = aruQ * Quaternion.Inverse(trkQ);
        Vector3 pWT = aruP - (qWT * trkP);

        _qWT = qWT;
        _pWT = pWT;
        _hasWorldFromTracker = true;
    }

    // ---------------------------
    // Output helpers
    // ---------------------------
    private void ApplyOutputs(Vector3 cubePosW, Quaternion cubeRotW)
    {
        if (cubeWorldOut != null)
        {
            cubeWorldOut.position = cubePosW;
            cubeWorldOut.rotation = cubeRotW;
        }

        if (cubeRelativeOut != null && hmdTransform != null)
        {
            if (parentRelativeOutToHmd && cubeRelativeOut.parent != hmdTransform)
                cubeRelativeOut.SetParent(hmdTransform, worldPositionStays: false);

            Quaternion invH = Quaternion.Inverse(hmdTransform.rotation);
            cubeRelativeOut.localPosition = invH * (cubePosW - hmdTransform.position);
            cubeRelativeOut.localRotation = invH * cubeRotW;
        }
    }

    // ---------------------------
    // UDP bind / receive
    // ---------------------------
    private void TryBindAndStartReceiver()
    {
        if (_udp != null) return;

        try
        {
            var family = forceIPv4 ? AddressFamily.InterNetwork : AddressFamily.Unspecified;
            _udp = new UdpClient(family);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));

            _boundOk = true;

            _rxThread = new Thread(ReceiveLoop) { IsBackground = true };
            _rxThread.Start();

            lock (_poseLock) { _lastPacketTime = -999f; }
        }
        catch
        {
            _boundOk = false;
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        try { _udp?.Close(); } catch { }
        _udp = null;

        try { _rxThread?.Join(100); } catch { }
        _rxThread = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running && _udp != null)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);
                if (data == null || data.Length != 64) continue;

                if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
                    continue;

                int off = 4;
                _ = BitConverter.ToInt64(data, off); off += 8;
                uint devIndex = BitConverter.ToUInt32(data, off); off += 4;

                if (targetDeviceIndex >= 0 && devIndex != (uint)targetDeviceIndex)
                    continue;

                float[] f = new float[12];
                for (int i = 0; i < 12; i++)
                {
                    f[i] = BitConverter.ToSingle(data, off);
                    off += 4;
                }

                // 3x4 row-major -> 4x4
                Matrix4x4 M = Matrix4x4.identity;
                M.m00 = f[0]; M.m01 = f[1]; M.m02 = f[2]; M.m03 = f[3];
                M.m10 = f[4]; M.m11 = f[5]; M.m12 = f[6]; M.m13 = f[7];
                M.m20 = f[8]; M.m21 = f[9]; M.m22 = f[10]; M.m23 = f[11];

                if (applyUnityZFlip)
                {
                    Matrix4x4 S = Matrix4x4.identity;
                    S.m22 = -1f;
                    M = S * M * S;
                }

                // ★追加：XとZが逆転する場合の補正（M = F*M*F）
                Matrix4x4 F = Matrix4x4.identity;
                F.m00 = -1f;  // flip X
                F.m22 = -1f;  // flip Z
                M = F * M * F;

                Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
                Vector3 up = new Vector3(M.m01, M.m11, M.m21);
                Vector3 fwd = new Vector3(M.m02, M.m12, M.m22);
                Quaternion rot = Quaternion.LookRotation(fwd, up);

                lock (_poseLock)
                {
                    _trkPosW = pos;
                    _trkRotW = rot;
                    _hasPose = true;
                    _lastPacketTime = float.NaN;

                    if (targetDeviceIndex < 0) targetDeviceIndex = (int)devIndex;
                }
            }
            catch
            {
                // swallow
            }
        }
    }

    void LateUpdate()
    {
        lock (_poseLock)
        {
            if (float.IsNaN(_lastPacketTime))
                _lastPacketTime = Time.realtimeSinceStartup;
        }
    }

    // ---------------------------
    // Visuals (optional)
    // ---------------------------
    private void Visual_BindFailed()
    {
        if (cubeWorldOut == null) return;

        Vector3 jitter = new Vector3(
            (Mathf.PerlinNoise(Time.time * 13f, 1f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 17f, 2f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 19f, 3f) - 0.5f) * 2f
        ) * bindFailJitter;

        cubeWorldOut.position = bindFailedPosition + jitter;
        cubeWorldOut.rotation = Quaternion.Euler(0f, Time.time * 180f, 0f);
    }

    private void Visual_BoundButNoPacket()
    {
        if (cubeWorldOut == null) return;

        float bob = Mathf.Sin(Time.time * idleBobSpeed) * idleBobAmplitude;
        cubeWorldOut.position = idleBasePosition + new Vector3(0f, bob, 0f);
        cubeWorldOut.rotation = Quaternion.Euler(0f, Time.time * 30f, 0f);
    }
}
