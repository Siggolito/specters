using UnityEngine;

/// <summary>
/// OPTIONAL. The EdgeRust shader already reads the renderer's bounds on its own, so you
/// only need this to override them - to shrink or grow the band the rust is measured
/// against, or on a rotated mesh where the world AABB is bigger than the object itself.
///
/// Runs in edit mode too, so the rust updates as soon as you drop it on.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Renderer))]
public class EdgeRustBounds : MonoBehaviour
{
    private static readonly int CenterId  = Shader.PropertyToID("_BoundsCenter");
    private static readonly int ExtentsId = Shader.PropertyToID("_BoundsExtents");

    [Tooltip("Shrinks or grows the bounds the rust is measured against. 1 = the real mesh bounds.")]
    [SerializeField] private float boundsScale = 1f;

    [Tooltip("Re-read the bounds every frame. Only needed for meshes that change shape at runtime.")]
    [SerializeField] private bool updateContinuously = false;

    private Renderer cachedRenderer;
    private MaterialPropertyBlock block;

    private void OnEnable() => Apply();
    private void OnValidate() => Apply();

    private void Update()
    {
        if (updateContinuously || !Application.isPlaying) Apply();
    }

    /// <summary>Pushes the current mesh bounds into the material property block.</summary>
    public void Apply()
    {
        if (cachedRenderer == null) cachedRenderer = GetComponent<Renderer>();
        if (cachedRenderer == null) return;

        if (!TryGetLocalBounds(out Bounds bounds)) return;

        block ??= new MaterialPropertyBlock();
        cachedRenderer.GetPropertyBlock(block);

        float s = Mathf.Max(0.0001f, boundsScale);
        block.SetVector(CenterId, bounds.center);
        block.SetVector(ExtentsId, bounds.extents * s);

        cachedRenderer.SetPropertyBlock(block);
    }

    /// <summary>Local-space bounds of whatever this renderer draws.</summary>
    private bool TryGetLocalBounds(out Bounds bounds)
    {
        bounds = default;

        if (TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
        {
            bounds = filter.sharedMesh.bounds;
            return true;
        }

        if (cachedRenderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
        {
            bounds = skinned.sharedMesh.bounds;
            return true;
        }

        return false;
    }

    private void OnDrawGizmosSelected()
    {
        if (!TryGetLocalBounds(out Bounds b)) return;
        Gizmos.color = new Color(1f, 0.55f, 0.15f, 0.8f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(b.center, b.size * Mathf.Max(0.0001f, boundsScale));
    }
}
