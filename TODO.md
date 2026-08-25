# LocalCollidables — TODO

> **Read this at the start of every session.** It records work that is in the working tree
> but not in git, so a fresh session doesn't mistake it for finished, committed, or absent.

Last updated: **2026-08-25**

---

## ✅ Done and pushed

- **Unused PanacheUI reference removed** (`ac19fae`, on `master`). The csproj referenced
  `PanacheUI.dll` and `SkiaSharp.dll`, but nothing in `src/` used a PanacheUI type — no
  `using` directive and no fully-qualified name. The reference created a build dependency on
  `devPlugins\PanacheUI` for nothing, so it's gone. Verified with a clean Release build.

  **If this plugin ever grows a PanacheUI-based UI**, do not re-add a bare `<Reference>`.
  Import the shared props instead:

  ```xml
  <PanacheUIMinimumVersion>0.1.6</PanacheUIMinimumVersion>
  <Import Project="$(APPDATA)\XIVLauncher\devPlugins\PanacheUI\PanacheUI.Consumer.props" />
  ```

  Contract: [`../PanacheUI/CONSUMERS.md`](../PanacheUI/CONSUMERS.md).

---

## ⚠️ Uncommitted work in the tree — decide before shipping

**None of this is mine and none of it has been reviewed.** It was already in the working
tree on 2026-08-25 and is untouched.

```
LocalCollidables.json    pluginmaster.json    <-- version bump, looks like in-progress release prep
src/Plugin.cs
```

The two `.json` edits are a version bump, which suggests a release was started and not
finished. **Confirm the version state before triggering CI** — a half-applied bump plus a
push means CI tags a release from a tree that was never meant to ship.

---

## Before the next release

- [ ] Resolve the half-applied version bump in `LocalCollidables.json` / `pluginmaster.json`.
- [ ] Review the uncommitted `src/Plugin.cs` changes — commit or revert.
- [ ] Read [`../BROKEN.md`](../BROKEN.md) before any commit / build / push, per the
      directory-wide rule.
