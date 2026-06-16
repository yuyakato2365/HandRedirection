using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public sealed class HandLocalScaniverseOcclusion : MonoBehaviour
{
    [Header("Scaniverse Sources")]
    public Transform[] scaniverseRoots;
    public MeshRenderer[] scaniverseRenderers;
    public MeshRenderer[] excludedRenderers;
    public bool useRootsAlongsideExplicitRenderers = false;
    public bool autoFindScaniverseRoots = true;
    public string[] autoFindNameContains = { "Scaniverse" };
    public string[] includeRendererNameContains = new string[0];
    public string[] excludeRendererNameContains = new string[0];
    public bool onlyApplyOverlayToNamedRootChildren = true;
    public string[] scaniverseMeshRootNameEquals = { "Root", "root" };
    public bool respectInactiveScaniverseRoots = true;

    [Header("Scaniverse Child Activation")]
    public bool enableNonSelectedChildrenUnderScaniverseRoots = true;
    public string[] doNotAutoEnableNameEquals = { "Root", "root" };

    [Header("Hand Tracking")]
    public Transform leftHand;
    public Transform rightHand;
    public PinchProvider leftPinchProvider;
    public PinchProvider rightPinchProvider;
    public bool autoFindHands = true;
    public bool preferGoGoOriginalHandSources = true;
    public Transform leftIndexTip;
    public Transform rightIndexTip;
    [Range(0f, 1f)] public float handMaskIndexTipBlend = 0.65f;
    public GoGoInteractionController_NoY3[] redirectionControllers;

    [Header("Forearm Box Mask")]
    public bool enableForearmBoxMask = true;
    public bool autoFindAvatarHandDriver = true;
    public AvatarHandTrackingDriver avatarHandDriver;
    public Transform leftElbowOverride;
    public Transform rightElbowOverride;
    [Tooltip("Use the avatar driver's captured hand-local forearm direction. Off by default because the avatar hand space can differ from the original hand anchor space.")]
    public bool useAvatarCapturedForearmDirection = false;
    [Tooltip("Prefer wrist-to-index-tip opposite direction for the forearm mask when an index tip is available.")]
    public bool useIndexTipDirectionForForearmMask = true;
    public Vector3 leftForearmMaskWristLocalDirection = Vector3.down;
    public Vector3 rightForearmMaskWristLocalDirection = Vector3.down;
    public float defaultForearmLengthMeters = 0.25f;
    public Vector3 defaultWristToElbowLocalDirection = Vector3.back;
    public float forearmBoxHalfWidthMeters = 0.3f;
    public float forearmBoxHalfHeightMeters = 0.3f;
    public float forearmBoxFeatherMeters = 0.07f;
    public float forearmBoxDepthBiasMeters = 0.02f;

    [Header("Placement Safety")]
    public bool hideOverlayDuringAnchorPlacement = false;
    public ManualSpatialAnchorPlacer anchorPlacer;
    public SpatialAnchorToDeskOriginBinder deskBinder;

    [Header("Original Object Mask Sources")]
    public Transform[] objectOriginalMaskPoints;
    public bool autoFindObjectOriginalMaskPoints = true;
    public int maxObjectMaskPoints = 16;
    [Tooltip("Local offset applied to each original tracker/object pose before building its view-space cover. Negative Z means the transform's back side.")]
    public Vector3 objectMaskLocalOffset = new Vector3(0f, 0f, -0.06f);
    public float objectMaskRadiusMeters = 0.54f;
    public float objectMaskFeatherMeters = 0.21f;
    public float objectMaskDepthBiasMeters = 0.02f;

    [Header("Mask")]
    public float radiusMeters = 0.18f;
    public float featherMeters = 0.07f;
    [Tooltip("Only draw Scaniverse fragments this far behind the hand from the current viewpoint.")]
    public float handDepthBiasMeters = 0.02f;
    [Tooltip("Higher values make the edge fade more gradually into passthrough.")]
    public float maskSoftness = 1.35f;
    [Range(0f, 1f)] public float opacity = 1f;
    public bool hideOriginalScaniverseRenderers = true;

    [Header("Render Order")]
    public int overlayRenderQueue = 3000;
    public string overlayLayerName = "";

    [Header("Generated Cache Cleanup")]
    public bool rebuildOverlayOnEnable = true;
    public bool destroyGeneratedOverlayOnDisable = true;
    public bool destroyStaleGeneratedOverlayObjects = true;
    public bool pruneOldHandOverlayLogs = true;
    public int logRetentionDays = 7;
    public float emptyOverlayRebuildRetryIntervalSec = 2f;
    public string[] logCleanupFilePatterns =
    {
        "HandLocalScaniverseOverlay*.log",
        "SpatialAnchorHandAlignment*.log"
    };

    private const string GeneratedRootName = "GeneratedHandLocalScaniverseOverlay";
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int LeftHandPosId = Shader.PropertyToID("_LeftHandPos");
    private static readonly int RightHandPosId = Shader.PropertyToID("_RightHandPos");
    private static readonly int LeftForearmStartId = Shader.PropertyToID("_LeftForearmStart");
    private static readonly int LeftForearmEndId = Shader.PropertyToID("_LeftForearmEnd");
    private static readonly int LeftForearmRightId = Shader.PropertyToID("_LeftForearmRight");
    private static readonly int LeftForearmUpId = Shader.PropertyToID("_LeftForearmUp");
    private static readonly int RightForearmStartId = Shader.PropertyToID("_RightForearmStart");
    private static readonly int RightForearmEndId = Shader.PropertyToID("_RightForearmEnd");
    private static readonly int RightForearmRightId = Shader.PropertyToID("_RightForearmRight");
    private static readonly int RightForearmUpId = Shader.PropertyToID("_RightForearmUp");
    private static readonly int ForearmBoxHalfSizeId = Shader.PropertyToID("_ForearmBoxHalfSize");
    private static readonly int ForearmBoxFeatherId = Shader.PropertyToID("_ForearmBoxFeather");
    private static readonly int ForearmBoxDepthBiasId = Shader.PropertyToID("_ForearmBoxDepthBias");
    private static readonly int RadiusId = Shader.PropertyToID("_Radius");
    private static readonly int FeatherId = Shader.PropertyToID("_Feather");
    private static readonly int DepthBiasId = Shader.PropertyToID("_DepthBias");
    private static readonly int ObjectMaskPositionsId = Shader.PropertyToID("_ObjectMaskPositions");
    private static readonly int ObjectMaskCountId = Shader.PropertyToID("_ObjectMaskCount");
    private static readonly int ObjectMaskRadiusId = Shader.PropertyToID("_ObjectMaskRadius");
    private static readonly int ObjectMaskFeatherId = Shader.PropertyToID("_ObjectMaskFeather");
    private static readonly int ObjectMaskDepthBiasId = Shader.PropertyToID("_ObjectMaskDepthBias");
    private static readonly int MaskSoftnessId = Shader.PropertyToID("_MaskSoftness");
    private static readonly int OpacityId = Shader.PropertyToID("_Opacity");

    private readonly List<GameObject> generatedObjects = new List<GameObject>();
    private readonly List<Material> generatedMaterials = new List<Material>();
    private readonly List<Renderer> sourceRenderers = new List<Renderer>();
    private readonly List<Vector4> objectMaskPositions = new List<Vector4>();
    private readonly Vector4[] objectMaskPositionArray = new Vector4[16];
    private MaterialPropertyBlock propertyBlock;
    private Shader maskShader;
    private int overlayLayer = -1;
    private bool generatedOverlayVisible = true;
    private float nextEmptyOverlayRebuildTime;

    private void OnEnable()
    {
        propertyBlock = propertyBlock ?? new MaterialPropertyBlock();
        maskShader = Shader.Find("HandRedirection/Hand Local Scaniverse Mask");
        overlayLayer = ResolveOverlayLayer();

        if (pruneOldHandOverlayLogs)
            PruneOldLogs();

        if (rebuildOverlayOnEnable)
            RebuildOverlay();
    }

    private void OnDisable()
    {
        RestoreSourceRenderers();

        if (destroyGeneratedOverlayOnDisable)
            DestroyGeneratedOverlay();
    }

    private void OnDestroy()
    {
        RestoreSourceRenderers();
        DestroyGeneratedOverlay();
    }

    private void LateUpdate()
    {
        if (generatedObjects.Count == 0 && Time.realtimeSinceStartup >= nextEmptyOverlayRebuildTime)
            RebuildOverlay();

        AutoAssignReferences();

        bool rotationAdjustmentActive = deskBinder != null && deskBinder.IsAdjustingAlignment;
        if (hideOverlayDuringAnchorPlacement && !rotationAdjustmentActive && anchorPlacer != null && (anchorPlacer.IsPlacementMode || anchorPlacer.IsCreatingAnchor))
        {
            SetGeneratedOverlayVisible(false);
            return;
        }

        SetGeneratedOverlayVisible(true);

        Vector3 left = ResolveHandPosition(leftHand, leftIndexTip, leftPinchProvider);
        Vector3 right = ResolveHandPosition(rightHand, rightIndexTip, rightPinchProvider);
        ResolveForearmBoxMask(true, leftHand, leftIndexTip, leftElbowOverride, out Vector4 leftForearmStart, out Vector4 leftForearmEnd, out Vector4 leftForearmRight, out Vector4 leftForearmUp);
        ResolveForearmBoxMask(false, rightHand, rightIndexTip, rightElbowOverride, out Vector4 rightForearmStart, out Vector4 rightForearmEnd, out Vector4 rightForearmRight, out Vector4 rightForearmUp);
        int objectMaskCount = CollectObjectMaskPositions();

        for (int i = 0; i < generatedObjects.Count; i++)
        {
            GameObject obj = generatedObjects[i];
            if (obj == null)
                continue;

            Renderer renderer = obj.GetComponent<Renderer>();
            if (renderer == null)
                continue;

            renderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetVector(LeftHandPosId, left);
            propertyBlock.SetVector(RightHandPosId, right);
            propertyBlock.SetVector(LeftForearmStartId, leftForearmStart);
            propertyBlock.SetVector(LeftForearmEndId, leftForearmEnd);
            propertyBlock.SetVector(LeftForearmRightId, leftForearmRight);
            propertyBlock.SetVector(LeftForearmUpId, leftForearmUp);
            propertyBlock.SetVector(RightForearmStartId, rightForearmStart);
            propertyBlock.SetVector(RightForearmEndId, rightForearmEnd);
            propertyBlock.SetVector(RightForearmRightId, rightForearmRight);
            propertyBlock.SetVector(RightForearmUpId, rightForearmUp);
            propertyBlock.SetVector(ForearmBoxHalfSizeId, new Vector4(
                Mathf.Max(0.001f, forearmBoxHalfWidthMeters),
                Mathf.Max(0.001f, forearmBoxHalfHeightMeters),
                0f,
                0f));
            propertyBlock.SetFloat(ForearmBoxFeatherId, Mathf.Max(0.0001f, forearmBoxFeatherMeters));
            propertyBlock.SetFloat(ForearmBoxDepthBiasId, Mathf.Max(0f, forearmBoxDepthBiasMeters));
            propertyBlock.SetFloat(RadiusId, Mathf.Max(0.001f, radiusMeters));
            propertyBlock.SetFloat(FeatherId, Mathf.Max(0.0001f, featherMeters));
            propertyBlock.SetFloat(DepthBiasId, Mathf.Max(0f, handDepthBiasMeters));
            propertyBlock.SetVectorArray(ObjectMaskPositionsId, objectMaskPositionArray);
            propertyBlock.SetInt(ObjectMaskCountId, objectMaskCount);
            propertyBlock.SetFloat(ObjectMaskRadiusId, Mathf.Max(0.001f, objectMaskRadiusMeters));
            propertyBlock.SetFloat(ObjectMaskFeatherId, Mathf.Max(0.0001f, objectMaskFeatherMeters));
            propertyBlock.SetFloat(ObjectMaskDepthBiasId, Mathf.Max(0f, objectMaskDepthBiasMeters));
            propertyBlock.SetFloat(MaskSoftnessId, Mathf.Max(0.001f, maskSoftness));
            propertyBlock.SetFloat(OpacityId, opacity);
            renderer.SetPropertyBlock(propertyBlock);
        }
    }

    [ContextMenu("Hand Local Scaniverse/Rebuild Overlay")]
    public void RebuildOverlay()
    {
        nextEmptyOverlayRebuildTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, emptyOverlayRebuildRetryIntervalSec);
        DestroyGeneratedOverlay();
        sourceRenderers.Clear();

        if (maskShader == null)
        {
            Debug.LogWarning("[HandLocalScaniverseOcclusion] Mask shader was not found.");
            return;
        }

        AutoAssignReferences();
        CleanupStaleGeneratedOverlayObjects();

        HashSet<MeshRenderer> selectedRenderers = CollectSelectedRenderers();
        EnableNonSelectedChildrenUnderRoots(selectedRenderers);
        foreach (MeshRenderer renderer in selectedRenderers)
            CreateOverlayRenderer(renderer);
    }

    [ContextMenu("Hand Local Scaniverse/Log Selected Renderers")]
    public void LogSelectedRenderers()
    {
        AutoAssignReferences();

        HashSet<MeshRenderer> selectedRenderers = CollectSelectedRenderers();
        foreach (MeshRenderer renderer in selectedRenderers)
        {
            MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            string meshName = filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "(no mesh)";
            Debug.Log($"[HandLocalScaniverseOcclusion] selected renderer='{GetHierarchyPath(renderer.transform)}', mesh='{meshName}'", renderer);
        }

        Debug.Log($"[HandLocalScaniverseOcclusion] selected renderer count={selectedRenderers.Count}");
    }

    private HashSet<MeshRenderer> CollectSelectedRenderers()
    {
        HashSet<MeshRenderer> selectedRenderers = new HashSet<MeshRenderer>();

        MeshRenderer[] explicitRenderers = scaniverseRenderers ?? Array.Empty<MeshRenderer>();
        for (int i = 0; i < explicitRenderers.Length; i++)
        {
            MeshRenderer renderer = explicitRenderers[i];
            if (renderer == null || IsExcludedRenderer(renderer))
                continue;

            selectedRenderers.Add(renderer);
        }

        bool hasExplicitRenderers = scaniverseRenderers != null && scaniverseRenderers.Length > 0;
        if (hasExplicitRenderers && !useRootsAlongsideExplicitRenderers)
            return selectedRenderers;

        Transform[] roots = scaniverseRoots ?? Array.Empty<Transform>();
        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            Transform root = roots[rootIndex];
            if (root == null)
                continue;
            if (respectInactiveScaniverseRoots && !root.gameObject.activeInHierarchy)
                continue;

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null || !ShouldUseRendererFromRoot(renderer, root))
                    continue;

                selectedRenderers.Add(renderer);
            }
        }

        return selectedRenderers;
    }

    private void CreateOverlayRenderer(MeshRenderer sourceRenderer)
    {
        if (sourceRenderer == null)
            return;
        if (sourceRenderer.gameObject.name.StartsWith(GeneratedRootName, StringComparison.Ordinal))
            return;

        MeshFilter sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
        if (sourceFilter == null || sourceFilter.sharedMesh == null)
            return;

        GameObject overlay = new GameObject($"{GeneratedRootName}_{sourceRenderer.name}");
        overlay.transform.SetParent(sourceRenderer.transform, false);
        overlay.transform.localPosition = Vector3.zero;
        overlay.transform.localRotation = Quaternion.identity;
        overlay.transform.localScale = Vector3.one;
        if (overlayLayer >= 0)
            overlay.layer = overlayLayer;

        MeshFilter overlayFilter = overlay.AddComponent<MeshFilter>();
        overlayFilter.sharedMesh = sourceFilter.sharedMesh;

        MeshRenderer overlayRenderer = overlay.AddComponent<MeshRenderer>();
        overlayRenderer.sharedMaterials = BuildOverlayMaterials(sourceRenderer.sharedMaterials);
        overlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        overlayRenderer.receiveShadows = false;
        overlayRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        overlayRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        overlayRenderer.allowOcclusionWhenDynamic = false;

        generatedObjects.Add(overlay);
        sourceRenderers.Add(sourceRenderer);
        if (hideOriginalScaniverseRenderers)
            sourceRenderer.enabled = false;
    }

    private void EnableNonSelectedChildrenUnderRoots(HashSet<MeshRenderer> selectedRenderers)
    {
        if (!enableNonSelectedChildrenUnderScaniverseRoots)
            return;

        Transform[] roots = scaniverseRoots ?? Array.Empty<Transform>();
        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            Transform scaniverseRoot = roots[rootIndex];
            if (scaniverseRoot == null)
                continue;
            if (respectInactiveScaniverseRoots && !scaniverseRoot.gameObject.activeInHierarchy)
                continue;

            Transform[] children = scaniverseRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || child == scaniverseRoot)
                    continue;
                if (child.gameObject.name.StartsWith(GeneratedRootName, StringComparison.Ordinal))
                    continue;
                if (NameEqualsAny(child.name, doNotAutoEnableNameEquals))
                    continue;

                child.gameObject.SetActive(true);
            }

            MeshRenderer[] renderers = scaniverseRoot.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null || selectedRenderers.Contains(renderer))
                    continue;
                if (renderer.gameObject.name.StartsWith(GeneratedRootName, StringComparison.Ordinal))
                    continue;

                renderer.enabled = true;
            }
        }
    }

    private Material[] BuildOverlayMaterials(Material[] sourceMaterials)
    {
        if (sourceMaterials == null || sourceMaterials.Length == 0)
            sourceMaterials = new Material[] { null };

        Material[] materials = new Material[sourceMaterials.Length];
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            Material source = sourceMaterials[i];
            Material material = new Material(maskShader)
            {
                name = source != null ? $"{source.name}_HandLocalOverlay" : "HandLocalScaniverseOverlay"
            };

            if (source != null)
            {
                CopyTexture(source, material, "_BaseMap", "_BaseMap");
                CopyTexture(source, material, "_MainTex", "_BaseMap");
                CopyColor(source, material, "_BaseColor", "_BaseColor");
                CopyColor(source, material, "_Color", "_BaseColor");
            }

            material.renderQueue = overlayRenderQueue;
            material.SetVector(LeftForearmStartId, InvalidMaskVector());
            material.SetVector(LeftForearmEndId, InvalidMaskVector());
            material.SetVector(LeftForearmRightId, Vector4.zero);
            material.SetVector(LeftForearmUpId, Vector4.zero);
            material.SetVector(RightForearmStartId, InvalidMaskVector());
            material.SetVector(RightForearmEndId, InvalidMaskVector());
            material.SetVector(RightForearmRightId, Vector4.zero);
            material.SetVector(RightForearmUpId, Vector4.zero);
            material.SetVector(ForearmBoxHalfSizeId, new Vector4(forearmBoxHalfWidthMeters, forearmBoxHalfHeightMeters, 0f, 0f));
            material.SetFloat(ForearmBoxFeatherId, forearmBoxFeatherMeters);
            material.SetFloat(ForearmBoxDepthBiasId, forearmBoxDepthBiasMeters);
            material.SetFloat(RadiusId, radiusMeters);
            material.SetFloat(FeatherId, featherMeters);
            material.SetFloat(DepthBiasId, handDepthBiasMeters);
            material.SetInt(ObjectMaskCountId, 0);
            material.SetFloat(ObjectMaskRadiusId, objectMaskRadiusMeters);
            material.SetFloat(ObjectMaskFeatherId, objectMaskFeatherMeters);
            material.SetFloat(ObjectMaskDepthBiasId, objectMaskDepthBiasMeters);
            material.SetFloat(MaskSoftnessId, maskSoftness);
            material.SetFloat(OpacityId, opacity);
            materials[i] = material;
            generatedMaterials.Add(material);
        }

        return materials;
    }

    private static void CopyTexture(Material source, Material target, string sourceName, string targetName)
    {
        if (source == null || target == null || !source.HasProperty(sourceName) || !target.HasProperty(targetName))
            return;

        Texture texture = source.GetTexture(sourceName);
        if (texture != null)
            target.SetTexture(targetName, texture);
    }

    private static void CopyColor(Material source, Material target, string sourceName, string targetName)
    {
        if (source == null || target == null || !source.HasProperty(sourceName) || !target.HasProperty(targetName))
            return;

        target.SetColor(targetName, source.GetColor(sourceName));
    }

    private void AutoAssignReferences()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (autoFindAvatarHandDriver && avatarHandDriver == null)
            avatarHandDriver = FindAnyObjectByType<AvatarHandTrackingDriver>();

        if (autoFindScaniverseRoots && (scaniverseRoots == null || scaniverseRoots.Length == 0))
            scaniverseRoots = FindScaniverseRoots();

        AutoAssignRedirectionControllers();

        if (preferGoGoOriginalHandSources && redirectionControllers != null)
        {
            for (int i = 0; i < redirectionControllers.Length; i++)
            {
                GoGoInteractionController_NoY3 controller = redirectionControllers[i];
                if (controller == null)
                    continue;

                if (controller.leftHandOriginal != null)
                    leftHand = controller.leftHandOriginal;
                if (controller.rightHandOriginal != null)
                    rightHand = controller.rightHandOriginal;
                if (controller.leftIndexTipPoint != null)
                    leftIndexTip = controller.leftIndexTipPoint;
                if (controller.rightIndexTipPoint != null)
                    rightIndexTip = controller.rightIndexTipPoint;
                if (leftHand != null && rightHand != null)
                    break;
            }
        }

        if (!autoFindHands)
            return;

        if (leftPinchProvider == null || rightPinchProvider == null)
        {
            PinchProvider[] providers = FindObjectsByType<PinchProvider>(FindObjectsSortMode.None);
            for (int i = 0; i < providers.Length; i++)
            {
                PinchProvider provider = providers[i];
                if (provider == null)
                    continue;

                string name = provider.name.ToLowerInvariant();
                if (leftPinchProvider == null && name.Contains("left"))
                    leftPinchProvider = provider;
                else if (rightPinchProvider == null && name.Contains("right"))
                    rightPinchProvider = provider;
            }
        }

        if (leftHand == null && leftPinchProvider != null)
            leftHand = leftPinchProvider.transform;
        if (rightHand == null && rightPinchProvider != null)
            rightHand = rightPinchProvider.transform;
    }

    private void ResolveForearmBoxMask(
        bool left,
        Transform wrist,
        Transform indexTip,
        Transform elbowOverride,
        out Vector4 start,
        out Vector4 end,
        out Vector4 right,
        out Vector4 up)
    {
        start = InvalidMaskVector();
        end = InvalidMaskVector();
        right = Vector4.zero;
        up = Vector4.zero;

        if (!enableForearmBoxMask || wrist == null)
            return;

        Vector3 wristPosition = wrist.position;
        Vector3 elbowPosition;
        if (elbowOverride != null)
        {
            elbowPosition = elbowOverride.position;
        }
        else
        {
            float length = Mathf.Max(0.001f, defaultForearmLengthMeters);
            Vector3 worldDirection = Vector3.zero;

            AvatarHandTrackingDriver.HandRig rig = GetAvatarHandRig(left);
            if (useAvatarCapturedForearmDirection && rig != null)
            {
                Vector3 localDirection = rig.wristToElbowLocalDirection.sqrMagnitude > 1e-8f
                    ? rig.wristToElbowLocalDirection
                    : defaultWristToElbowLocalDirection;
                worldDirection = wrist.rotation * localDirection.normalized;
                if (rig.wristToElbowLength > 0.001f)
                    length = rig.wristToElbowLength;
            }

            if (worldDirection.sqrMagnitude < 1e-8f && useIndexTipDirectionForForearmMask && indexTip != null)
                worldDirection = wristPosition - indexTip.position;

            if (worldDirection.sqrMagnitude < 1e-8f)
            {
                Vector3 localDirection = left ? leftForearmMaskWristLocalDirection : rightForearmMaskWristLocalDirection;
                if (localDirection.sqrMagnitude < 1e-8f)
                    localDirection = defaultWristToElbowLocalDirection.sqrMagnitude > 1e-8f
                        ? defaultWristToElbowLocalDirection
                        : Vector3.down;
                worldDirection = wrist.rotation * localDirection.normalized;
            }

            elbowPosition = wristPosition + worldDirection.normalized * length;
        }

        Vector3 segment = elbowPosition - wristPosition;
        if (segment.sqrMagnitude < 1e-8f)
            return;

        Vector3 segmentDirection = segment.normalized;
        Vector3 rightAxis = Vector3.ProjectOnPlane(wrist.right, segmentDirection);
        if (rightAxis.sqrMagnitude < 1e-8f)
            rightAxis = Vector3.ProjectOnPlane(wrist.up, segmentDirection);
        if (rightAxis.sqrMagnitude < 1e-8f)
            rightAxis = Vector3.Cross(segmentDirection, Vector3.up);
        if (rightAxis.sqrMagnitude < 1e-8f)
            rightAxis = Vector3.Cross(segmentDirection, Vector3.forward);

        rightAxis.Normalize();
        Vector3 upAxis = Vector3.Cross(segmentDirection, rightAxis);
        if (upAxis.sqrMagnitude < 1e-8f)
            return;
        upAxis.Normalize();

        start = new Vector4(wristPosition.x, wristPosition.y, wristPosition.z, 1f);
        end = new Vector4(elbowPosition.x, elbowPosition.y, elbowPosition.z, 1f);
        right = new Vector4(rightAxis.x, rightAxis.y, rightAxis.z, 0f);
        up = new Vector4(upAxis.x, upAxis.y, upAxis.z, 0f);
    }

    private AvatarHandTrackingDriver.HandRig GetAvatarHandRig(bool left)
    {
        if (avatarHandDriver == null)
            return null;

        return left ? avatarHandDriver.leftHand : avatarHandDriver.rightHand;
    }

    private static Vector4 InvalidMaskVector()
    {
        return new Vector4(9999f, 9999f, 9999f, 0f);
    }

    private void AutoAssignRedirectionControllers()
    {
        if (redirectionControllers != null && redirectionControllers.Length > 0)
            return;

        redirectionControllers = FindObjectsByType<GoGoInteractionController_NoY3>(FindObjectsSortMode.None);
    }

    private Transform[] FindScaniverseRoots()
    {
        List<Transform> roots = new List<Transform>();
        MeshRenderer[] renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        for (int i = 0; i < renderers.Length; i++)
        {
            MeshRenderer renderer = renderers[i];
            if (renderer == null)
                continue;

            Transform root = FindMatchingRoot(renderer.transform);
            if (root != null && !roots.Contains(root))
                roots.Add(root);
        }

        return roots.ToArray();
    }

    private Transform FindMatchingRoot(Transform start)
    {
        Transform current = start;
        Transform best = null;
        while (current != null)
        {
            if (NameMatches(current.name))
                best = current;
            current = current.parent;
        }

        if (respectInactiveScaniverseRoots && best != null && !best.gameObject.activeInHierarchy)
            return null;

        return best;
    }

    private bool NameMatches(string objectName)
    {
        if (autoFindNameContains == null || autoFindNameContains.Length == 0)
            return false;

        string lower = objectName.ToLowerInvariant();
        for (int i = 0; i < autoFindNameContains.Length; i++)
        {
            string token = autoFindNameContains[i];
            if (!string.IsNullOrWhiteSpace(token) && lower.Contains(token.ToLowerInvariant()))
                return true;
        }

        return false;
    }

    private bool ShouldUseRendererFromRoot(MeshRenderer renderer, Transform scaniverseRoot)
    {
        if (renderer == null || IsExcludedRenderer(renderer))
            return false;

        if (onlyApplyOverlayToNamedRootChildren && !IsUnderNamedRootChild(renderer.transform, scaniverseRoot))
            return false;

        if (includeRendererNameContains != null && includeRendererNameContains.Length > 0)
            return NameMatchesAny(renderer.name, includeRendererNameContains) || NameMatchesAny(renderer.gameObject.name, includeRendererNameContains);

        return true;
    }

    private bool IsUnderNamedRootChild(Transform transform, Transform scaniverseRoot)
    {
        if (transform == null)
            return false;

        Transform current = transform;
        while (current != null && current != scaniverseRoot)
        {
            if (NameEqualsAny(current.name, scaniverseMeshRootNameEquals))
                return true;

            current = current.parent;
        }

        return false;
    }

    private bool IsExcludedRenderer(MeshRenderer renderer)
    {
        if (renderer == null)
            return true;

        if (renderer.gameObject.name.StartsWith(GeneratedRootName, StringComparison.Ordinal))
            return true;

        MeshRenderer[] excluded = excludedRenderers ?? Array.Empty<MeshRenderer>();
        for (int i = 0; i < excluded.Length; i++)
        {
            if (excluded[i] == renderer)
                return true;
        }

        return NameMatchesAny(renderer.name, excludeRendererNameContains) || NameMatchesAny(renderer.gameObject.name, excludeRendererNameContains);
    }

    private static bool NameMatchesAny(string objectName, string[] tokens)
    {
        if (string.IsNullOrEmpty(objectName) || tokens == null || tokens.Length == 0)
            return false;

        string lower = objectName.ToLowerInvariant();
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (!string.IsNullOrWhiteSpace(token) && lower.Contains(token.ToLowerInvariant()))
                return true;
        }

        return false;
    }

    private static bool NameEqualsAny(string objectName, string[] names)
    {
        if (string.IsNullOrEmpty(objectName) || names == null || names.Length == 0)
            return false;

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (!string.IsNullOrWhiteSpace(name) && string.Equals(objectName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string GetHierarchyPath(Transform transform)
    {
        if (transform == null)
            return "(null)";

        string path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = $"{current.name}/{path}";
            current = current.parent;
        }

        return path;
    }

    private int CollectObjectMaskPositions()
    {
        objectMaskPositions.Clear();
        int limit = Mathf.Clamp(maxObjectMaskPoints, 0, objectMaskPositionArray.Length);

        AddObjectMaskTransforms(objectOriginalMaskPoints, limit);

        if (autoFindObjectOriginalMaskPoints)
        {
            AutoAssignRedirectionControllers();
            if (redirectionControllers != null)
            {
                for (int i = 0; i < redirectionControllers.Length; i++)
                {
                    GoGoInteractionController_NoY3 controller = redirectionControllers[i];
                    if (controller == null)
                        continue;

                    AddObjectMaskTransform(controller.cubeRealWorldSource != null ? controller.cubeRealWorldSource : controller.cubeReal, limit);

                    List<GoGoInteractionController_NoY3.WarpObjectEntry> entries = controller.objects;
                    if (entries == null)
                        continue;

                    for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                    {
                        GoGoInteractionController_NoY3.WarpObjectEntry entry = entries[entryIndex];
                        if (entry == null || !entry.enabled)
                            continue;

                        AddObjectMaskTransform(entry.realWorldSource != null ? entry.realWorldSource : entry.realObject, limit);
                    }
                }
            }
        }

        int count = Mathf.Min(objectMaskPositions.Count, limit);
        for (int i = 0; i < objectMaskPositionArray.Length; i++)
            objectMaskPositionArray[i] = i < count ? objectMaskPositions[i] : new Vector4(9999f, 9999f, 9999f, 0f);

        return count;
    }

    private void AddObjectMaskTransforms(Transform[] transforms, int limit)
    {
        if (transforms == null)
            return;

        for (int i = 0; i < transforms.Length; i++)
            AddObjectMaskTransform(transforms[i], limit);
    }

    private void AddObjectMaskTransform(Transform transform, int limit)
    {
        if (transform == null || objectMaskPositions.Count >= limit)
            return;

        Vector3 position = transform.TransformPoint(objectMaskLocalOffset);
        for (int i = 0; i < objectMaskPositions.Count; i++)
        {
            Vector4 existingMask = objectMaskPositions[i];
            Vector3 existing = new Vector3(existingMask.x, existingMask.y, existingMask.z);
            if ((existing - position).sqrMagnitude < 0.000001f)
                return;
        }

        objectMaskPositions.Add(new Vector4(position.x, position.y, position.z, 1f));
    }

    private Vector3 ResolveHandPosition(Transform fallback, Transform indexTip, PinchProvider provider)
    {
        if (fallback != null && indexTip != null)
            return Vector3.Lerp(fallback.position, indexTip.position, Mathf.Clamp01(handMaskIndexTipBlend));
        if (fallback != null)
            return fallback.position;
        if (indexTip != null)
            return indexTip.position;
        if (provider != null)
            return provider.PinchPosWorld;

        return new Vector3(9999f, 9999f, 9999f);
    }

    private int ResolveOverlayLayer()
    {
        if (string.IsNullOrWhiteSpace(overlayLayerName))
            return -1;

        int layer = LayerMask.NameToLayer(overlayLayerName);
        if (layer < 0)
            Debug.LogWarning($"[HandLocalScaniverseOcclusion] Layer '{overlayLayerName}' was not found.");
        return layer;
    }

    private void RestoreSourceRenderers()
    {
        for (int i = 0; i < sourceRenderers.Count; i++)
        {
            if (sourceRenderers[i] != null)
                sourceRenderers[i].enabled = true;
        }
    }

    private void DestroyGeneratedOverlay()
    {
        for (int i = 0; i < generatedObjects.Count; i++)
        {
            GameObject obj = generatedObjects[i];
            if (obj == null)
                continue;

            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }
        generatedObjects.Clear();

        for (int i = 0; i < generatedMaterials.Count; i++)
        {
            Material material = generatedMaterials[i];
            if (material == null)
                continue;

            if (Application.isPlaying)
                Destroy(material);
            else
                DestroyImmediate(material);
        }
        generatedMaterials.Clear();
    }

    private void CleanupStaleGeneratedOverlayObjects()
    {
        if (!destroyStaleGeneratedOverlayObjects)
            return;

        Transform[] roots = scaniverseRoots ?? Array.Empty<Transform>();
        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            Transform root = roots[rootIndex];
            if (root == null)
                continue;

            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = children.Length - 1; i >= 0; i--)
            {
                Transform child = children[i];
                if (child == null || child == root)
                    continue;
                if (!child.gameObject.name.StartsWith(GeneratedRootName, StringComparison.Ordinal))
                    continue;

                if (Application.isPlaying)
                    Destroy(child.gameObject);
                else
                    DestroyImmediate(child.gameObject);
            }
        }
    }

    private void SetGeneratedOverlayVisible(bool visible)
    {
        if (generatedOverlayVisible == visible)
            return;

        generatedOverlayVisible = visible;
        for (int i = 0; i < generatedObjects.Count; i++)
        {
            GameObject obj = generatedObjects[i];
            if (obj != null)
                obj.SetActive(visible);
        }
    }

    private void PruneOldLogs()
    {
        if (logRetentionDays <= 0)
            return;

        try
        {
            string logDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs"));
            if (!Directory.Exists(logDirectory))
                return;

            DateTime cutoff = DateTime.Now.AddDays(-logRetentionDays);
            string[] patterns = logCleanupFilePatterns == null || logCleanupFilePatterns.Length == 0
                ? new[] { "HandLocalScaniverseOverlay*.log" }
                : logCleanupFilePatterns;

            for (int patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
            {
                string pattern = patterns[patternIndex];
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                string[] files = Directory.GetFiles(logDirectory, pattern);
                for (int i = 0; i < files.Length; i++)
                {
                    FileInfo info = new FileInfo(files[i]);
                    if (info.LastWriteTime < cutoff)
                        info.Delete();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HandLocalScaniverseOcclusion] Log cleanup failed: {e.Message}");
        }
    }
}
