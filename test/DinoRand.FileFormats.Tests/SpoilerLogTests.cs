using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the per-seed spoiler log (docs/SPOILER-LOG-PLAN.md): the typed collector,
/// the pure markdown builder (debug block first, dynamic section omission, determinism), the
/// deterministic config dump, and pass-level recording on a synthetic world (no game files).
/// </summary>
public class SpoilerLogTests
{
    // --- Collector ---------------------------------------------------------------------------

    [Fact]
    public void Collector_SectionIsGetOrCreate_AndInsertionOrdered()
    {
        var c = new SpoilerCollector();
        var a = c.Section("Enemies", "Room", "Vanilla", "New");
        var b = c.Section("Items", "Room", "Vanilla item", "New item");
        var again = c.Section("Enemies", "Room", "Vanilla", "New");

        Assert.Same(a, again);
        Assert.Equal(new[] { "Enemies", "Items" }, c.Sections.Select(s => s.Title));
        Assert.Equal(new[] { "Room", "Vanilla item", "New item" }, b.Columns);
    }

    [Fact]
    public void Collector_RowsAndNotes_AccumulateInOrder()
    {
        var c = new SpoilerCollector();
        var s = c.Section("Enemies", "Room", "Vanilla", "New");
        s.AddNote("mode: weighted");
        s.AddRow("ST102", "Velociraptor", "Allosaurus");
        s.AddRow("ST105", "Velociraptor", "Oviraptor");

        Assert.Equal(new[] { "mode: weighted" }, s.Notes);
        Assert.Equal(2, s.Rows.Count);
        Assert.Equal(new[] { "ST105", "Velociraptor", "Oviraptor" }, s.Rows[1]);
    }

    // --- Builder -----------------------------------------------------------------------------

    private static SpoilerDebugInfo Debug(string seedString = "DINO-test", int seed = 1234) => new(
        SeedString: seedString,
        SeedValue: seed,
        GameId: "dc2",
        AppVersion: "0.2.0",
        GeneratedUtc: "2026-07-03T00:00:00Z",
        ConfigDump: new[] { "RandomizeEnemies = True", "RandomizeItems = True" },
        PassLog: new[] { "[dc2-enemy] donor tally: Allosaurus×1" },
        OutputFiles: new[] { "ST102.DAT" });

    [Fact]
    public void Builder_DebugBlockComesFirst_AndCarriesTheRunIdentity()
    {
        var c = new SpoilerCollector();
        c.Section("Enemies", "Room", "Vanilla", "New").AddRow("ST102", "Velociraptor", "Allosaurus");
        var md = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), c.Sections));

        int marker = md.IndexOf(SpoilerLogBuilder.SpoilerMarker, StringComparison.Ordinal);
        Assert.True(marker > 0, "spoiler marker missing");

        var debugBlock = md[..marker];
        Assert.Contains("DINO-test", debugBlock);
        Assert.Contains("1234", debugBlock);
        Assert.Contains("dc2", debugBlock);
        Assert.Contains("0.2.0", debugBlock);
        Assert.Contains("2026-07-03T00:00:00Z", debugBlock);
        Assert.Contains("RandomizeEnemies = True", debugBlock);
        Assert.Contains("[dc2-enemy] donor tally: Allosaurus×1", debugBlock);
        Assert.Contains("ST102.DAT", debugBlock);

        // Room-level spoilers appear ONLY below the marker.
        Assert.DoesNotContain("Allosaurus×1×", debugBlock); // sanity: no accidental table leak
        Assert.Contains("| ST102 | Velociraptor | Allosaurus |", md[marker..]);
    }

    [Fact]
    public void Builder_RendersEachSection_WithNotesAndGfmTable()
    {
        var c = new SpoilerCollector();
        var s = c.Section("Enemies (DC2 cross-species)", "Room", "Vanilla species", "New species");
        s.AddNote("mode: fixed: Tyrannosaurus");
        s.AddRow("ST102", "Velociraptor", "Tyrannosaurus");
        var md = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), c.Sections));

        Assert.Contains("## Enemies (DC2 cross-species)", md);
        Assert.Contains("mode: fixed: Tyrannosaurus", md);
        Assert.Contains("| Room | Vanilla species | New species |", md);
        Assert.Contains("| --- | --- | --- |", md);
        Assert.Contains("| ST102 | Velociraptor | Tyrannosaurus |", md);
    }

    [Fact]
    public void Builder_OmitsSectionsThatWereNeverRecorded()
    {
        // Only the sections a pass actually created appear — a disabled pass records nothing,
        // so its table is absent (dynamic tables, plan §4).
        var c = new SpoilerCollector();
        c.Section("Items (DC1)", "Room", "Vanilla item", "New item").AddRow("0x10B", "9mm", "Med. Pak S");
        var md = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), c.Sections));

        Assert.Contains("## Items (DC1)", md);
        Assert.DoesNotContain("Enemies", md[md.IndexOf(SpoilerLogBuilder.SpoilerMarker, StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Builder_WithNoSections_SaysNoChanges()
    {
        var md = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), Array.Empty<SpoilerSection>()));
        Assert.Contains(SpoilerLogBuilder.SpoilerMarker, md);
        Assert.Contains("No changes recorded", md);
    }

    [Fact]
    public void Builder_IsDeterministic_SameDocumentSameMarkdown()
    {
        var c1 = new SpoilerCollector();
        c1.Section("Enemies", "Room", "Vanilla", "New").AddRow("ST102", "Velociraptor", "Allosaurus");
        var c2 = new SpoilerCollector();
        c2.Section("Enemies", "Room", "Vanilla", "New").AddRow("ST102", "Velociraptor", "Allosaurus");

        var md1 = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), c1.Sections));
        var md1Again = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), c1.Sections));
        var md2 = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), c2.Sections));

        Assert.Equal(md1, md1Again);
        Assert.Equal(md1, md2);
    }

    [Fact]
    public void Builder_EscapesPipesInCells()
    {
        var c = new SpoilerCollector();
        c.Section("Items", "Room", "Vanilla", "New").AddRow("0x10B", "A|B", "C");
        var md = SpoilerLogBuilder.Build(new SpoilerDocument(Debug(), c.Sections));
        Assert.Contains(@"A\|B", md);
    }

    // --- Config dump -------------------------------------------------------------------------

    [Fact]
    public void DumpConfig_IsDeterministic_SortedAndComplete()
    {
        var cfg = new RandomizerConfig
        {
            RandomizeItems = false,
            Dc2SpeciesWeights = new Dictionary<int, byte> { [0x08] = 3, [0x02] = 1 },
        };
        var dump1 = SpoilerLogBuilder.DumpConfig(cfg);
        var dump2 = SpoilerLogBuilder.DumpConfig(cfg);

        Assert.Equal(dump1, dump2);
        Assert.Contains("RandomizeItems = False", dump1);
        Assert.Contains("RandomizeEnemies = True", dump1);
        // Dictionary values render deterministically (sorted by key).
        Assert.Contains(dump1, l => l.StartsWith("Dc2SpeciesWeights = ") && l.Contains("0x02=1") && l.Contains("0x08=3")
                                    && l.IndexOf("0x02=1", StringComparison.Ordinal) < l.IndexOf("0x08=3", StringComparison.Ordinal));
        // Every public config property appears exactly once.
        var names = typeof(RandomizerConfig).GetProperties().Select(p => p.Name);
        foreach (var n in names)
            Assert.Single(dump1, l => l.StartsWith(n + " = ", StringComparison.Ordinal));
    }

    // --- Context wiring ----------------------------------------------------------------------

    [Fact]
    public void RandomizationContexts_AlwaysCarryACollector()
    {
        var game = new DinoCrisis1();
        var rooms = new[] { new RoomFile(1, 0x0d) };
        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(1),
                                           new RandomizerConfig(), _ => { });
        Assert.NotNull(ctx.Spoiler);
    }

    // --- Pass recording (synthetic world, no game files) ---------------------------------------

    [Fact]
    public void ItemRandomizer_RecordsChangedPickups_WithNamesAndModeNote()
    {
        var game = new DinoCrisis1();
        int start = game.StartRoomCode;
        var room = new RoomFile((start >> 8) & 0xff, start & 0xff);
        for (int i = 0; i < 40; i++)
            room.Items.Add(new ItemRecord { ItemId = 0x1C, OriginalItemId = 0x1C, Amount = 1, FileOffset = i });
        var rooms = new[] { room };
        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(7),
                                           new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        var section = Assert.Single(ctx.Spoiler.Sections, s => s.Title.StartsWith("Items"));
        Assert.Contains(section.Notes, n => n.Contains("rerolled"));

        // Rows exist exactly for the pickups whose item actually changed, and the vanilla cell
        // names the original item (Med. Pak S), not a hex id.
        var changed = room.Items.Where(r => r.ItemId != r.OriginalItemId).ToList();
        Assert.NotEmpty(changed);
        Assert.Equal(changed.Count, section.Rows.Count);
        Assert.All(section.Rows, r => Assert.Contains("Med. Pak S", r[1]));
        Assert.All(section.Rows, r => Assert.NotEqual(r[1], r[2]));
    }

    [Fact]
    public void ItemRandomizer_UnchangedReroll_RecordsNoRow()
    {
        // A slot that rerolls onto its own vanilla item is not a change — no row (plan §4).
        var game = new DinoCrisis1();
        int start = game.StartRoomCode;
        var room = new RoomFile((start >> 8) & 0xff, start & 0xff);
        for (int i = 0; i < 40; i++)
            room.Items.Add(new ItemRecord { ItemId = 0x16, OriginalItemId = 0x16, Amount = 1, FileOffset = i });
        var rooms = new[] { room };
        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(7),
                                           new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        var section = Assert.Single(ctx.Spoiler.Sections, s => s.Title.StartsWith("Items"));
        var changed = room.Items.Count(r => r.ItemId != r.OriginalItemId);
        Assert.Equal(changed, section.Rows.Count);
        Assert.All(section.Rows, r => Assert.NotEqual(r[1], r[2]));
    }

    // --- Name lookups -------------------------------------------------------------------------

    [Fact]
    public void Dc1ItemNames_ResolveKnownIds_AndFallBackToHex()
    {
        Assert.Equal("Med. Pak S", Dc1ItemNames.NameOf(0x1C));
        Assert.Equal("9mm Parabellum", Dc1ItemNames.NameOf(0x16));
        Assert.Contains("0xFF", Dc1ItemNames.NameOf(0xFF));
    }

    [Fact]
    public void Dc1RoomNames_ResolveFromEmbeddedMap()
    {
        Assert.Equal("Locker Room", Dc1RoomNames.NameOf(0x100));
        Assert.Null(Dc1RoomNames.NameOf(0xFFF));
    }
}
