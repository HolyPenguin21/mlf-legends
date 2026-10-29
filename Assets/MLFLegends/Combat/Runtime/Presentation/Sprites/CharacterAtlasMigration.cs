using System;
using System.Collections.Generic;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Explicit migration of one character's settings to the clips of its current atlas (#70). Never automatic: the config
    /// Inspector runs it on the owner's command (undoable) and prints the report. Idempotent: a second run changes nothing.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A strike slot whose clip is missing from the atlas and has a declared successor
    /// (<see cref="CombatAnimationSet.SuccessorClip"/>, kick_spin → physical_ability) is mapped to it. The old clip's T and curve
    /// move to the successor (the slot's tuned timing), its range becomes the whole new clip and the phases come from the
    /// successor's strike_pose; the orphaned old settings are removed.</item>
    /// <item>Every atlas clip without settings gets explicit initial settings: the whole clip, T = sum of its JSON duration_ms,
    /// flat curve, phases from strike_pose when present (else 0/0), landing from the land marker.</item>
    /// <item>Everything else is only reported, never rewritten: valid ranges, T, curves, phases and the Combat values stay.
    /// A range shorter than its clip is listed (frames unplayed), a range or phase outside the clip is an error for the owner.</item>
    /// </list>
    /// </remarks>
    public static class CharacterAtlasMigration
    {
        public static bool Apply(CharacterAnimationEntry entry, SpriteAtlasDefinition definition, List<string> report)
        {
            if (entry == null || definition == null)
                throw new ArgumentNullException(entry == null ? nameof(entry) : nameof(definition));
            var path = entry.CharacterId;
            var changed = false;
            var animations = new List<CharacterAnimationSettings>(entry.Animations ?? Array.Empty<CharacterAnimationSettings>());
            CharacterAnimationSettings Find(string clipId) => animations.Find(a => a != null && a.ClipId == clipId);
            var combat = entry.Combat ?? (entry.Combat = new CharacterCombatSettings());

            for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
            {
                var old = combat.ClipFor(kind);
                var successor = CombatAnimationSet.SuccessorClip(old);
                if (definition.TryGetClipIndex(old, out _) || successor == null || !definition.TryGetClipIndex(successor, out var index))
                    continue;
                var strike = combat.Strike(kind);
                strike.ClipId = successor;
                changed = true;
                report.Add($"{path}.Combat.{kind}.ClipId: {old} → {successor} (renamed by this atlas)");
                var source = Find(old);
                if (Find(successor) == null && source != null)
                {
                    var moved = source.Copy();
                    moved.ClipId = successor;
                    var clip = definition.Clips[index];
                    moved.EndFrame = clip.Frames.Count - 1;
                    var phases = PosePhases(clip, out var prep, out var active);
                    if (phases) { moved.PreparationEndFrame = prep; moved.ActiveEndFrame = active; }
                    animations.Add(moved);
                    report.Add($"{path}.animations.{successor}: from {old} — T {source.TotalDurationSeconds:0.#####} s and curve kept; " +
                        $"range 0..{source.EndFrame} → 0..{moved.EndFrame}; phases {source.PreparationEndFrame}/{source.ActiveEndFrame} → " +
                        $"{moved.PreparationEndFrame}/{moved.ActiveEndFrame}" + (phases ? " (strike_pose)" : " (no strike_pose: kept, check them)"));
                }
            }

            for (var i = animations.Count - 1; i >= 0; i--)
            {
                var settings = animations[i];
                if (settings == null || definition.TryGetClipIndex(settings.ClipId, out _)) continue;
                var successor = CombatAnimationSet.SuccessorClip(settings.ClipId);
                var used = false;
                for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++) used |= combat.ClipFor(kind) == settings.ClipId;
                if (successor != null && !used && Find(successor) != null)
                {
                    animations.RemoveAt(i);
                    changed = true;
                    report.Add($"{path}.animations.{settings.ClipId}: removed (replaced by {successor})");
                }
                else
                    report.Add($"{path}.animations.{settings.ClipId}: clip missing from the atlas (kept; remove or remap it)");
            }

            foreach (var clip in definition.Clips)
            {
                var settings = Find(clip.Id);
                if (settings == null)
                {
                    settings = Initial(clip);
                    animations.Add(settings);
                    changed = true;
                    report.Add($"{path}.animations.{clip.Id}: initialized — 0..{settings.EndFrame}, T {settings.TotalDurationSeconds:0.#####} s " +
                        $"(JSON duration_ms), flat curve, phases {settings.PreparationEndFrame}/{settings.ActiveEndFrame}");
                    continue;
                }
                var last = clip.Frames.Count - 1;
                if (settings.EndFrame > last)
                    report.Add($"{path}.animations.{clip.Id}.EndFrame: {settings.EndFrame} is outside the clip's local frames 0..{last} (fix it)");
                else if (settings.EndFrame < last)
                    report.Add($"{path}.animations.{clip.Id}: EndFrame {settings.EndFrame} leaves local frames {settings.EndFrame + 1}..{last} unplayed (kept)");
            }

            if (changed) entry.Animations = animations.ToArray();
            if (!changed) report.Add($"{path}: nothing to migrate");
            return changed;
        }

        /// <summary>Explicit settings of a clip that has none: the art's own range, duration and markers.</summary>
        public static CharacterAnimationSettings Initial(SpriteAtlasClip clip)
        {
            var total = 0.0;
            foreach (var ms in clip.DurationsMs) total += ms;
            var settings = new CharacterAnimationSettings
            {
                ClipId = clip.Id, EndFrame = clip.Frames.Count - 1, TotalDurationSeconds = total / 1000,
                SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1), LandingStartFrame = -1,
            };
            if (PosePhases(clip, out var prep, out var active)) { settings.PreparationEndFrame = prep; settings.ActiveEndFrame = active; }
            if (clip.Id == CombatAnimationSet.JumpClip && clip.Markers.TryGetValue("land", out var land) && land.Length > 0)
                settings.LandingStartFrame = land[0];
            return settings;
        }

        // Preparation ends just before strike_pose, Active ends at its last frame; at least one frame per phase.
        static bool PosePhases(SpriteAtlasClip clip, out int preparationEnd, out int activeEnd)
        {
            preparationEnd = activeEnd = 0;
            if (!clip.Markers.TryGetValue("strike_pose", out var pose) || pose.Length == 0) return false;
            if (pose[0] < 1 || pose[pose.Length - 1] >= clip.Frames.Count - 1) return false;
            preparationEnd = pose[0] - 1;
            activeEnd = pose[pose.Length - 1];
            return true;
        }
    }
}
