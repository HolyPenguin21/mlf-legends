using System.IO;
using MLFLegends.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    // Stage 03 tuning: a config saved before StartFrame/EndFrame (legacy "FrameCount = first N frames") loads with the same
    // played range, duration and curve. Imported through the real AssetDatabase/YAML path, then deleted.
    public class CharacterAnimationMigrationTests
    {
        const string TempPath = "Assets/MLFLegendsLegacyConfigMigration.asset";

        // The config script GUID (CharacterAnimationConfig.cs.meta) and a pre-range entry: FrameCount 4, T 0.75, curve 1 → 2.
        const string LegacyYaml = @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 0}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: e0cd4284045e577f9f5347eaa71fee0f, type: 3}
  m_Name: MLFLegendsLegacyConfigMigration
  m_EditorClassIdentifier:
  m_Characters:
  - CharacterId: legacy
    AtlasCatalog: {fileID: 0}
    Animations:
    - ClipId: punch_left
      FrameCount: 4
      TotalDurationSeconds: 0.75
      SpeedCurve:
        serializedVersion: 2
        m_Curve:
        - serializedVersion: 3
          time: 0
          value: 1
          inSlope: 1
          outSlope: 1
          tangentMode: 0
          weightedMode: 0
          inWeight: 0
          outWeight: 0
        - serializedVersion: 3
          time: 1
          value: 2
          inSlope: 1
          outSlope: 1
          tangentMode: 0
          weightedMode: 0
          inWeight: 0
          outWeight: 0
        m_PreInfinity: 2
        m_PostInfinity: 2
        m_RotationOrder: 4
";

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(TempPath);

        [Test]
        public void LegacyFrameCountBecomesTheSameInclusiveRangeAndKeepsDurationAndCurve()
        {
            // Unity writes YAML with LF; the verbatim string follows the checkout's line endings.
            File.WriteAllText(TempPath, LegacyYaml.Replace("\r\n", "\n"));
            AssetDatabase.ImportAsset(TempPath, ImportAssetOptions.ForceSynchronousImport);
            var config = AssetDatabase.LoadAssetAtPath<CharacterAnimationConfig>(TempPath);
            Assert.That(config, Is.Not.Null);
            var settings = config.Characters[0].Animations[0];
            Assert.That(settings.StartFrame, Is.Zero);
            Assert.That(settings.EndFrame, Is.EqualTo(3), "First 4 frames = local 0..3.");
            Assert.That(settings.FrameCount, Is.EqualTo(4));
            Assert.That(settings.TotalDurationSeconds, Is.EqualTo(0.75));
            Assert.That(settings.SpeedCurve.keys.Length, Is.EqualTo(2));
            Assert.That(settings.SpeedCurve.keys[1].value, Is.EqualTo(2f));
            Assert.That(settings.SpeedCurve.keys[0].outTangent, Is.EqualTo(1f));
            Assert.That((settings.PreparationEndFrame, settings.ActiveEndFrame), Is.EqualTo((0, 0)),
                "Phases did not exist before: left unset, so validation reports them instead of guessing.");

            // Saved again: the range survives a round trip and the legacy count does not come back.
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            var saved = File.ReadAllText(TempPath);
            Assert.That(saved, Does.Contain("EndFrame: 3"));
            Assert.That(saved, Does.Not.Contain("FrameCount: 4"));
            Assert.That(saved, Does.Contain("m_LegacyFrameCount: 0"));
            AssetDatabase.ImportAsset(TempPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            config = AssetDatabase.LoadAssetAtPath<CharacterAnimationConfig>(TempPath);
            Assert.That(config.Characters[0].Animations[0].EndFrame, Is.EqualTo(3), "Not shifted again on the next load.");
        }
    }
}
