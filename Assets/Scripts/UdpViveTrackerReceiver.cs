using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

public class UdpViveTrackerReceiver : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 9000;

    [Header("Target (observation)")]
    public Transform trackerWorld;   // ここを更新する（観測用Transform）

    [Header("Select tracker")]
    public int trackerIndex = -1;    // -1なら最初に来たやつを採用

    [Header("OpenVR -> Unity conversion")]
    public bool flipZ = true;              // 位置Z反転（典型）
    public bool flipRotationHandedness = true; // 回転も同様に反転（典型）
    public float positionScale = 1.0f;     // m->mなら1

    [Header("Status")]
    public bool hasPose;
    public double lastTimestampMs;
    public float staleSeconds = 0.2f; // これ以上来なければ無効扱い

    UdpClient udp;
    IPEndPoint ep;
    int boundIndex = -1;

    void Start()
    {
        if (!trackerWorld) trackerWorld = this.transform;
        ep = new IPEndPoint(IPAddress.Any, listenPort);
        udp = new UdpClient(listenPort);
        udp.Client.ReceiveTimeout = 1;
    }

    void Update()
    {
        bool gotAny = false;
        while (udp.Available > 0)
        {
            var data = udp.Receive(ref ep);
            if (TryParse(data, out long ms, out int idx, out Matrix4x4 T_openvr))
            {
                gotAny = true;
                if (trackerIndex >= 0)
                {
                    if (idx != trackerIndex) continue;
                    boundIndex = trackerIndex;
                }
                else
                {
                    // 最初に来たトラッカーを採用
                    if (boundIndex < 0) boundIndex = idx;
                    if (idx != boundIndex) continue;
                }

                ApplyPose(T_openvr);
                lastTimestampMs = ms;
                hasPose = true;
            }
        }

        // 途切れ判定
        if (hasPose)
        {
            // UnityのTimeで簡易判定（msはPC時刻なので比較しない）
            // 「最新パケットを受信したUpdateフレーム」で gotAny がfalseのまま続くとstale
            // 簡易：Time.timeを使ってもいいが、ここは省略して“受信が止まったら無効”にするなら以下が簡単
        }

        // 何も来てなければ一定時間で無効化（簡易）
        // ※より正確にやるなら「最後に受信したTime.time」を記録して比較
    }

    void OnDestroy()
    {
        udp?.Close();
    }

    bool TryParse(byte[] data, out long ms, out int idx, out Matrix4x4 T)
    {
        ms = 0; idx = 0; T = Matrix4x4.identity;

        // 4 + 8 + 4 + 12*4 = 64 bytes
        if (data == null || data.Length < 64) return false;

        if (data[0] != (byte)'T' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'0')
            return false;

        ms = BitConverter.ToInt64(data, 4);
        idx = BitConverter.ToInt32(data, 12);

        // float 12個（row-major 3x4）
        int o = 16;
        float m00 = BitConverter.ToSingle(data, o + 0);
        float m01 = BitConverter.ToSingle(data, o + 4);
        float m02 = BitConverter.ToSingle(data, o + 8);
        float m03 = BitConverter.ToSingle(data, o + 12);

        float m10 = BitConverter.ToSingle(data, o + 16);
        float m11 = BitConverter.ToSingle(data, o + 20);
        float m12 = BitConverter.ToSingle(data, o + 24);
        float m13 = BitConverter.ToSingle(data, o + 28);

        float m20 = BitConverter.ToSingle(data, o + 32);
        float m21 = BitConverter.ToSingle(data, o + 36);
        float m22 = BitConverter.ToSingle(data, o + 40);
        float m23 = BitConverter.ToSingle(data, o + 44);

        T = Matrix4x4.identity;
        T.m00 = m00; T.m01 = m01; T.m02 = m02; T.m03 = m03;
        T.m10 = m10; T.m11 = m11; T.m12 = m12; T.m13 = m13;
        T.m20 = m20; T.m21 = m21; T.m22 = m22; T.m23 = m23;
        return true;
    }

    void ApplyPose(Matrix4x4 T_openvr)
    {
        // 位置（OpenVRのm単位）
        Vector3 p = new Vector3(T_openvr.m03, T_openvr.m13, T_openvr.m23) * positionScale;

        // 回転（行列→Quaternion）
        Quaternion q = QuaternionFromMatrix(T_openvr);

        if (flipZ)
        {
            p.z = -p.z;
        }

        if (flipRotationHandedness)
        {
            // 典型的な左右手系変換の一例（Z反転を回転にも反映）
            // q' = ( -x, -y,  z,  w ) みたいな形になることが多い
            q = new Quaternion(-q.x, -q.y, q.z, q.w);
        }

        trackerWorld.SetPositionAndRotation(p, q);
    }

    static Quaternion QuaternionFromMatrix(Matrix4x4 m)
    {
        // forward = Z列, up = Y列（Unityの慣習）
        Vector3 forward = new Vector3(m.m02, m.m12, m.m22);
        Vector3 up = new Vector3(m.m01, m.m11, m.m21);
        if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f)
            return Quaternion.identity;
        return Quaternion.LookRotation(forward, up);
    }
}
