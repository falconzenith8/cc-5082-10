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
            var mainScene = EditorBuildSettings.scenes.Length > 0 ? EditorBuildSettings.scenes[0].path : null;
            if (!string.IsNullOrEmpty(mainScene) && System.IO.File.Exists(mainScene)) return;

            if (Object.FindObjectOfType<DemoSceneBootstrap>() != null) return;
            CreateBootstrapScene();
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
