using System;
using System.Collections.Generic;
using UnityEngine;

namespace MLFLegends.Combat
{
    public enum CombatEffectSource : byte { Disabled, Catalog, Technical }
    public enum CombatEffectAnchor : byte { Offset, HandLeft, HandRight, CastOrigin }
    public enum CombatEffectMirror : byte { Facing, Never }
    public enum CombatEffectMissingAnchor : byte { FallbackOffset, Skip }

    [Serializable]
    public sealed class CharacterEffectSettings
    {
        public CombatEffectSource Source;
        public SpriteAtlasCatalog Catalog;
        [Tooltip("Effect clip id; Technical uses swing or impact. No implicit looping.")]
        public string ClipId = "";
        public float Scale = 1;
        [Tooltip("World units relative to support (Swing) or confirmed waist contact (Impact); forward X mirrors once.")]
        public Vector3 Offset;
        public CombatEffectMirror Mirror;
        public CombatEffectAnchor Anchor;
        public CombatEffectMissingAnchor MissingAnchor;
        public SpriteAnchorLayer Layer = SpriteAnchorLayer.Front;
        [Tooltip("Impact lifetime only (0..2 s, positive). Swing always ends after the last graphic Active frame.")]
        public float DurationSeconds = .12f;

        public void Validate(string path, List<string> errors)
        {
            if (Source == CombatEffectSource.Disabled) return;
            if (!Enum.IsDefined(typeof(CombatEffectSource), Source) || !Enum.IsDefined(typeof(CombatEffectMirror), Mirror)
                || !Enum.IsDefined(typeof(CombatEffectAnchor), Anchor) || !Enum.IsDefined(typeof(CombatEffectMissingAnchor), MissingAnchor)
                || !Enum.IsDefined(typeof(SpriteAnchorLayer), Layer)) errors.Add(path + ": invalid policy");
            if (!float.IsFinite(Scale) || Scale <= 0 || Scale > 16 || !float.IsFinite(Offset.x) || !float.IsFinite(Offset.y)
                || !float.IsFinite(Offset.z) || !float.IsFinite(DurationSeconds) || DurationSeconds <= 0 || DurationSeconds > 2)
                errors.Add(path + ": scale must be finite in (0,16], offset finite, Impact duration in (0,2] seconds");
            if (string.IsNullOrEmpty(ClipId) || Source == CombatEffectSource.Catalog && Catalog == null)
                errors.Add(path + ": enabled binding needs clip id and catalog (unless Technical)");
        }
        internal CharacterEffectSettings Copy() => (CharacterEffectSettings)MemberwiseClone();
    }
}
