using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FireCA.Tests
{
    /// <summary>PlayMode tests of milestone M5: the scripted demo run and the cell updates readout.</summary>
    public sealed class DemoMilestoneTests
    {
        FireCAApp _app;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("FireCA", LoadSceneMode.Single);
            yield return null;
            yield return null;
            _app = FireCAApp.Instance;
            Assert.IsNotNull(_app);
        }

        [Test]
        public void DemoRun_HasTheFourBeatsOfTheMilestoneDocument()
        {
            DemoRun.Beat[] beats = DemoRun.Beats;
            Assert.AreEqual(4, beats.Length);
            // Beat ends: 13:00, 13:40, 14:30, and 17:00, with a start at 13:00 solar time.
            Assert.AreEqual(0.0, beats[0].EndSimSeconds);
            Assert.AreEqual(40 * 60.0, beats[1].EndSimSeconds);
            Assert.AreEqual(90 * 60.0, beats[2].EndSimSeconds);
            Assert.AreEqual(240 * 60.0, beats[3].EndSimSeconds);
            Assert.AreEqual(0f, beats[0].TimeScale, "Beat 1 runs with the Sim Clock paused.");
            Assert.AreEqual(60f, beats[1].TimeScale);
            Assert.AreEqual(60f, beats[2].TimeScale);
            Assert.AreEqual(240f, beats[3].TimeScale);
            string[] cameras = { "Overview", "Canyon Mouth", "Chute", "Top Down" };
            for (int i = 0; i < 4; i++) Assert.AreEqual(cameras[i], CameraRig.PresetNames[beats[i].CameraPreset]);
            Assert.AreEqual("Arrival time", beats[3].Overlay);
            Assert.IsNotNull(Overlays.Find(beats[3].Overlay));
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator DemoRun_StartsWithOneCall_AndRunsTheBeatsInOrder()
        {
            Assert.IsTrue(_app.LoadScenario("test-canyon-512"), _app.LastError);
            var demo = Object.FindFirstObjectByType<DemoRun>();
            var rig = Object.FindFirstObjectByType<CameraRig>();
            Assert.IsNotNull(demo, "The scene has no DemoRun component.");
            demo.IntroSeconds = 0.5f;
            Assert.AreEqual(DemoRun.State.Idle, demo.CurrentState);

            demo.StartDemo();
            Assert.AreEqual(DemoRun.State.SpinUp, demo.CurrentState);
            Assert.AreEqual(0.0, _app.Clock.Seconds, "The demo run must reset the Scenario.");
            int frames = 0;
            while (demo.CurrentState == DemoRun.State.SpinUp && frames++ < 3000) yield return null;

            // Beat 1: paused, Overview, slow orbit.
            Assert.AreEqual(DemoRun.State.Running, demo.CurrentState);
            Assert.AreEqual(0, demo.BeatIndex);
            Assert.IsFalse(_app.Clock.Playing);
            float yaw = rig.transform.eulerAngles.y;
            yield return null;
            yield return null;
            Assert.AreNotEqual(yaw, rig.transform.eulerAngles.y, "The camera must orbit in beat 1.");

            // Beat 2: 60x, Canyon Mouth.
            frames = 0;
            while (demo.BeatIndex == 0 && frames++ < 3000) yield return null;
            Assert.AreEqual(1, demo.BeatIndex);
            Assert.IsTrue(_app.Clock.Playing);
            Assert.AreEqual(60f, _app.Clock.TimeScale);
            Assert.IsNull(_app.Overlays.Selected);

            // The beats follow the Sim Clock. A higher time scale shortens the wait of the test.
            _app.Clock.SetTimeScale(1000f);
            frames = 0;
            while (demo.BeatIndex == 1 && frames++ < 20000) yield return null;
            Assert.AreEqual(2, demo.BeatIndex);
            Assert.GreaterOrEqual(_app.Clock.Seconds, 40 * 60.0);
            Assert.AreEqual(60f, _app.Clock.TimeScale, "Beat 3 must set the time scale.");

            _app.Clock.SetTimeScale(1000f);
            frames = 0;
            while (demo.BeatIndex == 2 && frames++ < 20000) yield return null;
            Assert.AreEqual(3, demo.BeatIndex);
            Assert.AreEqual(240f, _app.Clock.TimeScale);
            Assert.AreEqual("Arrival time", _app.Overlays.Selected.Name);
            Assert.IsFalse(_app.SmokeVisible, "Beat 4 hides the smoke, so the Arrival time bands stay visible.");

            // The cell updates readout counts Fire Grid cells per Fire step and Wind Grid cells per pressure iteration.
            for (int i = 0; i < 120; i++) yield return null;
            StepScheduler scheduler = _app.Scheduler;
            GridSpec grid = _app.Scenario.Grid;
            double expected = scheduler.FireStepsPerSecond * grid.FireCellTotal
                + scheduler.WindStepsPerSecond * grid.WindCellTotal * _app.Wind.PressureIterations;
            Assert.Greater(scheduler.CellUpdatesPerSecond, 0.0);
            Assert.AreEqual(expected, scheduler.CellUpdatesPerSecond, expected * 1e-6);

            _app.Clock.SetTimeScale(1000f);
            frames = 0;
            while (demo.CurrentState == DemoRun.State.Running && frames++ < 40000) yield return null;
            Assert.AreEqual(DemoRun.State.Done, demo.CurrentState);
            Assert.GreaterOrEqual(_app.Clock.Seconds, 240 * 60.0);
            Assert.IsFalse(_app.Clock.Playing, "The demo run must pause the Sim Clock at the end.");

            demo.StopDemo();
            Assert.AreEqual(DemoRun.State.Idle, demo.CurrentState);
            Assert.IsTrue(_app.SmokeVisible);
        }
    }
}
