using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Umad.P3Eq.UmadP3EqConstants;

namespace AnoMech.Scenarios.Umad.P3Eq;

// Drives the Black Hole mechanic (4 sets / 10 waves) for the Earthquake segment, off the
// per-run layout in UmadP3EqState.BlackHoleSets.
//
//  - Each set spawns 11 holes (DataId 19512): 4 middle + 4 inner (cosmetic, never tether) and
//    3 outer-ring holes (the only tether sources).
//  - Each wave spawns passable tethers via SimWorld.TetherPassable, so the engine handles
//    crossing/passing. The initial target is a random player; the SOURCE hole is deterministic:
//      Sets 2 & 3 — all 3 outer holes tether simultaneously, kept across the 3 hits.
//      Sets 1 & 4 — split into a "solo" wave (the one hole at the clockwise predecessor of the
//                   set's absent outer slot) and a "pair" wave (the other two). S1 = solo then
//                   pair; S4 = pair then solo.
//  - On resolve, each tethered hole casts Nothingness and the CURRENT holder takes a hit. After
//    3 total hits a player's in-line debuff is stripped — everyone eats exactly 3 (24 / 8).
internal sealed class UmadP3EqBlackHoles
{
    private readonly SimWorld world;
    private readonly SimParty party;
    private readonly UmadP3EqState state;
    private readonly Random rng = new();

    // Fired the moment a player's Primordial Crust is cleansed (3rd hit). The scenario wires this to an
    // un-casted aftershock proxy centered on CHAOS (the cleanse burst emanates from Chaos, not the
    // cleansed player). Kept as a callback so the proxy/VFX plumbing stays in the scenario (where
    // ProxyCast + the Chaos handle live) rather than duplicated here.
    private readonly Action crustCleanseBurst;

    // Current set state.
    private readonly List<SimEnemy> holes = [];                 // all 11 spawned (for despawn)
    private readonly Dictionary<OuterPos, SimEnemy> outer = []; // the 3 present outer holes
    private OuterPos? solo;

    private readonly List<SimTether> tethers = [];
    private readonly Dictionary<PartyRole, int> hits = new();   // persists across all 4 sets

    // Max Black Hole tethers a single player may hold at once. The beams are passable, so a holder
    // CAN be stacked with several at once (collapse onto one player is intended) — this just caps how
    // many. 8 = whole party, i.e. unbounded for the <=3 simultaneous beams (full collapse allowed);
    // lower it (e.g. 1) to force the beams to coordinate onto distinct players instead.
    private const int MaxTethersPerPlayer = 8;

    // Black Spark contact punish: ANY hole (outer + cosmetic inner/middle) zaps a player whose hitbox
    // overlaps the hole with Damage Down, repeating every SparkPulse (log: a hole pulsed a stationary
    // player ~every 1.1s and was NOT consumed). Cooldown is per-hole; cleared when the set despawns.
    // Contact = the engine's "hitboxes touching" test (hole radius + player radius) — no magic number,
    // self-adjusting to the real hole size; HoleRadiusFallback only kicks in if a hitbox reads 0.
    private const float HoleRadiusFallback = 1.85f;  // used only when hitbox radii are unavailable
    private const float SparkPulse = 1.1f;           // log-measured per-hole tick
    private const float DamageDownDuration = 180f;   // Damage Down lifetime (refreshed each pulse)
    private readonly Dictionary<SimEnemy, float> sparkCooldown = new();

    public UmadP3EqBlackHoles(SimWorld world, SimParty party, UmadP3EqState state, Action crustCleanseBurst)
    {
        this.world = world;
        this.party = party;
        this.state = state;
        this.crustCleanseBurst = crustCleanseBurst;
    }

    // Apply the initial debuffs at Earthquake resolve. In-line + Primordial Crust persist until the
    // Black Hole hits strip them (see ResolveHits); Accretion is a short self-expiring timer.
    public void ApplyDebuffs()
    {
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            if (party.Get(role) is not { } member) continue;
            // "# In Line": shown with NO countdown (pinned at -1). Removed together with Primordial Crust
            // on the player's 3rd Black Hole hit (see ResolveHits) — only Meanest Existence remains after.
            member.AddStatus(InLineStatus(role), duration: -1f);
            // Primordial Crust: every player. The visible countdown is the real cleanse deadline, keyed
            // to the in-line number (log-accurate: First 72s / Second 106s / Third 139s).
            member.AddStatus(StatusId.PrimordialCrust, duration: CrustDuration(role));
        }
        // Accretion: one healer + one DPS — a 14s countdown that expires on its own.
        foreach (var role in state.Accretion)
            party.Get(role)?.AddStatus(StatusId.Accretion, duration: 14f);
    }

    // Primordial Crust cleanse deadline by in-line number (log-accurate: First 72s / Second 106s / Third 139s).
    private float CrustDuration(PartyRole role) => state.NumberOf(role) switch
    {
        1 => 72f,
        2 => 106f,
        _ => 139f,
    };

    // Per-frame contact check (driven from the scenario's real-time Tick). Any alive player standing in
    // ANY hole (all 11 of the live set, outer + cosmetic) gets zapped by Black Spark (BCCD) + Damage Down
    // on a ~1.1s per-hole pulse; the hole is NOT consumed. No live set → empty list → no-op.
    public void Tick(float delta)
    {
        foreach (var hole in holes)
        {
            // Per-hole pulse cadence: count down while on cooldown, otherwise it's ready to zap.
            if (sparkCooldown.GetValueOrDefault(hole) is var cd && cd > 0f)
            {
                sparkCooldown[hole] = cd - delta;
                continue;
            }

            var pulsed = false;
            foreach (var player in AlivePlayers())
            {
                var d = player.Position - hole.Position;
                var reach = hole.HitboxRadius + player.HitboxRadius;     // "hitboxes touching"
                if (reach <= 0.01f) reach = HoleRadiusFallback;          // guard: hitbox not loaded yet
                if (d.X * d.X + d.Z * d.Z > reach * reach) continue;
                player.AddStatus(StatusId.DamageDown, duration: DamageDownDuration);
                pulsed = true;
            }
            // Black Spark (BCCD) has no VFX/omen asset and no perceptible visual (its only animation is a
            // monster hit-clip that doesn't read on the hole's swirl model — verified in-game). So there's
            // nothing to render: the contact feedback is just the Damage Down above + the hole's own swirl.
            if (pulsed) sparkCooldown[hole] = SparkPulse;
        }
    }

    // Spawn the 11 holes for set `setIndex` (0..3). Assumes the previous set was despawned.
    public void SpawnSet(int setIndex)
    {
        var set = state.BlackHoleSets[setIndex];
        foreach (var p in set.Cosmetic) SpawnHole(p);
        foreach (var (pos, local) in set.Outer)
            if (SpawnHole(local) is { } hole) outer[pos] = hole;
        solo = set.Solo;
    }

    // S1 wave 1 / S4 wave 2 — the single tether from the solo (CW-predecessor) hole.
    public void TetherSolo()
    {
        if (solo is not { } s || !outer.TryGetValue(s, out var hole)) return;
        TetherFrom([hole]);
    }

    // S1 wave 2 / S4 wave 1 — the two tethers from the non-solo outer holes.
    public void TetherPair() =>
        TetherFrom(outer.Where(kv => kv.Key != solo).Select(kv => kv.Value).ToList());

    // S2 / S3 — all three outer holes tether at once (kept across the set's 3 hits).
    public void TetherAllThree() => TetherFrom(outer.Values.ToList());

    // Random distinct initial holders, one per source hole.
    private void TetherFrom(IReadOnlyList<SimEnemy> sources)
    {
        var players = AlivePlayers().OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < sources.Count && i < players.Count; i++)
            // Reverse the visual so the beam emanates from the black hole, not the player — the
            // passable source is still the migrating player; only the VFX host end is flipped.
            tethers.Add(world.TetherPassable(players[i], sources[i], TetherId.BlackHole3, maxPerPlayer: MaxTethersPerPlayer).SetReverseVisual());
    }

    // Each tethered hole cleaves and hits its current holder, advancing that player's "unmaking"
    // debuff chain.
    //
    // `clearTethers: true` despawns this wave's tethers the instant it resolves (lock-on) instead of
    // leaving them up until a later ClearTethers/DespawnSet — so the beams don't linger. Pass true for
    // every wave that re-sources from a different hole next (sets 1 & 4) and for each set's FINAL wave
    // (sets 2 & 3 wave 3). Persistent waves (sets 2/3 waves 1-2) pass false so the tether survives to
    // migrate to its next holder.
    //
    // Per-player progression (none of these carry a visible timer — all pinned):
    //   hit 1 → +Unbecoming (1 stack; never accrues to 2+)
    //   hit 2 → swap Unbecoming → Meanest Existence
    //   hit 3 → cleanse Primordial Crust AND its in-line debuff (Meanest Existence stays), drop the
    //           holder to 1 HP; healed back to full ~2s later (see DropToOne).
    public void ResolveHits(bool clearTethers = false)
    {
        foreach (var tether in tethers)
        {
            if (tether.A is not { } holder) continue;
            // Aim the line AOE at the player currently holding this hole's tether (passable
            // tethers update A as players cross), so it resolves toward the holder, not due south.
            // Rotate the hole to face the holder before casting — the cast omen orients off the
            // caster's facing. atan2(dx, dz) matches the engine's convention (0 = south, π = north).
            if (tether.B is SimEnemy hole)
            {
                var d = holder.Position - hole.Position;
                if (d.X * d.X + d.Z * d.Z > 1e-6f)
                    hole.SetRotation(MathF.Atan2(d.X, d.Z));
                hole.Cast(ActionId.Nothingness, targetLocation: holder.Position, targetId: holder.GameObjectId);
            }
            if ((holder as ISimPartyMember)?.Role is not { } role) continue;
            hits[role] = hits.GetValueOrDefault(role) + 1;
            switch (hits[role])
            {
                case 1: // pinned at -1 so no countdown is shown (the chain is consumed, not timed)
                    holder.AddStatus(StatusId.Unbecoming, duration: -1f);
                    break;
                case 2:
                    holder.RemoveStatus(StatusId.Unbecoming);
                    holder.AddStatus(StatusId.MeanestExistence, duration: -1f);
                    break;
                case 3:
                    holder.RemoveStatus(StatusId.PrimordialCrust);
                    holder.RemoveStatus(InLineStatus(role));
                    crustCleanseBurst(); // aftershock burst around Chaos
                    DropToOne(holder);
                    break;
                // >3 (possible since holders are assigned randomly): already fully resolved, nothing to do.
            }
        }
        if (clearTethers) ClearTethers();
    }

    // The 3rd Black Hole hit is lethal — it cleanses Primordial Crust by bottoming the holder out at
    // 1 HP, then a heal refills them ~2s later. There's no safe HP surface on SimCharacter, so we poke
    // bc->Health directly; this stays inside the scenario (no engine change). PartyHud mirrors Health
    // into the party list every frame, so the bar visibly drops then refills. Doppels only — the real
    // player's HP is server-authoritative, so we leave SimPlayer alone.
    private unsafe void DropToOne(SimCharacter holder)
    {
        if (holder is not SimPartyNpc) return;
        var bc = holder.BattleCharaPtr;
        if (bc == null) return;
        var max = bc->MaxHealth;
        bc->Health = 1;
        world.Events.Add(2f, () => SetHealth(holder, max));
    }

    private static unsafe void SetHealth(SimCharacter holder, uint hp)
    {
        var bc = holder.BattleCharaPtr;
        if (bc != null) bc->Health = hp;
    }

    public void ClearTethers()
    {
        foreach (var tether in tethers) tether.Despawn();
        tethers.Clear();
    }

    public void DespawnSet()
    {
        ClearTethers();
        foreach (var hole in holes) hole.Despawn();
        holes.Clear();
        outer.Clear();
        sparkCooldown.Clear();
        solo = null;
    }

    // Mid-set despawn of just the SOLO outer hole. Set 1 (Pattern A) fires the solo wave first, then
    // the solo hole vanishes ~T+9 while the pair wave is still tethered — the pair + cosmetics stay
    // until DespawnSet. No-op if there's no solo slot or it's already gone.
    public void DespawnSolo()
    {
        if (solo is { } s && outer.Remove(s, out var hole))
            RemoveHole(hole);
    }

    // Mid-set despawn of the two non-solo (PAIR) outer holes. Set 4 (Pattern B) fires the pair wave
    // first, then the pair holes vanish ~T+9 while the solo wave is still tethered.
    public void DespawnPair()
    {
        foreach (var pos in outer.Keys.Where(p => p != solo).ToList())
            if (outer.Remove(pos, out var hole))
                RemoveHole(hole);
    }

    // Despawn one hole and drop it from every tracking collection so neither the contact-punish Tick
    // nor a later DespawnSet touches the freed handle again.
    private void RemoveHole(SimEnemy hole)
    {
        hole.Despawn();
        holes.Remove(hole);
        sparkCooldown.Remove(hole);
    }

    private SimEnemy? SpawnHole(Vector3 local)
    {
        var hole = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.BlackHole,
            Targetable: false,
            EnemyList: EnemyListMode.Never,
            IsVisible: true,
            Placement: new Placement(local, 0f)));
        if (hole != null) holes.Add(hole);
        return hole;
    }

    private ushort InLineStatus(PartyRole role) => state.NumberOf(role) switch
    {
        1 => StatusId.FirstTarget,
        2 => StatusId.SecondTarget,
        _ => StatusId.ThirdTarget,
    };

    private List<SimCharacter> AlivePlayers()
    {
        var list = new List<SimCharacter>();
        foreach (var role in Enum.GetValues<PartyRole>())
            if (party.Get(role) is { } member && member.IsAlive())
                list.Add(member);
        return list;
    }
}
