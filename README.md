# MLF Legends

Unity **6000.5.4f1** — 2.5D mass-action fighting game, designed for 1v1 through 200 vs 200 combat in the same authoritative ECS simulation.

**Status:** Stage 00 / Unity project bootstrap **IN PROGRESS**. A Unity Universal 3D (URP) template project is already committed. Its Editor compile, smoke scene and automated tests have **not** yet been verified.

## Starting the project

1. Clone https://github.com/HolyPenguin21/mlf-legends.
2. Open the repository root in Unity Hub using **6000.5.4f1**.
3. The project currently references **Universal Render Pipeline 17.5.0** and **Input System 1.19.0**. The package manifest now includes **Entities 6.5.0** and **Entities Graphics 6.5.0**; the lockfile also lists Burst 1.8.29 and Collections 6.5.0. Unity 6000.5 distributes Entities and Entities Graphics as Editor-matched core packages.
4. In Unity Package Manager verify that all packages resolve without errors. Preserve the committed `Packages/manifest.json` and `Packages/packages-lock.json`; core-package versions follow the Editor.
5. Pull the Stage 00 test branch/PR, then run tests from **Window > General > Test Runner** (EditMode followed by PlayMode), or on Windows use `Tools/Run-UnityTests.ps1` with Unity closed. See `Docs/QA.md` for exact commands and evidence requirements. No test is passed until actually executed.

Do not commit generated `Library/`, `Temp/`, `Logs/` or `UserSettings/`. **Do** commit relevant Unity `.meta` files.

## Technical documentation

- [Roadmap and stage gates](Docs/ROADMAP.md)
- [Combat architecture](Docs/ARCHITECTURE.md)
- [Combat specification](Docs/COMBAT_SPEC.md)
- [Accepted decisions](Docs/DECISIONS.md)
- [QA / execution evidence](Docs/QA.md)

## Working rules

Current scope is **combat only**. Complete and validate duel 1v1 first, then 4v4, 16v16, 50v50, finally 200v200. All scales must use the same gameplay rules; no separate duel physics or attack pipeline. Development changes require dependency review, targeted tests, regression validation and explicit QA status.

Campaign aging, inheritance, recruiting and strategy simulation are deferred.
