using System.IO;
using UnityEngine;

public class ResultPlacer : MonoBehaviour
{
    [Header("Spawn")]
    public GameObject placeholderPrefab; // e.g., a simple mesh
    public Transform parent;

    public string lastSavedPath;

    public GameObject PlacePlaceholder(Vector3 centerWorld, float targetRadius)
    {
        if (placeholderPrefab == null) return null;

        var go = Instantiate(placeholderPrefab, centerWorld, Quaternion.identity, parent);
        go.transform.localScale = Vector3.one * (targetRadius * 2f);
        return go;
    }

    public void SavePly(byte[] plyBytes)
    {
        string dir = Application.persistentDataPath;
        string path = Path.Combine(dir, "result.ply");
        File.WriteAllBytes(path, plyBytes);
        lastSavedPath = path;
        Debug.Log($"Saved PLY: {path}");
    }
}
