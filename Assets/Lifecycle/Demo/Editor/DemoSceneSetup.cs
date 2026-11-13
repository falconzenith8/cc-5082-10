using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Lifecycle.Demo;

namespace Lifecycle.DemoEditor
{
    /// <summary>
    /// Entry point for the demo. Prefers the project's main scene (which should contain a
    /// DemoSceneBootstrap); if the project has no main scene configured, it creates and opens a
    /// minimal Bootstrap scene so Play mode works immediately after cloning.
    /// </summary>
    [InitializeOnLoad]
    public static class DemoSceneSetup
    {
        private const string BootstrapScenePath = "Assets/Scenes/LifecycleDemo.unity";

        static DemoSceneSetup()
        {
            // Defer until the editor is idle; asset DB may still be importing.
            EditorApplication.delayCall += EnsureSceneReady;
        }

        [MenuItem("Lifecycle/Open Demo Scene")]
        public static void OpenDemoScene()
        {
            OpenAndPlay();
        }

        /// <summary>
        /// Graphical entry point for the object-reuse sample. Opens the demo scene with the
        /// reuse panel visible, so the reuse regression can be exercised by hand (borrow, return,
        /// transition) and inspected on screen.
        /// </summary>
        [MenuItem("Lifecycle/Open Object Reuse Sample")]
        public static void OpenObjectReuseSample()
        {
            OpenAndPlay();
            var bootstrap = Object.FindObjectOfType<DemoSceneBootstrap>();
            if (bootstrap != null)
            {
                bootstrap.ShowReusePanel = true;
                Debug.Log("[LifecycleDemo] Object Reuse panel enabled (key O toggles).");
            }
        }

        private static void OpenAndPlay()
        {
            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && Object.FindObjectOfType<DemoSceneBootstrap>() != null)
            {
                EditorApplication.isPlaying = true;
                return;
            }

            EnsureSceneReady();
            if (Object.FindObjectOfType<DemoSceneBootstrap>() == null)
            {
                CreateBootstrapScene();
            }

            EditorApplication.isPlaying = true;
        }

        private static void EnsureSceneReady()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            // A scene file existing on disk is not enough: a batch run can start from a clean
            // checkout whose Assets/Scenes folder was never generated, or from a stale scene that
            // predates the bootstrap component. Verify the bootstrap actually exists before
            // trusting the saved asset, and regenerate it if not.
            var mainScene = EditorBuildSettings.scenes.Length > 0 ? EditorBuildSettings.scenes[0].path : null;
            var sceneHasBootstrap = string.IsNullOrEmpty(mainScene) || !System.IO.File.Exists(mainScene)
                ? false
                : SceneContainsBootstrap(mainScene);
            if (sceneHasBootstrap) return;

            if (Object.FindObjectOfType<DemoSceneBootstrap>() != null) return;
            CreateBootstrapScene();
        }

        private static bool SceneContainsBootstrap(string scenePath)
        {
            // Loading the scene to inspect it would mutate the editor's loaded assets, which is
            // exactly what a clean batch run must avoid. Reading the serialized scene text is
            // enough to know whether the bootstrap component type is present.
            try
            {
                return System.IO.File.ReadAllText(scenePath)
                    .Contains("DemoSceneBootstrap");
            }
            catch
            {
                return false;
            }
        }

        private static void CreateBootstrapScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var boot = new GameObject("DemoSceneBootstrap");
            boot.AddComponent<DemoSceneBootstrap>();

            var directory = System.IO.Path.GetDirectoryName(BootstrapScenePath);
            if (!System.IO.Directory.Exists(directory)) System.IO.Directory.CreateDirectory(directory);
            EditorSceneManager.SaveScene(scene, BootstrapScenePath);
            AssetDatabase.Refresh();

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BootstrapScenePath, true) };
            Debug.Log("[LifecycleDemo] Created demo bootstrap scene at " + BootstrapScenePath);
        }
    }
}
