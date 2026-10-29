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

        [Tooltip("Hit box start along the facing, world units from the fighter's position (inclusive, 0..8). Target point = accepted body waist.")]
        public float ReachMin;

        [Tooltip("Hit box end along the facing, world units (inclusive, ReachMin..8).")]
        public float ReachMax;

        [Tooltip("Largest |ΔZ| (depth) between attacker and target, world units (0..4).")]
        public float DepthHalfWidth;

        [Tooltip("Largest |ΔY| (altitude) between attacker and target, world units (0..8).")]
        public float HeightTolerance;

        [Tooltip("Vertical hit center offset from the accepted waist (Position.y + Body.Height/2), world units.")]
        public float HitOffsetY;
        [Tooltip("Self launch impulse J; 0 disables. Delta velocity = J / spawn mass. Maximum 24 units/s.")]
        public float SelfImpulse;
        public float ElevationDegrees;
        [Tooltip("Local clip frame of launch; -1 = first Active tick. Quantized through the same T/curve as phases.")]
        public int LaunchFrame = -1;
        [Tooltip("Temporary physical height in world units; 0 keeps the spawn height.")]
        public float CompactHeight;
        public int CompactFirstFrame, CompactEndFrame;
        [Tooltip("Additional once-per-target J: forward X, upward Y, outward Z (tie: stable ids). Applied next tick.")]
        public Vector3 OnHitImpulse;

        public CombatStrikeParameters Parameters => new CombatStrikeParameters
        {
            Damage = Damage, ReachMin = ReachMin, ReachMax = ReachMax, DepthHalfWidth = DepthHalfWidth, HeightTolerance = HeightTolerance,
            HitOffsetY = HitOffsetY, SelfImpulse = SelfImpulse, ElevationDegrees = ElevationDegrees,
            CompactHeight = CompactHeight, CompactEnd = (ushort)(CompactHeight > 0 ? 1 : 0),
            OnHitImpulse = new Unity.Mathematics.float3(OnHitImpulse.x, OnHitImpulse.y, OnHitImpulse.z),
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
    /// Body: a vertical cylinder from CombatPosition (Radius, spawn Height, Mass), in world units; art pixels, nominal
    /// height and pivots are never converted into it. Published by the scene (CombatSmokeBootstrap) into the World's
    /// <see cref="CombatStrikeSchedule"/>: strikes use the values of the table current at their start, fighters the body of
    /// the table current at their spawn (next start/restart).
    /// <para>Migration: entries serialized before #69 have no Combat block. Their deserialized block is version 0 and is
    /// filled ONCE with the previous built-in values (<see cref="CombatAttackTuning"/>, <see cref="CombatBodyTuning"/>, the
    /// Stage 05.1 clips punch_left/punch_right/kick_left/kick_right/kick_spin); version 1 is never rewritten, so the
    /// migration is idempotent and leaves animation timing, curves and pivots untouched. The v1 → v2 art rename
    /// kick_spin → physical_ability is a separate, explicit atlas migration (<see cref="CharacterAtlasMigration"/>, #70).</para>
    /// </remarks>
    [Serializable]
    public sealed class CharacterCombatSettings : ISerializationCallbackReceiver
    {
        public const int CurrentVersion = 1;

        [Header("Body: vertical cylinder from CombatPosition")]
        [Tooltip("Radius, world units (0.05..2). Contacts, walls and spawn support use it.")]
        public float BodyRadius = CombatBodyTuning.Radius;

        [Tooltip("Spawn/base height above CombatPosition (0.25..8); Movement may temporarily accept a strike profile height.")]
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
        public void ApplyTo(ref CombatStrikeSchedule table, CombatAnimationSet animations = null)
        {
            for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
            {
                var settings = Strike(kind);
                var value = settings.Parameters;
                var timing = animations?.Attack(kind);
                value.LaunchTick = value.SelfImpulse > 0 ? table.Get(kind).ActiveStart : (ushort)0;
                if (timing != null)
                {
                    if (settings.LaunchFrame >= timing.FrameCount || settings.LaunchFrame < -1
                        || (value.CompactHeight > 0 && (settings.CompactFirstFrame < 0
                            || settings.CompactEndFrame <= settings.CompactFirstFrame || settings.CompactEndFrame > timing.FrameCount)))
                        throw new ArgumentException($"{kind}: physical frame outside clip range");
                    ushort TickAt(int frame) => (ushort)Math.Floor((frame == timing.FrameCount ? timing.DurationSeconds
                        : timing.FrameStartSeconds(frame)) * SimulationTiming.TickRateHz + 1e-6);
                    if (settings.LaunchFrame >= 0) value.LaunchTick = TickAt(settings.LaunchFrame);
                    if (value.CompactHeight > 0)
                    {
                        value.CompactStart = TickAt(settings.CompactFirstFrame);
                        value.CompactEnd = (ushort)Math.Max(value.CompactStart + 1, TickAt(settings.CompactEndFrame));
                    }
                }
                table.SetStrike(kind, value);
            }
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
            PhysicalAbility.ClipId = CombatAnimationSet.LegacyPhysicalAbilityClip; // the art such data was authored for
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
