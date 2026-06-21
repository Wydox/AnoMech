using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Umad.P3Eq.UmadP3EqConstants;

namespace AnoMech.Scenarios.Umad.P3Eq;

// UMAD P3 "Earthquake" — second half of Phase 3 (Chaos + Exdeath + Kefka), from Chaos's
// Earthquake cast through the P3 enrage.
//
// Times are log-accurate, rebased so Earthquake's
// CAST starts at t3.57 (a 3.57 s lead-in — long enough to fit Max's pre-EQ cast at its true EQ−3.07
// offset): every absolute time below = the note's EQ-relative offset + 3.57. Boss casts replay as
// cast bars (their real omens auto-draw, oriented by the caster's
// facing); the proxy-delivered resolve VFX (Earthquake AoE, aftershocks, Slap Happy shockwave,
// Look-upon-Me line, Stomp towers) fire from 9020 KefkaHelper proxies. The Black Hole mechanic
// (4 sets / 10 waves of passable tethers) is driven by UmadP3EqBlackHoles. Scope is a layer-1
// visual replay + the enrage wipe; damage/death resolution for the other mechanics and dodge AI
// are layer-2 TODOs.
public sealed class UmadP3EqScenario : IScenario
{
    public string Name => "UMAD P3 Earthquake";

    // TerritoryId 1363 = the Dancing Mad instance; Origin (100,0,100) is the arena centre
    // (Splatoon's P3 black-hole script keys off the same point). WeatherId 174 matches the
    // adjacent P4 Kefka Says scenario.
    public TargetInstance TargetInstance { get; } = new(
        TerritoryId: 1363,
        Origin: new Vector3(100f, 0f, 100f),
        PlayerPosition: new Vector3(100f, 0f, 116f),
        WeatherId: 174);

    public IReadOnlyList<Waymark> Waymarks { get; } = UmadConstants.UmadWaymarks;
    public ushort Bgm => 533;

    // Same arena (TerritoryId 1363) as P2 Forsaken / P4 Kefka Says: drop the duty's
    // BG spawn-ring collider ~10 units north of centre, which a real pull clears on
    // Commence but our fake instance must disable itself.
    public IReadOnlyList<Vector3> ColliderRemovalPoints => [new(0, 0, -10)];

    public void DrawSettings() => settingsWindow.Draw();
    private readonly UmadP3EqSettingsWindow settingsWindow = new();

    public IReadOnlyList<IScenarioAi> AiStrats => [new UmadP3EqAi()];

    // Arena half-width; the Look-upon-Me line proxy sits this far out so its line sweeps the full
    // diameter through centre. Towers sit half this far out (~"halfway to the edge").
    private const float ArenaRadius = 20f;
    private const float TowerDistance = 10f;
    // The two opposite arms of an Implosion shockwave sit this far off centre so their VFX don't collapse.
    private const float ImplosionArmOffset = 2f;

    private UmadP3EqState state = null!;
    private UmadP3EqBlackHoles blackHoles = null!;
    private SimWorld world = null!;
    private SimParty party = null!;

    private SimEnemy? kefka;
    private SimEnemy? chaos;
    private SimEnemy? exdeath;

    // Chaos's facing captured the instant the Implosion cast starts, reused for BOTH cross waves so
    // the cross stays locked to that heading — whatever it is (e.g. left over from a Damning Edict) —
    // with no mid-mechanic re-orient or snap-back. Set at the Implosion timeline entry.
    private float implosionLockedRotation;

    // Implosion shockwave timing — same cast-bar anchoring as Stomp/Slap/Thunder. The two perpendicular
    // cross sweeps are armed when Chaos's BAFD/BAFE cast starts and fired off its bar completion (Tick),
    // not the scheduler, so they stay locked to the Implosion resolve instead of drifting late off the
    // engine-driven cast bar. Offsets are seconds after the bar completes (log: ~0.73s then ~2.75s).
    private bool implosionPending;
    private const float ImplosionWave1Delay = 0.73f;
    private const float ImplosionWave2Delay = 2.75f;

    // Thunder III tank-buster pair timing. The two hits are anchored to the cast BAR's real-time
    // completion (watched in Tick) instead of the EventScheduler, which is scaled by EventTimeScale —
    // otherwise, at any speed > 1, the hits drift early and detach from the bar. See StartThunderPair.
    private bool thunderPending;              // a Thunder III bar is up; fire hit 1 when it finishes
    private float thunderHit2Delay;           // >0 = hit 2 pending, real seconds remaining
    private const float ThunderHitGap = 3.0f; // log gap between the two back-to-back tank hits

    // Slap Happy resolve timing. Like Thunder, the resolve (center AoE + half-room cleave + cones)
    // is anchored to Kefka's cast BAR completion via Tick — not the scaled EventScheduler — so the
    // whole thing stays glued to the bar at any EventTimeScale. slapPending is the armed instance;
    // `deferred` holds the post-completion offsets (center, then cleave + cones) in real seconds.
    private SlapHappyInstance? slapPending;
    private readonly List<(float Remaining, Action Run)> deferred = new();
    private const float SlapCenterDelay = 2.16f; // completion → BAE9 center circle
    private const float SlapCleaveDelay = 3.60f; // completion → BAEB cleave + cones
    // Role-stack cones fire from 3 proxies in the same frame; identical positions collapse their VFX
    // into one, so each cone's source is nudged out along its own heading by Base + index·Step. Kefka's
    // hitbox spans the inner arena, so an offset this small still reads as emanating from him.
    private const float ConeSourceBase = 2f;
    private const float ConeSourceStep = 2f;

    // Stomp-a-Mole resolve timing. Same reason as Thunder/Slap Happy: the four towers + the return-to-
    // idle stance are anchored to Kefka's BAEF cast BAR completion (watched in Tick), NOT the
    // EventScheduler. The scheduler runs on Dalamud's frame delta (truncated to whole ms), while the
    // cast bar advances on the engine's own clock — so scheduler-timed towers drift late off the stomp
    // even at 1x. stompPending is the armed flag; the offsets are seconds after the bar completes
    // (log: towers at +0.22/+1.50/+2.80/+4.13, idle stance at +1.57).
    private bool stompPending;
    private static readonly float[] StompTowerDelays = { 0.22f, 1.50f, 2.80f, 4.13f };
    private const float StompIdleDelay = 1.57f;

    // Log-measured delay from a Primordial Crust cleanse (a player's 3rd Black Hole hit) to its BAFA
    // aftershock around Chaos — rock-steady at ~1.07s across every cleanse in every pull, never same-frame.
    private const float AftershockCleanseDelay = 1.07f;

    // How long the ground omen (telegraph) is visible before a cast resolves. The boss casts below run
    // their full (log-accurate) cast bar, but the omen should only flash the final ~1s before the hit —
    // so each is given omenDelay = castLength − TelegraphWindow (the omen appears that far into the cast).
    private const float TelegraphWindow = 1.0f;

    public void Run(SimWorld worldParam, int? selectedAi)
    {
        // Seed UMAD action/status names into the game's RSV table so cast bars (boss + enemy-list
        // cast text) render real names instead of the raw "_rsv_#####" placeholders. Inn-only runs
        // never receive the server's RSV packets, so we write the mappings ourselves. Cheap and
        // idempotent — every UMAD scenario does this at the top of Run.
        UmadRsvStrings.Seed();
        world = worldParam;
        party = worldParam.Party;
        state = new UmadP3EqState(party, settingsWindow.Overrides);
        // Cleansing Primordial Crust (a player's 3rd Black Hole hit) bursts an un-casted aftershock
        // (BAFA) around Chaos — the same shockwave as the timed EQ aftershocks, just keyed to the cleanse
        // instead of the clock, and emanating from Chaos rather than centre. The log puts the burst a fixed
        // ~1.07s AFTER the cleanse, so schedule it (scaled with EventTimeScale like the rest of the timeline).
        blackHoles = new UmadP3EqBlackHoles(world, party, state,
            // Crust cleanse → aftershock around Chaos (~1.07s later).
            () => world.Events.Add(AftershockCleanseDelay, () =>
            {
                if (chaos is { } c)
                    ProxyCast(c.Position, 0f, ActionId.AccretionQuake, lifetime: 3f, castSeconds: 0f);
            }));

        // Reset real-time resolve state (scenario is a reused singleton; a mid-run reset could leave
        // these armed and fire a stray hit/resolve at the start of the next run).
        thunderPending = false;
        thunderHit2Delay = 0f;
        slapPending = null;
        stompPending = false;
        implosionPending = false;
        deferred.Clear();

        if (selectedAi is { } idx && idx < AiStrats.Count)
            ((IScenarioAi<UmadP3EqState>)AiStrats[idx]).Run(state, world);

        // ============================ Timeline ============================
        // Sorted by absolute scenario time (= note EQ-relative offset + 3.57). Earthquake cast = t3.57.
        // Opening: normal-size Kefka casts Max, teleports OUT (vanishes), transforms while hidden,
        // then the giant rises in via the entrance right as Earthquake resolves (~t8.6) — the warp-out
        // hides the model reload, so there's no transform flicker.
        world.Events.Add(0.1f, SpawnBosses);                               // Chaos + Exdeath + normal-size Kefka (pre-transform)
        world.Events.Add(0.5f, () => kefka?.Cast(ActionId.Max));           // BAE5 — last pre-EQ Kefka cast; ends ~t5.2
        world.Events.Add(3.57f, () => chaos?.Cast(ActionId.Earthquake, castSeconds: 4.7f)); // C571 cast bar (forced 4.7s) → resolves ~t8.27
        world.Events.Add(6.97f, () => kefka?.PlayActionTimeline(UmadConstants.TimelineId.WarpOut)); // normal warp-out — vanish
        world.Events.Add(7.17f, () => kefka?.SetVisible(false));            // stay hidden through the reload
        world.Events.Add(7.27f, TransformToGiant);                         // giant form swap (model reload) while vanished
        world.Events.Add(7.27f, () => kefka?.SetPosition(new Placement(Vector3.Zero, 0f))); // recentre while hidden so the giant rises from centre (log: pos reset to (100,100,0) at the transform)
        world.Events.Add(8.47f, () => kefka?.SetModelState(0x05));          // big-idle stance, set while hidden
        world.Events.Add(8.57f, () => blackHoles.ApplyDebuffs());           // EQ resolves (+4.99)
        world.Events.Add(8.57f, () => chaos?.Cast(ActionId.EarthquakeAoe, castSeconds: 0f)); // C572 instant raidwide resolve (HP-drop VFX)
        world.Events.Add(8.57f, () =>                                       // giant rises in as EQ resolves (+5.08)
        {
            kefka?.SetVisible(true);
            kefka?.PlayActionTimeline(TimelineId.GiantEntrance);
        });

        // T1
        world.Events.Add(14.80f, () => kefka?.PlayActionTimeline(TimelineId.WarpOut));
        world.Events.Add(15.20f, () => ProxyCast(Vector3.Zero, 0f, ActionId.AccretionQuake, lifetime: 3f, castSeconds: 0f)); // BAFA aftershock 1
        world.Events.Add(16.93f, () => WarpGiantIn(0));
        world.Events.Add(19.07f, () => StartSlapHappy(0));                    // Slap Happy 1 — variant cast; center AoE + cleave + cones fire off its real-time completion (Tick)
        world.Events.Add(19.17f, () => ProxyCast(Vector3.Zero, 0f, ActionId.AccretionQuake, lifetime: 3f, castSeconds: 0f)); // BAFA aftershock 2 (~EQ+15.6; pulls 2/27 + VoD, not pull-4's outlier +18.03)

        // --- Black Hole set 1 (1 then 2 tethers; solo = CW-predecessor of absent outer) ---
        // BAFB is a self-targeted 2.7s trigger cast (log: "starts casting Black Hole[BAFB] (2.7s) -> self",
        // CastDurationMax=2.7000). Pass castSeconds explicitly — the Action sheet's Cast100ms is 0 for this
        // trigger, so without it the cast fires instantly with no bar and no casting animation.
        world.Events.Add(24.77f, () => exdeath?.Cast(ActionId.BlackHole, castSeconds: 2.7f));   // BAFB, 2.7s
        world.Events.Add(27.98f, () => blackHoles.SpawnSet(0));
        world.Events.Add(29.54f, () => blackHoles.TetherSolo());             // wave 1
        world.Events.Add(34.88f, () => blackHoles.ResolveHits(clearTethers: true)); // tether gone at lock-on
        world.Events.Add(35.57f, () => blackHoles.TetherPair());             // wave 2 (re-sources from the other holes)
        world.Events.Add(37.21f, () => blackHoles.DespawnSolo());            // solo hole vanishes mid-set (log +33.64); pair stays
        world.Events.Add(40.18f, StartThunderPair);                          // Thunder III buster pair 2 — cast bar; hits fire off its real-time completion (Tick)
        world.Events.Add(41.96f, () => blackHoles.ResolveHits(clearTethers: true)); // tether gone at lock-on
        world.Events.Add(44.36f, () => blackHoles.DespawnSet());

        // T2
        world.Events.Add(45.25f, () => kefka?.PlayActionTimeline(TimelineId.WarpOut));
        world.Events.Add(47.39f, () => WarpGiantIn(1));
        world.Events.Add(48.14f, () => DamningEdict(state.DamningEdictTargets[0]));   // BB01 — 180° cleave aimed at a random player
        world.Events.Add(49.52f, () => StartSlapHappy(1));                    // Slap Happy 2 — variant cast; resolve fires off its completion (Tick)

        // --- Black Hole set 2 (3 tethers, persistent across the 3 hits) ---
        world.Events.Add(58.37f, () => blackHoles.SpawnSet(1));
        world.Events.Add(58.57f, () => blackHoles.TetherAllThree());
        world.Events.Add(65.41f, () => blackHoles.ResolveHits());            // wave 1
        world.Events.Add(70.50f, () => blackHoles.ResolveHits());            // wave 2

        // T3
        world.Events.Add(72.68f, () => kefka?.PlayActionTimeline(TimelineId.WarpOut));
        world.Events.Add(74.81f, () => WarpGiantIn(2));
        world.Events.Add(75.30f, () => DamningEdict(state.DamningEdictTargets[1]));   // BB01 — 180° cleave aimed at a random player
        world.Events.Add(75.57f, () => blackHoles.ResolveHits(clearTethers: true)); // set 2 wave 3 (final) — tether gone at lock-on
        world.Events.Add(76.94f, () => kefka?.Cast(ActionId.LookUponMeAndDespairCastA, castSeconds: 3.7f, omenDelay: 3.7f - TelegraphWindow)); // BAEC (cast 3.7s, omen last ~1s)
        world.Events.Add(76.99f, () => LineCleave(2));                       // BAEE line through centre (facing T3)
        world.Events.Add(78.06f, () => blackHoles.DespawnSet());            // set 2
        world.Events.Add(81.40f, StartThunderPair);                          // Thunder III buster pair 3 — cast bar; hits fire off its real-time completion (Tick)
        world.Events.Add(82.07f, () => kefka?.SetModelState(0x07));          // look-at stance
        world.Events.Add(84.07f, () => kefka?.Cast(ActionId.LookUponMeVfxA)); // C4BA
        world.Events.Add(84.87f, () => kefka?.SetModelState(0x05));          // back to idle

        // --- Black Hole set 3 (3 tethers, persistent) ---
        world.Events.Add(92.87f, () => blackHoles.SpawnSet(2));
        world.Events.Add(93.07f, () => blackHoles.TetherAllThree());
        world.Events.Add(99.66f, () => blackHoles.ResolveHits());            // wave 1
        world.Events.Add(104.73f, () => blackHoles.ResolveHits());           // wave 2

        // T4 (long gap while BH waves resolve)
        world.Events.Add(109.19f, () => kefka?.PlayActionTimeline(TimelineId.WarpOut));
        world.Events.Add(109.81f, () => blackHoles.ResolveHits(clearTethers: true)); // set 3 wave 3 (final) — tether gone at lock-on
        world.Events.Add(111.32f, () => WarpGiantIn(3));
        world.Events.Add(112.09f, () => blackHoles.DespawnSet());           // set 3

        // --- Slap Happy 3 + White Hole + Implosion (~simultaneous) ---
        world.Events.Add(115.90f, () => exdeath?.Cast(ActionId.WhiteHole));  // BD66
        world.Events.Add(115.90f, () =>
        {
            // Lock the cross to Chaos's facing AT CAST START — no snap to any "home" heading. Whatever
            // direction he's left pointing (e.g. from the prior Damning Edict) is captured here and
            // reused for both waves, so the cross never re-orients while the mechanic resolves. The two
            // shockwave sweeps are anchored to the bar completion in Tick (see implosionPending).
            implosionLockedRotation = chaos?.Rotation ?? 0f;
            chaos?.Cast(state.ImplosionLatitudinal                          // BAFD/BAFE cast (4.7s) — resolves ~120.60
                ? ActionId.LatitudinalImplosion : ActionId.LongitudinalImplosion, castSeconds: 4.7f);
            implosionPending = true;
        });
        world.Events.Add(117.46f, () => StartSlapHappy(2));                   // Slap Happy 3 — variant cast; resolve fires off its completion (Tick)

        // --- Black Hole set 4 (2 then 1 tether; solo = CW-predecessor of absent outer) ---
        world.Events.Add(126.13f, () => blackHoles.SpawnSet(3));
        world.Events.Add(126.53f, () => blackHoles.TetherPair());            // wave 1

        // T5
        world.Events.Add(130.58f, () => kefka?.PlayActionTimeline(TimelineId.WarpOut));
        world.Events.Add(132.71f, () => WarpGiantIn(4));
        world.Events.Add(133.11f, () => blackHoles.ResolveHits(clearTethers: true)); // set 4 wave 1 — tether gone at lock-on
        world.Events.Add(134.54f, () => blackHoles.TetherSolo());            // wave 2 (re-sources from the solo hole)
        world.Events.Add(134.85f, () => kefka?.Cast(ActionId.LookUponMeAndDespairCastB, castSeconds: 3.7f, omenDelay: 3.7f - TelegraphWindow)); // BAED (cast 3.7s, omen last ~1s)
        world.Events.Add(134.90f, () => LineCleave(4));                      // BAEE line through centre (facing T5)
        world.Events.Add(135.29f, () => blackHoles.DespawnPair());           // pair holes vanish mid-set (log +131.72); solo stays
        world.Events.Add(140.17f, () => blackHoles.ResolveHits(clearTethers: true)); // set 4 wave 2 (final) — tether gone at lock-on (EQ+136.60, resolved n=2)
        world.Events.Add(139.98f, () => kefka?.SetModelState(0x07));         // look-at stance
        world.Events.Add(141.98f, () => kefka?.Cast(ActionId.LookUponMeVfxB)); // C533
        world.Events.Add(142.51f, () => blackHoles.DespawnSet());           // set 4
        world.Events.Add(142.78f, () => kefka?.SetModelState(0x06));         // pre-Stomp stance (held through BAEF)

        // --- Blizzard III / Stomp-a-Mole / Knock Down / Big Bang ---
        // Blizzard III markers: BB0F trigger (2.7s) → on resolve TWO back-to-back waves of 8
        // player-spread AoEs (BB0D, 2.7s each) under every player; wave 2 drops as wave 1 resolves.
        // Stomp-a-Mole: BAEF cast (4.7s) → FOUR towers (BAF0, 1.2s each), staggered ~1.3s and
        // alternating across the two slots ⊥ to Kefka's facing (NOT two simultaneous towers).
        // Knock Down: Chaos BB02 (4.7s) with one stack head-marker on a single random player.
        // All times rebased from the pull-4 extended combat log.
        world.Events.Add(146.12f, () => exdeath?.Cast(ActionId.BlizzardIIICast, castSeconds: 2.7f)); // BB0F trigger
        world.Events.Add(148.07f, StartStomp);                                   // BAEF, 4.7s — towers + idle anchored to the bar (Tick)
        world.Events.Add(149.26f, BlizzardMarkers);                               // BB0D wave 1 (×8 spread)
        world.Events.Add(149.59f, () => chaos?.Cast(ActionId.KnockDown1, castSeconds: 4.7f));        // BB02, 4.7s
        world.Events.Add(149.59f, () => party.Get(state.KnockDownStackTarget)?    // stack head-marker on 1 random player
            .AttachLockonVfx(LockonId.Stack, duration: 5.1f));
        world.Events.Add(152.28f, BlizzardMarkers);                              // BB0D wave 2 (×8 spread)
        world.Events.Add(159.60f, () => exdeath?.Cast(ActionId.BlizzardIII, castSeconds: 3.7f));     // BB11 freeze
        world.Events.Add(159.73f, () => chaos?.Cast(ActionId.BigBang, castSeconds: 4.7f));           // BB05

        // --- Enrage (cactbot ~840.7s) — Meteor, then the raid wipes. Meteor uses the 30-pull avg
        // EQ+168.88 (the old EQ+165.14 was the single shortest pull); wipe follows the 9.7s cast. ---
        world.Events.Add(172.45f, () => exdeath?.Cast(ActionId.MeteorExdeathEnrage)); // C61E, 9.7s
        world.Events.Add(182.15f, () => party.WipeAllPlayers("UMAD P3 Earthquake enrage"));
    }

    // Real-time (unscaled) per-frame hook. Keeps cast-bar-anchored resolves glued to their bars
    // regardless of EventTimeScale (the scheduler is scaled; cast bars and this Tick are not).
    public void Tick(float delta, float elapsed)
    {
        // --- Black Hole contact punish: zap players standing in a real hole (BCCD + Damage Down, ~1.1s pulse). ---
        blackHoles.Tick(delta);

        // --- Thunder III: hits anchored to Exdeath's bar (see StartThunderPair). ---
        if (thunderPending && exdeath is { IsCasting: false })
        {
            thunderPending = false;
            ThunderHit();                       // hit 1 — closest to Exdeath now
            thunderHit2Delay = ThunderHitGap;   // arm hit 2
        }
        if (thunderHit2Delay > 0f)
        {
            thunderHit2Delay -= delta;
            if (thunderHit2Delay <= 0f)
            {
                thunderHit2Delay = 0f;
                ThunderHit();                   // hit 2 — recomputed closest
            }
        }

        // --- Slap Happy: on Kefka's bar completion, queue the resolve as real-time offsets so the
        //     center AoE, half-room cleave, and role/party-stack cones stay aligned with the bar. ---
        if (slapPending is { } slap && kefka is { IsCasting: false })
        {
            slapPending = null;
            Defer(SlapCenterDelay, () => kefka?.Cast(ActionId.SlapHappyFinal, castSeconds: 1.2f, omenDelay: 1.2f - TelegraphWindow)); // BAE9 center circle (cast 1.2s, omen last ~1s)
            Defer(SlapCleaveDelay, () =>
            {
                // The cones ARE the cleave attack — one cleave-cone per target (3 roles, or 1 random
                // member). There is no separate boss-facing half-room cleave: that double-counted as a
                // second cleave (the party stack showed 2 on 2 targets instead of 1). Shape per variant:
                // BAEB for the role-stack hand (BAE7), BAEA for the party-stack hand (BAE6).
                var coneAction = slap.RoleStack ? ActionId.Shockwave : ActionId.ShockingImpact;
                for (var c = 0; c < slap.ConeTargets.Count; c++)
                    FireCone(slap.ConeTargets[c], c, coneAction);
            });
        }

        // --- Stomp-a-Mole: on Kefka's BAEF bar completion, queue the 4 alternating towers + the
        //     return-to-idle stance as real-time offsets so they stay glued to the stomp animation
        //     (scheduler-timed towers otherwise drift late off the engine-driven cast bar). ---
        if (stompPending && kefka is { IsCasting: false })
        {
            stompPending = false;
            for (var t = 0; t < StompTowerDelays.Length; t++)
            {
                var index = t; // capture per-iteration for the closure
                Defer(StompTowerDelays[t], () => StompTower(index));
            }
            Defer(StompIdleDelay, () => kefka?.SetModelState(0x05)); // back to idle, mid-tower (log)
        }

        // --- Implosion: on Chaos's BAFD/BAFE bar completion, queue the two perpendicular shockwave
        //     crosses (BAFF) as real-time offsets so they stay locked to the Implosion resolve. Axes
        //     are relative to Chaos's facing captured at cast start (implosionLockedRotation):
        //     Longitudinal = Front/back first then Sides; Latitudinal = Sides first then Front/back. ---
        if (implosionPending && chaos is { IsCasting: false })
        {
            implosionPending = false;
            Defer(ImplosionWave1Delay, () => ImplosionWave(sides: state.ImplosionLatitudinal));   // wave 1
            Defer(ImplosionWave2Delay, () => ImplosionWave(sides: !state.ImplosionLatitudinal));  // wave 2 (⊥)
        }

        // --- Drain the real-time deferred queue (iterate back-to-front so removals are safe). ---
        for (var i = deferred.Count - 1; i >= 0; i--)
        {
            var remaining = deferred[i].Remaining - delta;
            if (remaining <= 0f)
            {
                var run = deferred[i].Run;
                deferred.RemoveAt(i);
                run();
            }
            else
            {
                deferred[i] = (remaining, deferred[i].Run);
            }
        }
    }

    private void Defer(float seconds, Action run) => deferred.Add((seconds, run));

    // Warp the giant back in facing the i-th per-run random heading. Position stays the logical centre
    // (the warp-in animation places the visible model at the perimeter); only the facing changes.
    private void WarpGiantIn(int teleportIndex)
    {
        kefka?.SetPosition(new Placement(Vector3.Zero, state.GiantHeadings[teleportIndex]));
        kefka?.PlayActionTimeline(TimelineId.WarpIn);
    }

    // Begins a Thunder III tank-buster pair: the BB09 (ThunderIIISync) cast bar on Exdeath plus the
    // flag Tick watches to fire the two hits. BB09 is release-silent on our doppels, so the bar
    // itself draws no hit — the two BB0C hits below are the only Thunder III VFX shown.
    private void StartThunderPair()
    {
        exdeath?.Cast(ActionId.ThunderIIISync, castSeconds: 4.7f);
        thunderPending = true;
    }

    // One Thunder III tank-buster hit: an instant BB0C (the real ThunderIII) striking whichever
    // player is closest to Exdeath at this instant. Invoked from Tick (real time) so each hit lands
    // locked to the cast bar — hit 1 the frame the bar finishes, hit 2 a fixed gap later — and the
    // closest target is recomputed per call, so the two hits can land on different players as they move.
    private void ThunderHit()
    {
        if (exdeath is not { } exd) return;
        if (party.Find.Closest(exd.Position) is not { } target) return;
        exd.Cast(ActionId.ThunderIII, targetLocation: target.Position,
            castSeconds: 0f, targetId: target.GameObjectId);
    }

    // Begins a Slap Happy: casts the hand variant (cast bar + hand-up animation) and arms the resolve
    // that Tick fires off the bar's real-time completion (center circle, then half-room cleave + cones).
    // Confirmed in-game: Role Stack = BAE7, Party Stack = BAE6 (the role stack's 3 cones go with the
    // BAE7 hand-up). Each cast keeps its matching cleave in the resolve below (BAE7↔BAEB, BAE6↔BAEA),
    // so the cleave side stays on the raised hand. Cone count follows the variant: 3 for role, 1 for party.
    private void StartSlapHappy(int index)
    {
        var slap = state.SlapHappy[index];
        kefka?.Cast(slap.RoleStack ? ActionId.SlapHappyCastB : ActionId.SlapHappyCastA);
        slapPending = slap;
    }

    // Begins Stomp-a-Mole: casts BAEF (4.7s cast bar + pre-stomp stance) and arms the tower resolve
    // that Tick fires off the bar's real-time completion (4 alternating towers + the return-to-idle
    // stance), so the towers stay locked to the stomp instead of drifting on the scheduler clock.
    private void StartStomp()
    {
        kefka?.Cast(ActionId.StompAMoleCast, castSeconds: 4.7f);
        stompPending = true;
    }

    // Spawn one Slap Happy cone aimed at `role`'s current position. A 9020 proxy facing the target
    // instant-casts `coneAction` — the variant's own swing shape (BAEB role / BAEA party), so each
    // stack's cones match its cleave instead of always using the wider role shape. A directional
    // action is required because BAE8 ("sub-hit", paired with the BAE9 center circle) ignores facing
    // and never aims. The proxy sits a short, per-cone-distinct distance off centre along its heading
    // (coneIndex) so the simultaneous role-stack cones don't share a position and collapse into one.
    private void FireCone(PartyRole role, int coneIndex, uint coneAction)
    {
        if (party.Get(role) is not { } target) return;
        var pos = target.Position;                                  // scenario-local; centre = Vector3.Zero
        var heading = MathF.Atan2(pos.X, pos.Z);                    // face centre → target
        var facing = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
        var origin = (ConeSourceBase + coneIndex * ConeSourceStep) * facing;
        ProxyCast(origin, heading, coneAction, lifetime: 3f, castSeconds: 0f);
    }

    // Spawn a 9020 KefkaHelper proxy that fires `actionId` — its real omen + release VFX auto-draw
    // from the action sheet, oriented by the proxy's facing — then despawns. Mirrors TopP5Delta's
    // helper-proxy pattern. `local` is scenario-local; `lifetime` is seconds from now.
    private void ProxyCast(Vector3 local, float rotation, uint actionId, float lifetime = 4f, float? castSeconds = null, float omenDelay = 0f)
    {
        var helper = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: UmadConstants.BNpcBaseId.KefkaHelper, // 9020 visual-delivery pool
            Targetable: false,
            EnemyList: EnemyListMode.Never,
            Placement: new Placement(local, rotation)));
        if (helper == null) return;
        helper.Cast(actionId, castSeconds: castSeconds, omenDelay: omenDelay);
        world.Events.Add(lifetime, helper.Despawn);
    }

    // Damning Edict (BB01): a 180° frontal cleave from Chaos aimed at a random player, with the
    // facing LOCKED at cast start (log). Turn Chaos to face the target before casting so the cleave
    // omen orients toward them; no targetLocation is passed, so the release stays on that locked
    // heading even if the player moves during the cast. A dead/absent target leaves his facing as-is.
    private void DamningEdict(PartyRole target)
    {
        if (chaos is not { } c) return;
        if (party.Get(target) is { } member)
        {
            var d = member.Position - c.Position;
            if (d.X * d.X + d.Z * d.Z > 1e-6f)
                c.SetRotation(MathF.Atan2(d.X, d.Z)); // 0 = south, +Z; matches the engine's facing convention
        }
        c.Cast(ActionId.DamningEdict, castSeconds: 4.7f, omenDelay: 4.7f - TelegraphWindow);
    }

    // Look upon Me line cleave (BAEE): edge-to-edge through centre along Kefka's facing for the
    // given teleport. The proxy sits on the far edge (opposite the facing) so the line sweeps the
    // full diameter through the middle.
    private void LineCleave(int headingIndex)
    {
        var heading = state.GiantHeadings[headingIndex];
        // BAEE line is a 4.7s cast (log); show the line omen only the last ~1s before it resolves.
        ProxyCast(-ArenaRadius * Facing(heading), heading, ActionId.LookUponMeAndDespair, lifetime: 6f,
            castSeconds: 4.7f, omenDelay: 4.7f - TelegraphWindow);
    }

    // Implosion resolve wave (BAFF "Shockwave"): a line sweep centered on CHAOS's position, oriented
    // relative to his facing locked at the Implosion cast start (implosionLockedRotation). `sides:false`
    // = Front/back (along that facing); `sides:true` = Sides (perpendicular). Mirrors the log's paired
    // events — two proxies just off Chaos on opposite sides, each facing outward — so the shockwave
    // reads as collapsing through him both ways. (Both proxies must sit at slightly different positions
    // or their VFX collapse into one.)
    private void ImplosionWave(bool sides)
    {
        if (chaos is not { } c) return;
        var axis = implosionLockedRotation + (sides ? MathF.PI / 2f : 0f); // locked Chaos facing, +90° for the Sides axis
        var dir = Facing(axis);
        ProxyCast(c.Position + ImplosionArmOffset * dir, axis, ActionId.ImplosionShockwave, lifetime: 4f, castSeconds: 0f);
        ProxyCast(c.Position - ImplosionArmOffset * dir, axis + MathF.PI, ActionId.ImplosionShockwave, lifetime: 4f, castSeconds: 0f);
    }

    // One Stomp-a-Mole tower (BAF0, 1.2s): a 9020 proxy fills a stack circle halfway out from centre
    // (TowerDistance) on the axis ⊥ to Kefka's facing (T5). The FIRST tower is always the slot 90°
    // CLOCKWISE of his looking direction; the four then alternate CW / CCW / CW / CCW (log: pull4,
    // Kefka facing west → first tower north = 90° CW).
    private void StompTower(int index)
    {
        var heading = state.GiantHeadings[4];
        // Facing vector is (sin h, cos h); rotating it 90° clockwise (top-down, N up) gives (-cos h, sin h).
        var clockwise = new Vector3(-MathF.Cos(heading), 0f, MathF.Sin(heading));
        var sign = index % 2 == 0 ? 1f : -1f;          // tower 0 = CW side, then alternate to CCW and back
        ProxyCast(sign * clockwise * TowerDistance, 0f, ActionId.StompAMole, castSeconds: 1.2f, omenDelay: 1.2f - TelegraphWindow);
    }

    // Blizzard III spread markers (BB0D, 2.7s): one player-targeted ground AOE dropped under every
    // ALIVE player at its CURRENT position. Called twice (two back-to-back waves) off the BB0F
    // trigger; each wave locks a fresh telegraph wherever the players stand at that instant.
    //
    // omenDelay 0 (NOT the scenario's boss-cast "flash the last ~1s" convention): a spread marker
    // must show on the ground the moment it LOCKS and stay there the full 2.7s, so the player can
    // walk out of the locked circle before it detonates. The mechanic is: lock at position → players
    // move (telegraph stays put) → AOE fires → wave 2 locks at the new positions → repeat.
    private void BlizzardMarkers()
    {
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            if (party.Get(role) is not { } member || !member.IsAlive()) continue;
            ProxyCast(member.Position, 0f, ActionId.BlizzardIIIFirst, castSeconds: 2.7f, omenDelay: 0f);
        }
    }

    // Facing direction for an absolute rotation (0 = south/+Z, π/2 = east/+X, π = north/−Z).
    private static Vector3 Facing(float rotation) => new(MathF.Sin(rotation), 0f, MathF.Cos(rotation));

    private void SpawnBosses()
    {
        // Chaos + Exdeath are the on-arena targetable bosses (they drive the casts/mechanics).
        chaos = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.Chaos,
            NameId: UmadConstants.BNpcNameId.Chaos,
            Level: 100,
            Targetable: true,
            EnemyList: EnemyListMode.Always,
            IsVisible: true,
            Placement: new Placement(new Vector3(-15f, 0f, 0f), MathF.PI / 2f)));          // pos?

        exdeath = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.Exdeath,
            NameId: UmadConstants.BNpcNameId.Exdeath,
            Level: 100,
            Targetable: true,
            EnemyList: EnemyListMode.Always,
            IsVisible: true,
            Placement: new Placement(new Vector3(15f, 0f, 0f), -MathF.PI / 2f)));          // pos?

        // Kefka = the P3 god-form (BNpcBase 19504). He spawns NORMAL-SIZE here (bare 19504) and casts
        // Max pre-EQ; the giant transform is applied on the timeline (TransformToGiant at t5.6) so the
        // looming giant rises in via the entrance (~t7) instead of being giant from frame 1. He spawns at
        // his Max-cast spot — 10 units NORTH of centre, local (0,0,-10), the same point as the collider
        // removal (pull-4 log: BAE5 cast at world (100,90,0)). He is recentred to Vector3.Zero while hidden
        // (t7.27, just before the giant entrance) so the entrance still rises from centre — exactly as the
        // log does (it resets pos to (100,100,0) at the transform, before PlayActionTimeline 11D4). The
        // entrance animation displaces the visible model to the perimeter on its own, so the recentred
        // logical position must stay at the centre and must NOT be pre-offset, or it double-displaces.
        kefka = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.BigKefka,                    // 19504 (bare = normal-size)
            NameId: UmadConstants.BNpcNameId.Kefka,
            Level: 100,
            Targetable: false,                                  // god-Kefka is never clickable…
            EnemyList: EnemyListMode.Always,                    // …but is listed in the enemy list like the real fight.
                                                                // Always (not OnlyWhenVisible) because he transforms via
                                                                // SetModelState, whose rebuild would flap an IsVisible-gated row.
            IsVisible: true,
            Placement: new Placement(new Vector3(0f, 0f, -10f), 0f))); // Max-cast spot: 10 north of centre (log: world (100,90,0))
        kefka?.PlayActionTimeline(UmadConstants.TimelineId.Spawn); // normal (non-giant) warp-in, pre-EQ
    }

    // Apply the giant god-Kefka transform. The status Param flows to the engine as a TransformationId
    // via Statuses.AddStatusInit (the path TOP uses for OmegaM/OmegaF) — 0x1FA = 506 = the giant model
    // (radius 6.0 -> 22.5). Triggers an async model reload — fired during the warp-out vanish (t5.7)
    // so the reload is hidden, then reveal + entrance at t7.0. Long duration so it persists.
    private void TransformToGiant()
    {
        kefka?.AddStatus(StatusId.KefkaFormSwap, duration: 9999f,
            stacks: StatusId.GiantKefkaForm, overrideStacks: true);
    }
}
