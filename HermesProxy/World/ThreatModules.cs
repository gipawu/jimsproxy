using Framework.Logging;
using HermesProxy.World.Objects;
using System.Collections.Generic;

namespace HermesProxy.World;

// Phase 3 of the threat translation port. Registers per-spell threat behavior
// for the local player and pet — Hunter and Pet abilities first, other classes
// follow in later phases.
//
// LibThreatClassic2 (the upstream addon library this is ported from) hangs each
// spell off a Lua module's CastSuccessHandlers / CastMissHandlers tables. We
// flatten that into a single static dictionary keyed by spell id. All threat
// numbers come straight from KTMClassic\Libs\LibThreatClassic2\ClassModules\
// Classic\{Hunter,Pet}.lua so on-wire client behavior stays bug-for-bug
// compatible with what an addon-driven setup would show.
//
// Caster gating: every handler re-checks that the caster is the local player
// or pet before mutating threat. The OnDamage path already does the same check;
// we duplicate it here because spell hits propagate via SMSG_SPELL_GO for any
// caster in range, not just the local player's threaters.
internal static class ThreatModules
{
    private delegate void ThreatHandler(
        ThreatTracker tracker,
        GlobalSessionData session,
        int spellId,
        WowGuid128 caster,
        IList<WowGuid128> hitTargets);

    private static readonly Dictionary<int, ThreatHandler> Handlers = new()
    {
        // Hunter — Distracting Shot (rank 1..6). Flat add; misses subtract but
        // we don't see a CastMiss event server-side, so we only model success.
        [20736] = HunterDistractingShot,
        [14274] = HunterDistractingShot,
        [15629] = HunterDistractingShot,
        [15630] = HunterDistractingShot,
        [15631] = HunterDistractingShot,
        [15632] = HunterDistractingShot,

        // Hunter — Disengage (rank 1..3). Flat negative threat against the
        // current target. SMSG_SPELL_GO fires on success, so subtract directly.
        [781]   = HunterDisengage,
        [14272] = HunterDisengage,
        [14273] = HunterDisengage,

        // Hunter — Feign Death. ×0 multiplier across every mob the player has
        // threat on. Vanilla addon code waits for ERR_FEIGN_DEATH_RESISTED to
        // un-prime; on the proxy we apply unconditionally — the legacy server
        // already cleared its threat on success, so a per-mob resist will just
        // re-establish threat naturally as combat resumes.
        [5384]  = HunterFeignDeath,

        // Pet — Growl (rank 1..7). Flat threat add per rank. Vanilla addon
        // additionally scales by pet AP; we ship base values now and revisit
        // AP scaling in Phase 3.5 (needs UNIT_FIELD_ATTACK_POWER lookup +
        // tester validation that pet field caching is current).
        [2649]  = PetGrowl,
        [14916] = PetGrowl,
        [14917] = PetGrowl,
        [14918] = PetGrowl,
        [14919] = PetGrowl,
        [14920] = PetGrowl,
        [14921] = PetGrowl,

        // Pet — Cower (rank 1..6). Flat negative threat on current target.
        [1742]  = PetCower,
        [1753]  = PetCower,
        [1754]  = PetCower,
        [1755]  = PetCower,
        [1756]  = PetCower,
        [16697] = PetCower,
    };

    private static readonly Dictionary<int, double> DistractingShotAmount = new()
    {
        [20736] = 120, [14274] = 200, [15629] = 300,
        [15630] = 400, [15631] = 500, [15632] = 600,
    };

    private static readonly Dictionary<int, double> DisengageAmount = new()
    {
        [781] = -140, [14272] = -280, [14273] = -405,
    };

    private static readonly Dictionary<int, double> GrowlAmount = new()
    {
        [2649] = 50, [14916] = 65, [14917] = 110,
        [14918] = 170, [14919] = 240, [14920] = 320, [14921] = 415,
    };

    private static readonly Dictionary<int, double> CowerAmount = new()
    {
        [1742] = -30, [1753] = -55, [1754] = -85,
        [1755] = -125, [1756] = -175, [16697] = -225,
    };

    public static bool TryHandle(
        ThreatTracker tracker,
        GlobalSessionData session,
        int spellId,
        WowGuid128 caster,
        IList<WowGuid128> hitTargets)
    {
        if (!Handlers.TryGetValue(spellId, out var handler))
            return false;

        handler(tracker, session, spellId, caster, hitTargets);
        return true;
    }

    private static void HunterDistractingShot(ThreatTracker tracker, GlobalSessionData session, int spellId, WowGuid128 caster, IList<WowGuid128> hitTargets)
    {
        if (caster != session.GameState.CurrentPlayerGuid) return;
        if (hitTargets.Count == 0) return;
        if (!DistractingShotAmount.TryGetValue(spellId, out double amount)) return;

        var target = hitTargets[0];
        tracker.AddThreat(target, caster, amount);

        Log.Event("threat.spell.distracting_shot", new
        {
            spell_id = spellId,
            target_low = target.GetCounter(),
            amount,
        });
    }

    private static void HunterDisengage(ThreatTracker tracker, GlobalSessionData session, int spellId, WowGuid128 caster, IList<WowGuid128> hitTargets)
    {
        if (caster != session.GameState.CurrentPlayerGuid) return;
        if (hitTargets.Count == 0) return;
        if (!DisengageAmount.TryGetValue(spellId, out double amount)) return;

        var target = hitTargets[0];
        tracker.AddThreat(target, caster, amount);

        Log.Event("threat.spell.disengage", new
        {
            spell_id = spellId,
            target_low = target.GetCounter(),
            amount,
        });
    }

    private static void HunterFeignDeath(ThreatTracker tracker, GlobalSessionData session, int spellId, WowGuid128 caster, IList<WowGuid128> hitTargets)
    {
        if (caster != session.GameState.CurrentPlayerGuid) return;

        tracker.MultiplyThreat(caster, 0.0);

        Log.Event("threat.spell.feign_death", new
        {
            spell_id = spellId,
            caster_low = caster.GetCounter(),
        });
    }

    private static void PetGrowl(ThreatTracker tracker, GlobalSessionData session, int spellId, WowGuid128 caster, IList<WowGuid128> hitTargets)
    {
        if (caster != session.GameState.CurrentPetGuid) return;
        if (hitTargets.Count == 0) return;
        if (!GrowlAmount.TryGetValue(spellId, out double amount)) return;

        var target = hitTargets[0];
        tracker.AddThreat(target, caster, amount);

        Log.Event("threat.spell.growl", new
        {
            spell_id = spellId,
            target_low = target.GetCounter(),
            amount,
        });
    }

    private static void PetCower(ThreatTracker tracker, GlobalSessionData session, int spellId, WowGuid128 caster, IList<WowGuid128> hitTargets)
    {
        if (caster != session.GameState.CurrentPetGuid) return;
        if (hitTargets.Count == 0) return;
        if (!CowerAmount.TryGetValue(spellId, out double amount)) return;

        var target = hitTargets[0];
        tracker.AddThreat(target, caster, amount);

        Log.Event("threat.spell.cower", new
        {
            spell_id = spellId,
            target_low = target.GetCounter(),
            amount,
        });
    }
}
