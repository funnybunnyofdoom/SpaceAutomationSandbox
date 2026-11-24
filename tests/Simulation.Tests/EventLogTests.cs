using System.Linq;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Events know what they're about (#100).</summary>
public sealed class EventLogTests
{
    [Fact]
    public void Events_keep_their_kind_and_subject_through_a_save()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.Crafts.First(c => c.IsShipyard);
        var body = sector.Bodies.First(b => b.IsMineable);
        sector.AddEvent("plain");
        sector.AddEvent("trouble", EventKind.Danger, TargetRef.Of(yard));
        sector.AddEvent("life", EventKind.Life, TargetRef.Of(body));
        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, TestData.Catalog()).Sector;
        Assert.Equal(sector.Events, loaded.Events); // records: same text, kind and subject
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        Assert.True(loaded.Events[0].Target.IsNone);
    }

    [Fact]
    public void Meeting_a_civilization_is_an_event_about_its_star_and_the_list_is_capped()
    {
        var sector = TestData.NewGame().Sector;
        var star = sector.Stars[0];
        sector.MeetCivilization(star, cultureId: "pacifist", baseline: 0);
        var contact = sector.Events.Single(e => e.Text.StartsWith("Contact"));
        Assert.Equal(EventKind.Civilization, contact.Kind);
        Assert.Equal(TargetRef.Of(star), contact.Target);
        for (var i = 0; i < SectorState.MaxEvents + 50; i++) sector.AddEvent($"e{i}");
        Assert.Equal(SectorState.MaxEvents, sector.Events.Count);
    }
}
