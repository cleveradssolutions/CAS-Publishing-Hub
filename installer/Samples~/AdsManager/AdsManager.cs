// Ads facade template shipped with the CAS Publishing Hub.
//
// Drop-in: copy this file into the game (e.g. Assets/Scripts/Ads/AdsManager.cs) — nothing to add
// to a scene, it bootstraps itself before the first scene loads. The game then calls:
//
//     AdsManager.ShowInterstitial("level_complete");
//     AdsManager.ShowRewarded("double_reward", earned => { if (earned) GiveDoubleReward(); });
//     AdsManager.ShowBanner();  /  AdsManager.HideBanner();
//
// Frequency capping (the interval between interstitials) and ad loading are CAS's job — they are
// configured in the CAS settings asset the Hub wrote, not here. Rewarded ads are only ever shown
// when the player opted in (pressed a button): never call ShowRewarded from a timer or a scene load.
using System;
using CAS;
using CAS.Publishing.Installer.Runtime; // WithPublishingSettings(): hands the Tenjin SDK key to CAS
using UnityEngine;

public sealed class AdsManager : MonoBehaviour
{
    // ── Configuration (edit here; no scene object to set up) ───────────────────────────────
    private const bool ShowBannerOnStart = false;
    private const AdSize BannerSize = AdSize.AdaptiveBanner;
    private const AdPosition BannerPosition = AdPosition.BottomCenter;
    private const string LogPrefix = "[Ads]";

    // ── State ──────────────────────────────────────────────────────────────────────────────
    private static AdsManager _instance;
    private IMediationManager _manager;
    private IAdView _banner;
    private Action<bool> _rewardCallback;
    private bool _rewardEarned;

    /// <summary>True once CAS finished initializing; ads cannot show before that.</summary>
    public static bool IsInitialized => _instance != null && _instance._manager != null;
    public static bool IsInterstitialReady => IsInitialized && _instance._manager.IsReadyAd(AdType.Interstitial);
    public static bool IsRewardedReady => IsInitialized && _instance._manager.IsReadyAd(AdType.Rewarded);

    // ── Bootstrap ──────────────────────────────────────────────────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null) return;
        var go = new GameObject(nameof(AdsManager));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<AdsManager>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;

        MobileAds.BuildManager()
            .WithPublishingSettings()
            .WithCompletionListener(OnInitialized)
            .Initialize();
    }

    private void OnInitialized(InitialConfiguration config)
    {
        if (config.error != null)
        {
            Debug.LogWarning($"{LogPrefix} CAS failed to initialize: {config.error}");
            return;
        }

        _manager = config.manager;
        _manager.OnRewardedAdCompleted += OnRewardCompleted;
        _manager.OnRewardedAdClosed += OnRewardedClosed;
        _manager.OnRewardedAdFailedToShow += OnRewardedFailedToShow;

        Debug.Log($"{LogPrefix} CAS initialized (test ads: {_manager.isTestAdMode}).");
        if (ShowBannerOnStart) ShowBanner();
    }

    private void OnDestroy()
    {
        if (_manager == null) return;
        _manager.OnRewardedAdCompleted -= OnRewardCompleted;
        _manager.OnRewardedAdClosed -= OnRewardedClosed;
        _manager.OnRewardedAdFailedToShow -= OnRewardedFailedToShow;
    }

    // ── Interstitial ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows an interstitial if one is loaded and CAS's interval since the last one has passed;
    /// otherwise does nothing (and keeps a load in flight). Safe to call at every natural break.
    /// </summary>
    /// <param name="placement">A short name of where in the game this is ("level_complete").</param>
    /// <returns>True when an ad was actually shown.</returns>
    public static bool ShowInterstitial(string placement = null)
    {
        if (!IsInitialized) return false;
        var manager = _instance._manager;
        if (!manager.IsReadyAd(AdType.Interstitial))
        {
            manager.LoadAd(AdType.Interstitial);
            return false;
        }
        manager.ShowAd(AdType.Interstitial, placement);
        return true;
    }

    // ── Rewarded ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a rewarded ad the player asked for. <paramref name="onResult"/> is called exactly
    /// once: true when the reward was earned, false when the ad was skipped, failed or not ready.
    /// </summary>
    public static void ShowRewarded(Action<bool> onResult, string placement = null)
    {
        if (!IsRewardedReady)
        {
            if (IsInitialized) _instance._manager.LoadAd(AdType.Rewarded);
            onResult?.Invoke(false);
            return;
        }

        _instance._rewardCallback = onResult;
        _instance._rewardEarned = false;
        _instance._manager.ShowAd(AdType.Rewarded, placement);
    }

    /// <summary>Same as <see cref="ShowRewarded(Action{bool}, string)"/> with a placement first.</summary>
    public static void ShowRewarded(string placement, Action<bool> onResult) => ShowRewarded(onResult, placement);

    private void OnRewardCompleted() => _rewardEarned = true;

    private void OnRewardedClosed() => FinishReward(_rewardEarned);

    private void OnRewardedFailedToShow(string error)
    {
        Debug.LogWarning($"{LogPrefix} rewarded ad failed to show: {error}");
        FinishReward(false);
    }

    private void FinishReward(bool earned)
    {
        var callback = _rewardCallback;
        _rewardCallback = null;
        _rewardEarned = false;
        callback?.Invoke(earned);
    }

    // ── Banner ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Shows the banner (creating it on first call). Menus and idle screens only.</summary>
    public static void ShowBanner()
    {
        if (!IsInitialized) return;
        var self = _instance;
        if (self._banner == null)
        {
            self._banner = self._manager.GetAdView(BannerSize);
            self._banner.position = BannerPosition;
        }
        self._banner.SetActive(true);
        self._banner.Load();
    }

    public static void HideBanner()
    {
        if (_instance == null || _instance._banner == null) return;
        _instance._banner.SetActive(false);
    }
}
