using UnityEngine;

/// <summary>
/// How far this object can currently be heard, in metres. Whoever moves the object
/// pushes a radius in each frame via <see cref="SetRadius"/>; listeners just read
/// <see cref="Radius"/>. Sounds start instantly and fade out, so a footstep lingers
/// for a moment after you stop rather than snapping to silence.
/// </summary>
[DisallowMultipleComponent]
public class NoiseEmitter : MonoBehaviour
{
    [Tooltip("How quickly a sound fades once you stop making it, in metres per second.")]
    [SerializeField] private float decayPerSecond = 18f;

    /// <summary>Current audible radius in metres. 0 means silent.</summary>
    public float Radius { get; private set; }

    /// <summary>
    /// Report the noise being made right now. Louder than the current value takes
    /// effect immediately; quieter fades down at <see cref="decayPerSecond"/>.
    /// </summary>
    public void SetRadius(float radius)
    {
        radius = Mathf.Max(0f, radius);
        Radius = radius >= Radius
            ? radius
            : Mathf.MoveTowards(Radius, radius, decayPerSecond * Time.deltaTime);
    }

    private void OnDrawGizmosSelected()
    {
        if (Radius <= 0.01f) return;
        Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, Radius);
    }
}
