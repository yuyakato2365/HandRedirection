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

    [Header("Debug")]
    [Range(0f, 1f)] public float knob01;
    public float secondsSinceLastPacket = 999f;
    public string lastFrom = "";

    private UdpClient _udp;
    private Thread _thread;
    private volatile float _latest;     // ここはvolatileでOK
    private volatile bool _running;

    private readonly object _lock = new object();
    private DateTime _lastPacketTime = DateTime.MinValue;

    void Awake()
    {
        Application.runInBackground = true;
    }

    void OnEnable()
    {
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

        if (t == DateTime.MinValue)
            secondsSinceLastPacket = 999f;
        else
            secondsSinceLastPacket = (float)(DateTime.UtcNow - t).TotalSeconds;
    }

    private void RecvLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);
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
