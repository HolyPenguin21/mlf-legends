using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Playback of one clip. Frame numbers are LOCAL indices of the source clip, from 0 (its first frame in the manifest),
    /// never page cells or global atlas frame numbers. The clip plays <see cref="StartFrame"/>..<see cref="EndFrame"/>
    /// inclusive over <see cref="TotalDurationSeconds"/>, distributed by <see cref="SpeedCurve"/>; frames after EndFrame
    /// are never read. Strike clips also split that range into Preparation / Active / Recovery, which become the
    /// authoritative simulation windows of the strike (see ANIMATION.md).
    /// </summary>
    [Serializable]
    public sealed class CharacterAnimationSettings : ISerializationCallbackReceiver
    {
        public string ClipId = "idle_combat";

        [Tooltip("Last played frame, inclusive. Local clip frame index from 0 (not a page cell or atlas frame number).")]
        public int EndFrame;

        [Tooltip("Full playback duration of StartFrame..EndFrame in seconds; independent of JSON duration_ms. For a strike it is also the strike's simulated length.")]
        public double TotalDurationSeconds = 1;

        [Tooltip("X: normalized playback time 0..1 over StartFrame..EndFrame. Y: relative speed >=0. Constant 1 = uniform. Finite unweighted tangents only.")]
        public AnimationCurve SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1);

        [Tooltip("Strike clips only: last Preparation frame, inclusive (local index). Preparation = StartFrame..this; no hits, the fighter may move.")]
        public int PreparationEndFrame;

        [Tooltip("Strike clips only: last Active frame, inclusive (local index). Active = PreparationEndFrame+1..this: hits resolve. Recovery = this+1..EndFrame.")]
        public int ActiveEndFrame;

        // Migration only: the legacy "FrameCount = first N frames" value (before the Stage 03 range settings), moved into EndFrame = N - 1 on load and then 0.
        // Never read otherwise; the frame count is always derived from StartFrame/EndFrame.
        [SerializeField, HideInInspector, FormerlySerializedAs("FrameCount")] int m_LegacyFrameCount;

        /// <summary>First played frame: the source clip's first frame, local index 0. Not editable.</summary>
        public int StartFrame => 0;

        /// <summary>Played frames, <c>EndFrame - StartFrame + 1</c>. Not editable.</summary>
        public int FrameCount => EndFrame - StartFrame + 1;

        /// <summary>First Active frame (derived from <see cref="PreparationEndFrame"/>).</summary>
        public int ActiveStartFrame => PreparationEndFrame + 1;

        /// <summary>First Recovery frame (derived from <see cref="ActiveEndFrame"/>); Recovery ends at <see cref="EndFrame"/>.</summary>
        public int RecoveryStartFrame => ActiveEndFrame + 1;

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (m_LegacyFrameCount <= 0)
                return;
            EndFrame = StartFrame + m_LegacyFrameCount - 1;
            m_LegacyFrameCount = 0;
        }

        internal CharacterAnimationSettings Copy() => new CharacterAnimationSettings
        {
            ClipId = ClipId, EndFrame = EndFrame, TotalDurationSeconds = TotalDurationSeconds,
            SpeedCurve = SpeedCurve == null ? null : new AnimationCurve(SpeedCurve.keys),
            PreparationEndFrame = PreparationEndFrame, ActiveEndFrame = ActiveEndFrame,
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
