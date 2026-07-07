namespace DinoRand.Randomizer.Spoiler;

/// <summary>
/// The typed per-run diff ledger (docs/decisions/cross/SPOILER-LOG-PLAN.md §4). Passes record what they changed
/// — at the moment they decide it — as structured section rows/notes; the markdown builder is a
/// pure projection of this, so the spoiler can never drift from what actually happened (it is
/// never re-derived from logs or output files). Lives on both run contexts
/// (<see cref="RandomizationContext.Spoiler"/> / <see cref="Dc2.Dc2RandomizationContext.Spoiler"/>).
/// Recording is pure list-appends: no RNG, no I/O — zero behavioral impact by construction.
/// </summary>
public sealed class SpoilerCollector
{
    private readonly List<SpoilerSection> _sections = new();

    /// <summary>Get-or-create the section titled <paramref name="title"/> (insertion-ordered —
    /// passes run in a fixed order, so the document order is deterministic). Columns are set by
    /// the first caller; a pass that ran but changed nothing may still add only notes.</summary>
    public SpoilerSection Section(string title, params string[] columns)
    {
        var existing = _sections.FirstOrDefault(s => s.Title == title);
        if (existing is not null) return existing;
        var section = new SpoilerSection(title, columns);
        _sections.Add(section);
        return section;
    }

    /// <summary>Sections in the order first recorded. Empty when no enabled pass recorded anything.</summary>
    public IReadOnlyList<SpoilerSection> Sections => _sections;
}

/// <summary>One spoiler table: a title, column headers, data rows, and free-text notes (mode
/// lines, skip summaries). Columns are per-section so each pass shapes its own table.</summary>
public sealed class SpoilerSection
{
    private readonly List<string[]> _rows = new();
    private readonly List<string> _notes = new();

    internal SpoilerSection(string title, string[] columns)
    {
        Title = title;
        Columns = columns;
    }

    public string Title { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyList<string>> Rows => _rows;
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Add one table row (cells positionally match <see cref="Columns"/>).</summary>
    public void AddRow(params string[] cells) => _rows.Add(cells);

    /// <summary>Add a free-text note rendered as a bullet above the table (a mode line, a
    /// skipped/protected-rooms summary — never silence, plan §4).</summary>
    public void AddNote(string note) => _notes.Add(note);
}
