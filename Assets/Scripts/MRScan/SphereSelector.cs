using UnityEngine;

public class SphereSelector : MonoBehaviour
{
    [Header("Ray")]
    public Transform rayOrigin;
    public float rayLength = 5f;
    public LayerMask hitMask = ~0;

    [Header("Sphere Visual")]
    public Transform sphereVisual; // a transparent sphere prefab/mesh
    public float radius = 0.2f;

    public bool IsConfirmed { get; private set; }
    public Vector3 CenterWorld { get; private set; }
    public float Radius => radius;

    void Update()
    {
        if (IsConfirmed) return;

        // 1) Raycast -> center
        if (Physics.Raycast(rayOrigin.position, rayOrigin.forward, out var hit, rayLength, hitMask))
        {
            CenterWorld = hit.point;
        }
        else
        {
            CenterWorld = rayOrigin.position + rayOrigin.forward * 1.0f;
        }

        // 2) Radius adjust (example: keyboard / controller axis)
        float delta = 0f;
        if (Input.GetKey(KeyCode.UpArrow)) delta += 0.2f * Time.deltaTime;
        if (Input.GetKey(KeyCode.DownArrow)) delta -= 0.2f * Time.deltaTime;
        radius = Mathf.Clamp(radius + delta, 0.05f, 2.0f);

        // 3) Confirm (example: Enter)
        if (Input.GetKeyDown(KeyCode.Return))
        {
            IsConfirmed = true;
        }

        // update visual
        if (sphereVisual != null)
        {
            sphereVisual.position = CenterWorld;
            sphereVisual.localScale = Vector3.one * (radius * 2f); // diameter scale
        }
    }

    public void ResetSelection()
    {
        IsConfirmed = false;
    }
}
