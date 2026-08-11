# CP-SAT wrong optimality proof — repro for a google/or-tools issue

OR-Tools **9.15.6755** (`Google.OrTools` NuGet, C#/.NET 10, Windows 11 x64).

`model.pb.zip` holds a binary `CpModelProto` (90'770 variables, 86'541 constraints, all linear,
validator-accepted, no hints/strategies/assumptions). Solving it three ways with default
parameters gives mutually inconsistent optimality certificates:

| Run | Extra constraint | Status | Objective | Log |
|---|---|---|---|---|
| A | none | OPTIMAL | 9'086'429 | `runA-unpinned.log` |
| B | `supply_dim_3_8 == 1` | OPTIMAL | **6'012'953** | `runB-pinned-1.log` |
| C | `supply_dim_3_8 == 0` | OPTIMAL | 9'086'429 | `runC-pinned-0.log` |

`supply_dim_3_8` is a Boolean, so B and C partition A's feasible set: A's optimum must be
min(B, C) = 6'012'953, yet A certifies 9'086'429 with a matching best bound. After every solve
the runner independently verifies the returned solution against the **original, unpinned**
file's rows by plain Int128 arithmetic (enforcement literals honoured, domains checked,
objective recomputed) — run B's cheaper point passes with zero violations, so it is feasible
in the model run A was solved on.

## Run it

```bash
unzip model.pb.zip
dotnet run -c Release -- model.pb solve default                     # -> OPTIMAL 9086429
dotnet run -c Release -- model.pb solve default supply_dim_3_8 1    # -> OPTIMAL 6012953
dotnet run -c Release -- model.pb solve default supply_dim_3_8 0    # -> OPTIMAL 9086429
dotnet run -c Release -- model.pb inspect                           # model anatomy
dotnet run -c Release -- model.pb names                             # variable-name survey
```

Also reproduces with `num_search_workers:1,random_seed:1` (deterministic) and with 8 workers.
