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

## Root cause (found 2026-08-12)

`ActivityBoundHelper::RemoveEnforcementThatMakesConstraintTrivial()`
(`ortools/sat/presolve_util.cc`) accumulates `int64_t` coefficients into two
`int` variables. Boolean terms totalling more than 2^31 wrap them, so
`max_activity` goes negative, `Domain(min_activity, max_activity)` is **empty**,
`IsIncludedIn(rhs)` is trivially true, and the enforcement literal is removed —
turning `enf => row` into an unconditional `row`.

Instrumented v9.15 on `repro-192-constraints.pb`:

```
OC-DIAG int32-overflow: int32[0, -378941584]  int64[0, 8210993008]
OC-DIAG remove-enf lit=1688 activity[0, -378941584] empty=1 broke=0
```

`presolve_util-5293.patch` fixes it (`int` -> `int64_t`, plus guarding two
"abort" `break`s that left the bounds incomplete but still ran the test).
Verified against a from-source v9.15 build, **default parameters**:

| model | v9.15 | patched |
|---|---|---|
| `model.pb` (90'770 vars / 86'541 rows) | OPTIMAL 9'086'429 | OPTIMAL **6'012'953** |
| `repro-192-constraints.pb` | OPTIMAL 1'834'217 | OPTIMAL **1'738'487** |

`repro-192-constraints.pb` is `model.pb` delta-debugged to 192 constraints /
1'150 variables; it solves in 0,2 s. Workaround without a patched build:
`presolve_inclusion_work_limit:0`, which disables the routine.
