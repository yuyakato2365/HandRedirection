using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class PcInferenceClient : MonoBehaviour
{
    [Header("PC server")]
    public string serverUrl = "http://192.168.0.10:8000/infer"; // change to your PC IP

    [Serializable]
    public class InferResponse
    {
        public string format; // "ply"
        public string message;
    }

    public IEnumerator PostInfer(byte[] imageJpeg, string payloadJson, Action<byte[]> onPlyBytes, Action<string> onError)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("image", imageJpeg, "frame.jpg", "image/jpeg");
        form.AddField("payload", payloadJson);

        using (UnityWebRequest req = UnityWebRequest.Post(serverUrl, form))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 120; // seconds
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"HTTP error: {req.error}\n{req.downloadHandler.text}");
                yield break;
            }

            // We return raw PLY bytes as response body (application/octet-stream)
            byte[] plyBytes = req.downloadHandler.data;
            onPlyBytes?.Invoke(plyBytes);
        }
    }
}
