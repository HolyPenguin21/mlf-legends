using System;
using System.Collections.Generic;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Editable combat values of one strike slot of a character (#69): which clip it plays and its damage/hit box. The clip's
    /// Preparation/Active/Recovery frames, duration and curve stay in the character's Animations (the only timing source).
    /// </summary>
    [Serializable]
    public sealed class CharacterStrikeSettings
    {
        [Tooltip("Clip this slot plays (character clip id). Its Preparation / Active / Recovery frames in Animations set the strike windows.")]
        public string ClipId = "";

        [Tooltip("HP removed from each valid target, once per strike (0..1000).")]
        public int Damage;

        [Tooltip("Hit box start along the facing, world units from the fighter's position (inclusive, 0..8). Target point = its position.")]
        public float ReachMin;

        [Tooltip("Hit box end along the facing, world units (inclusive, ReachMin..8).")]
        public float ReachMax;

        [Tooltip("Largest |ΔZ| (depth) between attacker and target, world units (0..4).")]
        public float DepthHalfWidth;

        [Tooltip("Largest |ΔY| (altitude) between attacker and target, world units (0..8).")]
        public float HeightTolerance;

        public CombatStrikeParameters Parameters => new CombatStrikeParameters
        {
            Damage = Damage, ReachMin = ReachMin, ReachMax = ReachMax, DepthHalfWidth = DepthHalfWidth, HeightTolerance = HeightTolerance,
        };

        internal static CharacterStrikeSettings Default(CombatAttackKind kind)
        {
            var strike = CombatAttackTuning.Get(kind);
            return new CharacterStrikeSettings
            {
                ClipId = CombatAnimationSet.DefaultClip(kind), Damage = strike.Damage, ReachMin = strike.ReachMin,
                ReachMax = strike.ReachMax, DepthHalfWidth = strike.DepthHalfWidth, HeightTolerance = strike.HeightTolerance,
            };
        }

        internal CharacterStrikeSettings Copy() => (CharacterStrikeSettings)MemberwiseClone();
    }

    /// <summary>
    /// Per-character combat data next to its animation entry (#69): the physical body and the five strike slots. One
    /// character binding (CharacterId) for every fighter of that type; nothing here depends on a CombatantId, team or role.
    /// </summary>
    /// <remarks>
    /// Body: a vertical cylinder from CombatPosition (Radius, constant Height, Mass), in world units; art pixels, nominal
    /// height and pivots are never converted into it. Published by the scene (CombatSmokeBootstrap) into the World's
    /// <see cref="CombatStrikeSchedule"/>: strikes use the values of the table current at their start, fighters the body of
    /// the table current at their spawn (next start/restart).
    /// <para>Migration: entries serialized before #69 have no Combat block. Their deserialized block is version 0 and is
    /// filled ONCE with the previous built-in values (<see cref="CombatAttackTuning"/>, <see cref="CombatBodyTuning"/>, the
    /// Stage 05.1 clips punch_left/punch_right/kick_left/kick_right/kick_spin); version 1 is never rewritten, so the
    /// migration is idempotent and leaves animation timing, curves and pivots untouched.</para>
    /// </remarks>
    [Serializable]
    public sealed class CharacterCombatSettings : ISerializationCallbackReceiver
    {
        public const int CurrentVersion = 1;

        [Header("Body: vertical cylinder from CombatPosition")]
        [Tooltip("Radius, world units (0.05..2). Contacts, walls and spawn support use it.")]
        public float BodyRadius = CombatBodyTuning.Radius;

        [Tooltip("Constant height above CombatPosition, world units (0.25..8).")]
        public float BodyHeight = CombatBodyTuning.Height;

        [Tooltip("Mass (0.05..100): impulse Δv = J / Mass and the inelastic contact share.")]
        public float BodyMass = CombatBodyTuning.Mass;

        [Header("Strikes (U series: Hand1 Hand1 Hand2 Hand1 Hand2 …; I series: Foot1 Foot1 Foot2 …; O: Physical Ability)")]
        public CharacterStrikeSettings Hand1 = CharacterStrikeSettings.Default(CombatAttackKind.Hand1);
        public CharacterStrikeSettings Hand2 = CharacterStrikeSettings.Default(CombatAttackKind.Hand2);
        public CharacterStrikeSettings Foot1 = CharacterStrikeSettings.Default(CombatAttackKind.Foot1);
        public CharacterStrikeSettings Foot2 = CharacterStrikeSettings.Default(CombatAttackKind.Foot2);
        public CharacterStrikeSettings PhysicalAbility = CharacterStrikeSettings.Default(CombatAttackKind.PhysicalAbility);

        [SerializeField, HideInInspector] int m_Version = CurrentVersion;

        public int Version => m_Version;

        public CombatBody Body => new CombatBody { Radius = BodyRadius, Height = BodyHeight, Mass = BodyMass };

        public CharacterStrikeSettings Strike(CombatAttackKind kind)
        {
            switch (kind)
            {
                case CombatAttackKind.Hand1: return Hand1;
                case CombatAttackKind.Hand2: return Hand2;
                case CombatAttackKind.Foot1: return Foot1;
                case CombatAttackKind.Foot2: return Foot2;
                case CombatAttackKind.PhysicalAbility: return PhysicalAbility;
                default: return null;
            }
        }

        /// <summary>Clip of <paramref name="kind"/>; the slot's default clip when the slot or its id is missing.</summary>
        public string ClipFor(CombatAttackKind kind)
        {
            var clip = Strike(kind)?.ClipId;
            return string.IsNullOrEmpty(clip) ? CombatAnimationSet.DefaultClip(kind) : clip;
        }

        /// <summary>Range checks with labelled errors (never clamps); clip ids are checked against the atlas by CombatAnimationSet.</summary>
        public bool Validate(string path, List<string> errors)
        {
            var before = errors.Count;
            if (!CombatCharacterLimits.IsValid(Body))
                errors.Add($"{path}.Body: Radius {BodyRadius} ({CombatCharacterLimits.MinRadius}..{CombatCharacterLimits.MaxRadius}), " +
                    $"Height {BodyHeight} ({CombatCharacterLimits.MinHeight}..{CombatCharacterLimits.MaxHeight}), " +
                    $"Mass {BodyMass} ({CombatCharacterLimits.MinMass}..{CombatCharacterLimits.MaxMass}) outside the supported range");
            for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
            {
                var strike = Strike(kind);
                if (strike == null)
                {
                    errors.Add($"{path}.{kind}: strike settings missing");
                    continue;
                }
                if (!CombatCharacterLimits.IsValid(strike.Parameters))
                    errors.Add($"{path}.{kind}: Damage {strike.Damage} (0..{CombatCharacterLimits.MaxDamage}), Reach {strike.ReachMin}..{strike.ReachMax} " +
                        $"(0 <= min <= max <= {CombatCharacterLimits.MaxReach}), Depth ±{strike.DepthHalfWidth} (0..{CombatCharacterLimits.MaxDepthHalfWidth}), " +
                        $"Height ±{strike.HeightTolerance} (0..{CombatCharacterLimits.MaxHeightTolerance}) outside the supported range");
                if (CombatAnimationSet.IsMotionClip(ClipFor(kind)))
                    errors.Add($"{path}.{kind}.ClipId: '{ClipFor(kind)}' is a locomotion clip, not a strike");
            }
            return errors.Count == before;
        }

        /// <summary>Writes this character's per-strike values and body into <paramref name="table"/> (timing is not touched).</summary>
        public void ApplyTo(ref CombatStrikeSchedule table)
        {
            for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
                table.SetStrike(kind, Strike(kind).Parameters);
            table.Body = Body;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (m_Version >= CurrentVersion)
                return;
            // Pre-#69 data (no Combat block, or a zero-initialized one): the previous built-in values, exactly once.
            var defaults = new CharacterCombatSettings();
            BodyRadius = defaults.BodyRadius; BodyHeight = defaults.BodyHeight; BodyMass = defaults.BodyMass;
            Hand1 = defaults.Hand1; Hand2 = defaults.Hand2; Foot1 = defaults.Foot1; Foot2 = defaults.Foot2;
            PhysicalAbility = defaults.PhysicalAbility;
            m_Version = CurrentVersion;
        }

        internal CharacterCombatSettings Copy() => new CharacterCombatSettings
        {
            BodyRadius = BodyRadius, BodyHeight = BodyHeight, BodyMass = BodyMass,
            Hand1 = Hand1?.Copy(), Hand2 = Hand2?.Copy(), Foot1 = Foot1?.Copy(), Foot2 = Foot2?.Copy(),
            PhysicalAbility = PhysicalAbility?.Copy(), m_Version = m_Version,
        };
    }
}
