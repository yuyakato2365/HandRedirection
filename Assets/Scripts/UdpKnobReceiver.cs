using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class UdpKnobReceiver : MonoBehaviour
{
    [Header("UDP Listen")]
    public int listenPort = 5005;

    [Header("Filter (optional)")]
    [Tooltip("空なら全送信元を受け付ける。指定すると、そのIPv4/IPv6からのUDPのみ採用する（例: 172.20.10.126）")]
    public string allowedSenderIp = ""; // ESP32のIPなど

    [Tooltip("trueならフィルタに一致しないパケットを捨てるログを出す（デバッグ用、連打注意）")]
    public bool logDroppedPackets = false;

    [Header("Debug")]
    [Range(0f, 1f)] public float knob01;
    public float secondsSinceLastPacket = 999f;
    public string lastFrom = "";

    private UdpClient _udp;
    private Thread _thread;
    private volatile float _latest;
    private volatile bool _running;

    private readonly object _lock = new object();
    private DateTime _lastPacketTime = DateTime.MinValue;

    // 解析済みの許可IP（nullならフィルタ無効）
    private IPAddress _allowedAddr = null;

    void Awake()
    {
        Application.runInBackground = true;
    }

    void OnEnable()
    {
        // allowedSenderIp を事前パース
        _allowedAddr = null;
        if (!string.IsNullOrWhiteSpace(allowedSenderIp))
        {
            if (!IPAddress.TryParse(allowedSenderIp.Trim(), out _allowedAddr))
            {
                Debug.LogWarning($"[UdpKnobReceiver] allowedSenderIp parse failed: '{allowedSenderIp}'. Filter disabled.");
                _allowedAddr = null;
            }
        }

        _running = true;

        // IPv4で明示的にバインド（Quest実機で安定しやすい）
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        _udp.Client.ReceiveTimeout = 1000;

        _thread = new Thread(RecvLoop) { IsBackground = true };
        _thread.Start();
    }

    void OnDisable()
    {
        _running = false;

        try { _udp?.Close(); } catch { }
        _udp = null;

        try { _thread?.Join(200); } catch { }
        _thread = null;
    }

    void Update()
    {
        knob01 = Mathf.Clamp01(_latest);

        DateTime t;
        lock (_lock) t = _lastPacketTime;

        secondsSinceLastPacket = (t == DateTime.MinValue)
            ? 999f
            : (float)(DateTime.UtcNow - t).TotalSeconds;
    }

    private void RecvLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);

                // ★追加：送信元IPフィルタ
                if (_allowedAddr != null && !ep.Address.Equals(_allowedAddr))
                {
                    if (logDroppedPackets)
                        Debug.Log($"[UdpKnobReceiver] Dropped from {ep.Address}:{ep.Port}");
                    continue;
                }

                string s = Encoding.ASCII.GetString(data).Trim();

                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                {
                    _latest = Mathf.Clamp01(v);

                    lock (_lock)
                    {
                        _lastPacketTime = DateTime.UtcNow;
                        lastFrom = ep.Address.ToString();
                    }
                }
            }
            catch (SocketException) { /* timeout等 */ }
            catch (ObjectDisposedException) { /* 終了 */ }
            catch (Exception) { /* 必要ならログ */ }
        }
    }
}
