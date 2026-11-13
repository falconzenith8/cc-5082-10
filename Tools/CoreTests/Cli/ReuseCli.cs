using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lifecycle.Core;
using Lifecycle.CoreTests.Faults;
using Lifecycle.CoreTests.Scenarios;

namespace Lifecycle.CoreTests.Cli
{
    /// <summary>
    /// Command-line companion for the object-reuse sample. It only assists reuse work: replay
    /// scenarios, print the invariant report, run the reuse test subset, and demonstrate the
    /// fault injection. It is explicitly secondary to the graphical entry point (the Unity
    /// menu item), which is documented as the primary way to exercise object reuse.
    /// </summary>
    internal static class ReuseCli
    {
        public const int ExitOk = 0;
        public const int ExitFault = 1;
        public const int ExitUsage = 2;

        public static int Run(string[] args)
        {
            var mode = args.Length > 2 ? args[2].ToLowerInvariant() : "help";
            try
            {
                switch (mode)
                {
                    case "list":
                        return ListScenarios();
                    case "script":
                        return PrintScripts();
                    case "replay":
                        return Replay();
                    case "report":
                        return RunReport();
                    case "tests":
                        return RunTests(args);
                    case "mutate":
                        return Mutate();
                    case "help":
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return ExitOk;
                    default:
                        Console.Error.WriteLine("unknown object-reuse mode: " + mode);
                        PrintUsage();
                        return ExitUsage;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("object-reuse command failed: " + ex.GetType().Name + ": " + ex.Message);
                return ExitFault;
            }
        }

        private static int ListScenarios()
        {
            foreach (var document in ScenarioCatalog.All)
            {
                Console.WriteLine(document.Name + "  (" + document.Operations.Count + " operations)");
            }

            return ExitOk;
        }

        private static int PrintScripts()
        {
            foreach (var document in ScenarioCatalog.All)
            {
                Console.WriteLine(document.ToScript());
                Console.WriteLine();
            }

            return ExitOk;
        }

        /// <summary>Replays every scenario in arrival and reverse completion order and prints the report.</summary>
        private static int Replay()
        {
            var failed = 0;
            foreach (var document in ScenarioCatalog.All)
            {
                var orders = OrderReplay.Run(document);
                var handles = new List<string>();
                foreach (var operation in document.Operations)
                {
                    if (operation.Kind == "request") handles.Add(operation.Handle);
                }

                Console.WriteLine("== " + document.Name + " ==");
                foreach (var order in orders)
                {
                    Console.WriteLine("  order " + order.Name + ": " + order.Observation.ReuseReport.Replace("\n", "\n  "));
                    foreach (var violation in ScenarioInvariants.Check(order.Observation, handles))
                    {
                        Console.WriteLine("  VIOLATION " + violation);
                        failed++;
                    }
                }

                foreach (var violation in ScenarioInvariants.CheckAcrossOrders(
                             orders[0].Observation, orders[1].Observation, handles, handles))
                {
                    Console.WriteLine("  CROSS-ORDER VIOLATION " + violation);
                    failed++;
                }

                Console.WriteLine("  outcomes that may vary:" + ScenarioInvariants.DescribeVaryingOutcomes(
                    orders[0].Observation, orders[1].Observation, handles));
            }

            Console.WriteLine();
            Console.WriteLine("allowed to vary:");
            foreach (var line in ScenarioInvariants.AllowedToVary) Console.WriteLine("  - " + line);
            Console.WriteLine("must hold:");
            foreach (var line in ScenarioInvariants.MustHold) Console.WriteLine("  - " + line);

            return failed == 0 ? ExitOk : ExitFault;
        }

        /// <summary>Writes a run report to stdout and, optionally, to a file (--out=path).</summary>
        private static int RunReport()
        {
            var report = new RunReport();
            foreach (var document in ScenarioCatalog.All)
            {
                var orders = OrderReplay.Run(document);
                var handles = new List<string>();
                foreach (var operation in document.Operations)
                {
                    if (operation.Kind == "request") handles.Add(operation.Handle);
                }

                foreach (var order in orders)
                {
                    report.AddScenario(document, order, ScenarioInvariants.Check(order.Observation, handles));
                }

                report.AddCrossOrder(document,
                    ScenarioInvariants.CheckAcrossOrders(orders[0].Observation, orders[1].Observation, handles, handles));
            }

            var probeEvent = GenerationFenceProbe.Run(enforceFence: false);
            report.AddFault("generation-fence", probeEvent.Script, GenerationFenceProbe.Describe(probeEvent),
                probeEvent.ExposedError);

            var text = report.ToText();
            Console.WriteLine(text);

            var outPath = FindArg("--out=");
            if (outPath != null)
            {
                var dir = Path.GetDirectoryName(outPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, text);
                Console.WriteLine();
                Console.WriteLine("report written to " + outPath);
            }

            return report.FailureCount == 0 ? ExitOk : ExitFault;
        }

        private static int RunTests(string[] args)
        {
            var filter = FindArg("--filter=");
            var category = FindArg("--category=") ?? "reuse";
            // Delegated to Program.Main's runner so the CLI and the batch run share one definition.
            return Program.RunTests(filter, category);
        }

        private static int Mutate()
        {
            if (!SourceMutationDemo.CanRun(out var reason))
            {
                Console.Error.WriteLine("cannot run the source mutation demo here: " + reason);
                return ExitUsage;
            }

            var outcome = SourceMutationDemo.Run();
            Console.WriteLine(SourceMutationDemo.Describe(outcome));
            if (!outcome.ExposedError)
            {
                Console.Error.WriteLine("the removed build did not fail; the demonstration is not valid here");
                return ExitFault;
            }

            if (!outcome.RestoredAndClean)
            {
                Console.Error.WriteLine("the restored build did not pass");
                return ExitFault;
            }

            Console.WriteLine("fault demonstrated: removed build failed, restored build passed.");
            return ExitOk;
        }

        private static string FindArg(string prefix)
        {
            // Program passes the flattened args through Environment for the CLI.
            var raw = Environment.GetEnvironmentVariable("LIFECYCLE_CLI_ARGS");
            if (string.IsNullOrEmpty(raw)) return null;
            foreach (var arg in raw.Split('\u001f'))
            {
                if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg.Substring(prefix.Length);
            }

            return null;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("usage: LifecycleCoreTests run object-reuse <mode> [options]");
            Console.WriteLine();
            Console.WriteLine("  list                 list the replayable object-reuse scenarios");
            Console.WriteLine("  script               print each scenario's shortest operation sequence");
            Console.WriteLine("  replay                replay every scenario in two completion orders and check invariants");
            Console.WriteLine("  report [--out=path]   write a run report (scenario results, invariants, fault injection)");
            Console.WriteLine("  tests [--filter=x]   run the reuse test subset (same suite as the batch run)");
            Console.WriteLine("  mutate               remove one generation validation from a temp copy and prove it fails");
            Console.WriteLine();
            Console.WriteLine("Graphical entry point (preferred): Unity menu Lifecycle > Open Object Reuse Sample.");
        }
    }
}
