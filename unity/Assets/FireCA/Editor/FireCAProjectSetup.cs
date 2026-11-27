using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UIElements;

namespace FireCA.Editor
{
    /// <summary>
    /// Builds the project assets that are not hand-written files: the Canyon Generator settings assets,
    /// the two default Scenarios, the terrain material, the UI panel settings, the sky profile, and the FireCA scene.
    /// Every step can run again without harm.
    /// </summary>
    public static class FireCAProjectSetup
    {
        public const string CanyonSettingsFolder = "Assets/FireCA/Editor/CanyonSettings";
        public const string SettingsFolder = "Assets/FireCA/Settings";
        public const string ScenePath = "Assets/FireCA/Scenes/FireCA.unity";
        public const string TerrainGraphPath = "Assets/FireCA/Shaders/Graphs/FireCATerrain.shadergraph";
        public const string TerrainMaterialPath = SettingsFolder + "/FireCATerrain.mat";
        public const string DerivedLayersPath = "Assets/FireCA/Shaders/Compute/DerivedLayers.compute";
        public const string PanelSettingsPath = SettingsFolder + "/FireCAPanelSettings.asset";
        public const string ThemePath = SettingsFolder + "/FireCARuntimeTheme.tss";
        public const string SkyProfilePath = SettingsFolder + "/FireCASkyProfile.asset";
        const string TemplateScenePath = "Assets/OutdoorsScene.unity";
        const string TemplateSkyProfilePath = "Assets/Settings/SkyandFogSettingsProfile.asset";

        public const string DemoScenario = "synthetic-canyon-01";
        /// <summary>The Scenario that the scene loads at startup (docs/decisions.md, decision D35).</summary>
        public const string StartupScenario = "south-canyon";
        public const string TestScenario = "test-canyon-512";

        [MenuItem("Tools/FireCA/Setup/Run All Setup Steps")]
        public static void RunAll()
        {
            CreateCanyonSettings();
            GenerateDefaultScenarios();
            BuildScene();
        }

        public static CanyonGeneratorSettings LoadCanyonSettings(string scenarioName) =>
            AssetDatabase.LoadAssetAtPath<CanyonGeneratorSettings>($"{CanyonSettingsFolder}/{scenarioName}.asset");

        [MenuItem("Tools/FireCA/Setup/Create Canyon Settings")]
        public static void CreateCanyonSettings()
        {
            Directory.CreateDirectory(CanyonSettingsFolder);
            CreateSettings(DemoScenario, 4096);
            CreateSettings(TestScenario, 512);
            AssetDatabase.SaveAssets();
        }

        static void CreateSettings(string scenarioName, int gridSize)
        {
            string path = $"{CanyonSettingsFolder}/{scenarioName}.asset";
            if (AssetDatabase.LoadAssetAtPath<CanyonGeneratorSettings>(path) != null) return;
            AssetDatabase.Refresh();
            var settings = ScriptableObject.CreateInstance<CanyonGeneratorSettings>();
            settings.scenarioName = scenarioName;
            settings.gridSize = gridSize;
            settings.seed = 1;
            AssetDatabase.CreateAsset(settings, path);
        }

        [MenuItem("Tools/FireCA/Setup/Generate Default Scenarios")]
        public static void GenerateDefaultScenarios()
        {
            CreateCanyonSettings();
            CanyonGeneratorWindow.GenerateAndWrite(LoadCanyonSettings(DemoScenario));
            CanyonGeneratorWindow.GenerateAndWrite(LoadCanyonSettings(TestScenario));
        }

        [MenuItem("Tools/FireCA/Setup/Build Scene")]
        public static void BuildScene()
        {
            Directory.CreateDirectory(SettingsFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            AssetDatabase.Refresh();

            Material terrainMaterial = EnsureTerrainMaterial();
            PanelSettings panelSettings = EnsurePanelSettings();
            VolumeProfile skyProfile = EnsureSkyProfile();

            if (!File.Exists(ScenePath))
            {
                AssetDatabase.CopyAsset(TemplateScenePath, ScenePath);
                AssetDatabase.Refresh();
            }
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Camera
            var cameraObject = GameObject.Find("Main Camera");
            var camera = cameraObject.GetComponent<Camera>();
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 65000f;
            camera.fieldOfView = 60f;
            cameraObject.transform.SetPositionAndRotation(new Vector3(8300f, 5200f, -1800f), Quaternion.Euler(35f, -35f, 0f));
            if (cameraObject.GetComponent<CameraRig>() == null) cameraObject.AddComponent<CameraRig>();

            // Sun. The Weather Model drives the sun from M2. The value here is 13:00 solar time in summer.
            var sun = GameObject.Find("Sun");
            if (sun != null) sun.transform.rotation = Quaternion.Euler(65f, 20f, 0f);

            var volumeObject = GameObject.Find("Sky and Fog Volume");
            if (volumeObject != null) volumeObject.GetComponent<Volume>().sharedProfile = skyProfile;

            // Simulation root
            var appObject = GameObject.Find("FireCA") ?? new GameObject("FireCA");
            var app = appObject.GetComponent<FireCAApp>() ?? appObject.AddComponent<FireCAApp>();
            app.DerivedLayersCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(DerivedLayersPath);
            app.WeatherCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/FireCA/Shaders/Compute/Weather.compute");
            app.SpreadCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/FireCA/Shaders/Compute/Spread.compute");
            app.WindCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/FireCA/Shaders/Compute/Wind.compute");
            app.TerrainMaterial = terrainMaterial;
            app.SunLight = sun != null ? sun.GetComponent<Light>() : null;
            app.StartupScenario = StartupScenario;
            if (appObject.GetComponent<FrameBenchmark>() == null) appObject.AddComponent<FrameBenchmark>();
            if (appObject.GetComponent<DemoRun>() == null) appObject.AddComponent<DemoRun>();
            var particles = appObject.GetComponent<WindParticles>() ?? appObject.AddComponent<WindParticles>();
            particles.ParticleCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/FireCA/Shaders/Compute/WindParticles.compute");
            particles.ParticleMaterial = EnsureMaterial(SettingsFolder + "/WindParticles.mat", "Assets/FireCA/Shaders/Graphs/WindParticles.shader");
            EditorUtility.SetDirty(particles);
            var vegetation = appObject.GetComponent<Vegetation>() ?? appObject.AddComponent<Vegetation>();
            vegetation.VegetationCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/FireCA/Shaders/Compute/Vegetation.compute");
            vegetation.PlantMaterial = EnsureMaterial(SettingsFolder + "/Vegetation.mat", "Assets/FireCA/Shaders/Graphs/Vegetation.shader");
            vegetation.FireMaterial = EnsureMaterial(SettingsFolder + "/VegetationFire.mat", "Assets/FireCA/Shaders/Graphs/VegetationFire.shader");
            vegetation.PlantModelData = AssetDatabase.LoadAssetAtPath<TextAsset>(PlantModels.DataPath);
            vegetation.LeafAtlas = EnsureLeafAtlas();
            EditorUtility.SetDirty(vegetation);
            var slice = appObject.GetComponent<SlicePlane>() ?? appObject.AddComponent<SlicePlane>();
            slice.SliceMaterial = EnsureMaterial(SettingsFolder + "/SlicePlane.mat", "Assets/FireCA/Shaders/Graphs/SlicePlane.shader");
            EditorUtility.SetDirty(slice);
            EditorUtility.SetDirty(app);

            // Smoke Pass: an HDRP Custom Pass at the Before Post Process injection point.
            var smokeObject = GameObject.Find("Smoke Pass") ?? new GameObject("Smoke Pass");
            var passVolume = smokeObject.GetComponent<CustomPassVolume>() ?? smokeObject.AddComponent<CustomPassVolume>();
            passVolume.isGlobal = true;
            passVolume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
            SmokePass smokePass = null;
            foreach (CustomPass pass in passVolume.customPasses) if (pass is SmokePass found) smokePass = found;
            if (smokePass == null) smokePass = (SmokePass)passVolume.AddPassOfType<SmokePass>();
            smokePass.name = "Smoke Pass";
            smokePass.material = EnsureMaterial(SettingsFolder + "/SmokePass.mat", "Assets/FireCA/Shaders/Graphs/SmokePass.shader");
            smokePass.targetColorBuffer = CustomPass.TargetBuffer.Camera;
            smokePass.targetDepthBuffer = CustomPass.TargetBuffer.None;
            smokePass.clearFlags = ClearFlag.None;
            EditorUtility.SetDirty(passVolume);

            // UI
            var uiObject = GameObject.Find("UI") ?? new GameObject("UI");
            var document = uiObject.GetComponent<UIDocument>() ?? uiObject.AddComponent<UIDocument>();
            var serializedDocument = new SerializedObject(document);
            serializedDocument.FindProperty("m_PanelSettings").objectReferenceValue = panelSettings;
            serializedDocument.ApplyModifiedPropertiesWithoutUndo();
            if (uiObject.GetComponent<ControlPanel>() == null) uiObject.AddComponent<ControlPanel>();
            if (uiObject.GetComponent<StatsHud>() == null) uiObject.AddComponent<StatsHud>();
            EditorUtility.SetDirty(document);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[FireCA] Built the scene {ScenePath}.");
        }

        /// <summary>Sets the import settings of the Leaf Atlas (decision D34) and returns the texture.</summary>
        static Texture2D EnsureLeafAtlas()
        {
            var importer = AssetImporter.GetAtPath(PlantModels.LeafAtlasPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"[FireCA] {PlantModels.LeafAtlasPath} is missing. Run tools/blender/plant_models.py.");
                return null;
            }
            // The channels are data: brightness, wood mask, and a random value per leaf. The alpha channel cuts the leaves.
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.mipMapsPreserveCoverage = true;
            importer.alphaTestReferenceValue = 0.4f;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 8;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(PlantModels.LeafAtlasPath);
        }

        static Material EnsureMaterial(string materialPath, string shaderPath)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(materialPath) };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material EnsureTerrainMaterial()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(TerrainGraphPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "FireCATerrain" };
                AssetDatabase.CreateAsset(material, TerrainMaterialPath);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }
            UnityEditor.Rendering.HighDefinition.HDShaderUtils.ResetMaterialKeywords(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        static PanelSettings EnsurePanelSettings()
        {
            // The theme holds the style sheet of the project only. It does not import the default theme of Unity
            // (decision D35).
            const string themeText = "@import url(\"project://database/Assets/FireCA/UI/FireCA.uss\");\nVisualElement {}\n";
            if (!File.Exists(ThemePath) || File.ReadAllText(ThemePath) != themeText)
            {
                File.WriteAllText(ThemePath, themeText);
                AssetDatabase.ImportAsset(ThemePath);
            }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            }
            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 1f;
            EditorUtility.SetDirty(panel);
            return panel;
        }

        // The template profile, with fog and shadow distances for a scene of 8 km.
        static VolumeProfile EnsureSkyProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(SkyProfilePath);
            if (profile == null)
            {
                AssetDatabase.CopyAsset(TemplateSkyProfilePath, SkyProfilePath);
                AssetDatabase.Refresh();
                profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(SkyProfilePath);
            }

            if (profile.TryGet(out Fog fog))
            {
                fog.enabled.Override(true);
                fog.meanFreePath.Override(30000f);
                fog.baseHeight.Override(1000f);
                fog.maximumHeight.Override(4000f);
                fog.maxFogDistance.Override(60000f);
                fog.enableVolumetricFog.Override(false);
            }

            if (!profile.TryGet(out HDShadowSettings shadows))
                shadows = UnityEditor.Rendering.VolumeProfileFactory.CreateVolumeComponent<HDShadowSettings>(profile, false, true);
            shadows.maxShadowDistance.Override(22000f);
            shadows.cascadeShadowSplitCount.Override(4);

            EditorUtility.SetDirty(profile);
            return profile;
        }
    }
}
