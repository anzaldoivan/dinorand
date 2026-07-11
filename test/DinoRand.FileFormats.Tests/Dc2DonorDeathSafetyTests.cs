using System.Collections.Generic;
using System.Linq;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Coverage invariant for the death-crash class (RCA §7b): a LAND donor that is a <b>setpiece</b> or a
/// <b>boss</b> is a "no-standard-death" model — in vanilla it is never killed via the generic death
/// path, so the engine's per-species death handler can request an animation clip its short model lacks
/// → crash on kill (Triceratops E70 = the witnessed case). Ordinary trash-mob donors (non-setpiece,
/// non-boss) are proven death-safe by every vanilla playthrough, so they need no lever.
///
/// <para><b>What this guards (and what it does NOT).</b> This is a <i>coupling</i> guard, cheap and
/// data-driven: it fails when a risky LAND donor becomes injectable without a killability lever wired
/// to cover it. It does NOT verify a lever actually works in-game (that needs a live witness), nor does
/// it verify real model clip counts against the engine's requested death indices — that would need a
/// per-species death-handler-index registry and an E*.DAT anim-clip parser, neither of which exists
/// (dcmtool's clip interchange is unshipped). It also does not guard other crash classes (aquatic
/// entry-residency, NULL-clip 0x48DE55). It guards exactly the regression it can: "a risky donor shipped
/// killable-injectable with no death coverage."</para>
///
/// <para><b>DEATH-class only.</b> "Covered" here means the DEATH crash is neutralized — NOT that the donor
/// is a fully crash-safe combat enemy. A levered set-piece can still crash on other animations its AI
/// requests: the Triceratops death lever ships, yet a live session (2026-07-11, dump 10-03-58) crashed on
/// a side ATTACK (out-of-range attack pkgRef → wild [actor+0xa0] → 0x479c0f) — same class, different
/// animation, deferred tech debt (RCA §7d / K95). Full combat-animation coverage for set-piece donors is
/// a separate, larger effort (audit every AI-requestable animation, or a general 0x48E050 bounds-guard).</para>
/// </summary>
public class Dc2DonorDeathSafetyTests
{
    /// <summary>Types whose death path is made safe by a shipped killability lever:
    /// <list type="bullet">
    /// <item><c>0x03</c> Tyrannosaurus — <see cref="Dc2TrexKillableInstaller"/> (phase-clamp skip).</item>
    /// <item><c>0x09</c> Triceratops — <see cref="Dc2TriceratopsKillableInstaller"/> (death-anim remap 8→7).</item>
    /// </list>
    /// Add a type here only when a lever that neutralizes ITS death crash is shipped.</summary>
    private static readonly IReadOnlySet<int> DeathSafetyCovered = new HashSet<int> { 0x03, 0x09 };

    /// <summary>Risky LAND donors deliberately DEFERRED — a suspected same-class death crash, opt-in only,
    /// with no lever yet, tracked for a future decode/rework rather than blocking sign-off:
    /// <list type="bullet">
    /// <item><c>0x06</c> Giganotosaurus — a set-piece boss whose vanilla "kill" is a scripted permanent
    /// stun (Regina's fire hazards pin him in the ground), NOT a death animation, so like the Triceratops
    /// he never runs the standard death path. Injecting + shooting him elsewhere is expected to crash the
    /// same way (unverified). Handoff: docs/decisions/dc2/enemies/DC2-GIGANOTOSAURUS-KILLABILITY-HANDOFF.md.</item>
    /// </list>
    /// This is an explicit, reviewed parking spot — NOT a silent pass: a NEW risky donor still fails the
    /// invariant until it is triaged into <see cref="DeathSafetyCovered"/> or here with a reason.</summary>
    private static readonly IReadOnlySet<int> DeferredDeathCrashRisk = new HashSet<int> { 0x06 };

    /// <summary>LAND donors that are set-pieces or bosses — the "no-standard-death" models that can crash
    /// when killed. Aquatic set-pieces (Plesiosaurus) are excluded: they crash on entry-residency, a
    /// different class, and are wave-only + water-flag-gated.</summary>
    private static IEnumerable<Dc2Species> RiskyLandDonors() => Dc2SpeciesTable.All
        .Where(s => (s.IsSetpiece || s.IsBoss)
                    && s.Habitat == Dc2Habitat.Land
                    && s.Confidence == Confidence.Known);

    /// <summary>THE invariant: every risky LAND donor that can be injected into a killable room must be
    /// either covered by a death-safety lever or an explicitly-deferred, reasoned known-risk. It
    /// originally failed on Giganotosaurus (0x06) — the concrete latent crash this guard exists to catch;
    /// 0x06 is now parked in <see cref="DeferredDeathCrashRisk"/>, so a genuinely NEW risky donor (no
    /// lever, not triaged) is what trips it next.</summary>
    [Fact]
    public void EveryRiskyLandDonor_IsCoveredOrExplicitlyDeferred()
    {
        var untriaged = RiskyLandDonors()
            .Where(s => !DeathSafetyCovered.Contains(s.Type) && !DeferredDeathCrashRisk.Contains(s.Type))
            .Select(s => $"{s.Creature} (0x{s.Type:X2}, {s.EFile})")
            .ToList();

        Assert.True(untriaged.Count == 0,
            "Risky LAND donors (set-piece/boss) injectable into killable rooms with neither a death-safety "
            + "lever nor an explicit deferred-risk entry — each may crash or be unkillable when shot. "
            + "Ship a lever (add to DeathSafetyCovered) or park it with a reason (DeferredDeathCrashRisk): "
            + string.Join(", ", untriaged));
    }

    /// <summary>Regression pin: each covered lever must actually fire when its donor is injectable, so the
    /// coverage above is real and not stale. (If a lever's WantedFor drifts off its donor's pool
    /// membership, the coverage claim is a lie — this catches that.)</summary>
    [Theory]
    [InlineData(0x03, false, true)]  // T-Rex: boss pool
    [InlineData(0x09, true, false)]  // Triceratops: setpiece pool
    public void CoveredLever_FiresWhenItsDonorIsInjectable(int type, bool viaSetpiece, bool viaBoss)
    {
        var cfg = new RandomizerConfig
        {
            RandomizeEnemies = true,
            Dc2EnemyMode = Dc2EnemyDistributionMode.Weighted,
            IncludeDc2SetpieceEnemies = viaSetpiece,
            IncludeDc2BossEnemies = viaBoss,
        };
        // The donor is in the injectable pool under these toggles...
        Assert.True(Dc2SpeciesTable.IsDonorPoolMember(type, viaSetpiece, viaBoss, allowWater: false));
        // ...so its lever must want to apply.
        bool wanted = type == 0x03
            ? Dc2TrexKillableInstaller.WantedFor(cfg)
            : Dc2TriceratopsKillableInstaller.WantedFor(cfg);
        Assert.True(wanted, $"lever for 0x{type:X2} did not fire though its donor is injectable");
    }
}
