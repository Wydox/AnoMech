namespace AnoMech.Scenarios.Umad.P3Eq;

// IDs scoped to the UMAD P3 *Earthquake* segment only (Chaos's Earthquake → P3 enrage,
// cactbot dancing_mad.txt ~671.8s→840.8s). Deliberately NOT folded into the shared
// UmadConstants, since these are specific to this 2nd-half-of-P3 scenario. Family-wide
// values (Kefka/Chaos BNpcNameId, waymarks, territory) stay in UmadConstants and are
// referenced fully-qualified from the scenario.
//
// Sources: action ids cross-validated between the cactbot timeline (hex labels) and the
// Splatoon "Dawntrail/Dancing Mad/P3_Earthquake" script (decimal). Comments give the
// cactbot label + decimal value.
public static class UmadP3EqConstants
{
    // BNpcBase rows for actors in this segment, from the Splatoon "Dancing Mad" scripts
    // (decimal). These intentionally use the Splatoon/cactbot rows, which differ from the
    // shared UmadConstants rows (Kefka 18475 / Chaos 19507).
    public static class BNpcBaseId
    {
        public const uint Kefka        = 19451; // Splatoon KefkaDataId — invisible P3 ANCHOR (Splatoon reads
                                                // its position only; draws no body). NOT the visible Kefka.

        // The GIANT P3 god-Kefka the player tracks (position + L/R-hand tells + stomp). CONFIRMED from the
        // P3 ACT log: BNpcBase 19504 (== FinalDondoko), the actor that casts Trance (C2D6) / Slap Happy
        // (BAE6/BAE7) / Look upon Me (BAEC/BAED) / Stomp-a-Mole (BAEF). Spawning it bare looks normal-sized
        // — the GIANT is the TRANSFORMED state: applying status 0x9E8 with Param 0x1FA makes the engine
        // derive TransformationId 506 and swap to the giant model (radius 6.0 -> 22.5). See SpawnBosses +
        // StatusId.KefkaFormSwap / GiantKefkaForm. (Earlier guesses 19506/2000045/layout were all wrong.)
        public const uint BigKefka     = 19504;
        public const uint Chaos        = 19508; // Splatoon ChaosDataId
        public const uint Exdeath      = 19509; // Splatoon ExdeathDataId (P3_Bowels_of_Agony_TLB)
        public const uint BlackHole    = 19512; // Splatoon BlackHoleDataId
        public const uint FinalDondoko = 19504; // Splatoon FinalDondokoDataId (== BigKefka, the giant P3 Kefka)
    }

    // Event-object (EObj sheet) rows. NOTE: 2000045 was a giant-Kefka candidate but the ObjectTable dump
    // proved it's "Moist Depression" (a terrain object), and the giant Kefka turned out to be a transformed
    // BattleChara (BNpcBaseId.BigKefka), not an EObj. Kept only as a documented dead end.
    public static class EObjId
    {
        public const uint MoistDepression = 2000045; // confirmed terrain object — NOT Kefka
    }

    // Action ids (hex). Source actor noted per line (Cha = Chaos, Exd = Exdeath, Kef = Kefka).
    public static class ActionId
    {
        public const uint Earthquake               = 0xC571U; // Cha — phase opener      (50545)
        public const uint EarthquakeAoe            = 0xC572U; // Cha — raidwide resolve  (50546)
        public const uint AccretionQuake           = 0xBAFAU; // Cha — quake pulses      (47866)
        public const uint BlackHole                = 0xBAFBU; // Exd                      (47867)
        public const uint Nothingness              = 0xBAFCU; // BlackHole               (47868)
        public const uint BlackSpark               = 0xBCCDU; // BlackHole
        public const uint WhiteHole                = 0xBD66U; // Exd                      (48486)
        public const uint LongitudinalImplosion    = 0xBAFDU; // Cha                      (47869)
        public const uint LatitudinalImplosion     = 0xBAFEU; // Cha                      (47870)
        public const uint ImplosionShockwave       = 0xBAFFU; //                          (47871)
        public const uint DamningEdict             = 0xBB01U; // Cha — Splatoon DubbingEdict (47873)
        public const uint KnockDown1               = 0xBB02U; // Cha
        public const uint KnockDown2               = 0xBB03U; // Cha — Splatoon LandingHit (47875)
        public const uint BigBang                  = 0xBB05U; // Cha
        public const uint BigBangHidden            = 0xBB06U; // Cha
        public const uint StompAMoleCast           = 0xBAEFU; // Kef — Splatoon "Dondoko" cast (47855)
        public const uint StompAMole               = 0xBAF0U; // Kef — Splatoon DondokoHit (47856)
        public const uint Max                      = 0xBAE5U; // Kef — last pre-EQ cast (small Kefka)
        public const uint SlapHappyCastA           = 0xBAE6U; // Kef castbar
        public const uint SlapHappyCastB           = 0xBAE7U; // Kef castbar
        public const uint SlapHappy                = 0xBAE8U; // Kef hits
        public const uint SlapHappyFinal           = 0xBAE9U; // Kef
        public const uint ShockingImpact           = 0xBAEAU; // Kef
        public const uint Shockwave                = 0xBAEBU; // Kef
        public const uint LookUponMeAndDespairCastA = 0xBAECU; // Kef
        public const uint LookUponMeAndDespairCastB = 0xBAEDU; // Kef
        public const uint LookUponMeAndDespair     = 0xBAEEU; // Kef — line cleave (proxy)
        public const uint LookUponMeVfxA           = 0xC4BAU; // Kef — "look" VFX after BAEC (was Blackblood)
        public const uint LookUponMeVfxB           = 0xC533U; // Kef — "look" VFX after BAED
        public const uint BlizzardIIICast          = 0xBB0FU; // Exd — Splatoon LateP3Blizzaga (47887)
        public const uint BlizzardIIIFirst         = 0xBB0DU; // Exd
        public const uint BlizzardIII              = 0xBB11U; // Exd
        public const uint ThunderIIISync           = 0xBB09U; // Exd
        public const uint ThunderIII               = 0xBB0CU; // Exd
        public const uint Aetherlink1              = 0xC2E4U; // Cha/Exd
        public const uint Aetherlink2              = 0xC2E5U; // Cha/Exd

        // Enrage (cactbot ~840.7s jump targets).
        public const uint BowelsOfAgonyEnrage      = 0xC61FU; // Cha
        public const uint MeteorExdeathEnrage      = 0xC61EU; // Exd
        public const uint MeteorEnrage             = 0xC258U; // Exd
        public const uint BowelsOfAgonyEnrageAlt   = 0xC259U; // Cha
    }

    // Status ids. From the Splatoon script (decimal shown in comments).
    public static class StatusId
    {
        public const ushort Accretion        = (ushort)0x644;  // 1604 — one healer + one DPS; ~13s timer
        public const ushort DamageDown       = (ushort)0xB5F;  // 2911 — applied by Black Spark (BCCD) when a player stands in a hole
        // The three Earthquake "unmaking" debuffs. Their names are RSV-only in the Status sheet (the real
        // text arrives via RSV in-duty), so UmadRsvStrings.Seed() registers them or they render as
        // "_rsv_5452_…". Confirmed from in-duty type-262 RSVData: 5452 = Unbecoming, 5453 = Meanest
        // Existence, 5454 = Primordial Crust. (The earlier "Earth"/"LineDone" labels were wrong guesses, and
        // status 1605 — a same-named but unrelated sheet row — is NOT the one this fight applies.)
        public const ushort Unbecoming       = (ushort)0x154C; // 5452 — 1 stack on the 1st Black Hole hit
        public const ushort MeanestExistence = (ushort)0x154D; // 5453 — replaces Unbecoming on the 2nd hit
        public const ushort PrimordialCrust  = (ushort)0x154E; // 5454 — all 8 at EQ; timer by in-line #; cleansed
                                                               // (removed) on the 3rd hit, dropping the holder to 1 HP
        public const ushort FirstTarget  = (ushort)0xBBC;  // 3004 — line-order debuff group
        public const ushort SecondTarget = (ushort)0xBBD;  // 3005
        public const ushort ThirdTarget  = (ushort)0xBBE;  // 3006

        // Boss form-swap (from the P3 log). Applying KefkaFormSwap with stacks=Param routes the Param to
        // the engine as a TransformationId (Statuses.AddStatusInit path, like TOP's OmegaM/OmegaF). The
        // giant god-Kefka used the chain 0x1FF(511) -> 0x22B(555) -> 0x1FA(506); 506 is the final giant
        // (radius 22.5), applied right as Earthquake begins — so that's the one our segment opens with.
        public const ushort KefkaFormSwap  = (ushort)0x9E8; // 2536 — form-swap status carrying the Param
        public const ushort GiantKefkaForm = (ushort)0x1FA; // 506  — TransformationId param: giant god-Kefka
    }

    // ActionTimeline ids the giant Kefka plays via ActorControl 0x197 (PlayActionTimeline) in the P3 log.
    // 0x1E39/0x1E43 in the log matched the shared UmadConstants WarpOut/Spawn, which is how we know 0x197
    // is PlayActionTimeline and these params are timeline rows. The giant's teleport uses the +1 variants.
    public static class TimelineId
    {
        public const ushort GiantEntrance = (ushort)0x11D4; // 4564 — entrance (rise-from-below + displaces model out), once at transform
        public const ushort WarpOut       = (ushort)0x1E3A; // 7738 — teleport vanish (giant variant of 0x1E39)
        public const ushort WarpIn        = (ushort)0x1E44; // 7748 — teleport reappear (giant variant of 0x1E43)
        // The settled BIG IDLE is NOT a timeline — it's ModelState 0x05 (log: 273 003F|5 at the transform;
        // 0x3F = SetModelState). Applied via kefka.SetModelState(0x05) in the scenario, not here.

        // DEAD END (kept as documentation): Black Spark (BCCD/48333) has EMPTY VFX + EMPTY Omen fields;
        // its only visual is AnimationEnd → ActionTimeline 1378 (mon_sp/gimmick/monster_hanyou_hitclip…),
        // a monster hit-clip that does NOT read on the hole's swirl model — playing it on the hole showed
        // nothing in-game. There is no perceptible Black Spark VFX to render; the contact feedback is the
        // Damage Down debuff + the hole's own swirl. Not used by the scenario.
        public const ushort BlackSpark    = (ushort)0x562;  // 1378
    }

    // Head-marker / tether data ids (decimal), from the Splatoon script.
    public static class MarkerId
    {
        public const uint FinalStack         = 161; // stack head-marker icon
        public const uint TargetIconCommand  = 34;  // ActorControl command (not an icon id)
    }

    public static class TetherId
    {
        public const ushort BlackHole3 = 84;
        public const ushort BlackHole5 = 15;
    }

    // Head-marker lockon rows — indexes into the Lockon Excel sheet, resolved to
    // vfx/lockon/eff/{IconName}.avfx by SimCharacter.AttachLockonVfx. NOTE this is the Lockon
    // SHEET row, NOT the Splatoon network head-marker id (MarkerId.FinalStack = 161 is a different
    // table). Stack = row 100, the generic 4-player stack marker the TOP scenarios also use.
    public static class LockonId
    {
        public const uint Stack = 100;
    }
}
