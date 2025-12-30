using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction.HandGrab;

public class VrWorldGrabDetector_FromHandGrabInteractable : MonoBehaviour
{
    [Header("World follower (あなたのVrWorldTargetsFollower_Linear)")]
    public VrWorldTargetsFollower_Linear follower;

    [Header("Grab sources (HandGrabInteractable たち)")]
    public List<HandGrabInteractable> sources = new List<HandGrabInteractable>();

    [Header("Auto collect (任意)")]
    public bool autoCollectInScene = false;
    public bool includeInactive = false;

    private bool isGrabbing = false;
    private HandGrabInteractor activeInteractor = null;

    private void Awake()
    {
        if (autoCollectInScene)
        {
#if UNITY_2023_1_OR_NEWER
            var found = GameObject.FindObjectsByType<HandGrabInteractable>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            sources = new List<HandGrabInteractable>(found);
#else
            sources = new List<HandGrabInteractable>(FindObjectsOfType<HandGrabInteractable>(includeInactive));
#endif
        }

        sources.RemoveAll(s => s == null);
    }

    private void Update()
    {
        if (follower == null) return;
        if (sources == null || sources.Count == 0) return;

        // いま「どれか1つでも掴まれているか？」と「その掴んでいる手(Interactor)」を取得
        bool anySelected = TryGetAnySelectingInteractor(out HandGrabInteractor interactor);

        if (!anySelected)
        {
            // 掴みが終わった
            if (isGrabbing)
            {
                follower.EndGrab();
                isGrabbing = false;
                activeInteractor = null;
            }
            return;
        }

        // 掴みが始まった
        if (!isGrabbing)
        {
            if (interactor != null)
            {
                follower.BeginGrab(interactor.transform);
                isGrabbing = true;
                activeInteractor = interactor;
            }
            return;
        }

        // 掴み中に「掴んでいる手」が切り替わったら、基準を切り替える（ジャンプを抑えるため End→Begin）
        if (interactor != null && interactor != activeInteractor)
        {
            follower.EndGrab();
            follower.BeginGrab(interactor.transform);
            activeInteractor = interactor;
        }
    }

    private bool TryGetAnySelectingInteractor(out HandGrabInteractor interactor)
    {
        // 優先順位：sourcesの先頭から見つかったもの
        for (int i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            if (src == null) continue;

            // HandGrabInteractable は Interactable の派生で SelectingInteractors を持つ
            // 「空でない」＝そのInteractableが選択中＝掴み中
            foreach (var it in src.SelectingInteractors)
            {
                if (it != null)
                {
                    interactor = it;
                    return true;
                }
            }
        }

        interactor = null;
        return false;
    }
}
