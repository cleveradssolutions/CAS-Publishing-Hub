# CAS Publishing Hub

Release builds of the CAS Publishing Hub for Unity — the one-click installer for the CAS publishing
SDK — and its package catalog. Each release is a commit on `main` tagged with its version.

| Folder | Package |
|---|---|
| [`installer/`](installer) | `ai.cas.installer` — the Hub itself |
| [`metadata/`](metadata) | `ai.cas.installer.metadata` — the package catalog the Hub reads |

## Install

`Window → Package Manager → + → Add package from git URL…`:

```
https://github.com/cleveradssolutions/CAS-Publishing-Hub.git?path=/installer#1.0.9
```

The Hub adds the catalog itself (`https://github.com/cleveradssolutions/CAS-Publishing-Hub.git?path=/metadata`, from `main`, so catalog updates arrive
without a new Hub release). Git installs don't self-update: to move to a newer release, change the
`#<version>` suffix.

The same packages are on the `https://npm.cas.ai/` registry (scope `ai.cas`).

Full guide: [`installer/Documentation~/user-guide.md`](installer/Documentation~/user-guide.md).
