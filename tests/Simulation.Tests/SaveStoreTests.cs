using System;
using System.IO;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#60 save slots: headers, listing, autosave rotation, rename, broken files, migration.</summary>
public sealed class SaveStoreTests : IDisposable
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sas_saves_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static string Json(string name, DateTime when, long tick = 0)
    {
        var sector = TestData.NewGame().Sector;
        return SaveGameSerializer.ToJson(sector, sector.PilotCraftId, new SaveHeader(name, when.ToString("O"), 3_600, tick, sector.Seed));
    }

    [Fact]
    public void TheHeaderIsReadWithoutTheSector_AndTheSaveStillLoads()
    {
        var path = Path.Combine(_folder, "save_a.json");
        SaveStore.Write(path, Json("My base", new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc), 1234));

        var slot = SaveStore.Read(path);
        Assert.False(slot.Broken);
        Assert.Equal("My base", slot.Name);
        Assert.Equal(1234, slot.Header!.Tick);
        Assert.Equal(3_600, slot.Header.PlayTimeS);
        Assert.Equal(SaveGameSerializer.SchemaVersion, slot.SchemaVersion);
        Assert.Equal(SaveKind.Manual, slot.Kind);

        var loaded = SaveGameSerializer.FromJson(File.ReadAllText(path), Catalog);
        Assert.Equal("My base", loaded.Header!.Name);
    }

    [Fact]
    public void TheListIsNewestFirst_AndBrokenFilesAreShownWithTheReason()
    {
        SaveStore.Write(Path.Combine(_folder, "save_old.json"), Json("Old", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        SaveStore.Write(Path.Combine(_folder, "save_new.json"), Json("New", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        File.WriteAllText(Path.Combine(_folder, "save_bad.json"), "{ this is not json");

        var list = SaveStore.List(_folder);
        Assert.Equal(3, list.Count);
        var good = list.Where(s => !s.Broken).Select(s => s.Name).ToList();
        Assert.Equal(new[] { "New", "Old" }, good);
        var bad = Assert.Single(list, s => s.Broken);
        Assert.Contains("unreadable", bad.Error);
        Assert.True(File.Exists(bad.Path)); // not moved or deleted
        Assert.Equal("New", SaveStore.Newest(_folder)!.Name);
    }

    [Fact]
    public void Autosaves_FillThreeSlots_ThenReplaceTheOldest()
    {
        var written = new string[5];
        for (var i = 0; i < 5; i++)
        {
            written[i] = SaveStore.NextAutosavePath(_folder);
            SaveStore.Write(written[i], Json($"Auto {i}", DateTime.UtcNow));
            File.SetLastWriteTimeUtc(written[i], new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc));
        }
        Assert.Equal(3, Directory.GetFiles(_folder, "autosave_*.json").Length);
        Assert.Equal(written[0], written[3]); // the oldest slot came round again
        Assert.Equal(written[1], written[4]);
        Assert.All(SaveStore.List(_folder), s => Assert.Equal(SaveKind.Auto, s.Kind));
    }

    [Fact]
    public void Rename_ChangesTheName_AndKeepsTheGame()
    {
        var path = Path.Combine(_folder, "save_r.json");
        SaveStore.Write(path, Json("Before", DateTime.UtcNow, 77));
        SaveStore.Rename(path, "After");
        Assert.Equal("After", SaveStore.Read(path).Name);
        Assert.Equal(77, SaveGameSerializer.FromJson(File.ReadAllText(path), Catalog).Header!.Tick);
    }

    [Fact]
    public void AnOldSaveWithoutAHeader_IsListed_AndCanBeRenamed()
    {
        var sector = TestData.NewGame().Sector;
        var path = Path.Combine(_folder, "save_old.json");
        SaveStore.Write(path, SaveGameSerializer.ToJson(sector, sector.PilotCraftId)); // no header
        Assert.False(SaveStore.Read(path).Broken);
        SaveStore.Rename(path, "Named later");
        Assert.Equal("Named later", SaveStore.Read(path).Name);
        SaveGameSerializer.FromJson(File.ReadAllText(path), Catalog); // still loads
    }

    [Fact]
    public void TheSaveFromBeforeSlots_MovesIntoTheFolderOnce()
    {
        Directory.CreateDirectory(_folder);
        var legacy = Path.Combine(_folder, "..", "legacy_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(legacy, Json("Legacy", DateTime.UtcNow));
        var moved = SaveStore.MigrateLegacy(legacy, _folder);
        Assert.NotNull(moved);
        Assert.False(File.Exists(legacy));
        Assert.Single(SaveStore.List(_folder));
        Assert.Null(SaveStore.MigrateLegacy(legacy, _folder)); // nothing left to move
    }

    [Fact]
    public void NewManualSaves_NeverOverwriteEachOther()
    {
        var now = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        var first = SaveStore.NewManualPath(_folder, now);
        SaveStore.Write(first, Json("One", now));
        var second = SaveStore.NewManualPath(_folder, now);
        Assert.NotEqual(first, second);
    }
}
