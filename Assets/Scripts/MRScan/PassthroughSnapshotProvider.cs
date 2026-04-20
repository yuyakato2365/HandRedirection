using System;
using System.Collections;
using System.Reflection;
using Unity.Collections;
using UnityEngine;

public class PassthroughSnapshotProvider_RightCam_V2 : MonoBehaviour
{
    [Serializable]
    public struct CameraSnapshot
    {
        public byte[] jpegBytes;
        public int width, height;
        public float fx, fy, cx, cy;
        public Vector3 camPosWorld;
        public Quaternion camRotWorld;
        public RectInt roiInFullRes;
        public bool usedRoi;
    }

    [Header("V2 Marker (must appear)")]
    [SerializeField] private int inspectorMarker = 20260216;   // Å© Ç±ÇÍÇ™InspectorÇ…èoÇ»ÇØÇÍÇŒîΩâfÇ≥ÇÍÇƒÇ»Ç¢

    [Header("Right Cam (drag PassthroughCameraAccess component here)")]
    [SerializeField] private MonoBehaviour rightCam;

    [Header("Output")]
    [Range(10, 100)][SerializeField] private int jpegQuality = 90;

    [Header("ROI")]
    [SerializeField] private bool cropBySphereRoi = true;
    [SerializeField] private float roiMargin = 1.2f;
    [SerializeField] private int minRoiSize = 256;

    private void Awake()
    {
        Debug.Log($"[PSP_V2] Awake OK. marker={inspectorMarker}, type={GetType().FullName}");
    }

    public void CaptureRightEyeJpegAsync(
        Vector3 Cw, float R,
        Action<CameraSnapshot> onDone,
        Action<string> onError)
    {
        StartCoroutine(CaptureCoroutine(Cw, R, onDone, onError));
    }

    private IEnumerator CaptureCoroutine(
        Vector3 Cw, float R,
        Action<CameraSnapshot> onDone,
        Action<string> onError)
    {
        if (rightCam == null) { onError?.Invoke("PSP_V2: rightCam is null"); yield break; }

        // IsPlaying
        float t0 = Time.realtimeSinceStartup;
        while (!GetBoolProp(rightCam, "IsPlaying", false))
        {
            if (Time.realtimeSinceStartup - t0 > 5f) { onError?.Invoke("PSP_V2: IsPlaying timeout"); yield break; }
            yield return null;
        }

        // Resolution
        Vector2Int res = GetVector2IntProp(rightCam, "CurrentResolution", Vector2Int.zero);
        if (res.x <= 0 || res.y <= 0) { onError?.Invoke("PSP_V2: invalid resolution"); yield break; }

        // GetColors
        object colorsObj = InvokeMethod(rightCam, "GetColors");
        if (colorsObj == null) { onError?.Invoke("PSP_V2: GetColors() missing"); yield break; }

        NativeArray<Color32> colors;
        try { colors = (NativeArray<Color32>)colorsObj; }
        catch { onError?.Invoke($"PSP_V2: GetColors type={colorsObj.GetType()}"); yield break; }

        if (!colors.IsCreated || colors.Length != res.x * res.y)
        {
            onError?.Invoke($"PSP_V2: colors mismatch len={colors.Length} res={res.x}x{res.y}");
            yield break;
        }

        // Intrinsics (best-effort)
        float fx, fy, cx, cy;
        if (!TryGetIntrinsics(rightCam, out fx, out fy, out cx, out cy))
        {
            fx = res.x * 0.9f; fy = res.x * 0.9f; cx = res.x * 0.5f; cy = res.y * 0.5f;
        }

        // Pose
        Pose camPoseW = new Pose(rightCam.transform.position, rightCam.transform.rotation);
        object poseObj = InvokeMethod(rightCam, "GetCameraPose");
        if (poseObj is Pose p) camPoseW = p;

        // Texture
        Texture2D full = new Texture2D(res.x, res.y, TextureFormat.RGBA32, false, false);
        full.SetPixels32(colors.ToArray());
        full.Apply(false, false);

        RectInt roi = new RectInt(0, 0, res.x, res.y);
        bool usedRoi = false;
        if (cropBySphereRoi)
        {
            roi = ComputeSphereRoiPixels(Cw, R, camPoseW, fx, fy, cx, cy, res.x, res.y, roiMargin, minRoiSize);
            usedRoi = !(roi.x == 0 && roi.y == 0 && roi.width == res.x && roi.height == res.y);
        }

        Texture2D outTex = full;
        if (usedRoi)
        {
            outTex = Crop(full, roi);
            Destroy(full);
        }

        byte[] jpg = outTex.EncodeToJPG(jpegQuality);
        Destroy(outTex);

        onDone?.Invoke(new CameraSnapshot
        {
            jpegBytes = jpg,
            width = roi.width,
            height = roi.height,
            fx = fx,
            fy = fy,
            cx = cx,
            cy = cy,
            camPosWorld = camPoseW.position,
            camRotWorld = camPoseW.rotation,
            roiInFullRes = roi,
            usedRoi = usedRoi
        });
    }

    // ---- helpers ----
    private static object InvokeMethod(object obj, string name)
    {
        var mi = obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return mi?.Invoke(obj, null);
    }
    private static bool GetBoolProp(object obj, string name, bool def)
    {
        var pi = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pi == null) return def;
        object v = pi.GetValue(obj);
        return v is bool b ? b : def;
    }
    private static Vector2Int GetVector2IntProp(object obj, string name, Vector2Int def)
    {
        var pi = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pi == null) return def;
        object v = pi.GetValue(obj);
        return v is Vector2Int vi ? vi : def;
    }
    private static bool TryGetIntrinsics(MonoBehaviour cam, out float fx, out float fy, out float cx, out float cy)
    {
        fx = fy = cx = cy = 0;
        var intrPi = cam.GetType().GetProperty("Intrinsics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (intrPi == null) return false;
        object intr = intrPi.GetValue(cam);
        if (intr == null) return false;

        Vector2 focal = GetVector2Prop(intr, "FocalLength", Vector2.zero);
        Vector2 pp = GetVector2Prop(intr, "PrincipalPoint", Vector2.zero);
        if (focal == Vector2.zero || pp == Vector2.zero) return false;

        fx = focal.x; fy = focal.y; cx = pp.x; cy = pp.y;
        return true;
    }
    private static Vector2 GetVector2Prop(object obj, string name, Vector2 def)
    {
        var pi = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pi == null) return def;
        object v = pi.GetValue(obj);
        return v is Vector2 vv ? vv : def;
    }
    private static RectInt ComputeSphereRoiPixels(Vector3 Cw, float R, Pose camPoseW,
        float fx, float fy, float cx, float cy, int w, int h, float margin, int minSize)
    {
        Quaternion qcw = Quaternion.Inverse(camPoseW.rotation);
        Vector3 Pc = qcw * (Cw - camPoseW.position);
        if (Pc.z <= 0.05f) return new RectInt(0, 0, w, h);

        float u = fx * (Pc.x / Pc.z) + cx;
        float v = fy * (Pc.y / Pc.z) + cy;
        float rPix = fx * (R / Pc.z) * Mathf.Max(1f, margin);
        int half = Mathf.Max((int)rPix, minSize / 2);

        int x0 = Mathf.Clamp((int)u - half, 0, w - 1);
        int y0 = Mathf.Clamp((int)v - half, 0, h - 1);
        int x1 = Mathf.Clamp((int)u + half, 0, w);
        int y1 = Mathf.Clamp((int)v + half, 0, h);

        int rw = Mathf.Max(x1 - x0, minSize);
        int rh = Mathf.Max(y1 - y0, minSize);
        int mx = (x0 + x1) / 2;
        int my = (y0 + y1) / 2;

        int nx0 = Mathf.Clamp(mx - rw / 2, 0, w - 1);
        int ny0 = Mathf.Clamp(my - rh / 2, 0, h - 1);
        int nx1 = Mathf.Clamp(nx0 + rw, 0, w);
        int ny1 = Mathf.Clamp(ny0 + rh, 0, h);

        return new RectInt(nx0, ny0, nx1 - nx0, ny1 - ny0);
    }
    private static Texture2D Crop(Texture2D src, RectInt roi)
    {
        Color[] pixels = src.GetPixels(roi.x, roi.y, roi.width, roi.height);
        Texture2D dst = new Texture2D(roi.width, roi.height, src.format, false, false);
        dst.SetPixels(pixels);
        dst.Apply(false, false);
        return dst;
    }
}
