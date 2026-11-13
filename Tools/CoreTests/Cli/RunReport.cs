using System;
using System.Collections.Generic;
using System.Text;
using Lifecycle.CoreTests.Scenarios;

namespace Lifecycle.CoreTests.Cli
{
    /// <summary>
    /// Structured run report. It records each scenario's two completion orders, the invariant
    /// violations found, the outcomes that were allowed to vary, and the fault-injection results.
    /// A failing report names the shortest operation sequence that reproduced the fault.
    /// </summary>
    internal sealed class RunReport
    {
        private readonly List<string> _sections = new List<string>();
        private readonly List<string> _failures = new List<string>();

        public int FailureCount => _failures.Count;
        public IReadOnlyList<string> FailureLines => _failures;

        public void AddScenario(ScenarioDocument document, OrderReplay.OrderResult order,
            IReadOnlyList<InvariantViolation> violations)
        {
            var sb = new StringBuilder();
            sb.Append("scenario '").Append(document.Name).Append("' order=").Append(order.Name)
                .Append(": leases=").Append(order.Observation.LeasesHeld)
                .Append(" subs=").Append(order.Observation.ActiveSubscriptions)
                .Append(" pendingWork=").Append(order.Observation.PendingWorkItems)
                .Append(" unfinished=").Append(order.Observation.UnfinishedRequests)
                .Append(" stale=").Append(order.Observation.StaleCallbacks)
                .Append(" created=").Append(order.Observation.Created)
                .Append(" destroyed=").Append(order.Observation.Destroyed);
            foreach (var violation in violations)
            {
                sb.Append("\n  VIOLATION ").Append(violation);
                sb.Append("\n  shortest failing sequence:\n").Append(Indent(document.ToScript()));
                _failures.Add(document.Name + "/" + order.Name + ": " + violation);
            }

            _sections.Add(sb.ToString());
        }

        public void AddCrossOrder(ScenarioDocument document, IReadOnlyList<InvariantViolation> violations)
        {
            var sb = new StringBuilder();
            sb.Append("scenario '").Append(document.Name).Append("' cross-order comparison: ");
            if (violations.Count == 0)
            {
                sb.Append("conservation holds");
            }
            else
            {
                foreach (var violation in violations)
                {
                    sb.Append("\n  CROSS-ORDER VIOLATION ").Append(violation);
                    sb.Append("\n  shortest failing sequence:\n").Append(Indent(document.ToScript()));
                    _failures.Add(document.Name + ": " + violation);
                }
            }

            _sections.Add(sb.ToString());
        }

        public void AddFault(string name, string script, string detail, bool exposedAsExpected)
        {
            var sb = new StringBuilder();
            sb.Append("fault-injection '").Append(name).Append("': ").Append(detail)
                .Append(exposedAsExpected ? " [error exposed as expected]" : " [not exposed]");
            sb.Append("\n  shortest operation sequence:\n").Append(Indent(script));
            if (!exposedAsExpected)
            {
                _failures.Add("fault-injection " + name + " did not expose an error");
            }

            _sections.Add(sb.ToString());
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("Lifecycle object-reuse run report\n");
            sb.Append("generated ").Append(DateTime.UtcNow.ToString("o")).Append('\n');
            sb.Append("failures ").Append(_failures.Count).Append('\n');
            sb.Append('\n');
            foreach (var section in _sections)
            {
                sb.Append(section).Append('\n');
            }

            if (_failures.Count > 0)
            {
                sb.Append("\nfailures:\n");
                foreach (var failure in _failures) sb.Append("  - ").Append(failure).Append('\n');
            }

            return sb.ToString();
        }

        private static string Indent(string text)
        {
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                sb.Append("    ").Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }

            return sb.ToString();
        }
    }
}
