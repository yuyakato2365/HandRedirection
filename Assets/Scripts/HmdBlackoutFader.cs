using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class HmdBlackoutFader : MonoBehaviour
{
    public float fadeOutSeconds = 1.0f;
    public float blackHoldSeconds = 0.12f;
    public float fadeInSeconds = 1.0f;

    private Camera targetCamera;
    private GameObject fadeQuad;
    private Material fadeMaterial;
    private Coroutine fadeRoutine;

    public static HmdBlackoutFader GetOrCreate()
    {
        HmdBlackoutFader existing = FindAnyObjectByType<HmdBlackoutFader>();
        if (existing != null)
            return existing;

        GameObject owner = new GameObject("HMD Blackout Fader");
        DontDestroyOnLoad(owner);
        return owner.AddComponent<HmdBlackoutFader>();
    }

    public void BeginBlackout(Action actionAtBlack)
    {
        EnsureVisual();
        if (fadeRoutine != null)
            StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(BlackoutRoutine(actionAtBlack));
    }

    private IEnumerator BlackoutRoutine(Action actionAtBlack)
    {
        yield return Fade(0f, 1f, fadeOutSeconds);
        SetAlpha(1f);
        actionAtBlack?.Invoke();
        if (blackHoldSeconds > 0f)
            yield return new WaitForSecondsRealtime(blackHoldSeconds);
        yield return Fade(1f, 0f, fadeInSeconds);
        SetAlpha(0f);
        fadeRoutine = null;
    }

    private IEnumerator Fade(float from, float to, float seconds)
    {
        if (seconds <= 0f)
        {
            SetAlpha(to);
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.unscaledDeltaTime;
            SetAlpha(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / seconds)));
            yield return null;
        }
        SetAlpha(to);
    }

    private void EnsureVisual()
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].isActiveAndEnabled)
                {
                    camera = cameras[i];
                    break;
                }
            }
        }
        if (camera == null)
            return;

        if (fadeQuad == null)
        {
            fadeQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            fadeQuad.name = "HMD Blackout Surface";
            Destroy(fadeQuad.GetComponent<Collider>());

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            fadeMaterial = new Material(shader) { name = "HMD Blackout Material" };
            if (fadeMaterial.HasProperty("_Surface")) fadeMaterial.SetFloat("_Surface", 1f);
            if (fadeMaterial.HasProperty("_SrcBlend")) fadeMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (fadeMaterial.HasProperty("_DstBlend")) fadeMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (fadeMaterial.HasProperty("_ZWrite")) fadeMaterial.SetFloat("_ZWrite", 0f);
            if (fadeMaterial.HasProperty("_ZTest")) fadeMaterial.SetFloat("_ZTest", (float)CompareFunction.Always);
            if (fadeMaterial.HasProperty("_Cull")) fadeMaterial.SetFloat("_Cull", (float)CullMode.Off);
            fadeMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            fadeMaterial.EnableKeyword("_ALPHABLEND_ON");
            fadeMaterial.renderQueue = 5000;
            Renderer fadeRenderer = fadeQuad.GetComponent<Renderer>();
            fadeRenderer.sharedMaterial = fadeMaterial;
            fadeRenderer.shadowCastingMode = ShadowCastingMode.Off;
            fadeRenderer.receiveShadows = false;
        }

        targetCamera = camera;
        fadeQuad.transform.SetParent(targetCamera.transform, false);
        float distance = Mathf.Max(targetCamera.nearClipPlane + 0.05f, 0.15f);
        float height = 2f * distance * Mathf.Tan(targetCamera.fieldOfView * Mathf.Deg2Rad * 0.5f) * 1.4f;
        float width = height * Mathf.Max(1f, targetCamera.aspect) * 1.4f;
        fadeQuad.transform.localPosition = new Vector3(0f, 0f, distance);
        fadeQuad.transform.localRotation = Quaternion.identity;
        fadeQuad.transform.localScale = new Vector3(width, height, 1f);
        SetAlpha(0f);
    }

    private void SetAlpha(float alpha)
    {
        if (fadeMaterial == null || fadeQuad == null)
            return;
        Color color = new Color(0f, 0f, 0f, Mathf.Clamp01(alpha));
        if (fadeMaterial.HasProperty("_BaseColor")) fadeMaterial.SetColor("_BaseColor", color);
        if (fadeMaterial.HasProperty("_Color")) fadeMaterial.SetColor("_Color", color);
        fadeQuad.SetActive(alpha > 0.001f);
    }

    private void OnDestroy()
    {
        if (fadeQuad != null) Destroy(fadeQuad);
        if (fadeMaterial != null) Destroy(fadeMaterial);
    }
}
