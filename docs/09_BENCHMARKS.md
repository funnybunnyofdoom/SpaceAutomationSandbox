# 09 — Simulation benchmarks (#20)

These measure; they don't promise anything (docs/02, Performance). Rerun after big changes and add a dated row set.

```powershell
dotnet run -c Release --project tools/Benchmarks          # 10, 100, 1,000, 10,000 ships
dotnet run -c Release --project tools/Benchmarks -- 500   # any counts
```

**Scenario:** the default starter sector (seed 20261120). N automated starter ships each run a mining job, spread over Rock-1, Rock-2 and Rock-3, with the hold returning at 25% full, all delivering to the starter shipyard. Timing starts after 40 warm-up ticks, so most ships are mid-flight using the autopilot (obstacle checks, docking-port searches), which is the expensive part. Only the simulation is timed: no Godot, no rendering, no scene nodes. A thousand ships still means zero scene nodes until a view draws them (docs/02: "no node per NPC").

**Budget:** the simulation runs 20 ticks per second, so 50 ms per tick is everything. Rendering, UI and the rest of the frame need most of that.

## 2026-11-21 — dev PC (16 logical CPUs, .NET 8.0.20, Release, single-threaded)

| Automated ships | Setup | Ticks timed | ms per tick (mean) | ms per tick (worst 1%) | % of 50 ms budget |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 0.01 s | 2,400 | 0.060 | 0.136 | 0.1% |
| 100 | 0.00 s | 2,400 | 0.248 | 0.760 | 0.5% |
| 1,000 | 0.04 s | 600 | 1.127 | 1.325 | 2.3% |
| 10,000 | 0.91 s | 100 | 11.697 | 12.688 | 23.4% |

**Reading it:**
- **Re-run 2026-11-21 after power, kits, requests and job history (#7, #10, #8, #1, #19):** 10,000 ships 13.3 ms per tick mean, 14.8 ms worst 1% (26.6% of budget), up from 11.7 ms.
- **Cost per ship is roughly linear** at about 1.1–1.2 µs per ship per tick at 1,000 to 10,000 ships, so the job and autopilot code has no hidden quadratic step yet.
- **10,000 ships fit the budget, but not comfortably.** Combined with rendering, they'd need the cheaper distant simulation of #17 (abstract transit and LOD), or the swarm aggregation of #31.
- **Setup grows faster than linearly** (0.9 s for 10,000), because `AddCraft` keeps its list sorted on every add. That's fine for gameplay, which adds ships one at a time, but worth fixing if large fleets are ever created at once.
- **The scenario is contention-heavy.** 10,000 ships share one shipyard with two docking ports and 400 t of storage, so most of them end up waiting. Waiting ships are cheap, so a busier economy with many stations would cost more per ship. Treat these numbers as a lower bound for flying-heavy fleets.
