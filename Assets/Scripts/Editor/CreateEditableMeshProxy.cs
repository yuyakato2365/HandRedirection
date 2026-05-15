using UnityEditor;
using UnityEngine;

public static class CreateEditableMeshProxy
{
    [MenuItem("Tools/Desk/Create Editable Mesh Proxy From Selected")]
    private static void CreateProxyFromSelected()
    {
        GameObject selected = Selection.activeGameObject;
        if (selected == null)
        {
            EditorUtility.DisplayDialog("Create Editable Mesh Proxy", "Hierarchy で mesh オブジェクトを 1 つ選択してください。", "OK");
            return;
        }

        MeshFilter sourceFilter = selected.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = selected.GetComponent<MeshRenderer>();
        if (sourceFilter == null || sourceRenderer == null || sourceFilter.sharedMesh == null)
        {
            EditorUtility.DisplayDialog("Create Editable Mesh Proxy", "選択オブジェクトに MeshFilter / MeshRenderer / Mesh が必要です。", "OK");
            return;
        }

        Transform parent = selected.transform.parent;

        GameObject proxy = new GameObject(selected.name + "_EditableProxy");
        Undo.RegisterCreatedObjectUndo(proxy, "Create Editable Mesh Proxy");

        if (parent != null)
        {
            Undo.SetTransformParent(proxy.transform, parent, "Parent Editable Mesh Proxy");
        }

        proxy.transform.SetPositionAndRotation(selected.transform.position, selected.transform.rotation);
        proxy.transform.localScale = selected.transform.lossyScale;

        MeshFilter proxyFilter = Undo.AddComponent<MeshFilter>(proxy);
        proxyFilter.sharedMesh = sourceFilter.sharedMesh;

        MeshRenderer proxyRenderer = Undo.AddComponent<MeshRenderer>(proxy);
        proxyRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
        proxyRenderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
        proxyRenderer.receiveShadows = sourceRenderer.receiveShadows;
        proxyRenderer.lightProbeUsage = sourceRenderer.lightProbeUsage;
        proxyRenderer.reflectionProbeUsage = sourceRenderer.reflectionProbeUsage;
        proxyRenderer.motionVectorGenerationMode = sourceRenderer.motionVectorGenerationMode;
        proxyRenderer.allowOcclusionWhenDynamic = sourceRenderer.allowOcclusionWhenDynamic;
        proxyRenderer.probeAnchor = sourceRenderer.probeAnchor;
        proxyRenderer.renderingLayerMask = sourceRenderer.renderingLayerMask;
        proxyRenderer.sortingLayerID = sourceRenderer.sortingLayerID;
        proxyRenderer.sortingOrder = sourceRenderer.sortingOrder;

        GameObject visualOffset = new GameObject(selected.name + "_VisualOffset");
        Undo.RegisterCreatedObjectUndo(visualOffset, "Create Editable Mesh Visual Offset");
        if (parent != null)
        {
            Undo.SetTransformParent(visualOffset.transform, parent, "Parent Editable Mesh Visual Offset");
        }

        visualOffset.transform.SetPositionAndRotation(selected.transform.position, selected.transform.rotation);
        visualOffset.transform.localScale = Vector3.one;
        Undo.SetTransformParent(proxy.transform, visualOffset.transform, "Move Editable Mesh Proxy Under Visual Offset");
        proxy.transform.localPosition = Vector3.zero;
        proxy.transform.localRotation = Quaternion.identity;
        proxy.transform.localScale = Vector3.one;

        Undo.RecordObject(selected, "Disable Source Mesh");
        selected.SetActive(false);

        Selection.activeGameObject = visualOffset;
        EditorGUIUtility.PingObject(visualOffset);

        EditorUtility.DisplayDialog(
            "Create Editable Mesh Proxy",
            "編集用の通常メッシュを作成しました。\n\n今後は *_VisualOffset を動かして位置合わせしてください。元の imported mesh は無効化しています。",
            "OK");
    }

    [MenuItem("Tools/Desk/Create Editable Mesh Proxy From Selected", true)]
    private static bool ValidateCreateProxyFromSelected()
    {
        GameObject selected = Selection.activeGameObject;
        if (selected == null)
            return false;

        return selected.GetComponent<MeshFilter>() != null && selected.GetComponent<MeshRenderer>() != null;
    }
}
