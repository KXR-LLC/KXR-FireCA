using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FireCA.Tests
{
    /// <summary>
    /// PlayMode tests of milestone M9: the Tool Bar, the Control Panel with tabs, the Stats HUD with tabs, and the
    /// orbit tool (docs/05-milestones.md and docs/decisions.md, decision D35).
    /// </summary>
    public sealed class InterfaceMilestoneTests
    {
        FireCAApp _app;
        CameraRig _rig;
        ControlPanel _panel;
        StatsHud _hud;
        VisualElement _root;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("FireCA", LoadSceneMode.Single);
            yield return null;
            yield return null;
            _app = FireCAApp.Instance;
            Assert.IsNotNull(_app);
            _rig = Object.FindFirstObjectByType<CameraRig>();
            _panel = Object.FindFirstObjectByType<ControlPanel>();
            _hud = Object.FindFirstObjectByType<StatsHud>();
            Assert.IsNotNull(_rig);
            Assert.IsNotNull(_panel);
            Assert.IsNotNull(_hud);
            _root = _panel.GetComponent<UIDocument>().rootVisualElement;
        }

        // ---- criterion 1 ----

        [UnityTest]
        public IEnumerator Scene_LoadsSouthCanyonAtStartup()
        {
            Assert.IsNull(_app.LastError, _app.LastError);
            Assert.AreEqual("south-canyon", _app.Scenario.Id);
            yield return null;
        }

        // ---- criteria 2 and 3 ----

        [UnityTest]
        public IEnumerator ToolBar_HoldsTheGearAndTheThreeToggles_AndThePanelStartsClosed()
        {
            VisualElement bar = _root.Q("tool-bar");
            Assert.IsNotNull(bar, "The UI has no Tool Bar.");
            foreach (string name in new[] { "gear", "ignite-tool", "time-scale", "orbit-tool" })
                Assert.IsNotNull(bar.Q(name), $"The Tool Bar has no element '{name}'.");

            Assert.IsFalse(_panel.PanelOpen, "The Control Panel must be closed at startup.");
            _panel.SetPanelOpen(true);
            yield return null;
            Assert.IsTrue(_panel.PanelOpen);
            VisualElement panel = _root.Q("control-panel");
            Assert.Greater(panel.resolvedStyle.width, 100f);

            CollectionAssert.AreEqual(new[] { "Tools", "Weather, Wind, Fire", "Display" }, ControlPanel.TabNames);
            // Each control is on the tab that decision D35 gives.
            var onTab = new (int tab, string name)[]
            {
                (0, "scenario-list"), (0, "time-scale-slider"), (0, "ignite-switch"), (0, "slice-plane"), (0, "orbit-period"),
                (1, "wind-speed"), (1, "advection-sharpness"), (1, "wind-nest"), (1, "coupling-gain"),
                (2, "overlay-list"), (2, "vegetation"), (2, "smoke"), (2, "wind-particles"),
            };
            for (int tab = 0; tab < 3; tab++)
            {
                _panel.SelectTab(tab);
                yield return null;
                Assert.AreEqual(tab, _panel.SelectedTab);
                foreach (var (ownerTab, name) in onTab)
                {
                    VisualElement control = panel.Q(name);
                    Assert.IsNotNull(control, $"The Control Panel has no control '{name}'.");
                    bool shown = control.resolvedStyle.display == DisplayStyle.Flex && control.worldBound.width > 1f && control.panel != null
                        && Shown(control);
                    Assert.AreEqual(ownerTab == tab, shown, $"Control '{name}' with tab {tab} selected.");
                }
            }
            _panel.SetPanelOpen(false);
            yield return null;
            Assert.IsFalse(_panel.PanelOpen);
        }

        static bool Shown(VisualElement element)
        {
            for (VisualElement e = element; e != null; e = e.parent)
                if (e.resolvedStyle.display == DisplayStyle.None) return false;
            return true;
        }

        // ---- criterion 4 ----

        [UnityTest]
        public IEnumerator StatsHud_HasTwoTabs()
        {
            CollectionAssert.AreEqual(new[] { "Conditions", "Performance" }, StatsHud.TabNames);
            Assert.AreEqual(0, _hud.SelectedTab, "The Conditions tab must be on show at startup.");
            yield return new WaitForSecondsRealtime(0.6f);
            VisualElement hud = _root.Q("stats-hud");
            Assert.IsNotNull(hud);
            // The Conditions tab shows the Scenario and the wind.
            Assert.IsTrue(Contains(hud, "south-canyon"), "The Conditions tab must show the id of the Scenario.");
            Assert.IsTrue(Contains(hud, "Area burning") && Contains(hud, "Area burnt") && Contains(hud, "Time scale"));
            _hud.SelectTab(1);
            yield return null;
            Assert.AreEqual(1, _hud.SelectedTab);
            Assert.IsTrue(Contains(hud, "Cell updates") && Contains(hud, "Texture memory"));
        }

        static bool Contains(VisualElement root, string text)
        {
            foreach (Label label in root.Query<Label>().ToList())
                if (label.text.Contains(text) && Shown(label)) return true;
            return false;
        }

        // ---- criterion 5 ----

        [UnityTest]
        public IEnumerator Ui_UsesNoControlOfTheDefaultTheme()
        {
            _panel.SetPanelOpen(true);
            yield return null;
            Assert.AreEqual(0, _root.Query<Button>().ToList().Count, "The UI must not use the Button of Unity.");
            Assert.AreEqual(0, _root.Query<Toggle>().ToList().Count, "The UI must not use the Toggle of Unity.");
            Assert.AreEqual(0, _root.Query<Slider>().ToList().Count, "The UI must not use the Slider of Unity.");
            Assert.AreEqual(0, _root.Query<DropdownField>().ToList().Count, "The UI must not use the DropdownField of Unity.");
            Assert.AreEqual(0, _root.Query<ScrollView>().ToList().Count, "The UI must not use the ScrollView of Unity.");
            var settings = _panel.GetComponent<UIDocument>().panelSettings;
            Assert.AreEqual("FireCARuntimeTheme", settings.themeStyleSheet.name);
            // The font of every text is B612.
            foreach (Label label in _root.Query<Label>().ToList())
                StringAssert.StartsWith("B612", label.resolvedStyle.unityFontDefinition.font != null
                    ? label.resolvedStyle.unityFontDefinition.font.name
                    : label.resolvedStyle.unityFont.name, $"The text '{label.text}' does not use B612.");
        }

        // ---- criterion 6 ----

        [Test]
        public void TimeScaleSteps_GoUpAndDown()
        {
            Assert.AreEqual(120f, ControlPanel.NextTimeScale(60f, 1));
            Assert.AreEqual(30f, ControlPanel.NextTimeScale(60f, -1));
            Assert.AreEqual(60f, ControlPanel.NextTimeScale(45f, 1));
            Assert.AreEqual(30f, ControlPanel.NextTimeScale(45f, -1));
            Assert.AreEqual(1000f, ControlPanel.NextTimeScale(1000f, 1));
            Assert.AreEqual(1f, ControlPanel.NextTimeScale(1f, -1));
        }

        // ---- criteria 7 and 8 ----

        [UnityTest]
        public IEnumerator OrbitTool_TurnsOnceInItsPeriodOfTheSimClock()
        {
            Assert.AreEqual(6f, _rig.OrbitPeriodHours, "The default time of one orbit is 6 hours.");
            // The test moves the Sim Clock, so the Step Scheduler is off.
            _app.SchedulerEnabled = false;
            _rig.SetMode(CameraRig.Mode.Orbit);
            _rig.ApplyPreset(0);
            yield return null;
            Vector3 target = _rig.Target;
            Vector3 start = _rig.transform.position;
            Vector3 offset = start - target;

            _rig.SetAutoOrbit(true);
            Assert.IsTrue(_rig.AutoOrbit);
            yield return null;
            yield return null;
            Assert.Less(Vector3.Distance(start, _rig.transform.position), 0.5f, "The orbit must start at the position of the camera, and a paused Sim Clock must not turn it.");

            // A quarter of the period turns the camera by 90 degrees around the look-at point.
            double t0 = _app.Clock.Seconds;
            _app.Clock.Advance(t0 + 1.5 * 3600.0, 0f);
            yield return null;
            yield return null;
            Vector3 quarter = _rig.transform.position - target;
            Assert.Less(Vector3.Distance(_rig.Target, target), 0.01f, "The look-at point must not move.");
            Assert.AreEqual(offset.y, quarter.y, 1f);
            Assert.AreEqual(offset.magnitude, quarter.magnitude, 1f);
            float angle = Vector3.Angle(new Vector3(offset.x, 0f, offset.z), new Vector3(quarter.x, 0f, quarter.z));
            Assert.AreEqual(90f, angle, 0.5f);
            Debug.Log($"[FireCA test] orbit tool: {angle:F2} degrees after 1.5 h of the Sim Clock with a period of 6 h.");

            // The rest of the period brings the camera back to the start.
            _app.Clock.Advance(t0 + 6.0 * 3600.0, 0f);
            yield return null;
            yield return null;
            Assert.Less(Vector3.Distance(start, _rig.transform.position), offset.magnitude * 0.002f + 1f);

            // A period of 3 hours turns the camera two times faster.
            _rig.OrbitPeriodHours = 3f;
            _app.Clock.Advance(t0 + 7.5 * 3600.0, 0f);
            yield return null;
            yield return null;
            Vector3 half = _rig.transform.position - target;
            Assert.AreEqual(180f, Vector3.Angle(new Vector3(offset.x, 0f, offset.z), new Vector3(half.x, 0f, half.z)), 0.5f);

            _rig.SetAutoOrbit(false);
            Vector3 stopped = _rig.transform.position;
            _app.Clock.Advance(t0 + 9.0 * 3600.0, 0f);
            yield return null;
            yield return null;
            Assert.Less(Vector3.Distance(stopped, _rig.transform.position), 0.5f, "The camera must not turn with the orbit tool off.");
        }

        [UnityTest]
        public IEnumerator OrbitTool_FromTheFlyMode_OrbitsThePointAtTheCenterOfTheView()
        {
            _app.SchedulerEnabled = false;
            _rig.SetMode(CameraRig.Mode.Orbit);
            _rig.ApplyPreset(0);
            yield return null;
            _rig.SetMode(CameraRig.Mode.Fly);
            yield return null;
            Vector3 position = _rig.transform.position;
            Vector3 forward = _rig.transform.forward;

            _rig.SetAutoOrbit(true);
            yield return null;
            Assert.AreEqual(CameraRig.Mode.Orbit, _rig.CurrentMode);
            Assert.Less(Vector3.Distance(position, _rig.transform.position), 0.5f, "The orbit must start at the position of the camera.");
            Vector3 toTarget = _rig.Target - position;
            Assert.Less(Vector3.Angle(forward, toTarget), 0.1f, "The look-at point must be at the center of the view.");
            float ground = _app.SampleElevation(_rig.Target.x, _rig.Target.z);
            Assert.AreEqual(ground, _rig.Target.y, 25f, "The look-at point must be on the terrain.");

            // The fly mode turns the orbit tool off.
            _rig.SetMode(CameraRig.Mode.Fly);
            Assert.IsFalse(_rig.AutoOrbit);
        }
    }
}
