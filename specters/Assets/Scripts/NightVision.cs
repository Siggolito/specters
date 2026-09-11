using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Toggleable night vision (default key: N) plus a light meter that only shows while
/// the goggles are on, so you can read whether you are actually standing in the dark.
///
/// The effect is a runtime-built URP post-processing Volume — nothing to wire up in
/// the scene. The meter samples the real scene lights at eye height with an occlusion
/// raycast per light, so standing behind cover genuinely reads as dark.
/// </summary>
[DisallowMultipleComponent]
public class NightVision : MonoBehaviour
{
    [Header("Toggle")]
    [SerializeField] private Key toggleKey = Key.N;
    [SerializeField] private bool startEnabled = false;
    [Tooltip("Seconds for the goggles to fade in / out.")]
    [SerializeField] private float warmupSeconds = 0.22f;

    [Header("Look")]
    [SerializeField] private Color tint = new Color(0.42f, 1f, 0.48f);
    [Tooltip("Brightness gain, in stops.")]
    [SerializeField] private float postExposure = 2.6f;
    [Range(-100f, 100f)] [SerializeField] private float contrast = 20f;
    [Range(-100f, 100f)] [SerializeField] private float saturation = -60f;
    [Range(0f, 1f)] [SerializeField] private float vignetteIntensity = 0.45f;
    [Range(0f, 1f)] [SerializeField] private float grainIntensity = 0.55f;

    [Header("Light Meter")]
    [SerializeField] private bool showMeter = true;
    [Tooltip("Where the meter reads from. Auto-found on this object if left empty.")]
    [SerializeField] private LightSampler sampler;

    [Header("Meter Placement")]
    [SerializeField] private Vector2 meterPadding = new Vector2(24f, 48f);
    [SerializeField] private Vector2 meterSize = new Vector2(260f, 16f);

    /// <summary>Are the goggles on?</summary>
    public bool Active { get; private set; }

    /// <summary>Light falling on the player, 0 (pitch dark) to 1 (fully exposed).</summary>
    public float LightLevel01 => sampler != null ? sampler.Level01 : 0f;

    /// <summary>True while the player is dark enough to count as hidden.</summary>
    public bool IsHidden => sampler != null && sampler.IsHidden;

    private float HiddenThreshold => sampler != null ? sampler.HiddenThreshold : 0.25f;

    private Camera cam;
    private Volume volume;
    private VolumeProfile profile;
    private ColorAdjustments colorAdjustments;
    private Vignette vignette;
    private FilmGrain filmGrain;

    private float weight;

    private Texture2D pxWhite;
    private GUIStyle labelStyle;

    private void Awake()
    {
        cam = GetComponentInChildren<Camera>();
        if (cam == null) cam = Camera.main;

        if (cam != null)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = true;
        }

        if (sampler == null) sampler = GetComponent<LightSampler>();
        if (sampler == null) sampler = GetComponentInParent<LightSampler>();

        BuildVolume();

        Active = startEnabled;
        weight = Active ? 1f : 0f;
        ApplyWeight();
    }

    private void OnDestroy()
    {
        if (profile != null) Destroy(profile);
        if (pxWhite != null) Destroy(pxWhite);
    }

    private void BuildVolume()
    {
        var host = new GameObject("NightVisionVolume");
        host.transform.SetParent(transform, false);
        host.hideFlags = HideFlags.DontSave;

        volume = host.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f;

        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.hideFlags = HideFlags.DontSave;
        volume.sharedProfile = profile;

        colorAdjustments = profile.Add<ColorAdjustments>();
        colorAdjustments.postExposure.overrideState = true;
        colorAdjustments.contrast.overrideState = true;
        colorAdjustments.colorFilter.overrideState = true;
        colorAdjustments.saturation.overrideState = true;

        vignette = profile.Add<Vignette>();
        vignette.intensity.overrideState = true;
        vignette.smoothness.overrideState = true;
        vignette.color.overrideState = true;
        vignette.color.value = Color.black;
        vignette.smoothness.value = 0.6f;

        filmGrain = profile.Add<FilmGrain>();
        filmGrain.intensity.overrideState = true;
        filmGrain.type.overrideState = true;
        filmGrain.type.value = FilmGrainLookup.Medium1;

        PushLookSettings();
    }

    private void PushLookSettings()
    {
        if (colorAdjustments == null) return;
        colorAdjustments.postExposure.value = postExposure;
        colorAdjustments.contrast.value = contrast;
        colorAdjustments.saturation.value = saturation;
        colorAdjustments.colorFilter.value = tint;
        vignette.intensity.value = vignetteIntensity;
        filmGrain.intensity.value = grainIntensity;
    }

    private void OnValidate()
    {
        if (Application.isPlaying) PushLookSettings();
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        var kb = Keyboard.current;
        if (kb != null && kb[toggleKey].wasPressedThisFrame)
            Active = !Active;

        weight = Mathf.MoveTowards(weight, Active ? 1f : 0f,
                                   warmupSeconds <= 0f ? 1f : dt / warmupSeconds);
        ApplyWeight();
    }

    private void ApplyWeight()
    {
        if (volume == null) return;
        volume.weight = weight;
        volume.enabled = weight > 0.0001f;
    }

    // ---- Meter -----------------------------------------------------------

    private void OnGUI()
    {
        if (!showMeter || weight <= 0.01f) return;

        if (pxWhite == null)
        {
            pxWhite = new Texture2D(1, 1);
            pxWhite.SetPixel(0, 0, Color.white);
            pxWhite.Apply();
        }
        if (labelStyle == null)
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };

        float w = meterSize.x, h = meterSize.y;
        float x = meterPadding.x;
        float y = Screen.height - meterPadding.y - h;
        float a = Mathf.Clamp01(weight);

        Color fill = LightLevel01 < HiddenThreshold ? new Color(0.30f, 0.95f, 0.42f)
                   : LightLevel01 < 0.6f        ? new Color(0.95f, 0.78f, 0.25f)
                                                : new Color(0.95f, 0.32f, 0.28f);

        Bar(x - 2f, y - 2f, w + 4f, h + 4f, new Color(0.05f, 0.12f, 0.06f, 0.85f * a));
        Bar(x, y, w, h, new Color(0f, 0f, 0f, 0.65f * a));
        Bar(x, y, w * LightLevel01, h, new Color(fill.r, fill.g, fill.b, 0.9f * a));
        Bar(x + w * HiddenThreshold - 1f, y - 3f, 2f, h + 6f, new Color(1f, 1f, 1f, 0.7f * a));

        labelStyle.normal.textColor = new Color(0.75f, 1f, 0.8f, a);
        GUI.Label(new Rect(x, y - 20f, w, 18f), "LIGHT", labelStyle);

        labelStyle.normal.textColor = new Color(fill.r, fill.g, fill.b, a);
        string state = LightLevel01 < HiddenThreshold ? "HIDDEN" : "EXPOSED";
        GUI.Label(new Rect(x + w - 110f, y - 20f, 110f, 18f),
                  state + "  " + Mathf.RoundToInt(LightLevel01 * 100f) + "%", labelStyle);
    }

    private void Bar(float x, float y, float w, float h, Color c)
    {
        if (w <= 0f || h <= 0f) return;
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(x, y, w, h), pxWhite);
        GUI.color = prev;
    }
}
