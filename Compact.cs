using Google.Protobuf;
using Google.OrTools.Sat;

// Drop variables no surviving constraint or the objective references, and remap
// every index. A reference is either a variable index (>= 0) or a negated
// literal (NegatedRef(v) == -v - 1), so both forms have to be rewritten.

internal static class Compact
{
    private static int Map(int reference, int[] newIndex)
    {
        if (reference >= 0) return newIndex[reference];
        var mapped = newIndex[~reference];
        return ~mapped;
    }

    public static void Run(CpModelProto proto, string outPath)
    {
        var used = new bool[proto.Variables.Count];
        void Use(int reference) => used[reference >= 0 ? reference : ~reference] = true;

        foreach (var ct in proto.Constraints)
        {
            foreach (var lit in ct.EnforcementLiteral) Use(lit);
            if (ct.ConstraintCase == ConstraintProto.ConstraintOneofCase.Linear)
                foreach (var v in ct.Linear.Vars) Use(v);
            else
                throw new InvalidOperationException($"unhandled constraint type {ct.ConstraintCase}");
        }
        foreach (var v in proto.Objective.Vars) Use(v);

        var newIndex = new int[proto.Variables.Count];
        Array.Fill(newIndex, -1);
        var result = new CpModelProto { Objective = new CpObjectiveProto() };
        for (var i = 0; i < used.Length; i++)
        {
            if (!used[i]) continue;
            newIndex[i] = result.Variables.Count;
            result.Variables.Add(proto.Variables[i].Clone());
        }

        foreach (var ct in proto.Constraints)
        {
            var copy = ct.Clone();
            for (var i = 0; i < copy.EnforcementLiteral.Count; i++)
                copy.EnforcementLiteral[i] = Map(copy.EnforcementLiteral[i], newIndex);
            for (var i = 0; i < copy.Linear.Vars.Count; i++)
                copy.Linear.Vars[i] = Map(copy.Linear.Vars[i], newIndex);
            result.Constraints.Add(copy);
        }

        result.Objective.ScalingFactor = proto.Objective.ScalingFactor;
        result.Objective.Offset = proto.Objective.Offset;
        foreach (var d in proto.Objective.Domain) result.Objective.Domain.Add(d);
        for (var i = 0; i < proto.Objective.Vars.Count; i++)
        {
            result.Objective.Vars.Add(Map(proto.Objective.Vars[i], newIndex));
            result.Objective.Coeffs.Add(proto.Objective.Coeffs[i]);
        }

        File.WriteAllBytes(outPath, result.ToByteArray());
        File.WriteAllText(Path.ChangeExtension(outPath, ".textproto"), result.ToString());
        Console.WriteLine($"compacted: {proto.Variables.Count} -> {result.Variables.Count} vars, "
                        + $"{result.Constraints.Count} constraints -> {outPath}");
    }
}
