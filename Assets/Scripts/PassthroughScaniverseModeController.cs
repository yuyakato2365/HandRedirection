using UnityEngine;

public sealed class PassthroughScaniverseModeController : MonoBehaviour
{
    public enum PassthroughScaniverseMode
    {
        DiminishedReality,
        ScaledScaniverseRoom
    }

    [Header("Mode")]
    public PassthroughScaniverseMode startMode = PassthroughScaniverseMode.ScaledScaniverseRoom;
    public bool applyStartModeOnStart = true;

    [Header("References")]
    public DeskScaleSliderPanel deskScaleSliderPanel;
    public HandLocalScaniverseOcclusion handLocalScaniverseOcclusion;
    public bool autoFindReferences = true;
    public bool rebuildDiminishedRealityOverlayWhenEnabled = true;

    public PassthroughScaniverseMode CurrentMode { get; private set; }
    public bool UsesDiminishedReality => CurrentMode == PassthroughScaniverseMode.DiminishedReality;

    private void Awake()
    {
        CurrentMode = startMode;
        AutoAssignReferencesIfNeeded();
    }

    private void Start()
    {
        if (applyStartModeOnStart)
            ApplyCurrentMode();
    }

    public void UseDiminishedRealityMode()
    {
        SetMode(PassthroughScaniverseMode.DiminishedReality);
    }

    public void UseScaledScaniverseRoomMode()
    {
        SetMode(PassthroughScaniverseMode.ScaledScaniverseRoom);
    }

    public void ToggleMode()
    {
        SetMode(UsesDiminishedReality
            ? PassthroughScaniverseMode.ScaledScaniverseRoom
            : PassthroughScaniverseMode.DiminishedReality);
    }

    public void SetMode(PassthroughScaniverseMode mode)
    {
        CurrentMode = mode;
        ApplyCurrentMode();
    }

    public void ApplyCurrentMode()
    {
        AutoAssignReferencesIfNeeded();

        bool useDiminishedReality = UsesDiminishedReality;
        if (deskScaleSliderPanel != null)
            deskScaleSliderPanel.SetScaniverseRoomDeformationEnabled(!useDiminishedReality);

        if (useDiminishedReality && handLocalScaniverseOcclusion == null)
            handLocalScaniverseOcclusion = PassthroughRuntimeGuard.EnsureHandLocalScaniverseOcclusion();

        if (handLocalScaniverseOcclusion == null)
            return;

        if (useDiminishedReality)
        {
            handLocalScaniverseOcclusion.hideOriginalScaniverseRenderers = true;
            handLocalScaniverseOcclusion.enabled = true;
            if (rebuildDiminishedRealityOverlayWhenEnabled && handLocalScaniverseOcclusion.isActiveAndEnabled)
                handLocalScaniverseOcclusion.RebuildOverlay();
        }
        else
        {
            handLocalScaniverseOcclusion.enabled = false;
        }
    }

    private void AutoAssignReferencesIfNeeded()
    {
        if (!autoFindReferences)
            return;

        if (deskScaleSliderPanel == null)
            deskScaleSliderPanel = FindAnyObjectByType<DeskScaleSliderPanel>();
        if (handLocalScaniverseOcclusion == null)
            handLocalScaniverseOcclusion = FindAnyObjectByType<HandLocalScaniverseOcclusion>();
    }
}
