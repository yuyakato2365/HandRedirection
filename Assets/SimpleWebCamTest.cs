using UnityEngine;
using UnityEngine.UI;

public class SimpleWebCamTest : MonoBehaviour
{
    public RawImage preview;
    WebCamTexture cam;

    void Start()
    {
        if (preview == null) preview = GetComponent<RawImage>();

        var devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            Debug.LogError("No webcam devices");
            return;
        }

        Debug.Log("Use camera: " + devices[0].name);
        cam = new WebCamTexture(devices[0].name, 640, 480, 30);
        preview.texture = cam;
        cam.Play();
    }

    void OnDestroy()
    {
        if (cam != null && cam.isPlaying) cam.Stop();
    }
}
