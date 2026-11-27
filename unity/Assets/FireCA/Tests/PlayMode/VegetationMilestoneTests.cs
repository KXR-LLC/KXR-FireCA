using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FireCA.Tests
{
    /// <summary>
    /// PlayMode tests of milestone M8: the Vegetation on the GPU (docs/05-milestones.md and docs/decisions.md,
    /// decision D33).
    /// </summary>
    public sealed class VegetationMilestoneTests
    {
        const string RealScenario = "south-canyon";

        FireCAApp _app;
        Vegetation _vegetation;
        CameraRig _rig;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("FireCA", LoadSceneMode.Single);
            yield return null;
            yield return null;
            _app = FireCAApp.Instance;
            Assert.IsNotNull(_app);
            _vegetation = Object.FindFirstObjectByType<Vegetation>();
            _rig = Object.FindFirstObjectByType<CameraRig>();
            Assert.IsNotNull(_vegetation, "The scene has no Vegetation component.");
            Assert.IsNotNull(_rig);
            _app.WindNestEnabled = false;
            Assert.IsTrue(_app.LoadScenario(RealScenario), _app.LastError);
            yield return null;
            yield return null;
        }

        Vector3 IgnitionPoint()
        {
            var ignition = _app.Scenario.Data.ignitions[0];
            return new Vector3(ignition.xM, _app.SampleElevation(ignition.xM, ignition.zM), ignition.zM);
        }

        IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator Criteria7And8_LevelOfDetailFollowsTheDistance_MemoryIsInRange()
        {
            Assert.Greater(_vegetation.PlantCount, 100000);

            // Far: the Overview preset is kilometers from the Plants.
            _rig.ApplyPreset(0);
            yield return Frames(3);
            _vegetation.ReadCountsNow();
            int farNear = _vegetation.DrawnPerLod[0], farTotal = _vegetation.DrawnTotal;
            Assert.AreEqual(0, farNear, "Plants at the full level of detail from the Overview preset");
            Assert.Greater(_vegetation.DrawnPerLod[2], 1000);
            Assert.LessOrEqual(farTotal, _vegetation.PlantCount);

            // Near: 60 m from the trees at the first ignition.
            Vector3 at = IgnitionPoint();
            _rig.LookFrom(at + new Vector3(45f, 25f, 45f), at + Vector3.up * 5f);
            yield return Frames(3);
            _vegetation.ReadCountsNow();
            Assert.Greater(_vegetation.DrawnPerLod[0], 0, "Plants at the full level of detail near the camera");
            Assert.Greater(_vegetation.DrawnPerLod[1], _vegetation.DrawnPerLod[0], "The middle level holds more Plants than the full level");
            Assert.Greater(_vegetation.DrawnPerLod[2], _vegetation.DrawnPerLod[1], "The far level holds the most Plants");
            Debug.Log($"[FireCA test] Vegetation: {_vegetation.PlantCount} Plants. Overview: {farTotal} drawn. Near view: " +
                $"{_vegetation.DrawnPerLod[0]}, {_vegetation.DrawnPerLod[1]}, {_vegetation.DrawnPerLod[2]} at levels 0, 1, 2.");

            // The toggle and the density.
            _vegetation.Density = 0.5f;
            yield return Frames(2);
            int full = _vegetation.DrawnTotal;
            _vegetation.ReadCountsNow();
            Assert.That(_vegetation.DrawnTotal, Is.InRange(0.4f * full, 0.6f * full), "Plants drawn at a density of 0.5");
            _vegetation.Density = 1f;
            _vegetation.Visible = false;
            yield return Frames(2);
            Assert.AreEqual(0, _vegetation.DrawnTotal);
            _vegetation.Visible = true;

            float megabytes = _vegetation.TotalBytes / (1024f * 1024f);
            Debug.Log($"[FireCA test] Vegetation memory: {megabytes:F1} MB.");
            Assert.That(megabytes, Is.InRange(10f, 200f));
        }

        [UnityTest]
        public IEnumerator Criterion9_PlantsInTheFireAreInTheListOfBurningPlants()
        {
            Vector3 at = IgnitionPoint();
            _rig.LookFrom(at + new Vector3(80f, 50f, 80f), at);
            yield return Frames(3);
            _vegetation.ReadCountsNow();
            Assert.AreEqual(0, _vegetation.BurningDrawn, "burning Plants before the ignition");

            // A fire of 60 m radius around the first ignition. The Sim Clock runs for 20 simulated seconds or more.
            _app.Spread.Ignite(new Vector2(at.x, at.z), 60f, _app.Clock.Seconds);
            double start = _app.Clock.Seconds;
            _app.Clock.SetTimeScale(60f);
            _app.Clock.Playing = true;
            for (int i = 0; i < 600 && _app.Clock.Seconds < start + 20.0; i++) yield return null;
            _app.Clock.Playing = false;
            Assert.GreaterOrEqual(_app.Clock.Seconds, start + 20.0, "The Sim Clock did not run.");
            yield return Frames(3);
            _vegetation.ReadCountsNow();
            int burning = _vegetation.BurningDrawn;
            Assert.Greater(burning, 20, "burning Plants in a fire of 60 m radius");

            // With no effects, the list is empty.
            _vegetation.EffectsVisible = false;
            yield return Frames(2);
            _vegetation.ReadCountsNow();
            Assert.AreEqual(0, _vegetation.BurningDrawn);
            _vegetation.EffectsVisible = true;
            Debug.Log($"[FireCA test] Vegetation: {burning} burning Plants in a fire of 60 m radius.");
        }
    }
}
