using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>When a world's animals damage machines anchored among them (#93).</summary>
public enum AnimalTemper
{
    Harmless,
    /// <summary>When there are nearly as many as the plants can feed.</summary>
    Crowding,
    /// <summary>When there's too little for them to eat.</summary>
    Hungry,
}

/// <summary>How a living world is doing (#94).</summary>
public enum LifeHealth { Lost, Stressed, Stable, Thriving }

/// <summary>
/// Native animals (#93) and ecosystems (#94; the creator's choices). A living world may have one species. Each site has a density 0..1: it
/// grows towards what the site's plants can feed, grazes them, drifts to sites with more to eat, and can die out. About half the species damage
/// anchored machines, when crowded or when hungry. Managed with <b>repellers</b> (push them off a site), <b>ranches</b> (a sustainable share
/// as food), <b>culling</b> (an armed craft anchored there; for biomass) and <b>pens</b> (carry some to another site or world). Thriving worlds
/// regrow faster and stressed ones slower, and civilizations that care for life judge you by the worlds you've found.
/// Animals are part of their body's own mass, so the mass ledger stays exact.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Chance that a living world has animals, and that they're harmless.</summary>
    public const double AnimalChance = 0.7, HarmlessChance = 0.5;

    /// <summary>Animals on a site as full as its plants can feed, kg.</summary>
    public const double FullFaunaKg = 50_000;

    public const double FaunaGrowthPerMinute = 0.02, FaunaStarvePerMinute = 0.1, GrazingPerMinute = 0.004, FaunaDriftPerMinute = 0.03, RepelPerMinute = 0.6;

    /// <summary>Below this a site's animals are gone.</summary>
    public const double FaunaGone = 0.003;

    /// <summary>Ranches leave at least this share of what the plants can feed.</summary>
    public const double RanchLeaves = 0.5;

    /// <summary>Density one weapon culls a minute, how much of the kill is usable, and the grievance for a whole site's animals.</summary>
    public const double CullPerMinute = 0.1, CullYield = 0.5, CullGrievance = 10;

    /// <summary>Average plant cover at which a world thrives, and below which it's stressed; and how that changes growth.</summary>
    public const double ThrivingCover = 0.6, StressedCover = 0.25, ThrivingGrowth = 1.5, StressedGrowth = 0.5;

    /// <summary>What a world you've found adds to the opinion of civilizations that care for life: thriving, stressed, lost; and the most it can.</summary>
    public const double ThrivingOpinion = 3, StressedOpinion = -3, LostOpinion = -10, MaxLifeOpinion = 20;

    private static readonly string[] AnimalStarts = { "Bru", "Cal", "Dor", "Fen", "Gal", "Hob", "Kri", "Lom", "Mur", "Nar", "Pell", "Ruk", "Sil", "Tam", "Vor" };
    private static readonly string[] AnimalEnds = { "ba", "den", "ek", "ilo", "oon", "rax", "sk", "uta", "wick", "yx" };

    public static LifeHealth HealthOf(CelestialBodyState body)
    {
        if (!body.HasLife || body.Sites.Count == 0) return LifeHealth.Lost;
        var cover = body.Sites.Average(s => s.Flora);
        return cover >= ThrivingCover ? LifeHealth.Thriving : cover < StressedCover ? LifeHealth.Stressed : LifeHealth.Stable;
    }

    private static void AddAnimals(CelestialBodyState body, DeterministicRandom rng)
    {
        body.AnimalName = AnimalStarts[rng.NextUInt64() % (ulong)AnimalStarts.Length] + AnimalEnds[rng.NextUInt64() % (ulong)AnimalEnds.Length];
        body.AnimalTemper = rng.NextDouble() < HarmlessChance ? AnimalTemper.Harmless : rng.NextDouble() < 0.5 ? AnimalTemper.Crowding : AnimalTemper.Hungry;
        foreach (var site in body.SiteList) site.Fauna = site.Flora * rng.Range(0.2, 0.7);
        body.HasAnimals = body.Sites.Any(s => s.Fauna >= FaunaGone);
    }

    /// <summary>DEBUG and tests: gives a living body animals of a temper.</summary>
    public void DebugAddAnimals(BodyId bodyId, AnimalTemper temper)
    {
        var body = GetBody(bodyId);
        AddAnimals(body, new DeterministicRandom(Seed ^ (0x46415541UL * bodyId.Value)));
        body.AnimalTemper = temper;
    }

    /// <summary>Every ten seconds, after the plants: animals feed, breed, starve and move, and may damage machines.</summary>
    private void StepAnimals(CelestialBodyState body)
    {
        if (!body.HasAnimals) return;
        const double minutes = FloraStepTicks * FixedStepClock.TickSeconds / 60;
        var sites = body.SiteList;
        var n = sites.Count;
        var repelled = new bool[n];
        foreach (var craft in _crafts)
        {
            if (craft.LandedOnBodyId != body.Id || !craft.IsAnchored || craft.IsWreck || craft.Stats.RepellerCount == 0 || !craft.RepellerOn) continue;
            if (SiteUnder(craft) is { } site) repelled[site.Index] = true;
        }
        for (var i = 0; i < n; i++)
        {
            var site = sites[i];
            if (site.Fauna <= 0) continue;
            var food = site.Flora;
            if (site.Fauna <= food) site.Fauna += site.Fauna * (1 - site.Fauna / Math.Max(food, 1e-9)) * FaunaGrowthPerMinute * minutes;
            else site.Fauna = Math.Max(food, site.Fauna - Math.Min(site.Fauna, FaunaStarvePerMinute * minutes * (site.Fauna - food + 0.05)));
            site.Flora = Math.Max(0, site.Flora - site.Fauna * GrazingPerMinute * minutes);
            if (site.Flora < FloraGone) site.Flora = 0;
        }
        // Drift to a neighbour with more to eat; off a repelled site in any case. What leaves one arrives at the other.
        var moves = new double[n];
        for (var i = 0; i < n; i++)
        {
            var here = sites[i].Fauna;
            if (here <= 0) continue;
            for (var side = -1; side <= 1; side += 2)
            {
                var j = (i + side + n) % n;
                if (j == i || repelled[j]) continue;
                var pull = Math.Max(0, (sites[j].Flora - sites[j].Fauna) - (sites[i].Flora - here));
                var rate = repelled[i] ? RepelPerMinute / 2 : FaunaDriftPerMinute * Math.Min(1, pull);
                var moved = here * rate * minutes;
                moves[i] -= moved;
                moves[j] += moved;
            }
        }
        var any = false;
        for (var i = 0; i < n; i++)
        {
            sites[i].Fauna = Math.Clamp(sites[i].Fauna + moves[i], 0, 1);
            if (sites[i].Fauna < FaunaGone) sites[i].Fauna = 0;
            any |= sites[i].Fauna > 0;
        }
        if (!any)
        {
            body.HasAnimals = false;
            AddEvent($"The {body.AnimalName}s of {body.Name} have died out.", EventKind.Life, TargetRef.Of(body));
            return;
        }
        if (body.AnimalTemper != AnimalTemper.Harmless) Trample(body);
    }

    /// <summary>Would animals damage machines at this site now?</summary>
    public static bool AnimalsAngry(CelestialBodyState body, SurfaceSite site) => site.Fauna > 0.02 && body.AnimalTemper switch
    {
        AnimalTemper.Crowding => site.Fauna > 0.8 * site.Flora,
        AnimalTemper.Hungry => site.Flora < 0.3,
        _ => false,
    };

    private void Trample(CelestialBodyState body)
    {
        if (Parts is null) return;
        foreach (var craft in _crafts)
        {
            if (!craft.IsAnchored || craft.LandedOnBodyId != body.Id || craft.IsWreck || SiteUnder(craft) is not { } site || !AnimalsAngry(body, site)) continue;
            WearPart(craft, (Tick / FloraStepTicks * 7 + craft.Id.Value) % craft.Design.Cells.Count, 0.04 * Math.Max(0.25, site.Fauna));
        }
    }

    /// <summary>Wears one part, never below a tenth of its health.</summary>
    private void WearPart(CraftState craft, long cellIndex, double amount)
    {
        var cell = craft.Design.Cells[(int)cellIndex];
        var health = craft.HealthList.GetValueOrDefault(cell.Pos, 1);
        var worn = Math.Max(Math.Min(health, 0.1), health - amount);
        if (worn >= health) return;
        craft.HealthList[cell.Pos] = worn;
        craft.HealthChanged(Parts!);
    }

    /// <summary>Takes animals off a site (kg, whole): off the density, and out of the body's mass.</summary>
    private static long TakeAnimals(CelestialBodyState body, SurfaceSite site, long kg)
    {
        kg = Math.Min(Math.Min(kg, (long)Math.Floor(site.Fauna * FullFaunaKg)), body.BulkKg);
        if (kg <= 0) return 0;
        site.Fauna = Math.Max(0, site.Fauna - kg / FullFaunaKg);
        if (site.Fauna < FaunaGone) site.Fauna = 0;
        body.TakeBulk(kg);
        return kg;
    }

    /// <summary>Each tick: ranches, repellers, culling and pens on anchored craft.</summary>
    private void StepAnimalWork()
    {
        foreach (var craft in _crafts)
        {
            craft.AnimalStatus = null;
            craft.RepellerOn = false;
            if (craft.PenKg > craft.Stats.PenCapacityKg) // a pen broke off: those animals are lost
            {
                var lost = craft.PenKg - (long)craft.Stats.PenCapacityKg;
                craft.PenKg -= lost;
                Eject(BiomassMaterial, lost);
            }
            var s = craft.Stats;
            if (craft.Culling && (s.WeaponList.Count == 0 || craft.IsWreck || !craft.IsAnchored)) craft.Culling = false;
            if (craft.IsWreck || !craft.IsAnchored || !craft.IsLanded || (s.RepellerCount == 0 && s.RanchRateKgPerS <= 0 && !craft.Culling) || !Operational(craft)) continue;
            var body = GetBody(craft.LandedOnBodyId);
            if (SiteUnder(craft) is not { } site) continue;
            if (s.RepellerCount > 0)
            {
                craft.RepellerOn = UseMachinePower(craft, s.RepelPowerKW * FixedStepClock.TickSeconds);
                if (!craft.RepellerOn) craft.AnimalStatus = "repeller: no power";
            }
            if (s.RanchRateKgPerS > 0) Ranch(craft, body, site);
            if (craft.Culling) Cull(craft, body, site);
        }
    }

    private void Ranch(CraftState craft, CelestialBodyState body, SurfaceSite site)
    {
        var spare = (long)Math.Floor((site.Fauna - RanchLeaves * site.Flora) * FullFaunaKg);
        if (!body.HasAnimals || site.Fauna <= 0) { craft.AnimalStatus = "ranch waiting: no animals here"; return; }
        if (craft.FreeCargoKg <= 0) { craft.AnimalStatus = "ranch waiting: no room for the food"; return; }
        if (spare <= 0) { craft.AnimalStatus = "ranch waiting: the herd needs to grow"; craft.RanchRemainderKg = 0; return; }
        if (!UseMachinePower(craft, craft.Stats.RanchPowerKW * FixedStepClock.TickSeconds)) { craft.AnimalStatus = "ranch: no power"; return; }
        var due = craft.RanchRemainderKg + craft.Stats.RanchRateKgPerS * FixedStepClock.TickSeconds;
        var taken = TakeAnimals(body, site, Math.Min(Math.Min((long)Math.Floor(due), spare), craft.FreeCargoKg));
        craft.RanchRemainderKg = Math.Min(1, due - taken) is var left && left < 1 ? left : 0;
        craft.Inventory.Add(FoodMaterial, taken);
        craft.AnimalStatus = $"ranching {body.AnimalName}s";
    }

    /// <summary>Switches culling on or off for an armed craft anchored among animals.</summary>
    public ActionResult SetCulling(CraftId craftId, bool on)
    {
        var craft = GetCraft(craftId);
        if (!on) { craft.Culling = false; return new(true, $"{craft.Name}: culling stopped."); }
        if (craft.IsWreck || craft.Stats.WeaponList.Count == 0) return new(false, $"{craft.Name} has no weapons.");
        if (!craft.IsAnchored || !craft.IsLanded) return new(false, $"{craft.Name} must be anchored on the site.");
        var body = GetBody(craft.LandedOnBodyId);
        if (SiteUnder(craft) is not { Fauna: > 0 }) return new(false, "There are no animals here.");
        if (LawActive(Laws.ProtectLife)) return new(false, "Not allowed: law: native life is protected (Protect native life).");
        craft.Culling = true;
        return new(true, $"{craft.Name}: culling the {body.AnimalName}s here.");
    }

    private void Cull(CraftState craft, CelestialBodyState body, SurfaceSite site)
    {
        if (site.Fauna <= 0 || LawActive(Laws.ProtectLife)) { craft.Culling = false; craft.CullRemainderKg = 0; return; }
        var due = craft.CullRemainderKg + craft.Stats.WeaponList.Count * CullPerMinute * FullFaunaKg / 60 * FixedStepClock.TickSeconds;
        var taken = TakeAnimals(body, site, (long)Math.Floor(due));
        craft.CullRemainderKg = due - Math.Floor(due);
        var usable = Math.Min((long)(taken * CullYield), craft.FreeCargoKg);
        craft.Inventory.Add(BiomassMaterial, usable);
        Eject(BiomassMaterial, taken - usable);
        if (body.Orbit is not null)
            foreach (var faction in _factions) // those who care for life hear of it
                if (faction.Culture.StanceOn(Laws.ProtectLife) > 0) faction.Grievance = Math.Min(100, faction.Grievance + taken / FullFaunaKg * CullGrievance);
        craft.AnimalStatus = $"culling {body.AnimalName}s ({site.Fauna * 100:0}% left)";
    }

    // --- Pens: carrying animals elsewhere ---

    /// <summary>Fills an anchored craft's pens with the site's animals.</summary>
    public ActionResult LoadAnimals(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (craft.Stats.PenCapacityKg <= 0) return new(false, $"{craft.Name} has no animal pen.");
        if (!craft.IsAnchored || !craft.IsLanded) return new(false, $"{craft.Name} must be anchored on the site.");
        var body = GetBody(craft.LandedOnBodyId);
        if (SiteUnder(craft) is not { Fauna: > 0 } site) return new(false, "There are no animals here.");
        if (craft.PenKg > 0 && craft.PenAnimal != body.AnimalName) return new(false, $"The pens hold {craft.PenAnimal}s.");
        var taken = TakeAnimals(body, site, (long)craft.Stats.PenCapacityKg - craft.PenKg);
        if (taken <= 0) return new(false, "The pens are full.");
        craft.PenKg += taken;
        craft.PenAnimal = body.AnimalName;
        craft.PenTemper = body.AnimalTemper;
        return new(true, $"{craft.Name}: {taken / 1000.0:0.0##} t of {body.AnimalName}s in the pens.");
    }

    /// <summary>Lets the animals out where the craft is anchored: they join their own kind, or settle a living world without animals.</summary>
    public ActionResult ReleaseAnimals(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (craft.PenKg <= 0) return new(false, "The pens are empty.");
        if (!craft.IsAnchored || !craft.IsLanded) return new(false, $"{craft.Name} must be anchored on the site.");
        var body = GetBody(craft.LandedOnBodyId);
        if (SiteUnder(craft) is not { Flora: > 0 } site) return new(false, "Nothing grows here for them to eat.");
        if (body.HasAnimals && body.AnimalName != craft.PenAnimal) return new(false, $"{body.Name} has {body.AnimalName}s: two kinds of animal won't share a world.");
        var fits = (long)Math.Floor(Math.Max(0, 1 - site.Fauna) * FullFaunaKg);
        var kg = Math.Min(craft.PenKg, fits);
        if (kg <= 0) return new(false, "There's no room for more here.");
        craft.PenKg -= kg;
        body.ReturnBulk(kg);
        site.Fauna = Math.Min(1, site.Fauna + kg / FullFaunaKg);
        var settled = !body.HasAnimals;
        body.AnimalName = craft.PenAnimal;
        body.AnimalTemper = craft.PenTemper;
        body.HasAnimals = true;
        if (settled) AddEvent($"{craft.PenAnimal}s now live on {body.Name}.", EventKind.Life, TargetRef.Of(body));
        return new(true, $"{craft.Name}: {kg / 1000.0:0.0##} t of {craft.PenAnimal}s released on {body.Name}.");
    }

    // --- Opinion (#94) ---

    /// <summary>What the worlds you've found add to the opinion of civilizations that care for life.</summary>
    private void UpdateLifeOpinion()
    {
        double opinion = 0;
        foreach (var body in _bodies)
        {
            if (!body.LifeFound) continue;
            opinion += body.HasLife ? HealthOf(body) switch { LifeHealth.Thriving => ThrivingOpinion, LifeHealth.Stressed => StressedOpinion, _ => 0 } : LostOpinion;
        }
        opinion = Math.Clamp(opinion, -MaxLifeOpinion, MaxLifeOpinion);
        foreach (var faction in _factions) faction.LifeOpinion = faction.Culture.StanceOn(Laws.ProtectLife) > 0 ? opinion : 0;
    }
}
