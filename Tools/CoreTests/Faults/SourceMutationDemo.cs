using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lifecycle.CoreTests.Faults
{
    /// <summary>
    /// Demonstrates that one specific source-level generation validation is load-bearing.
    ///
    /// A copy of the shipped manager is made in a temporary directory, the single generation
    /// comparison in <c>IsCurrent</c> is deleted from that copy, and the object-reuse tests are
    /// recompiled against it. The removed build must fail; the untouched build must pass. The
    /// repository itself is never modified — the mutation lives only in the temp copy.
    /// </summary>
    internal static class SourceMutationDemo
    {
        /// <summary>The exact line the demonstration removes (indentation included).</summary>
        public const string RemovedLine = "            slot.Generation = slot.Generation.Next();";

        public sealed class Outcome
        {
            public int ExitCodeRemoved;
            public int ExitCodeRestored;
            public string RemovedTail = string.Empty;
            public string RestoredTail = string.Empty;
            public bool ExposedError => ExitCodeRemoved != 0;
            public bool RestoredAndClean => ExitCodeRestored == 0;
        }

        /// <summary>
        /// Returns true when the demo can run here: it needs the SDK, the repository layout, and a
        /// writable temp directory. CI and local runs use it; a packaged build can skip it.
        /// </summary>
        public static bool CanRun(out string reason)
        {
            reason = null;
            if (!Directory.Exists("/workspace/Assets/Lifecycle/Core"))
            {
                reason = "repository layout not found";
                return false;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")) &&
                File.Exists("/home/node/.dotnet/dotnet") == false)
            {
                // Best effort: the CLI locates dotnet itself; failing that, skip instead of failing.
                reason = "no dotnet SDK on this machine";
                return false;
            }

            return true;
        }

        public static Outcome Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "lifecycle-mutation-" + Guid.NewGuid().ToString("N"));
            var outcome = new Outcome();
            try
            {
                var removed = BuildVariant(root, "removed", mutate: true);
                var restored = BuildVariant(root, "restored", mutate: false);
                outcome.ExitCodeRemoved = TestProjectRunner.Run(removed);
                outcome.ExitCodeRestored = TestProjectRunner.Run(restored);
                outcome.RemovedTail = TestProjectRunner.LastTail(removed);
                outcome.RestoredTail = TestProjectRunner.LastTail(restored);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch
                {
                    // Temp cleanup must never mask the result.
                }
            }

            return outcome;
        }

        private static string BuildVariant(string root, string name, bool mutate)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            CopyDirectory("/workspace/Assets/Lifecycle/Core", Path.Combine(dir, "Core"));
            CopyDirectory("/workspace/Tools/CoreTests", Path.Combine(dir, "Tests"));

            if (mutate)
            {
                var manager = Path.Combine(dir, "Core", "ObjectPool.cs");
                var text = File.ReadAllText(manager);
                if (text.IndexOf(RemovedLine, StringComparison.Ordinal) < 0)
                {
                    throw new InvalidOperationException(
                        "the generation advance to remove was not found; the demo must be updated");
                }

                File.WriteAllText(manager, text.Replace(RemovedLine + "\n", string.Empty).Replace(RemovedLine, string.Empty));
            }

            File.WriteAllText(Path.Combine(dir, "Mutation.csproj"), ProjectFile);
            return dir;
        }

        private const string ProjectFile =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>\n" +
            "  <Nullable>disable</Nullable><LangVersion>latest</LangVersion><AssemblyName>Mutation</AssemblyName>\n" +
            "  <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n" +
            "  <NoWarn>CS0168;CS0219;CS0414;CS0649</NoWarn></PropertyGroup>\n" +
            "  <ItemGroup><Compile Include=\"Core/*.cs\" /><Compile Include=\"Tests/**/*.cs\" /></ItemGroup>\n" +
            "</Project>\n";

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source, "*.cs"))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (var sub in Directory.GetDirectories(source))
            {
                var name = Path.GetFileName(sub);
                // Build outputs contain generated *.cs (assembly info); copying them into the
                // nested project would duplicate the assembly attributes.
                if (name == "bin" || name == "obj") continue;
                CopyDirectory(sub, Path.Combine(destination, name));
            }
        }

        public static string Describe(Outcome outcome)
        {
            var sb = new StringBuilder();
            sb.Append("removed-build exit=").Append(outcome.ExitCodeRemoved)
                .Append(" restored-build exit=").Append(outcome.ExitCodeRestored);
            if (!string.IsNullOrEmpty(outcome.RemovedTail))
            {
                sb.Append("\n  removed tail: ").Append(outcome.RemovedTail);
            }

            if (!string.IsNullOrEmpty(outcome.RestoredTail))
            {
                sb.Append("\n  restored tail: ").Append(outcome.RestoredTail);
            }

            return sb.ToString();
        }
    }
}
