using Framework.Logging;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;

namespace HermesProxy.World;

// JimsProxy threat translation: client-side threat calculation engine.
//
// Vanilla 1.12 servers don't broadcast threat over the wire; threat tables live
// in server-side internal state. The modern 1.14 client expects SMSG_THREAT_UPDATE
// (and friends) so its native APIs (UnitDetailedThreatSituation, UnitThreatSituation,
// UNIT_THREAT_LIST_UPDATE event) populate. Without those packets the modern client
// shows zero threat and downstream addons (Details TinyThreat, TidyPlates_ThreatPlates,
// default UI target frame) get nothing to display.
//
// This class observes combat events forwarded from the legacy server, computes
// threat per LibThreatClassic2 rules (port-in-progress, see ClassModules/), and
// emits modern SMSG threat opcodes to the client.
//
// Phase 2 scope: track damage threat for the local player and their pet. Other
// group members' threat is not yet shown (Phase 6 group sync). Class-specific
// abilities (Distracting Shot, Feign Death, Sunder Armor flat threat, defensive
// stance multipliers, etc.) ship in Phase 3+.
//
// Threading: methods are called from the WorldClient thread (which dispatches
// SMSG packet handlers). Emission goes back via WorldClient.SendPacketToClient
// so it stays on the same thread — no cross-thread state mutation.
public sealed class ThreatTracker
{
    // Per-mob threat list. Outer key = threatened entity (mob). Inner dict
    // maps threater (player/pet) -> raw threat value. We pack × 100 only at
    // emit time so internal math can stay floating point.
    private readonly Dictionary<WowGuid128, Dictionary<WowGuid128, double>> _threatLists = new();

    // Mobs whose threat list has been mutated since the last emit. Flushed
    // in EmitDirty(). Lets a single damage event that touches multiple mobs
    // (e.g. Multi-Shot, Volley) batch into one round of SMSG emission.
    private readonly HashSet<WowGuid128> _dirty = new();

    // Last-emitted highest threater per mob — so we only emit
    // SMSG_HIGHEST_THREAT_UPDATE when the top actually changes.
    private readonly Dictionary<WowGuid128, WowGuid128> _lastHighest = new();

    private readonly GlobalSessionData _session;

    public ThreatTracker(GlobalSessionData session)
    {
        _session = session;
    }

    // Add (or subtract, if amount is negative) raw threat from a threater
    // against a mob. Marks the mob dirty so the next EmitDirty pushes the
    // updated threat list to the client.
    public void AddThreat(WowGuid128 mob, WowGuid128 threater, double amount)
    {
        if (mob == default || threater == default || amount == 0)
            return;

        if (!_threatLists.TryGetValue(mob, out var list))
        {
            list = new Dictionary<WowGuid128, double>();
            _threatLists[mob] = list;
        }

        list.TryGetValue(threater, out double existing);
        double updated = existing + amount;
        // Vanilla clamps threat at zero — negative threat is invisible to the
        // server's pull aggro check. LibThreatClassic2 mirrors this.
        if (updated < 0) updated = 0;
        list[threater] = updated;

        _dirty.Add(mob);
    }

    // Set a threater's threat on a mob to an absolute value. Used by Growl
    // (which sets pet threat to current top + 1) in Phase 3.
    public void SetThreat(WowGuid128 mob, WowGuid128 threater, double amount)
    {
        if (mob == default || threater == default)
            return;

        if (!_threatLists.TryGetValue(mob, out var list))
        {
            list = new Dictionary<WowGuid128, double>();
            _threatLists[mob] = list;
        }
        if (amount < 0) amount = 0;
        list[threater] = amount;
        _dirty.Add(mob);
    }

    // Multiply a single threater's threat by a factor across ALL their mobs.
    // Hunter Feign Death uses this (factor = 0). Returns to the caller via
    // dirty-mark; emit happens on next flush.
    public void MultiplyThreat(WowGuid128 threater, double factor)
    {
        if (threater == default) return;
        foreach (var (mob, list) in _threatLists)
        {
            if (list.TryGetValue(threater, out double current))
            {
                double updated = current * factor;
                if (updated < 0) updated = 0;
                list[threater] = updated;
                _dirty.Add(mob);
            }
        }
    }

    // Mob died, ran far away, evaded, etc. Remove its threat list entirely
    // and emit SMSG_THREAT_CLEAR so the modern client knows to drop it from
    // the threat APIs.
    public void ClearMob(WowGuid128 mob)
    {
        if (mob == default) return;
        if (!_threatLists.Remove(mob))
            return;
        _lastHighest.Remove(mob);
        _dirty.Remove(mob);

        var pkt = new ThreatClearPkt { UnitGUID = mob };
        SendToClient(pkt);

        Log.Event("threat.mob_cleared", new
        {
            mob_guid = mob.ToString(),
        });
    }

    // A single threater (e.g. a player who left the group, died) drops off a
    // mob's threat list. Vanilla also fires this on Vanish, etc. — Phase 3+.
    public void RemoveThreater(WowGuid128 mob, WowGuid128 threater)
    {
        if (mob == default || threater == default) return;
        if (!_threatLists.TryGetValue(mob, out var list)) return;
        if (!list.Remove(threater)) return;

        var pkt = new ThreatRemovePkt
        {
            UnitGUID = mob,
            AboutGUID = threater,
        };
        SendToClient(pkt);

        // The mob's remaining list may still need an update (since the top
        // may have changed). Mark dirty so EmitDirty refreshes it.
        if (list.Count > 0)
        {
            _dirty.Add(mob);
        }
        else
        {
            _threatLists.Remove(mob);
            _lastHighest.Remove(mob);
        }
    }

    // Flush all pending dirty mobs to the client. Call after a batch of damage
    // events — typically once per WorldClient packet dispatch is fine since
    // most encounters fire one combat event at a time.
    public void EmitDirty()
    {
        if (_dirty.Count == 0) return;

        // Snapshot then clear before emitting so any re-entrancy from the
        // emit path doesn't see the same dirty mobs twice.
        var toEmit = new List<WowGuid128>(_dirty);
        _dirty.Clear();

        foreach (var mob in toEmit)
        {
            if (!_threatLists.TryGetValue(mob, out var list) || list.Count == 0)
                continue;

            // Find the top threater. Ties broken arbitrarily — vanilla itself
            // has tie-breaking quirks but they don't matter for display.
            WowGuid128 newHighest = default;
            double highestValue = -1;
            foreach (var (threater, value) in list)
            {
                if (value > highestValue)
                {
                    highestValue = value;
                    newHighest = threater;
                }
            }

            var update = new ThreatUpdatePkt { UnitGUID = mob };
            foreach (var (threater, value) in list)
            {
                update.ThreatList.Add(new ThreatInfo
                {
                    ThreaterGUID = threater,
                    Threat = ToWireThreat(value),
                });
            }
            SendToClient(update);

            // Emit HIGHEST only when the top changes — saves churn but keeps
            // tank-aggro indicators (red border, nameplate color) snappy.
            _lastHighest.TryGetValue(mob, out var prevHighest);
            if (prevHighest != newHighest)
            {
                _lastHighest[mob] = newHighest;
                var highest = new HighestThreatUpdatePkt
                {
                    UnitGUID = mob,
                    HighestThreatGUID = newHighest,
                };
                foreach (var (threater, value) in list)
                {
                    highest.ThreatList.Add(new ThreatInfo
                    {
                        ThreaterGUID = threater,
                        Threat = ToWireThreat(value),
                    });
                }
                SendToClient(highest);
            }
        }
    }

    // Wipe everything — used on session disconnect / character switch. Doesn't
    // emit packets since the client connection is going away anyway.
    public void Reset()
    {
        _threatLists.Clear();
        _lastHighest.Clear();
        _dirty.Clear();
    }

    // Called from the SMSG_DESTROY_OBJECT handler. Two cases to clean up:
    //   1) The destroyed unit was a mob we tracked → ClearMob (emit SMSG_THREAT_CLEAR).
    //   2) The destroyed unit was a threater (player who left, pet despawned) →
    //      RemoveThreater across every mob that had them on its list.
    public void OnUnitDestroyed(WowGuid128 guid)
    {
        if (guid == default) return;

        // Case 1: this guid was a mob in our tracked set.
        if (_threatLists.ContainsKey(guid))
        {
            ClearMob(guid);
        }

        // Case 2: this guid was a threater on some other mob's list. Scan all
        // remaining lists. Vanilla group sizes keep this O(party) — cheap.
        List<WowGuid128>? mobsToCleanup = null;
        foreach (var (mob, list) in _threatLists)
        {
            if (list.ContainsKey(guid))
            {
                mobsToCleanup ??= new List<WowGuid128>();
                mobsToCleanup.Add(mob);
            }
        }
        if (mobsToCleanup != null)
        {
            foreach (var mob in mobsToCleanup)
                RemoveThreater(mob, guid);
            EmitDirty();
        }
    }

    // Called by the SMSG combat-log observers in WorldClient handlers. Checks
    // if the attacker is one of "my threaters" (the local player or their pet)
    // and adds damage threat against the victim mob if so. Damage threat in
    // vanilla is 1.0 × raw damage with no school multiplier (modifiers from
    // class abilities like Defensive Stance ×1.45 ship in Phase 3+).
    //
    // Auto-flushes the dirty set so a single combat-log packet results in at
    // most one SMSG_THREAT_UPDATE on the wire. Callers don't need to remember
    // to call EmitDirty.
    public void OnDamage(WowGuid128 attacker, WowGuid128 victim, double rawDamage)
    {
        if (rawDamage <= 0) return;
        if (!IsLocalThreater(attacker)) return;
        if (victim == default) return;

        AddThreat(victim, attacker, rawDamage);
        EmitDirty();
    }

    // True if the given GUID is the local player or a unit they currently own
    // (pet, totem, guardian). Pet ownership is read from UNIT_FIELD_SUMMONEDBY
    // on the unit's cached fields, which the legacy server populates and the
    // proxy mirrors. We re-check on every event rather than caching because
    // pets get swapped (Hunter Call Pet, Warlock summon swap) and the cost
    // of one dictionary lookup per damage event is negligible.
    private bool IsLocalThreater(WowGuid128 guid)
    {
        if (guid == default) return false;
        var playerGuid = _session.GameState.CurrentPlayerGuid;
        if (guid == playerGuid) return true;

        var fields = _session.GameState.GetCachedObjectFieldsLegacy(guid);
        if (fields == null) return false;

        int summonedByIdx = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_SUMMONEDBY);
        if (summonedByIdx < 0) return false;

        WowGuid64 summonedBy64 = fields.GetGuidValue(summonedByIdx);
        if (summonedBy64 == WowGuid64.Empty) return false;

        WowGuid128 summonedBy128 = summonedBy64.To128(_session.GameState);
        return summonedBy128 == playerGuid;
    }

    private static uint ToWireThreat(double rawThreat)
    {
        // Modern protocol packs threat × 100. Classic Era addons divide back
        // down on read (verified in Details_TinyThreat.lua). Clamp to uint
        // range to avoid wrap if some absurd encounter happens.
        double scaled = rawThreat * 100.0;
        if (scaled <= 0) return 0;
        if (scaled >= uint.MaxValue) return uint.MaxValue;
        return (uint)scaled;
    }

    private void SendToClient(ServerPacket packet)
    {
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;
        worldClient.SendPacketToClient(packet);
    }
}
