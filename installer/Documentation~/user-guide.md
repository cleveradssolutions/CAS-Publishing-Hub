# CAS Publishing Hub — User Guide

The CAS Publishing Hub is a one-click Unity installer for the CAS publishing SDK. It scans
your project, installs and updates the SDK components (CAS, Tenjin, Firebase Analytics, …),
migrates legacy installs to UPM, registers the required scoped registries and keeps
everything up to date — without you ever editing `Packages/manifest.json` by hand.

- **Editor code, plus a small runtime.** What reaches your player build: `PublishingSettings` —
  the asset that can hold a Tenjin SDK key, which your game hands to CAS with
  `.WithPublishingSettings()` (the Hub no longer asks for the key — CAS takes it from the CAS
  dashboard) — and, only when Unity IAP is in the project,
  `CASHub.ReportPurchase(product, order)`, the one-line purchase report. Everything else stays
  in the Editor.
- **Agent mode.** An AI coding agent can do the whole integration without the Editor window —
  see `AGENTS.md` in the package and section 5 below.
- **Unity 2022.3 or newer.**
- Requires internet access in the Editor (the Hub talks to the package registry).

---

## 1. Installation

Pick whichever route fits your project. All three end in the same place: the Hub opens its
setup wizard automatically on first launch.

### Route A — UPM scoped registry (recommended)

1. Open `Edit → Project Settings → Package Manager`, expand **Scoped Registries**, press **+**
   and fill in:

   | Field | Value |
   |---|---|
   | Name | `CAS Publishing` |
   | URL | `https://npm.cas.ai/` |
   | Scope(s) | `ai.cas` |

   Press **Save / Apply**.

2. Open `Window → Package Manager`, press **+ → Add package by name…** and enter:

   ```
   ai.cas.installer
   ```

   Leave the version empty to get the latest release.

Alternatively, add both entries straight into `Packages/manifest.json`:

```jsonc
{
  "scopedRegistries": [
    { "name": "CAS Publishing", "url": "https://npm.cas.ai/", "scopes": [ "ai.cas" ] }
  ],
  "dependencies": {
    "ai.cas.installer": "latest"
  }
}
```

> This is the only registry you ever add by hand. The extra scopes needed by individual
> components (`com.cleversolutions`, `com.tenjin`, `com.google`) are registered **for you**
> when you install those components from the Hub.

### Route B — Git URL

No registry setup needed. `Window → Package Manager → + → Add package from git URL…`:

```
https://github.com/cleveradssolutions/CAS-Publishing-Hub.git?path=/installer
```

Append `#<version>` to pin a specific release. Note: git installs do not self-update — to
move to a newer release, change the `#<version>` suffix.

### Route C — Bootstrap `.unitypackage`

If you received `cas-publishing-bootstrap.unitypackage`:

1. `Assets → Import Package → Custom Package…`, select the file, import everything.
2. The bootstrap writes the registry and the Hub dependency into `manifest.json`, Unity
   resolves the package, and the wizard opens.
3. Once the Hub is fully in place, the bootstrap **deletes itself** — nothing is left behind
   in your project.

---

## 2. First launch — the setup wizard

On first run the Hub downloads its component catalog and opens automatically. If it doesn't,
open it from the menu: **CAS Hub → Open Hub**.

The wizard walks you through three steps. Steps you have passed stay clickable in the top
stepper, so you can always go back.

### Step 1 — Install

A component picker listing the SDK components (CAS SDK, Tenjin SDK, Firebase Analytics)
with their live install state and the exact versions that will be installed. Tick what you
need and press **Install**.

The Hub installs the components one at a time. Unity reloads scripts between package
installs — this is normal; the progress screen survives the reloads and continues where it
left off.

### Step 2 — Configure

A readiness table for the components you installed, scoped to your **active build target**:

- On **Android** the table covers Android only; on **iOS** — iOS only.
- On any other build target the screen offers working **Switch to Android** /
  **Switch to iOS** buttons and reopens on the chosen platform after the switch.

Cells are clickable:

- The **CAS ID** cell leads to the **Placements** step (ad formats, Publishing solution,
  Tenjin MMP). The Tenjin key is not asked for: CAS takes it from the app's settings in the
  CAS dashboard. Keep one call in your CAS initialization — it also passes on a key an earlier
  Hub stored in `Assets/Resources/PublishingSettings.asset`:

  ```csharp
  using CAS.Publishing.Installer.Runtime;

  MobileAds.BuildManager()
      .WithPublishingSettings()   // hands a stored Tenjin key to CAS, if any
      .Initialize();
  ```
- **Firebase** cells locate the platform config file — `google-services.json` (Android) or
  `GoogleService-Info.plist` (iOS) — and tell you exactly which file is missing.
- On Android, a banner offers a one-click **Enable** for the gradle/manifest build templates
  when they are missing.

Panels under the table list anything that still needs attention. **Continue** unlocks once
the active platform is fully configured.

### Step 3 — Done

A checklist confirming what's installed and configured, with shortcuts back into the
components list and to [cas.ai](https://cas.ai).

---

## 3. Everyday use

After the intro the window opens on three tabs:

| Tab | What it does |
|---|---|
| **Components** | Every SDK component with live status and version. Row actions: **Install**, **Update**, **Connect to Hub** (adopt a legacy or manually-installed copy into UPM), **Fix** (e.g. a missing scoped registry), **Remove SDK**. Statuses refresh automatically after every action, or on demand via **Refresh**. |
| **Configuration** | The same per-platform readiness table as in the wizard, always reachable. |
| **About** | The installed Hub version. Checks the registry for a newer one and **updates in place** — a red dot on the tab means an update is available. |

Want the guided flow again? The **↺ Setup Wizard** button on the right of the tab bar
returns you to the step-by-step wizard at any time. `Assets → CleverAdsSolutions → Hub
(Restart Intro)` restarts the intro from scratch.

### Migrating an existing project

If your project already contains SDK components installed another way — a legacy
`.unitypackage` under `Assets/`, a manual copy, or a git dependency — the Hub detects them
and offers **Connect to Hub** on the affected rows. The migration removes the legacy files,
adds the proper UPM dependency, and keeps your settings. You'll be shown exactly what is
about to change before anything is touched.

### Updates

- **Hub itself:** the About tab updates it in place (UPM installs; for git installs change
  the `#<version>` suffix instead).
- **Component catalog:** updates silently in the background on Editor load, so new
  components and migration rules appear without any action from you.
- **Components:** rows show **Update** when a newer recommended version is available.

---

## 4. Purchases and the ads code

**Purchases.** With Unity IAP in the project, report every purchase with one call — CAS
forwards it to Tenjin, nothing else to wire:

```csharp
using CAS.Publishing.Installer.Runtime;

CASHub.ReportPurchase(product, pendingOrder);   // Unity IAP 5, before confirming the order
CASHub.ReportPurchase(args.purchasedProduct);   // legacy IStoreListener / Unity IAP 4
```

Google Play, the App Store and Amazon validation data are attached automatically. The method
exists only while both Unity IAP and CAS are installed; on another payment system use
`CAS.PurchaseInfo` directly, as in the CAS docs.

**Ads manager.** If your game has no ads layer yet, import the **Ads Manager template** from
the Package Manager's *Samples* tab (or copy `Samples~/AdsManager/AdsManager.cs`). It needs no
scene setup and gives you `AdsManager.ShowInterstitial(...)`, `AdsManager.ShowRewarded(...)`,
`AdsManager.ShowBanner()` / `HideBanner()`, initialized with the Hub's settings.

---

## 5. Agent mode — integrating with an AI coding agent

`AGENTS.md` in the package is a complete instruction for an AI coding agent (Claude Code,
Cursor, Codex, …) to do everything this guide describes without opening the Editor window:
bootstrap, install, configure, verify, add the ads manager, report purchases and — only when
you ask — place ad calls in your game. Point your agent at the file:

> Read `Library/PackageCache/ai.cas.installer@*/AGENTS.md` and integrate the CAS publishing SDK
> for Android.

Under the hood it runs Unity in batch mode with three entry points
(`CAS.Publishing.Installer.Wizard.HeadlessHub.Install` / `Configure` / `Status`) that reuse the
wizard's own pipeline; each run writes `Library/CASHub/report.json`. The Hub still does nothing
in batch mode on its own — only these commands make it act.

---

## 6. Troubleshooting

**The wizard didn't open / shows "Installing catalog metadata…"**
The Hub is fetching its catalog package. If your first launch happened offline, press the
**Install Metadata** button once you're back online — the state updates immediately, no
Editor restart needed.

**A component row says "Needs registry".**
Press **Fix** on the row — the Hub re-adds the missing scoped registry to your manifest.

**Unity reloads scripts repeatedly during installation.**
Expected. Every package install triggers a domain reload; the Hub's progress screen is
built to survive them and continue.

**The Configuration tab shows a "switch platform" prompt.**
Your active build target is neither Android nor iOS. Use the provided switch buttons or
`File → Build Settings` to select a mobile platform.

**Package Manager flags the package signature (Unity 6.3+).**
All CAS Publishing packages are cryptographically signed. Make sure you are on Unity
6000.3.5f2 or newer — earlier 6.3 builds contain a known bug that reports valid signatures
as invalid.

---

Questions or issues? Visit [cas.ai](https://cas.ai) and contact your CAS account manager.
