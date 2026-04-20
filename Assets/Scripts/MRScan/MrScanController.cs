using System.Collections;
using UnityEngine;

public class MrScanController : MonoBehaviour
{
    [Header("Selector (assign ONE)")]
    [Tooltip("旧: キー/コントローラ等で球を確定するタイプ。使うならこっち。")]
    [SerializeField] private SphereSelector sphereSelector;

    [Tooltip("新: 手ピンチで球中心/半径/確定/Scanを扱うタイプ。使うならこっち。")]
    [SerializeField] private HandSphereSelector handSphereSelector;

    [Header("Pipeline")]
    [SerializeField] private PassthroughSnapshotProvider_RightCam_V2 snapshotProviderV2;
    [SerializeField] private PcInferenceClient pcClient;
    [SerializeField] private ResultPlacer resultPlacer;

    [Header("Optional fallback (only used when SphereSelector is used)")]
    [SerializeField] private bool enableKeyboardFallback = false;
    [SerializeField] private KeyCode startScanKey = KeyCode.Space;

    private bool busy;

    void Update()
    {
        if (busy) return;

        // ---- Validate refs ----
        if (snapshotProviderV2 == null || pcClient == null || resultPlacer == null)
        {
            Debug.LogError("MrScanController: missing pipeline refs (snapshotProviderV2/pcClient/resultPlacer).");
            return;
        }

        // ---- Choose selector ----
        bool useHand = (handSphereSelector != null);
        bool useLegacy = (!useHand && sphereSelector != null);

        if (!useHand && !useLegacy)
        {
            Debug.LogError("MrScanController: assign either handSphereSelector or sphereSelector.");
            return;
        }

        // ---- Check confirm + scan trigger ----
        bool confirmed;
        bool scanRequested;

        if (useHand)
        {
            confirmed = handSphereSelector.IsConfirmed;
            scanRequested = handSphereSelector.ScanRequestedThisFrame; // ← 手でScan（長押しピンチ等）
        }
        else
        {
            confirmed = sphereSelector.IsConfirmed;
            scanRequested = enableKeyboardFallback && Input.GetKeyDown(startScanKey);
        }

        if (!confirmed)
        {
            // 連打ログが邪魔ならコメントアウトしてOK
            // Debug.Log("MrScanController: sphere not confirmed");
            return;
        }

        if (!scanRequested) return;

        StartCoroutine(Flow(useHand));
    }

    private IEnumerator Flow(bool useHand)
    {
        busy = true;

        Vector3 Cw = useHand ? handSphereSelector.CenterWorld : sphereSelector.CenterWorld;
        float R = useHand ? handSphereSelector.Radius : sphereSelector.Radius;

        // ---- Capture passthrough (right eye) ----
        bool done = false;
        PassthroughSnapshotProvider_RightCam_V2.CameraSnapshot snap = default;
        string err = null;

        snapshotProviderV2.CaptureRightEyeJpegAsync(
            Cw, R,
            s => { snap = s; done = true; },
            e => { err = e; done = true; }
        );

        while (!done) yield return null;
        if (!string.IsNullOrEmpty(err))
        {
            Debug.LogError(err);
            busy = false;
            yield break;
        }

        // ROI後画像の中心をpoint promptにする（球で囲ってる想定）
        float u = snap.width * 0.5f;
        float v = snap.height * 0.5f;

        ScanPayload payload = new ScanPayload
        {
            prompt = new ScanPayload.Prompt { type = "point", u = u, v = v },
            sphere_world = new ScanPayload.SphereWorld { cx = Cw.x, cy = Cw.y, cz = Cw.z, r = R },
            camera = new ScanPayload.CameraInfo
            {
                fx = snap.fx,
                fy = snap.fy,
                cx = snap.cx,
                cy = snap.cy,
                T_wc = new ScanPayload.Pose
                {
                    px = snap.camPosWorld.x,
                    py = snap.camPosWorld.y,
                    pz = snap.camPosWorld.z,
                    qx = snap.camRotWorld.x,
                    qy = snap.camRotWorld.y,
                    qz = snap.camRotWorld.z,
                    qw = snap.camRotWorld.w
                }
            }
        };

        string json = JsonUtility.ToJson(payload);

        // ---- Send to PC ----
        byte[] received = null;
        string netErr = null;

        yield return pcClient.PostInfer(
            snap.jpegBytes,
            json,
            b => received = b,
            e => netErr = e
        );

        if (!string.IsNullOrEmpty(netErr))
        {
            Debug.LogError(netErr);
            busy = false;
            yield break;
        }

        if (received == null || received.Length == 0)
        {
            Debug.LogError("MrScanController: empty result");
            busy = false;
            yield break;
        }

        // ---- Save & place ----
        resultPlacer.SavePly(received);
        resultPlacer.PlacePlaceholder(Cw, R);

        busy = false;
    }
}
