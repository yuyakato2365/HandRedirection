/*
using System;
using System.Collections.Generic;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OpenCVForUnityExample
{
    /// <summary>
    /// QRCodeDetector Example
    /// An example of detecting QRCode using the QRCodeDetector class.
    /// https://github.com/opencv/opencv/blob/master/samples/cpp/qrcode.cpp
    /// </summary>
    [RequireComponent(typeof(MultiSource2MatHelper))]
    public class QRCodeDetectorExample : MonoBehaviour
    {
        // Public Fields
        [Header("Output")]
        /// <summary>
        /// The RawImage for previewing the result.
        /// </summary>
        public RawImage ResultPreview;

        [Space(10)]

        // Private Fields
        /// <summary>
        /// The gray mat.
        /// </summary>
        private Mat _grayMat;

        /// <summary>
        /// The texture.
        /// </summary>
        private Texture2D _texture;

        /// <summary>
        /// The QRCode detector.
        /// </summary>
        private QRCodeDetector _detector;

        /// <summary>
        /// The points.
        /// </summary>
        private Mat _points;

        /// <summary>
        /// The decoded info
        /// </summary>
        private List<string> _decodedInfo;

        /// <summary>
        /// The straight qrcode
        /// </summary>
        private List<Mat> _straightQrcode;

        /// <summary>
        /// The multi source to mat helper.
        /// </summary>
        private MultiSource2MatHelper _multiSource2MatHelper;

        /// <summary>
        /// The FPS monitor.
        /// </summary>
        private FpsMonitor _fpsMonitor;

        // Unity Lifecycle Methods
        private void Start()
        {
            _fpsMonitor = GetComponent<FpsMonitor>();

            _multiSource2MatHelper = gameObject.GetComponent<MultiSource2MatHelper>();
            _multiSource2MatHelper.OutputColorFormat = Source2MatHelperColorFormat.RGBA;

            _detector = new QRCodeDetector();

            _multiSource2MatHelper.Initialize();
        }

        private void Update()
        {
            if (_multiSource2MatHelper.IsPlaying() && _multiSource2MatHelper.DidUpdateThisFrame())
            {

                Mat rgbaMat = _multiSource2MatHelper.GetMat();

                Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

                bool result = _detector.detectAndDecodeMulti(_grayMat, _decodedInfo, _points, _straightQrcode);

                if (result)
                {
                    // Debug.Log(_points.dump());
                    // Debug.Log(_points.ToString());

                    // Debug.Log("_decodedInfo.Count " + _decodedInfo.Count);
                    // Debug.Log("_straightQrcode.Count " + _straightQrcode.Count);

#if NET_STANDARD_2_1 && !OPENCV_DONT_USE_UNSAFE_CODE
                    // draw QRCode contour using non-allocating methods.
                    ReadOnlySpan<float> qrCodeCorners = _points.AsSpan<float>();
#else
                    // draw QRCode contour using allocating methods.
                    float[] qrCodeCorners = new float[_points.total() * _points.channels()];
                    _points.get(0, 0, qrCodeCorners);
#endif

                    // Debug.Log("qrCodeCorners.Length " + qrCodeCorners.Length);

                    for (int i = 0; i < qrCodeCorners.Length; i += 8)
                    {
                        // Draw QR code bounding box by connecting the 4 corners
                        for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                        {
                            int currentCorner = i + cornerIndex * 2;
                            int nextCorner = i + ((cornerIndex + 1) % 4) * 2;

                            Imgproc.line(rgbaMat,
                                new Point(qrCodeCorners[currentCorner], qrCodeCorners[currentCorner + 1]),
                                new Point(qrCodeCorners[nextCorner], qrCodeCorners[nextCorner + 1]),
                                new Scalar(255, 0, 0, 255), 2);
                        }

                        // Display decoded information
                        int qrCodeIndex = i / 8;
                        if (_decodedInfo.Count > qrCodeIndex && _decodedInfo[qrCodeIndex] != null)
                        {
                            Imgproc.putText(rgbaMat, _decodedInfo[qrCodeIndex],
                                new Point(qrCodeCorners[i], qrCodeCorners[i + 1]),
                                Imgproc.FONT_HERSHEY_SIMPLEX, 0.7,
                                new Scalar(255, 255, 255, 255), 2, Imgproc.LINE_AA, false);
                        }
                    }

                    // Display straightQrcode using imshow
                    for (int i = 0; i < _straightQrcode.Count; i++)
                    {
                        DebugMat.imshow("straightQrcode[" + i + "]", _straightQrcode[i], false, null, _decodedInfo[i]);
                    }
                }
                else
                {
                    Imgproc.putText(rgbaMat, "Decoding failed.", new Point(5, rgbaMat.rows() - 10), Imgproc.FONT_HERSHEY_SIMPLEX, 0.7, new Scalar(255, 255, 255, 255), 2, Imgproc.LINE_AA, false);
                }

                OpenCVMatUtils.MatToTexture2D(rgbaMat, _texture);
            }
        }

        private void OnDestroy()
        {
            _multiSource2MatHelper?.Dispose();

            _detector?.Dispose();
        }

        // Public Methods
        /// <summary>
        /// Raises the source to mat helper initialized event.
        /// </summary>
        public void OnSourceToMatHelperInitialized()
        {
            Debug.Log("OnSourceToMatHelperInitialized");

            Mat rgbaMat = _multiSource2MatHelper.GetMat();

            _texture = new Texture2D(rgbaMat.cols(), rgbaMat.rows(), TextureFormat.RGBA32, false);
            OpenCVMatUtils.MatToTexture2D(rgbaMat, _texture);

            ResultPreview.texture = _texture;
            ResultPreview.GetComponent<AspectRatioFitter>().aspectRatio = (float)_texture.width / _texture.height;

            if (_fpsMonitor != null)
            {
                _fpsMonitor.Add("width", rgbaMat.width().ToString());
                _fpsMonitor.Add("height", rgbaMat.height().ToString());
                _fpsMonitor.Add("orientation", Screen.orientation.ToString());
            }

            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);

            _points = new Mat();
            _decodedInfo = new List<string>();
            _straightQrcode = new List<Mat>();

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            // If the WebCam is front facing, flip the Mat horizontally. Required for successful detection.
            if (_multiSource2MatHelper.Source2MatHelper is WebCamTexture2MatHelper webCamHelper)
                webCamHelper.FlipHorizontal = webCamHelper.IsFrontFacing();
#endif
        }

        /// <summary>
        /// Raises the source to mat helper disposed event.
        /// </summary>
        public void OnSourceToMatHelperDisposed()
        {
            Debug.Log("OnSourceToMatHelperDisposed");

            _grayMat?.Dispose();

            if (_texture != null) Texture2D.Destroy(_texture); _texture = null;

            _points?.Dispose();

            _decodedInfo?.Clear();

            foreach (var item in _straightQrcode)
            {
                item?.Dispose();
            }
            _straightQrcode?.Clear();
        }

        /// <summary>
        /// Raises the source to mat helper error occurred event.
        /// </summary>
        /// <param name="errorCode">Error code.</param>
        /// <param name="message">Message.</param>
        public void OnSourceToMatHelperErrorOccurred(Source2MatHelperErrorCode errorCode, string message)
        {
            Debug.Log("OnSourceToMatHelperErrorOccurred " + errorCode + ":" + message);

            if (_fpsMonitor != null)
            {
                _fpsMonitor.ConsoleText = "ErrorCode: " + errorCode + ":" + message;
            }
        }

        /// <summary>
        /// Raises the back button click event.
        /// </summary>
        public void OnBackButtonClick()
        {
            SceneManager.LoadScene("OpenCVForUnityExample");
        }

        /// <summary>
        /// Raises the play button click event.
        /// </summary>
        public void OnPlayButtonClick()
        {
            _multiSource2MatHelper.Play();
        }

        /// <summary>
        /// Raises the pause button click event.
        /// </summary>
        public void OnPauseButtonClick()
        {
            _multiSource2MatHelper.Pause();
        }

        /// <summary>
        /// Raises the stop button click event.
        /// </summary>
        public void OnStopButtonClick()
        {
            _multiSource2MatHelper.Stop();
        }

        /// <summary>
        /// Raises the change camera button click event.
        /// </summary>
        public void OnChangeCameraButtonClick()
        {
            _multiSource2MatHelper.RequestedIsFrontFacing = !_multiSource2MatHelper.RequestedIsFrontFacing;
        }
    }
}
*/

/*
using System;
using System.Collections.Generic;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OpenCVForUnityExample
{
    /// <summary>
    /// QRCodeDetector Example + PnP pose estimation + Unity Transform binding.
    /// </summary>
    public class QRCodeDetectorExample : MonoBehaviour
    {
        [Header("UI")]
        public RawImage ResultPreview;

        [Header("Pose Estimation")]
        [Tooltip("Real-world length of one side of the QR code in meters.")]
        public float qrSizeMeters = 0.08f;  // 8cm
        [Tooltip("Assumed vertical field of view of the camera in degrees.")]
        public float cameraFovY = 60f;

        [Header("Unity Binding (WebCam debug)")]
        [Tooltip("PnP���ʂ�R�t����Unity���̃J�����i�ʏ��Main Camera�j")]
        public Camera unityCamera;

        [Tooltip("QR�R�[�h�ʂ̒��S���p����\���A���J�[(GameObject)")]
        public Transform qrAnchor;

        [Tooltip("�����L���[�u�ɑΉ�������Unity��Cube�iqrAnchor�̎q�ɂȂ�j")]
        public Transform cube;

        // Texture used to display the camera / processed image.
        Texture2D _texture;

        // OpenCV objects.
        Mat _grayMat;
        Mat _points;
        readonly List<string> _decodedInfo = new List<string>();
        readonly List<Mat> _straightQrcode = new List<Mat>();
        QRCodeDetector _detector;

        // Camera parameters for solvePnP.
        Mat _cameraMatrix;
        MatOfDouble _distCoeffs;

        // Helpers from OpenCV for Unity.
        MultiSource2MatHelper _multiSource2MatHelper;
        FpsMonitor _fpsMonitor;

        // --------------------------------------------------------------------
        // Unity lifecycle
        // --------------------------------------------------------------------

        void Start()
        {
            _fpsMonitor = GetComponent<FpsMonitor>();
            _multiSource2MatHelper = GetComponent<MultiSource2MatHelper>();

            if (_multiSource2MatHelper == null)
            {
                Debug.LogError("MultiSource2MatHelper component is missing.");
                enabled = false;
                return;
            }

            // OnSourceToMatHelperInitialized / Disposed / ErrorOccurred ��
            // Inspector �� UnityEvent ���炱�̃N���X�̃��\�b�h�ɕR�t���Ă����B
            if (!_multiSource2MatHelper.IsInitialized())
            {
                _multiSource2MatHelper.Initialize();
            }
        }

        void Update()
        {
            if (_multiSource2MatHelper == null ||
                !_multiSource2MatHelper.IsPlaying() ||
                !_multiSource2MatHelper.DidUpdateThisFrame())
            {
                return;
            }

            Mat rgbaMat = _multiSource2MatHelper.GetMat();
            if (rgbaMat == null || rgbaMat.empty())
                return;

            // RGBA �� Gray
            Imgproc.cvtColor(rgbaMat, _grayMat, Imgproc.COLOR_RGBA2GRAY);

            // �O�t���[���̌��ʂ��N���A
            _decodedInfo.Clear();
            foreach (var m in _straightQrcode) m.Dispose();
            _straightQrcode.Clear();
            if (_points == null)
            {
                _points = new Mat();
            }
            else
            {
                _points.release();
                _points = new Mat();
            }

            // ����QR���o
            bool result = _detector.detectAndDecodeMulti(_grayMat, _decodedInfo, _points, _straightQrcode);

            //Debug.Log($"detectAndDecodeMulti: result={result}, points={_points.total()}");

            if (result && !_points.empty())
            {
#if NET_STANDARD_2_1 && !OPENCV_DONT_USE_UNSAFE_CODE
                ReadOnlySpan<float> qrCodeCorners = _points.AsSpan<float>();
#else
                float[] qrCodeCorners = new float[_points.total() * _points.channels()];
                _points.get(0, 0, qrCodeCorners);
#endif
                for (int i = 0; i < qrCodeCorners.Length; i += 8)
                {
                    // ---- �g���`�� ----
                    for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                    {
                        int currentCorner = i + cornerIndex * 2;
                        int nextCorner = i + ((cornerIndex + 1) % 4) * 2;

                        Imgproc.line(
                            rgbaMat,
                            new Point(qrCodeCorners[currentCorner], qrCodeCorners[currentCorner + 1]),
                            new Point(qrCodeCorners[nextCorner], qrCodeCorners[nextCorner + 1]),
                            new Scalar(255, 0, 0, 255), 2);
                    }

                    int qrCodeIndex = i / 8;
                    if (_decodedInfo.Count > qrCodeIndex && _decodedInfo[qrCodeIndex] != null)
                    {
                        Imgproc.putText(
                            rgbaMat, _decodedInfo[qrCodeIndex],
                            new Point(qrCodeCorners[i], qrCodeCorners[i + 1]),
                            Imgproc.FONT_HERSHEY_SIMPLEX, 0.7,
                            new Scalar(255, 255, 255, 255), 2,
                            Imgproc.LINE_AA, false);
                    }

                    // ---- PnP �p������ ----
                    if (_cameraMatrix != null)
                    {
                        // 2D �摜���W�i������3D�_�ƍ��킹��j
                        Point[] imagePoints = new Point[]
                        {
                            new Point(qrCodeCorners[i + 0], qrCodeCorners[i + 1]), // ����
                            new Point(qrCodeCorners[i + 2], qrCodeCorners[i + 3]), // �E��
                            new Point(qrCodeCorners[i + 4], qrCodeCorners[i + 5]), // �E��
                            new Point(qrCodeCorners[i + 6], qrCodeCorners[i + 7]), // ����
                        };

                        // 3D���̍��W�FQR����(z=0)��A���S���_
                        float s = qrSizeMeters;
                        Point3[] objectPoints = new Point3[]
                        {
                            new Point3(-s/2f,  s/2f, 0), // ����
                            new Point3( s/2f,  s/2f, 0), // �E��
                            new Point3( s/2f, -s/2f, 0), // �E��
                            new Point3(-s/2f, -s/2f, 0), // ����
                        };

                        using (MatOfPoint2f imagePtsMat = new MatOfPoint2f())
                        using (MatOfPoint3f objectPtsMat = new MatOfPoint3f())
                        using (Mat rvec = new Mat())
                        using (Mat tvec = new Mat())
                        {
                            imagePtsMat.fromArray(imagePoints);
                            objectPtsMat.fromArray(objectPoints);

                            bool pnpOk = Calib3d.solvePnP(
                                objectPtsMat,
                                imagePtsMat,
                                _cameraMatrix,
                                _distCoeffs,
                                rvec,
                                tvec,
                                false,
                                Calib3d.SOLVEPNP_IPPE_SQUARE); // ���ʃX�N�G�A�p

                            if (pnpOk)
                            {
                                double[] t = new double[3];
                                tvec.get(0, 0, t);
                                Debug.Log(
                                    $"[PnP OK] QR[{qrCodeIndex}] tvec = ({t[0]:F3}, {t[1]:F3}, {t[2]:F3}) m");

                                // �� Unity���̃A���J�[Transform���X�V
                                UpdateQrAnchorFromPnP(rvec, tvec);
                            }
                            else
                            {
                                Debug.LogWarning($"[PnP FAIL] QR[{qrCodeIndex}]");
                            }
                        }
                    }
                    else
                    {
                        Debug.LogWarning("cameraMatrix is null");
                    }
                }
            }
            else
            {
                Imgproc.putText(
                    rgbaMat, "Decoding failed.",
                    new Point(5, rgbaMat.rows() - 10),
                    Imgproc.FONT_HERSHEY_SIMPLEX, 0.7,
                    new Scalar(255, 255, 255, 255), 2,
                    Imgproc.LINE_AA, false);
            }

            // ���ʂ��e�N�X�`���ɔ��f
            if (_texture != null)
            {
                OpenCVMatUtils.MatToTexture2D(rgbaMat, _texture);
            }
        }

        void OnDestroy()
        {
            if (_multiSource2MatHelper != null)
                _multiSource2MatHelper.Dispose();

            if (_texture != null) Destroy(_texture);
            if (_grayMat != null) { _grayMat.Dispose(); _grayMat = null; }
            if (_points != null) { _points.Dispose(); _points = null; }
            foreach (var m in _straightQrcode) m.Dispose();
            _straightQrcode.Clear();
            if (_cameraMatrix != null) { _cameraMatrix.Dispose(); _cameraMatrix = null; }
            if (_distCoeffs != null) { _distCoeffs.Dispose(); _distCoeffs = null; }
        }

        // --------------------------------------------------------------------
        // Callbacks from MultiSource2MatHelper
        // �iInspector �� UnityEvent �ɕR�t���Ă����j
        // --------------------------------------------------------------------

        public void OnSourceToMatHelperInitialized()
        {
            Debug.Log("OnSourceToMatHelperInitialized");

            Mat rgbaMat = _multiSource2MatHelper.GetMat();

            _texture = new Texture2D(rgbaMat.cols(), rgbaMat.rows(), TextureFormat.RGBA32, false);
            ResultPreview.texture = _texture;
            ResultPreview.rectTransform.sizeDelta =
                new Vector2(rgbaMat.cols(), rgbaMat.rows());

            _grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);

            _points?.Dispose();
            _points = new Mat();
            _decodedInfo.Clear();
            foreach (var m in _straightQrcode) m.Dispose();
            _straightQrcode.Clear();

            _detector = new QRCodeDetector();

            InitCameraMatrix(rgbaMat.cols(), rgbaMat.rows());

            // �� Unity���̐e�q�\�����Z�b�g
            if (unityCamera != null && qrAnchor != null)
            {
                qrAnchor.SetParent(unityCamera.transform, worldPositionStays: false);
                qrAnchor.localPosition = Vector3.zero;
                qrAnchor.localRotation = Quaternion.identity;
            }

            if (qrAnchor != null && cube != null)
            {
                cube.SetParent(qrAnchor, worldPositionStays: false);

                float cubeSize = qrSizeMeters;
                cube.localScale = new Vector3(cubeSize, cubeSize, cubeSize);

                // QR����(z=0)��Cube�O�ʒ����ƈ�v����悤�ɁAz�����ɔ������炷
                cube.localPosition = new Vector3(0f, 0f, cubeSize / 2f);
                cube.localRotation = Quaternion.identity;
            }
        }

        public void OnSourceToMatHelperDisposed()
        {
            Debug.Log("OnSourceToMatHelperDisposed");

            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }

            if (_grayMat != null) { _grayMat.Dispose(); _grayMat = null; }
            if (_points != null) { _points.Dispose(); _points = null; }
            foreach (var m in _straightQrcode) m.Dispose();
            _straightQrcode.Clear();
        }

        public void OnSourceToMatHelperErrorOccurred(Source2MatHelperErrorCode errorCode, string message)
        {
            Debug.LogError($"Source2MatHelperErrorOccurred: {errorCode} {message}");
        }

        // --------------------------------------------------------------------
        // UI button handlers�i���T���v���Ɠ����\���j
        // --------------------------------------------------------------------

        public void OnBackButtonClick()
        {
            SceneManager.LoadScene("OpenCVForUnityExample");
        }

        public void OnPlayButtonClick()
        {
            if (_multiSource2MatHelper != null && !_multiSource2MatHelper.IsPlaying())
                _multiSource2MatHelper.Play();
        }

        public void OnPauseButtonClick()
        {
            if (_multiSource2MatHelper != null && _multiSource2MatHelper.IsPlaying())
                _multiSource2MatHelper.Pause();
        }

        public void OnStopButtonClick()
        {
            if (_multiSource2MatHelper != null)
                _multiSource2MatHelper.Stop();
        }

        public void OnChangeCameraButtonClick()
        {
            if (_multiSource2MatHelper != null)
                _multiSource2MatHelper.RequestedIsFrontFacing =
                    !_multiSource2MatHelper.RequestedIsFrontFacing;
        }

        // --------------------------------------------------------------------
        // Camera intrinsics (�ȈՔ�)
        // --------------------------------------------------------------------

        void InitCameraMatrix(int width, int height)
        {
            double fy = (height / 2.0) /
                        Mathf.Tan(0.5f * cameraFovY * Mathf.Deg2Rad);
            double fx = fy;
            double cx = width / 2.0;
            double cy = height / 2.0;

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
        }

        // --------------------------------------------------------------------
        // PnP �� Unity Transform �ϊ�
        // --------------------------------------------------------------------

        /// <summary>
        /// solvePnP �� rvec/tvec �� Unity�J�������W�n�� Transform �ɕϊ����� qrAnchor �ɓK�p
        /// </summary>
        void UpdateQrAnchorFromPnP(Mat rvec, Mat tvec)
        {
            if (unityCamera == null || qrAnchor == null) return;
            if (rvec == null || tvec == null || rvec.empty() || tvec.empty()) return;

            // --- �ʒu�x�N�g��: OpenCV �� Unity (y�������]) ---
            double[] t = new double[3];
            tvec.get(0, 0, t);

            // OpenCV: (x, y��, z) �� Unity: (x, y��, z)
            Vector3 posCamUnity = new Vector3(
                (float)t[0],
                (float)(-t[1]),
                (float)t[2]
            );

            // --- ��]: Rodrigues �� R_cv �� R_u = C R_cv C ---
            Mat R_cv = new Mat(3, 3, CvType.CV_64F);
            Calib3d.Rodrigues(rvec, R_cv);

            double[] r = new double[9];
            R_cv.get(0, 0, r);
            // R_cv =
            // [ r0 r1 r2 ]
            // [ r3 r4 r5 ]
            // [ r6 r7 r8 ]

            // C = diag(1,-1,1) �����E����|��������
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = (float)r[0]; m.m01 = (float)(-r[1]); m.m02 = (float)r[2];
            m.m10 = (float)(-r[3]); m.m11 = (float)r[4]; m.m12 = (float)(-r[5]);
            m.m20 = (float)r[6]; m.m21 = (float)(-r[7]); m.m22 = (float)r[8];

            Vector3 forward = m.GetColumn(2); // Z��
            Vector3 up = m.GetColumn(1); // Y��
            Quaternion rotCamUnity = Quaternion.LookRotation(forward, up);

            // --- qrAnchor �� unityCamera �̎q�Ȃ̂� local �ɂ��̂܂ܓK�p ---
            qrAnchor.localPosition = posCamUnity;
            qrAnchor.localRotation = rotCamUnity;
        }
    }
}
*/