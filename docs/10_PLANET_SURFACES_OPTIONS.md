# 10 — Planet surfaces: options (#33)

**Status: decided 2026-11-23 (docs/06).** The creator chose **B, surface sites on the rim, built on A's flattened close-up view**. Goods move between structures on one body by craft only (shuttles), and bodies keep their true kilometre scale. The other options are kept below for the record. **Built in #70** (see the docs/06 row "Surface sites built"): sites, per-site ore, survey by horizon, site-aware mining jobs, site markers. The close-up view needed no extra flattening at true scale.

## Where things stand

- Bodies are solid discs at true scale: Home is 5–7 km in radius (about 38 km around), the Moon and big bodies are kilometres across, asteroids 400–900 m. A ship cell is 2 m.
- Landed craft are ordinary grid craft resting on the rim by their lowest cells. Clamps anchor them, foundations make fixed structures, and kits are deployed onto the surface.
- Mining digs visible craters where it happens. Bodies stay solid: no tunnels or hollow shells (docs/06, #28). Bulk mining shrinks a body until it's gone (#27).
- Deposits belong to the whole body, not to a place on it.
- docs/06 lists an *unapproved* default: "switch to a body-local tile construction view on landing".

## The options

### A. The rim as it is, made buildable (smallest change)

Keep one world and one view. When you're close to a surface, it's drawn flattened enough that square grids line up with the ground. Several landed structures can sit side by side along the rim, linked by short-hop shuttles or docking.

- **Good:** no second map or mode; automation, saves and rendering stay as they are; it scales to dismantling, since the rim simply shrinks.
- **Weak:** the buildable area is a line around the body (1D). Big ground bases become long strips. A 6 km planet has plenty of room, but there's no "into the ground".

### B. Surface sites (zones on the rim)

The rim is split into named sites, for example one per 500 m to 2 km of arc depending on the body's size. Each site has its own deposit mix (iron here, ice there), its own crater, and holds landed structures. Space view shows site markers; the map lists them. Deposits move from "the body" to "its sites".

- **Good:** exploring the surface matters (survey finds sites), which gives the place-based choices Factorio-style games need. It's still one world: a site is just an arc of the rim, so automation and jobs work unchanged ("mine at site Home-3").
- **Weak:** it changes the deposit data (save migration) and needs a site list in the UI. The surface is still a line within each site.

### C. A body-local surface view (the docs/06 default)

Landing opens a separate flat map of the body's surface: the rim unrolled as a strip that wraps around, plus some depth below it for pits. You build on it with the blueprint editor's grid. In space, the planet shows simplified markers for what's built.

- **Good:** real 2D bases with room to sprawl; the classic "colony on a planet" feel.
- **Weak:** the biggest change by far. It needs two simulations kept in step (surface and space), a second renderer and its input, and rules for what happens when the body shrinks or is dismantled (the strip has to shrink too). It also conflicts with the "no tunnels" rule unless depth is limited to open pits.

### D. Zoom straight into the disc (continuous)

No mode switch: zooming in on a planet keeps going down to the surface, which turns into tiles (rock, ice, ore) you build on and dig into. The disc's interior is a real tile grid.

- **Good:** seamless; you can dig anywhere; dismantling becomes visible tile by tile.
- **Weak:** a 6 km planet is about 28 million 2 m tiles, so it needs chunked storage and streaming. It contradicts "bodies stay solid, craters only" (#28) and reopens tunnels. It's the most expensive option to simulate and save.

## Recommendation

**B (surface sites), built on A's flattened close-up view.** It keeps the single-world model that automation, saves and the 1,000-ship target (#39) depend on. It gives the surface place-based meaning (where you land matters), works with craters and dismantling as they are, and can be built in stages:

1. A: flatten the close-up view and allow neighbouring structures.
2. B: sites with their own deposits, found by survey.

C or D could still come later as a visual layer if a 2D base view is wanted.

## Questions that come with any choice

- **Deposits by place:** should different places on one body hold different ores (B, C, D), or should a body stay one stockpile (A)?
- **Ground transport:** must structures on the same body move goods by shuttle (as in space), or can neighbouring ones share storage directly (conveyor-like links)?
- **Scale:** keep true-scale kilometre bodies, or shrink planets so a base covers a noticeable part of one?
