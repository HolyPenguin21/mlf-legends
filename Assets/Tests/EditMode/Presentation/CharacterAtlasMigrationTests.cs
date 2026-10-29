using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    // #70: the v2 atlas (12 clips / 76 frames, 192×160) through the tracked JSON/catalog/config, the explicit settings
    // migration, custom image positions and a strike that started before the atlas changed.
    public class CharacterAtlasMigrationTests
    {
        const string ManifestPath = "Assets/MLFLegends/Art/Characters/MageFemale01/Metadata/mage_female_01.atlas.json";
        const string CatalogPath = "Assets/MLFLegends/Art/Characters/MageFemale01/Metadata/MageFemale01SpriteAtlasCatalog.asset";
        const string ConfigPath = "Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset";
        const string OffsetsPath = "Docs/Evidence/70/owner-horizontal-offsets.json";
        const string Character = "mage_female_01";

        static SpriteAtlasDefinition Definition()
        {
            var errors = new List<string>();
            var definition = SpriteAtlasManifest.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath).text, errors);
            Assert.That(definition, Is.Not.Null, string.Join("\n", errors));
            return definition;
        }

        static CharacterAnimationConfig Config => AssetDatabase.LoadAssetAtPath<CharacterAnimationConfig>(ConfigPath);

        static CharacterAnimationEntry Clone(CharacterAnimationEntry entry) => JsonUtility.FromJson<CharacterAnimationEntry>(JsonUtility.ToJson(entry));

        static CharacterAnimationSettings Settings(string clip, int end, double seconds, int preparation = 0, int active = 0) =>
            new CharacterAnimationSettings { ClipId = clip, EndFrame = end, TotalDurationSeconds = seconds, SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1),
                PreparationEndFrame = preparation, ActiveEndFrame = active, LandingStartFrame = clip == "jump" ? 4 : -1 };

        [Test]
        public void ProjectManifestIsTheTwelveClipSeventySixFrameCanvasWithTheReferenceSupport()
        {
            var d = Definition();
            Assert.That(d.Clips.Count, Is.EqualTo(12)); Assert.That(d.Frames.Count, Is.EqualTo(76));
            Assert.That(d.SourceSize, Is.EqualTo(new int2(192, 160))); Assert.That(d.NominalHeightPx, Is.EqualTo(128));
            Assert.That(d.Pages.Count, Is.EqualTo(1)); Assert.That(d.Pages[0].Size, Is.EqualTo(new int2(2000, 2016)), "Not square, not resized.");
            Assert.That(math.distance(d.WorldSupport, new float2(96, 136)), Is.LessThan(1e-4f), "50% X / 15% from the bottom of 192×160.");
            var counts = d.Clips.ToDictionary(c => c.Id, c => c.Frames.Count);
            Assert.That(counts, Is.EqualTo(new Dictionary<string, int>
            {
                ["idle_combat"] = 6, ["run"] = 6, ["jump"] = 6, ["punch_left"] = 6, ["punch_right"] = 6, ["kick_left"] = 6, ["kick_right"] = 6,
                ["physical_ability"] = 8, ["block"] = 4, ["dash"] = 6, ["skill_light"] = 6, ["skill_complex"] = 10,
            }));
            Assert.That(CombatAnimationSet.ClipIds.All(c => counts.ContainsKey(c)), Is.True, "Every required clip exists; kick_spin is not required.");
        }

        // Every atlas clip is configurable; only idle/run/jump and the slot clips are ECS actions (block, dash, skills are not).
        [Test]
        public void ProjectConfigCoversAllTwelveClipsAndIsAlreadyMigrated()
        {
            var entry = Config.Characters.Single(c => c.CharacterId == Character);
            var d = Definition();
            foreach (var clip in d.Clips)
                Assert.That(Config.TryResolve(Character, clip.Id, out _, out var settings, out var error), Is.True, error);
            Assert.That(entry.Combat.PhysicalAbility.ClipId, Is.EqualTo("physical_ability"));
            Assert.That(entry.Animations.Any(a => a.ClipId == "kick_spin"), Is.False);
            var required = CombatAnimationSet.RequiredClipIds(entry.Combat.ClipFor);
            Assert.That(required, Is.EquivalentTo(new[] { "idle_combat", "run", "jump", "punch_left", "punch_right", "kick_left", "kick_right", "physical_ability" }));
            var report = new List<string>();
            Assert.That(CharacterAtlasMigration.Apply(Clone(entry), d, report), Is.False, string.Join("\n", report));
            Assert.That(report, Is.EqualTo(new[] { $"{Character}: nothing to migrate" }), "No range of the migrated config hides frames.");
        }

        // The pre-#70 owner config against the v2 atlas: explicit rename + initialization, everything else reported, idempotent.
        [Test]
        public void MigrationRenamesTheAbilityInitializesNewClipsAndReportsWithoutResettingTuning()
        {
            var d = Definition();
            var curve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 2));
            var spin = Settings("kick_spin", 5, .5, 3, 4); spin.SpeedCurve = curve;
            var entry = new CharacterAnimationEntry
            {
                CharacterId = Character,
                Animations = new[]
                {
                    Settings("idle_combat", 5, .75), Settings("run", 4, .5), Settings("jump", 4, 1), Settings("punch_left", 5, 29 / 60.0, 2, 3),
                    Settings("punch_right", 5, 29 / 60.0, 2, 3), Settings("kick_left", 4, .5, 1, 2), Settings("kick_right", 4, .5, 1, 2), spin,
                },
                Combat = new CharacterCombatSettings(),
            };
            entry.Combat.PhysicalAbility.ClipId = "kick_spin";
            entry.Combat.PhysicalAbility.Damage = 17;
            var report = new List<string>();
            Assert.That(CharacterAtlasMigration.Apply(entry, d, report), Is.True);
            TestContext.WriteLine(string.Join("\n", report));

            Assert.That(entry.Combat.PhysicalAbility.ClipId, Is.EqualTo("physical_ability"));
            Assert.That(entry.Combat.PhysicalAbility.Damage, Is.EqualTo(17), "Combat values are kept.");
            var ability = entry.Animations.Single(a => a.ClipId == "physical_ability");
            Assert.That((ability.EndFrame, ability.TotalDurationSeconds, ability.PreparationEndFrame, ability.ActiveEndFrame), Is.EqualTo((7, .5, 3, 4)),
                "Slot T kept, whole new clip, phases from strike_pose [4].");
            Assert.That(ability.SpeedCurve.keys.Select(k => k.value), Is.EqualTo(new[] { 1f, 2f }), "Curve kept.");
            Assert.That(entry.Animations.Any(a => a.ClipId == "kick_spin"), Is.False);
            var block = entry.Animations.Single(a => a.ClipId == "block");
            Assert.That((block.EndFrame, block.TotalDurationSeconds, block.PreparationEndFrame), Is.EqualTo((3, .6, 0)), "JSON 100+150+200+150 ms.");
            Assert.That(entry.Animations.Single(a => a.ClipId == "skill_complex").TotalDurationSeconds, Is.EqualTo(1.1).Within(1e-9));
            Assert.That(entry.Animations.Single(a => a.ClipId == "run").EndFrame, Is.EqualTo(4), "A valid range is reported, never rewritten.");
            Assert.That(entry.Animations.Single(a => a.ClipId == "kick_left").PreparationEndFrame, Is.EqualTo(1));
            Assert.That(report, Has.Some.Contains("Combat.PhysicalAbility.ClipId: kick_spin → physical_ability"));
            Assert.That(report, Has.Some.Contains("animations.run: EndFrame 4 leaves local frames 5..5 unplayed (kept)"));
            Assert.That(report, Has.Some.Contains("animations.kick_left: EndFrame 4 leaves local frames 5..5 unplayed (kept)"));
            Assert.That(report.Count(r => r.Contains(": initialized")), Is.EqualTo(4), "block, dash, skill_light, skill_complex.");

            var errors = new List<string>();
            var set = CombatAnimationSet.Create(d, c => entry.Animations.FirstOrDefault(a => a.ClipId == c), Character, errors, null, entry.Combat.ClipFor);
            Assert.That(set, Is.Not.Null, string.Join("\n", errors));

            var before = JsonUtility.ToJson(entry);
            var again = new List<string>();
            Assert.That(CharacterAtlasMigration.Apply(entry, d, again), Is.False, "Repeated migration is idempotent.");
            Assert.That(JsonUtility.ToJson(entry), Is.EqualTo(before));
        }

        // Custom image position (owner's Sprite Editor shift, #70): a same-size rect shifted on the page moves the pivot, the
        // drawn pixels stay the manifest region; the automatic binder still needs the exact rect.
        [Test]
        public void ShiftedSpriteRectIsACustomImagePositionWithinHalfACanvas()
        {
            var d = Definition();
            d.TryGetFrameIndex("kick_left_003", out var index);
            var frame = d.Frames[index];
            var cell = new Rect(frame.Rect.x, 2016 - frame.Rect.y - 160, 192, 160);
            Assert.That(SpriteAtlasCatalog.TrySpriteCanvasOrigin(d, frame, cell, out var origin), Is.True);
            Assert.That(origin, Is.EqualTo(float2.zero));
            var shifted = new Rect(cell.x - 17, cell.y + 3, 192, 160);
            Assert.That(SpriteAtlasCatalog.TrySpriteCanvasOrigin(d, frame, shifted, out _), Is.False, "Automatic binding stays exact.");
            Assert.That(SpriteAtlasCatalog.TrySpriteCanvasOrigin(d, frame, shifted, out origin, customPosition: true), Is.True);
            Assert.That(origin, Is.EqualTo(new float2(-17, -3)), "Canvas Y-down offset of the shifted region.");
            Assert.That(SpriteAtlasCatalog.TrySpriteCanvasOrigin(d, frame, new Rect(cell.x - 97, cell.y, 192, 160), out _, customPosition: true), Is.False, "More than half a canvas.");
            Assert.That(SpriteAtlasCatalog.TrySpriteCanvasOrigin(d, frame, new Rect(cell.x, cell.y, 190, 160), out _, customPosition: true), Is.False, "Wrong size.");
        }

        // The installed Sprite pivots: feet line 136 for every frame, the owner's horizontal custom positions, rects on the cells.
        [Test]
        public void ImportedPivotsKeepTheFeetLineAndTheOwnersHorizontalPositions()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteAtlasCatalog>(CatalogPath);
            if (catalog.Pages.Count == 0 || catalog.Pages[0].Texture == null)
                Assert.Ignore("External atlas PNG is not installed (it is delivered outside Git).");
            var errors = new List<string>();
            var warnings = new List<string>();
            using var atlas = SpriteAtlas.Load(catalog, errors, warnings);
            Assert.That(atlas, Is.Not.Null, string.Join("\n", errors));
            Assert.That(warnings.Where(w => w.Contains("image pivots")), Is.Empty, "Every Sprite sits exactly on its cell.");
            var offsets = JsonUtility.FromJson<Offsets>("{\"Items\":" + ToArray(System.IO.File.ReadAllText(OffsetsPath)) + "}");
            var dx = offsets.Items.ToDictionary(o => o.Frame, o => o.Dx);
            Assert.That(catalog.ImagePivots.Count, Is.EqualTo(76));
            for (var i = 0; i < atlas.FrameCount; i++)
            {
                var id = atlas.Definition.Frames[i].Id;
                var pivot = atlas.ImagePivot(i);
                Assert.That(pivot.y, Is.EqualTo(136).Within(1e-3), id);
                Assert.That(pivot.x, Is.EqualTo(96 + dx[id]).Within(1e-3), id);
            }
        }

        [System.Serializable] class Offset { public string Frame; public int Dx; }
        [System.Serializable] class Offsets { public Offset[] Items; }

        static string ToArray(string json) => "[" + string.Join(",", json.Trim().Trim('{', '}').Split(',')
            .Select(p => p.Split(':')).Select(kv => $"{{\"Frame\":{kv[0].Trim()},\"Dx\":{kv[1].Trim()}}}")) + "]";

        // Policy: an atlas/config change applies on the next frame; a strike that started with the old windows keeps them in the
        // simulation and is drawn with the new clip's frames OF THE SAME PHASE only (install art between battles / Restart).
        [Test]
        public void AStrikeStartedWithTheOldArtWindowsDrawsOnlyFramesOfItsSimulatedPhase()
        {
            var d = Definition();
            var errors = new List<string>();
            CharacterAnimationSettings Project(string c) => Config.TryResolve(Character, c, out _, out var s, out _) ? s : null;
            var oldKick = Settings("kick_left", 4, .5, 1, 2); // the pre-#70 owner kick: 12/6/12 ticks
            Assert.That(CombatAnimationSet.TryCreateStrikeSchedule(d, c => c == "kick_left" ? oldKick : Project(c), Character, errors, null, out var old), Is.True,
                string.Join("\n", errors));
            var timing = old.Foot1;
            Assert.That((timing.WindupTicks, timing.ActiveTicks, timing.RecoveryTicks), Is.EqualTo((12, 6, 12)));
            var set = CombatAnimationSet.Create(d, Project, Character, errors, null);
            Assert.That(set, Is.Not.Null, string.Join("\n", errors));
            var settings = Project("kick_left");
            d.TryGetClipIndex("kick_left", out var clipIndex);
            var frames = d.Clips[clipIndex].Frames.ToList();
            var state = new CombatAttackState { Kind = CombatAttackKind.Foot1, StartTick = 100, Timing = timing };
            for (uint e = 0; e < timing.End; e++)
            {
                var local = frames.IndexOf((int)set.FrameFor(100 + e, 0, new CombatMotion { Grounded = true }, state));
                var phase = local <= settings.PreparationEndFrame ? CombatAttackPhase.Windup
                    : local <= settings.ActiveEndFrame ? CombatAttackPhase.Active : CombatAttackPhase.Recovery;
                Assert.That(phase, Is.EqualTo(timing.PhaseAt(e)), $"tick {e}: local frame {local}");
            }
        }
    }
}
