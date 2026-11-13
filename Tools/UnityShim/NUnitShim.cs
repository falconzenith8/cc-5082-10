// Minimal NUnit + UnityTest shim so the PlayMode test file compiles headlessly. Signatures only.
using System;

namespace NUnit.Framework
{
    public class SetUpAttribute : Attribute { }
    public class TearDownAttribute : Attribute { }
    public class TestAttribute : Attribute { }
    public class UnityTestAttribute : Attribute { }

    public static class Assert
    {
        public static void AreEqual(object expected, object actual, string message = null) { }
        public static void AreEqual(int expected, int actual, string message = null) { }
        public static void AreEqual(float expected, float actual, string message = null) { }
        public static void AreSame(object expected, object actual, string message = null) { }
        public static void IsFalse(bool condition, string message = null) { }
        public static void IsTrue(bool condition, string message = null) { }
        public static void IsNotNull(object value, string message = null) { }
        public static void IsNull(object value, string message = null) { }
        public static void Less(float value, float limit, string message = null) { }
        public static void LessOrEqual(float value, float limit, string message = null) { }
        public static void LessOrEqual(int value, int limit, string message = null) { }
    }

    public static class StringAssert
    {
        public static void Contains(string expected, string actual, string message = null) { }
    }
}
