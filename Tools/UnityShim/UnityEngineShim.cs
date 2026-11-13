// Minimal UnityEngine shim for headless compilation of the Lifecycle.Unity / Lifecycle.Demo
// assemblies. This is NOT a runtime; it only declares the members those assemblies use, so the
// C# compiler can verify signatures. Kept under Tools/UnityShim, outside the Unity project.
using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static T Instantiate<T>(T original, Transform parent) where T : Object => original;
        public static void Destroy(Object obj) { }
        public static T FindObjectOfType<T>() where T : Object => null;
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !ReferenceEquals(a, b);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => 0;
        public static implicit operator bool(Object o) => o != null;
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
        public T GetComponent<T>() where T : Component => null;
    }

    public class Behaviour : Component
    {
        public bool enabled;
        public bool isActiveAndEnabled;
    }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => null;
    }

    public sealed class Coroutine { }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { }
        public GameObject(string name, params Type[] components) { }
        public Transform transform;
        public string tag;
        public bool activeSelf;
        public void SetActive(bool value) { }
        public T AddComponent<T>() where T : Component => null;
        public T GetComponent<T>() where T : Component => null;
        public static GameObject Find(string name) => null;
        public static GameObject CreatePrimitive(PrimitiveType type) => null;
    }

    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }

    public class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public Transform parent;
        public int childCount;
        public void SetParent(Transform p, bool worldPositionStays) { }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => default;
        public static Quaternion Euler(float x, float y, float z) => default;
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
        public static Color black => new Color(0, 0, 0);
        public static Color cyan => new Color(0, 1, 1);
        public static Color yellow => new Color(1, 1, 0);
        public static Color magenta => new Color(1, 0, 1);
        public static Color Lerp(Color a, Color b, float t) => a;
        public static Color operator *(Color a, float d) => a;
        public static Color operator +(Color a, Color b) => a;
    }

    public class Renderer : Component { public Material material; }
    public class Material : Object { public Color color; }
    public class Collider : Component { public bool enabled; }
    public class Camera : Behaviour { public static Camera main; }
    public class Light : Behaviour { public LightType type; }
    public enum LightType { Directional, Point, Spot }
    public class AudioListener : Behaviour { }

    public static class Time
    {
        public static int frameCount;
        public static float time;
        public static float timeSinceLevelLoad;
        public static float deltaTime;
    }

    public static class Mathf
    {
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Lerp(float a, float b, float t) => a;
    }

    public static class Application { public static bool isPlaying; }
    public static class Debug { public static void Log(object message) { } public static void LogWarning(object m) { } public static void LogError(object m) { } }

    public static class Input
    {
        public static bool GetKeyDown(KeyCode key) => false;
    }

    public enum KeyCode { Alpha1, Alpha2, Alpha3, U, R, G, C, T, X, V, H, O, E }

    public class WaitForSeconds : IEnumerator
    {
        public WaitForSeconds(float seconds) { }
        public object Current => null;
        public bool MoveNext() => false;
        public void Reset() { }
    }

    public class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string tooltip) { } }

    public class WaitForEndOfFrame : IEnumerator
    {
        public object Current => null;
        public bool MoveNext() => false;
        public void Reset() { }
    }

    public class GUIContent { public static GUIContent none = new GUIContent(); }
    public class GUIStyleState { public Color textColor; }
    public enum FontStyle { Normal, Bold, Italic }
    public class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
        public int fontSize;
        public bool richText;
        public FontStyle fontStyle;
        public GUIStyleState normal = new GUIStyleState();
    }

    public class GUISkin { public GUIStyle label; }
    public static class GUI { public static GUISkin skin; public static void Box(Rect r, GUIContent c) { } }
    public static class GUILayout
    {
        public static void BeginArea(Rect r) { }
        public static void EndArea() { }
        public static void Label(string text, GUIStyle style = null) { }
        public static void Space(float pixels) { }
        public static bool Button(string text) => false;
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public bool IsValid() => true;
        public string name;
        public string path;
    }

    public static class SceneManager
    {
        public static Scene GetActiveScene() => default;
    }
}
