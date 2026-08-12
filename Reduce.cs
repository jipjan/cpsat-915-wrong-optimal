using Google.Protobuf;
using Google.OrTools.Sat;

// Delta-debugger for the CP-SAT presolve unsoundness.
//
// Oracle: a sub-model reproduces the bug iff solving it with DEFAULT parameters
// disagrees with solving it with presolve_inclusion_work_limit:0 (which disables
// the ProcessAtMostOneAndLinear / inclusion family). Both directions count:
//   - default reports a worse optimum than inclusion:0  (wrong optimality proof)
//   - default reports INFEASIBLE where inclusion:0 finds a solution (false infeasible)
// inclusion:0 is treated as the reference; it agrees with CBC on the full model.
//
// Strategy: ddmin over the constraint list (remove chunks, restart on success),
// then drop unused variables and remap indices.

internal static class Reduce
{
    private const string Common = "num_search_workers:1,random_seed:1,max_time_in_seconds:";

    private static (CpSolverStatus, double) SolveWith(CpModelProto proto, string extra, int seconds)
    {
        var m = new CpModel();
        m.Model.MergeFrom(proto);
        var s = new CpSolver { StringParameters = Common + seconds + (extra.Length > 0 ? "," + extra : "") };
        var st = s.Solve(m);
        return (st, st is CpSolverStatus.Optimal or CpSolverStatus.Feasible ? s.ObjectiveValue : double.NaN);
    }

    // True when the sub-model still exhibits the disagreement.
    private static bool Reproduces(CpModelProto proto, int seconds)
    {
        var (refStatus, refObj) = SolveWith(proto, "presolve_inclusion_work_limit:0", seconds);
        // The reference must reach a definite answer, or we cannot judge.
        if (refStatus is not (CpSolverStatus.Optimal or CpSolverStatus.Infeasible)) return false;

        var (defStatus, defObj) = SolveWith(proto, "", seconds);
        if (defStatus is not (CpSolverStatus.Optimal or CpSolverStatus.Infeasible)) return false;

        if (refStatus == CpSolverStatus.Optimal && defStatus == CpSolverStatus.Infeasible) return true;
        if (refStatus == CpSolverStatus.Optimal && defStatus == CpSolverStatus.Optimal) return defObj != refObj;
        return false;
    }

    private static CpModelProto Build(CpModelProto full, bool[] keep)
    {
        var p = full.Clone();
        p.Constraints.Clear();
        for (var i = 0; i < keep.Length; i++)
            if (keep[i]) p.Constraints.Add(full.Constraints[i]);
        return p;
    }

    public static void Run(CpModelProto full, int budgetMinutes, int seconds)
    {
        var deadline = DateTime.UtcNow.AddMinutes(budgetMinutes);
        var n = full.Constraints.Count;
        var keep = new bool[n];
        Array.Fill(keep, true);

        if (!Reproduces(full, seconds))
        {
            Console.WriteLine("full model does not reproduce under the oracle - aborting");
            return;
        }
        Console.WriteLine($"start: {n} constraints, oracle holds");

        var granularity = 2;
        var tests = 0;
        while (DateTime.UtcNow < deadline)
        {
            var live = Enumerable.Range(0, n).Where(i => keep[i]).ToArray();
            if (live.Length <= 1) break;
            if (granularity > live.Length) break;

            var progress = false;
            var chunk = (live.Length + granularity - 1) / granularity;
            for (var start = 0; start < live.Length && DateTime.UtcNow < deadline; start += chunk)
            {
                var candidate = (bool[])keep.Clone();
                for (var k = start; k < Math.Min(start + chunk, live.Length); k++) candidate[live[k]] = false;
                var remaining = candidate.Count(b => b);
                if (remaining == 0) continue;

                tests++;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ok = Reproduces(Build(full, candidate), seconds);
                Console.WriteLine($"  test {tests}: g={granularity} try {remaining} rows -> {(ok ? "REPRO (kept)" : "lost")} [{sw.Elapsed.TotalSeconds:F1}s]");
                Console.Out.Flush();
                if (!ok) continue;

                keep = candidate;
                progress = true;
                File.WriteAllBytes("reduced.pb", Build(full, keep).ToByteArray());
                break;
            }

            if (progress) granularity = 2;
            else granularity *= 2;
        }

        var finalProto = Build(full, keep);
        File.WriteAllBytes("reduced.pb", finalProto.ToByteArray());
        Console.WriteLine($"done: {finalProto.Constraints.Count} constraints remain after {tests} tests "
                        + $"(started at {n}); written to reduced.pb");
    }
}
