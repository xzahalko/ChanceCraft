using System.Collections;
using UnityEngine;

// Minimal helper to schedule panel refresh actions on the next frame.
// Keeps the same API your code expects: UIRemoteRefresher.Instance.RefreshNextFrame(GameObject).
public class UIRemoteRefresher : MonoBehaviour
{
    private static UIRemoteRefresher _instance;
    public static UIRemoteRefresher Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<UIRemoteRefresher>();
                if (_instance == null)
                {
                    var go = new GameObject("UIRemoteRefresher");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<UIRemoteRefresher>();
                }
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(this.gameObject);
        }
        else if (_instance != this)
        {
            Destroy(this.gameObject);
        }
    }

    // Schedule a best-effort refresh of the provided panel on the next frame.
    // The implementation is intentionally conservative: it toggles the target GameObject
    // off/on (if available) and forces canvas updates. This mirrors common UI-refresh tricks.
    public void RefreshNextFrame(GameObject panel, int additionalFramesToWait = 0)
    {
        StartCoroutine(RefreshRoutine(panel, Mathf.Max(0, additionalFramesToWait)));
    }

    private IEnumerator RefreshRoutine(GameObject panel, int additionalFrames)
    {
        // Wait one frame (allow the game's immediate UI updates to complete)
        yield return null;

        // Optionally wait more frames
        for (int i = 0; i < additionalFrames; i++)
            yield return null;

        // All yields done above — now perform non-yielding operations inside try/catch.
        try
        {
            if (panel != null)
            {
                // Best-effort toggle: disable then enable without yielding between to avoid yield-in-try issues.
                // Toggling without an extra frame delay is usually sufficient to force Unity to rebuild UI.
                try
                {
                    bool active = panel.activeSelf;
                    if (active) panel.SetActive(false);
                    if (active) panel.SetActive(true);
                }
                catch { /* ignore per-game specifics */ }
            }

            // Force Unity to rebuild canvas layout
            try { Canvas.ForceUpdateCanvases(); } catch { }

            // Attempt to call the high-level refresh helper (safe if present)
            try
            {
                ChanceCraftUIRefreshUsage.RefreshCraftingUiAfterChange();
            }
            catch { /* it's okay if the helper isn't available or throws */ }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[ChanceCraft] UIRemoteRefresher.RefreshRoutine exception: {ex}");
        }

        yield break;
    }
}