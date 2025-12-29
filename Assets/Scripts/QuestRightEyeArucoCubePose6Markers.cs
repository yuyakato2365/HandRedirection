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
/// 6枚のArUco(3..8)が立方体の各面中心に貼られている前提で、
/// 「検出された中で最もよく見えている1枚」を選び、
/// そのPnP結果からCube中心Poseを推定して更新する。
///
/// 追加機能:
/// - 出力位置のオフセット補正（World座標 / Cube座標の両対応、XYZ調整）
/// - 追従性を保ちつつ微細揺れを抑える One Euro Filter スムージング（位置＋回転）
/// - マーカID切替時のちらつき低減：ヒステリシス＋保持時間＋切替ブレンド
///
/// 追加修正(今回):
/// - 「推定は今まで通り」行う（markerRotW + markerToCubeRot で cubeRotBaseW を作る）
/// - ただし最終出力する前に、検知IDに応じて「Cube自身（Cubeローカル軸）」で追加回転を上書きする。
///   ※右掛け cubeRotFinalW = cubeRotBaseW * extraCubeLocalRot でローカル回転になる。
///   ※位置も cubeRotFinalW で逆算し直す（ここ重要）
///
/// 指定の追加回転（すべて “Cubeローカル基準” ）:
///   ID4: Z180 → X90
///   ID5: Z90  → Y90
///   ID6: X90
///   ID7: Z90  → Y90
///   ID8: Z180
///   ID3: 補正なし
/// </summary>
public class QuestRightEyeArucoCubePose6Markers : MonoBehaviour
{
    [Header("Meta XR Passthrough")]
    public PassthroughCameraAccess rightEyePca;
    public Transform hmdTransform;                // CenterEyeAnchor 推奨（表示用変換に使用）

    [Header("Outputs")]
    public Transform cubeCenterWorld;             // 推定したCube中心をワールドに置く（デバッグ用）
    public Transform cubeCenterRelativeToHmd;     // 推定したCube中心をHMDローカルに置く（任意）

    [Header("Geometry (meters)")]
    public float cubeSizeMeters = 0.150f;
    public float markerSizeMeters = 0.038f;

    [Header("ArUco")]
    public int dictionaryId = Objdetect.DICT_4X4_50;
    public bool cornerSubPix = true;
    public bool drawOverlay = true;

    [Header("Camera / Intrinsics")]
    public bool flipImageVerticallyBeforeDetect = false;
    public bool intrinsicsInTextureSpace = true;
    public bool useQrStyleRotationConversion = true;

    [Header("Best marker selection")]
    public BestMarkerCriterion bestMarkerCriterion = BestMarkerCriterion.AreaOverReprojError;

    [Tooltip("score = areaPx / (reprojErrPx + ε) の ε")]
    public float reprojEpsilon = 1e-3f;

    [Header("Calibration / Position Offsets (meters)")]
    [Tooltip("World座標系で足し込むオフセット（UnityのXYZ）。常に一定方向にずれる場合に使う。")]
    public bool enableWorldOffset = true;
    public Vector3 cubeCenterOffsetInWorld = Vector3.zero;

    [Tooltip("Cube座標系で足し込むオフセット（CubeのXYZ）。Cubeの回転に追従してずれる場合に使う。")]
    public bool enableCubeOffset = false;
    public Vector3 cubeCenterOffsetInCube = Vector3.zero;

    [Header("Smoothing (One Euro Filter)")]
    public bool enableSmoothing = true;
    public float posMinCutoffHz = 1.0f;
    public float posBeta = 0.05f;
    public float rotMinCutoffHz = 1.0f;
    public float rotBeta = 0.05f;
    public float derivCutoffHz = 1.0f;

    [Header("Marker switching stability")]
    public bool enableMarkerHysteresis = true;
    public float switchScoreMargin = 1.15f;
    public float minHoldSeconds = 0.15f;

    public bool enableSwitchBlend = true;
    public float switchBlendSeconds = 0.10f;

    [Header("Outlier gate (optional)")]
    public bool enableJumpGate = false;
    public float maxJumpMeters = 0.20f;
    public float maxJumpDegrees = 45f;

    [Header("Perf")]
    public float processInterval = 0.02f;

    [Header("Debug UI")]
    public RawImage preview;
    public TMP_Text statusText;

    public enum BestMarkerCriterion
    {
        LargestArea,
        SmallestReprojError,
        AreaOverReprojError
    }

    // --- OpenCV ---
    Mat _rgbaMat, _grayMat;
    RenderTexture _rt;
    Texture2D _cpuTex;

    Mat _cameraMatrixFull;
    MatOfDouble _distCoeffs;
    bool _intrinsicsReady;
    int _texW, _texH;

    Dictionary _arucoDict;
    ArucoDetector _arucoDetector;
    List<Mat> _markerCorners = new List<Mat>();
    Mat _markerIds;
    List<Mat> _rejected = new List<Mat>();

    // PnP temp
    readonly List<Mat> _rvecCandidates = new List<Mat>();
    readonly List<Mat> _tvecCandidates = new List<Mat>();
    readonly Mat _workRvec = new Mat(3, 1, CvType.CV_64F);
    readonly Mat _workTvec = new Mat(3, 1, CvType.CV_64F);

    struct Binding
    {
        public int id;
        public Vector3 offsetCube;           // Cube中心 → マーカ中心（Cube座標）
        public Quaternion markerToCubeRot;   // マーカ座標 → Cube座標（貼り位置）
        public Quaternion extraCubeLocalRot; // 最終出力直前に「Cubeローカル」で上書きする追加回転
    }
    Dictionary<int, Binding> _bindings = new Dictionary<int, Binding>();

    float _lastProcessTime;

    // last output (for jump gate / switch blend base)
    bool _hasLastCubePose;
    Vector3 _lastCubePosW;
    Quaternion _lastCubeRotW;

    // --- Smoothing state (One Euro) ---
    bool _hasFilterState;
    float _lastFilterTime;
    Vector3 _fPosW;
    Quaternion _fRotW;
    Vector3 _prevRawPosW;
    Quaternion _prevRawRotW;
    Vector3 _fVelW;
    float _fAngVelDeg;

    // --- Marker selection state ---
    int _currentMarkerId = -1;
    float _currentMarkerSince = 0f;

    // --- Switch blending state ---
    bool _isSwitchBlending;
    float _switchStartTime;
    Vector3 _switchFromPosW;
    Quaternion _switchFromRotW;

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.HEADSET_CAMERA"))
            UnityEngine.Android.Permission.RequestUserPermission("android.permission.HEADSET_CAMERA");
#endif
        if (rightEyePca == null)
        {
            Debug.LogError("rightEyePca is not set.");
            enabled = false;
            return;
        }

        _distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);
        _arucoDict = Objdetect.getPredefinedDictionary(dictionaryId);
        _arucoDetector = new ArucoDetector(_arucoDict);
        _markerIds = new Mat();

        BuildCubeBindings();

        if (cubeCenterRelativeToHmd != null && hmdTransform != null)
            cubeCenterRelativeToHmd.SetParent(hmdTransform, worldPositionStays: false);
    }

    void Update()
    {
        if (rightEyePca == null) return;
        if (!rightEyePca.IsPlaying) return;
        if (!rightEyePca.IsUpdatedThisFrame) return;

        if (Time.time - _lastProcessTime < processInterval) return;
        _lastProcessTime = Time.time;

        Texture camTex = rightEyePca.GetTexture();
        if (camTex == null) return;

        EnsureBuffers(camTex.width, camTex.height);

        // GPU→CPU
        Graphics.Blit(camTex, _rt);
        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, _texW, _texH), 0, 0);
        _cpuTex.Apply(false);
        RenderTexture.active = prev;

        Utils.texture2DToMat(_cpuTex, _rgbaMat);

        if (!_intrinsicsReady)
        {
            if (!BuildCameraMatrixFromIntrinsics(_texW, _texH))
            {
                SetStatus("Intrinsics not ready.");
                return;
            }
        }

        int prevId = _currentMarkerId;

        bool ok = EstimateCubePoseWithStableMarker(
            _rgbaMat,
            out Vector3 cubePosW,
            out Quaternion cubeRotW,
            out int chosenId,
            out float chosenAreaPx,
            out float chosenReprojErr,
            out double chosenScore,
            out double bestScore,
            out int bestIdRaw);

        if (!ok)
        {
            SetStatus("AruCo: none/failed");
            return;
        }

        bool idSwitchedThisFrame = (prevId >= 0 && chosenId != prevId);

        if (enableJumpGate && _hasLastCubePose && !idSwitchedThisFrame && !_isSwitchBlending)
        {
            float dp = Vector3.Distance(_lastCubePosW, cubePosW);
            float dr = Quaternion.Angle(_lastCubeRotW, cubeRotW);
            if (dp > maxJumpMeters || dr > maxJumpDegrees)
            {
                SetStatus($"Rejected jump (id={chosenId}, dp={dp:F3}m, dr={dr:F1}deg)");
                return;
            }
        }

        if (chosenId != _currentMarkerId)
        {
            _currentMarkerId = chosenId;
            _currentMarkerSince = Time.time;

            if (enableSwitchBlend && _hasLastCubePose)
            {
                _isSwitchBlending = true;
                _switchStartTime = Time.time;
                _switchFromPosW = _lastCubePosW;
                _switchFromRotW = _lastCubeRotW;
                _hasFilterState = false;
            }
        }

        if (_isSwitchBlending)
        {
            float t = (switchBlendSeconds <= 1e-6f) ? 1f : (Time.time - _switchStartTime) / switchBlendSeconds;
            if (t >= 1f)
            {
                _isSwitchBlending = false;
            }
            else
            {
                float s = SmoothStep01(Mathf.Clamp01(t));
                cubePosW = Vector3.Lerp(_switchFromPosW, cubePosW, s);

                Quaternion target = EnsureShortestPath(_switchFromRotW, cubeRotW);
                cubeRotW = Quaternion.Slerp(_switchFromRotW, target, s);
            }
        }

        if (enableSmoothing)
        {
            ApplyOneEuroSmoothing(ref cubePosW, ref cubeRotW);
        }

        _lastCubePosW = cubePosW;
        _lastCubeRotW = cubeRotW;
        _hasLastCubePose = true;

        if (cubeCenterWorld != null)
        {
            cubeCenterWorld.position = cubePosW;
            cubeCenterWorld.rotation = cubeRotW;
        }

        if (cubeCenterRelativeToHmd != null && hmdTransform != null)
        {
            Quaternion invH = Quaternion.Inverse(hmdTransform.rotation);
            cubeCenterRelativeToHmd.localPosition = invH * (cubePosW - hmdTransform.position);
            cubeCenterRelativeToHmd.localRotation = invH * cubeRotW;
        }

        SetStatus($"OK chosenId={chosenId} (best={bestIdRaw})  score={chosenScore:F2}/{bestScore:F2}  area={chosenAreaPx:F0}px²  reproj={chosenReprojErr:F2}px");
    }

    // ----------------------------
    // 6面のBinding定義（重要）
    // ----------------------------
    void BuildCubeBindings()
    {
        _bindings.Clear();
        float half = cubeSizeMeters * 0.5f;

        // Unity Cube axes:
        // +X right, +Y up, +Z forward

        Vector3 off3 = new Vector3(0, +half, 0);
        Vector3 off8 = new Vector3(0, -half, 0);
        Vector3 off4 = new Vector3(0, 0, -half);
        Vector3 off6 = new Vector3(0, 0, +half);
        Vector3 off5 = new Vector3(-half, 0, 0);
        Vector3 off7 = new Vector3(+half, 0, 0);

        AddBinding(3, off3, markerZInCube: Vector3.up, markerYInCube: Vector3.forward, extraCubeLocalRot: Quaternion.identity);
        //実際の観測姿勢をもとに補正をかけている。
        AddBinding(4, off4, markerZInCube: Vector3.forward, markerYInCube: Vector3.up, extraCubeLocalRot: RotX(180f));
        AddBinding(5, off5, markerZInCube: Vector3.right, markerYInCube: Vector3.up, extraCubeLocalRot: RotZ(180f));
        AddBinding(6, off6, markerZInCube: Vector3.back, markerYInCube: Vector3.up, extraCubeLocalRot: RotX(180f));
        AddBinding(7, off7, markerZInCube: Vector3.left, markerYInCube: Vector3.up, extraCubeLocalRot: RotZ(180f));
        AddBinding(8, off8, markerZInCube: Vector3.down, markerYInCube: Vector3.forward, extraCubeLocalRot: RotZ(0f));
    }

    void AddBinding(int id, Vector3 offsetCube, Vector3 markerZInCube, Vector3 markerYInCube, Quaternion extraCubeLocalRot)
    {
        Quaternion markerToCube = Quaternion.LookRotation(markerZInCube, markerYInCube);
        _bindings[id] = new Binding
        {
            id = id,
            offsetCube = offsetCube,
            markerToCubeRot = markerToCube,
            extraCubeLocalRot = extraCubeLocalRot
        };
    }

    // 「Z→X」の順で回す（Zが先、Xが後）
    static Quaternion SeqZThenX(float zDeg, float xDeg) => RotX(xDeg) * RotZ(zDeg);
    // 「Z→Y」の順で回す（Zが先、Yが後）
    static Quaternion SeqZThenY(float zDeg, float yDeg) => RotY(yDeg) * RotZ(zDeg);

    static Quaternion RotX(float deg) => Quaternion.AngleAxis(deg, Vector3.right);
    static Quaternion RotY(float deg) => Quaternion.AngleAxis(deg, Vector3.up);
    static Quaternion RotZ(float deg) => Quaternion.AngleAxis(deg, Vector3.forward);

    // ----------------------------
    // Candidate（markerPosW と cubeRotBaseW を保持）
    // ----------------------------
    struct Candidate
    {
        public bool valid;
        public int id;
        public float areaPx;
        public float reprojErrPx;
        public double score;

        public Vector3 markerPosW;         // 観測されたマーカ中心（ワールド）
        public Quaternion cubeRotBaseW;    // 従来計算：貼り位置を使って得たCube姿勢（ワールド）
    }

    bool EstimateCubePoseWithStableMarker(
        Mat rgba,
        out Vector3 cubePosW,
        out Quaternion cubeRotW,
        out int chosenId,
        out float chosenAreaPx,
        out float chosenReprojErrPx,
        out double chosenScore,
        out double bestScore,
        out int bestIdRaw)
    {
        cubePosW = Vector3.zero;
        cubeRotW = Quaternion.identity;
        chosenId = -1;
        chosenAreaPx = 0f;
        chosenReprojErrPx = float.PositiveInfinity;
        chosenScore = double.NegativeInfinity;
        bestScore = double.NegativeInfinity;
        bestIdRaw = -1;

        Imgproc.cvtColor(rgba, _grayMat, Imgproc.COLOR_RGBA2GRAY);
        if (flipImageVerticallyBeforeDetect) Core.flip(_grayMat, _grayMat, 0);

        _markerCorners.Clear();
        _rejected.Clear();
        _markerIds.release();

        _arucoDetector.detectMarkers(_grayMat, _markerCorners, _markerIds, _rejected);

        int n = (_markerIds != null) ? (int)_markerIds.total() : 0;
        if (n <= 0) return false;

        if (drawOverlay)
            Objdetect.drawDetectedMarkers(rgba, _markerCorners, _markerIds);

        Pose camWorldPose = rightEyePca.GetCameraPose();
        Point3[] objPts = BuildMarkerObjectPoints(markerSizeMeters);

        Candidate best = new Candidate { valid = false };
        Candidate current = new Candidate { valid = false };

        for (int i = 0; i < n; i++)
        {
            int id = (int)_markerIds.get(i, 0)[0];
            if (!_bindings.TryGetValue(id, out Binding bind)) continue;

            using (MatOfPoint2f imgPts = GetRefinedCorners(_markerCorners[i]))
            {
                float areaPx = ComputeQuadAreaPx(imgPts);

                if (!TrySolvePnP_IPPE(objPts, imgPts, _cameraMatrixFull, _distCoeffs, _workRvec, _workTvec, out float reprojErrPx))
                    continue;

                double score;
                switch (bestMarkerCriterion)
                {
                    case BestMarkerCriterion.LargestArea:
                        score = areaPx;
                        break;
                    case BestMarkerCriterion.SmallestReprojError:
                        score = -reprojErrPx;
                        break;
                    default:
                        score = areaPx / (reprojErrPx + reprojEpsilon);
                        break;
                }

                CvPoseToUnityCameraLocal(_workRvec, _workTvec, out Vector3 markerPosCamLocal, out Quaternion markerRotCamLocal);

                Vector3 markerPosW = camWorldPose.position + camWorldPose.rotation * markerPosCamLocal;
                Quaternion markerRotW = camWorldPose.rotation * markerRotCamLocal;

                // 従来どおり：貼り位置で cubeRotBaseW を計算（ここではまだ「追加回転」はしない）
                Quaternion cubeRotBaseW = markerRotW * Quaternion.Inverse(bind.markerToCubeRot);

                Candidate cand = new Candidate
                {
                    valid = true,
                    id = id,
                    areaPx = areaPx,
                    reprojErrPx = reprojErrPx,
                    score = score,
                    markerPosW = markerPosW,
                    cubeRotBaseW = cubeRotBaseW
                };

                if (!best.valid || cand.score > best.score) best = cand;
                if (_currentMarkerId >= 0 && id == _currentMarkerId) current = cand;
            }
        }

        if (!best.valid) return false;

        bestScore = best.score;
        bestIdRaw = best.id;

        Candidate chosen = best;

        if (enableMarkerHysteresis && current.valid && _currentMarkerId >= 0)
        {
            float held = Time.time - _currentMarkerSince;
            bool holdLock = held < minHoldSeconds;
            bool switchAllowed = (!holdLock) && (best.score >= current.score * switchScoreMargin);

            if (!switchAllowed) chosen = current;
        }

        if (!_bindings.TryGetValue(chosen.id, out Binding chosenBind))
            return false;

        // ★最終出力直前に、IDに応じて Cube 自身を追加回転（右掛け＝Cubeローカル軸）
        Quaternion cubeRotFinalW = chosen.cubeRotBaseW * chosenBind.extraCubeLocalRot;

        // ★位置は「補正後の回転」で逆算（重要）
        Vector3 cubePosFinalW = chosen.markerPosW - (cubeRotFinalW * chosenBind.offsetCube);

        if (enableCubeOffset) cubePosFinalW += cubeRotFinalW * cubeCenterOffsetInCube;
        if (enableWorldOffset) cubePosFinalW += cubeCenterOffsetInWorld;

        chosenId = chosen.id;
        chosenAreaPx = chosen.areaPx;
        chosenReprojErrPx = chosen.reprojErrPx;
        chosenScore = chosen.score;

        cubePosW = cubePosFinalW;
        cubeRotW = cubeRotFinalW;
        return true;
    }

    float ComputeQuadAreaPx(MatOfPoint2f imgPts)
    {
        Point[] p = imgPts.toArray();
        if (p == null || p.Length < 4) return 0f;

        double area2 = 0.0;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            area2 += p[i].x * p[j].y - p[j].x * p[i].y;
        }
        return (float)(Math.Abs(area2) * 0.5);
    }

    bool TrySolvePnP_IPPE(Point3[] objectPoints, MatOfPoint2f imagePoints, Mat K, MatOfDouble dist,
                          Mat outRvec, Mat outTvec, out float reprojErrPx)
    {
        reprojErrPx = float.PositiveInfinity;

        using (MatOfPoint3f obj = new MatOfPoint3f(objectPoints))
        {
            ClearMatList(_rvecCandidates);
            ClearMatList(_tvecCandidates);

            int nsol = Calib3d.solvePnPGeneric(obj, imagePoints, K, dist,
                _rvecCandidates, _tvecCandidates, false, Calib3d.SOLVEPNP_IPPE_SQUARE);

            if (nsol <= 0) return false;

            int best = -1;
            double bestErr = double.PositiveInfinity;

            for (int i = 0; i < _rvecCandidates.Count; i++)
            {
                double z = _tvecCandidates[i].get(2, 0)[0];
                if (z <= 0) continue;

                double err = ComputeReprojectionError(obj, imagePoints, K, dist, _rvecCandidates[i], _tvecCandidates[i]);
                if (err < bestErr)
                {
                    bestErr = err;
                    best = i;
                }
            }

            if (best < 0)
            {
                for (int i = 0; i < _rvecCandidates.Count; i++)
                {
                    double err = ComputeReprojectionError(obj, imagePoints, K, dist, _rvecCandidates[i], _tvecCandidates[i]);
                    if (err < bestErr)
                    {
                        bestErr = err;
                        best = i;
                    }
                }
            }

            if (best < 0) return false;

            _rvecCandidates[best].copyTo(outRvec);
            _tvecCandidates[best].copyTo(outTvec);
            reprojErrPx = (float)bestErr;
            return true;
        }
    }

    double ComputeReprojectionError(MatOfPoint3f obj, MatOfPoint2f img, Mat K, MatOfDouble dist, Mat rvec, Mat tvec)
    {
        using (MatOfPoint2f proj = new MatOfPoint2f())
        {
            Calib3d.projectPoints(obj, rvec, tvec, K, dist, proj);
            Point[] p = proj.toArray();
            Point[] q = img.toArray();

            int n = Mathf.Min(p.Length, q.Length);
            if (n <= 0) return double.PositiveInfinity;

            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = p[i].x - q[i].x;
                double dy = p[i].y - q[i].y;
                sum += Math.Sqrt(dx * dx + dy * dy);
            }
            return sum / n;
        }
    }

    void CvPoseToUnityCameraLocal(Mat rvec, Mat tvec, out Vector3 posCamLocal, out Quaternion rotCamLocal)
    {
        double[] t = new double[3];
        tvec.get(0, 0, t);

        // OpenCV camera coords -> Unity camera local
        // pos: (x, -y, z)
        posCamLocal = new Vector3((float)t[0], (float)(-t[1]), (float)t[2]);

        using (Mat R_cv = new Mat(3, 3, CvType.CV_64F))
        {
            Calib3d.Rodrigues(rvec, R_cv);
            double[] r = new double[9];
            R_cv.get(0, 0, r);

            if (useQrStyleRotationConversion)
            {
                Matrix4x4 m = Matrix4x4.identity;
                m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
                m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
                m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];
                rotCamLocal = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
            }
            else
            {
                // 使わない設定なら、とりあえず無回転（必要ならここも実装）
                rotCamLocal = Quaternion.identity;
            }
        }
    }

    MatOfPoint2f GetRefinedCorners(Mat cornersMat)
    {
        Point[] pts = new Point[4];
        for (int j = 0; j < 4; j++)
        {
            double[] v = cornersMat.get(0, j);
            pts[j] = new Point(v[0], v[1]);
        }

        MatOfPoint2f mp = new MatOfPoint2f(pts);

        if (cornerSubPix)
        {
            TermCriteria tc = new TermCriteria(TermCriteria.EPS | TermCriteria.MAX_ITER, 30, 0.001);
            Imgproc.cornerSubPix(_grayMat, mp, new Size(4, 4), new Size(-1, -1), tc);
        }
        return mp;
    }

    Point3[] BuildMarkerObjectPoints(float sizeMeters)
    {
        float s = sizeMeters * 0.5f;
        return new Point3[]
        {
            new Point3(-s, +s, 0),
            new Point3(+s, +s, 0),
            new Point3(+s, -s, 0),
            new Point3(-s, -s, 0),
        };
    }

    bool BuildCameraMatrixFromIntrinsics(int texW, int texH)
    {
        if (rightEyePca == null) return false;
        var intr = rightEyePca.Intrinsics;

        float fx = intr.FocalLength.x;
        float fy = intr.FocalLength.y;
        float cx = intr.PrincipalPoint.x;
        float cy = intr.PrincipalPoint.y;

        bool normalized = (fx > 0 && fx < 10 && fy > 0 && fy < 10 && cx >= 0 && cx <= 2 && cy >= 0 && cy <= 2);
        if (normalized)
        {
            fx *= intr.SensorResolution.x;
            fy *= intr.SensorResolution.y;
            cx *= intr.SensorResolution.x;
            cy *= intr.SensorResolution.y;
        }

        if (!intrinsicsInTextureSpace)
        {
            float sx = (float)texW / (float)intr.SensorResolution.x;
            float sy = (float)texH / (float)intr.SensorResolution.y;
            fx *= sx; fy *= sy;
            cx *= sx; cy *= sy;
        }

        if (flipImageVerticallyBeforeDetect)
            cy = (texH - 1) - cy;

        if (_cameraMatrixFull == null) _cameraMatrixFull = new Mat(3, 3, CvType.CV_64F);
        _cameraMatrixFull.put(0, 0, fx); _cameraMatrixFull.put(0, 1, 0); _cameraMatrixFull.put(0, 2, cx);
        _cameraMatrixFull.put(1, 0, 0); _cameraMatrixFull.put(1, 1, fy); _cameraMatrixFull.put(1, 2, cy);
        _cameraMatrixFull.put(2, 0, 0); _cameraMatrixFull.put(2, 1, 0); _cameraMatrixFull.put(2, 2, 1);

        _intrinsicsReady = true;
        return true;
    }

    void EnsureBuffers(int w, int h)
    {
        if (_rt != null && (_texW == w && _texH == h)) return;

        _texW = w; _texH = h;

        if (_rt != null) _rt.Release();
        _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        _rt.Create();

        if (_cpuTex != null) Destroy(_cpuTex);
        _cpuTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
        _grayMat = new Mat(h, w, CvType.CV_8UC1);

        _intrinsicsReady = false;

        if (preview != null) preview.texture = _rt;
    }

    void ClearMatList(List<Mat> list)
    {
        for (int i = 0; i < list.Count; i++) list[i]?.Dispose();
        list.Clear();
    }

    void SetStatus(string s)
    {
        if (statusText != null) statusText.text = s;
    }

    void ApplyOneEuroSmoothing(ref Vector3 rawPosW, ref Quaternion rawRotW)
    {
        float now = Time.time;
        float dt = (_hasFilterState) ? (now - _lastFilterTime) : 0f;

        if (!_hasFilterState || dt <= 1e-6f)
        {
            _hasFilterState = true;
            _lastFilterTime = now;

            _fPosW = rawPosW;
            _fRotW = rawRotW;

            _prevRawPosW = rawPosW;
            _prevRawRotW = rawRotW;

            _fVelW = Vector3.zero;
            _fAngVelDeg = 0f;

            rawPosW = _fPosW;
            rawRotW = _fRotW;
            return;
        }

        Vector3 vel = (rawPosW - _prevRawPosW) / dt;
        float aDeriv = AlphaFromCutoff(derivCutoffHz, dt);
        _fVelW = LowPass(_fVelW, vel, aDeriv);
        float speed = _fVelW.magnitude;

        float posCutoff = Mathf.Max(1e-3f, posMinCutoffHz + posBeta * speed);
        float aPos = AlphaFromCutoff(posCutoff, dt);
        _fPosW = LowPass(_fPosW, rawPosW, aPos);

        float angDeg = Quaternion.Angle(_prevRawRotW, rawRotW);
        float angVelDeg = angDeg / dt;
        _fAngVelDeg = LowPass(_fAngVelDeg, angVelDeg, aDeriv);

        float rotCutoff = Mathf.Max(1e-3f, rotMinCutoffHz + rotBeta * _fAngVelDeg);
        float aRot = AlphaFromCutoff(rotCutoff, dt);

        Quaternion target = EnsureShortestPath(_fRotW, rawRotW);
        _fRotW = Quaternion.Slerp(_fRotW, target, aRot);

        _prevRawPosW = rawPosW;
        _prevRawRotW = rawRotW;
        _lastFilterTime = now;

        rawPosW = _fPosW;
        rawRotW = _fRotW;
    }

    static float AlphaFromCutoff(float cutoffHz, float dt)
    {
        float tau = 1.0f / (2.0f * Mathf.PI * cutoffHz);
        return 1.0f / (1.0f + tau / dt);
    }

    static Vector3 LowPass(Vector3 prev, Vector3 x, float alpha)
    {
        return prev + alpha * (x - prev);
    }

    static float LowPass(float prev, float x, float alpha)
    {
        return prev + alpha * (x - prev);
    }

    static float SmoothStep01(float t)
    {
        return t * t * (3f - 2f * t);
    }

    static Quaternion EnsureShortestPath(Quaternion from, Quaternion to)
    {
        if (Quaternion.Dot(from, to) < 0f)
            to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
        return to;
    }

    void OnDestroy()
    {
        _rgbaMat?.Dispose();
        _grayMat?.Dispose();
        _cameraMatrixFull?.Dispose();
        _distCoeffs?.Dispose();
        _markerIds?.Dispose();

        ClearMatList(_rvecCandidates);
        ClearMatList(_tvecCandidates);

        if (_rt != null) _rt.Release();
        if (_cpuTex != null) Destroy(_cpuTex);
    }
}
