using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// <b>PLACEHOLDER — DEFERRED (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §7).</b> Swapping the on-screen cutscene
/// character model (so a remapped voice matches a new on-screen actor) requires DC1's native
/// cutscene-actor load path, which is still unsolved — live characters (Gail/Kirk/Rick/Regina/Cooper)
/// are NOT <c>0x20</c> entities and load via native engine code outside the SCD stream
/// (docs/_prompts/dc1/NATIVE-CUTSCENE-LOADER-PROMPT.md, docs/decisions/dc1/enemies/CROSS-ROOM-SPECIES-PLAN.md).
///
/// <para>This exists only to mark the seam. BioRand keeps model and voice as fully separate subsystems
/// sharing only the actor-name identity, so a real DC1 model swap can land here later without touching
/// the voice path. It is never called by the gated <see cref="VoiceRandomizer"/> no-op.</para>
/// </summary>
public static class CharacterModelSwap
{
    /// <summary>
    /// Would re-skin the on-screen cutscene actor from <paramref name="from"/> to <paramref name="to"/>.
    /// Not implemented — the native cutscene-actor loader is undecoded.
    /// </summary>
    public static void Swap(VoiceActor from, VoiceActor to) =>
        // TODO: deferred — model swap (needs the native cutscene-actor load path; see plan §7).
        throw new NotImplementedException(
            "Cutscene character model swap is deferred: DC1's native cutscene-actor load path is not yet " +
            "reverse-engineered (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §7). Voice randomization does not require it.");
}
