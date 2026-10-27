using System;
using System.Collections.Generic;
using UnityEngine;

namespace MLFLegends.Combat
{
    [Serializable]
    public sealed class CharacterAnimationSettings
    {
        public string ClipId = "idle_combat";
        [Min(1)] public int FrameCount = 1;
        [Tooltip("Full cycle duration in seconds; independent of JSON duration_ms.")]
        public double TotalDurationSeconds = 1;
        [Tooltip("X: normalized cycle time 0..1. Y: relative speed >=0. Constant 1 = uniform. Finite unweighted tangents only.")]
        public AnimationCurve SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1);

        internal CharacterAnimationSettings Copy() => new CharacterAnimationSettings
        {
            ClipId = ClipId, FrameCount = FrameCount, TotalDurationSeconds = TotalDurationSeconds,
            SpeedCurve = SpeedCurve == null ? null : new AnimationCurve(SpeedCurve.keys),
        };
    }

    [Serializable]
    public sealed class CharacterAnimationEntry
    {
        public string CharacterId = "";
        public SpriteAtlasCatalog AtlasCatalog;
        public CharacterAnimationSettings[] Animations = Array.Empty<CharacterAnimationSettings>();

        internal CharacterAnimationEntry Copy()
        {
            var copy = new CharacterAnimationEntry { CharacterId = CharacterId, AtlasCatalog = AtlasCatalog };
            copy.Animations = new CharacterAnimationSettings[Animations?.Length ?? 0];
            for (var i = 0; i < copy.Animations.Length; i++)
                copy.Animations[i] = Animations[i]?.Copy();
            return copy;
        }
    }

    /// <summary>One project-owned presentation config: characters → existing atlas catalogs → animation timing.</summary>
    /// <remarks>Inspector edits increment Version. Runtime callers replace settings through Set (defensive copy),
    /// not by mutating Characters. Consumers never destroy the config/catalog/textures.</remarks>
    [CreateAssetMenu(menuName = "MLF Legends/Character Animation Config", fileName = "CharacterAnimationConfig")]
    public sealed class CharacterAnimationConfig : ScriptableObject
    {
        [SerializeField] CharacterAnimationEntry[] m_Characters = Array.Empty<CharacterAnimationEntry>();
        public IReadOnlyList<CharacterAnimationEntry> Characters => m_Characters ?? Array.Empty<CharacterAnimationEntry>();
        public int Version { get; private set; }

        public void Set(params CharacterAnimationEntry[] characters)
        {
            m_Characters = new CharacterAnimationEntry[characters?.Length ?? 0];
            for (var i = 0; i < m_Characters.Length; i++)
                m_Characters[i] = characters[i]?.Copy();
            Version++;
        }

        void OnValidate() => Version++;

        /// <summary>Validates only the requested character/clip: unrelated invalid records cannot block this character.</summary>
        public bool TryResolve(string characterId, string clipId, out SpriteAtlasCatalog catalog,
            out CharacterAnimationSettings settings, out string error)
        {
            catalog = null;
            settings = null;
            error = null;
            CharacterAnimationEntry found = null;
            foreach (var character in Characters)
            {
                if (character == null || character.CharacterId != characterId)
                    continue;
                if (found != null)
                {
                    error = $"characters.{characterId}: duplicate CharacterId";
                    return false;
                }
                found = character;
            }
            if (string.IsNullOrWhiteSpace(characterId) || found == null)
                error = $"characters.{characterId}: character settings missing";
            else if (found.AtlasCatalog == null)
                error = $"characters.{characterId}.AtlasCatalog: no catalog assigned";
            else
            {
                foreach (var animation in found.Animations ?? Array.Empty<CharacterAnimationSettings>())
                {
                    if (animation == null || animation.ClipId != clipId)
                        continue;
                    if (settings != null)
                    {
                        error = $"characters.{characterId}.animations.{clipId}: duplicate ClipId";
                        return false;
                    }
                    settings = animation;
                }
                if (settings == null)
                    error = $"characters.{characterId}.animations.{clipId}: animation settings missing";
            }
            if (error != null)
                return false;
            catalog = found.AtlasCatalog;
            return true;
        }
    }
}
