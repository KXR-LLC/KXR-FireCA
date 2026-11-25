using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FireCA.Editor
{
    /// <summary>The editor window of the Canyon Generator, under Tools > FireCA > Canyon Generator.</summary>
    public sealed class CanyonGeneratorWindow : EditorWindow
    {
        CanyonGeneratorSettings _settings;
        UnityEditor.Editor _settingsEditor;
        Vector2 _scroll;
        string _lastResult = "";

        [MenuItem("Tools/FireCA/Canyon Generator")]
        static void Open()
        {
            var window = GetWindow<CanyonGeneratorWindow>("Canyon Generator");
            window.minSize = new Vector2(380f, 320f);
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Canyon Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The Canyon Generator writes a synthetic Scenario folder under StreamingAssets/Scenarios. " +
                "The same settings and the same seed produce the same files.", MessageType.None);

            var picked = (CanyonGeneratorSettings)EditorGUILayout.ObjectField("Settings", _settings, typeof(CanyonGeneratorSettings), false);
            if (picked != _settings)
            {
                _settings = picked;
                DestroyImmediate(_settingsEditor);
                _settingsEditor = null;
            }
            if (_settings == null)
            {
                EditorGUILayout.HelpBox("Select a Canyon Generator settings asset.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_settingsEditor == null) _settingsEditor = UnityEditor.Editor.CreateEditor(_settings);
            _settingsEditor.OnInspectorGUI();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", ScenarioFolder(_settings.scenarioName));
            if (GUILayout.Button("Generate Scenario", GUILayout.Height(28f)))
            {
                _lastResult = GenerateAndWrite(_settings);
            }
            if (!string.IsNullOrEmpty(_lastResult)) EditorGUILayout.HelpBox(_lastResult, MessageType.Info);
        }

        void OnDisable()
        {
            if (_settingsEditor != null) DestroyImmediate(_settingsEditor);
        }

        public static string ScenarioFolder(string scenarioName) =>
            Path.Combine(Application.dataPath, "StreamingAssets", "Scenarios", scenarioName);

        /// <summary>Generates the Scenario of the settings asset and writes the Scenario folder.</summary>
        public static string GenerateAndWrite(CanyonGeneratorSettings settings)
        {
            var watch = Stopwatch.StartNew();
            CanyonResult result = CanyonGenerator.Generate(settings);
            string folder = ScenarioFolder(settings.scenarioName);
            CanyonGenerator.Write(result, folder);
            AssetDatabase.Refresh();
            string message = $"Wrote '{settings.scenarioName}' ({settings.gridSize} cells per side, seed {settings.seed}) " +
                $"in {watch.Elapsed.TotalSeconds:F1} s.";
            UnityEngine.Debug.Log($"[FireCA] {message}");
            return message;
        }
    }
}
