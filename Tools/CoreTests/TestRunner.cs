using System;
using System.Collections.Generic;
using System.Text;
using Lifecycle.Core;

namespace Lifecycle.CoreTests
{
    /// <summary>Categories let the object-reuse CLI run only the reuse subset instead of everything.</summary>
    internal static class TestCategory
    {
        public const string Core = "core";
        public const string Reuse = "reuse";
    }

    /// <summary>Tiny assertion-based test runner so the harness has no external dependencies.</summary>
    internal static class TestRunner
    {
        private static readonly List<TestCase> Cases = new List<TestCase>();
        private static int _failed;
        private static int _passed;
        private static int _skipped;
        private static readonly List<string> Failures = new List<string>();

        public static void Test(string name, Action body) => Test(name, TestCategory.Core, body);

        public static void Test(string name, string category, Action body)
        {
            Cases.Add(new TestCase(name, category, body));
        }

        public static IReadOnlyList<string> FailureLines => Failures;

        /// <summary>Excludes a test by name for nested/reentrant runs.</summary>
        public static void Skip(string name)
        {
            for (var i = Cases.Count - 1; i >= 0; i--)
            {
                if (Cases[i].Name == name) Cases.RemoveAt(i);
            }
        }

        public static int Run(string filter = null, string category = null)
        {
            var started = DateTime.UtcNow;
            foreach (var test in Cases)
            {
                if (filter != null && test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _skipped++;
                    continue;
                }

                if (category != null && test.Category.IndexOf(category, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _skipped++;
                    continue;
                }

                try
                {
                    test.Body();
                    _passed++;
                    Console.WriteLine("PASS  [" + test.Category + "] " + test.Name);
                }
                catch (Exception ex)
                {
                    _failed++;
                    Console.WriteLine("FAIL  [" + test.Category + "] " + test.Name);
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

                    Failures.Add(test.Name + ": " + ex.Message);
                }
            }

            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            Console.WriteLine();
            Console.WriteLine("----- " + _passed + " passed, " + _failed + " failed, " + _skipped +
                              " filtered out in " + elapsed.ToString("F0") + " ms -----");
            return _failed == 0 ? 0 : 1;
        }

        public static IReadOnlyList<string> AllTestNames
        {
            get
            {
                var names = new List<string>();
                foreach (var test in Cases) names.Add("[" + test.Category + "] " + test.Name);
                return names;
            }
        }

        private sealed class TestCase
        {
            public TestCase(string name, string category, Action body)
            {
                Name = name;
                Category = category;
                Body = body;
            }

            public string Name { get; }
            public string Category { get; }
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

        public static void Null(object value, string message) => True(value == null, message);

        public static void Same(object expected, object actual, string message) =>
            True(ReferenceEquals(expected, actual), message);

        public static void NotSame(object unexpected, object actual, string message) =>
            True(!ReferenceEquals(unexpected, actual), message);

        public static void Contains(string haystack, string needle, string message)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                throw new AssertionException(message + " (looked for '" + needle + "' in '" + haystack + "')");
            }
        }

        public static void DoesNotContain(string haystack, string needle, string message)
        {
            if (haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
            {
                throw new AssertionException(message + " (unexpectedly found '" + needle + "' in '" + haystack + "')");
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

        public static string DumpLines(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var line in lines) sb.Append("\n    ").Append(line);
            return sb.ToString();
        }
    }
}
