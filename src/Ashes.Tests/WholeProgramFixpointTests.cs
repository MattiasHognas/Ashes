using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class WholeProgramFixpointTests
{
    [Test]
    public void RunToFixpoint_stops_as_soon_as_an_iteration_reports_no_change()
    {
        int runCount = 0;
        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            runCount++;
            return false;
        });

        runCount.ShouldBe(1);
    }

    [Test]
    public void RunToFixpoint_keeps_iterating_while_changes_are_reported()
    {
        int remaining = 3;
        int runCount = 0;
        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            runCount++;
            if (remaining == 0)
            {
                return false;
            }

            remaining--;
            return true;
        });

        // 3 iterations report a change (remaining 3 -> 2 -> 1 -> 0), plus the final
        // no-change iteration that stops the loop.
        runCount.ShouldBe(4);
    }

    [Test]
    public void RunToFixpoint_computes_a_shrinking_least_fixpoint_correctly()
    {
        // Mirrors ComputeNonAllocatingFunctions/ComputeEvaluableFunctions's shape: start with
        // every node a candidate, remove any whose dependency isn't (also) a candidate, until
        // stable. "c" depends on "b" which depends on a node not in the candidate set at all
        // ("external"), so both "b" and "c" should be knocked out, leaving only "a".
        var candidates = new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };
        var dependsOn = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["b"] = "external",
            ["c"] = "b",
        };

        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach (var node in candidates.ToArray())
            {
                if (dependsOn.TryGetValue(node, out string? dependency) && !candidates.Contains(dependency))
                {
                    changed |= candidates.Remove(node);
                }
            }

            return changed;
        });

        // HashSet<T>.ShouldBe(HashSet<T>) is sequence-order-sensitive in Shouldly, not
        // set-equality (see IrControlFlowGraphTests) — compare via sorted sequences.
        candidates.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(new[] { "a" });
    }

    [Test]
    public void RunToFixpoint_computes_a_growing_least_fixpoint_correctly()
    {
        // Mirrors PropagateLiveHandlerEffects/PropagateCoroutineEffects's shape: start with a
        // seed set and transitively add every reachable callee until stable.
        var live = new HashSet<string>(StringComparer.Ordinal) { "entry" };
        var calleesByCaller = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["entry"] = ["a"],
            ["a"] = ["b"],
            ["b"] = ["c"],
            ["c"] = [],
            ["unreached"] = ["z"],
        };

        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach (var caller in live.ToArray())
            {
                if (calleesByCaller.TryGetValue(caller, out var callees))
                {
                    foreach (var callee in callees)
                    {
                        changed |= live.Add(callee);
                    }
                }
            }

            return changed;
        });

        live.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(new[] { "a", "b", "c", "entry" });
    }
}
