using UnityEngine;

public class ExhibitionAudioFeedback : MonoBehaviour
{
    public enum Cue
    {
        PlacementNear,
        PlacementSuccess,
        ScaleUp,
        ScaleDown,
        ScaleStart
    }

    [Header("Audio")]
    public AudioSource audioSource;
    public bool createAudioSourceIfMissing = true;
    public bool useGeneratedFallbackTones = true;
    [Range(0f, 1f)] public float volume = 0.85f;

    [Header("Clips")]
    public AudioClip placementNearClip;
    public AudioClip placementSuccessClip;
    public AudioClip scaleUpClip;
    public AudioClip scaleDownClip;
    public AudioClip scaleStartClip;

    private static ExhibitionAudioFeedback instance;
    private AudioClip generatedNearClip;
    private AudioClip generatedSuccessClip;
    private AudioClip generatedScaleUpClip;
    private AudioClip generatedScaleDownClip;
    private AudioClip generatedScaleStartClip;

    public static ExhibitionAudioFeedback Instance
    {
        get
        {
            if (instance != null)
                return instance;

            instance = FindAnyObjectByType<ExhibitionAudioFeedback>();
            if (instance != null)
                return instance;

            GameObject go = new GameObject("ExhibitionAudioFeedback");
            instance = go.AddComponent<ExhibitionAudioFeedback>();
            DontDestroyOnLoad(go);
            return instance;
        }
    }

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
        }
        else if (instance != this)
        {
            Destroy(this);
            return;
        }

        EnsureAudioSource();
        LoadDefaultClipsFromResources();
    }

    public static void PlayCue(Cue cue)
    {
        Instance.Play(cue);
    }

    public void PlayPlacementNear()
    {
        Play(Cue.PlacementNear);
    }

    public void PlayPlacementSuccess()
    {
        Play(Cue.PlacementSuccess);
    }

    public void PlayScaleUp()
    {
        Play(Cue.ScaleUp);
    }

    public void PlayScaleDown()
    {
        Play(Cue.ScaleDown);
    }

    public void PlayScaleStart()
    {
        Play(Cue.ScaleStart);
    }

    public void Play(Cue cue)
    {
        EnsureAudioSource();
        AudioClip clip = ResolveClip(cue);
        if (audioSource == null || clip == null)
            return;

        audioSource.PlayOneShot(clip, volume);
    }

    private void EnsureAudioSource()
    {
        if (audioSource != null || !createAudioSourceIfMissing)
            return;

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
    }

    private void LoadDefaultClipsFromResources()
    {
        if (placementNearClip == null)
            placementNearClip = Resources.Load<AudioClip>("ExhibitionAudio/placement_near");
        if (placementSuccessClip == null)
            placementSuccessClip = Resources.Load<AudioClip>("ExhibitionAudio/placement_success");
        if (scaleUpClip == null)
            scaleUpClip = Resources.Load<AudioClip>("ExhibitionAudio/scale_up");
        if (scaleDownClip == null)
            scaleDownClip = Resources.Load<AudioClip>("ExhibitionAudio/scale_down");
        if (scaleStartClip == null)
            scaleStartClip = Resources.Load<AudioClip>("ExhibitionAudio/scale_start");
    }

    private AudioClip ResolveClip(Cue cue)
    {
        switch (cue)
        {
            case Cue.PlacementNear:
                return placementNearClip != null ? placementNearClip : GetGeneratedClip(ref generatedNearClip, "near", 660f, 0.12f);
            case Cue.PlacementSuccess:
                return placementSuccessClip != null ? placementSuccessClip : GetGeneratedSuccessClip();
            case Cue.ScaleUp:
                return scaleUpClip != null ? scaleUpClip : GetGeneratedClip(ref generatedScaleUpClip, "scale_up", 880f, 0.10f);
            case Cue.ScaleDown:
                return scaleDownClip != null ? scaleDownClip : GetGeneratedClip(ref generatedScaleDownClip, "scale_down", 440f, 0.10f);
            case Cue.ScaleStart:
                return scaleStartClip != null ? scaleStartClip : GetGeneratedClip(ref generatedScaleStartClip, "scale_start", 550f, 0.08f);
            default:
                return null;
        }
    }

    private AudioClip GetGeneratedSuccessClip()
    {
        if (!useGeneratedFallbackTones)
            return null;
        if (generatedSuccessClip != null)
            return generatedSuccessClip;

        generatedSuccessClip = CreateTwoToneClip("placement_success", 784f, 1046.5f, 0.18f);
        return generatedSuccessClip;
    }

    private AudioClip GetGeneratedClip(ref AudioClip clip, string name, float frequency, float seconds)
    {
        if (!useGeneratedFallbackTones)
            return null;
        if (clip == null)
            clip = CreateToneClip(name, frequency, seconds);
        return clip;
    }

    private static AudioClip CreateToneClip(string name, float frequency, float seconds)
    {
        int sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        int sampleCount = Mathf.Max(1, Mathf.RoundToInt(sampleRate * seconds));
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            float envelope = Mathf.Sin(Mathf.PI * i / Mathf.Max(1, sampleCount - 1));
            samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.35f;
        }

        AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static AudioClip CreateTwoToneClip(string name, float frequencyA, float frequencyB, float seconds)
    {
        int sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        int sampleCount = Mathf.Max(1, Mathf.RoundToInt(sampleRate * seconds));
        float[] samples = new float[sampleCount];
        int split = sampleCount / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            float frequency = i < split ? frequencyA : frequencyB;
            float t = (float)i / sampleRate;
            float envelope = Mathf.Sin(Mathf.PI * i / Mathf.Max(1, sampleCount - 1));
            samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.35f;
        }

        AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
