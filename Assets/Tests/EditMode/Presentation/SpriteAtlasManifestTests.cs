using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    // Stage 03 (#38): atlas manifest v1 parsing/validation (Docs/SPRITE_ATLAS_CONTRACT.md). Pure data, no GPU.
    public class SpriteAtlasManifestTests
    {
        static string ExamplePath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Docs", "SpriteAtlas", "mage_female_01.example.json"));

        static SpriteAtlasDefinition ParseValid(string json)
        {
            var errors = new List<string>();
            var definition = SpriteAtlasManifest.Parse(json, errors);
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(definition, Is.Not.Null);
            return definition;
        }

        static List<string> ParseInvalid(string json)
        {
            var errors = new List<string>();
            Assert.That(SpriteAtlasManifest.Parse(json, errors), Is.Null, "A broken manifest yields no definition at all.");
            Assert.That(errors, Is.Not.Empty);
            return errors;
        }

        /// <summary>The fixture manifest with one exact text replacement (the original text must exist).</summary>
        static string Mutate(string from, string to)
        {
            var json = TechnicalAtlasFixture.ManifestJson;
            Assert.That(json.Contains(from), $"Mutation anchor not found: {from}");
            return json.Replace(from, to);
        }

        [Test]
        public void TheDocumentedExampleIsAValidManifest()
        {
            Assert.That(File.Exists(ExamplePath), ExamplePath);
            var definition = ParseValid(File.ReadAllText(ExamplePath));
            Assert.That(definition.CharacterId, Is.EqualTo("mage_female_01"));
            Assert.That(definition.SourceSize, Is.EqualTo(new int2(192, 192)));
            Assert.That(definition.NominalHeightPx, Is.EqualTo(128f));
            Assert.That(definition.Pivot, Is.EqualTo(new float2(96f, 160f)));
            Assert.That(definition.Pages.Count, Is.EqualTo(1));
            Assert.That(definition.Frames.Count, Is.EqualTo(6));
            Assert.That(definition.Clips.Count, Is.EqualTo(1));
            Assert.That(definition.TryGetClipIndex("idle_combat", out var idle), Is.True);
            Assert.That(definition.Clips[idle].Frames.Count, Is.EqualTo(6));
            Assert.That(definition.Clips[idle].Loop, Is.True);
            Assert.That(definition.Clips[idle].HoldLast, Is.False);
            Assert.That(definition.Frames.All(f => !f.HasCastOrigin), Is.True);
            Assert.That(definition.Transitions, Is.Empty);
        }

        [Test]
        public void TheTechnicalFixtureHasTwoPagesOfDifferentSizesAndSharedFrames()
        {
            var definition = ParseValid(TechnicalAtlasFixture.ManifestJson);
            Assert.That(definition.Pages.Select(p => p.Size), Is.EqualTo(new[] { new int2(128, 128), new int2(96, 160) }));
            Assert.That(definition.Frames.Select(f => f.Page), Is.EqualTo(new[] { 0, 0, 1 }));
            Assert.That(definition.Frames[0].Rect, Is.EqualTo(new int4(4, 4, 40, 80)));
            Assert.That(definition.Frames[0].TrimOffset, Is.EqualTo(new int2(70, 80)));
            Assert.That(definition.TryGetClipFrame("tech_cast", 1, out var a), Is.True);
            Assert.That(definition.TryGetClipFrame("tech_idle", 0, out var b), Is.True);
            Assert.That(a, Is.EqualTo(b), "tech_a is used by both clips.");
            Assert.That(definition.Clips[1].Markers.Keys, Is.EquivalentTo(new[] { "release_pose", "return" }));
            Assert.That(definition.TryGetClipFrame("tech_cast", 2, out _), Is.False);
            Assert.That(definition.TryGetFrameIndex("missing", out _), Is.False);
            Assert.That(definition.Clips[0].DurationsMs, Is.EqualTo(new[] { 100f, 62.5f }), "Fractional JSON durations remain valid metadata.");
            var cast = definition.Clips[1];
            Assert.That(cast.Loop, Is.False);
            Assert.That(cast.HoldLast, Is.True);
            Assert.That(cast.Markers["release_pose"], Is.EqualTo(new[] { 0 }));
            Assert.That(cast.Markers["return"], Is.EqualTo(new[] { 1 }));
            var hidden = definition.Frames[1];
            Assert.That(hidden.HandLeft.Visible, Is.False);
            Assert.That(hidden.HandLeft.Position, Is.EqualTo(new float2(100, 140)));
            Assert.That(hidden.HandRight.Layer, Is.EqualTo(SpriteAnchorLayer.Front));
            Assert.That(hidden.HasCastOrigin, Is.True);
            Assert.That(definition.Frames[0].HasCastOrigin, Is.False);
            var transition = definition.Transitions.Single();
            Assert.That(definition.Clips[transition.FromClip].Id, Is.EqualTo("tech_idle"));
            Assert.That(transition.ExitFrame, Is.EqualTo(1));
            Assert.That(definition.Clips[transition.ToClip].Id, Is.EqualTo("tech_cast"));
            Assert.That(transition.EntryFrame, Is.Zero);
        }

        [Test]
        public void SourceSizeAndPivotAreDataNotConstants()
        {
            var json = Mutate("\"source_size\": [192, 192]", "\"source_size\": [256, 320]")
                .Replace("\"pivot\": [96, 160]", "\"pivot\": [128.5, 300]")
                .Replace("\"nominal_height_px\": 96", "\"nominal_height_px\": 150");
            var definition = ParseValid(json);
            Assert.That(definition.SourceSize, Is.EqualTo(new int2(256, 320)));
            Assert.That(definition.Pivot, Is.EqualTo(new float2(128.5f, 300f)));
            Assert.That(definition.NominalHeightPx, Is.EqualTo(150f));
        }

        [Test]
        public void MetaIsOptionalAndIgnored()
        {
            ParseValid(Mutate("\"meta\": { \"generator\": \"TechnicalAtlasFixture\", \"note\": \"technical test atlas, not a character\" },", ""));
        }

        [Test]
        public void EmptyClipsAndTransitionsAreValid()
        {
            var json = TechnicalAtlasFixture.ManifestJson;
            var clipsStart = json.IndexOf("\"clips\"");
            var definition = ParseValid(json.Substring(0, clipsStart) + "\"clips\": {}, \"compatible_transitions\": [] }");
            Assert.That(definition.Clips, Is.Empty, "The runtime requires no clip.");
        }

        public static IEnumerable<TestCaseData> Violations()
        {
            TestCaseData Case(string name, string from, string to, string expected) =>
                new TestCaseData(from, to, expected).SetName("Rejects_" + name);

            yield return Case("UnknownSchemaVersion", "\"schema_version\": 1", "\"schema_version\": 2", "unsupported schema_version 2");
            yield return Case("StringSchemaVersion", "\"schema_version\": 1", "\"schema_version\": \"1\"", "unsupported schema_version");
            yield return Case("DuplicateTopLevelKey", "\"character_id\": \"technical_fixture\",", "\"character_id\": \"technical_fixture\", \"character_id\": \"x\",", "character_id: duplicate key");
            yield return Case("DuplicateFrameId", "\"tech_b\": {", "\"tech_a\": {", "frames.tech_a: duplicate key");
            yield return Case("DuplicateClipId", "\"tech_cast\": {", "\"tech_idle\": {", "clips.tech_idle: duplicate key");
            yield return Case("DuplicateAnchorKey", "\"cast_origin\": null", "\"cast_origin\": null, \"cast_origin\": null", "frames.tech_a.anchors.cast_origin: duplicate key");
            yield return Case("UnknownKeyTypo", "\"trim_offset\": [70, 80]", "\"trim_ofset\": [70, 80]", "frames.tech_a.trim_ofset: unknown key");
            yield return Case("MissingTrim", ", \"trim_offset\": [70, 80]", "", "frames.tech_a.trim_offset: is required");
            yield return Case("EmptyCharacterId", "\"character_id\": \"technical_fixture\"", "\"character_id\": \"\"", "character_id: must be a non-empty string");
            yield return Case("ZeroSourceSize", "\"source_size\": [192, 192]", "\"source_size\": [0, 192]", "source_size[0]: 0 is outside");
            yield return Case("NominalAboveSource", "\"nominal_height_px\": 96", "\"nominal_height_px\": 193", "nominal_height_px");
            yield return Case("PivotOutside", "\"pivot\": [96, 160]", "\"pivot\": [96, 200]", "pivot: [96, 200] lies outside");
            yield return Case("AuthoredLeft", "\"authored_facing\": \"right\"", "\"authored_facing\": \"left\"", "authored_facing: \"left\" is not supported");
            yield return Case("PageTooLarge", "\"width\": 128, \"height\": 128", "\"width\": 4096, \"height\": 128", "pages[0].width: 4096 is outside");
            yield return Case("DuplicatePageFile", "\"technical_page1.png\"", "\"technical_page0.png\"", "pages[1].file: duplicate page file");
            yield return Case("MissingPage", "\"page\": 1,", "\"page\": 5,", "frames.tech_c.page: page 5 does not exist");
            yield return Case("FractionalRect", "\"rect\": [4, 4, 40, 80]", "\"rect\": [4.5, 4, 40, 80]", "frames.tech_a.rect[0]: must be an integer, got 4.5");
            yield return Case("ExponentRect", "\"rect\": [4, 4, 40, 80]", "\"rect\": [4, 4, 4e1, 80]", "frames.tech_a.rect[2]: must be an integer");
            yield return Case("NegativeRect", "\"rect\": [4, 4, 40, 80]", "\"rect\": [-4, 4, 40, 80]", "frames.tech_a.rect[0]: -4 is outside");
            yield return Case("ZeroWidthRect", "\"rect\": [4, 4, 40, 80]", "\"rect\": [4, 4, 0, 80]", "frames.tech_a.rect[2]: 0 is outside");
            yield return Case("RectOutsidePage", "\"rect\": [8, 8, 50, 100]", "\"rect\": [8, 8, 90, 100]", "frames.tech_c.rect: [8,8,90,100] exceeds page 1 (96x160)");
            yield return Case("RectWithoutExtrusionRoom", "\"rect\": [4, 4, 40, 80]", "\"rect\": [1, 4, 40, 80]", "leaves no room for 2 px extrusion");
            yield return Case("RectsOverlap", "\"rect\": [52, 4, 60, 40]", "\"rect\": [30, 4, 60, 40]", "frames.tech_b.rect: overlaps frame \"tech_a\"");
            yield return Case("ExtrusionsOverlap", "\"rect\": [52, 4, 60, 40]", "\"rect\": [47, 4, 60, 40]", "overlaps frame \"tech_a\"");
            yield return Case("NegativeTrim", "\"trim_offset\": [70, 80]", "\"trim_offset\": [-1, 80]", "frames.tech_a.trim_offset[0]: -1 is outside");
            yield return Case("TrimOutsideSource", "\"trim_offset\": [70, 80]", "\"trim_offset\": [160, 80]", "frames.tech_a.trim_offset: [160,80] + size [40,80] exceeds source_size 192x192");
            yield return Case("NaNIsNotJson", "\"position\": [72, 120]", "\"position\": [NaN, 120]", "is not valid JSON");
            yield return Case("InfiniteNumber", "\"position\": [72, 120]", "\"position\": [1e400, 120]", "frames.tech_a.anchors.hand_left.position[0]: 1e400 is not finite");
            yield return Case("ZeroDirection", "\"direction\": [-1, 0], \"layer\": \"back\", \"visible\": true }", "\"direction\": [0, 0], \"layer\": \"back\", \"visible\": true }", "direction: must not be [0, 0]");
            yield return Case("BadLayer", "\"layer\": \"back\", \"visible\": false", "\"layer\": \"middle\", \"visible\": false", "frames.tech_b.anchors.hand_left.layer: \"middle\" must be");
            yield return Case("VisibleNotBool", "\"layer\": \"back\", \"visible\": false", "\"layer\": \"back\", \"visible\": \"no\"", "frames.tech_b.anchors.hand_left.visible: must be true or false");
            yield return Case("MissingHand", "\"hand_left\": { \"position\": [35, 100], \"direction\": [-1, 0], \"layer\": \"back\", \"visible\": true },", "", "frames.tech_c.anchors.hand_left: is required");
            yield return Case("MissingCastOrigin", "\"cast_origin\": null", "\"cast_origin_x\": null", "frames.tech_a.anchors.cast_origin: is required");
            yield return Case("UnknownClipFrame", "{ \"id\": \"tech_c\", \"duration_ms\": 150 }", "{ \"id\": \"tech_z\", \"duration_ms\": 150 }", "clips.tech_cast.frames[0].id: frame \"tech_z\" does not exist");
            yield return Case("ZeroDuration", "\"duration_ms\": 150", "\"duration_ms\": 0", "clips.tech_cast.frames[0].duration_ms: 0 must be > 0");
            yield return Case("NegativeDuration", "\"duration_ms\": 62.5", "\"duration_ms\": -62.5", "must be > 0");
            yield return Case("LoopAndHoldLast", "\"loop\": false, \"hold_last\": true", "\"loop\": true, \"hold_last\": true", "clips.tech_cast: loop and hold_last must not both be true");
            yield return Case("EmptyClipFrames", "\"frames\": [ { \"id\": \"tech_a\", \"duration_ms\": 100 }, { \"id\": \"tech_b\", \"duration_ms\": 62.5 } ]", "\"frames\": []", "clips.tech_idle.frames: must contain at least one frame");
            yield return Case("MarkerOutOfRange", "\"return\": [1]", "\"return\": [2]", "clips.tech_cast.markers.return[0]: 2 is outside [0, 1]");
            yield return Case("MarkerNotArray", "\"return\": [1]", "\"return\": 1", "clips.tech_cast.markers.return: must be an array");
            yield return Case("MarkerEmptyArray", "\"return\": [1]", "\"return\": []", "must list at least one local frame index");
            yield return Case("MarkerRepeat", "\"return\": [1]", "\"return\": [1, 1]", "repeats local index 1");
            yield return Case("TransitionUnknownClip", "\"to_clip\": \"tech_cast\"", "\"to_clip\": \"tech_jump\"", "compatible_transitions[0].to_clip: clip \"tech_jump\" does not exist");
            yield return Case("TransitionIndexOutOfRange", "\"exit_frame\": 1", "\"exit_frame\": 2", "compatible_transitions[0].exit_frame: 2 is outside [0, 1]");
            yield return Case("DuplicateTransition", "{ \"from_clip\": \"tech_idle\", \"exit_frame\": 1, \"to_clip\": \"tech_cast\", \"entry_frame\": 0 }", "{ \"from_clip\": \"tech_idle\", \"exit_frame\": 1, \"to_clip\": \"tech_cast\", \"entry_frame\": 0 }, { \"from_clip\": \"tech_idle\", \"exit_frame\": 1, \"to_clip\": \"tech_cast\", \"entry_frame\": 0 }", "compatible_transitions[1]: duplicates compatible_transitions[0]");
            yield return Case("TrailingComma", "\"entry_frame\": 0 }", "\"entry_frame\": 0 },", "is not valid JSON");
        }

        [TestCaseSource(nameof(Violations))]
        public void ViolationsAreRejectedWithTheFieldNamed(string from, string to, string expected)
        {
            var errors = ParseInvalid(Mutate(from, to));
            Assert.That(errors.Any(e => e.Contains(expected)), $"Expected an error containing '{expected}', got:\n{string.Join("\n", errors)}");
        }

        [Test]
        public void EmptyPagesAreRejected()
        {
            var json = Regex.Replace(TechnicalAtlasFixture.ManifestJson, @"""pages"":\s*\[[^\]]*\]", "\"pages\": []");
            Assert.That(json, Does.Contain("\"pages\": []"));
            var errors = ParseInvalid(json);
            Assert.That(errors, Has.Some.Contains("pages: must contain at least one page"));
        }

        [Test]
        public void AUtf8ByteOrderMarkIsAccepted()
        {
            ParseValid((char)0xFEFF + TechnicalAtlasFixture.ManifestJson);
        }

        [TestCase(" x")]
        [TestCase(" {}")]
        [TestCase(" }")]
        public void ContentAfterTheRootObjectIsRejected(string tail)
        {
            ParseInvalid(TechnicalAtlasFixture.ManifestJson + tail);
        }

        [Test]
        public void CommentsAreNotJson()
        {
            ParseInvalid(TechnicalAtlasFixture.ManifestJson.Replace("\"schema_version\": 1,", "// exporter note\n  \"schema_version\": 1,"));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("[1, 2]")]
        [TestCase("{ \"schema_version\": 1")]
        [TestCase("not json")]
        public void NonManifestTextIsRejected(string json)
        {
            ParseInvalid(json);
        }

        [Test]
        public void ManyErrorsAreReportedButBounded()
        {
            var json = TechnicalAtlasFixture.ManifestJson.Replace("\"visible\": true", "\"visible\": 1").Replace("\"visible\": false", "\"visible\": 0");
            var errors = ParseInvalid(json);
            Assert.That(errors.Count(e => e.Contains("visible: must be true or false")), Is.EqualTo(7), "Every bad field is named, not only the first.");
            var many = new List<string>();
            var frames = string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"f{i}\": {{ \"page\": 9 }}"));
            Assert.That(SpriteAtlasManifest.Parse(TechnicalAtlasFixture.ManifestJson.Substring(0, TechnicalAtlasFixture.ManifestJson.IndexOf("\"frames\""))
                + $"\"frames\": {{ {frames} }}, \"clips\": {{}}, \"compatible_transitions\": [] }}", many), Is.Null);
            Assert.That(many.Count, Is.EqualTo(SpriteAtlasManifest.MaxErrors), "Diagnostics are capped, not spammed.");
        }
    }
}
