/*
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Meta XR Passthrough Camera API
using Meta.XR;

// OpenCV for Unity
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;        // Utils.texture2DToMat
using OpenCVForUnity.UnityIntegration;  // OpenCVMatUtils
using Rect = UnityEngine.Rect;

/// <summary>
/// Quest3 右目 Passthrough カメラ + QR検出 + PnP。
/// ・キューブのワールド姿勢
/// ・HMD基準のキューブ姿勢
/// を同時に求める。
/// 進捗は TextMeshPro で表示する。
/// </summary>
public class QuestRightEyeQrTracker : MonoBehaviour
{
    [Header("Right-eye Passthrough Camera")]
    [Tooltip("右目用 PassthroughCameraAccess コンポーネント")]
    public PassthroughCameraAccess rightEyePca;

    [Tooltip("デバッグ用 Passthrough画像表示 RawImage（任意）")]
    public RawImage preview;

    [Header("Pose Estimation")]
    [Tooltip("QRコード一辺の実長さ [m]")]
    public float qrSizeMeters = 0.08f;

    [Header("HMD / Cube / Anchor")]
    [Tooltip("HMD（CenterEyeAnchor か MainCamera）の Transform")]
    public Transform hmdTransform;

    [Tooltip("ワールド空間で QR 面中心＋姿勢を表すアンカー")]
    public Transform qrAnchorWorld;

    [Tooltip("実物キューブに対応する Cube（qrAnchorWorld の子）")]
    public Transform cubeWorld;

    [Tooltip("HMD を親にして、HMD 基準の CubeTransform を格納する先")]
    public Transform cubeRelativeToHmd;

    [Header("Debug UI (TMP)")]
    [Tooltip("進捗表示用の TextMeshPro（UI）")]
    public TMP_Text statusText;

    // ---- GPU→CPU→OpenCV バッファ ----
    Texture2D _cpuTex;
    RenderTexture _rt;
    Mat _rgbaMat;
    Mat _grayMat;

    // ---- QR 検出用 ----
    Mat _points;
    readonly List<string> _decodedInfo = new List<string>();
    readonly List<Mat> _straightQrcode = new List<Mat>();
    QRCodeDetector _detector;

    // ---- カメラ内部パラメータ ----
    Mat _cameraMatrix;
    MatOfDouble _distCoeffs;

    // ----------------------------------------------------------------------
    // Unity lifecycle
    // ----------------------------------------------------------------------

    void Start()
    {

        // 追加: 権限リクエスト
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
            "android.permission.CAMERA",
            "horizonos.permission.HEADSET_CAMERA"
        });
        }

        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) が未設定です。");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        _detector = new QRCodeDetector();

        // Cube の親子・スケール設定
        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * qrSizeMeters;
            // QR平面(z=0) を Cube 前面中央に合わせる
            cubeWorld.localPosition = new Vector3(0f, 0f, qrSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        // HMD 基準 Transform 用：親を HMD にしておく
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null)
        {
            SetStatus("Error: PCA is null");
            return;
        }

        if (!rightEyePca.IsPlaying)
        {
            SetStatus("PCA: not playing");
            return;
        }

        if (!rightEyePca.IsUpdatedThisFrame)
        {
            // フレーム更新待ち
            SetStatus("PCA: waiting frame");
            return;
        }

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null)
        {
            SetStatus("PCA: texture is null");
            return;
        }

        EnsureBuffers(camTex.width, camTex.height);
        SetStatus($"Frame: {camTex.width}x{camTex.height}");

        // GPU → RT → CPU Texture
        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        // CPU Texture → OpenCV Mat
        if (_rgbaMat == null)
        {
            _rgbaMat = new Mat(_cpuTex.height, _cpuTex.width, CvType.CV_8UC4);
            _grayMat = new Mat(_cpuTex.height, _cpuTex.width, CvType.CV_8UC1);
            Debug.Log($"Create Mats for PCA: {_cpuTex.width}x{_cpuTex.height}");
        }
        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        // Intrinsics がまだなら一度だけ構築
        if (_cameraMatrix == null)
        {
            SetStatus("Build intrinsics...");
            BuildCameraMatrixFromIntrinsics();
            if (_cameraMatrix == null)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
            else
            {
                SetStatus("Intrinsics ready");
            }
        }

        // QR 検出 ＋ PnP → Transform 更新
        RunQrDetectionAndPnP(_rgbaMat);

        // デバッグ表示
        if (preview != null)
        {
            OpenCVMatUtils.MatToTexture2D(_rgbaMat, _cpuTex);
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta =
                new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    // ----------------------------------------------------------------------
    // Helper
    // ----------------------------------------------------------------------

    void SetStatus(string msg)
    {
        if (statusText != null)
        {
            statusText.text = msg;
        }
        // 必要ならログも残す
        // Debug.Log(msg);
    }

    void EnsureBuffers(int w, int h)
    {
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();
            _rgbaMat = null;
            _grayMat = null;
        }
    }

    void BuildCameraMatrixFromIntrinsics()
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        double fx = intr.FocalLength.x;
        double fy = intr.FocalLength.y;
        double cx = intr.PrincipalPoint.x;
        double cy = intr.PrincipalPoint.y;

        _cameraMatrix = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrix.put(0, 0, fx);
        _cameraMatrix.put(0, 1, 0);
        _cameraMatrix.put(0, 2, cx);
        _cameraMatrix.put(1, 0, 0);
        _cameraMatrix.put(1, 1, fy);
        _cameraMatrix.put(1, 2, cy);
        _cameraMatrix.put(2, 0, 0);
        _cameraMatrix.put(2, 1, 0);
        _cameraMatrix.put(2, 2, 1.0);

        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

        Debug.Log($"[RightEye Intrinsics] fx={fx:F1}, fy={fy:F1}, cx={cx:F1}, cy={cy:F1}");
    }

    void RunQrDetectionAndPnP(Mat rgbaMat)
    {
        if (_grayMat == null)
        {
            SetStatus("Gray Mat is null");
            return;
        }

        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);
        SetStatus("QR: detecting...");

        _decodedInfo.Clear();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();

        if (_points == null) _points = new Mat();
        else
        {
            _points.release();
            _points = new Mat();
        }

        bool result = _detector.detectAndDecodeMulti(_grayMat, _decodedInfo, _points, _straightQrcode);

        if (!result || _points.empty())
        {
            Imgproc.putText(
                rgbaMat, "Decoding failed.",
                new Point(5, rgbaMat.rows() - 10),
                Imgproc.FONT_HERSHEY_SIMPLEX, 0.7,
                new Scalar(255, 255, 255, 255), 2,
                Imgproc.LINE_AA, false);

            SetStatus("QR: not found");
            return;
        }

        SetStatus($"QR: found {_points.total() / 4} markers");

        float[] qrCodeCorners = new float[_points.total() * _points.channels()];
        _points.get(0, 0, qrCodeCorners);

        for (int i = 0; i < qrCodeCorners.Length; i += 8)
        {
            int idx = i / 8;

            // 枠線描画
            for (int c = 0; c < 4; c++)
            {
                int cur = i + c * 2;
                int nxt = i + ((c + 1) % 4) * 2;
                Imgproc.line(
                    rgbaMat,
                    new Point(qrCodeCorners[cur], qrCodeCorners[cur + 1]),
                    new Point(qrCodeCorners[nxt], qrCodeCorners[nxt + 1]),
                    new Scalar(255, 0, 0, 255), 2);
            }

            if (_decodedInfo.Count > idx && _decodedInfo[idx] != null)
            {
                Imgproc.putText(
                    rgbaMat, _decodedInfo[idx],
                    new Point(qrCodeCorners[i], qrCodeCorners[i + 1]),
                    Imgproc.FONT_HERSHEY_SIMPLEX, 0.7,
                    new Scalar(255, 255, 255, 255), 2,
                    Imgproc.LINE_AA, false);
            }

            if (_cameraMatrix == null)
            {
                Debug.LogWarning("cameraMatrix is null");
                SetStatus("Error: cameraMatrix null");
                continue;
            }

            // 2D image points
            Point[] imagePoints = new Point[]
            {
                new Point(qrCodeCorners[i + 0], qrCodeCorners[i + 1]),
                new Point(qrCodeCorners[i + 2], qrCodeCorners[i + 3]),
                new Point(qrCodeCorners[i + 4], qrCodeCorners[i + 5]),
                new Point(qrCodeCorners[i + 6], qrCodeCorners[i + 7]),
            };

            // 3D object points (QR平面、中心原点)
            float s = qrSizeMeters;
            Point3[] objectPoints = new Point3[]
            {
                new Point3(-s/2f,  s/2f, 0),
                new Point3( s/2f,  s/2f, 0),
                new Point3( s/2f, -s/2f, 0),
                new Point3(-s/2f, -s/2f, 0),
            };

            using (MatOfPoint2f imagePtsMat = new MatOfPoint2f())
            using (MatOfPoint3f objectPtsMat = new MatOfPoint3f())
            using (Mat rvec = new Mat())
            using (Mat tvec = new Mat())
            {
                imagePtsMat.fromArray(imagePoints);
                objectPtsMat.fromArray(objectPoints);

                bool ok = Calib3d.solvePnP(
                    objectPtsMat,
                    imagePtsMat,
                    _cameraMatrix,
                    _distCoeffs,
                    rvec,
                    tvec,
                    false,
                    Calib3d.SOLVEPNP_IPPE_SQUARE);

                if (!ok)
                {
                    Debug.LogWarning($"[PnP FAIL] QR[{idx}]");
                    SetStatus("PnP: fail");
                    continue;
                }

                // 距離だけステータスに軽く出す（Z: 奥行き）
                double[] t = new double[3];
                tvec.get(0, 0, t);
                SetStatus($"PnP: OK  z={t[2]:F2} m");

                UpdateTransformsFromPnP(rvec, tvec);
            }

            // 1個だけで十分なら break してもOK
            // break;
        }
    }

    void UpdateTransformsFromPnP(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // --- 右目カメラローカル位置（OpenCV → Unity） ---
        double[] t = new double[3];
        tvec.get(0, 0, t);

        Vector3 posCamLocal = new Vector3(
            (float)t[0],          // x右
            (float)(-t[1]),       // y下 → y上
            (float)t[2]           // z前
        );

        // --- 回転（OpenCV → Unity） ---
        Mat R_cv = new Mat(3, 3, CvType.CV_64F);
        Calib3d.Rodrigues(rvec, R_cv);
        double[] r = new double[9];
        R_cv.get(0, 0, r);

        // C = diag(1,-1,1) で y 反転を焼き込む
        Matrix4x4 m = Matrix4x4.identity;
        m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
        m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
        m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];

        Vector3 forward = m.GetColumn(2);
        Vector3 up = m.GetColumn(1);
        Quaternion rotCamLocal = Quaternion.LookRotation(forward, up);

        // --- 右目カメラのワールドPoseを取得 ---
        Pose camWorldPose = rightEyePca.GetCameraPose();

        Vector3 cubeWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
        Quaternion cubeWorldRot = camWorldPose.rotation * rotCamLocal;

        // 1. ワールド空間の Anchor/Cube を更新
        qrAnchorWorld.position = cubeWorldPos;
        qrAnchorWorld.rotation = cubeWorldRot;

        // 2. HMD基準の CubeTransform を更新
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (cubeWorldPos - hmdTransform.position);
            Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * cubeWorldRot;

            cubeRelativeToHmd.localPosition = relPos;
            cubeRelativeToHmd.localRotation = relRot;
        }
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);
        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _points?.Dispose();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();
        _cameraMatrix?.Dispose();
        _distCoeffs?.Dispose();
    }
}
*/




/*
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Meta XR Passthrough Camera API
using Meta.XR;

// OpenCV for Unity
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.UnityIntegration;
using Rect = UnityEngine.Rect;

/// <summary>
/// Quest3 右目 Passthrough カメラ + QR検出 + PnP（軽量化版）
/// ・処理間隔を落とす（10Hz）
/// ・解像度ダウンサンプリングしてからQR検出
/// ・バッファ再利用
/// ・TextMeshPro更新を間引き
/// </summary>
public class QuestRightEyeQrTracker : MonoBehaviour
{
    [Header("Right-eye Passthrough Camera")]
    [Tooltip("右目用 PassthroughCameraAccess コンポーネント")]
    public PassthroughCameraAccess rightEyePca;

    [Tooltip("デバッグ用 Passthrough画像表示 RawImage（任意）")]
    public RawImage preview;

    [Header("Pose Estimation")]
    [Tooltip("QRコード一辺の実長さ [m]")]
    public float qrSizeMeters = 0.08f;

    [Header("HMD / Cube / Anchor")]
    [Tooltip("HMD（CenterEyeAnchor か MainCamera）の Transform")]
    public Transform hmdTransform;

    [Tooltip("ワールド空間で QR 面中心＋姿勢を表すアンカー")]
    public Transform qrAnchorWorld;

    [Tooltip("実物キューブに対応する Cube（qrAnchorWorld の子）")]
    public Transform cubeWorld;

    [Tooltip("HMD を親にして、HMD 基準の CubeTransform を格納する先")]
    public Transform cubeRelativeToHmd;

    [Header("Debug UI (TMP)")]
    [Tooltip("進捗表示用の TextMeshPro（UI）")]
    public TMP_Text statusText;

    [Header("Performance Settings")]
    [Tooltip("QR検出の実行間隔 [秒]（例: 0.1 で 10Hz）")]
    public float processInterval = 0.1f;

    [Tooltip("ステータス表示更新間隔 [秒]")]
    public float statusUpdateInterval = 0.3f;

    [Tooltip("QR検出用に縮小するスケール（0.5 なら 1/2 解像度）")]
    [Range(0.25f, 1.0f)]
    public float downscale = 0.5f;

    // ---- 時間管理 ----
    float _lastProcessTime;
    float _lastStatusTime;

    // ---- GPU→CPU→OpenCV バッファ ----
    Texture2D _cpuTex;
    RenderTexture _rt;
    Mat _rgbaMat;      // フル解像度 RGBA
    Mat _grayMat;      // フル解像度 グレースケール
    Mat _graySmall;    // 縮小グレースケール

    // ---- QR 検出用 ----
    Mat _points;
    readonly List<string> _decodedInfo = new List<string>();
    readonly List<Mat> _straightQrcode = new List<Mat>();
    QRCodeDetector _detector;

    // ---- カメラ内部パラメータ ----
    Mat _cameraMatrixFull;   // フル解像度用
    Mat _cameraMatrixSmall;  // 縮小画像用
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;

    // ---- 一時配列（毎フレームnewを避ける）----
    float[] _qrCodeCornersBuf;

    // ----------------------------------------------------------------------
    // Unity lifecycle
    // ----------------------------------------------------------------------

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Passthrough / カメラ権限
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA"
            });
        }
#endif

        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) が未設定です。");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        _detector = new QRCodeDetector();

        // Cube の親子・スケール設定
        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * qrSizeMeters;
            cubeWorld.localPosition = new Vector3(0f, 0f, qrSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        // HMD 基準 Transform 用：親を HMD にしておく
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null)
        {
            SetStatus("Error: PCA is null");
            return;
        }

        if (!rightEyePca.IsPlaying)
        {
            SetStatus("PCA: not playing");
            return;
        }

        if (!rightEyePca.IsUpdatedThisFrame)
        {
            // フレーム更新待ち
            return;
        }

        // QR処理は一定間隔でのみ実行（軽量化の要）
        if (Time.time - _lastProcessTime < processInterval)
        {
            return;
        }
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null)
        {
            SetStatus("PCA: texture is null");
            return;
        }

        EnsureBuffers(camTex.width, camTex.height);

        // GPU → RT → CPU Texture
        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        // CPU Texture → OpenCV Mat（フル解像度）
        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        // Intrinsics 構築（まだなら一度だけ）
        if (!_intrinsicsReady)
        {
            BuildCameraMatricesFromIntrinsics();
            if (!_intrinsicsReady)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
        }

        // QR 検出 ＋ PnP → Transform 更新（ダウンサンプリングつき）
        RunQrDetectionAndPnP(_rgbaMat);

        // デバッグ用プレビュー（必要なら）
        if (preview != null)
        {
            // ここでは _cpuTex をそのまま表示
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta = new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    // ----------------------------------------------------------------------
    // Helper
    // ----------------------------------------------------------------------

    void SetStatus(string msg)
    {
        if (statusText == null) return;

        // 更新頻度を間引いてGCやレイアウトコストを削減
        if (Time.time - _lastStatusTime < statusUpdateInterval) return;
        _lastStatusTime = Time.time;

        statusText.text = msg;
        // Debug.Log(msg);
    }

    void EnsureBuffers(int w, int h)
    {
        // 解像度が変わったときだけバッファを作り直す
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();
            _graySmall?.Dispose();

            _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
            _grayMat = new Mat(h, w, CvType.CV_8UC1);

            int sw = Mathf.Max(1, Mathf.RoundToInt(w * downscale));
            int sh = Mathf.Max(1, Mathf.RoundToInt(h * downscale));
            _graySmall = new Mat(sh, sw, CvType.CV_8UC1);

            Debug.Log($"[PCA Buffers] RT/Texture/Mats created: {w}x{h}, small={sw}x{sh}");

            // 解像度が変わった場合は cameraMatrix も作り直す
            _intrinsicsReady = false;
        }
    }

    void BuildCameraMatricesFromIntrinsics()
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        double fx = intr.FocalLength.x;
        double fy = intr.FocalLength.y;
        double cx = intr.PrincipalPoint.x;
        double cy = intr.PrincipalPoint.y;

        _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx);
        _cameraMatrixFull.put(0, 1, 0);
        _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0);
        _cameraMatrixFull.put(1, 1, fy);
        _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0);
        _cameraMatrixFull.put(2, 1, 0);
        _cameraMatrixFull.put(2, 2, 1.0);

        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

        // 縮小画像用 cameraMatrix を作成
        _cameraMatrixSmall = _cameraMatrixFull.clone();
        double[] fxv = new double[1];
        double[] fyv = new double[1];
        double[] cxv = new double[1];
        double[] cyv = new double[1];
        _cameraMatrixFull.get(0, 0, fxv);
        _cameraMatrixFull.get(1, 1, fyv);
        _cameraMatrixFull.get(0, 2, cxv);
        _cameraMatrixFull.get(1, 2, cyv);

        fxv[0] *= downscale;
        fyv[0] *= downscale;
        cxv[0] *= downscale;
        cyv[0] *= downscale;

        _cameraMatrixSmall.put(0, 0, fxv);
        _cameraMatrixSmall.put(1, 1, fyv);
        _cameraMatrixSmall.put(0, 2, cxv);
        _cameraMatrixSmall.put(1, 2, cyv);

        Debug.Log($"[RightEye Intrinsics] fx={fx:F1}, fy={fy:F1}, cx={cx:F1}, cy={cy:F1}, scale={downscale}");

        _intrinsicsReady = true;
    }

    void RunQrDetectionAndPnP(Mat rgbaMat)
    {
        if (_grayMat == null || _graySmall == null)
        {
            SetStatus("Gray Mats not ready");
            return;
        }

        // フル解像度 → グレースケール
        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

        // 縮小（ピクセル数を減らしてQR検出を高速化）
        Imgproc.resize(_grayMat, _graySmall, _graySmall.size());

        _decodedInfo.Clear();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();

        if (_points == null) _points = new Mat();
        else _points.release();

        bool result = _detector.detectAndDecodeMulti(_graySmall, _decodedInfo, _points, _straightQrcode);

        if (!result || _points.empty())
        {
            SetStatus("QR: not found");
            return;
        }

        int totalCorners = (int)(_points.total() * _points.channels());
        if (_qrCodeCornersBuf == null || _qrCodeCornersBuf.Length != totalCorners)
        {
            _qrCodeCornersBuf = new float[totalCorners];
        }
        _points.get(0, 0, _qrCodeCornersBuf);

        SetStatus($"QR: found {_points.total() / 4} markers");

        // 各QRコードごとに PnP
        for (int i = 0; i < _qrCodeCornersBuf.Length; i += 8)
        {
            int idx = i / 8;

            if (_cameraMatrixSmall == null)
            {
                SetStatus("Error: cameraMatrixSmall null");
                continue;
            }

            // 2D image points（縮小画像座標系）
            Point[] imagePoints = new Point[]
            {
                new Point(_qrCodeCornersBuf[i + 0], _qrCodeCornersBuf[i + 1]),
                new Point(_qrCodeCornersBuf[i + 2], _qrCodeCornersBuf[i + 3]),
                new Point(_qrCodeCornersBuf[i + 4], _qrCodeCornersBuf[i + 5]),
                new Point(_qrCodeCornersBuf[i + 6], _qrCodeCornersBuf[i + 7]),
            };

            // 3D object points (QR平面、中心原点)
            float s = qrSizeMeters;
            Point3[] objectPoints = new Point3[]
            {
                new Point3(-s/2f,  s/2f, 0),
                new Point3( s/2f,  s/2f, 0),
                new Point3( s/2f, -s/2f, 0),
                new Point3(-s/2f, -s/2f, 0),
            };

            using (MatOfPoint2f imagePtsMat = new MatOfPoint2f())
            using (MatOfPoint3f objectPtsMat = new MatOfPoint3f())
            using (Mat rvec = new Mat())
            using (Mat tvec = new Mat())
            {
                imagePtsMat.fromArray(imagePoints);
                objectPtsMat.fromArray(objectPoints);

                bool ok = Calib3d.solvePnP(
                    objectPtsMat,
                    imagePtsMat,
                    _cameraMatrixSmall,   // 縮小画像用のカメラ行列
                    _distCoeffs,
                    rvec,
                    tvec,
                    false,
                    Calib3d.SOLVEPNP_IPPE_SQUARE);

                if (!ok)
                {
                    SetStatus("PnP: fail");
                    continue;
                }

                double[] t = new double[3];
                tvec.get(0, 0, t);
                SetStatus($"PnP: OK  z={t[2]:F2} m");

                UpdateTransformsFromPnP(rvec, tvec);
            }

            // 1個目だけ使うならここで break
            // break;
        }
    }

    void UpdateTransformsFromPnP(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // --- 右目カメラローカル位置（OpenCV → Unity） ---
        double[] t = new double[3];
        tvec.get(0, 0, t);

        Vector3 posCamLocal = new Vector3(
            (float)t[0],          // x右
            (float)(-t[1]),       // y下 → y上
            (float)t[2]           // z前
        );

        // --- 回転（OpenCV → Unity） ---
        Mat R_cv = new Mat(3, 3, CvType.CV_64F);
        Calib3d.Rodrigues(rvec, R_cv);
        double[] r = new double[9];
        R_cv.get(0, 0, r);

        // C = diag(1,-1,1) で y 反転を焼き込む
        Matrix4x4 m = Matrix4x4.identity;
        m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
        m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
        m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];

        Vector3 forward = m.GetColumn(2);
        Vector3 up = m.GetColumn(1);
        Quaternion rotCamLocal = Quaternion.LookRotation(forward, up);

        // --- 右目カメラのワールドPoseを取得 ---
        Pose camWorldPose = rightEyePca.GetCameraPose();

        Vector3 cubeWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
        Quaternion cubeWorldRot = camWorldPose.rotation * rotCamLocal;

        // 1. ワールド空間の Anchor/Cube を更新
        qrAnchorWorld.position = cubeWorldPos;
        qrAnchorWorld.rotation = cubeWorldRot;

        // 2. HMD基準の CubeTransform を更新
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (cubeWorldPos - hmdTransform.position);
            Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * cubeWorldRot;

            cubeRelativeToHmd.localPosition = relPos;
            cubeRelativeToHmd.localRotation = relRot;
        }
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _graySmall?.Dispose();
        _points?.Dispose();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();

        _cameraMatrixFull?.Dispose();
        _cameraMatrixSmall?.Dispose();
        _distCoeffs?.Dispose();
    }
}
*/



























/*
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Meta XR Passthrough Camera API
using Meta.XR;

// OpenCV for Unity
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using Rect = UnityEngine.Rect;

/// <summary>
/// Quest 3 右目 Passthrough カメラから画像を取得し、
/// OpenCV で QR 検出 + PnP 姿勢推定を行って、
///
/// - qrAnchorWorld : ワールド空間の QR 面中心 + 姿勢
/// - cubeWorld     : 実物キューブに対応する 3D Cube（qrAnchorWorld の子）
/// - cubeRelativeToHmd : HMD を親にした「実物キューブの実位置 O_real」
///
/// を更新する。
/// 手や物体の「写像 F」は別スクリプト（GoGoInteractionController_NoY）で行う。
/// </summary>
public class QuestRightEyeQrTracker : MonoBehaviour
{
    [Header("Meta XR Passthrough")]
    [Tooltip("右目用 PassthroughCameraAccess")]
    public PassthroughCameraAccess rightEyePca;

    [Tooltip("HMD（CenterEyeAnchor など）")]
    public Transform hmdTransform;

    [Header("QR Settings")]
    [Tooltip("QRコード一辺の実寸 (m)")]
    public float qrSizeMeters = 0.08f;

    [Header("World Anchors")]
    [Tooltip("ワールド空間で QR 面中心＋姿勢を表すアンカー")]
    public Transform qrAnchorWorld;

    [Tooltip("実物キューブに対応する Cube（qrAnchorWorld の子）")]
    public Transform cubeWorld;

    [Tooltip("HMD を親にして、HMD 基準の CubeTransform を格納する先（O_real）")]
    public Transform cubeRelativeToHmd;

    [Header("Debug UI")]
    public TMP_Text statusText;
    public RawImage preview;

    [Header("Performance Settings")]
    [Tooltip("QR検出の実行間隔 [秒]（例: 0.1 で 10Hz）")]
    public float processInterval = 0.1f;

    [Tooltip("ステータス表示更新間隔 [秒]")]
    public float statusUpdateInterval = 0.3f;

    [Tooltip("QR検出用に縮小するスケール（0.5 なら 1/2 解像度）")]
    [Range(0.25f, 1.0f)]
    public float downscale = 0.7f;

    // ---- 時間管理 ----
    float _lastProcessTime;
    float _lastStatusTime;

    // ---- GPU→CPU→OpenCV バッファ ----
    Texture2D _cpuTex;
    RenderTexture _rt;
    Mat _rgbaMat;      // フル解像度 RGBA
    Mat _grayMat;      // フル解像度グレースケール
    Mat _graySmall;    // 縮小グレースケール

    // ---- QR 検出用 ----
    Mat _points;
    readonly List<string> _decodedInfo = new List<string>();
    readonly List<Mat> _straightQrcode = new List<Mat>();
    QRCodeDetector _detector;

    // ---- カメラ内部パラメータ ----
    Mat _cameraMatrixFull;   // フル解像度用
    Mat _cameraMatrixSmall;  // 縮小画像用
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;

    // ---- 一時配列（毎フレーム new を避ける）----
    float[] _qrCodeCornersBuf;

    // ----------------------------------------------------------------------
    // Unity lifecycle
    // ----------------------------------------------------------------------

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Passthrough / カメラ権限
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA"
            });
        }
#endif

        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) が未設定です。");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        _detector = new QRCodeDetector();
        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0); // 歪みは一旦ゼロ扱い

        // Cube の親子・スケール設定（ワールド用）
        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * qrSizeMeters;
            // QR平面(z=0) を Cube 前面中央に合わせる
            cubeWorld.localPosition = new Vector3(0f, 0f, qrSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        // HMD 基準 Transform 用：親を HMD にしておく（ここが O_real）
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null)
        {
            SetStatus("Error: PCA is null");
            return;
        }

        if (!rightEyePca.IsPlaying)
        {
            SetStatus("PCA: not playing");
            return;
        }

        if (!rightEyePca.IsUpdatedThisFrame)
        {
            // フレーム更新待ち（負荷軽減のため何もしない）
            return;
        }

        // QR処理は一定間隔でのみ実行（軽量化の要）
        if (Time.time - _lastProcessTime < processInterval)
        {
            return;
        }
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null)
        {
            SetStatus("PCA: texture is null");
            return;
        }

        EnsureBuffers(camTex.width, camTex.height);

        // GPU → RT → CPU Texture
        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        // CPU Texture → OpenCV Mat（フル解像度）
        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        // Intrinsics 構築（まだなら一度だけ）
        if (!_intrinsicsReady)
        {
            BuildCameraMatricesFromIntrinsics();
            if (!_intrinsicsReady)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
        }

        // QR 検出 ＋ PnP → Transform 更新（ダウンサンプリング付き）
        RunQrDetectionAndPnP(_rgbaMat);

        // デバッグ用プレビュー
        if (preview != null)
        {
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta = new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    // ----------------------------------------------------------------------
    // QR detection + PnP
    // ----------------------------------------------------------------------

    void RunQrDetectionAndPnP(Mat rgbaMat)
    {
        if (rgbaMat == null || rgbaMat.empty()) return;

        // RGBA → グレースケール
        if (_grayMat == null || _grayMat.cols() != rgbaMat.cols() || _grayMat.rows() != rgbaMat.rows())
        {
            _grayMat?.Dispose();
            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);
        }
        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

        // 縮小グレースケール
        if (_graySmall == null)
        {
            int sw = Mathf.Max(1, Mathf.RoundToInt(_grayMat.cols() * downscale));
            int sh = Mathf.Max(1, Mathf.RoundToInt(_grayMat.rows() * downscale));
            _graySmall = new Mat(sh, sw, CvType.CV_8UC1);
        }
        Imgproc.resize(_grayMat, _graySmall, new Size(_graySmall.cols(), _graySmall.rows()));

        // QR 検出
        if (_points == null) _points = new Mat();
        _decodedInfo.Clear();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();

        bool found = _detector.detectAndDecodeMulti(_graySmall, _decodedInfo, _points, _straightQrcode);
        if (!found || _points.empty())
        {
            SetStatus("QR: none");
            return;
        }

        // points Mat から 4隅座標を取得（1 QR あたり 8 要素: x0,y0,...,x3,y3）
        int total = (int)(_points.total() * _points.channels());
        if (_qrCodeCornersBuf == null || _qrCodeCornersBuf.Length < total)
        {
            _qrCodeCornersBuf = new float[total];
        }
        _points.get(0, 0, _qrCodeCornersBuf);

        // ここでは 1 個目の QR だけ使う
        if (total < 8)
        {
            SetStatus("QR: invalid points");
            return;
        }

        int i = 0;
        Point[] imagePoints = new Point[]
        {
            new Point(_qrCodeCornersBuf[i + 0], _qrCodeCornersBuf[i + 1]),
            new Point(_qrCodeCornersBuf[i + 2], _qrCodeCornersBuf[i + 3]),
            new Point(_qrCodeCornersBuf[i + 4], _qrCodeCornersBuf[i + 5]),
            new Point(_qrCodeCornersBuf[i + 6], _qrCodeCornersBuf[i + 7]),
        };

        // 3D object points (QR平面、中心原点)
        float s = qrSizeMeters;
        Point3[] objectPoints = new Point3[]
        {
            new Point3(-s/2f,  s/2f, 0),
            new Point3( s/2f,  s/2f, 0),
            new Point3( s/2f, -s/2f, 0),
            new Point3(-s/2f, -s/2f, 0),
        };

        using (MatOfPoint2f imagePtsMat = new MatOfPoint2f())
        using (MatOfPoint3f objectPtsMat = new MatOfPoint3f())
        using (Mat rvec = new Mat())
        using (Mat tvec = new Mat())
        {
            imagePtsMat.fromArray(imagePoints);
            objectPtsMat.fromArray(objectPoints);

            bool ok = Calib3d.solvePnP(
                objectPtsMat,
                imagePtsMat,
                _cameraMatrixSmall,   // 縮小画像用のカメラ行列
                _distCoeffs,
                rvec,
                tvec,
                false,
                Calib3d.SOLVEPNP_IPPE_SQUARE);

            if (!ok)
            {
                SetStatus("PnP: fail");
                return;
            }

            double[] t = new double[3];
            tvec.get(0, 0, t);
            SetStatus($"PnP: OK  z={t[2]:F2} m");

            UpdateTransformsFromPnP(rvec, tvec);
        }
    }

    void UpdateTransformsFromPnP(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // --- 右目カメラローカル位置（OpenCV → Unity） ---
        double[] t = new double[3];
        tvec.get(0, 0, t);

        Vector3 posCamLocal = new Vector3(
            (float)t[0],          // x右
            (float)(-t[1]),       // y下 → y上
            (float)t[2]           // z前
        );

        // --- 回転（OpenCV → Unity） ---
        Mat R_cv = new Mat(3, 3, CvType.CV_64F);
        Calib3d.Rodrigues(rvec, R_cv);
        double[] r = new double[9];
        R_cv.get(0, 0, r);

        // C = diag(1,-1,1) で y 反転を焼き込む
        Matrix4x4 m = Matrix4x4.identity;
        m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
        m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
        m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];

        Vector3 forward = m.GetColumn(2);
        Vector3 up = m.GetColumn(1);
        Quaternion rotCamLocal = Quaternion.LookRotation(forward, up);

        // --- 右目カメラのワールドPoseを取得 ---
        Pose camWorldPose = rightEyePca.GetCameraPose();

        Vector3 cubeWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
        Quaternion cubeWorldRot = camWorldPose.rotation * rotCamLocal;

        // 1. ワールド空間の Anchor/Cube を更新
        qrAnchorWorld.position = cubeWorldPos;
        qrAnchorWorld.rotation = cubeWorldRot;

        // 2. HMD基準の CubeTransform を更新（ここが実座標 O_real）
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (cubeWorldPos - hmdTransform.position);
            Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * cubeWorldRot;

            cubeRelativeToHmd.localPosition = relPos;
            cubeRelativeToHmd.localRotation = relRot;
        }
    }

    // ----------------------------------------------------------------------
    // Helper
    // ----------------------------------------------------------------------

    void SetStatus(string msg)
    {
        if (statusText == null) return;

        // 更新頻度を間引いて GC やレイアウトコストを削減
        if (Time.time - _lastStatusTime < statusUpdateInterval) return;
        _lastStatusTime = Time.time;

        statusText.text = msg;
    }

    void EnsureBuffers(int w, int h)
    {
        // 解像度が変わったときだけバッファを作り直す
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();
            _graySmall?.Dispose();

            _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
            _grayMat = new Mat(h, w, CvType.CV_8UC1);

            int sw = Mathf.Max(1, Mathf.RoundToInt(w * downscale));
            int sh = Mathf.Max(1, Mathf.RoundToInt(h * downscale));
            _graySmall = new Mat(sh, sw, CvType.CV_8UC1);

            Debug.Log($"[PCA Buffers] RT/Texture/Mats created: {w}x{h}, small={sw}x{sh}");

            // 解像度が変わった場合は cameraMatrix も作り直す
            _intrinsicsReady = false;
        }
    }

    void BuildCameraMatricesFromIntrinsics()
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        double fx = intr.FocalLength.x;
        double fy = intr.FocalLength.y;
        double cx = intr.PrincipalPoint.x;
        double cy = intr.PrincipalPoint.y;

        _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx);
        _cameraMatrixFull.put(0, 1, 0);
        _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0);
        _cameraMatrixFull.put(1, 1, fy);
        _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0);
        _cameraMatrixFull.put(2, 1, 0);
        _cameraMatrixFull.put(2, 2, 1);

        // downscale に合わせて小さい画像用のカメラ行列を構築
        int fullW = (int)intr.SensorResolution.x;
        int fullH = (int)intr.SensorResolution.y;
        int smallW = Mathf.Max(1, Mathf.RoundToInt(fullW * downscale));
        int smallH = Mathf.Max(1, Mathf.RoundToInt(fullH * downscale));

        double scaleX = (double)smallW / fullW;
        double scaleY = (double)smallH / fullH;

        _cameraMatrixSmall = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixSmall.put(0, 0, fx * scaleX);
        _cameraMatrixSmall.put(0, 1, 0);
        _cameraMatrixSmall.put(0, 2, cx * scaleX);
        _cameraMatrixSmall.put(1, 0, 0);
        _cameraMatrixSmall.put(1, 1, fy * scaleY);
        _cameraMatrixSmall.put(1, 2, cy * scaleY);
        _cameraMatrixSmall.put(2, 0, 0);
        _cameraMatrixSmall.put(2, 1, 0);
        _cameraMatrixSmall.put(2, 2, 1);

        _intrinsicsReady = true;
        Debug.Log("[PCA] Intrinsics ready.");
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _graySmall?.Dispose();
        _points?.Dispose();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();

        _cameraMatrixFull?.Dispose();
        _cameraMatrixSmall?.Dispose();
        _distCoeffs?.Dispose();
    }
}
*/



/*
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Meta XR Passthrough Camera API
using Meta.XR;

// OpenCV for Unity
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using Rect = UnityEngine.Rect;

/// <summary>
/// Quest 3 右目 Passthrough カメラから画像を取得し、
/// OpenCV (ArUco) でマーカー検出 + PnP 姿勢推定を行って、
///
/// - qrAnchorWorld : ワールド空間の「マーカー面中心 + 姿勢」
/// - cubeWorld     : 実物キューブに対応する 3D Cube（qrAnchorWorld の子）
/// - cubeRelativeToHmd : HMD を親にした「実物キューブの実位置 O_real」
///
/// を更新する。
/// </summary>
public class QuestRightEyeArucoTracker_Rebuild : MonoBehaviour
{
    [Header("Meta XR Passthrough")]
    [Tooltip("右目用 PassthroughCameraAccess")]
    public PassthroughCameraAccess rightEyePca;

    [Tooltip("HMD（CenterEyeAnchor など）")]
    public Transform hmdTransform;

    [Header("ArUco Settings")]
    [Tooltip("マーカー一辺の実寸 (m)")]
    public float markerSizeMeters = 0.08f;

    [Tooltip("使用する辞書 (Objdetect.DICT_4X4_50 など)")]
    public int dictionaryId = Objdetect.DICT_4X4_50;

    [Tooltip("追跡したいマーカーID（-1 で最初に見つかったもの）")]
    public int targetMarkerId = -1;

    [Header("World Anchors")]
    [Tooltip("ワールド空間で マーカー面中心＋姿勢を表すアンカー")]
    public Transform qrAnchorWorld;

    [Tooltip("実物キューブに対応する Cube（qrAnchorWorld の子）")]
    public Transform cubeWorld;

    [Tooltip("HMD を親にして、HMD 基準の CubeTransform を格納する先（O_real）")]
    public Transform cubeRelativeToHmd;

    [Header("Debug UI")]
    public TMP_Text statusText;
    public RawImage preview;

    [Header("Performance Settings")]
    [Tooltip("マーカー検出の実行間隔 [秒]（例: 0.1 で 10Hz）")]
    public float processInterval = 0.1f;

    [Tooltip("ステータス表示更新間隔 [秒]")]
    public float statusUpdateInterval = 0.3f;

    [Tooltip("検出用に縮小するスケール（0.5 なら 1/2 解像度）")]
    [Range(0.25f, 1.0f)]
    public float downscale = 0.7f;

    // ---- 時間管理 ----
    float _lastProcessTime;
    float _lastStatusTime;

    // ---- GPU→CPU→OpenCV バッファ ----
    Texture2D _cpuTex;
    RenderTexture _rt;
    Mat _rgbaMat;      // フル解像度 RGBA
    Mat _grayMat;      // フル解像度 Gray
    Mat _graySmall;    // 縮小 Gray

    // ---- ArUco 検出用 ----
    OpenCVForUnity.ObjdetectModule.Dictionary _arucoDict;
    ArucoDetector _arucoDetector;
    Mat _markerIds;
    readonly List<Mat> _markerCorners = new List<Mat>();
    readonly List<Mat> _rejectedCorners = new List<Mat>();

    // ---- カメラ内部パラメータ ----
    Mat _cameraMatrixFull;
    Mat _cameraMatrixSmall;
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;

    // ---- 一時配列（毎フレーム new を避ける）----
    float[] _cornerBuf; // 8要素（x0,y0,...,x3,y3）

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA"
            });
        }
#endif

        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) が未設定です。");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        // 歪みは一旦ゼロ扱い（必要ならキャリブレーション値を入れる）
        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

        // --- ArUco 初期化 ---
        _arucoDict = Objdetect.getPredefinedDictionary(dictionaryId);
        _arucoDetector = new ArucoDetector(_arucoDict);
        _markerIds = new Mat();

        // Cube の親子・スケール設定（ワールド用）
        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * markerSizeMeters;
            cubeWorld.localPosition = new Vector3(0f, 0f, markerSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        // HMD 基準 Transform 用：親を HMD にしておく（ここが O_real）
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null)
        {
            SetStatus("Error: PCA is null");
            return;
        }

        if (!rightEyePca.IsPlaying)
        {
            SetStatus("PCA: not playing");
            return;
        }

        if (!rightEyePca.IsUpdatedThisFrame)
            return;

        if (Time.time - _lastProcessTime < processInterval)
            return;
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null)
        {
            SetStatus("PCA: texture is null");
            return;
        }

        EnsureBuffers(camTex.width, camTex.height);

        // GPU → RT → CPU Texture
        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        // CPU Texture → OpenCV Mat
        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        // Intrinsics 構築（まだなら一度だけ）
        if (!_intrinsicsReady)
        {
            BuildCameraMatricesFromIntrinsics(_cpuTex.width, _cpuTex.height);
            if (!_intrinsicsReady)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
        }

        RunArucoDetectionAndPnP(_rgbaMat);

        if (preview != null)
        {
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta = new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    void RunArucoDetectionAndPnP(Mat rgbaMat)
    {
        if (rgbaMat == null || rgbaMat.empty()) return;

        // RGBA → Gray
        if (_grayMat == null || _grayMat.cols() != rgbaMat.cols() || _grayMat.rows() != rgbaMat.rows())
        {
            _grayMat?.Dispose();
            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);
        }
        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

        // Downscale gray
        if (_graySmall == null)
        {
            int sw = Mathf.Max(1, Mathf.RoundToInt(_grayMat.cols() * downscale));
            int sh = Mathf.Max(1, Mathf.RoundToInt(_grayMat.rows() * downscale));
            _graySmall = new Mat(sh, sw, CvType.CV_8UC1);
        }
        Imgproc.resize(_grayMat, _graySmall, new Size(_graySmall.cols(), _graySmall.rows()));

        // 前回の corners を破棄してクリア
        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);
        _markerIds.release();

        // ArUco 検出
        _arucoDetector.detectMarkers(_graySmall, _markerCorners, _markerIds, _rejectedCorners);

        if (_markerIds == null || _markerIds.empty() || _markerIds.rows() <= 0 || _markerCorners.Count <= 0)
        {
            SetStatus("AruCo: none");
            return;
        }

        // 追跡するマーカーを選ぶ
        int useIndex = 0;
        int useId = (int)_markerIds.get(0, 0)[0];

        if (targetMarkerId >= 0)
        {
            bool found = false;
            for (int r = 0; r < _markerIds.rows(); r++)
            {
                int id = (int)_markerIds.get(r, 0)[0];
                if (id == targetMarkerId)
                {
                    useIndex = r;
                    useId = id;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                SetStatus($"AruCo: target id {targetMarkerId} not found");
                return;
            }
        }

        // corners（縮小画像座標系）を取得：通常 4点×2ch=8
        Mat cornerMat = _markerCorners[useIndex];
        int need = (int)(cornerMat.total() * cornerMat.channels());
        if (_cornerBuf == null || _cornerBuf.Length < need) _cornerBuf = new float[need];

        cornerMat.get(0, 0, _cornerBuf);
        if (need < 8)
        {
            SetStatus("AruCo: invalid corners");
            return;
        }

        // corners は (TL, TR, BR, BL) の並びを想定
        Point[] imagePoints = new Point[]
        {
            new Point(_cornerBuf[0], _cornerBuf[1]),
            new Point(_cornerBuf[2], _cornerBuf[3]),
            new Point(_cornerBuf[4], _cornerBuf[5]),
            new Point(_cornerBuf[6], _cornerBuf[7]),
        };

        // 3D object points（マーカー平面、中心原点）
        float s = markerSizeMeters;
        Point3[] objectPoints = new Point3[]
        {
            new Point3(-s/2f,  s/2f, 0),
            new Point3( s/2f,  s/2f, 0),
            new Point3( s/2f, -s/2f, 0),
            new Point3(-s/2f, -s/2f, 0),
        };

        using (MatOfPoint2f imagePtsMat = new MatOfPoint2f())
        using (MatOfPoint3f objectPtsMat = new MatOfPoint3f())
        using (Mat rvec = new Mat())
        using (Mat tvec = new Mat())
        {
            imagePtsMat.fromArray(imagePoints);
            objectPtsMat.fromArray(objectPoints);

            bool ok = Calib3d.solvePnP(
                objectPtsMat,
                imagePtsMat,
                _cameraMatrixSmall,   // 縮小画像用の K
                _distCoeffs,
                rvec,
                tvec,
                false,
                Calib3d.SOLVEPNP_IPPE_SQUARE);

            if (!ok)
            {
                SetStatus("PnP: fail");
                return;
            }

            double[] t = new double[3];
            tvec.get(0, 0, t);
            SetStatus($"AruCo id={useId}  z={t[2]:F2} m");

            UpdateTransformsFromPnP(rvec, tvec);
        }
    }

    void UpdateTransformsFromPnP(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // --- 右目カメラローカル位置（OpenCV → Unity） ---
        double[] t = new double[3];
        tvec.get(0, 0, t);

        Vector3 posCamLocal = new Vector3(
            (float)t[0],
            (float)(-t[1]),
            (float)t[2]
        );

        // --- 回転（OpenCV → Unity） ---
        Mat R_cv = new Mat(3, 3, CvType.CV_64F);
        Calib3d.Rodrigues(rvec, R_cv);

        double[] r = new double[9];
        R_cv.get(0, 0, r);

        // ★ここが重要：安全に y 反転を入れる（S * R * S）
        Matrix4x4 Rcv = Matrix4x4.identity;
        Rcv.m00 = (float)r[0]; Rcv.m01 = (float)r[1]; Rcv.m02 = (float)r[2];
        Rcv.m10 = (float)r[3]; Rcv.m11 = (float)r[4]; Rcv.m12 = (float)r[5];
        Rcv.m20 = (float)r[6]; Rcv.m21 = (float)r[7]; Rcv.m22 = (float)r[8];

        Matrix4x4 S = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
        Matrix4x4 Ru = S * Rcv * S;

        Vector3 forward = Ru.GetColumn(2);
        Vector3 up = Ru.GetColumn(1);
        Quaternion rotCamLocal = Quaternion.LookRotation(forward, up);

        // --- 右目カメラのワールドPoseを取得 ---
        Pose camWorldPose = rightEyePca.GetCameraPose();

        Vector3 cubeWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
        Quaternion cubeWorldRot = camWorldPose.rotation * rotCamLocal;

        // 1. ワールド空間の Anchor/Cube を更新
        qrAnchorWorld.position = cubeWorldPos;
        qrAnchorWorld.rotation = cubeWorldRot;

        // 2. HMD基準の CubeTransform を更新（O_real）
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (cubeWorldPos - hmdTransform.position);
            Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * cubeWorldRot;

            cubeRelativeToHmd.localPosition = relPos;
            cubeRelativeToHmd.localRotation = relRot;
        }
    }

    void SetStatus(string msg)
    {
        if (statusText == null) return;
        if (Time.time - _lastStatusTime < statusUpdateInterval) return;
        _lastStatusTime = Time.time;
        statusText.text = msg;
    }

    void EnsureBuffers(int w, int h)
    {
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();
            _graySmall?.Dispose();

            _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
            _grayMat = new Mat(h, w, CvType.CV_8UC1);

            int sw = Mathf.Max(1, Mathf.RoundToInt(w * downscale));
            int sh = Mathf.Max(1, Mathf.RoundToInt(h * downscale));
            _graySmall = new Mat(sh, sw, CvType.CV_8UC1);

            Debug.Log($"[PCA Buffers] RT/Texture/Mats created: {w}x{h}, small={sw}x{sh}");

            // 解像度が変わった場合は cameraMatrix も作り直す
            _intrinsicsReady = false;
        }
    }

    // ★QR版との差が出やすい所なので「作り直しの核」
    // intr.SensorResolution を基準にした K を、そのまま使うのではなく
    // "実際に処理しているテクスチャ解像度"（_cpuTex）へスケールしてから downscale を入れる
    void BuildCameraMatricesFromIntrinsics(int texW, int texH)
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        // intrの基準解像度 → 実テクスチャ解像度へのスケール
        double sx = (double)texW / intr.SensorResolution.x;
        double sy = (double)texH / intr.SensorResolution.y;

        double fx = intr.FocalLength.x * sx;
        double fy = intr.FocalLength.y * sy;
        double cx = intr.PrincipalPoint.x * sx;
        double cy = intr.PrincipalPoint.y * sy;

        _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx); _cameraMatrixFull.put(0, 1, 0); _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0); _cameraMatrixFull.put(1, 1, fy); _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0); _cameraMatrixFull.put(2, 1, 0); _cameraMatrixFull.put(2, 2, 1);

        // downscale後に合わせた K（= solvePnPに渡すやつ）
        double fxS = fx * downscale;
        double fyS = fy * downscale;
        double cxS = cx * downscale;
        double cyS = cy * downscale;

        _cameraMatrixSmall = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixSmall.put(0, 0, fxS); _cameraMatrixSmall.put(0, 1, 0); _cameraMatrixSmall.put(0, 2, cxS);
        _cameraMatrixSmall.put(1, 0, 0); _cameraMatrixSmall.put(1, 1, fyS); _cameraMatrixSmall.put(1, 2, cyS);
        _cameraMatrixSmall.put(2, 0, 0); _cameraMatrixSmall.put(2, 1, 0); _cameraMatrixSmall.put(2, 2, 1);

        _intrinsicsReady = true;

        Debug.Log($"[PCA] Intrinsics ready. SensorRes={intr.SensorResolution.x}x{intr.SensorResolution.y} Tex={texW}x{texH} downscale={downscale}");
    }

    static void ClearMatList(List<Mat> list)
    {
        for (int i = 0; i < list.Count; i++)
            list[i]?.Dispose();
        list.Clear();
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _graySmall?.Dispose();

        _markerIds?.Dispose();
        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);

        _cameraMatrixFull?.Dispose();
        _cameraMatrixSmall?.Dispose();
        _distCoeffs?.Dispose();

        _arucoDetector?.Dispose();
        _arucoDict?.Dispose();
    }
}
*/



/*
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Meta XR Passthrough Camera API
using Meta.XR;

// OpenCV for Unity
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using Rect = UnityEngine.Rect;

/// <summary>
/// Quest 3 右目 Passthrough カメラから画像を取得し、
/// OpenCV で QR 検出 + PnP 姿勢推定を行って、
///
/// - qrAnchorWorld : ワールド空間の QR 面中心 + 姿勢
/// - cubeWorld     : 実物キューブに対応する 3D Cube（qrAnchorWorld の子）
/// - cubeRelativeToHmd : HMD を親にした「実物キューブの実位置 O_real」
///
/// を更新する。
/// ※QRが見えなくなっても「ワールド固定」を維持する（last world pose を保持して再投影）
/// </summary>
public class QuestRightEyeQrTracker : MonoBehaviour
{
    [Header("Meta XR Passthrough")]
    [Tooltip("右目用 PassthroughCameraAccess")]
    public PassthroughCameraAccess rightEyePca;

    [Tooltip("HMD（CenterEyeAnchor など）")]
    public Transform hmdTransform;

    [Header("QR Settings")]
    [Tooltip("QRコード一辺の実寸 (m)")]
    public float qrSizeMeters = 0.08f;

    [Header("World Anchors")]
    [Tooltip("ワールド空間で QR 面中心＋姿勢を表すアンカー")]
    public Transform qrAnchorWorld;

    [Tooltip("実物キューブに対応する Cube（qrAnchorWorld の子）")]
    public Transform cubeWorld;

    [Tooltip("HMD を親にして HMD 基準の CubeTransform を格納する先（O_real）")]
    public Transform cubeRelativeToHmd;

    [Header("Debug UI")]
    public TMP_Text statusText;
    public RawImage preview;

    [Header("Performance Settings")]
    [Tooltip("QR検出の実行間隔 [秒]（例: 0.1 で 10Hz）")]
    public float processInterval = 0.1f;

    [Tooltip("ステータス表示更新間隔 [秒]")]
    public float statusUpdateInterval = 0.3f;

    [Tooltip("QR検出用に縮小するスケール（0.7 なら 70% 解像度）")]
    [Range(0.25f, 1.0f)]
    public float downscale = 0.7f;

    [Header("Tracking Hold")]
    [Tooltip("QRが見えない間も最後のワールド姿勢を保持してワールド固定を維持する")]
    public bool holdLastWorldPoseWhenQrLost = true;

    // ---- 時間管理 ----
    float _lastProcessTime;
    float _lastStatusTime;

    // ---- GPU/CPU/OpenCV バッファ ----
    Texture2D _cpuTex;
    RenderTexture _rt;
    Mat _rgbaMat;      // フル解像度 RGBA
    Mat _grayMat;      // フル解像度 Gray
    Mat _graySmall;    // 縮小 Gray

    // ---- QR 検出 ----
    Mat _points;
    readonly List<string> _decodedInfo = new List<string>();
    readonly List<Mat> _straightQrcode = new List<Mat>();
    QRCodeDetector _detector;

    // ---- カメラ内部パラメータ ----
    Mat _cameraMatrixFull;
    Mat _cameraMatrixSmall;
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;

    // ---- 一時配列（GC削減）----
    float[] _qrCodeCornersBuf;

    // ---- 追加：最後に得た「cube のワールド姿勢」を保持 ----
    bool _hasLastCubeWorldPose = false;
    Vector3 _lastCubeWorldPos;
    Quaternion _lastCubeWorldRot = Quaternion.identity;

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA"
            });
        }
#endif

        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) が未設定です。");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        _detector = new QRCodeDetector();
        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

        // Cube の親子・スケール設定（ワールド用）
        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * qrSizeMeters;
            // QR平面(z=0) を Cube 前面中心に合わせる
            cubeWorld.localPosition = new Vector3(0f, 0f, qrSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        // HMD 基準 Transform（O_real）
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null)
        {
            SetStatus("Error: PCA is null");
            return;
        }

        if (!rightEyePca.IsPlaying)
        {
            SetStatus("PCA: not playing");
            return;
        }

        if (!rightEyePca.IsUpdatedThisFrame)
        {
            return;
        }

        // QR処理は一定間隔でのみ実行（軽量化）
        if (Time.time - _lastProcessTime < processInterval)
        {
            return;
        }
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null)
        {
            SetStatus("PCA: texture is null");
            return;
        }

        EnsureBuffers(camTex.width, camTex.height);

        // GPU → RT → CPU Texture
        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        // CPU Texture → OpenCV Mat（フル）
        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        // Intrinsics 構築（未なら一度だけ）
        if (!_intrinsicsReady)
        {
            BuildCameraMatricesFromIntrinsics();
            if (!_intrinsicsReady)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
        }

        // QR 検出 + PnP（成功時のみ last world pose 更新）
        RunQrDetectionAndPnP(_rgbaMat);

        // デバッグプレビュー
        if (preview != null)
        {
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta = new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    /// <summary>
    /// 毎フレーム、最後に得た「ワールド姿勢」をHMDローカルに再投影して
    /// cubeRelativeToHmd の world pose が固定されるようにする。
    /// （QRが見えない間も頭に貼り付かない）
    /// </summary>
    void LateUpdate()
    {
        if (!holdLastWorldPoseWhenQrLost) return;
        if (!_hasLastCubeWorldPose) return;
        if (cubeRelativeToHmd == null || hmdTransform == null) return;

        Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (_lastCubeWorldPos - hmdTransform.position);
        Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * _lastCubeWorldRot;

        cubeRelativeToHmd.localPosition = relPos;
        cubeRelativeToHmd.localRotation = relRot;
    }

    // ----------------------------------------------------------------------
    // QR detection + PnP
    // ----------------------------------------------------------------------

    void RunQrDetectionAndPnP(Mat rgbaMat)
    {
        if (rgbaMat == null || rgbaMat.empty()) return;

        // RGBA → Gray（フル）
        if (_grayMat == null || _grayMat.cols() != rgbaMat.cols() || _grayMat.rows() != rgbaMat.rows())
        {
            _grayMat?.Dispose();
            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);
        }
        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

        // 縮小 Gray
        if (_graySmall == null)
        {
            int sw = Mathf.Max(1, Mathf.RoundToInt(_grayMat.cols() * downscale));
            int sh = Mathf.Max(1, Mathf.RoundToInt(_grayMat.rows() * downscale));
            _graySmall = new Mat(sh, sw, CvType.CV_8UC1);
        }
        Imgproc.resize(_grayMat, _graySmall, new Size(_graySmall.cols(), _graySmall.rows()));

        // QR 検出
        if (_points == null) _points = new Mat();
        _decodedInfo.Clear();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();

        bool found = _detector.detectAndDecodeMulti(_graySmall, _decodedInfo, _points, _straightQrcode);
        if (!found || _points.empty())
        {
            SetStatus("QR: none");
            return;
        }

        int total = (int)(_points.total() * _points.channels());
        if (_qrCodeCornersBuf == null || _qrCodeCornersBuf.Length < total)
        {
            _qrCodeCornersBuf = new float[total];
        }
        _points.get(0, 0, _qrCodeCornersBuf);

        // ここでは 1個目のQRだけ使う
        if (total < 8)
        {
            SetStatus("QR: invalid points");
            return;
        }

        int i = 0;
        Point[] imagePoints = new Point[]
        {
            new Point(_qrCodeCornersBuf[i + 0], _qrCodeCornersBuf[i + 1]),
            new Point(_qrCodeCornersBuf[i + 2], _qrCodeCornersBuf[i + 3]),
            new Point(_qrCodeCornersBuf[i + 4], _qrCodeCornersBuf[i + 5]),
            new Point(_qrCodeCornersBuf[i + 6], _qrCodeCornersBuf[i + 7]),
        };

        // 3D object points（QR平面・中心原点）
        float s = qrSizeMeters;
        Point3[] objectPoints = new Point3[]
        {
            new Point3(-s/2f,  s/2f, 0),
            new Point3( s/2f,  s/2f, 0),
            new Point3( s/2f, -s/2f, 0),
            new Point3(-s/2f, -s/2f, 0),
        };

        using (MatOfPoint2f imagePtsMat = new MatOfPoint2f())
        using (MatOfPoint3f objectPtsMat = new MatOfPoint3f())
        using (Mat rvec = new Mat())
        using (Mat tvec = new Mat())
        {
            imagePtsMat.fromArray(imagePoints);
            objectPtsMat.fromArray(objectPoints);

            bool ok = Calib3d.solvePnP(
                objectPtsMat,
                imagePtsMat,
                _cameraMatrixSmall,
                _distCoeffs,
                rvec,
                tvec,
                false,
                Calib3d.SOLVEPNP_IPPE_SQUARE);

            if (!ok)
            {
                SetStatus("PnP: fail");
                return;
            }

            double[] t = new double[3];
            tvec.get(0, 0, t);
            SetStatus($"PnP: OK  z={t[2]:F2} m");

            UpdateTransformsFromPnP(rvec, tvec);
        }
    }

    void UpdateTransformsFromPnP(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // --- カメラローカル位置（OpenCV → Unity）---
        double[] t = new double[3];
        tvec.get(0, 0, t);

        Vector3 posCamLocal = new Vector3(
            (float)t[0],
            (float)(-t[1]),
            (float)t[2]
        );

        // --- 回転（OpenCV → Unity）---
        Mat R_cv = new Mat(3, 3, CvType.CV_64F);
        Calib3d.Rodrigues(rvec, R_cv);
        double[] r = new double[9];
        R_cv.get(0, 0, r);

        Matrix4x4 m = Matrix4x4.identity;
        m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
        m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
        m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];

        Vector3 forward = m.GetColumn(2);
        Vector3 up = m.GetColumn(1);
        Quaternion rotCamLocal = Quaternion.LookRotation(forward, up);

        // --- 右目カメラのワールドPose ---
        Pose camWorldPose = rightEyePca.GetCameraPose();

        Vector3 cubeWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
        Quaternion cubeWorldRot = camWorldPose.rotation * rotCamLocal;

        // 1) ワールド空間アンカー更新（QRが見えた時だけ更新される＝ロスト時は最後の姿勢で止まる）
        qrAnchorWorld.position = cubeWorldPos;
        qrAnchorWorld.rotation = cubeWorldRot;

        // 2) last world pose を保存（これが「ロスト中のワールド固定」の基準）
        _lastCubeWorldPos = cubeWorldPos;
        _lastCubeWorldRot = cubeWorldRot;
        _hasLastCubeWorldPose = true;

        // 3) このフレームでも一応更新（LateUpdateでも再投影される）
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (cubeWorldPos - hmdTransform.position);
            Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * cubeWorldRot;

            cubeRelativeToHmd.localPosition = relPos;
            cubeRelativeToHmd.localRotation = relRot;
        }
    }

    // ----------------------------------------------------------------------
    // Helper
    // ----------------------------------------------------------------------

    void SetStatus(string msg)
    {
        if (statusText == null) return;
        if (Time.time - _lastStatusTime < statusUpdateInterval) return;
        _lastStatusTime = Time.time;
        statusText.text = msg;
    }

    void EnsureBuffers(int w, int h)
    {
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();
            _graySmall?.Dispose();

            _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
            _grayMat = new Mat(h, w, CvType.CV_8UC1);

            int sw = Mathf.Max(1, Mathf.RoundToInt(w * downscale));
            int sh = Mathf.Max(1, Mathf.RoundToInt(h * downscale));
            _graySmall = new Mat(sh, sw, CvType.CV_8UC1);

            Debug.Log($"[PCA Buffers] RT/Texture/Mats created: {w}x{h}, small={sw}x{sh}");
            _intrinsicsReady = false;
        }
    }

    void BuildCameraMatricesFromIntrinsics()
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        double fx = intr.FocalLength.x;
        double fy = intr.FocalLength.y;
        double cx = intr.PrincipalPoint.x;
        double cy = intr.PrincipalPoint.y;

        _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx);
        _cameraMatrixFull.put(0, 1, 0);
        _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0);
        _cameraMatrixFull.put(1, 1, fy);
        _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0);
        _cameraMatrixFull.put(2, 1, 0);
        _cameraMatrixFull.put(2, 2, 1);

        int fullW = (int)intr.SensorResolution.x;
        int fullH = (int)intr.SensorResolution.y;
        int smallW = Mathf.Max(1, Mathf.RoundToInt(fullW * downscale));
        int smallH = Mathf.Max(1, Mathf.RoundToInt(fullH * downscale));

        double scaleX = (double)smallW / fullW;
        double scaleY = (double)smallH / fullH;

        _cameraMatrixSmall = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixSmall.put(0, 0, fx * scaleX);
        _cameraMatrixSmall.put(0, 1, 0);
        _cameraMatrixSmall.put(0, 2, cx * scaleX);
        _cameraMatrixSmall.put(1, 0, 0);
        _cameraMatrixSmall.put(1, 1, fy * scaleY);
        _cameraMatrixSmall.put(1, 2, cy * scaleY);
        _cameraMatrixSmall.put(2, 0, 0);
        _cameraMatrixSmall.put(2, 1, 0);
        _cameraMatrixSmall.put(2, 2, 1);

        _intrinsicsReady = true;
        Debug.Log("[PCA] Intrinsics ready.");
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _graySmall?.Dispose();
        _points?.Dispose();
        foreach (var m in _straightQrcode) m.Dispose();
        _straightQrcode.Clear();

        _cameraMatrixFull?.Dispose();
        _cameraMatrixSmall?.Dispose();
        _distCoeffs?.Dispose();
    }
}
*/




/*

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Meta.XR;

using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using Rect = UnityEngine.Rect;

public class QuestRightEyeArucoTracker_Rebuild : MonoBehaviour
{
    [Header("Meta XR Passthrough")]
    public PassthroughCameraAccess rightEyePca;
    public Transform hmdTransform;

    [Header("ArUco Settings")]
    public float markerSizeMeters = 0.08f;
    public int dictionaryId = Objdetect.DICT_4X4_50;
    public int targetMarkerId = -1;

    [Header("World Anchors")]
    public Transform qrAnchorWorld;
    public Transform cubeWorld;
    public Transform cubeRelativeToHmd;

    [Header("Debug UI")]
    public TMP_Text statusText;
    public RawImage preview;

    [Header("Performance Settings")]
    public float processInterval = 0.1f;
    public float statusUpdateInterval = 0.3f;
    [Range(0.25f, 1.0f)]
    public float downscale = 0.7f;

    [Header("Corner Refinement (recommended)")]
    public bool refineCornersOnFullRes = true;
    public int cornerSubPixWin = 5;   // 5〜7くらい
    public int cornerSubPixIters = 20;
    public double cornerSubPixEps = 0.03;

    float _lastProcessTime;
    float _lastStatusTime;

    Texture2D _cpuTex;
    RenderTexture _rt;
    Mat _rgbaMat;
    Mat _grayMat;
    Mat _graySmall;

    OpenCVForUnity.ObjdetectModule.Dictionary _arucoDict;
    ArucoDetector _arucoDetector;
    Mat _markerIds;
    readonly List<Mat> _markerCorners = new List<Mat>();
    readonly List<Mat> _rejectedCorners = new List<Mat>();

    Mat _cameraMatrixFull;
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;

    float[] _cornerBuf;

    int _smallW, _smallH;

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA"
            });
        }
#endif
        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) が未設定です。");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

        _arucoDict = Objdetect.getPredefinedDictionary(dictionaryId);
        _arucoDetector = new ArucoDetector(_arucoDict);
        _markerIds = new Mat();

        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * markerSizeMeters;
            cubeWorld.localPosition = new Vector3(0f, 0f, markerSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null) { SetStatus("Error: PCA is null"); return; }
        if (!rightEyePca.IsPlaying) { SetStatus("PCA: not playing"); return; }
        if (!rightEyePca.IsUpdatedThisFrame) return;

        if (Time.time - _lastProcessTime < processInterval) return;
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null) { SetStatus("PCA: texture is null"); return; }

        EnsureBuffers(camTex.width, camTex.height);

        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        if (!_intrinsicsReady)
        {
            BuildCameraMatrixFullFromIntrinsics(_cpuTex.width, _cpuTex.height);
            if (!_intrinsicsReady)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
        }

        RunArucoDetectionAndPnP(_rgbaMat);

        if (preview != null)
        {
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta = new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    void RunArucoDetectionAndPnP(Mat rgbaMat)
    {
        if (rgbaMat == null || rgbaMat.empty()) return;

        // RGBA -> Gray (full)
        if (_grayMat == null || _grayMat.cols() != rgbaMat.cols() || _grayMat.rows() != rgbaMat.rows())
        {
            _grayMat?.Dispose();
            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);
        }
        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

        // Resize to small for detection
        if (_graySmall == null)
        {
            _smallW = Mathf.Max(1, Mathf.RoundToInt(_grayMat.cols() * downscale));
            _smallH = Mathf.Max(1, Mathf.RoundToInt(_grayMat.rows() * downscale));
            _graySmall = new Mat(_smallH, _smallW, CvType.CV_8UC1);
        }
        Imgproc.resize(_grayMat, _graySmall, new Size(_graySmall.cols(), _graySmall.rows()));

        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);
        _markerIds.release();

        _arucoDetector.detectMarkers(_graySmall, _markerCorners, _markerIds, _rejectedCorners);

        if (_markerIds == null || _markerIds.empty() || _markerIds.rows() <= 0 || _markerCorners.Count <= 0)
        {
            SetStatus("AruCo: none");
            return;
        }

        // Select marker
        int useIndex = 0;
        int useId = (int)_markerIds.get(0, 0)[0];

        if (targetMarkerId >= 0)
        {
            bool found = false;
            for (int r = 0; r < _markerIds.rows(); r++)
            {
                int id = (int)_markerIds.get(r, 0)[0];
                if (id == targetMarkerId)
                {
                    useIndex = r;
                    useId = id;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                SetStatus($"AruCo: target id {targetMarkerId} not found");
                return;
            }
        }

        // corners from SMALL image
        Mat cornerMat = _markerCorners[useIndex];
        int need = (int)(cornerMat.total() * cornerMat.channels());
        if (_cornerBuf == null || _cornerBuf.Length < need) _cornerBuf = new float[need];

        cornerMat.get(0, 0, _cornerBuf);
        if (need < 8) { SetStatus("AruCo: invalid corners"); return; }

        // Scale corners up to FULL image coordinates
        double sx = (double)_grayMat.cols() / _graySmall.cols();
        double sy = (double)_grayMat.rows() / _graySmall.rows();

        Point[] ptsFull = new Point[]
        {
            new Point(_cornerBuf[0] * sx, _cornerBuf[1] * sy),
            new Point(_cornerBuf[2] * sx, _cornerBuf[3] * sy),
            new Point(_cornerBuf[4] * sx, _cornerBuf[5] * sy),
            new Point(_cornerBuf[6] * sx, _cornerBuf[7] * sy),
        };

        // Optional: cornerSubPix on FULL gray for better accuracy
        if (refineCornersOnFullRes)
        {
            using (MatOfPoint2f tmp = new MatOfPoint2f())
            {
                tmp.fromArray(ptsFull);
                TermCriteria criteria = new TermCriteria(TermCriteria.EPS + TermCriteria.MAX_ITER, cornerSubPixIters, cornerSubPixEps);
                Size win = new Size(cornerSubPixWin, cornerSubPixWin);
                Imgproc.cornerSubPix(_grayMat, tmp, win, new Size(-1, -1), criteria);
                ptsFull = tmp.toArray();
            }
        }

        // Object points (marker plane centered at origin)
        float s = markerSizeMeters;
        Point3[] objectPoints = new Point3[]
        {
            new Point3(-s/2f,  s/2f, 0),
            new Point3( s/2f,  s/2f, 0),
            new Point3( s/2f, -s/2f, 0),
            new Point3(-s/2f, -s/2f, 0),
        };

        using (MatOfPoint2f imagePtsMat = new MatOfPoint2f())
        using (MatOfPoint3f objectPtsMat = new MatOfPoint3f())
        using (Mat rvec = new Mat())
        using (Mat tvec = new Mat())
        {
            imagePtsMat.fromArray(ptsFull);
            objectPtsMat.fromArray(objectPoints);

            // ★安定性優先：ITERATIVE（IPPE_SQUARE は跳ぶことがある）
            bool ok = Calib3d.solvePnP(
                objectPtsMat,
                imagePtsMat,
                _cameraMatrixFull,   // FULL image K
                _distCoeffs,
                rvec,
                tvec,
                false,
                Calib3d.SOLVEPNP_ITERATIVE);

            if (!ok)
            {
                SetStatus("PnP: fail");
                return;
            }

            double[] t = new double[3];
            tvec.get(0, 0, t);
            SetStatus($"AruCo id={useId}  z={t[2]:F2} m");

            UpdateTransformsFromPnP(rvec, tvec);
        }
    }

    void UpdateTransformsFromPnP(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // tvec: OpenCV cam coords (x right, y down, z forward) -> Unity cam coords (x right, y up, z forward)
        double[] t = new double[3];
        tvec.get(0, 0, t);

        Vector3 posCamLocal = new Vector3(
            (float)t[0],
            (float)(-t[1]),
            (float)t[2]
        );

        // rvec -> R
        using (Mat R_cv = new Mat(3, 3, CvType.CV_64F))
        {
            Calib3d.Rodrigues(rvec, R_cv);
            double[] r = new double[9];
            R_cv.get(0, 0, r);

            Matrix4x4 Rcv = Matrix4x4.identity;
            Rcv.m00 = (float)r[0]; Rcv.m01 = (float)r[1]; Rcv.m02 = (float)r[2];
            Rcv.m10 = (float)r[3]; Rcv.m11 = (float)r[4]; Rcv.m12 = (float)r[5];
            Rcv.m20 = (float)r[6]; Rcv.m21 = (float)r[7]; Rcv.m22 = (float)r[8];

            // basis change with y flip: Ru = S * Rcv * S
            Matrix4x4 S = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
            Matrix4x4 Ru = S * Rcv * S;

            Vector3 forward = Ru.GetColumn(2);
            Vector3 up = Ru.GetColumn(1);
            Quaternion rotCamLocal = Quaternion.LookRotation(forward, up);

            Pose camWorldPose = rightEyePca.GetCameraPose();

            Vector3 cubeWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
            Quaternion cubeWorldRot = camWorldPose.rotation * rotCamLocal;

            qrAnchorWorld.position = cubeWorldPos;
            qrAnchorWorld.rotation = cubeWorldRot;

            if (cubeRelativeToHmd != null && hmdTransform != null)
            {
                Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (cubeWorldPos - hmdTransform.position);
                Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * cubeWorldRot;

                cubeRelativeToHmd.localPosition = relPos;
                cubeRelativeToHmd.localRotation = relRot;
            }
        }
    }

    void SetStatus(string msg)
    {
        if (statusText == null) return;
        if (Time.time - _lastStatusTime < statusUpdateInterval) return;
        _lastStatusTime = Time.time;
        statusText.text = msg;
    }

    void EnsureBuffers(int w, int h)
    {
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();
            _graySmall?.Dispose();

            _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
            _grayMat = new Mat(h, w, CvType.CV_8UC1);

            _smallW = Mathf.Max(1, Mathf.RoundToInt(w * downscale));
            _smallH = Mathf.Max(1, Mathf.RoundToInt(h * downscale));
            _graySmall = new Mat(_smallH, _smallW, CvType.CV_8UC1);

            Debug.Log($"[PCA Buffers] {w}x{h}, small={_smallW}x{_smallH}");
            _intrinsicsReady = false;
        }
    }

    // intr.SensorResolution 基準の intrinsics を、実テクスチャ解像度(texW,texH)へスケールした FULL K を作る
    void BuildCameraMatrixFullFromIntrinsics(int texW, int texH)
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        double sx = (double)texW / intr.SensorResolution.x;
        double sy = (double)texH / intr.SensorResolution.y;

        double fx = intr.FocalLength.x * sx;
        double fy = intr.FocalLength.y * sy;
        double cx = intr.PrincipalPoint.x * sx;
        double cy = intr.PrincipalPoint.y * sy;

        _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx); _cameraMatrixFull.put(0, 1, 0); _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0); _cameraMatrixFull.put(1, 1, fy); _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0); _cameraMatrixFull.put(2, 1, 0); _cameraMatrixFull.put(2, 2, 1);

        _intrinsicsReady = true;
        Debug.Log($"[PCA] Intrinsics ready. SensorRes={intr.SensorResolution.x}x{intr.SensorResolution.y} Tex={texW}x{texH}");
    }

    static void ClearMatList(List<Mat> list)
    {
        for (int i = 0; i < list.Count; i++) list[i]?.Dispose();
        list.Clear();
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _graySmall?.Dispose();

        _markerIds?.Dispose();
        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);

        _cameraMatrixFull?.Dispose();
        _distCoeffs?.Dispose();

        _arucoDetector?.Dispose();
        _arucoDict?.Dispose();
    }
}
*/


/*
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Meta.XR;

using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using Rect = UnityEngine.Rect;

public class QuestRightEyeArucoTracker_DebugFull : MonoBehaviour
{
    [Header("Meta XR Passthrough")]
    public PassthroughCameraAccess rightEyePca;
    public Transform hmdTransform;

    [Header("ArUco Settings")]
    public float markerSizeMeters = 0.08f;
    public int dictionaryId = Objdetect.DICT_4X4_50;
    public int targetMarkerId = -1;

    [Header("World Anchors")]
    public Transform qrAnchorWorld;       // マーカーの絶対位置（ワールド）
    public Transform cubeWorld;           // デバッグ用Cube（qrAnchorWorld の子）
    public Transform cubeRelativeToHmd;   // HMDローカル（O_real）

    [Header("Debug UI")]
    public TMP_Text statusText;
    public RawImage preview;

    [Header("Update Rate")]
    public float processInterval = 0.05f;
    public float statusUpdateInterval = 0.3f;

    [Header("Corner Refinement")]
    public bool refineCornersOnFullRes = true;
    public int cornerSubPixWin = 5;
    public int cornerSubPixIters = 20;
    public double cornerSubPixEps = 0.03;

    [Header("PnP Strategy (IMPORTANT)")]
    [Tooltip("IPPE_SQUARE の複数解を solvePnPGeneric で取り出して、連続性＋再投影誤差で選ぶ（推奨ON）")]
    public bool useSolvePnPGeneric = true;

    [Tooltip("候補解選択で連続性を使う（推奨ON）")]
    public bool preferTemporalContinuity = true;

    [Tooltip("前フレーム連続性の重み（大きいほど『飛び』を嫌う）")]
    public float continuityWeight = 3.0f;

    [Tooltip("回転連続性の重み（rvec差の近似。大きいほど回転の飛びを嫌う）")]
    public float continuityRotWeight = 0.3f;

    [Header("Intrinsics / Image Convention (CRITICAL)")]
    [Tooltip("Intrinsicsが『GetTexture()解像度基準』ならON。『センサー解像度基準』ならOFF（OFFだと内部でスケールする）")]
    public bool intrinsicsInTextureSpace = true;

    [Tooltip("ReadPixels/表示系で上下反転している場合にON（まずはOFF推奨。オーバーレイで判定）")]
    public bool flipImageVerticallyBeforeDetect = false;

    [Header("Transform Convention")]
    [Tooltip("QR版と同じ OpenCV->Unity 回転変換（推奨）")]
    public bool useQrStyleRotationConversion = true;

    [Header("Hold & Filter")]
    public bool holdLastWorldPoseWhenMarkerLost = true;

    [Range(0.01f, 1.0f)]
    public float positionFilterFactor = 0.35f;

    [Range(0.01f, 1.0f)]
    public float rotationFilterFactor = 0.25f;

    [Header("Overlay Debug")]
    public bool drawOverlay = true;
    public bool showReprojectionError = true;

    // ---- internal ----
    float _lastProcessTime;
    float _lastStatusTime;

    Texture2D _cpuTex;
    RenderTexture _rt;

    Mat _rgbaMat;
    Mat _grayMat;

    OpenCVForUnity.ObjdetectModule.Dictionary _arucoDict;
    ArucoDetector _arucoDetector;

    Mat _markerIds;
    readonly List<Mat> _markerCorners = new List<Mat>();
    readonly List<Mat> _rejectedCorners = new List<Mat>();

    // solvePnPGeneric outputs
    readonly List<Mat> _rvecCandidates = new List<Mat>();
    readonly List<Mat> _tvecCandidates = new List<Mat>();

    Mat _cameraMatrixFull;
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;

    float[] _cornerBuf;

    // tracking state (OpenCV rvec/tvec)
    Mat _lastRvec;
    Mat _lastTvec;
    bool _hasLastTracking = false;

    // world pose hold / filter
    bool _hasLastCubeWorldPose = false;
    Vector3 _lastCubeWorldPos;
    Quaternion _lastCubeWorldRot = Quaternion.identity;

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA"
            });
        }
#endif
        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) is not set.");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        // NOTE: 本来は歪み係数が欲しい。まずは0で。
        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

        _arucoDict = Objdetect.getPredefinedDictionary(dictionaryId);
        _arucoDetector = new ArucoDetector(_arucoDict);

        _markerIds = new Mat();

        _lastRvec = new Mat(3, 1, CvType.CV_64F);
        _lastTvec = new Mat(3, 1, CvType.CV_64F);

        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * markerSizeMeters;
            cubeWorld.localPosition = new Vector3(0f, 0f, markerSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null) { SetStatus("Error: PCA is null"); return; }
        if (!rightEyePca.IsPlaying) { SetStatus("PCA: not playing"); return; }
        if (!rightEyePca.IsUpdatedThisFrame) return;

        if (Time.time - _lastProcessTime < processInterval) return;
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null) { SetStatus("PCA: texture is null"); return; }

        EnsureBuffers(camTex.width, camTex.height);

        // GPU -> CPU
        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        if (!_intrinsicsReady)
        {
            BuildCameraMatrixFromIntrinsics(_cpuTex.width, _cpuTex.height);
            if (!_intrinsicsReady)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
        }

        RunArucoDetectionAndPose(_rgbaMat);

        // preview
        if (preview != null)
        {
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta = new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    void LateUpdate()
    {
        if (!holdLastWorldPoseWhenMarkerLost) return;
        if (!_hasLastCubeWorldPose) return;
        if (cubeRelativeToHmd == null || hmdTransform == null) return;

        Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (_lastCubeWorldPos - hmdTransform.position);
        Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * _lastCubeWorldRot;

        cubeRelativeToHmd.localPosition = relPos;
        cubeRelativeToHmd.localRotation = relRot;
    }

    void RunArucoDetectionAndPose(Mat rgbaMat)
    {
        if (rgbaMat == null || rgbaMat.empty()) return;

        // Gray
        EnsureGray(rgbaMat);

        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

        if (flipImageVerticallyBeforeDetect)
        {
            Core.flip(_grayMat, _grayMat, 0);
            if (drawOverlay) Core.flip(_rgbaMat, _rgbaMat, 0);
        }

        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);
        _markerIds.release();

        _arucoDetector.detectMarkers(_grayMat, _markerCorners, _markerIds, _rejectedCorners);

        if (_markerIds == null || _markerIds.empty() || _markerIds.rows() <= 0 || _markerCorners.Count <= 0)
        {
            SetStatus("AruCo: none");
            _hasLastTracking = false;
            return;
        }

        // select marker
        int useIndex = 0;
        int useId = (int)_markerIds.get(0, 0)[0];

        if (targetMarkerId >= 0)
        {
            bool found = false;
            for (int r = 0; r < _markerIds.rows(); r++)
            {
                int id = (int)_markerIds.get(r, 0)[0];
                if (id == targetMarkerId)
                {
                    useIndex = r;
                    useId = id;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                SetStatus($"Wait id={targetMarkerId}");
                _hasLastTracking = false;
                return;
            }
        }

        // corners
        Mat cornerMat = _markerCorners[useIndex];
        int need = (int)(cornerMat.total() * cornerMat.channels());
        if (_cornerBuf == null || _cornerBuf.Length < need) _cornerBuf = new float[need];
        cornerMat.get(0, 0, _cornerBuf);

        if (need < 8)
        {
            SetStatus("AruCo: invalid corners");
            _hasLastTracking = false;
            return;
        }

        Point[] pts = new Point[]
        {
            new Point(_cornerBuf[0], _cornerBuf[1]),
            new Point(_cornerBuf[2], _cornerBuf[3]),
            new Point(_cornerBuf[4], _cornerBuf[5]),
            new Point(_cornerBuf[6], _cornerBuf[7]),
        };

        // cornerSubPix (optional)
        if (refineCornersOnFullRes)
        {
            using (MatOfPoint2f tmp = new MatOfPoint2f(pts))
            {
                TermCriteria criteria = new TermCriteria(TermCriteria.EPS + TermCriteria.MAX_ITER, cornerSubPixIters, cornerSubPixEps);
                Size win = new Size(cornerSubPixWin, cornerSubPixWin);
                Imgproc.cornerSubPix(_grayMat, tmp, win, new Size(-1, -1), criteria);
                pts = tmp.toArray();
            }
        }

        // object points (TL,TR,BR,BL), z=0 plane, centered
        float s = markerSizeMeters;
        Point3[] objectPoints = new Point3[]
        {
            new Point3(-s/2f,  s/2f, 0),
            new Point3( s/2f,  s/2f, 0),
            new Point3( s/2f, -s/2f, 0),
            new Point3(-s/2f, -s/2f, 0),
        };

        // solve pose
        double err;
        bool ok = SolvePose_IPPE_Generic(objectPoints, pts, _cameraMatrixFull, _distCoeffs, out err);

        if (!ok)
        {
            SetStatus("PnP: fail");
            _hasLastTracking = false;
            return;
        }

        _hasLastTracking = true;

        // overlay: detected corners (green) + reproj (red)
        if (drawOverlay)
        {
            DrawCornersAndReproj(_rgbaMat, objectPoints, pts, _lastRvec, _lastTvec, _cameraMatrixFull, _distCoeffs);
            Utils.matToTexture2D(_rgbaMat, _cpuTex);
        }

        // status
        double z = _lastTvec.get(2, 0)[0];
        if (showReprojectionError)
            SetStatus($"id={useId}  z={z:F2}m  err={err:F1}px");
        else
            SetStatus($"id={useId}  z={z:F2}m");

        // apply
        UpdateTransformsFromPnP(_lastRvec, _lastTvec);

        // unflip back for preview consistency（flipしてる場合、表示が逆になるのが嫌ならここは消してOK）
        if (flipImageVerticallyBeforeDetect && drawOverlay)
        {
            // すでにTextureへ戻したので、ここで戻す必要はない
        }
    }

    // ------------------------------------------------------------
    // Pose solver: solvePnPGeneric(IPPE_SQUARE) + choose best
    // ------------------------------------------------------------
    bool SolvePose_IPPE_Generic(
        Point3[] objectPoints,
        Point[] imagePoints,
        Mat K,
        MatOfDouble dist,
        out double bestErr)
    {
        bestErr = double.PositiveInfinity;

        using (MatOfPoint2f img = new MatOfPoint2f(imagePoints))
        using (MatOfPoint3f obj = new MatOfPoint3f(objectPoints))
        {
            if (!useSolvePnPGeneric)
            {
                // single solvePnP fallback
                using (Mat rvec = new Mat())
                using (Mat tvec = new Mat())
                {
                    bool ok = Calib3d.solvePnP(obj, img, K, dist, rvec, tvec, false, Calib3d.SOLVEPNP_IPPE_SQUARE);
                    if (!ok) return false;

                    double z = tvec.get(2, 0)[0];
                    if (z <= 0) return false;

                    bestErr = showReprojectionError ? ComputeReprojectionErrorPx(obj, img, rvec, tvec, K, dist) : 0.0;
                    rvec.copyTo(_lastRvec);
                    tvec.copyTo(_lastTvec);
                    return true;
                }
            }

            ClearMatList(_rvecCandidates);
            ClearMatList(_tvecCandidates);

            int nsol = Calib3d.solvePnPGeneric(
                obj, img,
                K, dist,
                _rvecCandidates, _tvecCandidates,
                false,
                Calib3d.SOLVEPNP_IPPE_SQUARE);

            if (nsol <= 0 || _rvecCandidates.Count <= 0 || _tvecCandidates.Count <= 0)
                return false;

            int bestIdx = -1;
            double bestScore = double.PositiveInfinity;

            for (int i = 0; i < _rvecCandidates.Count; i++)
            {
                Mat rvec = _rvecCandidates[i];
                Mat tvec = _tvecCandidates[i];
                if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) continue;

                double z = tvec.get(2, 0)[0];
                if (z <= 0) continue;

                double err = showReprojectionError ? ComputeReprojectionErrorPx(obj, img, rvec, tvec, K, dist) : 0.0;

                double cont = 0.0;
                if (preferTemporalContinuity && _hasLastTracking)
                {
                    // position continuity in camera coords
                    double dx = tvec.get(0, 0)[0] - _lastTvec.get(0, 0)[0];
                    double dy = tvec.get(1, 0)[0] - _lastTvec.get(1, 0)[0];
                    double dz = tvec.get(2, 0)[0] - _lastTvec.get(2, 0)[0];
                    double dpos = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                    // rotation continuity (approx by rvec diff)
                    double drx = rvec.get(0, 0)[0] - _lastRvec.get(0, 0)[0];
                    double dry = rvec.get(1, 0)[0] - _lastRvec.get(1, 0)[0];
                    double drz = rvec.get(2, 0)[0] - _lastRvec.get(2, 0)[0];
                    double drot = Math.Sqrt(drx * drx + dry * dry + drz * drz);

                    cont = continuityWeight * dpos + continuityRotWeight * drot;
                }

                double score = err + cont;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestErr = err;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) return false;

            _rvecCandidates[bestIdx].copyTo(_lastRvec);
            _tvecCandidates[bestIdx].copyTo(_lastTvec);
            return true;
        }
    }

    // ------------------------------------------------------------
    // Reprojection error / overlay
    // ------------------------------------------------------------
    static double ComputeReprojectionErrorPx(
        MatOfPoint3f objectPts,
        MatOfPoint2f imagePts,
        Mat rvec,
        Mat tvec,
        Mat cameraMatrix,
        MatOfDouble distCoeffs)
    {
        using (MatOfPoint2f projected = new MatOfPoint2f())
        {
            Calib3d.projectPoints(objectPts, rvec, tvec, cameraMatrix, distCoeffs, projected);

            Point[] obs = imagePts.toArray();
            Point[] proj = projected.toArray();
            if (obs == null || proj == null || obs.Length != proj.Length || obs.Length == 0) return double.NaN;

            double err = 0.0;
            for (int i = 0; i < obs.Length; i++)
            {
                double dx = proj[i].x - obs[i].x;
                double dy = proj[i].y - obs[i].y;
                err += Math.Sqrt(dx * dx + dy * dy);
            }
            return err / obs.Length;
        }
    }

    void DrawCornersAndReproj(
        Mat rgba,
        Point3[] objectPoints,
        Point[] detectedPts,
        Mat rvec, Mat tvec,
        Mat K, MatOfDouble dist)
    {
        // detected (green)
        for (int i = 0; i < 4; i++)
            Imgproc.circle(rgba, detectedPts[i], 6, new Scalar(0, 255, 0, 255), 2);

        using (var obj = new MatOfPoint3f(objectPoints))
        using (var proj = new MatOfPoint2f())
        {
            Calib3d.projectPoints(obj, rvec, tvec, K, dist, proj);
            var p = proj.toArray();

            // reproj (red)
            for (int i = 0; i < 4; i++)
                Imgproc.circle(rgba, p[i], 4, new Scalar(255, 0, 0, 255), 2);
        }
    }

    // ------------------------------------------------------------
    // Apply pose to Unity
    // ------------------------------------------------------------
    void UpdateTransformsFromPnP(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // tvec: OpenCV cam coords (x right, y down, z forward)
        double[] t = new double[3];
        tvec.get(0, 0, t);

        // If we flipped the image vertically before detect, the "image y" is inverted.
        // But solvePnP uses the points we fed; so conversion here stays OpenCV->Unity as usual.
        Vector3 posCamLocal = new Vector3((float)t[0], (float)(-t[1]), (float)t[2]);

        // rvec -> R
        Mat R_cv = new Mat(3, 3, CvType.CV_64F);
        Calib3d.Rodrigues(rvec, R_cv);
        double[] r = new double[9];
        R_cv.get(0, 0, r);
        R_cv.Dispose();

        Quaternion rotCamLocal;
        if (useQrStyleRotationConversion)
        {
            // QR版と同じ符号変換
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
            m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
            m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];

            Vector3 forward = m.GetColumn(2);
            Vector3 up = m.GetColumn(1);
            if (forward == Vector3.zero || up == Vector3.zero) return;
            rotCamLocal = Quaternion.LookRotation(forward, up);
        }
        else
        {
            // S*R*S
            Matrix4x4 Rm = Matrix4x4.identity;
            Rm.m00 = (float)r[0]; Rm.m01 = (float)r[1]; Rm.m02 = (float)r[2];
            Rm.m10 = (float)r[3]; Rm.m11 = (float)r[4]; Rm.m12 = (float)r[5];
            Rm.m20 = (float)r[6]; Rm.m21 = (float)r[7]; Rm.m22 = (float)r[8];

            Matrix4x4 S = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
            Matrix4x4 Ru = S * Rm * S;

            Vector3 forward = Ru.GetColumn(2);
            Vector3 up = Ru.GetColumn(1);
            if (forward == Vector3.zero || up == Vector3.zero) return;
            rotCamLocal = Quaternion.LookRotation(forward, up);
        }

        Pose camWorldPose = rightEyePca.GetCameraPose();

        Vector3 newWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
        Quaternion newWorldRot = camWorldPose.rotation * rotCamLocal;

        // mild LPF in world
        if (_hasLastCubeWorldPose)
        {
            float dist = Vector3.Distance(_lastCubeWorldPos, newWorldPos);
            if (dist < 0.5f)
            {
                newWorldPos = Vector3.Lerp(_lastCubeWorldPos, newWorldPos, positionFilterFactor);
                newWorldRot = Quaternion.Slerp(_lastCubeWorldRot, newWorldRot, rotationFilterFactor);
            }
        }

        qrAnchorWorld.position = newWorldPos;
        qrAnchorWorld.rotation = newWorldRot;

        _lastCubeWorldPos = newWorldPos;
        _lastCubeWorldRot = newWorldRot;
        _hasLastCubeWorldPose = true;

        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (newWorldPos - hmdTransform.position);
            Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * newWorldRot;

            cubeRelativeToHmd.localPosition = relPos;
            cubeRelativeToHmd.localRotation = relRot;
        }
    }

    // ------------------------------------------------------------
    // Intrinsics
    // ------------------------------------------------------------
    void BuildCameraMatrixFromIntrinsics(int texW, int texH)
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        double fx = intr.FocalLength.x;
        double fy = intr.FocalLength.y;
        double cx = intr.PrincipalPoint.x;
        double cy = intr.PrincipalPoint.y;

        if (!intrinsicsInTextureSpace)
        {
            // Intrinsics が SensorResolution 基準なら、Texture解像度へスケール
            double sx = (double)texW / intr.SensorResolution.x;
            double sy = (double)texH / intr.SensorResolution.y;
            fx *= sx; fy *= sy; cx *= sx; cy *= sy;
        }

        // もし flipImageVerticallyBeforeDetect を ON にしていて、かつ K を「画像座標系」に合わせたいなら
        // cy = (texH - 1) - cy; も理屈上あり得るが、まずはオーバーレイで判断する方が安全。
        // （points 自体を flip して検出しているため、K をいじると二重補正になりやすい）

        if (_cameraMatrixFull != null) _cameraMatrixFull.Dispose();
        _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx); _cameraMatrixFull.put(0, 1, 0); _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0); _cameraMatrixFull.put(1, 1, fy); _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0); _cameraMatrixFull.put(2, 1, 0); _cameraMatrixFull.put(2, 2, 1);

        _intrinsicsReady = true;

        Debug.Log($"[Intrinsics] SensorRes={intr.SensorResolution.x}x{intr.SensorResolution.y} Tex={texW}x{texH} " +
                  $"K: fx={fx:F1} fy={fy:F1} cx={cx:F1} cy={cy:F1} (intrinsicsInTextureSpace={intrinsicsInTextureSpace})");
    }

    // ------------------------------------------------------------
    // Buffers / utils
    // ------------------------------------------------------------
    void EnsureBuffers(int w, int h)
    {
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();

            _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
            _grayMat = new Mat(h, w, CvType.CV_8UC1);

            _intrinsicsReady = false;
        }
    }

    void EnsureGray(Mat rgbaMat)
    {
        if (_grayMat == null || _grayMat.cols() != rgbaMat.cols() || _grayMat.rows() != rgbaMat.rows())
        {
            _grayMat?.Dispose();
            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);
        }
    }

    void SetStatus(string msg)
    {
        if (statusText == null) return;
        if (Time.time - _lastStatusTime < statusUpdateInterval) return;
        _lastStatusTime = Time.time;
        statusText.text = msg;
    }

    static void ClearMatList(List<Mat> list)
    {
        for (int i = 0; i < list.Count; i++) list[i]?.Dispose();
        list.Clear();
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();

        _markerIds?.Dispose();
        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);

        ClearMatList(_rvecCandidates);
        ClearMatList(_tvecCandidates);

        _cameraMatrixFull?.Dispose();
        _distCoeffs?.Dispose();

        _arucoDetector?.Dispose();
        _arucoDict?.Dispose();

        _lastRvec?.Dispose();
        _lastTvec?.Dispose();
    }
}
*/


using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Meta.XR;

using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using Rect = UnityEngine.Rect;

/// <summary>
/// 「ぬるぬる補正」を完全除去した ArUco + PnP 版
/// - ワールドでの Lerp/Slerp フィルタ無し
/// - solvePnPGeneric の候補解選択で連続性ペナルティ無し（再投影誤差最小のみで選ぶ）
/// </summary>
public class QuestRightEyeArucoTracker_NoSmoothing : MonoBehaviour
{
    [Header("Meta XR Passthrough")]
    public PassthroughCameraAccess rightEyePca;
    public Transform hmdTransform;

    [Header("ArUco Settings")]
    public float markerSizeMeters = 0.08f;
    public int dictionaryId = Objdetect.DICT_4X4_50;
    public int targetMarkerId = -1;

    [Header("World Anchors")]
    public Transform qrAnchorWorld;       // マーカーの絶対位置（ワールド）
    public Transform cubeWorld;           // デバッグ用Cube（qrAnchorWorld の子）
    public Transform cubeRelativeToHmd;   // HMDローカル（O_real）

    [Header("Debug UI")]
    public TMP_Text statusText;
    public RawImage preview;

    [Header("Update Rate")]
    public float processInterval = 0.01f;
    public float statusUpdateInterval = 0.01f;

    [Header("Corner Refinement")]
    public bool refineCornersOnFullRes = true;
    public int cornerSubPixWin = 5;
    public int cornerSubPixIters = 20;
    public double cornerSubPixEps = 0.03;

    [Header("PnP Strategy")]
    [Tooltip("IPPE_SQUARE の複数解を solvePnPGeneric で取得し、再投影誤差が最小のものを選ぶ（連続性は使わない）")]
    public bool useSolvePnPGeneric = true;

    [Header("Intrinsics / Image Convention (CRITICAL)")]
    [Tooltip("Intrinsicsが『GetTexture()解像度基準』ならON。『センサー解像度基準』ならOFF（OFFだと内部でスケールする）")]
    public bool intrinsicsInTextureSpace = true;

    [Tooltip("ReadPixels/表示系で上下反転している場合にON（まずはOFF推奨。オーバーレイで判定）")]
    public bool flipImageVerticallyBeforeDetect = false;

    [Header("Transform Convention")]
    [Tooltip("QR版と同じ OpenCV->Unity 回転変換（推奨）")]
    public bool useQrStyleRotationConversion = true;

    [Header("Hold")]
    [Tooltip("マーカーロスト時に最後のワールド姿勢を保持し、HMD相対へ再計算して cubeRelativeToHmd を更新する")]
    public bool holdLastWorldPoseWhenMarkerLost = true;

    [Header("Overlay Debug")]
    public bool drawOverlay = true;
    public bool showReprojectionError = true;

    // ---- internal ----
    float _lastProcessTime;
    float _lastStatusTime;

    Texture2D _cpuTex;
    RenderTexture _rt;

    Mat _rgbaMat;
    Mat _grayMat;

    OpenCVForUnity.ObjdetectModule.Dictionary _arucoDict;
    ArucoDetector _arucoDetector;

    Mat _markerIds;
    readonly List<Mat> _markerCorners = new List<Mat>();
    readonly List<Mat> _rejectedCorners = new List<Mat>();

    // solvePnPGeneric outputs
    readonly List<Mat> _rvecCandidates = new List<Mat>();
    readonly List<Mat> _tvecCandidates = new List<Mat>();

    Mat _cameraMatrixFull;
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;

    float[] _cornerBuf;

    // tracking state (OpenCV rvec/tvec)
    Mat _lastRvec;
    Mat _lastTvec;
    bool _hasLastTracking = false;

    // hold state
    bool _hasLastCubeWorldPose = false;
    Vector3 _lastCubeWorldPos;
    Quaternion _lastCubeWorldRot = Quaternion.identity;

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            UnityEngine.Android.Permission.RequestUserPermissions(new string[] {
                "android.permission.CAMERA",
                "horizonos.permission.HEADSET_CAMERA"
            });
        }
#endif
        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca (PassthroughCameraAccess) is not set.");
            SetStatus("Error: rightEyePca not set");
            enabled = false;
            return;
        }

        // NOTE: 本来は歪み係数が欲しい。まずは0で。
        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);

        _arucoDict = Objdetect.getPredefinedDictionary(dictionaryId);
        _arucoDetector = new ArucoDetector(_arucoDict);

        _markerIds = new Mat();

        _lastRvec = new Mat(3, 1, CvType.CV_64F);
        _lastTvec = new Mat(3, 1, CvType.CV_64F);

        if (qrAnchorWorld != null && cubeWorld != null)
        {
            cubeWorld.SetParent(qrAnchorWorld, worldPositionStays: false);
            cubeWorld.localScale = Vector3.one * markerSizeMeters;
            cubeWorld.localPosition = new Vector3(0f, 0f, markerSizeMeters / 2f);
            cubeWorld.localRotation = Quaternion.identity;
        }

        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            cubeRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
            cubeRelativeToHmd.localPosition = Vector3.zero;
            cubeRelativeToHmd.localRotation = Quaternion.identity;
        }

        SetStatus("Init: waiting for PCA");
    }

    void Update()
    {
        if (rightEyePca == null) { SetStatus("Error: PCA is null"); return; }
        if (!rightEyePca.IsPlaying) { SetStatus("PCA: not playing"); return; }
        if (!rightEyePca.IsUpdatedThisFrame) return;

        if (Time.time - _lastProcessTime < processInterval) return;
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null) { SetStatus("PCA: texture is null"); return; }

        EnsureBuffers(camTex.width, camTex.height);

        // GPU -> CPU
        Graphics.Blit(camTex, _rt);
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _cpuTex.Apply();
        RenderTexture.active = null;

        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        if (!_intrinsicsReady)
        {
            BuildCameraMatrixFromIntrinsics(_cpuTex.width, _cpuTex.height);
            if (!_intrinsicsReady)
            {
                SetStatus("Intrinsics not ready");
                return;
            }
        }

        RunArucoDetectionAndPose(_rgbaMat);

        // preview
        if (preview != null)
        {
            preview.texture = _cpuTex;
            preview.rectTransform.sizeDelta = new Vector2(_cpuTex.width, _cpuTex.height);
        }
    }

    void LateUpdate()
    {
        // マーカーロスト時でも cubeRelativeToHmd を “最後のワールド姿勢” から更新し続ける（保持挙動）
        if (!holdLastWorldPoseWhenMarkerLost) return;
        if (!_hasLastCubeWorldPose) return;
        if (cubeRelativeToHmd == null || hmdTransform == null) return;

        Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (_lastCubeWorldPos - hmdTransform.position);
        Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * _lastCubeWorldRot;

        cubeRelativeToHmd.localPosition = relPos;
        cubeRelativeToHmd.localRotation = relRot;
    }

    void RunArucoDetectionAndPose(Mat rgbaMat)
    {
        if (rgbaMat == null || rgbaMat.empty()) return;

        EnsureGray(rgbaMat);
        Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

        if (flipImageVerticallyBeforeDetect)
        {
            Core.flip(_grayMat, _grayMat, 0);
            if (drawOverlay) Core.flip(_rgbaMat, _rgbaMat, 0);
        }

        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);
        _markerIds.release();

        _arucoDetector.detectMarkers(_grayMat, _markerCorners, _markerIds, _rejectedCorners);

        if (_markerIds == null || _markerIds.empty() || _markerIds.rows() <= 0 || _markerCorners.Count <= 0)
        {
            SetStatus("AruCo: none");
            _hasLastTracking = false;
            return;
        }

        // select marker
        int useIndex = 0;
        int useId = (int)_markerIds.get(0, 0)[0];

        if (targetMarkerId >= 0)
        {
            bool found = false;
            for (int r = 0; r < _markerIds.rows(); r++)
            {
                int id = (int)_markerIds.get(r, 0)[0];
                if (id == targetMarkerId)
                {
                    useIndex = r;
                    useId = id;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                SetStatus($"Wait id={targetMarkerId}");
                _hasLastTracking = false;
                return;
            }
        }

        // corners
        Mat cornerMat = _markerCorners[useIndex];
        int need = (int)(cornerMat.total() * cornerMat.channels());
        if (_cornerBuf == null || _cornerBuf.Length < need) _cornerBuf = new float[need];
        cornerMat.get(0, 0, _cornerBuf);

        if (need < 8)
        {
            SetStatus("AruCo: invalid corners");
            _hasLastTracking = false;
            return;
        }

        Point[] pts = new Point[]
        {
            new Point(_cornerBuf[0], _cornerBuf[1]),
            new Point(_cornerBuf[2], _cornerBuf[3]),
            new Point(_cornerBuf[4], _cornerBuf[5]),
            new Point(_cornerBuf[6], _cornerBuf[7]),
        };

        // cornerSubPix (optional)
        if (refineCornersOnFullRes)
        {
            using (MatOfPoint2f tmp = new MatOfPoint2f(pts))
            {
                TermCriteria criteria = new TermCriteria(TermCriteria.EPS + TermCriteria.MAX_ITER, cornerSubPixIters, cornerSubPixEps);
                Size win = new Size(cornerSubPixWin, cornerSubPixWin);
                Imgproc.cornerSubPix(_grayMat, tmp, win, new Size(-1, -1), criteria);
                pts = tmp.toArray();
            }
        }

        // object points (TL,TR,BR,BL), z=0 plane, centered
        float s = markerSizeMeters;
        Point3[] objectPoints = new Point3[]
        {
            new Point3(-s/2f,  s/2f, 0),
            new Point3( s/2f,  s/2f, 0),
            new Point3( s/2f, -s/2f, 0),
            new Point3(-s/2f, -s/2f, 0),
        };

        // solve pose
        double err;
        bool ok = SolvePose_IPPE_Generic_NoContinuity(objectPoints, pts, _cameraMatrixFull, _distCoeffs, out err);

        if (!ok)
        {
            SetStatus("PnP: fail");
            _hasLastTracking = false;
            return;
        }

        _hasLastTracking = true;

        // overlay: detected corners (green) + reproj (red)
        if (drawOverlay)
        {
            DrawCornersAndReproj(_rgbaMat, objectPoints, pts, _lastRvec, _lastTvec, _cameraMatrixFull, _distCoeffs);
            Utils.matToTexture2D(_rgbaMat, _cpuTex);
        }

        // status
        double z = _lastTvec.get(2, 0)[0];
        if (showReprojectionError)
            SetStatus($"id={useId}  z={z:F2}m  err={err:F1}px");
        else
            SetStatus($"id={useId}  z={z:F2}m");

        // apply (フィルタ無しで即適用)
        UpdateTransformsFromPnP_NoFilter(_lastRvec, _lastTvec);
    }

    // ------------------------------------------------------------
    // Pose solver: solvePnPGeneric(IPPE_SQUARE) + choose best (NO continuity)
    // ------------------------------------------------------------
    bool SolvePose_IPPE_Generic_NoContinuity(
        Point3[] objectPoints,
        Point[] imagePoints,
        Mat K,
        MatOfDouble dist,
        out double bestErr)
    {
        bestErr = double.PositiveInfinity;

        using (MatOfPoint2f img = new MatOfPoint2f(imagePoints))
        using (MatOfPoint3f obj = new MatOfPoint3f(objectPoints))
        {
            if (!useSolvePnPGeneric)
            {
                using (Mat rvec = new Mat())
                using (Mat tvec = new Mat())
                {
                    bool ok = Calib3d.solvePnP(obj, img, K, dist, rvec, tvec, false, Calib3d.SOLVEPNP_IPPE_SQUARE);
                    if (!ok) return false;

                    double z = tvec.get(2, 0)[0];
                    if (z <= 0) return false;

                    bestErr = showReprojectionError ? ComputeReprojectionErrorPx(obj, img, rvec, tvec, K, dist) : 0.0;
                    rvec.copyTo(_lastRvec);
                    tvec.copyTo(_lastTvec);
                    return true;
                }
            }

            ClearMatList(_rvecCandidates);
            ClearMatList(_tvecCandidates);

            int nsol = Calib3d.solvePnPGeneric(
                obj, img,
                K, dist,
                _rvecCandidates, _tvecCandidates,
                false,
                Calib3d.SOLVEPNP_IPPE_SQUARE);

            if (nsol <= 0 || _rvecCandidates.Count <= 0 || _tvecCandidates.Count <= 0)
                return false;

            int bestIdx = -1;

            for (int i = 0; i < _rvecCandidates.Count; i++)
            {
                Mat rvec = _rvecCandidates[i];
                Mat tvec = _tvecCandidates[i];
                if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) continue;

                double z = tvec.get(2, 0)[0];
                if (z <= 0) continue;

                double err = showReprojectionError ? ComputeReprojectionErrorPx(obj, img, rvec, tvec, K, dist) : 0.0;

                // ★連続性ペナルティ無し：err 最小だけで選ぶ
                if (err < bestErr)
                {
                    bestErr = err;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) return false;

            _rvecCandidates[bestIdx].copyTo(_lastRvec);
            _tvecCandidates[bestIdx].copyTo(_lastTvec);
            return true;
        }
    }

    // ------------------------------------------------------------
    // Apply pose to Unity (NO FILTER)
    // ------------------------------------------------------------
    void UpdateTransformsFromPnP_NoFilter(Mat rvec, Mat tvec)
    {
        if (qrAnchorWorld == null) return;
        if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

        // tvec: OpenCV cam coords (x right, y down, z forward)
        double[] t = new double[3];
        tvec.get(0, 0, t);

        // OpenCV: X-Right, Y-Down, Z-Forward
        // Unity:  X-Right, Y-Up,   Z-Forward
        Vector3 posCamLocal = new Vector3((float)t[0], (float)(-t[1]), (float)t[2]);

        // rvec -> R
        Mat R_cv = new Mat(3, 3, CvType.CV_64F);
        Calib3d.Rodrigues(rvec, R_cv);
        double[] r = new double[9];
        R_cv.get(0, 0, r);
        R_cv.Dispose();

        Quaternion rotCamLocal;
        if (useQrStyleRotationConversion)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
            m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
            m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];

            Vector3 forward = m.GetColumn(2);
            Vector3 up = m.GetColumn(1);
            if (forward == Vector3.zero || up == Vector3.zero) return;
            rotCamLocal = Quaternion.LookRotation(forward, up);
        }
        else
        {
            Matrix4x4 Rm = Matrix4x4.identity;
            Rm.m00 = (float)r[0]; Rm.m01 = (float)r[1]; Rm.m02 = (float)r[2];
            Rm.m10 = (float)r[3]; Rm.m11 = (float)r[4]; Rm.m12 = (float)r[5];
            Rm.m20 = (float)r[6]; Rm.m21 = (float)r[7]; Rm.m22 = (float)r[8];

            Matrix4x4 S = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
            Matrix4x4 Ru = S * Rm * S;

            Vector3 forward = Ru.GetColumn(2);
            Vector3 up = Ru.GetColumn(1);
            if (forward == Vector3.zero || up == Vector3.zero) return;
            rotCamLocal = Quaternion.LookRotation(forward, up);
        }

        Pose camWorldPose = rightEyePca.GetCameraPose();

        Vector3 newWorldPos = camWorldPose.position + camWorldPose.rotation * posCamLocal;
        Quaternion newWorldRot = camWorldPose.rotation * rotCamLocal;

        // ★フィルタ無しで即適用
        qrAnchorWorld.position = newWorldPos;
        qrAnchorWorld.rotation = newWorldRot;

        // 保存（ホールド用）
        _lastCubeWorldPos = newWorldPos;
        _lastCubeWorldRot = newWorldRot;
        _hasLastCubeWorldPose = true;

        // HMD相対も即更新
        if (cubeRelativeToHmd != null && hmdTransform != null)
        {
            Vector3 relPos = Quaternion.Inverse(hmdTransform.rotation) * (newWorldPos - hmdTransform.position);
            Quaternion relRot = Quaternion.Inverse(hmdTransform.rotation) * newWorldRot;

            cubeRelativeToHmd.localPosition = relPos;
            cubeRelativeToHmd.localRotation = relRot;
        }
    }

    // ------------------------------------------------------------
    // Reprojection error / overlay
    // ------------------------------------------------------------
    static double ComputeReprojectionErrorPx(
        MatOfPoint3f objectPts,
        MatOfPoint2f imagePts,
        Mat rvec,
        Mat tvec,
        Mat cameraMatrix,
        MatOfDouble distCoeffs)
    {
        using (MatOfPoint2f projected = new MatOfPoint2f())
        {
            Calib3d.projectPoints(objectPts, rvec, tvec, cameraMatrix, distCoeffs, projected);

            Point[] obs = imagePts.toArray();
            Point[] proj = projected.toArray();
            if (obs == null || proj == null || obs.Length != proj.Length || obs.Length == 0) return double.NaN;

            double err = 0.0;
            for (int i = 0; i < obs.Length; i++)
            {
                double dx = proj[i].x - obs[i].x;
                double dy = proj[i].y - obs[i].y;
                err += Math.Sqrt(dx * dx + dy * dy);
            }
            return err / obs.Length;
        }
    }

    void DrawCornersAndReproj(
        Mat rgba,
        Point3[] objectPoints,
        Point[] detectedPts,
        Mat rvec, Mat tvec,
        Mat K, MatOfDouble dist)
    {
        for (int i = 0; i < 4; i++)
            Imgproc.circle(rgba, detectedPts[i], 6, new Scalar(0, 255, 0, 255), 2);

        using (var obj = new MatOfPoint3f(objectPoints))
        using (var proj = new MatOfPoint2f())
        {
            Calib3d.projectPoints(obj, rvec, tvec, K, dist, proj);
            var p = proj.toArray();
            for (int i = 0; i < 4; i++)
                Imgproc.circle(rgba, p[i], 4, new Scalar(255, 0, 0, 255), 2);
        }
    }

    // ------------------------------------------------------------
    // Intrinsics
    // ------------------------------------------------------------
    void BuildCameraMatrixFromIntrinsics(int texW, int texH)
    {
        var intr = rightEyePca.Intrinsics;
        if (intr.SensorResolution.x <= 0 || intr.SensorResolution.y <= 0)
        {
            Debug.Log("Right-eye intrinsics not ready yet.");
            return;
        }

        double fx = intr.FocalLength.x;
        double fy = intr.FocalLength.y;
        double cx = intr.PrincipalPoint.x;
        double cy = intr.PrincipalPoint.y;

        if (!intrinsicsInTextureSpace)
        {
            double sx = (double)texW / intr.SensorResolution.x;
            double sy = (double)texH / intr.SensorResolution.y;
            fx *= sx; fy *= sy; cx *= sx; cy *= sy;
        }

        if (_cameraMatrixFull != null) _cameraMatrixFull.Dispose();
        _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx); _cameraMatrixFull.put(0, 1, 0); _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0); _cameraMatrixFull.put(1, 1, fy); _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0); _cameraMatrixFull.put(2, 1, 0); _cameraMatrixFull.put(2, 2, 1);

        _intrinsicsReady = true;

        Debug.Log($"[Intrinsics] SensorRes={intr.SensorResolution.x}x{intr.SensorResolution.y} Tex={texW}x{texH} " +
                  $"K: fx={fx:F1} fy={fy:F1} cx={cx:F1} cy={cy:F1} (intrinsicsInTextureSpace={intrinsicsInTextureSpace})");
    }

    // ------------------------------------------------------------
    // Buffers / utils
    // ------------------------------------------------------------
    void EnsureBuffers(int w, int h)
    {
        if (_rt == null || _rt.width != w || _rt.height != h)
        {
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);

            if (_cpuTex != null) Destroy(_cpuTex);
            _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            _rgbaMat?.Dispose();
            _grayMat?.Dispose();

            _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
            _grayMat = new Mat(h, w, CvType.CV_8UC1);

            _intrinsicsReady = false;
        }
    }

    void EnsureGray(Mat rgbaMat)
    {
        if (_grayMat == null || _grayMat.cols() != rgbaMat.cols() || _grayMat.rows() != rgbaMat.rows())
        {
            _grayMat?.Dispose();
            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);
        }
    }

    void SetStatus(string msg)
    {
        if (statusText == null) return;
        if (Time.time - _lastStatusTime < statusUpdateInterval) return;
        _lastStatusTime = Time.time;
        statusText.text = msg;
    }

    static void ClearMatList(List<Mat> list)
    {
        for (int i = 0; i < list.Count; i++) list[i]?.Dispose();
        list.Clear();
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();

        _markerIds?.Dispose();
        ClearMatList(_markerCorners);
        ClearMatList(_rejectedCorners);

        ClearMatList(_rvecCandidates);
        ClearMatList(_tvecCandidates);

        _cameraMatrixFull?.Dispose();
        _distCoeffs?.Dispose();

        _arucoDetector?.Dispose();
        _arucoDict?.Dispose();

        _lastRvec?.Dispose();
        _lastTvec?.Dispose();
    }
}
