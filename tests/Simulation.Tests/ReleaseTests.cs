using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>What a release needs in the files (#136-#139).</summary>
public class ReleaseTests
{
    [Fact]
    public void The_credits_name_every_third_party_author_in_the_asset_manifest()
    {
        var credits = TestData.Read("data/credits.json");
        var authors = TestData.Read("assets/ASSET_MANIFEST.md").Split('\n')
            .Where(l => l.StartsWith("| `"))
            .Select(l => l.Split('|')[4].Trim())
            .Where(a => !a.StartsWith("original"))
            .Select(a => Regex.Replace(a.Split(',')[0], @"\s*\(.*\)", "").Trim())
            .Distinct().ToList();
        Assert.NotEmpty(authors);
        foreach (var author in authors) Assert.Contains(author, credits);
    }

    [Fact]
    public void The_version_is_set_once_and_the_exe_numbers_follow_it()
    {
        var project = TestData.Read("project.godot");
        var version = Regex.Match(project, "config/version=\"([^\"]+)\"").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+(-[a-z0-9]+)?$", version);
        var numbers = version.Split('-')[0] + ".0";
        var presets = TestData.Read("export_presets.cfg");
        Assert.Contains($"application/file_version=\"{numbers}\"", presets);
        Assert.Contains($"application/product_version=\"{numbers}\"", presets);
        Assert.Contains(version.Split('-')[0], TestData.Read("CHANGELOG.md"));
        Assert.Contains("All rights reserved", TestData.Read("LICENSE"));
    }
}
