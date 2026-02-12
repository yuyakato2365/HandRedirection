/*
// Extension of FromOVRHandDataSource.
using System;
using System.Linq;
using Meta.XR.Util;
using UnityEngine;
using UnityEngine.Assertions;

using Oculus.Interaction;
using Oculus.Interaction.OVR;
using Oculus.Interaction.Input;

namespace Hitchhike
{
  public class HitchhikeFromOVRHandDataSource : DataSource<HandDataAsset>
  {
    [Header("OVR Data Source")]
    [SerializeField, Interface(typeof(IOVRCameraRigRef))]
    public UnityEngine.Object _cameraRigRef;

    [SerializeField]
    private bool _processLateUpdates;

    [Header("Shared Configuration")]
    [SerializeField]
    private Oculus.Interaction.Input.Handedness _handedness; //１

    [SerializeField]
    private OVRHand _ovrHand;

    [SerializeField, Interface(typeof(ITrackingToWorldTransformer))]
    private UnityEngine.Object _trackingToWorldTransformer;//2

    private ITrackingToWorldTransformer TrackingToWorldTransformer;

    [SerializeField, Interface(typeof(IHandSkeletonProvider))]
    private UnityEngine.Object _handSkeletonProvider;

    private IHandSkeletonProvider HandSkeletonProvider;

    private readonly HandDataAsset _handDataAsset = new HandDataAsset();

    private float _lastHandScale;

    private HandDataSourceConfig _config;

    private IOVRCameraRigRef CameraRigRef;

    public bool ProcessLateUpdates
    {
      get
      {
        return _processLateUpdates;
      }
      set
      {
        _processLateUpdates = value;
      }
    }

    protected override HandDataAsset DataAsset => _handDataAsset;

    public static Quaternion WristFixupRotation { get; } = new Quaternion(0f, 1f, 0f, 0f);

    private HandDataSourceConfig Config 
    {
      get
      {
        if (_config != null)
        {
          return _config;
        }

        _config = new HandDataSourceConfig
        {
          Handedness = _handedness
        };
        return _config;
      }
    }

    //3
    public Transform originalSpace;
    public Transform thisSpace;
    public bool isUpdating = true;
    public Vector3 defaultPosition = Vector3.zero;
    public bool scaleHandModel;
    public float filterRatio = 1f;
    public Pose rawHandPose { get; private set; }
    private Vector3 filteredPosition;
    private Quaternion filteredRotation;

    protected virtual void Awake()
    {
      TrackingToWorldTransformer = _trackingToWorldTransformer as ITrackingToWorldTransformer;
      CameraRigRef = _cameraRigRef as IOVRCameraRigRef;
      HandSkeletonProvider = _handSkeletonProvider as IHandSkeletonProvider;
      UpdateConfig();

      //originalSpace = transform;
      //thisSpace = transform;
      filteredPosition = defaultPosition;
      filteredRotation = Quaternion.identity;
    }

    protected override void Start()
    {
      this.BeginStart(ref _started, delegate
      {
        base.Start();
      });
      this.AssertField(CameraRigRef, "CameraRigRef");
      this.AssertField(TrackingToWorldTransformer, "TrackingToWorldTransformer");
      this.AssertField(HandSkeletonProvider, "HandSkeletonProvider");
      if (_ovrHand == null)
      {
        _ovrHand = (_handedness == Oculus.Interaction.Input.Handedness.Left) ? CameraRigRef.LeftHand : CameraRigRef.RightHand;
      }

      this.AssertField(_ovrHand, "_ovrHand");
      UpdateConfig();
      Assert.AreEqual(OVRRuntimeSettings.GetRuntimeSettings().HandSkeletonVersion, OVRHandSkeletonVersion.OpenXR, $"Hand Skeleton Version in OVRManager must be set to {OVRHandSkeletonVersion.OpenXR}.");
      this.EndStart(ref _started);
    }//4

    protected override void OnEnable()
    {
      base.OnEnable();
      if (_started)
      {
        CameraRigRef.WhenInputDataDirtied += HandleInputDataDirtied;
      }
    }

    protected override void OnDisable()
    {
      if (_started)
      {
        CameraRigRef.WhenInputDataDirtied -= HandleInputDataDirtied;
      }

      base.OnDisable();
      MarkInputDataRequiresUpdate();
    }

    private void HandleInputDataDirtied(bool isLateUpdate)
    {
      if (!isLateUpdate || _processLateUpdates)
      {
        MarkInputDataRequiresUpdate();
      }
    }//6

    private void UpdateConfig()
    {
      Config.Handedness = _handedness;
      Config.TrackingToWorldTransformer = TrackingToWorldTransformer;
      Config.HandSkeleton = HandSkeletonProvider[_handedness];
    }

    protected override void UpdateData()
    {
        // === 追加: 手の更新を一時停止するための制御 ===
        if (!isUpdating)
        {
            _handDataAsset.Root = new Pose()
            {
                position = defaultPosition,
                rotation = _handDataAsset.Root.rotation
            };
            return;
        }

        // === 元コードと同様 ===
        _handDataAsset.Config = Config;
        _handDataAsset.IsDataValid = true;
        _handDataAsset.IsConnected = false;

        if (_ovrHand != null && _ovrHand.isActiveAndEnabled && base.isActiveAndEnabled)
        {
            OVRSkeleton.SkeletonPoseData skeletonPoseData =
                ((OVRSkeleton.IOVRSkeletonDataProvider)_ovrHand).GetSkeletonPoseData();

            _handDataAsset.IsConnected = skeletonPoseData.IsDataValid && skeletonPoseData.RootScale > 0f;
            if (!_handDataAsset.IsConnected)
            {
                if (_lastHandScale <= 0f)
                {
                    skeletonPoseData.IsDataValid = false;
                }
                else
                {
                    skeletonPoseData.RootScale = _lastHandScale;
                }
            }
            else
            {
                _lastHandScale = skeletonPoseData.RootScale;
            }

            if (skeletonPoseData.IsDataValid && _handDataAsset.IsConnected)
            {
                UpdateDataPoses(skeletonPoseData);
                return;
            }
        }

        // === データ無効時の初期化 ===
        _handDataAsset.IsConnected = false;
        _handDataAsset.IsTracked = false;
        _handDataAsset.RootPoseOrigin = PoseOrigin.None;
        _handDataAsset.PointerPoseOrigin = PoseOrigin.None;
        _handDataAsset.IsHighConfidence = false;
        for (int i = 0; i < 5; i++)
        {
            _handDataAsset.IsFingerPinching[i] = false;
            _handDataAsset.IsFingerHighConfidence[i] = false;
        }
    }


    private Pose ApplySpaceMap(Transform original, Transform target, Vector3 pos, Quaternion rot)
    {
        if (original == null || target == null)
            return new Pose(pos, rot);

        Vector3 pLocal = original.InverseTransformPoint(pos);
        Quaternion rLocal = Quaternion.Inverse(original.rotation) * rot;

        Vector3 pMapped = target.TransformPoint(pLocal);
        Quaternion rMapped = target.rotation * rLocal;

        return new Pose(pMapped, rMapped);
    }

    private void UpdateDataPoses(OVRSkeleton.SkeletonPoseData poseData)
    {
        _handDataAsset.HandScale = scaleHandModel ? new float[] {
            thisSpace.lossyScale.x / originalSpace.lossyScale.x,
            thisSpace.lossyScale.y / originalSpace.lossyScale.y,
            thisSpace.lossyScale.z / originalSpace.lossyScale.z
        }.Average() : poseData.RootScale;

        _handDataAsset.IsTracked = _ovrHand.IsTracked;
        _handDataAsset.IsHighConfidence = poseData.IsDataHighConfidence;
        _handDataAsset.IsDominantHand = _ovrHand.IsDominantHand;
        _handDataAsset.RootPoseOrigin = (_handDataAsset.IsTracked ? PoseOrigin.RawTrackedPose : PoseOrigin.None);

        // === Rootの変換 ===
        Pose rootPoseWorld = new Pose(
            poseData.RootPose.Position.FromFlippedZVector3f(),
            poseData.RootPose.Orientation.FromFlippedZQuatf()
        );
        _handDataAsset.Root = ApplySpaceMap(originalSpace, thisSpace, rootPoseWorld.position, rootPoseWorld.rotation);

        // === Pointer Pose ===
        if (_ovrHand.IsPointerPoseValid)
        {
            var p = _ovrHand.PointerPose;
            _handDataAsset.PointerPoseOrigin = PoseOrigin.RawTrackedPose;
            _handDataAsset.PointerPose = ApplySpaceMap(originalSpace, thisSpace, p.position, p.rotation);
        }
        else
        {
            _handDataAsset.PointerPoseOrigin = PoseOrigin.None;
        }

        OVRPlugin.Skeleton2 ovrSkeleton =
            (_handedness == Oculus.Interaction.Input.Handedness.Left)
            ? OVRSkeletonData.LeftSkeleton
            : OVRSkeletonData.RightSkeleton;

        // === 各Joint（関節）の姿勢を変換 ===
        for (int j = 0; j < 26; j++)
        {
            Vector3 bonePos = poseData.BoneTranslations[j].FromFlippedZVector3f();
            Quaternion boneRot = poseData.BoneRotations[j].FromFlippedZQuatf();

            // ワールド→thisSpaceへの空間変換
            Pose mapped = ApplySpaceMap(originalSpace, thisSpace, bonePos, boneRot);

            // Root基準の相対Poseを作成
            Pose fromRoot = PoseUtils.Delta(_handDataAsset.Root, mapped);
            fromRoot.position /= _handDataAsset.HandScale;

            _handDataAsset.JointPoses[j] = fromRoot;
            _handDataAsset.JointRadii[j] = GetBoneRadius(in ovrSkeleton, j);
        }

        // === 掴み判定用Joint姿勢を明示的にthisSpace基準で再反映 ===
        for (int i = 0; i < _handDataAsset.JointPoses.Length; i++)
        {
            Pose p = _handDataAsset.JointPoses[i];
            _handDataAsset.JointPoses[i] = new Pose(
                thisSpace.TransformPoint(p.position),
                thisSpace.rotation * p.rotation
            );
        }

        // === 指のピンチ判定 ===
        for (int i = 0; i < 5; i++)
        {
            OVRHand.HandFinger finger = (OVRHand.HandFinger)i;
            _handDataAsset.IsFingerPinching[i] = _ovrHand.GetFingerIsPinching(finger);
            _handDataAsset.IsFingerHighConfidence[i] =
                _ovrHand.GetFingerConfidence(finger) == OVRHand.TrackingConfidence.High;
            _handDataAsset.FingerPinchStrength[i] = _ovrHand.GetFingerPinchStrength(finger);
        }

        // === Joint回転をSDK内部形式に変換 ===
        HandJointUtils.WristJointPosesToLocalRotations(
            _handDataAsset.JointPoses,
            ref _handDataAsset.Joints
        );
    }

//9

    // from HandSkeletonOVR
    internal static float GetBoneRadius(in OVRPlugin.Skeleton2 ovrSkeleton, int boneIndex)
    {
      if (boneIndex == 6)
      {
        boneIndex = 7;
      }
      else if (boneIndex == 11)
      {
        boneIndex = 12;
      }
      else if (boneIndex == 16)
      {
        boneIndex = 17;
      }
      else if (boneIndex == 21)
      {
        boneIndex = 22;
      }

      int num = Array.FindIndex(ovrSkeleton.BoneCapsules, (OVRPlugin.BoneCapsule c) => c.BoneIndex == boneIndex);
      if (num >= 0)
      {
        return ovrSkeleton.BoneCapsules[num].Radius;
      }

      return 0f;
    }

    Pose applyOffset(Vector3 anchorPos, Quaternion anchorRot)
    {
      // update raw hand pose
      rawHandPose = new Pose(anchorPos, anchorRot);

      var originalSpaceOrigin = originalSpace;
      var thisSpaceOrigin = thisSpace;

      var originalToActiveRot = Quaternion.Inverse(thisSpaceOrigin.rotation) * originalSpaceOrigin.rotation;
      var originalToActiveScale = new Vector3(
        thisSpaceOrigin.lossyScale.x / originalSpaceOrigin.lossyScale.x,
        thisSpaceOrigin.lossyScale.y / originalSpaceOrigin.lossyScale.y,
        thisSpaceOrigin.lossyScale.z / originalSpaceOrigin.lossyScale.z
      );

      var oMt = Matrix4x4.TRS(
        anchorPos,
        anchorRot,
        new Vector3(1, 1, 1)
      );

      var resMat =
      Matrix4x4.Translate(thisSpaceOrigin.position - originalSpaceOrigin.position) // orignal to copied translation
      * Matrix4x4.TRS(
          originalSpaceOrigin.position,
          Quaternion.Inverse(originalToActiveRot),
          originalToActiveScale
      ) // translation back to original space and rotation & scale around original space
      * Matrix4x4.Translate(-originalSpaceOrigin.position) // offset translation for next step
      * oMt; // hand anchor

      filteredPosition = filteredPosition * (1 - filterRatio) + resMat.GetPosition() * filterRatio;
      filteredRotation = Quaternion.Lerp(filteredRotation, resMat.rotation, filterRatio);

      return new Pose(
        filteredPosition,
        filteredRotation
      );
    }

    public void InjectAllFromOVRHandDataSource(UpdateModeFlags updateMode, IDataSource updateAfter, Oculus.Interaction.Input.Handedness handedness, ITrackingToWorldTransformer trackingToWorldTransformer, IHandSkeletonProvider handSkeletonProvider)
    {
      InjectAllDataSource(updateMode, updateAfter);
      InjectHandedness(handedness);
      InjectTrackingToWorldTransformer(trackingToWorldTransformer);
      InjectHandSkeletonProvider(handSkeletonProvider);
    }

    public void InjectHandedness(Oculus.Interaction.Input.Handedness handedness)
    {
      _handedness = handedness;
    }

    public void InjectTrackingToWorldTransformer(ITrackingToWorldTransformer trackingToWorldTransformer)
    {
      _trackingToWorldTransformer = trackingToWorldTransformer as UnityEngine.Object;
      TrackingToWorldTransformer = trackingToWorldTransformer;
    }

    public void InjectHandSkeletonProvider(IHandSkeletonProvider handSkeletonProvider)
    {
      _handSkeletonProvider = handSkeletonProvider as UnityEngine.Object;
      HandSkeletonProvider = handSkeletonProvider;
    }

    public void InjectOptionalOVRHand(OVRHand ovrHand)
    {
      _ovrHand = ovrHand;
    }
  }
}
*/

// HitchhikeFromOVRHandDataSource.cs
// Extension of FromOVRHandDataSource.
using System;
using System.Linq;
using Meta.XR.Util;
using UnityEngine;
using UnityEngine.Assertions;

using Oculus.Interaction;
using Oculus.Interaction.OVR;
using Oculus.Interaction.Input;

namespace Hitchhike
{
    public class HitchhikeFromOVRHandDataSource : DataSource<HandDataAsset>
    {
        [Header("OVR Data Source")]
        [SerializeField, Interface(typeof(IOVRCameraRigRef))]
        public UnityEngine.Object _cameraRigRef;

        [SerializeField]
        private bool _processLateUpdates;

        [Header("Shared Configuration")]
        [SerializeField]
        private Oculus.Interaction.Input.Handedness _handedness;

        [SerializeField]
        private OVRHand _ovrHand;

        [SerializeField, Interface(typeof(ITrackingToWorldTransformer))]
        private UnityEngine.Object _trackingToWorldTransformer;

        private ITrackingToWorldTransformer TrackingToWorldTransformer;

        [SerializeField, Interface(typeof(IHandSkeletonProvider))]
        private UnityEngine.Object _handSkeletonProvider;

        private IHandSkeletonProvider HandSkeletonProvider;

        private readonly HandDataAsset _handDataAsset = new HandDataAsset();

        private float _lastHandScale;

        private HandDataSourceConfig _config;

        private IOVRCameraRigRef CameraRigRef;

        public bool ProcessLateUpdates
        {
            get { return _processLateUpdates; }
            set { _processLateUpdates = value; }
        }

        protected override HandDataAsset DataAsset => _handDataAsset;

        public static Quaternion WristFixupRotation { get; } = new Quaternion(0f, 1f, 0f, 0f);

        private HandDataSourceConfig Config
        {
            get
            {
                if (_config != null) return _config;

                _config = new HandDataSourceConfig
                {
                    Handedness = _handedness
                };
                return _config;
            }
        }

        // =========================================================
        // Space Map (originalSpace -> thisSpace)
        // =========================================================
        [Header("Space Map")]
        [Tooltip("入力手データが定義されている座標空間（基準）")]
        public Transform originalSpace;

        [Tooltip("出力したい座標空間（写像先・適用先）")]
        public Transform thisSpace;

        [Header("Runtime Control")]
        public bool isUpdating = true;
        public Vector3 defaultPosition = Vector3.zero;

        [Tooltip("HandScaleを SpaceMap のスケール比で決める（ON） / poseData.RootScaleを使う（OFF）")]
        public bool scaleHandModel;

        [Range(0f, 1f)]
        public float filterRatio = 1f;

        public Pose rawHandPose { get; private set; }
        private Vector3 filteredPosition;
        private Quaternion filteredRotation;

        // =========================================================
        // ★追加：IndexTip 出力（あなたが用意するTransformへ書き込み）
        // =========================================================
        public enum IndexTipPointSpace
        {
            RawWorld,   // poseData を Unityワールドにしたもの（ApplySpaceMap前）
            ThisSpace   // ApplySpaceMap(originalSpace->thisSpace) 後
        }

        [Header("IndexTip Output")]
        [Tooltip("指先点の位置を格納するTransform（空のGameObjectなどをアサイン）")]
        public Transform indexTipPoint;

        [Tooltip("indexTipPoint に出力する座標の種類")]
        public IndexTipPointSpace indexTipPointSpace = IndexTipPointSpace.RawWorld;

        [Tooltip("poseData.BoneTranslations[] のうち、IndexTip に相当するインデックス（環境依存。合わなければここだけ調整）")]
        public int indexTipBoneIndex = 20;

        [Tooltip("indexTipPoint も姿勢を更新する（回転も欲しい場合）")]
        public bool updateIndexTipRotation = false;

        protected virtual void Awake()
        {
            TrackingToWorldTransformer = _trackingToWorldTransformer as ITrackingToWorldTransformer;
            CameraRigRef = _cameraRigRef as IOVRCameraRigRef;
            HandSkeletonProvider = _handSkeletonProvider as IHandSkeletonProvider;
            UpdateConfig();

            filteredPosition = defaultPosition;
            filteredRotation = Quaternion.identity;
        }

        protected override void Start()
        {
            this.BeginStart(ref _started, delegate
            {
                base.Start();
            });

            this.AssertField(CameraRigRef, "CameraRigRef");
            this.AssertField(TrackingToWorldTransformer, "TrackingToWorldTransformer");
            this.AssertField(HandSkeletonProvider, "HandSkeletonProvider");

            if (_ovrHand == null)
            {
                _ovrHand = (_handedness == Oculus.Interaction.Input.Handedness.Left)
                    ? CameraRigRef.LeftHand
                    : CameraRigRef.RightHand;
            }

            this.AssertField(_ovrHand, "_ovrHand");
            UpdateConfig();

            Assert.AreEqual(
                OVRRuntimeSettings.GetRuntimeSettings().HandSkeletonVersion,
                OVRHandSkeletonVersion.OpenXR,
                $"Hand Skeleton Version in OVRManager must be set to {OVRHandSkeletonVersion.OpenXR}."
            );

            this.EndStart(ref _started);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (_started)
            {
                CameraRigRef.WhenInputDataDirtied += HandleInputDataDirtied;
            }
        }

        protected override void OnDisable()
        {
            if (_started)
            {
                CameraRigRef.WhenInputDataDirtied -= HandleInputDataDirtied;
            }

            base.OnDisable();
            MarkInputDataRequiresUpdate();
        }

        private void HandleInputDataDirtied(bool isLateUpdate)
        {
            if (!isLateUpdate || _processLateUpdates)
            {
                MarkInputDataRequiresUpdate();
            }
        }

        private void UpdateConfig()
        {
            Config.Handedness = _handedness;
            Config.TrackingToWorldTransformer = TrackingToWorldTransformer;
            Config.HandSkeleton = HandSkeletonProvider[_handedness];
        }

        protected override void UpdateData()
        {
            // 手の更新を一時停止
            if (!isUpdating)
            {
                _handDataAsset.Root = new Pose()
                {
                    position = defaultPosition,
                    rotation = _handDataAsset.Root.rotation
                };
                return;
            }

            _handDataAsset.Config = Config;
            _handDataAsset.IsDataValid = true;
            _handDataAsset.IsConnected = false;

            if (_ovrHand != null && _ovrHand.isActiveAndEnabled && base.isActiveAndEnabled)
            {
                OVRSkeleton.SkeletonPoseData skeletonPoseData =
                    ((OVRSkeleton.IOVRSkeletonDataProvider)_ovrHand).GetSkeletonPoseData();

                _handDataAsset.IsConnected = skeletonPoseData.IsDataValid && skeletonPoseData.RootScale > 0f;
                if (!_handDataAsset.IsConnected)
                {
                    if (_lastHandScale <= 0f)
                    {
                        skeletonPoseData.IsDataValid = false;
                    }
                    else
                    {
                        skeletonPoseData.RootScale = _lastHandScale;
                    }
                }
                else
                {
                    _lastHandScale = skeletonPoseData.RootScale;
                }

                if (skeletonPoseData.IsDataValid && _handDataAsset.IsConnected)
                {
                    UpdateDataPoses(skeletonPoseData);
                    return;
                }
            }

            // データ無効時
            _handDataAsset.IsConnected = false;
            _handDataAsset.IsTracked = false;
            _handDataAsset.RootPoseOrigin = PoseOrigin.None;
            _handDataAsset.PointerPoseOrigin = PoseOrigin.None;
            _handDataAsset.IsHighConfidence = false;
            for (int i = 0; i < 5; i++)
            {
                _handDataAsset.IsFingerPinching[i] = false;
                _handDataAsset.IsFingerHighConfidence[i] = false;
            }
        }

        private Pose ApplySpaceMap(Transform original, Transform target, Vector3 pos, Quaternion rot)
        {
            if (original == null || target == null)
                return new Pose(pos, rot);

            Vector3 pLocal = original.InverseTransformPoint(pos);
            Quaternion rLocal = Quaternion.Inverse(original.rotation) * rot;

            Vector3 pMapped = target.TransformPoint(pLocal);
            Quaternion rMapped = target.rotation * rLocal;

            return new Pose(pMapped, rMapped);
        }

        private void UpdateDataPoses(OVRSkeleton.SkeletonPoseData poseData)
        {
            // HandScale
            _handDataAsset.HandScale = scaleHandModel
                ? new float[] {
                    thisSpace != null && originalSpace != null ? (thisSpace.lossyScale.x / originalSpace.lossyScale.x) : 1f,
                    thisSpace != null && originalSpace != null ? (thisSpace.lossyScale.y / originalSpace.lossyScale.y) : 1f,
                    thisSpace != null && originalSpace != null ? (thisSpace.lossyScale.z / originalSpace.lossyScale.z) : 1f
                }.Average()
                : poseData.RootScale;

            _handDataAsset.IsTracked = _ovrHand.IsTracked;
            _handDataAsset.IsHighConfidence = poseData.IsDataHighConfidence;
            _handDataAsset.IsDominantHand = _ovrHand.IsDominantHand;
            _handDataAsset.RootPoseOrigin = (_handDataAsset.IsTracked ? PoseOrigin.RawTrackedPose : PoseOrigin.None);

            // Root (world -> space mapped)
            Pose rootPoseWorld = new Pose(
                poseData.RootPose.Position.FromFlippedZVector3f(),
                poseData.RootPose.Orientation.FromFlippedZQuatf()
            );
            _handDataAsset.Root = ApplySpaceMap(originalSpace, thisSpace, rootPoseWorld.position, rootPoseWorld.rotation);

            // Pointer Pose
            if (_ovrHand.IsPointerPoseValid)
            {
                var p = _ovrHand.PointerPose;
                _handDataAsset.PointerPoseOrigin = PoseOrigin.RawTrackedPose;
                _handDataAsset.PointerPose = ApplySpaceMap(originalSpace, thisSpace, p.position, p.rotation);
            }
            else
            {
                _handDataAsset.PointerPoseOrigin = PoseOrigin.None;
            }

            OVRPlugin.Skeleton2 ovrSkeleton =
                (_handedness == Oculus.Interaction.Input.Handedness.Left)
                ? OVRSkeletonData.LeftSkeleton
                : OVRSkeletonData.RightSkeleton;

            // Joints
            for (int j = 0; j < 26; j++)
            {
                Vector3 bonePos = poseData.BoneTranslations[j].FromFlippedZVector3f();
                Quaternion boneRot = poseData.BoneRotations[j].FromFlippedZQuatf();

                Pose mapped = ApplySpaceMap(originalSpace, thisSpace, bonePos, boneRot);

                Pose fromRoot = PoseUtils.Delta(_handDataAsset.Root, mapped);
                fromRoot.position /= _handDataAsset.HandScale;

                _handDataAsset.JointPoses[j] = fromRoot;
                _handDataAsset.JointRadii[j] = GetBoneRadius(in ovrSkeleton, j);
            }

            // 既存挙動維持：JointPoses を thisSpace 基準で再反映
            for (int i = 0; i < _handDataAsset.JointPoses.Length; i++)
            {
                Pose p = _handDataAsset.JointPoses[i];
                if (thisSpace != null)
                {
                    _handDataAsset.JointPoses[i] = new Pose(
                        thisSpace.TransformPoint(p.position),
                        thisSpace.rotation * p.rotation
                    );
                }
            }

            // ★追加：IndexTip 出力
            UpdateIndexTipPoint(poseData);

            // Pinch
            for (int i = 0; i < 5; i++)
            {
                OVRHand.HandFinger finger = (OVRHand.HandFinger)i;
                _handDataAsset.IsFingerPinching[i] = _ovrHand.GetFingerIsPinching(finger);
                _handDataAsset.IsFingerHighConfidence[i] =
                    _ovrHand.GetFingerConfidence(finger) == OVRHand.TrackingConfidence.High;
                _handDataAsset.FingerPinchStrength[i] = _ovrHand.GetFingerPinchStrength(finger);
            }

            // Joint rotation conversion
            HandJointUtils.WristJointPosesToLocalRotations(
                _handDataAsset.JointPoses,
                ref _handDataAsset.Joints
            );
        }

        private void UpdateIndexTipPoint(OVRSkeleton.SkeletonPoseData poseData)
        {
            if (indexTipPoint == null) return;

            int j = Mathf.Clamp(indexTipBoneIndex, 0, 25);

            // Raw world (from poseData)
            Vector3 tipPosRawW = poseData.BoneTranslations[j].FromFlippedZVector3f();
            Quaternion tipRotRawW = poseData.BoneRotations[j].FromFlippedZQuatf();

            if (indexTipPointSpace == IndexTipPointSpace.RawWorld)
            {
                indexTipPoint.position = tipPosRawW;
                if (updateIndexTipRotation) indexTipPoint.rotation = tipRotRawW;
                return;
            }

            // ThisSpace (ApplySpaceMap)
            Pose tipMapped = ApplySpaceMap(originalSpace, thisSpace, tipPosRawW, tipRotRawW);
            indexTipPoint.position = tipMapped.position;
            if (updateIndexTipRotation) indexTipPoint.rotation = tipMapped.rotation;
        }

        // from HandSkeletonOVR
        internal static float GetBoneRadius(in OVRPlugin.Skeleton2 ovrSkeleton, int boneIndex)
        {
            if (boneIndex == 6) boneIndex = 7;
            else if (boneIndex == 11) boneIndex = 12;
            else if (boneIndex == 16) boneIndex = 17;
            else if (boneIndex == 21) boneIndex = 22;

            int num = Array.FindIndex(ovrSkeleton.BoneCapsules,
                (OVRPlugin.BoneCapsule c) => c.BoneIndex == boneIndex);
            if (num >= 0) return ovrSkeleton.BoneCapsules[num].Radius;
            return 0f;
        }

        Pose applyOffset(Vector3 anchorPos, Quaternion anchorRot)
        {
            rawHandPose = new Pose(anchorPos, anchorRot);

            var originalSpaceOrigin = originalSpace;
            var thisSpaceOrigin = thisSpace;

            var originalToActiveRot = Quaternion.Inverse(thisSpaceOrigin.rotation) * originalSpaceOrigin.rotation;
            var originalToActiveScale = new Vector3(
                thisSpaceOrigin.lossyScale.x / originalSpaceOrigin.lossyScale.x,
                thisSpaceOrigin.lossyScale.y / originalSpaceOrigin.lossyScale.y,
                thisSpaceOrigin.lossyScale.z / originalSpaceOrigin.lossyScale.z
            );

            var oMt = Matrix4x4.TRS(anchorPos, anchorRot, new Vector3(1, 1, 1));

            var resMat =
                Matrix4x4.Translate(thisSpaceOrigin.position - originalSpaceOrigin.position)
                * Matrix4x4.TRS(
                    originalSpaceOrigin.position,
                    Quaternion.Inverse(originalToActiveRot),
                    originalToActiveScale
                )
                * Matrix4x4.Translate(-originalSpaceOrigin.position)
                * oMt;

            filteredPosition = filteredPosition * (1 - filterRatio) + resMat.GetPosition() * filterRatio;
            filteredRotation = Quaternion.Lerp(filteredRotation, resMat.rotation, filterRatio);

            return new Pose(filteredPosition, filteredRotation);
        }

        public void InjectAllFromOVRHandDataSource(UpdateModeFlags updateMode, IDataSource updateAfter,
            Oculus.Interaction.Input.Handedness handedness,
            ITrackingToWorldTransformer trackingToWorldTransformer,
            IHandSkeletonProvider handSkeletonProvider)
        {
            InjectAllDataSource(updateMode, updateAfter);
            InjectHandedness(handedness);
            InjectTrackingToWorldTransformer(trackingToWorldTransformer);
            InjectHandSkeletonProvider(handSkeletonProvider);
        }

        public void InjectHandedness(Oculus.Interaction.Input.Handedness handedness)
        {
            _handedness = handedness;
        }

        public void InjectTrackingToWorldTransformer(ITrackingToWorldTransformer trackingToWorldTransformer)
        {
            _trackingToWorldTransformer = trackingToWorldTransformer as UnityEngine.Object;
            TrackingToWorldTransformer = trackingToWorldTransformer;
        }

        public void InjectHandSkeletonProvider(IHandSkeletonProvider handSkeletonProvider)
        {
            _handSkeletonProvider = handSkeletonProvider as UnityEngine.Object;
            HandSkeletonProvider = handSkeletonProvider;
        }

        public void InjectOptionalOVRHand(OVRHand ovrHand)
        {
            _ovrHand = ovrHand;
        }
    }
}
