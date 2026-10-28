# Final combat controls — issue #52, 2026-10-28

Base main: **1c96c6faf12a70e27e735655dd2c993434dd8e0b**, including merged PR #50 and #51.
Branch: `feature/52-final-combat-controls`. One PR, no implementing-agent merge. **Stage 03 remains open until owner
manual acceptance.** Existing five strikes/hits are delivered; body/world collisions remain Stage 04 work.

## Where to configure and what the keys mean

`Assets/InputSystem_Actions.inputactions` → **Player**. These are physical keys; Shift is not required.

| Key | Action / CommandButtons flag | Behaviour |
|---|---|---|
| Y | PunchLeft | Always punch_left |
| U | PunchRight | Always punch_right |
| I | Attack | Existing general hand series, alternating sides |
| H | KickLeft | Always kick_left |
| J | KickRight | Always kick_right, including after another right kick |
| K | KickSpin | Always kick_spin, also directly from free grounded stance |
| L | Kick | Existing general foot series: kick_left → kick_right → kick_spin |
| Space | Jump | Existing jump |

Anatomical side belongs to the existing named strike/clip. Facing mirrors the art and anchors; it does not swap the
requested anatomical strike. One press = one intention; holding never repeats. Exact requests retain their exact kind
when accepted by the existing follow-up buffer, while general Attack/Kick retain their old selection rules.

When several strike buttons are pressed for the same tick, the authoritative selection order is **PunchLeft → PunchRight
→ KickLeft → KickRight → KickSpin → Attack → Kick** (Y/U/H/J/K/I/L). Input callbacks only OR bits; their order does not
choose the winner. Player and AI share this selector. From free grounded state, Jump + any strike starts Jump and drops
the strike. During landing recovery all strike/Jump presses are consumed/dropped, never deferred until recovery ends.

Only these eight combat bindings remain. Player Move and all its existing bindings (including WASD/sticks), Camera/UI
maps and control schemes are preserved. Enter/mouse/gamepad/other-device combat aliases are removed, including the old
meaning of J/U/K/L. Look/Interact/Crouch/Previous/Next/Sprint/Guard template Player actions are removed. **Crouch is excluded
from the project. Dash/Cast are deferred and unbound. Guard is unimplemented and unbound; its future key is chosen before
Stage 06. L is now general Kick.**

## Existing data path and compatibility

`PlayerInputCapture` clones the existing Player map, validates required names before subscribing, and owns eight button
callbacks. Callback/action arrays are allocated once per capture. Held sampling and cleanup iterate those same arrays;
there are no new per-tick allocations, queues, clocks, action owners or render resources.

`PlayerInputCommandSystem` still consumes Pressed once per real tick. The existing common processor validates the full
flags and copies typed command values. `CombatActionSystem` calls the extended `CombatAttackTuning.StrikeButton/Select`.
Its existing `CombatAttackState.Buffered` stores ONE selected intention unchanged until chain execution. Preparation
press rejection, Active/Recovery buffer, chain points, max series length and finisher full Recovery remain as before.
Exact spin does not bypass any legality check or allow a follow-up out of its full-Recovery finisher.

`CommandButtons` is now **ushort**. Existing values remain Attack=1, Jump=2, Guard=4, Kick=8. Exact PunchLeft/PunchRight/
KickLeft/KickRight/KickSpin use bits 4..8 (16/32/64/128/256). The common valid mask is 0x1ff; undefined bits 0x200 and above
are invalid for either source. Guard remains reserved logically for numeric compatibility, without a Player action/binding.
Held/Pressed, capture blocked bits, Buffered, pending commands and replay fields are typed flags; no byte narrowing was
introduced. The unmanaged command/state layout changes, so consumers must rebuild; there is no promised binary replay
format to reinterpret automatically. Text replay diagnostics name the new flags through the existing enum formatting.

The original owner animation config/atlas/PNG, damage/reach, phase windows, chain delays, scene roster and camera settings
are unchanged. PR #51 already binds the SAME config to bootstrap/renderer and safely handles `clips: {}` before preview
indexing. Those fixes are checked in the base, not recreated. Landing remains derived from jump 4..5/T/curve: currently
**27 ticks / 0.45s**. Full source canvas/offset/anchors, shared GPU resources and 50%/25%/25% planar attack speeds remain.

## Verification and manual acceptance

Performed here (source/data/model checks, not Unity execution):

- All **83 C# files** parsed without syntax errors; `git diff --check`.
- Input asset: exactly eight expected keyboard-only combat bindings; nine Player actions including Move; unique
  action/binding IDs; Camera/UI/control schemes and the entire Move action/binding data unchanged from base.
- Flag/selector source model: stable old values, valid 0x1ff mask, all **512** nine-bit input combinations; exact mapping
  independent of prior strike. Priority covers all seven strike intentions and ignores Jump/Guard for strike selection.
- Traced capture → producer → common validation → action selection → buffered execution for both sources; high-bit
  KickSpin reaches every typed field without truncation. Jump and landing gates remain ahead of execution.
- Existing fixtures updated for Space/I/L and removed gamepad combat aliases. Existing input test exercises all eight
  physical bindings; existing player/AI strike check exercises all five exact starts/buffered repeats and the 127 nonempty
  simultaneous strike combinations. Undefined-bit rejection uses 0x200. No new test cases/files or test suite added.
- Diff checks preserve art, owner clip timing, scene, movement/hit systems, renderer and GPU buffers; no per-fighter
  objects or new runtime systems. Runtime allocation/performance claims still require measurement.

**NOT RUN:** Unity compilation/import/source generation/Burst, EditMode/PlayMode (including the updated fixtures), keyboard
play, GPU/GC/performance and player build: no Unity Editor/dependencies or external art page here. Syntax parsing/model
checks and historical results on other SHAs are not a Unity PASS for this delivery.

Owner check on the delivered SHA:

1. Tap/hold Y/U/H/J/K separately and through accepted chains: each retains its named clip, including repeated U/J and
   direct K from stance. Check both facings. I alternates hands; L chains feet into spin. Holding does not repeat.
2. Press several strikes together in different callback/event orders: priority Y/U/H/J/K/I/L wins once. Space wins from
   free grounded stance. Catch-up frames do not duplicate Pressed; AI can submit the same exact/general flags.
3. Verify startup publishes owner strike windows and 27-tick landing. Hold direction across contact: no sliding/early
   run; frame 4 then 5; afterward run or idle_combat. Press any strike/Space during recovery: no delayed action.
4. Check pause/focus/device resets, restart/despawn and World replacement for stale presses/buffers. Confirm old
   Enter/mouse/gamepad combat aliases are absent; WASD/sticks, camera and UI retain their existing behaviour.
5. Verify full extreme poses/anchors/offset in both facings, frustum edges, 50/25/25 speed and full-speed restoration;
   inspect shared resource/batch counts and actual allocations at 2/100/400 fighters. Stage 03 closes only on confirmation.
