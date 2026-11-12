using System;
using System.Collections.Generic;
using System.Text;
using Lifecycle.Core;

namespace Lifecycle.CoreTests
{
    /// <summary>Tiny assertion-based test runner so the harness has no external dependencies.</summary>
    internal static class TestRunner
    {
        private static readonly List<TestCase> Cases = new List<TestCase>();
        private static int _failed;
        private static int _passed;
        private static string _current;

        public static void Test(string name, Action body)
        {
            Cases.Add(new TestCase(name, body));
        }

        public static int Run(string filter = null)
        {
            var started = DateTime.UtcNow;
            foreach (var test in Cases)
            {
                if (filter != null && test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _current = test.Name;
                try
                {
                    test.Body();
                    _passed++;
                    Console.WriteLine("PASS  " + test.Name);
                }
                catch (Exception ex)
                {
                    _failed++;
                    Console.WriteLine("FAIL  " + test.Name);
                    Console.WriteLine("      " + ex.GetType().Name + ": " + ex.Message);
                    var stack = ex.StackTrace ?? string.Empty;
                    foreach (var line in stack.Split('\n'))
                    {
                        if (line.IndexOf("Lifecycle.CoreTests", StringComparison.Ordinal) >= 0)
                        {
                            Console.WriteLine("      at " + line.Trim());
                            break;
                        }
                    }
                }
            }

            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            Console.WriteLine();
            Console.WriteLine("----- " + _passed + " passed, " + _failed + " failed in " + elapsed.ToString("F0") + " ms -----");
            return _failed == 0 ? 0 : 1;
        }

        private sealed class TestCase
        {
            public TestCase(string name, Action body)
            {
                Name = name;
                Body = body;
            }

            public string Name { get; }
            public Action Body { get; }
        }
    }

    internal static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new AssertionException(message);
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new AssertionException(message + " (expected: " + expected + ", actual: " + actual + ")");
            }
        }

        public static void NotEqual<T>(T unexpected, T actual, string message)
        {
            if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            {
                throw new AssertionException(message + " (both were " + actual + ")");
            }
        }

        public static void NotNull(object value, string message) => True(value != null, message);

        public static void Contains(string haystack, string needle, string message)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                throw new AssertionException(message + " (looked for '" + needle + "' in '" + haystack + "')");
            }
        }

        public static T Throws<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                throw new AssertionException(message + " (threw " + other.GetType().Name + " instead of " + typeof(T).Name + ")");
            }

            throw new AssertionException(message + " (nothing was thrown)");
        }
    }

    internal sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    internal static class Format
    {
        public static string DumpTickets(IEnumerable<SpawnTicket> tickets)
        {
            var sb = new StringBuilder();
            foreach (var t in tickets) sb.Append("\n    ").Append(t);
            return sb.ToString();
        }
    }
}
