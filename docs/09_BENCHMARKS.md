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
- **Re-run 2026-11-22 after cheaper distant simulation (#17):** watched (the usual benchmark) 10,000 ships 43.7 ms per tick (87%), 1,000 ships 4.2 ms. **Far from the player** (`--far`: no piloted craft, timed after a minute so the fleet has left the yard): **10,000 ships 2.3 ms per tick (4.6%)**, 4.6 ms worst 1%; 1,000 ships 0.7 ms. Craft in transit or waiting far away cost almost nothing; what's left is the jobs that are mining or docking. Run it with `dotnet run -c Release --project tools/Benchmarks -- --far 1000 10000`.
- **Re-run 2026-11-22 after traffic rules (#55):** 10,000 ships **47.3 ms per tick mean (95% of budget)**, 61.7 ms worst 1%; 1,000 ships 4.3 ms (9%). This benchmark times the first seconds after all 10,000 ships launch at once from one yard, the worst case for traffic: about 11 ms is craft collisions, 7 ms giving way, the rest the existing simulation and the traffic grids. Fine for hundreds of ships; very large fleets at one station need cheaper distant simulation (#17).
- **Re-run 2026-11-21 after crew and life support (#11):** 10,000 ships 18.4 ms per tick mean (37% of budget); 1,000 ships 1.7 ms.
- **Re-run 2026-11-21 after gravity and collisions (#32, #34):** 10,000 ships **16.6 ms per tick mean** (33% of budget), 37.7 ms worst 1%; 1,000 ships 1.4 ms. Every free craft now gets gravity, and automated ones station avoidance and a collision check against the craft that can collide with them (stations, player ships); automated craft don't collide with each other, which keeps the collision grid small when thousands crowd a station. A first version that scanned every craft for stations ran at 218 ms.
- **Re-run 2026-11-21 after the star system (#53):** 24 bodies instead of 7 first doubled the cost (27 ms per tick at 10,000 ships), because every craft checks every body each tick. Body positions are now cached per tick in flat arrays and the autopilot checks for cruise once a second: **10,000 ships 11.1 ms per tick mean, 15.7 ms worst 1% (22% of budget)**; 1,000 ships 1.2 ms. The benchmark mines the home neighbourhood only (as before), so it stays comparable.
- **Re-run 2026-11-21 after power, kits, requests and job history (#7, #10, #8, #1, #19):** 10,000 ships 13.3 ms per tick mean, 14.8 ms worst 1% (26.6% of budget), up from 11.7 ms.
- **Cost per ship is roughly linear** at about 1.1–1.2 µs per ship per tick at 1,000 to 10,000 ships, so the job and autopilot code has no hidden quadratic step yet.
- **10,000 ships fit the budget, but not comfortably.** Combined with rendering, they'd need the cheaper distant simulation of #17 (abstract transit and LOD), or the swarm aggregation of #31.
- **Setup grows faster than linearly** (0.9 s for 10,000), because `AddCraft` keeps its list sorted on every add. That's fine for gameplay, which adds ships one at a time, but worth fixing if large fleets are ever created at once.
- **The scenario is contention-heavy.** 10,000 ships share one shipyard with two docking ports and 400 t of storage, so most of them end up waiting. Waiting ships are cheap, so a busier economy with many stations would cost more per ship. Treat these numbers as a lower bound for flying-heavy fleets.
