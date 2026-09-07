using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class ScalePlacementChallengeController : MonoBehaviour
{
    [Serializable]
    public class Pattern
    {
        public string id = "A";
        public string targetObjectId = "1";
        [Tooltip("Offset in meters from RedirectionOrigin: X is right and Y is forward on the tabletop.")]
        public Vector2 deskLocalPosition = new Vector2(0f, 0.82f);
        [Tooltip("Required object scale ratio for local X/Y/Z.")]
        public Vector3 targetScaleRatio = new Vector3(1.35f, 1.35f, 1.35f);
        [Tooltip("Allowed relative size error for X/Y/Z. 0.10 means +/-10 percent of the target on that axis.")]
        public Vector3 scaleToleranceRatio = new Vector3(0.10f, 0.10f, 0.10f);
        [Tooltip("Allowed center-position error in meters. X/Z are used; Y is retained only for saved-settings compatibility and is never used for completion.")]
        public Vector3 positionToleranceMeters = new Vector3(0.09f, 0.10f, 0.09f);
        [Tooltip("Optional extra visual clearance outside the exact target footprint. Keep zero when the ring itself should represent the clear size.")]
        [Min(0f)] public float ringPaddingMeters = 0f;
    }

    [Serializable]
    public class RingCharacter
    {
        [Tooltip("PANDA, GORILLA, or ELEPHANT. The order is always smallest, middle, largest Ring.")]
        public string id = "PANDA";
        [Tooltip("Optional explicit model root. When empty, a matching child is found below Characters.")]
        public Transform character;
        [Tooltip("Character horizontal diameter divided by its assigned Ring diameter.")]
        [Min(0.01f)] public float ringSizeMultiplier = 1f;
        [Tooltip("Optional correction when the imported model's local +Z is not its visual front.")]
        public float forwardYawOffsetDegrees;
    }

    [Serializable]
    private struct SavedPatternSettings
    {
        public int version;
        public string targetObjectId;
        public Vector2 deskLocalPosition;
        public Vector3 targetScaleRatio;
        public Vector3 scaleToleranceRatio;
        public Vector3 positionToleranceMeters;
    }

    private struct PatternFootprint
    {
        public Pattern pattern;
        public Vector2 radii;
    }

    [Header("References")]
    public Transform deskOrigin;
    [Tooltip("Pattern positions and front/left/right axes are based on this transform, never on the HMD.")]
    public Transform redirectionOrigin;
    public Transform tabletopReference;
    public GoGoInteractionController_NoY3 redirectionController;
    public DeskScaleSliderPanel deskScaleSlider;
    public ExhibitionExperienceResetter experienceResetter;

    [Header("Patterns")]
    public Pattern[] patterns =
    {
        // In the calibrated scene RedirectionOrigin.forward is the direction
        // where A is physically seen on the right. Keep that known-good A
        // position, mirror C to the left, and put B on the perpendicular front.
        new Pattern { id = "A", targetObjectId = "1", deskLocalPosition = new Vector2(0f, 0.82f), targetScaleRatio = new Vector3(1.35f, 1.35f, 1.35f) },
        new Pattern { id = "B", targetObjectId = "3", deskLocalPosition = new Vector2(-0.72f, 0f), targetScaleRatio = new Vector3(1.70f, 1.70f, 1.70f) },
        new Pattern { id = "C", targetObjectId = "4", deskLocalPosition = new Vector2(0f, -0.82f), targetScaleRatio = new Vector3(2.10f, 2.10f, 2.10f) }
    };
    public string activePatternId = "A";
    public bool persistActivePattern = true;
    public string activePatternPrefsKey = "HandRedirection.TargetRingPattern";
    public bool persistPatternSettings = true;
    public string patternSettingsPrefsKeyPrefix = "HandRedirection.TargetRingSettings.";
    [Tooltip("Start with the ring task disabled. Selecting A/B/C explicitly enables it.")]
    public bool enableChallengeOnStart = false;

    [Header("Ring Characters")]
    [Tooltip("Parent containing Panda, Gorilla, and Elephant. Automatically finds a GameObject named Characters when empty.")]
    public Transform charactersRoot;
    public RingCharacter[] ringCharacters =
    {
        new RingCharacter { id = "PANDA", ringSizeMultiplier = 1f },
        new RingCharacter { id = "GORILLA", ringSizeMultiplier = 1f },
        new RingCharacter { id = "ELEPHANT", ringSizeMultiplier = 1f }
    };
    [Tooltip("Empty space between the outer edge of a Ring and the corresponding character behind it.")]
    [Min(0f)] public float characterBehindRingGapMeters = 0.08f;
    [Tooltip("Shared rotation correction around the tabletop-normal axis after all characters face RedirectionOrigin.")]
    public float globalCharacterYawOffsetDegrees;
    public bool showRingCharacters = true;
    public bool persistCharacterSettings = true;
    public string characterMultiplierPrefsKeyPrefix = "HandRedirection.RingCharacterMultiplier.";
    public string characterYawPrefsKey = "HandRedirection.RingCharacterYawOffset";

    [Header("Ring Visual")]
    [Range(24, 128)] public int ringSegments = 72;
    [Min(0.001f)] public float ringWidth = 0.012f;
    public float tabletopLiftMeters = 0.006f;
    public Color incorrectColor = new Color(1f, 0.08f, 0.08f, 0.95f);
    public Color correctColor = new Color(0.05f, 0.85f, 1f, 0.98f);
    public bool latchSuccessUntilPatternChange = true;

    public event Action<string> PatternCompleted;
    public string ActivePatternId => activePatternId;
    public bool IsComplete => successLatched;
    public bool IsChallengeEnabled => challengeEnabled;

    private LineRenderer ring;
    public Renderer RingRenderer => ring;
    private Material ringMaterial;
    private Pattern activePattern;
    private GoGoInteractionController_NoY3.WarpObjectEntry activeEntry;
    private float tabletopHeight;
    private bool tabletopFrameCaptured;
    private Vector2 ringRadii = new Vector2(0.12f, 0.12f);
    private Vector3 targetObjectHalfExtents = new Vector3(0.095f, 0.05f, 0.095f);
    private float targetObjectLongestHalfExtent = 0.095f;
    private bool activeTargetUsesUniformScale;
    private float targetObjectHalfHeight = 0.05f;
    private bool successLatched;
    private bool wasCorrect;
    private bool challengeEnabled;
    private bool diagnosticStateInitialized;
    private bool lastDiagnosticPositionCorrect;
    private bool lastDiagnosticSizeCorrect;
    private readonly Dictionary<GoGoInteractionController_NoY3.WarpObjectEntry, Vector3> baselineScaleByEntry
        = new Dictionary<GoGoInteractionController_NoY3.WarpObjectEntry, Vector3>();
    private bool charactersResolved;
    private bool characterPlacementDirty = true;
    private int nextCharacterResolveFrame;
    private bool characterAssignmentsDirty = true;
    private bool characterAssignmentsNeedRetry;
    private int nextCharacterAssignmentRetryFrame;
    private readonly List<PatternFootprint> characterPatternAssignments = new List<PatternFootprint>(3);
    private readonly HashSet<int> alwaysOnTopConfiguredRenderers = new HashSet<int>();

    private void Awake()
    {
        LoadAllPatternSettings();
        EnsureCharacterDefinitions();
        LoadCharacterSettings();
        if (persistActivePattern && !string.IsNullOrWhiteSpace(activePatternPrefsKey))
            activePatternId = PlayerPrefs.GetString(activePatternPrefsKey, activePatternId);
    }

    private void Start()
    {
        ResolveReferences();
        ResolveCharacters();
        CreateRing();
        challengeEnabled = false;
        ring.gameObject.SetActive(false);
        if (enableChallengeOnStart)
        {
            if (!ActivatePattern(activePatternId, false, false))
                ActivatePattern("A", false, false);
        }
        UpdateCharacterPlacements();
    }

    private void LateUpdate()
    {
        if (!challengeEnabled) return;
        bool retryMissingReferences = (!charactersResolved || charactersRoot == null || deskOrigin == null) &&
                                      Time.frameCount >= nextCharacterResolveFrame;
        bool retryRingGeometry = characterAssignmentsNeedRetry && Time.frameCount >= nextCharacterAssignmentRetryFrame;
        if (characterPlacementDirty || retryMissingReferences || retryRingGeometry)
            UpdateCharacterPlacements();
    }

    private void Update()
    {
        if (!challengeEnabled)
        {
            if (ring != null && ring.gameObject.activeSelf)
                ring.gameObject.SetActive(false);
            return;
        }

        if (activePattern == null || activeEntry == null || activeEntry.warpedObject == null)
        {
            ResolveReferences();
            GoGoInteractionController_NoY3.WarpObjectEntry previousEntry = activeEntry;
            ResolveActiveEntry();
            if (activePattern == null || activeEntry == null || activeEntry.warpedObject == null)
                return;

            // The controller and its object list can become available after this
            // component's Start. Rebuild the ring as soon as the real entry is
            // resolved instead of leaving the 12 cm fallback ring forever.
            if (activeEntry != previousEntry)
            {
                ComputeTargetRingSize();
                UpdateRingPose();
            }
        }

        UpdateRingPose();
        bool correct = EvaluateCurrentTarget();
        if (correct && !wasCorrect && !successLatched)
        {
            successLatched = true;
            ExhibitionAudioFeedback.PlayCue(ExhibitionAudioFeedback.Cue.PlacementSuccess);
            PatternCompleted?.Invoke(activePattern.id);
        }
        if (!latchSuccessUntilPatternChange && !correct)
            successLatched = false;
        wasCorrect = correct;
        UpdateRingColor(successLatched || correct ? correctColor : incorrectColor);
    }

    public bool ActivatePattern(string patternId, bool resetSizes, bool save = true)
    {
        ResolveReferences();
        Pattern next = FindPattern(patternId);
        if (next == null)
            return false;

        if (resetSizes)
        {
            if (experienceResetter != null)
                experienceResetter.ResetForNextParticipant();
            if (deskScaleSlider != null)
                deskScaleSlider.SetScaleFromExternal(deskScaleSlider.sceneObjectReferenceScale);
        }

        activePattern = next;
        activePatternId = next.id.ToUpperInvariant();
        characterAssignmentsDirty = true;
        characterPlacementDirty = true;
        challengeEnabled = true;
        successLatched = false;
        wasCorrect = false;
        diagnosticStateInitialized = false;
        ResolveActiveEntry();
        if (!tabletopFrameCaptured)
        {
            tabletopHeight = ComputeTabletopHeight();
            tabletopFrameCaptured = true;
        }
        ComputeTargetRingSize();
        CreateRing();
        ring.gameObject.SetActive(true);
        UpdateRingPose();
        UpdateRingColor(incorrectColor);

        if (save && persistActivePattern && !string.IsNullOrWhiteSpace(activePatternPrefsKey))
        {
            PlayerPrefs.SetString(activePatternPrefsKey, activePatternId);
            PlayerPrefs.Save();
        }
        UpdateCharacterPlacements();
        return true;
    }

    public void DisableChallenge()
    {
        challengeEnabled = false;
        successLatched = false;
        wasCorrect = false;
        if (ring != null)
            ring.gameObject.SetActive(false);
        SetCharacterVisibility(false);
    }

    private void ResolveReferences()
    {
        if (redirectionController == null)
            redirectionController = DeskScaleSliderPanel.FindBestRedirectionController();
        if (deskOrigin == null && redirectionController != null)
            deskOrigin = redirectionController.deskOrigin;
        if (redirectionController != null && redirectionController.redirectionOrigin != null)
            redirectionOrigin = redirectionController.redirectionOrigin;
        SpatialAnchorToDeskOriginBinder binder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (binder != null)
        {
            if (deskOrigin == null) deskOrigin = binder.deskOrigin;
            // This is the point confirmed after Begin Anchor Placement. Prefer it
            // even when an earlier startup pass temporarily fell back to DeskOrigin.
            if (binder.redirectionOrigin != null) redirectionOrigin = binder.redirectionOrigin;
        }
        if (deskScaleSlider == null)
            deskScaleSlider = FindAnyObjectByType<DeskScaleSliderPanel>();
        if (tabletopReference == null && deskScaleSlider != null)
            tabletopReference = deskScaleSlider.primaryDeskReference;
        if (experienceResetter == null)
            experienceResetter = FindAnyObjectByType<ExhibitionExperienceResetter>();
    }

    private Pattern FindPattern(string id)
    {
        if (patterns == null || string.IsNullOrWhiteSpace(id)) return null;
        for (int i = 0; i < patterns.Length; i++)
            if (patterns[i] != null && string.Equals(patterns[i].id, id, StringComparison.OrdinalIgnoreCase))
                return patterns[i];
        return null;
    }

    private void EnsureCharacterDefinitions()
    {
        string[] ids = { "PANDA", "GORILLA", "ELEPHANT" };
        if (ringCharacters == null || ringCharacters.Length != ids.Length)
        {
            RingCharacter[] previous = ringCharacters;
            ringCharacters = new RingCharacter[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                RingCharacter retained = null;
                if (previous != null)
                {
                    for (int j = 0; j < previous.Length; j++)
                        if (previous[j] != null && string.Equals(previous[j].id, ids[i], StringComparison.OrdinalIgnoreCase))
                        { retained = previous[j]; break; }
                }
                ringCharacters[i] = retained ?? new RingCharacter { id = ids[i], ringSizeMultiplier = 1f };
            }
        }
        for (int i = 0; i < ids.Length; i++)
        {
            if (ringCharacters[i] == null) ringCharacters[i] = new RingCharacter();
            ringCharacters[i].id = ids[i];
            ringCharacters[i].ringSizeMultiplier = Mathf.Max(0.01f, ringCharacters[i].ringSizeMultiplier);
        }
    }

    private void ResolveCharacters()
    {
        EnsureCharacterDefinitions();
        if (charactersRoot == null)
        {
            GameObject namedRoot = GameObject.Find("Characters") ?? GameObject.Find("Charachters");
            if (namedRoot != null) charactersRoot = namedRoot.transform;
        }
        if (charactersRoot == null)
        {
            Transform[] all = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                Transform candidate = all[i];
                if (candidate != null && candidate.gameObject.scene.IsValid() &&
                    (string.Equals(candidate.name, "Characters", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(candidate.name, "Charachters", StringComparison.OrdinalIgnoreCase)))
                { charactersRoot = candidate; break; }
            }
        }
        if (charactersRoot == null)
        {
            nextCharacterResolveFrame = Time.frameCount + 30;
            return;
        }

        Transform[] descendants = charactersRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < ringCharacters.Length; i++)
        {
            RingCharacter definition = ringCharacters[i];
            if (definition.character != null) continue;
            string search = definition.id.ToLowerInvariant();
            for (int j = 0; j < descendants.Length; j++)
            {
                Transform candidate = descendants[j];
                if (candidate == charactersRoot) continue;
                if (candidate.name.ToLowerInvariant().Contains(search))
                { definition.character = candidate; ConfigureCharacterRenderers(candidate); break; }
            }
            if (definition.character != null) ConfigureCharacterRenderers(definition.character);
        }
        charactersResolved = true;
        for (int i = 0; i < ringCharacters.Length; i++)
            charactersResolved &= ringCharacters[i].character != null;
        nextCharacterResolveFrame = Time.frameCount + 30;
    }

    private RingCharacter FindCharacter(string characterId)
    {
        EnsureCharacterDefinitions();
        if (string.IsNullOrWhiteSpace(characterId)) return null;
        for (int i = 0; i < ringCharacters.Length; i++)
            if (string.Equals(ringCharacters[i].id, characterId, StringComparison.OrdinalIgnoreCase))
                return ringCharacters[i];
        return null;
    }

    private void ConfigureCharacterRenderers(Transform character)
    {
        if (character == null) return;
        Renderer[] renderers = character.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !alwaysOnTopConfiguredRenderers.Add(renderer.GetInstanceID()))
                continue;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;
            renderer.sortingOrder = 30000;

            // Use runtime material instances so imported/shared character assets
            // are not modified. Render after the scene and ignore its depth.
            Shader alwaysOnTopShader = Shader.Find("HandRedirection/Always On Top Unlit");
            if (alwaysOnTopShader == null)
                continue;
            Material[] sourceMaterials = renderer.sharedMaterials;
            Material[] materials = new Material[sourceMaterials.Length];
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material source = sourceMaterials[materialIndex];
                Material material = new Material(alwaysOnTopShader)
                {
                    name = (source != null ? source.name : "Character") + " Always On Top",
                    renderQueue = 4500
                };
                if (source != null)
                {
                    Texture texture = source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : source.mainTexture;
                    material.SetTexture("_BaseMap", texture);
                    material.SetTextureScale("_BaseMap", source.mainTextureScale);
                    material.SetTextureOffset("_BaseMap", source.mainTextureOffset);
                    Color color = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.color;
                    material.SetColor("_BaseColor", color);
                }
                materials[materialIndex] = material;
            }
            renderer.sharedMaterials = materials;
        }
    }

    public bool TryGetCharacterMultiplier(string characterId, out float multiplier)
    {
        RingCharacter definition = FindCharacter(characterId);
        multiplier = definition != null ? definition.ringSizeMultiplier : 1f;
        return definition != null;
    }

    public bool SetCharacterMultiplier(string characterId, float multiplier, bool save = true)
    {
        RingCharacter definition = FindCharacter(characterId);
        if (definition == null || !IsFinite(multiplier) || multiplier <= 0f)
            return false;
        definition.ringSizeMultiplier = Mathf.Max(0.01f, multiplier);
        characterPlacementDirty = true;
        if (save && persistCharacterSettings && !string.IsNullOrWhiteSpace(characterMultiplierPrefsKeyPrefix))
        {
            PlayerPrefs.SetFloat(characterMultiplierPrefsKeyPrefix + definition.id, definition.ringSizeMultiplier);
            PlayerPrefs.Save();
        }
        UpdateCharacterPlacements();
        return true;
    }

    public float GetGlobalCharacterYawOffset()
    {
        return globalCharacterYawOffsetDegrees;
    }

    public bool SetGlobalCharacterYawOffset(float degrees, bool save = true)
    {
        if (!IsFinite(degrees)) return false;
        globalCharacterYawOffsetDegrees = Mathf.Repeat(degrees + 180f, 360f) - 180f;
        characterPlacementDirty = true;
        if (save && persistCharacterSettings && !string.IsNullOrWhiteSpace(characterYawPrefsKey))
        {
            PlayerPrefs.SetFloat(characterYawPrefsKey, globalCharacterYawOffsetDegrees);
            PlayerPrefs.Save();
        }
        UpdateCharacterPlacements();
        return true;
    }

    public bool TryGetCharacterLayout(
        string characterId,
        out string patternId,
        out Vector2 deskPosition,
        out float diagramRadius,
        out float multiplier)
    {
        ResolveReferences();
        RingCharacter definition = FindCharacter(characterId);
        if (definition == null)
        {
            patternId = ""; deskPosition = Vector2.zero; diagramRadius = 0.1f; multiplier = 1f;
            return false;
        }
        RefreshCharacterAssignments();
        int characterIndex = Array.IndexOf(ringCharacters, definition);
        if (characterIndex < 0 || characterIndex >= characterPatternAssignments.Count)
        {
            patternId = ""; deskPosition = Vector2.zero; diagramRadius = 0.1f;
            multiplier = definition.ringSizeMultiplier;
            return false;
        }

        PatternFootprint assignment = characterPatternAssignments[characterIndex];
        patternId = assignment.pattern.id.ToUpperInvariant();
        multiplier = definition.ringSizeMultiplier;
        float ringRadius = Mathf.Max(assignment.radii.x, assignment.radii.y);
        diagramRadius = ringRadius * multiplier;
        Vector3 worldGroundCenter = GetCharacterWorldGroundCenter(assignment.pattern, ringRadius, diagramRadius);
        deskPosition = WorldToDeskPlanarPosition(worldGroundCenter);
        return true;
    }

    private void RefreshCharacterAssignments()
    {
        if (!characterAssignmentsDirty && characterPatternAssignments.Count == 3)
        {
            if (!characterAssignmentsNeedRetry || Time.frameCount < nextCharacterAssignmentRetryFrame)
                return;
        }
        characterPatternAssignments.Clear();
        characterAssignmentsNeedRetry = false;
        if (patterns != null)
        {
            for (int i = 0; i < patterns.Length; i++)
            {
                Pattern pattern = patterns[i];
                if (pattern == null) continue;
                GoGoInteractionController_NoY3.WarpObjectEntry entry = FindEntryForPattern(pattern);
                if (!TryCalculatePatternGeometry(pattern, entry, out _, out Vector2 radii))
                {
                    radii = new Vector2(0.12f, 0.12f);
                    characterAssignmentsNeedRetry = true;
                }
                characterPatternAssignments.Add(new PatternFootprint { pattern = pattern, radii = radii });
            }
        }
        characterPatternAssignments.Sort((a, b) =>
        {
            float ar = Mathf.Max(a.radii.x, a.radii.y);
            float br = Mathf.Max(b.radii.x, b.radii.y);
            int sizeComparison = ar.CompareTo(br);
            return sizeComparison != 0 ? sizeComparison : string.CompareOrdinal(a.pattern.id, b.pattern.id);
        });
        characterAssignmentsDirty = false;
        nextCharacterAssignmentRetryFrame = Time.frameCount + 30;
    }

    private Vector3 GetCharacterWorldGroundCenter(Pattern pattern, float ringRadius, float characterRadius)
    {
        Vector3 up = deskOrigin != null ? deskOrigin.up.normalized : Vector3.up;
        Vector3 tabletopPoint = deskOrigin != null ? deskOrigin.position + up * tabletopHeight : Vector3.zero;
        Vector3 ringGround = GetRingCenter(pattern);
        ringGround -= up * Vector3.Dot(ringGround - tabletopPoint, up);
        Transform origin = redirectionOrigin != null ? redirectionOrigin : deskOrigin;
        Vector3 originGround = origin != null ? origin.position : tabletopPoint;
        originGround -= up * Vector3.Dot(originGround - tabletopPoint, up);
        Vector3 awayFromOrigin = Vector3.ProjectOnPlane(ringGround - originGround, up);
        if (awayFromOrigin.sqrMagnitude < 1e-6f)
        {
            GetPatternAxes(out _, out _, out Vector3 fallbackForward);
            awayFromOrigin = fallbackForward;
        }
        float behindDistance = Mathf.Max(0f, ringRadius) + Mathf.Max(0f, characterRadius) + characterBehindRingGapMeters;
        return ringGround + awayFromOrigin.normalized * behindDistance;
    }

    private Vector2 WorldToDeskPlanarPosition(Vector3 worldPosition)
    {
        if (deskOrigin == null) return Vector2.zero;
        Vector3 up = deskOrigin.up.normalized;
        GetDeskPlanarAxes(up, out Vector3 deskRight, out Vector3 deskForward);
        Vector3 delta = worldPosition - deskOrigin.position;
        return new Vector2(Vector3.Dot(delta, deskRight), Vector3.Dot(delta, deskForward));
    }

    private void ResolveActiveEntry()
    {
        activeEntry = FindEntryForPattern(activePattern);
        CaptureBaselineScale(activeEntry);
    }

    private GoGoInteractionController_NoY3.WarpObjectEntry FindEntryForPattern(Pattern pattern)
    {
        if (pattern == null || redirectionController == null || redirectionController.objects == null)
            return null;
        for (int i = 0; i < redirectionController.objects.Count; i++)
        {
            GoGoInteractionController_NoY3.WarpObjectEntry entry = redirectionController.objects[i];
            if (entry != null && entry.enabled && string.Equals(entry.name, pattern.targetObjectId, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private void CaptureBaselineScale(GoGoInteractionController_NoY3.WarpObjectEntry entry)
    {
        if (entry == null || baselineScaleByEntry.ContainsKey(entry))
            return;
        Transform scaleSource = entry.warpedScaleSource != null ? entry.warpedScaleSource : entry.warpedObject;
        if (scaleSource == null)
            return;
        Vector3 baseline = entry.baseScaleInitialized ? entry.baseWarpedScale : scaleSource.localScale;
        if (Mathf.Abs(baseline.x) < 1e-5f || Mathf.Abs(baseline.y) < 1e-5f || Mathf.Abs(baseline.z) < 1e-5f)
            baseline = scaleSource.localScale;
        baselineScaleByEntry[entry] = baseline;
    }

    private float ComputeTabletopHeight()
    {
        if (deskOrigin == null || tabletopReference == null) return 0f;
        Renderer[] renderers = tabletopReference.GetComponentsInChildren<Renderer>(false);
        float highest = 0f;
        bool found = false;
        Vector3 up = deskOrigin.up.normalized;
        for (int i = 0; i < renderers.Length; i++)
        {
            Bounds b = renderers[i].bounds;
            Vector3 c = b.center;
            Vector3 e = b.extents;
            for (int mask = 0; mask < 8; mask++)
            {
                Vector3 corner = c + new Vector3((mask & 1) == 0 ? -e.x : e.x, (mask & 2) == 0 ? -e.y : e.y, (mask & 4) == 0 ? -e.z : e.z);
                float h = Vector3.Dot(corner - deskOrigin.position, up);
                if (!found || h > highest) { highest = h; found = true; }
            }
        }
        return found ? highest : 0f;
    }

    public bool TryGetPatternSettings(
        string patternId,
        out string targetObjectId,
        out Vector3 targetScale,
        out Vector3 scaleTolerance,
        out Vector3 positionTolerance)
    {
        Pattern pattern = FindPattern(patternId);
        if (pattern == null)
        {
            targetObjectId = "";
            targetScale = Vector3.one;
            scaleTolerance = Vector3.one * 0.1f;
            positionTolerance = Vector3.one * 0.09f;
            return false;
        }
        targetObjectId = pattern.targetObjectId;
        targetScale = pattern.targetScaleRatio;
        scaleTolerance = pattern.scaleToleranceRatio;
        positionTolerance = pattern.positionToleranceMeters;
        return true;
    }

    public bool SetPatternSettings(
        string patternId,
        string targetObjectId,
        Vector3 targetScale,
        Vector3 scaleTolerance,
        Vector3 positionTolerance,
        bool save = true)
    {
        Pattern pattern = FindPattern(patternId);
        if (pattern == null || string.IsNullOrWhiteSpace(targetObjectId))
            return false;

        pattern.targetObjectId = targetObjectId.Trim();
        pattern.targetScaleRatio = MaxComponents(targetScale, 0.01f);
        pattern.scaleToleranceRatio = MaxComponents(scaleTolerance, 0f);
        pattern.positionToleranceMeters = MaxComponents(positionTolerance, 0.001f);
        characterAssignmentsDirty = true;
        characterPlacementDirty = true;
        if (save)
            SavePatternSettings(pattern);
        if (activePattern == pattern)
        {
            successLatched = false;
            wasCorrect = false;
            diagnosticStateInitialized = false;
            ResolveActiveEntry();
            ComputeTargetRingSize();
            UpdateRingPose();
            UpdateRingColor(incorrectColor);
        }
        UpdateCharacterPlacements();
        return true;
    }

    public bool TryGetPatternLayout(
        string patternId,
        out string targetObjectId,
        out Vector2 deskPosition,
        out Vector2 calculatedRingRadii)
    {
        ResolveReferences();
        Pattern pattern = FindPattern(patternId);
        if (pattern == null)
        {
            targetObjectId = "";
            deskPosition = Vector2.zero;
            calculatedRingRadii = new Vector2(0.12f, 0.12f);
            return false;
        }

        targetObjectId = pattern.targetObjectId;
        deskPosition = GetDeskOriginRelativePosition(pattern);
        GoGoInteractionController_NoY3.WarpObjectEntry entry = FindEntryForPattern(pattern);
        if (!TryCalculatePatternGeometry(pattern, entry, out _, out calculatedRingRadii))
            calculatedRingRadii = new Vector2(0.12f, 0.12f);
        return true;
    }

    public bool SetPatternPosition(string patternId, Vector2 deskPosition, bool save = true)
    {
        ResolveReferences();
        Pattern pattern = FindPattern(patternId);
        if (pattern == null || deskOrigin == null || !IsFinite(deskPosition.x) || !IsFinite(deskPosition.y))
            return false;

        // Launcher diagram coordinates are RedirectionOrigin-local X/Z. Keep the
        // serialized field name for settings compatibility, but never convert
        // through DeskOrigin here.
        pattern.deskLocalPosition = deskPosition;
        characterPlacementDirty = true;
        if (save)
            SavePatternSettings(pattern);
        if (activePattern == pattern)
            UpdateRingPose();
        UpdateCharacterPlacements();
        Debug.Log(
            $"[ScalePlacementChallenge] Pattern {pattern.id} position updated " +
            $"RedirectionOriginLocalXZ=({pattern.deskLocalPosition.x:F3},{pattern.deskLocalPosition.y:F3})");
        return true;
    }

    private void ComputeTargetRingSize()
    {
        ringRadii = new Vector2(0.12f, 0.12f);
        targetObjectHalfExtents = new Vector3(0.095f, 0.05f, 0.095f);
        targetObjectLongestHalfExtent = 0.095f;
        activeTargetUsesUniformScale = false;
        targetObjectHalfHeight = 0.05f;
        if (activeEntry == null || activeEntry.warpedObject == null || activePattern == null)
            return;

        if (!TryCalculatePatternGeometry(activePattern, activeEntry, out Vector3 targetHalfExtents, out Vector2 calculatedRadii))
            return;
        targetObjectHalfExtents = MaxComponents(targetHalfExtents, 0.005f);
        targetObjectLongestHalfExtent = MaxComponent(targetObjectHalfExtents);
        activeTargetUsesUniformScale = ApproximatelyUniform(activePattern.targetScaleRatio);
        ringRadii = calculatedRadii;
        targetObjectHalfHeight = targetObjectHalfExtents.y;
        Transform scaleSource = activeEntry.warpedScaleSource != null ? activeEntry.warpedScaleSource : activeEntry.warpedObject;
        Vector3 baselineScale = baselineScaleByEntry.TryGetValue(activeEntry, out Vector3 capturedScale) ? capturedScale : scaleSource.localScale;
        Vector3 desiredScale = Vector3.Scale(baselineScale, activePattern.targetScaleRatio);
        Debug.Log(
            $"[ScalePlacementChallenge] Pattern {activePattern.id} ring rebuilt " +
            $"object={activePattern.targetObjectId} measurement=object_local_axes " +
            $"targetScale={activePattern.targetScaleRatio:F3} baselineScale={baselineScale:F3} " +
            $"desiredScale={desiredScale:F3} targetHalfExtents={targetObjectHalfExtents:F3} " +
            $"ringRadii=({ringRadii.x:F3},{ringRadii.y:F3})");
    }

    private bool TryCalculatePatternGeometry(
        Pattern pattern,
        GoGoInteractionController_NoY3.WarpObjectEntry entry,
        out Vector3 targetHalfExtents,
        out Vector2 calculatedRadii)
    {
        targetHalfExtents = new Vector3(0.095f, 0.05f, 0.095f);
        calculatedRadii = new Vector2(0.12f, 0.12f);
        if (pattern == null || entry == null || entry.warpedObject == null)
            return false;

        Transform scaleSource = entry.warpedScaleSource != null ? entry.warpedScaleSource : entry.warpedObject;
        GetObjectMeasurementAxes(scaleSource, out Vector3 sizeX, out Vector3 sizeY, out Vector3 sizeZ);
        CaptureBaselineScale(entry);
        Vector3 baselineScale = baselineScaleByEntry.TryGetValue(entry, out Vector3 capturedScale)
            ? capturedScale
            : scaleSource.localScale;
        Vector3 desiredScale = Vector3.Scale(baselineScale, pattern.targetScaleRatio);
        if (!TryGetVisualHalfExtentsAtScale(
                entry.warpedObject, scaleSource, desiredScale,
                sizeX, sizeY, sizeZ, out _, out targetHalfExtents))
            return false;

        targetHalfExtents = MaxComponents(targetHalfExtents, 0.005f);
        if (ApproximatelyUniform(pattern.targetScaleRatio))
        {
            float radius = Mathf.Max(0.04f, MaxComponent(targetHalfExtents) + pattern.ringPaddingMeters);
            calculatedRadii = new Vector2(radius, radius);
        }
        else
        {
            calculatedRadii = new Vector2(
                Mathf.Max(0.04f, targetHalfExtents.x + pattern.ringPaddingMeters),
                Mathf.Max(0.04f, targetHalfExtents.z + pattern.ringPaddingMeters));
        }
        return true;
    }

    private bool EvaluateCurrentTarget()
    {
        if (deskOrigin == null || activeEntry == null || activePattern == null)
            return false;

        GetPatternAxes(out Vector3 right, out Vector3 up, out Vector3 forward);
        if (!TryGetVisualHalfExtents(activeEntry.warpedObject, right, up, forward, out Vector3 visualCenter, out _))
            return false;
        Transform scaleSource = activeEntry.warpedScaleSource != null ? activeEntry.warpedScaleSource : activeEntry.warpedObject;
        Vector3 expectedCenter = GetExpectedObjectCenter(up);
        Vector3 visualDelta = visualCenter - expectedCenter;
        Vector3 pivotDelta = activeEntry.warpedObject.position - expectedCenter;
        float visualPositionErrorX = Mathf.Abs(Vector3.Dot(visualDelta, right));
        float visualPositionErrorZ = Mathf.Abs(Vector3.Dot(visualDelta, forward));
        float pivotPositionErrorX = Mathf.Abs(Vector3.Dot(pivotDelta, right));
        float pivotPositionErrorZ = Mathf.Abs(Vector3.Dot(pivotDelta, forward));
        Vector3 positionTolerance = activePattern.positionToleranceMeters;
        // Deliberately ignore height. The task is placement inside a target on
        // the tabletop plane, so only RedirectionOrigin-relative X/Z can block
        // completion.
        bool visualCenterCorrect = visualPositionErrorX <= positionTolerance.x &&
                                   visualPositionErrorZ <= positionTolerance.z;
        bool trackedPivotCorrect = pivotPositionErrorX <= positionTolerance.x &&
                                   pivotPositionErrorZ <= positionTolerance.z;
        bool positionCorrect = visualCenterCorrect || trackedPivotCorrect;

        // All three scene targets are deformed by changing this exact
        // Transform.localScale. Judge the requested ratio directly so mesh
        // asymmetry, renderer hierarchy, and tracker rotation cannot make B/C
        // disagree with A. Renderer dimensions remain responsible only for the
        // physical ring diameter.
        CaptureBaselineScale(activeEntry);
        bool sizeCorrect = false;
        Vector3 currentRatio = Vector3.one;
        if (baselineScaleByEntry.TryGetValue(activeEntry, out Vector3 baselineScale))
        {
            currentRatio = AbsScaleRatio(scaleSource.localScale, baselineScale);
            Vector3 ratioTolerance = Vector3.Scale(activePattern.targetScaleRatio, activePattern.scaleToleranceRatio);
            ratioTolerance = MaxComponents(ratioTolerance, 0.001f);
            sizeCorrect = Mathf.Abs(currentRatio.x - activePattern.targetScaleRatio.x) <= ratioTolerance.x &&
                          Mathf.Abs(currentRatio.y - activePattern.targetScaleRatio.y) <= ratioTolerance.y &&
                          Mathf.Abs(currentRatio.z - activePattern.targetScaleRatio.z) <= ratioTolerance.z;
        }

        if (!diagnosticStateInitialized ||
            positionCorrect != lastDiagnosticPositionCorrect ||
            sizeCorrect != lastDiagnosticSizeCorrect)
        {
            Debug.Log(
                $"[ScalePlacementChallenge] Pattern {activePattern.id} evaluation " +
                $"object={activePattern.targetObjectId} position={(positionCorrect ? "OK" : "NG")} " +
                $"visualErrorXZ=({visualPositionErrorX:F3},{visualPositionErrorZ:F3}) " +
                $"pivotErrorXZ=({pivotPositionErrorX:F3},{pivotPositionErrorZ:F3}) " +
                $"toleranceXZ=({positionTolerance.x:F3},{positionTolerance.z:F3}) " +
                $"size={(sizeCorrect ? "OK" : "NG")} currentRatio={currentRatio:F3} " +
                $"targetRatio={activePattern.targetScaleRatio:F3} toleranceRatio={activePattern.scaleToleranceRatio:F3}");
            diagnosticStateInitialized = true;
            lastDiagnosticPositionCorrect = positionCorrect;
            lastDiagnosticSizeCorrect = sizeCorrect;
        }

        return positionCorrect && sizeCorrect;
    }

    private Vector3 GetRingCenter()
    {
        return GetRingCenter(activePattern);
    }

    private Vector3 GetRingCenter(Pattern pattern)
    {
        if (deskOrigin == null || pattern == null) return transform.position;
        GetPatternAxes(out Vector3 right, out Vector3 up, out Vector3 forward);
        Transform origin = redirectionOrigin != null ? redirectionOrigin : deskOrigin;
        Vector3 basePosition = origin.position;
        float baseHeight = Vector3.Dot(basePosition - deskOrigin.position, up);
        basePosition += up * (tabletopHeight - baseHeight);
        return basePosition
            + right * pattern.deskLocalPosition.x
            + forward * pattern.deskLocalPosition.y
            + up * tabletopLiftMeters;
    }

    private Vector2 GetDeskOriginRelativePosition(Pattern pattern)
    {
        // Kept under its old public-protocol name for launcher compatibility.
        // Values are now always relative to the confirmed RedirectionOrigin.
        return pattern != null ? pattern.deskLocalPosition : Vector2.zero;
    }

    private void GetDeskPlanarAxes(Vector3 up, out Vector3 deskRight, out Vector3 deskForward)
    {
        deskForward = Vector3.ProjectOnPlane(deskOrigin.forward, up).normalized;
        if (deskForward.sqrMagnitude < 1e-6f)
            deskForward = Vector3.forward;
        deskRight = Vector3.Cross(up, deskForward).normalized;
        Vector3 sourceRight = Vector3.ProjectOnPlane(deskOrigin.right, up).normalized;
        if (sourceRight.sqrMagnitude > 1e-6f && Vector3.Dot(deskRight, sourceRight) < 0f)
            deskRight = -deskRight;
    }

    private void UpdateCharacterPlacements()
    {
        if (!showRingCharacters || !challengeEnabled)
        {
            SetCharacterVisibility(false);
            characterPlacementDirty = false;
            return;
        }

        ResolveReferences();
        if (!charactersResolved) ResolveCharacters();
        if (charactersRoot == null || deskOrigin == null)
        {
            characterPlacementDirty = false;
            nextCharacterResolveFrame = Time.frameCount + 30;
            return;
        }
        if (!tabletopFrameCaptured)
        {
            tabletopHeight = ComputeTabletopHeight();
            tabletopFrameCaptured = true;
        }

        RefreshCharacterAssignments();
        int count = Mathf.Min(ringCharacters.Length, characterPatternAssignments.Count);
        Vector3 up = deskOrigin.up.normalized;
        GetDeskPlanarAxes(up, out Vector3 deskRight, out Vector3 deskForward);
        int activeCharacterIndex = -1;
        for (int i = 0; i < count; i++)
        {
            PatternFootprint assignment = characterPatternAssignments[i];
            if (assignment.pattern == activePattern)
            {
                activeCharacterIndex = i;
                break;
            }
        }
        SetOnlyCharacterVisible(activeCharacterIndex);
        if (activeCharacterIndex >= 0 && activeCharacterIndex < ringCharacters.Length)
            Debug.Log($"[RingCharacterVisibilityV2] instance={GetInstanceID()} active={ringCharacters[activeCharacterIndex].id} pattern={activePattern.id}");
        if (activeCharacterIndex >= 0 && activeCharacterIndex < count)
        {
            RingCharacter definition = ringCharacters[activeCharacterIndex];
            PatternFootprint assignment = characterPatternAssignments[activeCharacterIndex];
            if (definition == null || definition.character == null)
            {
                characterPlacementDirty = false;
                return;
            }
            float assignedRingRadius = Mathf.Max(assignment.radii.x, assignment.radii.y);
            float desiredRadius = Mathf.Max(0.01f,
                assignedRingRadius * definition.ringSizeMultiplier);
            Vector3 worldGroundCenter = GetCharacterWorldGroundCenter(assignment.pattern, assignedRingRadius, desiredRadius);
            PlaceCharacter(definition, worldGroundCenter, desiredRadius, deskRight, up, deskForward);
        }
        characterPlacementDirty = false;
    }

    private void SetCharacterVisibility(bool visible)
    {
        if (!charactersResolved) ResolveCharacters();
        if (ringCharacters == null) return;
        for (int i = 0; i < ringCharacters.Length; i++)
        {
            Transform character = ringCharacters[i] != null ? ringCharacters[i].character : null;
            if (character != null && character.gameObject.activeSelf != visible)
                character.gameObject.SetActive(visible);
        }
    }

    private void SetOnlyCharacterVisible(int visibleIndex)
    {
        if (ringCharacters == null) return;
        for (int i = 0; i < ringCharacters.Length; i++)
        {
            Transform character = ringCharacters[i] != null ? ringCharacters[i].character : null;
            bool shouldBeVisible = i == visibleIndex;
            if (character != null && character.gameObject.activeSelf != shouldBeVisible)
                character.gameObject.SetActive(shouldBeVisible);
        }
    }

    private void PlaceCharacter(
        RingCharacter definition,
        Vector3 desiredGroundCenter,
        float desiredRadius,
        Vector3 deskRight,
        Vector3 up,
        Vector3 deskForward)
    {
        Transform character = definition.character;
        if (!character.gameObject.activeSelf) character.gameObject.SetActive(true);
        Transform facingTarget = redirectionOrigin != null ? redirectionOrigin : deskOrigin;
        Vector3 towardOrigin = Vector3.ProjectOnPlane(facingTarget.position - desiredGroundCenter, up);
        if (towardOrigin.sqrMagnitude > 1e-6f)
        {
            Quaternion faceOrigin = Quaternion.LookRotation(towardOrigin.normalized, up);
            // Imported character models face opposite Unity's +Z convention.
            // Flip the common baseline while preserving launcher/global and
            // per-character adjustment values as additional offsets.
            float yawCorrection = 180f + globalCharacterYawOffsetDegrees + definition.forwardYawOffsetDegrees;
            character.rotation = faceOrigin * Quaternion.AngleAxis(yawCorrection, Vector3.up);
        }

        if (!TryGetVisualHalfExtents(character, deskRight, up, deskForward, out _, out Vector3 beforeScale))
            return;
        float currentRadius = Mathf.Max(beforeScale.x, beforeScale.z);
        if (currentRadius > 1e-5f)
        {
            float scaleFactor = desiredRadius / currentRadius;
            if (Mathf.Abs(scaleFactor - 1f) > 0.0005f)
                character.localScale *= scaleFactor;
        }

        if (!TryGetVisualHalfExtents(character, deskRight, up, deskForward, out Vector3 visualCenter, out Vector3 halfExtents))
            return;
        Vector3 centerFromDesk = visualCenter - deskOrigin.position;
        float currentBottomHeight = Vector3.Dot(centerFromDesk, up) - halfExtents.y;
        Vector2 deskPosition = WorldToDeskPlanarPosition(desiredGroundCenter);
        Vector3 planarDelta = Vector3.ProjectOnPlane(desiredGroundCenter - visualCenter, up);
        character.position += planarDelta + up * (tabletopHeight - currentBottomHeight);
        Debug.Log(
            $"[RingCharacterPlacementV2] instance={GetInstanceID()} character={definition.id} " +
            $"deskXZ=({deskPosition.x:F3},{deskPosition.y:F3}) radius={desiredRadius:F3} " +
            $"yawOffset={globalCharacterYawOffsetDegrees + definition.forwardYawOffsetDegrees:F1} " +
            $"worldPosition={character.position:F3}");
    }

    private Vector3 GetExpectedObjectCenter(Vector3 up)
    {
        return GetRingCenter() + up * (targetObjectHalfHeight - tabletopLiftMeters);
    }

    private void GetPatternAxes(out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        Transform origin = redirectionOrigin != null ? redirectionOrigin : deskOrigin;
        up = deskOrigin != null ? deskOrigin.up.normalized : Vector3.up;
        forward = origin != null ? Vector3.ProjectOnPlane(origin.forward, up) : Vector3.forward;
        if (forward.sqrMagnitude < 1e-6f)
            forward = deskOrigin != null ? Vector3.ProjectOnPlane(deskOrigin.forward, up) : Vector3.forward;
        forward.Normalize();
        Vector3 originRight = origin != null ? Vector3.ProjectOnPlane(origin.right, up) : Vector3.zero;
        right = Vector3.Cross(up, forward).normalized;
        if (originRight.sqrMagnitude > 1e-6f && Vector3.Dot(right, originRight) < 0f)
            right = -right;
    }

    private static void GetObjectMeasurementAxes(
        Transform scaleSource,
        out Vector3 axisX,
        out Vector3 axisY,
        out Vector3 axisZ)
    {
        if (scaleSource == null)
        {
            axisX = Vector3.right;
            axisY = Vector3.up;
            axisZ = Vector3.forward;
            return;
        }

        // Renderer dimensions must be measured in axes that rotate with the
        // selected object. Measuring an elongated object in fixed world axes
        // changes its apparent X/Y/Z bounds whenever its tracker rotates; A
        // looked correct only because its shape was close to symmetric.
        axisX = scaleSource.right.normalized;
        axisY = Vector3.ProjectOnPlane(scaleSource.up, axisX).normalized;
        if (axisY.sqrMagnitude < 1e-6f)
            axisY = scaleSource.up.normalized;
        axisZ = Vector3.Cross(axisX, axisY).normalized;
        Vector3 sourceForward = scaleSource.forward.normalized;
        if (Vector3.Dot(axisZ, sourceForward) < 0f)
            axisZ = -axisZ;
    }

    private void CreateRing()
    {
        if (ring != null) return;
        GameObject go = new GameObject("Scale Placement Target Ring");
        go.transform.SetParent(transform, false);
        ring = go.AddComponent<LineRenderer>();
        ring.useWorldSpace = true;
        ring.loop = true;
        ring.positionCount = Mathf.Clamp(ringSegments, 24, 128);
        ring.startWidth = ringWidth;
        ring.endWidth = ringWidth;
        ring.numCapVertices = 4;
        Shader shader = Shader.Find("HandRedirection/Always On Top Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        ringMaterial = new Material(shader) { name = "Scale Placement Ring Material" };
        if (ringMaterial.HasProperty("_ZTest"))
            ringMaterial.SetFloat("_ZTest", (float)CompareFunction.Always);
        if (ringMaterial.HasProperty("_ZWrite"))
            ringMaterial.SetFloat("_ZWrite", 0f);
        ringMaterial.renderQueue = 4500;
        ring.sharedMaterial = ringMaterial;
        ring.sortingOrder = 30000;
    }

    private void UpdateRingPose()
    {
        if (ring == null || deskOrigin == null || activePattern == null) return;
        int count = Mathf.Clamp(ringSegments, 24, 128);
        if (ring.positionCount != count) ring.positionCount = count;
        ring.startWidth = ringWidth;
        ring.endWidth = ringWidth;
        Vector3 center = GetRingCenter();
        GetPatternAxes(out Vector3 right, out _, out Vector3 forward);
        for (int i = 0; i < count; i++)
        {
            float angle = (Mathf.PI * 2f * i) / count;
            ring.SetPosition(i, center + right * (Mathf.Cos(angle) * ringRadii.x) + forward * (Mathf.Sin(angle) * ringRadii.y));
        }
    }

    private void UpdateRingColor(Color color)
    {
        if (ring == null) return;
        ring.startColor = color;
        ring.endColor = color;
        if (ringMaterial != null)
        {
            if (ringMaterial.HasProperty("_BaseColor")) ringMaterial.SetColor("_BaseColor", color);
            if (ringMaterial.HasProperty("_Color")) ringMaterial.SetColor("_Color", color);
        }
    }

    private bool TryGetVisualHalfExtents(Transform root, Vector3 right, Vector3 up, Vector3 forward, out Vector3 center, out Vector3 halfExtents)
    {
        return TryGetProjectedVisualBounds(root, null, Vector3.one, false, right, up, forward, out center, out halfExtents);
    }

    private bool TryGetVisualHalfExtentsAtScale(
        Transform root,
        Transform scaleSource,
        Vector3 desiredLocalScale,
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        out Vector3 center,
        out Vector3 halfExtents)
    {
        return TryGetProjectedVisualBounds(
            root, scaleSource, desiredLocalScale, true,
            right, up, forward, out center, out halfExtents);
    }

    private bool TryGetProjectedVisualBounds(
        Transform root,
        Transform scaleSource,
        Vector3 desiredLocalScale,
        bool overrideScale,
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        out Vector3 center,
        out Vector3 halfExtents)
    {
        center = root != null ? root.position : Vector3.zero;
        halfExtents = Vector3.zero;
        if (root == null)
            return false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
        bool found = false;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        Matrix4x4 desiredScaleSourceLocalToWorld = Matrix4x4.identity;
        if (overrideScale && scaleSource != null)
        {
            Matrix4x4 parentLocalToWorld = scaleSource.parent != null
                ? scaleSource.parent.localToWorldMatrix
                : Matrix4x4.identity;
            desiredScaleSourceLocalToWorld = parentLocalToWorld * Matrix4x4.TRS(
                scaleSource.localPosition, scaleSource.localRotation, desiredLocalScale);
        }

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (!IsChallengeObjectRenderer(renderer))
                continue;

            Matrix4x4 rendererLocalToWorld = renderer.localToWorldMatrix;
            if (overrideScale && scaleSource != null &&
                (renderer.transform == scaleSource || renderer.transform.IsChildOf(scaleSource)))
            {
                Matrix4x4 rendererLocalToScaleSource = scaleSource.worldToLocalMatrix * renderer.localToWorldMatrix;
                rendererLocalToWorld = desiredScaleSourceLocalToWorld * rendererLocalToScaleSource;
            }

            Bounds localBounds = renderer.localBounds;
            Vector3 c = localBounds.center;
            Vector3 e = localBounds.extents;
            for (int mask = 0; mask < 8; mask++)
            {
                Vector3 localCorner = c + new Vector3(
                    (mask & 1) == 0 ? -e.x : e.x,
                    (mask & 2) == 0 ? -e.y : e.y,
                    (mask & 4) == 0 ? -e.z : e.z);
                Vector3 worldCorner = rendererLocalToWorld.MultiplyPoint3x4(localCorner);
                float x = Vector3.Dot(worldCorner, right);
                float y = Vector3.Dot(worldCorner, up);
                float z = Vector3.Dot(worldCorner, forward);
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                found = true;
            }
        }

        if (!found)
            return false;
        center = right * ((minX + maxX) * 0.5f)
               + up * ((minY + maxY) * 0.5f)
               + forward * ((minZ + maxZ) * 0.5f);
        halfExtents = new Vector3(
            (maxX - minX) * 0.5f,
            (maxY - minY) * 0.5f,
            (maxZ - minZ) * 0.5f);
        return halfExtents.sqrMagnitude > 1e-8f;
    }

    private void LoadAllPatternSettings()
    {
        if (!persistPatternSettings || patterns == null || string.IsNullOrWhiteSpace(patternSettingsPrefsKeyPrefix))
            return;
        for (int i = 0; i < patterns.Length; i++)
        {
            Pattern pattern = patterns[i];
            if (pattern == null || string.IsNullOrWhiteSpace(pattern.id)) continue;
            string json = PlayerPrefs.GetString(patternSettingsPrefsKeyPrefix + pattern.id.ToUpperInvariant(), "");
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                SavedPatternSettings saved = JsonUtility.FromJson<SavedPatternSettings>(json);
                if (!string.IsNullOrWhiteSpace(saved.targetObjectId))
                    pattern.targetObjectId = saved.targetObjectId.Trim();
                if (saved.version >= 2)
                    pattern.deskLocalPosition = saved.deskLocalPosition;
                pattern.targetScaleRatio = MaxComponents(saved.targetScaleRatio, 0.01f);
                pattern.scaleToleranceRatio = MaxComponents(saved.scaleToleranceRatio, 0f);
                pattern.positionToleranceMeters = MaxComponents(saved.positionToleranceMeters, 0.001f);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScalePlacementChallenge] Failed to load pattern {pattern.id}: {e.Message}");
            }
        }
    }

    private void LoadCharacterSettings()
    {
        if (!persistCharacterSettings || string.IsNullOrWhiteSpace(characterMultiplierPrefsKeyPrefix))
            return;
        EnsureCharacterDefinitions();
        for (int i = 0; i < ringCharacters.Length; i++)
        {
            RingCharacter definition = ringCharacters[i];
            string key = characterMultiplierPrefsKeyPrefix + definition.id;
            if (PlayerPrefs.HasKey(key))
                definition.ringSizeMultiplier = Mathf.Max(0.01f, PlayerPrefs.GetFloat(key, definition.ringSizeMultiplier));
        }
        if (!string.IsNullOrWhiteSpace(characterYawPrefsKey) && PlayerPrefs.HasKey(characterYawPrefsKey))
            globalCharacterYawOffsetDegrees = PlayerPrefs.GetFloat(characterYawPrefsKey, globalCharacterYawOffsetDegrees);
    }

    private void SavePatternSettings(Pattern pattern)
    {
        if (!persistPatternSettings || pattern == null || string.IsNullOrWhiteSpace(patternSettingsPrefsKeyPrefix))
            return;
        SavedPatternSettings saved = new SavedPatternSettings
        {
            version = 2,
            targetObjectId = pattern.targetObjectId,
            deskLocalPosition = pattern.deskLocalPosition,
            targetScaleRatio = pattern.targetScaleRatio,
            scaleToleranceRatio = pattern.scaleToleranceRatio,
            positionToleranceMeters = pattern.positionToleranceMeters
        };
        PlayerPrefs.SetString(patternSettingsPrefsKeyPrefix + pattern.id.ToUpperInvariant(), JsonUtility.ToJson(saved));
        PlayerPrefs.Save();
    }

    private static Vector3 MaxComponents(Vector3 value, float minimum)
    {
        return new Vector3(
            Mathf.Max(minimum, value.x),
            Mathf.Max(minimum, value.y),
            Mathf.Max(minimum, value.z));
    }

    private static float MaxComponent(Vector3 value)
    {
        return Mathf.Max(value.x, Mathf.Max(value.y, value.z));
    }

    private static bool ApproximatelyUniform(Vector3 value)
    {
        const float epsilon = 0.0001f;
        return Mathf.Abs(value.x - value.y) <= epsilon &&
               Mathf.Abs(value.x - value.z) <= epsilon;
    }

    private static Vector3 AbsScaleRatio(Vector3 current, Vector3 baseline)
    {
        return new Vector3(
            Mathf.Abs(baseline.x) > 1e-6f ? Mathf.Abs(current.x / baseline.x) : 1f,
            Mathf.Abs(baseline.y) > 1e-6f ? Mathf.Abs(current.y / baseline.y) : 1f,
            Mathf.Abs(baseline.z) > 1e-6f ? Mathf.Abs(current.z / baseline.z) : 1f);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsChallengeObjectRenderer(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled || renderer is LineRenderer || renderer.GetComponentInParent<DeformHandle>() != null)
            return false;
        string rendererName = renderer.name.ToLowerInvariant();
        return !rendererName.Contains("axis") &&
               !rendererName.Contains("debug") &&
               !rendererName.Contains("coordinate");
    }

    private void OnDestroy()
    {
        if (ringMaterial != null) Destroy(ringMaterial);
    }
}
