using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Lifecycle.CoreTests.Faults
{
    /// <summary>
    /// Runs a scratch test project in a subprocess using the dotnet SDK. Kept separate from the
    /// mutation demo so the demo can be reasoned about without process plumbing.
    /// </summary>
    internal static class TestProjectRunner
    {
        private static readonly System.Collections.Generic.Dictionary<string, string> Tails =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        public static string DotnetBinary { get; } = LocateDotnet();

        public static int Run(string projectDir)
        {
            var start = new ProcessStartInfo
            {
                FileName = DotnetBinary,
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(Path.Combine(projectDir, "Mutation.csproj"));
            start.ArgumentList.Add("--");
            start.ArgumentList.Add("--category=reuse");
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["LIFECYCLE_SKIP_MUTATION_DEMO"] = "1";
            using var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("failed to start dotnet");
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(180_000))
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException("mutation test project did not finish in time");
            }

            process.WaitForExit();
            var text = output.ToString();
            Tails[projectDir] = Tail(text);
            return process.ExitCode;
        }

        public static string LastTail(string projectDir) =>
            Tails.TryGetValue(projectDir, out var tail) ? tail : string.Empty;

        private static string Tail(string text)
        {
            var lines = text.Split('\n');
            var kept = new System.Collections.Generic.List<string>();
            for (var i = lines.Length - 1; i >= 0 && kept.Count < 3; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.IndexOf("PASS", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("FAIL", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("passed", StringComparison.Ordinal) >= 0)
                {
                    kept.Insert(0, line);
                }
            }

            return string.Join(" | ", kept);
        }

        private static string LocateDotnet()
        {
            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root))
            {
                var candidate = Path.Combine(root, "dotnet");
                if (File.Exists(candidate)) return candidate;
            }

            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
            if (File.Exists(home)) return home;
            return "dotnet";
        }
    }
}
