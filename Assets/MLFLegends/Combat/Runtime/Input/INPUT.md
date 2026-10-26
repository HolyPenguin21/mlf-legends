# Player input (Stage 02.3, Issue #22)

Real Unity Input System input for the one fighter the human controls, through the existing single command queue.
This folder adds no queue, clock, accumulator or combat state: its only output is at most one `CombatCommand` per real
fixed tick, appended to `CombatCommandSubmission` before the processor. Facing, movement, jump and every later rule
stay with their owners in `Simulation/`; player and AI commands are validated and applied identically.

## Pieces

| Type | Kind | Responsibility | Owner / lifetime |
|---|---|---|---|
| `PlayerInputCapture` | plain class, `IDisposable` | Raw device state of the Player actions + presses not yet consumed (`CommandButtons` bits). Not a command queue: no tick, no combatant, no `CombatCommand`. | Created/disposed by the adapter (tests create their own). Owns a **clone** of the asset's Player map, its `performed` callbacks and one `InputSystem.onDeviceChange` / `onAfterUpdate` subscription. |
| `PlayerInputCommandSystem` | `SystemBase`, `CombatCommandCollectionSystemGroup` | The single tick-aligned Player producer: per real fixed tick, one command for `BattleSession.HumanCombatant`, for the tick being simulated, before the processor (`OrderLast`). | One per World; auto-created in the default world, absent from replay/benchmark/manual worlds unless a test adds it. Holds a reference to the attached capture and the last binding (battle number, human ID); no static state. |
| `PlayerInputAdapter` | `MonoBehaviour` (scene) | Scene glue: while enabled, owns one capture and keeps it attached to the **current** default World's producer (checked on enable and once per frame: attaches when a World appears later, moves A → B without disable/enable); forwards application focus. | `CombatSmoke.unity` → *Player Input*. Disable/destroy detaches + disposes. A static enable counter only orders adapters (like the bootstrap owner token). |

The actions asset is `Assets/InputSystem_Actions.inputactions` (also the project-wide actions). Nobody here enables or
disables the shared asset or its maps: each capture enables and disposes only its own clone.

## Bindings (provisional, remappable in the asset)

| Action (Player map) | Keyboard | Gamepad | Command |
|---|---|---|---|
| Move (Vector2) | W A S D (W = world +Z, D = world +X) | left stick | `MoveX`, `MoveZ` |
| Attack (Button) | **J** (also Enter, left mouse) | West | `Attack` |
| Jump (Button) | **K** (also Space) | South | `Jump` |
| Guard (Button, new) | **L** | right shoulder | `Guard` |

Arrow keys were removed from Move: they pan the camera (`Presentation/CAMERA.md`). Attack and Guard are passed as the
existing intentions only; attacks, guard and L + direction dash are later stages. Input is never camera-relative.

## Sampling policy (what one command contains)

| Field | Source | Catch-up ticks of one frame |
|---|---|---|
| `MoveX`, `MoveZ` | `Move.ReadValue<Vector2>()` → `PlayerInputCapture.Quantize` | same live value on every tick |
| `Held` | device state of each action's bound buttons (`InputControl.IsPressed`) | same live value on every tick |
| `Pressed` | `performed` callbacks since the last consumption, OR-ed bits | first tick that produces a command only |
| `Tick` | `SimulationClock.Tick` (the tick being simulated) | — |
| `Source` | `Player` | — |

- **Quantization** (one explicit policy for analog bindings): idle when the stick magnitude is below 0.25
  (`MoveDeadZone`); otherwise an axis gives its sign when it is at least sin 22.5° of the magnitude, i.e. 8 equal 45°
  sectors. Keyboard composites (±1 and normalized diagonals) map exactly. `CombatMovementSystem` alone normalizes the
  diagonal speed. No float axes in commands.
- **Taps:** press and release between two ticks give `Pressed` with `Held = None`. Several presses of a button before a
  tick are one bit (current contract; no repeat counting). A 0-tick render frame keeps its presses for the next tick.
- **Held never creates Pressed.** A button already down when a capture is enabled is Held at once but never Pressed.
- **Attach boundary:** `Attach` drops presses recorded before it and binds to the session as it is at that moment, so a
  fresh tap made right after attaching to a running battle reaches its first tick (review #22); a later Start/Restart
  is still a new binding that drops older presses.
- **Not** a frame-rate `Tick + 1` producer (the pacing-dependent scheme of PR #20): commands are produced inside the
  fixed step, so 30/60/120 FPS and uneven pacing give one command per real tick.

## Frame order (verified in the real PlayerLoop, PlayMode tests)

```text
PreUpdate.NewInputUpdate        Input System (default ProcessEventsInDynamicUpdate): events → device state → callbacks (presses)
Update
├─ MonoBehaviour.Update / coroutines
└─ SimulationSystemGroup        default world, appended at the end of Update by Entities
   └─ FixedStepSimulationSystemGroup × N (0..N ticks)
      BeginFixedStep ECB → BattleLifecycleSystem → CombatClockSystem →
      CombatCommandCollectionSystemGroup [PlayerInputCommandSystem … CombatCommandProcessorSystem (OrderLast)] →
      CombatStateUpdateSystemGroup [CombatFacingSystem → CombatMovementSystem] → Publication → EndFixedStep ECB
PreLateUpdate.ScriptRunBehaviourLateUpdate   CombatCameraController (reads ECS, writes the camera only)
```

The project has no InputSettings asset, so the update mode is the default dynamic update: no custom accumulator, no
manual `InputSystem.Update()`, no use of `ProcessEventsInFixedUpdate` (that is MonoBehaviour `FixedUpdate`, unrelated
to the ECS combat tick).

## Binding, lifecycle and cleanup

The producer submits only for the **current, active, in-combat** `HumanCombatant` (found by `CombatantId` among enabled
`InCombat` actors each tick; no Entity handle is kept). Otherwise it submits nothing and drops pending presses:

| Situation | Effect |
|---|---|
| No capture attached / capture disposed | nothing (replay, benchmark, manual worlds; scenes without adapter) |
| Battle idle, human despawned or `InCombat` disabled | no command (no `UnknownCombatant` noise), presses dropped |
| New battle number or human ID (Start/Restart/End, also between catch-up ticks) | presses dropped before the first command of the new binding; Held/Move are the live state |
| `Time.timeScale == 0` (pause) | presses ignored and dropped after every input update; on resume Held/Move are live, no synthetic Pressed |
| Application focus lost / regained (`OnApplicationFocus`) | presses dropped; buttons held so far blocked (below); while unfocused Move/Held sample neutral, so the fighter stops on the next tick |
| Device disabled / enabled / soft or hard reset (the Input System's own focus cycle) | presses dropped; buttons held before it blocked (below) |
| Device removed / disconnected | presses dropped; the actions cancel the lost controls (neutral) |
| Second adapter enabled (duplicate, additive scene switch) | the producer keeps the adapter with the highest enable order, whichever attaches first; a replaced adapter neither retakes control nor detaches its successor; still one producer |
| Default World appears after the adapter was enabled, or is replaced A → B | the adapter detaches from A (if alive) and attaches to the new World's producer (stale presses dropped); each World is tried once, so an outranked adapter does not retry every frame |
| Adapter disabled/destroyed, Play Mode exit | detach (if still attached) + dispose of the clone and every subscription; a disposed World is skipped |

Pause cannot carry a press across it: a tap of the last unpaused 0-tick frame is dropped too.

**Held across a focus change or device reset (blocking).** With its default background behaviour the Input System
soft-resets/disables devices on focus loss and re-enables + syncs (or resets) them on regain; a key that is still
physically down then comes back through a sync or the next device report, which the action sees as a new press. The
capture therefore blocks every button that was held (device state at the end of the previous Input System update) when
focus changes or a device is disabled/enabled/reset: its presses are ignored until the device reports it released
(a held → released transition in a later update; the synthetic release of the reset itself does not count). Result:
a restored hold is Held without Pressed (no unwanted Jump/Attack); a button that was not held presses normally;
release → press works. Conservative edge: a button held at the interruption, released while unfocused and never
re-reported, loses its first press after regain (the next release unblocks it).

## Manual check (owner GUI, record in Docs/QA.md; NOT RUN until performed)

There are no fighter visuals before Stage 03: observe *Window > Entities > Hierarchy* (`Combatant Human` →
`CombatPosition`, `CombatMotion`, `CombatFacing`, `AppliedCombatCommand`) and the Main Camera Transform/Inspector.
1. Open `Assets/Scenes/CombatSmoke.unity`, enter Play Mode, click the Game view. Hold D/A/W/S: the human's X/Z change,
   `AppliedCombatCommand.Source = Player`; release: it stops. Tap K: one jump; hold K: one jump only. J/L: `Held`/`Pressed`
   Attack/Guard appear in the applied command, nothing else happens (no attacks/guard yet).
2. Camera: it follows the human's X/Z; arrows pan, `=`/`-` and the wheel zoom (limits 2…20), Home recenters; the
   rotation (40°, 0°, 0°) never changes; `Projection = Orthographic`.
3. Set `Time.timeScale = 0` (debugger or a temporary tool): K/J taps and WASD do not change ECS state, pan/zoom still
   work; restore 1 while holding D: the human moves, no jump from the paused taps.
4. Alt-tab away while holding D: the human stops on the next tick; return: it moves again only while D is held. Hold K,
   alt-tab away and back still holding K: no jump; release and press K: one jump.
5. Disable/enable *Player Input*: control stops/resumes, no jump on enable. Disable/enable *Combat Bootstrap*: the
   camera keeps its view while idle, then follows the new human with the pan reset.
6. Repeat with *Enter Play Mode Settings* = *Do not reload Domain or Scene*: no Console errors, no leak warnings on exit.

## Tests

- `Tests/EditMode/Input/PlayerInputProducerTests` (InputTestFixture virtual keyboard/gamepad + the real asset + the real
  producer in a replay World): 30/60/120 FPS and variable pacing, 0/1/2/3/10 ticks per frame, taps, holds, diagonals,
  stick quantization, Restart between catch-up ticks, End/idle/despawn/defeat, pause, focus and device loss, attach /
  replace / detach / dispose, replay worlds without adapter, player-vs-AI parity, 200 vs 200; review regressions: a fresh
  tap right after attaching to a running battle, enable-order priority, keys held across a reset or disable/enable cycle.
- `Tests/EditMode/Input/PlayerInputQuantizeTests`: the quantization policy.
- `Tests/PlayMode/Input/PlayerInputPlayModeTests`: real PlayerLoop order, real-loop taps/holds at 30/60/120 FPS
  (`Time.captureFramerate`), pause, focus, additive scene switch/re-enable, camera follow/rebind and the exact view on End
  (CombatSmoke); the Input System's real focus cycle (test runtime focus, runInBackground on/off) with the device
  re-reporting held K/J/L; an adapter following a default World that appears late or is replaced A → B → A.
- `Tests/EditMode/Performance/PlayerInputBenchmarkTests`: producer cost, see `Docs/PERFORMANCE_BASELINE.md`.
