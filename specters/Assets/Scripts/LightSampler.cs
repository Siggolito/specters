using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Measures how much light is actually falling on this object, every frame, whether
/// or not anything is displaying it. Night vision reads it for the meter; enemies read
/// it to decide whether the player is visible at all.
///
/// Each light is occlusion-tested with a raycast, so standing behind cover genuinely
/// reads as dark rather than just being far from the lamp.
/// </summary>
[DisallowMultipleComponent]
public class LightSampler : MonoBehaviour
{
    [Tooltip("Where light is measured. Defaults to a child camera, else this transform.")]
    [SerializeField] private Transform samplePoint;
    [Tooltip("Raw light level that reads as fully exposed.")]
    [SerializeField] private float fullBrightLevel = 1.2f;
    [Tooltip("Normalised level below which this object counts as hidden.")]
    [Range(0f, 1f)] [SerializeField] private float hiddenBelow = 0.25f;
    [SerializeField] private float smoothing = 6f;
    [Tooltip("How often the scene is re-scanned for lights, in seconds.")]
    [SerializeField] private float lightRefreshInterval = 0.5f;
    [Tooltip("Which layers can cast this object into shadow.")]
    [SerializeField] private LayerMask occluderMask = ~0;
    [Tooltip("Pushes the occlusion ray clear of the collider you are standing in.")]
    [SerializeField] private float sampleOriginOffset = 0.45f;

    /// <summary>Unnormalised light sum at the sample point.</summary>
    public float RawLevel { get; private set; }

    /// <summary>Light level, 0 (pitch dark) to 1 (fully exposed).</summary>
    public float Level01 { get; private set; }

    /// <summary>True while dark enough to count as hidden.</summary>
    public bool IsHidden => Level01 < hiddenBelow;

    /// <summary>The normalised level below which <see cref="IsHidden"/> turns true.</summary>
    public float HiddenThreshold => hiddenBelow;

    /// <summary>World position light is measured at.</summary>
    public Vector3 SamplePosition => samplePoint != null ? samplePoint.position : transform.position;

    private readonly List<Light> lights = new List<Light>();
    private float refreshTimer;

    private void Awake()
    {
        if (samplePoint == null)
        {
            var cam = GetComponentInChildren<Camera>();
            samplePoint = cam != null ? cam.transform : transform;
        }
        RefreshLights();
        Level01 = Mathf.Clamp01(SampleLightLevel(SamplePosition) / Mathf.Max(0.0001f, fullBrightLevel));
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        refreshTimer -= dt;
        if (refreshTimer <= 0f)
        {
            refreshTimer = Mathf.Max(0.1f, lightRefreshInterval);
            RefreshLights();
        }

        RawLevel = SampleLightLevel(SamplePosition);
        float target = Mathf.Clamp01(RawLevel / Mathf.Max(0.0001f, fullBrightLevel));
        Level01 = Mathf.Lerp(Level01, target, 1f - Mathf.Exp(-smoothing * dt));
    }

    private void RefreshLights()
    {
        lights.Clear();
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.isActiveAndEnabled && l.intensity > 0f)
                lights.Add(l);
    }

    /// <summary>Sums every light reaching the sample point, occlusion-tested per light.</summary>
    private float SampleLightLevel(Vector3 p)
    {
        Color ambientColor = RenderSettings.ambientMode == AmbientMode.Flat
            ? RenderSettings.ambientLight
            : RenderSettings.ambientSkyColor;
        float total = Luminance(ambientColor) * RenderSettings.ambientIntensity;

        for (int i = 0; i < lights.Count; i++)
        {
            Light l = lights[i];
            if (l == null || !l.isActiveAndEnabled) continue;

            if (l.type == LightType.Directional)
            {
                Vector3 dir = -l.transform.forward;
                Vector3 origin = p + dir * sampleOriginOffset;
                if (!Physics.Raycast(origin, dir, 1000f, occluderMask, QueryTriggerInteraction.Ignore))
                    total += l.intensity * Luminance(l.color);
                continue;
            }

            if (l.type != LightType.Point && l.type != LightType.Spot) continue;

            Vector3 toLight = l.transform.position - p;
            float dist = toLight.magnitude;
            if (dist > l.range || dist < 0.0001f) continue;

            Vector3 nDir = toLight / dist;
            float atten = 1f - Mathf.Clamp01(dist / l.range);
            atten *= atten;

            if (l.type == LightType.Spot)
            {
                float angle = Vector3.Angle(l.transform.forward, -nDir);
                float half = l.spotAngle * 0.5f;
                if (angle > half) continue;
                atten *= 1f - Mathf.Clamp01(angle / Mathf.Max(0.0001f, half));
            }

            Vector3 rayStart = p + nDir * sampleOriginOffset;
            float rayLen = dist - sampleOriginOffset - 0.05f;
            if (rayLen > 0f &&
                Physics.Raycast(rayStart, nDir, rayLen, occluderMask, QueryTriggerInteraction.Ignore))
                continue;

            total += l.intensity * Luminance(l.color) * atten;
        }

        return total;
    }

    private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
}
