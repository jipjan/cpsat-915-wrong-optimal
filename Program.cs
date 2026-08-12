using Google.OrTools.Sat;

// Independent validation of SmartComp3#346's upstream claim, from the proto file alone.
// Usage: protocheck <model.pb> [inspect|solve <params> [pinVarName]]

var proto = CpModelProto.Parser.ParseFrom(File.ReadAllBytes(args[0]));
var mode = args.Length > 1 ? args[1] : "inspect";

if (mode == "inspect")
{
    Console.WriteLine($"vars={proto.Variables.Count} constraints={proto.Constraints.Count}");

    var typeCounts = new Dictionary<string, int>();
    var withEnforcement = 0;
    foreach (var ct in proto.Constraints)
    {
        var key = ct.ConstraintCase.ToString();
        typeCounts[key] = typeCounts.GetValueOrDefault(key) + 1;
        if (ct.EnforcementLiteral.Count > 0) withEnforcement++;
    }
    foreach (var (k, v) in typeCounts.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  constraint type {k}: {v}");
    Console.WriteLine($"  rows with enforcement literals: {withEnforcement}");

    var obj = proto.Objective;
    Console.WriteLine($"objective: terms={obj.Vars.Count} offset={obj.Offset} scalingFactor={obj.ScalingFactor} "
        + $"domain=[{string.Join(",", obj.Domain)}] integerBefore={obj.IntegerBeforeOffset} "
        + $"integerAfter={obj.IntegerAfterOffset} integerScaling={obj.IntegerScalingFactor}");
    Console.WriteLine($"floatingPointObjective present: {proto.FloatingPointObjective != null && proto.FloatingPointObjective.Vars.Count > 0}");
    Console.WriteLine($"solutionHint present: {proto.SolutionHint != null && proto.SolutionHint.Vars.Count > 0}");
    Console.WriteLine($"searchStrategy entries: {proto.SearchStrategy.Count}");
    Console.WriteLine($"assumptions: {proto.Assumptions.Count} symmetry present: {proto.Symmetry != null}");

    for (var i = 0; i < proto.Variables.Count; i++)
        if (proto.Variables[i].Name == "supply_dim_3_8")
            Console.WriteLine($"supply_dim_3_8: index={i} domain=[{string.Join(",", proto.Variables[i].Domain)}]");

    // Largest |coefficient| and widest domain, for the numerics narrative.
    long maxCoef = 0, maxDomAbs = 0;
    foreach (var ct in proto.Constraints)
        if (ct.ConstraintCase == ConstraintProto.ConstraintOneofCase.Linear)
            foreach (var c in ct.Linear.Coeffs)
                maxCoef = Math.Max(maxCoef, Math.Abs(c));
    foreach (var v in proto.Variables)
        foreach (var d in v.Domain)
            maxDomAbs = Math.Max(maxDomAbs, Math.Abs(d));
    Console.WriteLine($"max |linear coeff|={maxCoef} max |var bound|={maxDomAbs}");
    return;
}

if (mode == "names")
{
    // Group variable names by their non-numeric skeleton, to see what a public upload exposes.
    var groups = new Dictionary<string, int>();
    var unnamed = 0;
    foreach (var v in proto.Variables)
    {
        if (string.IsNullOrEmpty(v.Name)) { unnamed++; continue; }
        var skeleton = System.Text.RegularExpressions.Regex.Replace(v.Name, @"\d+", "#");
        groups[skeleton] = groups.GetValueOrDefault(skeleton) + 1;
    }
    Console.WriteLine($"unnamed vars: {unnamed}; distinct name skeletons: {groups.Count}");
    foreach (var (k, v) in groups.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {v,6} x {k}");
    return;
}

if (mode == "compact")
{
    Compact.Run(proto, args[2]);
    return;
}

if (mode == "reduce")
{
    Reduce.Run(proto, budgetMinutes: int.Parse(args[2]), seconds: int.Parse(args[3]));
    return;
}

if (mode == "savehint")
{
    var pi = -1;
    for (var i = 0; i < proto.Variables.Count; i++)
        if (proto.Variables[i].Name == "supply_dim_3_8") { pi = i; break; }
    var row = new ConstraintProto { Linear = new LinearConstraintProto() };
    row.Linear.Vars.Add(pi); row.Linear.Coeffs.Add(1);
    row.Linear.Domain.Add(1); row.Linear.Domain.Add(1);
    proto.Constraints.Add(row);
    var mm = new CpModel();
    mm.Model.MergeFrom(proto);
    var ss = new CpSolver { StringParameters = "num_search_workers:1,random_seed:1" };
    var stt = ss.Solve(mm);
    Console.WriteLine($"pinned -> {stt} objective={ss.ObjectiveValue}");
    File.WriteAllLines("hint.txt", ss.Response.Solution.Select(v => v.ToString()));
    return;
}

if (mode == "probe")
{
    // Exit code 0 = hint survived presolve; non-zero (CHECK failure) = presolve broke it.
    var vals = File.ReadAllLines("hint.txt").Select(long.Parse).ToArray();
    proto.SolutionHint = new PartialVariableAssignment();
    for (var i = 0; i < vals.Length; i++) { proto.SolutionHint.Vars.Add(i); proto.SolutionHint.Values.Add(vals[i]); }
    var mm = new CpModel();
    mm.Model.MergeFrom(proto);
    var ops = args[2];
    var ss = new CpSolver
    {
        StringParameters = "num_search_workers:1,random_seed:1,log_search_progress:true,"
                         + "debug_crash_if_presolve_breaks_hint:true,max_time_in_seconds:40,"
                         + $"debug_max_num_presolve_operations:{ops}",
    };
    var stt = ss.Solve(mm);
    Console.WriteLine($"ops={ops} -> {stt} SURVIVED");
    return;
}

if (mode == "diag")
{
    // Step 1: solve the PINNED model to obtain the true optimum's full assignment.
    var pinIdx = -1;
    for (var i = 0; i < proto.Variables.Count; i++)
        if (proto.Variables[i].Name == "supply_dim_3_8") { pinIdx = i; break; }
    var pinRow = new ConstraintProto { Linear = new LinearConstraintProto() };
    pinRow.Linear.Vars.Add(pinIdx);
    pinRow.Linear.Coeffs.Add(1);
    pinRow.Linear.Domain.Add(1);
    pinRow.Linear.Domain.Add(1);

    var pinnedProto = proto.Clone();
    pinnedProto.Constraints.Add(pinRow);
    var pinnedModel = new CpModel();
    pinnedModel.Model.MergeFrom(pinnedProto);
    var s1 = new CpSolver { StringParameters = "num_search_workers:1,random_seed:1" };
    var st1 = s1.Solve(pinnedModel);
    Console.WriteLine($"[1] pinned solve -> {st1} objective={s1.ObjectiveValue}");
    if (st1 is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible)) return;
    var good = s1.Response.Solution.ToArray();

    // Step 2: feed that assignment to the UNPINNED model as a complete hint, and ask CP-SAT
    // to report the presolve operation that makes the hint infeasible.
    var hinted = proto.Clone();
    hinted.SolutionHint = new PartialVariableAssignment();
    for (var i = 0; i < good.Length; i++)
    {
        hinted.SolutionHint.Vars.Add(i);
        hinted.SolutionHint.Values.Add(good[i]);
    }
    var m2 = new CpModel();
    m2.Model.MergeFrom(hinted);
    var extra = args.Length > 2 ? args[2] : "";
    var s2 = new CpSolver
    {
        StringParameters = "num_search_workers:1,random_seed:1,log_search_progress:true,"
                         + "debug_crash_if_presolve_breaks_hint:true," + extra,
    };
    s2.StringParameters += "";
    var log = new System.Text.StringBuilder();
    s2.SetLogCallback(line => { log.AppendLine(line); });
    var st2 = s2.Solve(m2);
    File.WriteAllText("diag.log", log.ToString());
    Console.WriteLine($"[2] unpinned+hint -> {st2} objective={s2.ObjectiveValue} (log in diag.log)");
    return;
}

if (mode == "solve")
{
    var parms = args[2];
    if (args.Length > 3)
    {
        var pinValue = args.Length > 4 ? long.Parse(args[4]) : 1;
        var pinIndex = -1;
        for (var i = 0; i < proto.Variables.Count; i++)
            if (proto.Variables[i].Name == args[3]) { pinIndex = i; break; }
        if (pinIndex < 0) { Console.WriteLine($"pin var not found: {args[3]}"); return; }
        var pin = new ConstraintProto { Linear = new LinearConstraintProto() };
        pin.Linear.Vars.Add(pinIndex);
        pin.Linear.Coeffs.Add(1);
        pin.Linear.Domain.Add(pinValue);
        pin.Linear.Domain.Add(pinValue);
        proto.Constraints.Add(pin);
        Console.WriteLine($"pinned {args[3]} == {pinValue}");
    }
    var originalRows = args.Length > 3 ? proto.Constraints.Count - 1 : proto.Constraints.Count;
    var model = new CpModel();
    model.Model.MergeFrom(proto);
    var solver = new CpSolver { StringParameters = parms == "default" ? "" : parms };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var status = solver.Solve(model);
    Console.WriteLine($"params='{parms}' -> {status} objective={solver.ObjectiveValue} bound={solver.Response.BestObjectiveBound} wall={sw.Elapsed.TotalSeconds:F1}s");

    // Independent check: verify the returned solution against the ORIGINAL (unpinned) file's rows
    // by plain Int128 arithmetic — no solver trusted. A pinned run's solution passing here proves
    // the point is feasible in the unpinned model too.
    if (status is CpSolverStatus.Optimal or CpSolverStatus.Feasible)
    {
        var solution = solver.Response.Solution;
        int checkedRows = 0, skippedNonLinear = 0, violations = 0;
        for (var c = 0; c < originalRows; c++)
        {
            var ct = proto.Constraints[c];
            if (ct.ConstraintCase != ConstraintProto.ConstraintOneofCase.Linear) { skippedNonLinear++; continue; }
            var enforced = true;
            foreach (var lit in ct.EnforcementLiteral)
                if (!(lit >= 0 ? solution[lit] == 1 : solution[~lit] == 0)) { enforced = false; break; }
            if (!enforced) continue;
            Int128 sum = 0;
            for (var i = 0; i < ct.Linear.Vars.Count; i++)
                sum += (Int128)ct.Linear.Coeffs[i] * solution[ct.Linear.Vars[i]];
            var ok = false;
            for (var d = 0; d + 1 < ct.Linear.Domain.Count && !ok; d += 2)
                ok = sum >= ct.Linear.Domain[d] && sum <= ct.Linear.Domain[d + 1];
            checkedRows++;
            if (!ok && violations++ < 5)
                Console.WriteLine($"VIOLATED: sum={sum} domain=[{string.Join(",", ct.Linear.Domain)}]");
        }
        var domainOk = true;
        for (var i = 0; i < proto.Variables.Count && domainOk; i++)
        {
            var dom = proto.Variables[i].Domain;
            var inDomain = false;
            for (var d = 0; d + 1 < dom.Count && !inDomain; d += 2)
                inDomain = solution[i] >= dom[d] && solution[i] <= dom[d + 1];
            domainOk = inDomain;
        }
        Int128 objSum = 0;
        for (var i = 0; i < proto.Objective.Vars.Count; i++)
            objSum += (Int128)proto.Objective.Coeffs[i] * solution[proto.Objective.Vars[i]];
        Console.WriteLine($"check vs unpinned file: rows checked={checkedRows} skippedNonLinear={skippedNonLinear} "
            + $"VIOLATIONS={violations} domainsOk={domainOk} objectiveRecomputed={objSum}");
    }
}
