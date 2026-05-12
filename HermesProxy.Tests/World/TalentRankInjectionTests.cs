using System.Collections.Frozen;
using System.Collections.Generic;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// Pins the talent-rank injection algorithm shipped in SpellHandler.cs's
// ReconcileTalentRankInjection + the SMSG_SEND_KNOWN_SPELLS embed path.
//
// The bug: vanilla's Player::LearnTalent calls RemoveSpell on the previous rank when
// the player spends a point higher. The server's known-spells set only ever holds the
// HIGHEST rank of any multi-rank talent. The 1.14 client's IsPlayerSpell(rankNid)
// returns false for any rank below the highest, breaking talent-keyed lookups in
// LibClassicDurations (Permafrost-extended Chilled, Improved Battle Shout duration,
// Booming Voice on Demoralizing Shout, Imp Drain Soul, etc.).
//
// Fix: maintain a side-set of synthesized predecessor rank ids in the modern client's
// known-spells set. The desired-set is recomputed from every spell in the real
// known-spells set; the delta against the current synthesized-set drives synthetic
// SMSG_LEARNED_SPELLS / SMSG_UNLEARNED_SPELLS emits.
public class TalentRankInjectionTests
{
    // Mirrors ReconcileTalentRankInjection's delta computation. Inputs are the real
    // server-tracked known set, the current synthesized set, and the predecessor
    // lookup. Returns the spells to add to + remove from the synthesized set so it
    // matches the desired union of predecessors-of-known minus known.
    public static (List<uint> ToAdd, List<uint> ToRemove) ComputeDelta(
        HashSet<uint> realKnown,
        HashSet<uint> synthesized,
        IReadOnlyDictionary<uint, uint[]> predecessors)
    {
        var desired = new HashSet<uint>();
        foreach (var sid in realKnown)
        {
            if (!predecessors.TryGetValue(sid, out var preds))
                continue;
            foreach (var p in preds)
            {
                if (!realKnown.Contains(p))
                    desired.Add(p);
            }
        }
        var toAdd = new List<uint>();
        foreach (var d in desired)
            if (!synthesized.Contains(d))
                toAdd.Add(d);
        var toRemove = new List<uint>();
        foreach (var s in synthesized)
            if (!desired.Contains(s))
                toRemove.Add(s);
        return (toAdd, toRemove);
    }

    // Permafrost (Mage Frost talent, talent_id 65, 3 ranks): rank ids 11175, 12569, 12571.
    private static IReadOnlyDictionary<uint, uint[]> PermafrostPredecessors() => new Dictionary<uint, uint[]>
    {
        [11175] = System.Array.Empty<uint>(),
        [12569] = new uint[] { 11175 },
        [12571] = new uint[] { 11175, 12569 },
    };

    // Improved Battle Shout (Warrior Fury, talent_id 158, 5 ranks): 12321, 12835, 12836, 12837, 12838.
    private static IReadOnlyDictionary<uint, uint[]> ImprovedBattleShoutPredecessors() => new Dictionary<uint, uint[]>
    {
        [12321] = System.Array.Empty<uint>(),
        [12835] = new uint[] { 12321 },
        [12836] = new uint[] { 12321, 12835 },
        [12837] = new uint[] { 12321, 12835, 12836 },
        [12838] = new uint[] { 12321, 12835, 12836, 12837 },
    };

    // === Rank-spend scenarios ===

    [Fact]
    public void Permafrost1of3_AddsNoPredecessors()
    {
        var known = new HashSet<uint> { 11175 }; // rank 1
        var synth = new HashSet<uint>();
        var (toAdd, toRemove) = ComputeDelta(known, synth, PermafrostPredecessors());
        Assert.Empty(toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void Permafrost2of3_AddsRank1AsPredecessor()
    {
        var known = new HashSet<uint> { 12569 }; // rank 2 (rank 1 removed by server)
        var synth = new HashSet<uint>();
        var (toAdd, toRemove) = ComputeDelta(known, synth, PermafrostPredecessors());
        Assert.Equal(new[] { 11175u }, toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void Permafrost3of3_AddsRank1AndRank2AsPredecessors()
    {
        var known = new HashSet<uint> { 12571 };
        var synth = new HashSet<uint>();
        var (toAdd, toRemove) = ComputeDelta(known, synth, PermafrostPredecessors());
        Assert.Equal(2, toAdd.Count);
        Assert.Contains(11175u, toAdd);
        Assert.Contains(12569u, toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void Permafrost2to3_ReplacesSynthesizedPredecessor()
    {
        // Player went from 2/3 to 3/3. Server: UNLEARN(12569) then LEARN(12571).
        // Going into reconcile: realKnown has 12571, synthesized still has 11175 from
        // the previous reconcile at 2/3.
        var known = new HashSet<uint> { 12571 };
        var synth = new HashSet<uint> { 11175 };
        var (toAdd, toRemove) = ComputeDelta(known, synth, PermafrostPredecessors());
        Assert.Equal(new[] { 12569u }, toAdd); // rank 2 is the newly-needed predecessor
        Assert.Empty(toRemove); // rank 1 stays
    }

    // === Respec scenarios ===

    [Fact]
    public void Permafrost3of3_Respec_RemovesAllSynthesizedRanks()
    {
        // Player respec: server unlearns 12571 (the only rank it knows). Reconcile sees
        // real known is empty, synthesized still has the two predecessors → remove both.
        var known = new HashSet<uint>();
        var synth = new HashSet<uint> { 11175, 12569 };
        var (toAdd, toRemove) = ComputeDelta(known, synth, PermafrostPredecessors());
        Assert.Empty(toAdd);
        Assert.Equal(2, toRemove.Count);
        Assert.Contains(11175u, toRemove);
        Assert.Contains(12569u, toRemove);
    }

    [Fact]
    public void Permafrost3to2_RemovesRank2SynthesizedAfterServerSwap()
    {
        // Player respec'd one point out: 3/3 → 2/3. Server unlearns 12571, learns 12569.
        // Mid-flow synthesized set: {11175, 12569}. After UNLEARN(12571), realKnown={}
        // and synth={11175,12569} would temporarily remove both — but the LEARN(12569)
        // is paired, so the post-flow state must show realKnown={12569}, synth={11175}.
        var known = new HashSet<uint> { 12569 };
        var synth = new HashSet<uint> { 11175, 12569 }; // 12569 was synthesized at 3/3
        var (toAdd, toRemove) = ComputeDelta(known, synth, PermafrostPredecessors());
        Assert.Empty(toAdd);
        Assert.Equal(new[] { 12569u }, toRemove); // 12569 is now real-known, must leave synthesized
    }

    [Fact]
    public void ImprovedBattleShout5of5_AddsAllFourLowerRanks()
    {
        var known = new HashSet<uint> { 12838 };
        var synth = new HashSet<uint>();
        var (toAdd, toRemove) = ComputeDelta(known, synth, ImprovedBattleShoutPredecessors());
        Assert.Equal(4, toAdd.Count);
        foreach (uint expected in new uint[] { 12321, 12835, 12836, 12837 })
            Assert.Contains(expected, toAdd);
    }

    // === Idempotence ===

    [Fact]
    public void Reconcile_IsIdempotent_WhenStateAlreadyConsistent()
    {
        var known = new HashSet<uint> { 12571 };
        var synth = new HashSet<uint> { 11175, 12569 }; // already correct
        var (toAdd, toRemove) = ComputeDelta(known, synth, PermafrostPredecessors());
        Assert.Empty(toAdd);
        Assert.Empty(toRemove);
    }

    // === GameData loader: pin a few well-known entries from the shipped CSV. ===

    [Fact]
    public void GameData_TalentRankPredecessors_LoadsPermafrostCorrectly()
    {
        GameData.LoadTalentSpellRanks();
        Assert.True(GameData.TalentRankPredecessors.ContainsKey(12571), "Permafrost rank 3 spell missing");
        var preds = GameData.TalentRankPredecessors[12571];
        Assert.Equal(new uint[] { 11175, 12569 }, preds);
    }

    [Fact]
    public void GameData_TalentRankPredecessors_LoadsImprovedBattleShoutCorrectly()
    {
        GameData.LoadTalentSpellRanks();
        Assert.True(GameData.TalentRankPredecessors.ContainsKey(12838), "Improved Battle Shout rank 5 missing");
        Assert.Equal(new uint[] { 12321, 12835, 12836, 12837 }, GameData.TalentRankPredecessors[12838]);
    }

    [Fact]
    public void GameData_TalentRankPredecessors_Rank1HasEmptyPredecessors()
    {
        GameData.LoadTalentSpellRanks();
        Assert.True(GameData.TalentRankPredecessors.ContainsKey(11175));
        Assert.Empty(GameData.TalentRankPredecessors[11175]);
    }

    [Fact]
    public void GameData_TalentRankSiblings_IncludesAllOtherRanks()
    {
        GameData.LoadTalentSpellRanks();
        // Permafrost middle rank (12569): siblings should be 11175 (lower) + 12571 (higher).
        Assert.True(GameData.TalentRankSiblings.ContainsKey(12569));
        var sibs = new HashSet<uint>(GameData.TalentRankSiblings[12569]);
        Assert.Contains(11175u, sibs);
        Assert.Contains(12571u, sibs);
        Assert.Equal(2, sibs.Count);
    }

    [Fact]
    public void GameData_TalentRankPredecessors_CoversAllNineClasses()
    {
        GameData.LoadTalentSpellRanks();
        // Smoke test: at least 400 rank entries, expected ~1000+ across 432 talents.
        Assert.True(GameData.TalentRankPredecessors.Count > 400,
            $"Expected > 400 talent rank entries, got {GameData.TalentRankPredecessors.Count}");
    }
}
