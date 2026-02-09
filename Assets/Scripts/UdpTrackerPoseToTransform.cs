using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class UdpTrackerPoseToTransform_QuestNoLog : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Header("Target Cube (required for visual debugging)")]
    public Transform targetCube;

    [Header("State thresholds")]
    public float noPacketTimeoutSec = 1.0f;   // これ以上受信がなければ「未受信」扱い
    public float bindRetryIntervalSec = 1.0f; // bind失敗時のリトライ周期

    [Header("Visual behavior (no logs)")]
    public Vector3 bindFailedPosition = new Vector3(0f, 0.2f, 0.5f);
    public Vector3 idleBasePosition = new Vector3(0f, 1.2f, 1.5f);
    public float idleBobAmplitude = 0.08f;
    public float idleBobSpeed = 2.0f;
    public float bindFailJitter = 0.03f;

    [Header("Packet filter (optional)")]
    public int targetDeviceIndex = -1; // -1: 最初に来たデバイスで固定

    [Header("Coordinate options")]
    public bool applyUnityZFlip = true;

    // --- internal ---
    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;
    private volatile bool _boundOk;

    private readonly object _poseLock = new object();
    private bool _hasPose;
    private Vector3 _pos;
    private Quaternion _rot;
    private float _lastPacketTime; // main thread time

    // bind retry
    private float _nextBindTryTime;

    void Start()
    {
        if (targetCube == null)
        {
            // ログが見られない前提なら、ここは致命的なので止めるより “見える挙動” にする
            // ひとまずこのスクリプトは動かすが、視覚デバッグはできない
        }

        _running = true;
        _boundOk = false;
        _nextBindTryTime = Time.realtimeSinceStartup; // すぐ試す
    }

    void OnDestroy()
    {
        _running = false;
        StopReceiver();
    }

    void Update()
    {
        // 1) bindがまだならリトライ
        if (!_boundOk && Time.realtimeSinceStartup >= _nextBindTryTime)
        {
            TryBindAndStartReceiver();
            _nextBindTryTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, bindRetryIntervalSec);
        }

        // 2) 受信があれば追従、なければ状態表示
        bool hasPoseLocal;
        Vector3 p;
        Quaternion q;
        float lastPkt;

        lock (_poseLock)
        {
            hasPoseLocal = _hasPose;
            p = _pos;
            q = _rot;
            lastPkt = _lastPacketTime;
            _hasPose = false;
        }

        // Cubeが未設定なら何もできない（ただしbindは継続する）
        if (targetCube == null) return;

        if (!_boundOk)
        {
            // --- bind失敗：目に見えるエラー挙動（足元で震える） ---
            Vector3 jitter = new Vector3(
                (Mathf.PerlinNoise(Time.time * 13f, 1f) - 0.5f) * 2f,
                (Mathf.PerlinNoise(Time.time * 17f, 2f) - 0.5f) * 2f,
                (Mathf.PerlinNoise(Time.time * 19f, 3f) - 0.5f) * 2f
            ) * bindFailJitter;

            targetCube.position = bindFailedPosition + jitter;
            targetCube.rotation = Quaternion.Euler(0f, Time.time * 180f, 0f);
            return;
        }

        // bind成功
        float since = Time.realtimeSinceStartup - lastPkt;

        if (since > noPacketTimeoutSec)
        {
            // --- bind成功だが未受信：待機モーション（ゆっくり上下） ---
            float bob = Mathf.Sin(Time.time * idleBobSpeed) * idleBobAmplitude;
            targetCube.position = idleBasePosition + new Vector3(0f, bob, 0f);
            targetCube.rotation = Quaternion.Euler(0f, Time.time * 30f, 0f);
            return;
        }

        // --- 受信中：トラッカーに追従 ---
        if (hasPoseLocal)
        {
            targetCube.position = p;
            targetCube.rotation = q;
        }
        // hasPoseLocalがfalseでも lastPkt が新しい間は直前姿勢維持（チラつき防止）
    }

    private void TryBindAndStartReceiver()
    {
        // すでに受信中なら何もしない
        if (_udp != null) return;

        try
        {
            var family = forceIPv4 ? AddressFamily.InterNetwork : AddressFamily.Unspecified;
            _udp = new UdpClient(family);

            // 重要：0.0.0.0:9000 で待つ（QuestがどのIPであっても受ける）
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));

            _boundOk = true;

            // 受信スレッド開始
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true };
            _rxThread.Start();

            // bind成功した瞬間に「未受信→待機モーション」へ行くように lastPacketTime を古くしておく
            lock (_poseLock)
            {
                _lastPacketTime = -999f;
            }
        }
        catch
        {
            // ログ見えない前提なので握りつぶしてOK（視覚で bind失敗を表現する）
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

                // magic "TRK0"
                if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
                    continue;

                int off = 4;
                long nowMs = BitConverter.ToInt64(data, off); off += 8;
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

                Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
                Vector3 up = new Vector3(M.m01, M.m11, M.m21);
                Vector3 fwd = new Vector3(M.m02, M.m12, M.m22);
                Quaternion rot = Quaternion.LookRotation(fwd, up);

                lock (_poseLock)
                {
                    _pos = pos;
                    _rot = rot;
                    _hasPose = true;

                    // 「受信した」ことをメインスレッドで判定できるように時刻更新
                    // ※ Time.realtimeSinceStartup はスレッドで触らない
                    // 代わりにフラグだけ立て、メインスレッド側で更新するのが理想だが、
                    // ここでは lastPacketTime を “更新要求” するために _lastPacketTime を NaN にする
                    _lastPacketTime = float.NaN;

                    if (targetDeviceIndex < 0) targetDeviceIndex = (int)devIndex;
                }
            }
            catch
            {
                // 受信スレッドは落とさない
            }
        }
    }

    void LateUpdate()
    {
        // ReceiveLoopではUnity API触れないので、ここで「受信した」時刻を反映
        lock (_poseLock)
        {
            if (float.IsNaN(_lastPacketTime))
            {
                _lastPacketTime = Time.realtimeSinceStartup;
            }
        }
    }
}
