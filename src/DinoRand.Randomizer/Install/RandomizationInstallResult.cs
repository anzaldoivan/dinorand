namespace DinoRand.Randomizer.Install;

/// <summary>The overlay result and ordered messages produced by a coordinated install.</summary>
internal sealed record RandomizationInstallResult(
    InstallResult InstallResult,
    IReadOnlyList<RandomizationInstallEvent> Events);
