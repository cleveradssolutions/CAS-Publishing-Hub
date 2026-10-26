# CAS Publishing Hub

One-click Unity installer for the **CAS publishing SDK**. It scans your project, migrates
legacy installs to UPM, registers the required scoped registries, installs and updates the
SDK components (CAS, Tenjin, Firebase Analytics, …) and keeps everything up to date — **without
you ever editing `Packages/manifest.json` by hand**.

- **Editor code, plus a small runtime.** What reaches your player build: `PublishingSettings`
  — the asset that can hold a Tenjin SDK key, which your game hands to CAS with
  `.WithPublishingSettings()` (the Hub no longer asks for it: CAS takes the key from the CAS
  dashboard) — and, only when Unity IAP is in the project,
  `CASHub.ReportPurchase(product, order)`. Everything else is Editor-only.
- **Agent mode.** An AI coding agent can do the whole integration without the Editor window —
  see `AGENTS.md` in the package.
- **Unity 2022.3+.**
- Driven by a separate data package, `ai.cas.installer.metadata`, so new packages and
  migration rules reach you in the background without an installer update.

---

## 🤖 Install with an AI agent — copy, paste, done

Paste this into your coding agent (Claude Code, Cursor, Codex, …) opened in the Unity project.
It installs and configures the whole SDK without the Editor window; the agent asks you for the
CAS ID and the Firebase config on the way.

```text
Integrate the CAS publishing SDK into this Unity project as an AI agent, without opening the Unity Editor.
1. Make sure no Unity Editor has this project open (Temp/UnityLockfile must not exist) and the git tree is clean; stop and ask me otherwise.
2. In Packages/manifest.json add the scoped registry { "name": "CAS Publishing", "url": "https://npm.cas.ai/", "scopes": ["ai.cas"] } and the dependencies "ai.cas.installer" and "ai.cas.installer.metadata", both at version 1.0.9. Keep every existing entry.
3. Run Unity once in batch mode: <Unity> -batchmode -nographics -quit -projectPath . -logFile -   (Unity = the editor named in ProjectSettings/ProjectVersion.txt).
4. Read Library/PackageCache/ai.cas.installer@*/AGENTS.md and follow it step by step to the end. Before its Configure step, ask me for the platform (Android or iOS), the CAS ID and the Firebase config file — never invent them.
5. Finish with the report AGENTS.md describes.
```

The `1.0.9` above is this page's version. Everything the agent runs is documented in
`AGENTS.md` inside the package; the entry points are
`CAS.Publishing.Installer.Wizard.HeadlessHub.Install` / `Configure` / `Status`.

---

## Installing

Three supported ways in:

- **A — Via UPM (npm scoped registry).** The normal path for projects already on UPM.
- **B — Via Git URL.** No registry to register; Unity pulls the package straight from the
  public GitHub mirror. Good for a quick try or CI.
- **C — Via the bootstrap `.unitypackage`.** Zero-config onboarding for legacy projects.

Whichever you pick, on first run the installer pulls its metadata catalog and opens the setup
wizard automatically — you never edit `manifest.json` for the SDK components themselves.

> You only ever register the `ai.cas` scope (route A) to get the installer
> itself. The extra scopes for CAS / Tenjin / Firebase (`com.cleversolutions`,
> `com.tenjin`, `com.google`) are added **for you** by the installer when you install
> those components.

### A. Via UPM (npm scoped registry)

1. **Add the scoped registry.** `Edit → Project Settings → Package Manager → Scoped Registries → +`

   | Field | Value |
   |---|---|
   | Name | `CAS Publishing` |
   | URL | `https://npm.cas.ai/` |
   | Scope(s) | `ai.cas` |

   Click **Save / Apply**.

2. **Add the package.** `Window → Package Manager → +  → Add package by name…`

   ```
   ai.cas.installer
   ```

   Leave the version field empty to get the latest (or type a version, e.g. `1.0.9`).

Or edit `Packages/manifest.json` directly:

```jsonc
{
  "scopedRegistries": [
    {
      "name": "CAS Publishing",
      "url": "https://npm.cas.ai/",
      "scopes": [ "ai.cas" ]
    }
  ],
  "dependencies": {
    "ai.cas.installer": "1.0.9"
  }
}
```

### B. Via Git URL

No scoped registry needed. `Window → Package Manager → +  → Add package from git URL…`:

```
https://github.com/cleveradssolutions/CAS-Publishing-Hub.git?path=/installer
```

Omit the suffix to track the latest release, or pin one with `#1.0.9`. Git installs are
**not** auto-updated by the **About** tab's self-update — to move to a newer release, change
the `#<version>` tag.

### C. Via the bootstrap `.unitypackage` (zero-config)

Import the tiny `cas-publishing-bootstrap.unitypackage` you received
(`Assets → Import Package → Custom Package…`). On import it writes the scoped registry and
the installer dependency into `manifest.json`, then the installer takes over and opens the
wizard. Once everything is in place the bootstrap **deletes itself** — nothing is left behind.

---

## Using the installer

Open the window any time from the menu:

```
CAS Hub → Open Hub
```

### First run

A brand-new project walks through a short intro flow:

1. **Install** — a component picker (CAS SDK, Tenjin SDK, Firebase Analytics) with live
   install state and an **Install** button that installs everything not already present,
   surviving the domain reloads Unity triggers between package installs.
2. **Configure** — a readiness table for the components you've installed, scoped to the
   active build target (Android / iOS). Cells are clickable: CAS and Tenjin cells open CAS.AI's
   own native settings window, Firebase cells locate the config file. **Continue** unlocks once
   the active platform is fully configured.
3. **Done** — a checklist confirming what's ready.

Passed stepper steps are clickable to navigate back; after the intro, a **↺ Setup Wizard**
button on the right of the tab bar returns you to the step-by-step flow at any time.

### Everyday tabs

| Tab | What it does |
|---|---|
| **Components** | Every SDK component with live status. Row actions: **Install** / **Update** / **Connect to Hub** (migrate a legacy or manual install to UPM) / **Fix** / **Remove SDK**. Statuses refresh automatically after every action, or on demand via **Refresh**. |
| **Configuration** | The same per-platform readiness table as in the intro, always reachable. |
| **About** | Shows the installed hub version, checks the registry for a newer one, and **self-updates** in place. A red dot on the tab means an update is available. |

The metadata catalog (`ai.cas.installer.metadata`) updates **silently in the background** on
Editor load, so new packages and migration rules show up without any action from you.

---

**Full user guide:** [`Documentation~/user-guide.md`](https://github.com/cleveradssolutions/CAS-Publishing-Hub/blob/main/installer/Documentation~/user-guide.md) —
installation routes in detail, the setup wizard step by step, migration of existing
projects, and troubleshooting.
