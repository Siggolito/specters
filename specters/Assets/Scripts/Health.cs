using System;
using UnityEngine;

/// <summary>
/// Minimal damageable pool. Anything that shoots looks for this with
/// GetComponentInParent, so a collider anywhere under the object counts as a hit.
/// </summary>
[DisallowMultipleComponent]
public class Health : MonoBehaviour
{
    [SerializeField] private float maxHealth = 100f;
    [Tooltip("Draw a simple bar in the corner. Intended for the player.")]
    [SerializeField] private bool showBar = false;
    [SerializeField] private Vector2 barPadding = new Vector2(24f, 24f);
    [SerializeField] private Vector2 barSize = new Vector2(200f, 14f);

    public float Max => maxHealth;
    public float Current { get; private set; }
    public float Normalised => maxHealth <= 0f ? 0f : Mathf.Clamp01(Current / maxHealth);
    public bool IsDead => Current <= 0f;

    /// <summary>Fired with the damage amount whenever this takes a hit.</summary>
    public event Action<float> Damaged;

    /// <summary>Fired once, when health first reaches zero.</summary>
    public event Action Died;

    private float lastHitTime = -99f;
    private Texture2D px;
    private GUIStyle labelStyle;

    private void Awake() => Current = maxHealth;

    private void OnDestroy()
    {
        if (px != null) Destroy(px);
    }

    public void TakeDamage(float amount)
    {
        if (amount <= 0f || IsDead) return;

        Current = Mathf.Max(0f, Current - amount);
        lastHitTime = Time.time;
        Damaged?.Invoke(amount);

        if (Current <= 0f) Died?.Invoke();
    }

    public void Heal(float amount)
    {
        if (amount <= 0f || IsDead) return;
        Current = Mathf.Min(maxHealth, Current + amount);
    }

    public void ResetToFull() => Current = maxHealth;

    private void OnGUI()
    {
        if (!showBar) return;

        if (px == null)
        {
            px = new Texture2D(1, 1);
            px.SetPixel(0, 0, Color.white);
            px.Apply();
        }
        if (labelStyle == null)
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };

        float w = barSize.x, h = barSize.y;
        float x = barPadding.x;
        float y = Screen.height - barPadding.y - h;

        // flash white briefly on taking a hit
        float flash = Mathf.Clamp01(1f - (Time.time - lastHitTime) / 0.25f);
        Color fill = Color.Lerp(new Color(0.85f, 0.25f, 0.25f), Color.white, flash);

        Draw(x - 2f, y - 2f, w + 4f, h + 4f, new Color(0f, 0f, 0f, 0.75f));
        Draw(x, y, w, h, new Color(0.12f, 0.05f, 0.05f, 0.85f));
        Draw(x, y, w * Normalised, h, fill);

        labelStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(x, y - 19f, w, 18f),
                  "HEALTH  " + Mathf.CeilToInt(Current) + " / " + Mathf.CeilToInt(maxHealth), labelStyle);
    }

    private void Draw(float x, float y, float w, float h, Color c)
    {
        if (w <= 0f || h <= 0f) return;
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(x, y, w, h), px);
        GUI.color = prev;
    }
}
