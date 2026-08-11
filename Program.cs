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
    var model = new CpModel();
    model.Model.MergeFrom(proto);
    var solver = new CpSolver { StringParameters = parms == "default" ? "" : parms };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var status = solver.Solve(model);
    Console.WriteLine($"params='{parms}' -> {status} objective={solver.ObjectiveValue} bound={solver.Response.BestObjectiveBound} wall={sw.Elapsed.TotalSeconds:F1}s");
}
