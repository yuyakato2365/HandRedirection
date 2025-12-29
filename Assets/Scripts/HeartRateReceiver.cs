using UnityEngine;
using OscJack;

public class HeartRateReceiver : MonoBehaviour
{
    OscServer server;
    public int heartRate;
    public float heartRange;

    void Start()
    {
        // OSCサーバ起動 (ポート9000)
        server = new OscServer(9000);
        server.MessageDispatcher.AddCallback(
            "/avatar/parameters/HeartRateBPM",
            (string address, OscDataHandle data) =>
            {
                heartRate = data.GetElementAsInt(0);
                Debug.Log($"HeartRate: {heartRate}");
            });

        server.MessageDispatcher.AddCallback(
            "/avatar/parameters/HeartRateRangeFactor",
            (string address, OscDataHandle data) =>
            {
                heartRange = data.GetElementAsFloat(0);
                Debug.Log($"HeartRangeFactor: {heartRange}");
            });
    }

    void OnDestroy()
    {
        server?.Dispose();
    }
}
