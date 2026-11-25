using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Goods travelling between two places by jobs and swarms (#104): how many ships, and how many of them are waiting.</summary>
public sealed record MapRoute(TargetRef From, TargetRef To, int Ships, int Waiting);

public sealed partial class SectorState
{
    /// <summary>The routes your active mining, freight, trade jobs and swarms run, joined by their ends.</summary>
    public IReadOnlyList<MapRoute> MapRoutes()
    {
        var found = new Dictionary<(TargetRef From, TargetRef To), (int Ships, int Waiting)>();
        void Add(TargetRef from, TargetRef to, int ships, bool waiting)
        {
            if (from.IsNone || to.IsNone || !Exists(from) || !Exists(to)) return;
            var (n, w) = found.GetValueOrDefault((from, to));
            found[(from, to)] = (n + ships, w + (waiting ? ships : 0));
        }
        static TargetRef Craft(CraftId id) => new(id, BodyId.None);
        static TargetRef Body(BodyId id) => new(CraftId.None, id);
        foreach (var craft in _crafts)
        {
            if (!IsPlayers(craft) || craft.Job is not { Active: true } job) continue;
            var waiting = job.Status.StartsWith("waiting");
            switch (job)
            {
                case MiningJob m: Add(Body(m.SourceBodyId), Craft(m.DestinationId), 1, waiting); break;
                case HaulJob h: Add(Craft(h.PickupId), Craft(h.DestinationId), 1, waiting); break;
                case ServeRequestsJob { Contract: { } c }: Add(Craft(c.PickupId), Craft(c.DestinationId), 1, waiting); break;
                case ProgramJob p when ProgramById(p.ProgramId) is { } program: // between the places it calls at, in turn
                    var calls = program.AllSteps().Select(s => s.Kind == StepKind.Mine || s.Kind == StepKind.FlyTo && s.BodyId != 0 ? Body(new BodyId(s.BodyId)) : Craft(new CraftId(s.StationId))).Where(t => !t.IsNone).ToList();
                    for (var i = 0; i < calls.Count; i++)
                        if (calls[i] != calls[(i + 1) % calls.Count] && (i + 1 < calls.Count || calls.Count > 2)) Add(calls[i], calls[(i + 1) % calls.Count], 1, waiting);
                    break;
                case TradeJob t: Add(Craft(t.HomeId), Craft(t.TraderId), 1, waiting); break;
            }
        }
        foreach (var swarm in _swarms)
            Add(Body(swarm.SourceBodyId), Craft(swarm.DestinationId), swarm.Count, swarm.LimitReason is not null && swarm.DeliveryKgPerS <= 0);
        return found.Select(f => new MapRoute(f.Key.From, f.Key.To, f.Value.Ships, f.Value.Waiting))
            .OrderBy(r => r.From.Craft.Value).ThenBy(r => r.From.Body.Value).ThenBy(r => r.To.Craft.Value).ToList();
    }

    /// <summary>The star system a target is in.</summary>
    public BodyId? SystemOf(TargetRef target) =>
        !Exists(target) ? null : !target.Craft.IsNone ? StarAt(GetCraft(target.Craft).Position)?.Id : GetBody(target.Body).Kind == BodyKind.Star ? target.Body : StarOf(target.Body)?.Id;
}
