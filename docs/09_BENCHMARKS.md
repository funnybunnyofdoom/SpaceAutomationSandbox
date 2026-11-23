# 09 — Simulation benchmarks (#20)

These measure; they don't promise anything (docs/02, Performance). Rerun after big changes and add a dated row set.

```powershell
dotnet run -c Release --project tools/Benchmarks          # 10, 100, 1,000, 10,000 ships
dotnet run -c Release --project tools/Benchmarks -- 500   # any counts
```

**Scenario:** the default starter sector (seed 20261120). N automated starter ships each run a mining job, spread over Rock-1, Rock-2 and Rock-3, with the hold returning at 25% full, all delivering to the starter shipyard. Timing starts after 40 warm-up ticks, so most ships are mid-flight using the autopilot (obstacle checks, docking-port searches), which is the expensive part. Only the simulation is timed: no Godot, no rendering, no scene nodes. A thousand ships still means zero scene nodes until a view draws them (docs/02: "no node per NPC").

**Budget:** the simulation runs 20 ticks per second, so 50 ms per tick is everything. Rendering, UI and the rest of the frame need most of that.

## 2026-11-23 — in the game, rendering included (#39)

**Target (creator):** 1,000 active automated ships at 60 fps (16.7 ms per frame) on the dev PC.

```powershell
# Export first (Release C#), then from the export folder:
SpaceAutomationSandbox.exe -- --new-game --stress=1000 --fps-report=30            # normal zoom
SpaceAutomationSandbox.exe -- --new-game --stress=1000 --fps-report=30 --zoom=0.05 # whole neighbourhood on screen
```

`--stress=N` adds N automated miners around the starter yard (the benchmark's scenario). `--fps-report=S` turns vsync off, waits 5 s, measures real time between frames for S seconds (not Godot's smoothed delta), prints mean and worst-1% frame times plus the tick, draw, HUD and audio costs, and quits. Use the exported Release build: the editor's Debug build is about 9× slower in the simulation.

| Ships | Zoom | Mean frame | Worst 1% frame | Sim tick (mean) | Draw per frame |
| ---: | --- | ---: | ---: | ---: | ---: |
| 0 | normal | 1.31 ms (765 fps) | 2.09 ms (479 fps) | 0.02 ms | 1.09 ms |
| 1,000 | normal | 3.37 ms (297 fps) | 9.01 ms (111 fps) | 5.18 ms | 2.94 ms |
| 1,000 | 0.3 | 2.19 ms (458 fps) | 7.46 ms (134 fps) | 4.98 ms | 1.70 ms |
| 1,000 | 0.05 (all on screen) | 1.57 ms (637 fps) | 6.79 ms (147 fps) | 4.94 ms | 1.13 ms |
| 2,000 | normal | 6.73 ms (149 fps) | 20.53 ms (49 fps) | 12.81 ms | 5.56 ms |

**Before the fixes** (same build, 1,000 ships, normal zoom): 21.7 ms mean (46 fps). Drawing every craft, including those off screen, and a name tag for each cost about 17 ms.

**Reading it:**
- **1,000 ships meet the target with room to spare.** The worst frames are the ones where a simulation tick runs (20 per second); the rest are a few ms.
- **2,000 ships miss 60 fps in the worst frames**, mostly because of the simulation: 13 ms per tick, with traffic around one crowded yard. That's where swarm aggregation (#30) or cheaper crowds would have to come in.
- The simulation can't run faster than one tick per frame without more frames: at high time warp, frames take longer, as before (the frame budget in TimeWarp caps it).

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
