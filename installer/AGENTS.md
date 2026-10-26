# CAS Publishing Hub — integration guide for AI coding agents

You are an AI coding agent (Claude Code, Cursor, Codex, …) working in a Unity project. This file
tells you how to integrate the CAS publishing SDK **without opening the Unity Editor window and
without anyone clicking through the Hub's wizard**. The result is the same as the wizard's: the
packages installed and configured, an ads manager in the code, purchases reported, ads placed
where the game needs them.

Everything below runs Unity in batch mode and drives the Hub's own pipeline through three
`-executeMethod` entry points. The Hub deliberately does **nothing** on its own in batch mode
(that is a safety rule, not an omission) — only the commands in this guide make it act.

Work in this order. Stop and ask the human whenever this guide says so.

---

## 0. Before you start

1. **The Editor must be closed** on this project. `Temp/UnityLockfile` exists → the Editor is
   open → ask the human to close it. Two Unity processes on one project corrupt `Library/`.
2. **Version control.** Run `git status`. If the project is not a git repository, or has
   uncommitted changes, tell the human and ask whether to commit first. The Hub refuses to delete
   legacy files that git could not restore — a clean tree is what makes migrations safe.
3. **Find the Unity binary** the project uses: `ProjectSettings/ProjectVersion.txt` names the
   version; Unity Hub installs it at
   `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe` (Windows),
   `/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity` (macOS),
   `~/Unity/Hub/Editor/<version>/Editor/Unity` (Linux). Below it is called `$UNITY`.
4. **Collect from the human, do not invent:**
   - the target platform for this pass (`Android` or `iOS`; one platform per pass),
   - the **CAS ID** — the Android package name (`com.company.game`) or the iOS App Store id
     (digits only),
   - the **Firebase config file** (`google-services.json` for Android,
     `GoogleService-Info.plist` for iOS), if the game uses Firebase Analytics,
   - which ad formats the game will use (default: banner, interstitial, rewarded).

   Missing values can be filled in later by re-running step 3; the Hub does not block on them.

Every Unity command in this guide has this shape — keep all five flags:

```
$UNITY -batchmode -nographics -quit -projectPath "<project root>" -buildTarget <Android|iOS> \
       -logFile - -executeMethod <method> [extra args]
```

`-logFile -` prints the Editor log to stdout. Each Hub command also writes
`Library/CASHub/report.json` and echoes it on a log line starting with `[CAS Hub] REPORT `;
the process exit code is `0` on success and `1` on failure. **Read the report after every run.**
A run takes 1–5 minutes (package resolution, script compilation); wait for it.

If a run fails with C# compile errors that are not in `Packages/`, the game's own code does not
compile — fix that first (or ask the human), the Hub cannot run on a project that does not compile.

---

## 1. Bootstrap — put the Hub into the project

Add the Hub's scoped registry and its two packages (installer + metadata catalog) to
`Packages/manifest.json`. The exact registry name / URL / scope and the package ids are in the
package README (section *Installing → Edit manifest.json*) and, once the package is present, in
`Editor/Common/EditionInfo.cs`. Add the metadata package next to the installer — in batch mode the
Hub does not fetch its catalog by itself:

```jsonc
{
  "scopedRegistries": [
    { "name": "<RegistryName>", "url": "<RegistryUrl>", "scopes": [ "<RegistryScope>" ] }
  ],
  "dependencies": {
    "<InstallerPackageId>": "<latest version>",
    "<MetadataPackageId>": "<same version>"
  }
}
```

Keep every existing entry in the manifest; merge, do not overwrite. Skip this step when both
packages are already in `dependencies`.

Then run Unity once with no `-executeMethod` so it resolves the packages:

```
$UNITY -batchmode -nographics -quit -projectPath "<project root>" -logFile -
```

`Library/PackageCache/<InstallerPackageId>@…` exists afterwards → continue.

---

## 2. Install — the SDK components

```
$UNITY -batchmode -nographics -quit -projectPath "<project root>" -buildTarget <platform> \
       -logFile - -executeMethod CAS.Publishing.Installer.Wizard.HeadlessHub.Install
```

One run = one Scan → Plan → Apply pass over the default component set (CAS SDK, Firebase
Analytics, EDM4U, the adapters the catalog lists). It writes `Packages/manifest.json`; Unity
resolves the new packages on its **next** launch. So loop:

- `state: "resolving"` → run the same command again.
- `state: "installed"` → done; go to step 3.
- `state: "failed"` → read `messages`. Typical causes:
  - the metadata catalog is missing → step 1 was incomplete;
  - `refusing to delete` on a path → a legacy install (loose SDK files under `Assets/`) is
    untracked or modified, so the Hub will not delete it. **Do not delete those files yourself.** Show the
    human the paths from the report and ask them to commit or restore the files, then re-run;
  - a registry or network error → report it to the human verbatim.

Cap the loop at 6 runs; if it still says `resolving`, stop and show the human the last report.

The report also lists `actions` (what changed in the manifest) — keep them for your final summary.

---

## 3. Configure — CAS ID, ad formats, Firebase

Write a config file (anywhere; e.g. `Library/CASHub/config.json`):

```json
{
  "platform": "Android",
  "casId": "com.company.game",
  "adFormats": ["banner", "interstitial", "rewarded"],
  "audience": "notChildren",
  "firebaseConfig": "path/to/google-services.json"
}
```

| Key | Required | Notes |
|---|---|---|
| `platform` | no | `Android` / `iOS`; default: the `-buildTarget`. Pass the same value to `-buildTarget`. |
| `casId` | first pass | Validated per platform. Omit on later passes to keep the stored one. |
| `adFormats` | no | Any of `banner`, `interstitial`, `rewarded`, `appOpen`. Default: the first three. |
| `audience` | no | `mixed` / `children` / `notChildren`. Default: keep what the project has. |
| `keepExistingSolution` | no | `true` keeps a project's own Optimal/Families mediation solution instead of switching it to Publishing. Default `false`. |
| `firebaseConfig` | no | Absolute or project-relative path; validated and copied into `Assets/`. |

```
$UNITY -batchmode -nographics -quit -projectPath "<project root>" -buildTarget <platform> \
       -logFile - -executeMethod CAS.Publishing.Installer.Wizard.HeadlessHub.Configure \
       -casHubConfig Library/CASHub/config.json
```

This goes through the same writers as the wizard's Placements / Firebase steps: the CAS
settings asset, the Publishing + Tenjin MMP mediation solutions (their EDM4U dependency files),
the Firebase config in `Assets/`. `state: "configured"` → continue.
`failed` → the message names the bad value; fix the config and re-run. Run once per platform the
game ships on.

---

## 4. Verify

```
$UNITY -batchmode -nographics -quit -projectPath "<project root>" -buildTarget <platform> \
       -logFile - -executeMethod CAS.Publishing.Installer.Wizard.HeadlessHub.Status
```

`ready: true` is the same green state the wizard's Configuration screen shows. If not:
`pendingInstall` lists what step 2 would still do, `configuration[].cells[]` shows which value is
`Missing` / `NotConfigured`, `androidTemplatesMissing` names build templates step 2 creates. Go
back to the step that owns it. A `settingsAssetField` cell is the CAS ID; `assetFile` cells are the Firebase config.

---

## 5. Ads manager in the code

First look for one. Search the game's C# (not `Packages/`, not `Library/`) for:
`MobileAds.BuildManager`, `IMediationManager`, `CAS.`, `AdsManager`, `AdManager`, `AdService`,
`ShowInterstitial`, `ShowRewarded`, and other ad SDKs (`MaxSdk`, `IronSource`, `LevelPlay`,
`GoogleMobileAds`, `Advertisement.Initialize`).

- **A CAS manager exists** → leave it. Make sure its builder chain calls
  `.WithPublishingSettings()` (namespace `CAS.Publishing.Installer.Runtime`) before `.Initialize()` — that
  is what hands a stored Tenjin key to CAS (without one CAS takes it from the CAS dashboard).
  Add the call if it is missing; nothing else.
- **Another ad SDK's manager exists** → do not replace it. Report to the human that the project
  has its own ads layer and ask how they want to proceed.
- **Nothing** → copy the template from this package,
  `Samples~/AdsManager/AdsManager.cs`, into the game — `Assets/Scripts/Ads/AdsManager.cs`, or
  next to the game's other managers if it has a folder for them. It needs no scene setup: it
  bootstraps itself before the first scene loads and initializes CAS with the Hub's settings.
  Edit only the constants at the top of the file (banner on start, size, position). Its API:

  ```csharp
  AdsManager.ShowInterstitial("level_complete");            // bool: shown or not
  AdsManager.ShowRewarded("double_coins", earned => …);     // callback fires exactly once
  AdsManager.ShowBanner();  AdsManager.HideBanner();
  AdsManager.IsRewardedReady                                // enable/disable the button
  ```

### Purchases

If the project uses **Unity IAP** (`com.unity.purchasing` in the manifest), report every
purchase to CAS with the one-liner instead of hand-written receipt parsing — CAS forwards it to
Tenjin, nothing else to call:

```csharp
using CAS.Publishing.Installer.Runtime;
// Unity IAP 5, in the purchase-pending callback, before confirming the order:
CASHub.ReportPurchase(product, pendingOrder);
// Legacy IStoreListener.ProcessPurchase / Unity IAP 4:
CASHub.ReportPurchase(args.purchasedProduct);
```

Find the existing purchase callback and add the call there; do not restructure the store code.
Another payment system → leave it alone (the human can wire `CAS.PurchaseInfo` by hand).

---

## 6. Placing ads in the game (only when the human asks)

Do this only on an explicit request such as "add ads to the game". Then:

1. **Map the game loop first.** Find the natural breaks: level / round / match complete, game
   over, return to the main menu, pause → resume, a "continue?" prompt, shop, reward screens.
   Search for method and event names like `LevelComplete`, `OnWin`, `OnLose`, `GameOver`,
   `Restart`, `LoadNextLevel`, `ShowResults`, `OnContinue`, `Revive`, `DoubleReward`,
   `ClaimReward`, `FreeCoins`, scene loads and UI button handlers.
2. **Propose before editing.** Present the human a short table — file, method, ad type,
   placement name — and get a yes. Placement rules:
   - **Interstitial**: after a natural break is over (level complete → results shown → *then*
     the ad, before the next level loads). Never on the very first launch, never in the middle
     of gameplay, never in a tutorial, never twice in a row with a rewarded ad. One call per
     break; CAS itself enforces the interval between interstitials.
   - **Rewarded**: only behind a button the player presses for an optional benefit (revive,
     double reward, free currency, skip a wait). Grant the benefit in the callback, only when
     `earned` is true. Disable or hide the button when `AdsManager.IsRewardedReady` is false.
   - **Banner**: on menu / idle screens; hide it when gameplay starts and on every full-screen
     UI that would overlap it.
   - Placement names are short snake_case words that describe the spot (`level_complete`,
     `revive`, `main_menu`); they show up in the CAS dashboard.
3. **Edit minimally.** One line per placement, next to the existing logic; no refactoring of
   the game's flow, no new managers besides `AdsManager`.
4. **Compile.** Run Unity once more in batch mode (no `-executeMethod`) and check the log for
   `error CS`. Fix what you introduced.

---

## 7. Report back

Tell the human, in this order: the packages added/updated (from the step 2 reports), the
configuration applied per platform, the files you added or changed in the game, the placements
made (if any), and what still needs a human — store console setup, the other platform's pass,
missing keys, the App Tracking Transparency text on iOS. Suggest `git diff` for review.
