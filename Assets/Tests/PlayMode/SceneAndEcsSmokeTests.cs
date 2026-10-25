using System.Collections;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MLFLegends.Tests.PlayMode
{
    // Does not spawn real fighters: only verifies the existing Unity scene and Entities API.
    public struct PlayModeSmokeTag : IComponentData
    {
    }

    public class SceneAndEcsSmokeTests
    {
        [UnityTest]
        public IEnumerator SampleSceneLoadsAndEcsWorldSurvivesAFrame()
        {
            SceneManager.LoadScene("SampleScene", LoadSceneMode.Single);
            yield return null;

            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("SampleScene"),
                "Keep Assets/Scenes/SampleScene.unity enabled in Build Settings.");

            using (var world = new World("MLF Legends PlayMode smoke"))
            {
                var manager = world.EntityManager;
                var entity = manager.CreateEntity(typeof(PlayModeSmokeTag));

                Assert.That(manager.Exists(entity), Is.True);
                yield return null;
                Assert.That(manager.Exists(entity), Is.True);

                manager.DestroyEntity(entity);
                Assert.That(manager.Exists(entity), Is.False);
            }
        }
    }
}
