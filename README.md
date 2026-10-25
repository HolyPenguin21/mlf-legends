# MLF Legends

Unity 6000.5.4f1 — 2.5D mass-action fighting game.

**Status:** Stage 00 bootstrap in progress. This repository presently contains design documents, not a verified Unity project or a playable build.

## Setup

1. In Unity Hub select **Unity 6000.5.4f1** and the **Universal 3D (URP)** template; create the project as `mlf-legends`.
2. Add the Unity project to this repository, including `Assets/`, `Packages/`, and `ProjectSettings/`. Do not commit generated `Library/`, `Temp/` or `UserSettings/`.
3. Verify Unity opens without errors. Commit `Packages/manifest.json`, `Packages/packages-lock.json` and `ProjectSettings/ProjectVersion.txt` before pinning other package versions.
4. Stage 00 only closes after compile, project smoke test and the documented QA gate. Feature work should be developed on branches and reviewed via PR.

## Documentation

- [Implementation roadmap](Docs/ROADMAP.md)
- [Architecture](Docs/ARCHITECTURE.md)
- [Combat specification](Docs/COMBAT_SPEC.md)
- [Decision log](Docs/DECISIONS.md)
- [QA and verification](Docs/QA.md)

**Scope now:** combat only. First a robust 1v1 vertical slice, then larger groups and eventually 200 vs 200 using **one** authoritative simulation. Campaign design and simulation are deferred.
