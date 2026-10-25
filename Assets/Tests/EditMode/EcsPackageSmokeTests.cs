using NUnit.Framework;
using Unity.Entities;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MLFLegends.Tests.EditMode
{
    // Deliberately test-local. Combat entity data belongs to Stage 01, not the bootstrap.
    public struct SmokeValue : IComponentData
    {
        public int Value;
    }

    public class EcsPackageSmokeTests
    {
        [Test]
        public void SeparateEntitiesRetainTheirOwnDataAndCanBeDestroyed()
        {
            using (var world = new World("MLF Legends EditMode smoke"))
            {
                var manager = world.EntityManager;

                var first = manager.CreateEntity(typeof(SmokeValue));
                var second = manager.CreateEntity(typeof(SmokeValue));

                manager.SetComponentData(first, new SmokeValue { Value = 17 });
                manager.SetComponentData(second, new SmokeValue { Value = 29 });

                Assert.That(manager.GetComponentData<SmokeValue>(first).Value, Is.EqualTo(17));
                Assert.That(manager.GetComponentData<SmokeValue>(second).Value, Is.EqualTo(29));

                manager.DestroyEntity(first);

                Assert.That(manager.Exists(first), Is.False);
                Assert.That(manager.Exists(second), Is.True);
                Assert.That(manager.GetComponentData<SmokeValue>(second).Value, Is.EqualTo(29));
            }
        }

        [Test]
        public void UniversalThreeDimensionalRenderPipelineIsConfigured()
        {
            Assert.That(GraphicsSettings.defaultRenderPipeline,
                Is.InstanceOf<UniversalRenderPipelineAsset>(),
                "Stage 00 expects a Universal 3D (URP) pipeline asset.");
        }
    }
}
