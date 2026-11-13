// Minimal UnityEditor shim for headless compilation of Lifecycle.Editor. Signatures only.
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityEditor
{
    public static class EditorApplication
    {
        public static bool isPlaying;
        public static bool isPlayingOrWillChangePlaymode;
        public static Action delayCall;
    }

    public static class AssetDatabase
    {
        public static void Refresh() { }
    }

    public class EditorBuildSettingsScene
    {
        public EditorBuildSettingsScene(string path, bool enabled) { this.path = path; this.enabled = enabled; }
        public string path;
        public bool enabled;
    }

    public static class EditorBuildSettings
    {
        public static EditorBuildSettingsScene[] scenes = Array.Empty<EditorBuildSettingsScene>();
    }

    public class MenuItem : Attribute
    {
        public MenuItem(string path) { }
    }

    public class InitializeOnLoadAttribute : Attribute { }
}

namespace UnityEditor.SceneManagement
{
    public enum NewSceneSetup { EmptyScene, DefaultGameObjects }
    public enum NewSceneMode { Single, Additive }

    public static class EditorSceneManager
    {
        public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode) => default;
        public static bool SaveScene(Scene scene, string path) => true;
        public static Scene OpenScene(string path) => default;
    }
}
