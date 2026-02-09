/*
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class TrackerToCubeOffsetCalibrator : MonoBehaviour
{
    [Header("UDP (Tracker input)")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Tooltip("この送信元IP(PC)からのUDPのみ受理。空なら全て受理。")]
    public string acceptOnlyFromIp = "192.168.0.46";

    [Header("ArUco input (Pose source)")]
    [Tooltip("ArUcoで認識されたCubeのTransform（※すでにUnity座標系に変換済み）")]
    public Transform arucoCube;

    [Header("Output (Corrected pose)")]
    public Transform targetCube;

    [Header("Calibration")]
    public bool autoStartCalibration = true;
    public float calibrationDurationSec = 5.0f;

    public bool gateByStability = false;
    public float maxPosDeltaForSample = 0.03f;          // m
    public float maxAngleDeltaForSampleDeg = 3.0f;      // deg

    [Header("Tracker selection")]
    public int targetDeviceIndex = -1;

    [Header("OpenVR -> Unity (ONLY)")]
    [Tooltip("OpenVR(右手系寄り)→Unity(左手系)の基底変換。基本はZ反転のみ。")]
    public bool openvrToUnity_ZFlip = true;

    [Header("Visual state (no logs)")]
    public Vector3 bindFailedPosition = new Vector3(0f, 0.2f, 0.5f);
    public Vector3 idleBasePosition = new Vector3(0f, 1.2f, 1.5f);
    public float idleBobAmplitude = 0.08f;
    public float idleBobSpeed = 2.0f;
    public float bindFailJitter = 0.03f;

    public float noPacketTimeoutSec = 1.0f;
    public float bindRetryIntervalSec = 1.0f;

    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;
    private volatile bool _boundOk;

    private readonly object _trkLock = new object();
    private bool _trkHasNew;
    private Vector3 _trkPos;
    private Quaternion _trkRot;
    private uint _trkDev;
    private float _lastPacketTimeMain = -999f;

    private enum CalibState { NotStarted, Running, Done }
    private CalibState _calibState = CalibState.NotStarted;
    private float _calibEndTime = 0f;

    private Quaternion _qOffMean = Quaternion.identity;
    private Vector3 _pOffMean = Vector3.zero;
    private bool _hasOffset = false;

    private readonly List<Quaternion> _qOffSamples = new List<Quaternion>(512);
    private readonly List<Vector3> _pOffSamples = new List<Vector3>(512);

    private Vector3 _prevAruPos;
    private Quaternion _prevAruRot;
    private bool _hasPrevAru = false;

    private float _nextBindTryTime = 0f;

    void Start()
    {
        _running = true;
        _boundOk = false;
        _nextBindTryTime = Time.realtimeSinceStartup;

        if (autoStartCalibration)
            BeginCalibration();
    }

    void OnDestroy()
    {
        _running = false;
        StopReceiver();
    }

    void Update()
    {
        if (!_boundOk && Time.realtimeSinceStartup >= _nextBindTryTime)
        {
            TryBindAndStartReceiver();
            _nextBindTryTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, bindRetryIntervalSec);
        }

        if (targetCube == null) return;

        bool hasNew;
        Vector3 trkPos;
        Quaternion trkRot;

        lock (_trkLock)
        {
            hasNew = _trkHasNew;
            trkPos = _trkPos;
            trkRot = _trkRot;
            _trkHasNew = false;
        }

        if (hasNew) _lastPacketTimeMain = Time.realtimeSinceStartup;

        if (!_boundOk)
        {
            Visual_BindFailed();
            return;
        }

        float sincePkt = Time.realtimeSinceStartup - _lastPacketTimeMain;
        if (sincePkt > noPacketTimeoutSec)
        {
            Visual_BoundButNoPacket();
            return;
        }

        if (_calibState == CalibState.Running)
        {
            Visual_Calibrating();

            if (arucoCube != null)
            {
                // ArUcoはすでにUnity座標なので、そのまま使用
                TrySampleOffset(trkPos, trkRot, arucoCube.position, arucoCube.rotation);

                if (Time.realtimeSinceStartup >= _calibEndTime)
                    FinalizeCalibration();
            }
            return;
        }

        if (_hasOffset)
        {
            var posCorr = _qOffMean * trkPos + _pOffMean;
            var rotCorr = _qOffMean * trkRot;

            targetCube.position = posCorr;
            targetCube.rotation = rotCorr;
        }
        else
        {
            targetCube.position = trkPos;
            targetCube.rotation = trkRot;
        }
    }

    public void BeginCalibration()
    {
        _qOffSamples.Clear();
        _pOffSamples.Clear();
        _hasPrevAru = false;

        _calibState = CalibState.Running;
        _calibEndTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, calibrationDurationSec);
    }

    public void ClearCalibration()
    {
        _hasOffset = false;
        _qOffMean = Quaternion.identity;
        _pOffMean = Vector3.zero;
        _calibState = CalibState.NotStarted;
    }

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

            _lastPacketTimeMain = -999f;
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

        IPAddress acceptIp = null;
        bool useIpFilter = !string.IsNullOrWhiteSpace(acceptOnlyFromIp) && IPAddress.TryParse(acceptOnlyFromIp, out acceptIp);

        while (_running && _udp != null)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);

                if (useIpFilter && !ep.Address.Equals(acceptIp))
                    continue;

                if (data == null || data.Length != 64)
                    continue;

                if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
                    continue;

                int off = 4;
                off += 8;
                uint devIndex = BitConverter.ToUInt32(data, off);
                off += 4;

                if (targetDeviceIndex >= 0 && devIndex != (uint)targetDeviceIndex)
                    continue;

                float[] f = new float[12];
                for (int i = 0; i < 12; i++)
                {
                    f[i] = BitConverter.ToSingle(data, off);
                    off += 4;
                }

                // OpenVR 3x4 -> Matrix4x4
                Matrix4x4 M = Matrix4x4.identity;
                M.m00 = f[0]; M.m01 = f[1]; M.m02 = f[2]; M.m03 = f[3];
                M.m10 = f[4]; M.m11 = f[5]; M.m12 = f[6]; M.m13 = f[7];
                M.m20 = f[8]; M.m21 = f[9]; M.m22 = f[10]; M.m23 = f[11];

                // --- OpenVR -> Unity basis change (ONLY) ---
                if (openvrToUnity_ZFlip)
                {
                    Matrix4x4 Sz = Matrix4x4.identity;
                    Sz.m22 = -1f;
                    M = Sz * M * Sz;
                }

                Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
                Vector3 up = new Vector3(M.m01, M.m11, M.m21);
                Vector3 fwd = new Vector3(M.m02, M.m12, M.m22);
                Quaternion rot = Quaternion.LookRotation(fwd, up);

                rot = Quaternion.Inverse(rot);

                lock (_trkLock)
                {
                    _trkPos = pos;
                    _trkRot = rot;
                    _trkDev = devIndex;
                    _trkHasNew = true;

                    if (targetDeviceIndex < 0)
                        targetDeviceIndex = (int)devIndex;
                }
            }
            catch
            {
            }
        }
    }

    private void TrySampleOffset(Vector3 trkPos, Quaternion trkRot, Vector3 aruPos, Quaternion aruRot)
    {
        if (gateByStability)
        {
            if (_hasPrevAru)
            {
                float dp = Vector3.Distance(aruPos, _prevAruPos);
                float da = Quaternion.Angle(aruRot, _prevAruRot);
                if (dp > maxPosDeltaForSample || da > maxAngleDeltaForSampleDeg)
                {
                    _prevAruPos = aruPos;
                    _prevAruRot = aruRot;
                    return;
                }
            }
            _prevAruPos = aruPos;
            _prevAruRot = aruRot;
            _hasPrevAru = true;
        }

        Quaternion qOff = aruRot * Quaternion.Inverse(trkRot);
        Vector3 pOff = aruPos - (qOff * trkPos);

        _qOffSamples.Add(qOff);
        _pOffSamples.Add(pOff);
    }

    private void FinalizeCalibration()
    {
        if (_qOffSamples.Count < 5)
        {
            _hasOffset = false;
            _calibState = CalibState.Done;
            return;
        }

        _qOffMean = MeanQuaternion(_qOffSamples);
        _pOffMean = MeanVector3(_pOffSamples);

        _hasOffset = true;
        _calibState = CalibState.Done;
    }

    private static Vector3 MeanVector3(List<Vector3> vs)
    {
        Vector3 acc = Vector3.zero;
        for (int i = 0; i < vs.Count; i++) acc += vs[i];
        return acc / Mathf.Max(1, vs.Count);
    }

    private static Quaternion MeanQuaternion(List<Quaternion> qs)
    {
        if (qs == null || qs.Count == 0) return Quaternion.identity;

        Quaternion refQ = qs[0];
        Vector4 acc = Vector4.zero;

        for (int i = 0; i < qs.Count; i++)
        {
            Quaternion q = qs[i];
            if (Quaternion.Dot(q, refQ) < 0f)
            {
                q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w;
            }
            acc.x += q.x;
            acc.y += q.y;
            acc.z += q.z;
            acc.w += q.w;
        }

        float inv = 1f / qs.Count;
        Quaternion m = new Quaternion(acc.x * inv, acc.y * inv, acc.z * inv, acc.w * inv);
        return Quaternion.Normalize(m);
    }

    private void Visual_BindFailed()
    {
        Vector3 jitter = new Vector3(
            (Mathf.PerlinNoise(Time.time * 13f, 1f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 17f, 2f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 19f, 3f) - 0.5f) * 2f
        ) * bindFailJitter;

        targetCube.position = bindFailedPosition + jitter;
        targetCube.rotation = Quaternion.Euler(0f, Time.time * 180f, 0f);
    }

    private void Visual_BoundButNoPacket()
    {
        float bob = Mathf.Sin(Time.time * idleBobSpeed) * idleBobAmplitude;
        targetCube.position = idleBasePosition + new Vector3(0f, bob, 0f);
        targetCube.rotation = Quaternion.Euler(0f, Time.time * 30f, 0f);
    }

    private void Visual_Calibrating()
    {
        float bob = Mathf.Sin(Time.time * (idleBobSpeed * 1.3f)) * (idleBobAmplitude * 0.7f);
        targetCube.position = idleBasePosition + new Vector3(0f, bob, 0f);
        targetCube.rotation = Quaternion.Euler(Time.time * 90f, Time.time * 120f, 0f);
    }
}
*/

/*
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class TrackerToCubeOffsetCalibrator : MonoBehaviour
{
    [Header("UDP (Tracker input)")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Tooltip("この送信元IP(PC)からのUDPのみ受理。空なら全て受理。")]
    public string acceptOnlyFromIp = "192.168.0.46";

    [Header("HMD frame")]
    [Tooltip("HMD座標系の原点（例：CenterEyeAnchor / MainCamera）")]
    public Transform hmdOrigin;

    [Header("ArUco input (Pose source)")]
    [Tooltip("ArUcoで認識されたCubeのTransform（ワールドにいてもOK。hmdOrigin基準に落として使う）")]
    public Transform arucoCube;

    [Header("Output (Estimated cube pose in HMD frame)")]
    [Tooltip("推定Cube。hmdOrigin の子にして localPose 更新が推奨。")]
    public Transform targetCube;

    [Header("Calibration")]
    public bool autoStartCalibration = true;
    public float calibrationDurationSec = 5.0f;

    public bool gateByStability = true;
    public float maxPosDeltaForSample = 0.03f;          // m
    public float maxAngleDeltaForSampleDeg = 3.0f;      // deg

    [Header("Tracker selection")]
    public int targetDeviceIndex = -1;

    [Header("OpenVR -> Unity axis conversion")]
    [Tooltip("OpenVR->Unityの軸整合を行う（Z反転＋forward補正）。通常ON。")]
    public bool openvrToUnity = true;

    [Tooltip("OpenVR 3x4 の軸取り出し。合わない場合のみOFFで試す。")]
    public bool treatMatrixAsColumnMajorAxes = true;

    [Header("Visual state (no logs)")]
    public Vector3 bindFailedPosition = new Vector3(0f, 0.2f, 0.5f);
    public Vector3 idleBasePosition = new Vector3(0f, 1.2f, 1.5f);
    public float idleBobAmplitude = 0.08f;
    public float idleBobSpeed = 2.0f;
    public float bindFailJitter = 0.03f;

    public float noPacketTimeoutSec = 1.0f;
    public float bindRetryIntervalSec = 1.0f;

    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;
    private volatile bool _boundOk;

    private readonly object _trkLock = new object();
    private bool _trkHasNew;
    private Vector3 _trkPosUnity;        // Unity軸整合後（ただし原点はOpenVR universe）
    private Quaternion _trkRotUnity;
    private uint _trkDev;
    private float _lastPacketTimeMain = -999f;

    private enum CalibState { NotStarted, Running, Done }
    private CalibState _calibState = CalibState.NotStarted;
    private float _calibEndTime = 0f;

    // ここで推定するのは「Tracker -> HMD」の剛体変換
    private Quaternion _qTrkToHmd = Quaternion.identity;
    private Vector3 _pTrkToHmd = Vector3.zero;
    private bool _hasMapping = false;

    private readonly List<Quaternion> _qSamples = new List<Quaternion>(512);
    private readonly List<Vector3> _pSamples = new List<Vector3>(512);

    private Vector3 _prevAruPosH;
    private Quaternion _prevAruRotH;
    private bool _hasPrevAru = false;

    private float _nextBindTryTime = 0f;

    void Start()
    {
        _running = true;
        _boundOk = false;
        _nextBindTryTime = Time.realtimeSinceStartup;

        if (autoStartCalibration)
            BeginCalibration();
    }

    void OnDestroy()
    {
        _running = false;
        StopReceiver();
    }

    void Update()
    {
        if (!_boundOk && Time.realtimeSinceStartup >= _nextBindTryTime)
        {
            TryBindAndStartReceiver();
            _nextBindTryTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, bindRetryIntervalSec);
        }

        if (targetCube == null) return;

        bool hasNew;
        Vector3 trkPosU;
        Quaternion trkRotU;

        lock (_trkLock)
        {
            hasNew = _trkHasNew;
            trkPosU = _trkPosUnity;
            trkRotU = _trkRotUnity;
            _trkHasNew = false;
        }

        if (hasNew) _lastPacketTimeMain = Time.realtimeSinceStartup;

        if (!_boundOk)
        {
            Visual_BindFailed();
            return;
        }

        float sincePkt = Time.realtimeSinceStartup - _lastPacketTimeMain;
        if (sincePkt > noPacketTimeoutSec)
        {
            Visual_BoundButNoPacket();
            return;
        }

        // ArUco pose in HMD frame (pos/rot)
        bool hasAru = (arucoCube != null && hmdOrigin != null);
        Pose aruH = default;
        if (hasAru)
        {
            aruH = GetPoseInHmdFrame(arucoCube);
        }

        // ---- Calibration ----
        if (_calibState == CalibState.Running)
        {
            Visual_Calibrating();

            if (hasAru)
            {
                if (gateByStability)
                {
                    if (_hasPrevAru)
                    {
                        float dp = Vector3.Distance(aruH.position, _prevAruPosH);
                        float da = Quaternion.Angle(aruH.rotation, _prevAruRotH);
                        if (dp > maxPosDeltaForSample || da > maxAngleDeltaForSampleDeg)
                        {
                            _prevAruPosH = aruH.position;
                            _prevAruRotH = aruH.rotation;
                            return;
                        }
                    }
                    _prevAruPosH = aruH.position;
                    _prevAruRotH = aruH.rotation;
                    _hasPrevAru = true;
                }

                // 推定: cube(HMD) = T * tracker(UnityAxes)
                Quaternion q = aruH.rotation * Quaternion.Inverse(trkRotU);
                Vector3 p = aruH.position - (q * trkPosU);

                _qSamples.Add(q);
                _pSamples.Add(p);
            }

            if (Time.realtimeSinceStartup >= _calibEndTime)
                FinalizeCalibration();

            return;
        }

        // ---- Apply mapping (always output something) ----
        if (_hasMapping)
        {
            Vector3 cubePosH = _qTrkToHmd * trkPosU + _pTrkToHmd;
            Quaternion cubeRotH = _qTrkToHmd * trkRotU;

            SetPoseInHmdFrame(targetCube, new Pose(cubePosH, cubeRotH));
        }
        else
        {
            // マッピング未確定でも「止まらない」ように、暫定出力:
            // ArUcoがあるならArUcoをそのまま出す / 無ければ原点に置く等
            if (hasAru)
                SetPoseInHmdFrame(targetCube, aruH);
            else
                targetCube.localPosition = Vector3.zero;
        }
    }

    public void BeginCalibration()
    {
        _qSamples.Clear();
        _pSamples.Clear();
        _hasPrevAru = false;

        _calibState = CalibState.Running;
        _calibEndTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, calibrationDurationSec);
    }

    public void ClearCalibration()
    {
        _hasMapping = false;
        _qTrkToHmd = Quaternion.identity;
        _pTrkToHmd = Vector3.zero;
        _calibState = CalibState.NotStarted;
    }

    private void FinalizeCalibration()
    {
        if (_qSamples.Count < 5)
        {
            _hasMapping = false;
            _calibState = CalibState.Done;
            return;
        }

        _qTrkToHmd = MeanQuaternion(_qSamples);
        _pTrkToHmd = MeanVector3(_pSamples);

        _hasMapping = true;
        _calibState = CalibState.Done;
    }

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

            _lastPacketTimeMain = -999f;
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

        IPAddress acceptIp = null;
        bool useIpFilter = !string.IsNullOrWhiteSpace(acceptOnlyFromIp) && IPAddress.TryParse(acceptOnlyFromIp, out acceptIp);

        while (_running && _udp != null)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);

                if (useIpFilter && !ep.Address.Equals(acceptIp))
                    continue;

                if (data == null || data.Length != 64)
                    continue;

                if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
                    continue;

                int off = 4;
                off += 8;
                uint devIndex = BitConverter.ToUInt32(data, off);
                off += 4;

                if (targetDeviceIndex >= 0 && devIndex != (uint)targetDeviceIndex)
                    continue;

                float[] f = new float[12];
                for (int i = 0; i < 12; i++)
                {
                    f[i] = BitConverter.ToSingle(data, off);
                    off += 4;
                }

                // OpenVR 3x4
                float m00 = f[0], m01 = f[1], m02 = f[2], m03 = f[3];
                float m10 = f[4], m11 = f[5], m12 = f[6], m13 = f[7];
                float m20 = f[8], m21 = f[9], m22 = f[10], m23 = f[11];

                Vector3 xAxis, yAxis, zAxis, t;
                if (treatMatrixAsColumnMajorAxes)
                {
                    xAxis = new Vector3(m00, m10, m20);
                    yAxis = new Vector3(m01, m11, m21);
                    zAxis = new Vector3(m02, m12, m22);
                }
                else
                {
                    // fallback
                    xAxis = new Vector3(m00, m01, m02);
                    yAxis = new Vector3(m10, m11, m12);
                    zAxis = new Vector3(m20, m21, m22);
                }
                t = new Vector3(m03, m13, m23);

                if (openvrToUnity)
                {
                    // Z反転で右手→左手寄せ（位置も軸も統一）
                    xAxis.z = -xAxis.z;
                    yAxis.z = -yAxis.z;
                    zAxis.z = -zAxis.z;
                    t.z = -t.z;
                }

                // OpenVRは「-Z forward」寄りなので Unity forward(+Z)へ
                Vector3 forward = openvrToUnity ? -zAxis : zAxis;
                Vector3 up = yAxis;

                Quaternion rot = Quaternion.LookRotation(forward, up);
                Vector3 pos = t;

                // ★ここで Inverse(rot) は絶対にしない

                lock (_trkLock)
                {
                    _trkPosUnity = pos;
                    _trkRotUnity = rot;
                    _trkDev = devIndex;
                    _trkHasNew = true;

                    if (targetDeviceIndex < 0)
                        targetDeviceIndex = (int)devIndex;
                }
            }
            catch
            {
            }
        }
    }

    private Pose GetPoseInHmdFrame(Transform t)
    {
        Vector3 posH = hmdOrigin.InverseTransformPoint(t.position);
        Quaternion rotH = Quaternion.Inverse(hmdOrigin.rotation) * t.rotation;
        return new Pose(posH, rotH);
    }

    private void SetPoseInHmdFrame(Transform t, Pose poseH)
    {
        if (t.parent == hmdOrigin)
        {
            t.localPosition = poseH.position;
            t.localRotation = poseH.rotation;
        }
        else
        {
            t.position = hmdOrigin.TransformPoint(poseH.position);
            t.rotation = hmdOrigin.rotation * poseH.rotation;
        }
    }

    private static Vector3 MeanVector3(List<Vector3> vs)
    {
        Vector3 acc = Vector3.zero;
        for (int i = 0; i < vs.Count; i++) acc += vs[i];
        return acc / Mathf.Max(1, vs.Count);
    }

    private static Quaternion MeanQuaternion(List<Quaternion> qs)
    {
        if (qs == null || qs.Count == 0) return Quaternion.identity;

        Quaternion refQ = qs[0];
        Vector4 acc = Vector4.zero;

        for (int i = 0; i < qs.Count; i++)
        {
            Quaternion q = qs[i];
            if (Quaternion.Dot(q, refQ) < 0f)
            {
                q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w;
            }
            acc.x += q.x;
            acc.y += q.y;
            acc.z += q.z;
            acc.w += q.w;
        }

        float inv = 1f / qs.Count;
        Quaternion m = new Quaternion(acc.x * inv, acc.y * inv, acc.z * inv, acc.w * inv);
        return Quaternion.Normalize(m);
    }

    private void Visual_BindFailed()
    {
        Vector3 jitter = new Vector3(
            (Mathf.PerlinNoise(Time.time * 13f, 1f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 17f, 2f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 19f, 3f) - 0.5f) * 2f
        ) * bindFailJitter;

        targetCube.position = bindFailedPosition + jitter;
        targetCube.rotation = Quaternion.Euler(0f, Time.time * 180f, 0f);
    }

    private void Visual_BoundButNoPacket()
    {
        float bob = Mathf.Sin(Time.time * idleBobSpeed) * idleBobAmplitude;
        targetCube.position = idleBasePosition + new Vector3(0f, bob, 0f);
        targetCube.rotation = Quaternion.Euler(0f, Time.time * 30f, 0f);
    }

    private void Visual_Calibrating()
    {
        float bob = Mathf.Sin(Time.time * (idleBobSpeed * 1.3f)) * (idleBobAmplitude * 0.7f);
        targetCube.position = idleBasePosition + new Vector3(0f, bob, 0f);
        targetCube.rotation = Quaternion.Euler(Time.time * 90f, Time.time * 120f, 0f);
    }
}
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class TrackerToCubeOffsetCalibrator : MonoBehaviour
{
    [Header("UDP (Tracker input)")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Tooltip("この送信元IP(PC)からのUDPのみ受理。空なら全て受理。")]
    public string acceptOnlyFromIp = "192.168.0.46";

    [Header("HMD frame")]
    [Tooltip("HMD座標系の原点（例：CenterEyeAnchor / MainCamera）")]
    public Transform hmdOrigin;

    [Header("ArUco input (Cube pose in Quest WORLD)")]
    [Tooltip("ArUcoで認識されたCubeのTransform。必ず Questワールド上の正しい位置/回転になっていること。")]
    public Transform arucoCube;

    [Header("Output (Estimated cube pose in HMD frame)")]
    [Tooltip("推定Cube。hmdOrigin の子にして localPose 更新が推奨。")]
    public Transform targetCube;

    [Header("Calibration")]
    public bool autoStartCalibration = true;
    public float calibrationDurationSec = 5.0f;

    public bool gateByStability = true;
    public float maxPosDeltaForSample = 0.03f;          // m
    public float maxAngleDeltaForSampleDeg = 3.0f;      // deg

    [Header("Tracker selection")]
    public int targetDeviceIndex = -1;

    [Header("OpenVR -> Unity axis conversion")]
    [Tooltip("OpenVR->Unityの軸整合を行う（右手→左手 + forward整合）。通常ON。")]
    public bool openvrToUnity = true;

    [Header("Visual state (no logs)")]
    public Vector3 bindFailedPosition = new Vector3(0f, 0.2f, 0.5f);
    public Vector3 idleBasePosition = new Vector3(0f, 1.2f, 1.5f);
    public float idleBobAmplitude = 0.08f;
    public float idleBobSpeed = 2.0f;
    public float bindFailJitter = 0.03f;

    public float noPacketTimeoutSec = 1.0f;
    public float bindRetryIntervalSec = 1.0f;

    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;
    private volatile bool _boundOk;

    private readonly object _trkLock = new object();
    private bool _trkHasNew;
    private Vector3 _trkPosV;        // SteamVR(OpenVR)座標（Unity軸整合後）= ^V p_trk
    private Quaternion _trkRotV;     // ^V q_trk
    private uint _trkDev;
    private float _lastPacketTimeMain = -999f;

    private enum CalibState { NotStarted, Running, Done }
    private CalibState _calibState = CalibState.NotStarted;
    private float _calibEndTime = 0f;

    // ====== 案2の核心： ^Q T_V を推定する ======
    // Quest WORLD での pose = ^Q T_V * (SteamVR pose)
    private Quaternion _qQV = Quaternion.identity; // ^Q R_V
    private Vector3 _pQV = Vector3.zero;          // ^Q p_V
    private bool _hasQV = false;

    private readonly List<Quaternion> _qSamples = new List<Quaternion>(512);
    private readonly List<Vector3> _pSamples = new List<Vector3>(512);

    private Vector3 _prevAruPosW;
    private Quaternion _prevAruRotW;
    private bool _hasPrevAru = false;

    private float _nextBindTryTime = 0f;

    void Start()
    {
        _running = true;
        _boundOk = false;
        _nextBindTryTime = Time.realtimeSinceStartup;

        if (autoStartCalibration)
            BeginCalibration();
    }

    void OnDestroy()
    {
        _running = false;
        StopReceiver();
    }

    void Update()
    {
        if (!_boundOk && Time.realtimeSinceStartup >= _nextBindTryTime)
        {
            TryBindAndStartReceiver();
            _nextBindTryTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, bindRetryIntervalSec);
        }

        if (targetCube == null) return;

        // --- Tracker packet snapshot ---
        bool hasNew;
        Vector3 trkPosV;
        Quaternion trkRotV;

        lock (_trkLock)
        {
            hasNew = _trkHasNew;
            trkPosV = _trkPosV;
            trkRotV = _trkRotV;
            _trkHasNew = false;
        }

        if (hasNew) _lastPacketTimeMain = Time.realtimeSinceStartup;

        if (!_boundOk)
        {
            Visual_BindFailed();
            return;
        }

        float sincePkt = Time.realtimeSinceStartup - _lastPacketTimeMain;
        if (sincePkt > noPacketTimeoutSec)
        {
            Visual_BoundButNoPacket();
            return;
        }

        // --- ArUco cube pose in Quest WORLD ---
        bool hasAru = (arucoCube != null);
        Pose aruW = default;
        if (hasAru)
        {
            // 重要：arucoCubeは「Questワールドにおける」キューブ姿勢
            aruW = new Pose(arucoCube.position, arucoCube.rotation);
        }

        // ===== Calibration: estimate ^Q T_V =====
        if (_calibState == CalibState.Running)
        {
            Visual_Calibrating();

            if (hasAru)
            {
                if (gateByStability)
                {
                    if (_hasPrevAru)
                    {
                        float dp = Vector3.Distance(aruW.position, _prevAruPosW);
                        float da = Quaternion.Angle(aruW.rotation, _prevAruRotW);
                        if (dp > maxPosDeltaForSample || da > maxAngleDeltaForSampleDeg)
                        {
                            _prevAruPosW = aruW.position;
                            _prevAruRotW = aruW.rotation;
                            return;
                        }
                    }
                    _prevAruPosW = aruW.position;
                    _prevAruRotW = aruW.rotation;
                    _hasPrevAru = true;
                }

                // ^Q T_V = ^Q T_cube * inv(^V T_trk)
                // rotation: qQV = qCubeW * inv(qTrkV)
                Quaternion q = aruW.rotation * Quaternion.Inverse(trkRotV);
                // position: pQV = pCubeW - qQV * pTrkV
                Vector3 p = aruW.position - (q * trkPosV);

                _qSamples.Add(q);
                _pSamples.Add(p);
            }

            if (Time.realtimeSinceStartup >= _calibEndTime)
                FinalizeCalibration();

            return;
        }

        // ===== Apply mapping =====
        if (_hasQV && hmdOrigin != null)
        {
            // cube world pose: ^Q T_cube = ^Q T_V * ^V T_trk
            Vector3 cubePosW = _qQV * trkPosV + _pQV;
            Quaternion cubeRotW = _qQV * trkRotV;

            // convert to HMD frame (H)
            Pose cubeH = GetPoseInHmdFrame_FromWorld(cubePosW, cubeRotW);
            SetPoseInHmdFrame(targetCube, cubeH);
        }
        else
        {
            // fallback: ArUco があるなら ArUco をそのまま出す（動作を止めない）
            if (hasAru && hmdOrigin != null)
            {
                Pose cubeH = GetPoseInHmdFrame(arucoCube);
                SetPoseInHmdFrame(targetCube, cubeH);
            }
            else
            {
                targetCube.localPosition = Vector3.zero;
                targetCube.localRotation = Quaternion.identity;
            }
        }
    }

    public void BeginCalibration()
    {
        _qSamples.Clear();
        _pSamples.Clear();
        _hasPrevAru = false;

        _calibState = CalibState.Running;
        _calibEndTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, calibrationDurationSec);
    }

    public void ClearCalibration()
    {
        _hasQV = false;
        _qQV = Quaternion.identity;
        _pQV = Vector3.zero;
        _calibState = CalibState.NotStarted;
    }

    private void FinalizeCalibration()
    {
        if (_qSamples.Count < 5)
        {
            _hasQV = false;
            _calibState = CalibState.Done;
            return;
        }

        _qQV = MeanQuaternion(_qSamples);
        _pQV = MeanVector3(_pSamples);

        _hasQV = true;
        _calibState = CalibState.Done;
    }

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

            _lastPacketTimeMain = -999f;
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

    // Receive OpenVR 3x4 -> Unity axes pose in "V" frame
    private void ReceiveLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        IPAddress acceptIp = null;
        bool useIpFilter = !string.IsNullOrWhiteSpace(acceptOnlyFromIp) && IPAddress.TryParse(acceptOnlyFromIp, out acceptIp);

        while (_running && _udp != null)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);

                if (useIpFilter && !ep.Address.Equals(acceptIp))
                    continue;

                if (data == null || data.Length != 64)
                    continue;

                if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
                    continue;

                int off = 4;
                off += 8; // now_ms (int64)
                uint devIndex = BitConverter.ToUInt32(data, off);
                off += 4;

                if (targetDeviceIndex >= 0 && devIndex != (uint)targetDeviceIndex)
                    continue;

                float[] f = new float[12];
                for (int i = 0; i < 12; i++)
                {
                    f[i] = BitConverter.ToSingle(data, off);
                    off += 4;
                }

                Matrix4x4 M = Matrix4x4.identity;
                M.m00 = f[0]; M.m01 = f[1]; M.m02 = f[2]; M.m03 = f[3];
                M.m10 = f[4]; M.m11 = f[5]; M.m12 = f[6]; M.m13 = f[7];
                M.m20 = f[8]; M.m21 = f[9]; M.m22 = f[10]; M.m23 = f[11];

                // OpenVR(RH,-Zfwd) -> Unity(LH,+Zfwd)  (as you had)
                if (openvrToUnity)
                {
                    Matrix4x4 Sz = Matrix4x4.identity;
                    Sz.m22 = -1f;
                    M = Sz * M * Sz;
                }

                Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
                Quaternion rot = QuaternionFromMatrix(M);

                lock (_trkLock)
                {
                    _trkPosV = pos;
                    _trkRotV = rot;
                    _trkDev = devIndex;
                    _trkHasNew = true;

                    if (targetDeviceIndex < 0)
                        targetDeviceIndex = (int)devIndex;
                }
            }
            catch
            {
                // swallow packet errors
            }
        }
    }

    private static Quaternion QuaternionFromMatrix(Matrix4x4 m)
    {
        Vector3 forward = new Vector3(m.m02, m.m12, m.m22);
        Vector3 up = new Vector3(m.m01, m.m11, m.m21);

        if (forward.sqrMagnitude < 1e-10f || up.sqrMagnitude < 1e-10f)
            return Quaternion.identity;

        forward = forward.normalized;
        up = (up - Vector3.Dot(up, forward) * forward).normalized;

        if (up.sqrMagnitude < 1e-10f)
            return Quaternion.identity;

        return Quaternion.LookRotation(forward, up);
    }

    // --- Pose conversion helpers ---
    private Pose GetPoseInHmdFrame(Transform t)
    {
        Vector3 posH = hmdOrigin.InverseTransformPoint(t.position);
        Quaternion rotH = Quaternion.Inverse(hmdOrigin.rotation) * t.rotation;
        return new Pose(posH, rotH);
    }

    private Pose GetPoseInHmdFrame_FromWorld(Vector3 worldPos, Quaternion worldRot)
    {
        Vector3 posH = hmdOrigin.InverseTransformPoint(worldPos);
        Quaternion rotH = Quaternion.Inverse(hmdOrigin.rotation) * worldRot;
        return new Pose(posH, rotH);
    }

    private void SetPoseInHmdFrame(Transform t, Pose poseH)
    {
        if (t.parent == hmdOrigin)
        {
            t.localPosition = poseH.position;
            t.localRotation = poseH.rotation;
        }
        else
        {
            t.position = hmdOrigin.TransformPoint(poseH.position);
            t.rotation = hmdOrigin.rotation * poseH.rotation;
        }
    }

    private static Vector3 MeanVector3(List<Vector3> vs)
    {
        Vector3 acc = Vector3.zero;
        for (int i = 0; i < vs.Count; i++) acc += vs[i];
        return acc / Mathf.Max(1, vs.Count);
    }

    private static Quaternion MeanQuaternion(List<Quaternion> qs)
    {
        if (qs == null || qs.Count == 0) return Quaternion.identity;

        Quaternion refQ = qs[0];
        Vector4 acc = Vector4.zero;

        for (int i = 0; i < qs.Count; i++)
        {
            Quaternion q = qs[i];
            if (Quaternion.Dot(q, refQ) < 0f)
            {
                q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w;
            }
            acc.x += q.x; acc.y += q.y; acc.z += q.z; acc.w += q.w;
        }

        float inv = 1f / qs.Count;
        Quaternion m = new Quaternion(acc.x * inv, acc.y * inv, acc.z * inv, acc.w * inv);
        return Quaternion.Normalize(m);
    }

    // --- Visuals ---
    private void Visual_BindFailed()
    {
        Vector3 jitter = new Vector3(
            (Mathf.PerlinNoise(Time.time * 13f, 1f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 17f, 2f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(Time.time * 19f, 3f) - 0.5f) * 2f
        ) * bindFailJitter;

        targetCube.position = bindFailedPosition + jitter;
        targetCube.rotation = Quaternion.Euler(0f, Time.time * 180f, 0f);
    }

    private void Visual_BoundButNoPacket()
    {
        float bob = Mathf.Sin(Time.time * idleBobSpeed) * idleBobAmplitude;
        targetCube.position = idleBasePosition + new Vector3(0f, bob, 0f);
        targetCube.rotation = Quaternion.Euler(0f, Time.time * 30f, 0f);
    }

    private void Visual_Calibrating()
    {
        float bob = Mathf.Sin(Time.time * (idleBobSpeed * 1.3f)) * (idleBobAmplitude * 0.7f);
        targetCube.position = idleBasePosition + new Vector3(0f, bob, 0f);
        targetCube.rotation = Quaternion.Euler(Time.time * 90f, Time.time * 120f, 0f);
    }
}
