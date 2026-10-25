# MLF Legends — Decision log

## Approved / fixed
- Repository: `HolyPenguin21/mlf-legends`, branch `main`.
- Owner's editor: **Unity 6000.5.4f1**. Use **Universal 3D (URP)** for 3D environment, not 2D template.
- Current work is **only combat**; do not implement or expand campaign design.
- Upgraded LF2/Nekketsu appearance: ~4-head-high expressive 2D sprites in a real 3D scene, 2.5D.
- Orthographic camera at a **fixed angle** over all zoom levels, plus panning and follow.
- ECS/DOTS + Burst-based simulation, specialized GPU sprite rendering, custom collisions and custom navigation. No full per-fighter Animator, Rigidbody or NavMeshAgent simulation.
- One authoritative combat system for a duel and full-scale **200 vs 200** battle. Human controls one fighter; others are AI.
- Attack facing mainly left/right with configurable Z-depth tolerance; attacks can hit multiple enemies.
- Moderate combat tempo, with windup/active/recovery, blocking and counters important.
- **Front-facing guard with stability**, guard break and **timely block advantage** without automated counter.
- Short directional defensive dash bound to **L (guard) + new direction press**, including depth and diagonals, with collision intact.
- All combatants collide. Allies yield somewhat; human fighter can press through allies with slight resistance. Enemies obstruct.
- Basic direct attacks don't hurt allies; skill-generated environmental/physical consequences can affect friend and enemy.
- Defeated fighters do not respawn before battle ends.
- **S.P.E.C.I.A.L.** and anatomical injuries (eyes, arms, legs, head/torso) with functional consequences.
- Prepared magic spells with several type-dependent resources, and environmental effects, postponed until later stages.
- Implementation roadmap 00–13, quality gates, maintainable clear ownership and systematic dependency checks.

## Deferred but previously discussed — campaign
- Strategic campaign is turn-based with months or years per turn.
- Individual character age, illness, injury, potential growth/decline, inheritance, childbirth and recruitment.
- Player controls one campaign character; after death player chooses ending campaign or continuing as a different character.
- No campaign work now; preserve these choices as background only.

## Open questions / values not yet approved
- Compatible Entities, Entities Graphics and other DOTS package releases for 6000.5.4f1, to be resolved within editor.
- Fixed combat tick and runtime performance budget/hardware.
- Attack/combo/heavy strike key binding and cancel windows.
- Guard stability formulas, timely block window, dash frames, reactions and body injury thresholds.
- Rendering method and asset format, equipment visual layers, terrain deformation implementation.
- GitHub Actions/Unity licensing and test runner configuration.

Any change to accepted decisions requires explicit conversation and an entry here.
