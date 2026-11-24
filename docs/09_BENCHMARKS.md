# 09 — Simulation benchmarks (#20)

These measure; they don't promise anything (docs/02, Performance). Rerun after big changes and add a dated row set.

```powershell
dotnet run -c Release --project tools/Benchmarks          # 10, 100, 1,000, 10,000 ships
dotnet run -c Release --project tools/Benchmarks -- 500   # any counts
```

**Scenario:** the default starter sector (seed 20261120). N automated starter ships each run a mining job, spread over Rock-1, Rock-2 and Rock-3, with the hold returning at 25% full, all delivering to the starter shipyard. Timing starts after 40 warm-up ticks, so most ships are mid-flight using the autopilot (obstacle checks, docking-port searches), which is the expensive part. Only the simulation is timed: no Godot, no rendering, no scene nodes. A thousand ships still means zero scene nodes until a view draws them (docs/02: "no node per NPC").

**Budget:** the simulation runs 20 ticks per second, so 50 ms per tick is everything. Rendering, UI and the rest of the frame need most of that.

## 2026-11-24 — huge craft (#114)

```powershell
dotnet run -c Release --project tools/Benchmarks -- --huge              # 100, 500, 2,000, 10,000 parts; or give sizes
SpaceAutomationSandbox.exe -- --new-game --huge=10000x5 --zoom=0.4 --fps-report=8   # in the game; --no-meshes draws cell by cell
```

Simulation, dev PC, Release, ms (mean), b1806a2 before → after, one run each, with `DOTNET_TieredCompilation=0`. Without it some small rows look slower after: the old code ran hot loops long enough to be fully compiled, the new code doesn't. (The two versions end in identical states.)

| What | 100 parts | 500 | 2,000 | 10,000 |
| --- | ---: | ---: | ---: | ---: |
| tick: one flying | 0.008 → 0.008 | 0.008 → 0.008 | 0.010 → 0.009 | 0.008 → 0.011 |
| tick: one landed, one coming down | 0.021 → 0.022 | 0.019 → 0.009 | 0.079 → 0.021 | 0.45 → 0.016 |
| tick: two side by side | 0.022 → 0.010 | 0.27 → 0.011 | 4.0 → 0.019 | **102 → 0.06** |
| tick: two in contact | 0.027 → 0.018 | 0.27 → 0.012 | 4.0 → 0.022 | **98 → 0.07** |
| tick: 50 small ships close to it | 0.26 → 0.17 | 0.85 → 0.12 | 3.1 → 0.08 | 15 → 0.06 |
| tick: 20 small ships docked or docking | 0.013 → 0.011 | 0.32 → 0.06 | 0.63 → 0.04 | 0.022 → 0.022 |
| tick: under fire, 20 hits a tick | 0.35 → 0.18 | 0.88 → 0.05 | 3.6 → 0.16 | 18 → 0.8 |
| tick: losing 5 parts a tick | 0.14 → 0.06 | 0.69 → 0.22 | 2.9 → 0.9 | 18 → 5.7 |
| tick: 30 shots crossing it | 0.62 → 0.20 | 2.7 → 0.14 | 11 → 0.12 | 52 → 0.14 |
| editor: one part placed, all shown | 0.31 → 0.10 | 1.4 → 0.5 | 5.6 → 1.8 | 31 → 10 |
| editor: 200 undo steps kept, MB | 1 → 0 | 4.6 → 0 | 21 → 0 | 89 → 0 |

In the game (Release export, 8 s, vsync off): five craft of 10,000 parts on screen, 0.99 ms a frame (1,005 fps) as meshes, 10.7 ms (94 fps) cell by cell. 1,000 miners: 3.66 ms mean, 10.6 ms worst 1%, tick 4.9 ms; with a 10,000-part craft added: 3.76, 10.0, 5.0. The target (#39) is still met. 10,000 small ships (simulation only): 70 ms a tick before, 68 after; over the 50 ms budget either way (47 ms on 2026-11-22: features since then, not #114).

A 10,000-part station (283 m in radius, 11 ports) took deliveries from six automated miners over an hour of game time with no parts lost. Saving a game with it: 11 ms and 1.2 MB; loading: 88 ms (one run).

**Recommended largest size: 10,000 parts** (a grid of about 101 x 101), with everyday big ships of up to 2,000. What grows with size and isn't fixed: losing parts (about 1.1 ms each at 10,000), since the whole craft is worked out anew each time.

## 2026-11-24 — a swarm of 5,000 (#30)

Debug build from the project folder (not a Release export), 10 s, one run: `-- --new-game --demo-swarm=5000 --fps-report=10 --zoom=0.08`. Mean frame 1.15 ms, worst 1% 2.24 ms, sim tick 0.07 ms mean. A swarm's cost doesn't depend on its ship count; at most 150 of its ships are drawn.

## 2026-11-24 — in the game, with body art (#71–#74)

Release export of 065e61a, dev PC, commands as in the next section with `--fps-report=20`, one run per view. Target: 16.7 ms per frame.

| Ships | View | Mean frame | Worst 1% frame | Sim tick (mean) | Draw per frame |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1,000 | normal | 3.10 ms (323 fps) | 8.66 ms (115 fps) | 5.19 ms | 2.20 ms |
| 1,000 | 0.3 | 0.73 ms (1363 fps) | 6.08 ms (164 fps) | 5.15 ms | 0.18 ms |
| 1,000 | 0.05 (all on screen) | 1.26 ms (792 fps) | 8.48 ms (118 fps) | 5.15 ms | 0.23 ms |
| 1,000 | `--view-body=Home --view-bearing=-90 --zoom=1` | 1.16 ms (861 fps) | 6.65 ms (150 fps) | 5.15 ms | 0.22 ms |
| 1,000 | `--view-body=Moon --survey-all --dig --zoom=0.5` | 0.65 ms (1530 fps) | 6.18 ms (162 fps) | 5.40 ms | 0.09 ms |

**Reading it:** the target is met in every view; the worst frames are still mostly the simulation tick. At 0.05 the worst 1% is 1.7 ms above the 2026-11-23 run (its mean is lower); with single runs and possibly a different window size, that may be noise. The `--view-body` views have few ships on screen: they mainly test the body shader.

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
