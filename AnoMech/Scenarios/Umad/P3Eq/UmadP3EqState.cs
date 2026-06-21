using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Umad.P3Eq;

// Outer-ring cardinal slot, in clockwise order. Only the outer ring ever tethers; the solo
// black hole of sets 1/4 is the clockwise predecessor of the set's absent outer slot.
public enum OuterPos { N, E, S, W }

// One per-run Black Hole set layout. Cosmetic = the 4 middle + 4 inner holes (visual density,
// never tether). Outer = the 3 present outer-ring holes (the only tether sources). Solo = the
// CW-predecessor hole that fires the single-tether wave (sets 1 and 4 only; null for 2 and 3).
public sealed record BlackHoleSet(
    IReadOnlyList<Vector3> Cosmetic,
    IReadOnlyList<(OuterPos Pos, Vector3 Local)> Outer,
    OuterPos? Solo);

// One Slap Happy resolution. RoleStack = the left-hand "Role Stack" variant: 3 cones, one each at a
// random tank, healer, and DPS (ConeTargets has 3 entries). Otherwise it's the right-hand "Party
// Stack": a single cone at a random party member (ConeTargets has 1 entry). The variant sequence is
// fixed to the cast timeline (party, role, role); only the cone targets are randomized per run.
public sealed record SlapHappyInstance(bool RoleStack, IReadOnlyList<PartyRole> ConeTargets);

// Per-run randomized state for the Earthquake segment.
//
// Debuffs, to the strict rules:
//   Accretion (1604): one healer + one DPS; strictly one of the pair is First-in-line and the
//   other Second (never Third). The remaining 6 split into Supports (2 tanks + the non-Accretion
//   healer) and Dps (the other 3); EACH group holds exactly one First/Second/Third.
//   (status ids: First 3004 / Second 3005 / Third 3006.)
//
// Black Hole layout: 4 sets, each 11 holes drawn from a fixed 20-position grid, chosen by the
// inner-flip roll + 4 independent outer-absent rolls. Plus the per-run Implosion variant
// (Longitudinal vs Latitudinal).
//
// Random within those constraints. Built once in Run so a play stays deterministic.
public sealed class UmadP3EqState
{
    private readonly Rng rng = new();

    // ---- Debuffs ----

    // In-line number per role: 1 = First (3004), 2 = Second (3005), 3 = Third (3006).
    public IReadOnlyDictionary<PartyRole, int> InLine { get; }

    // The two Accretion (1604) holders: one healer + one DPS (one First, one Second).
    public IReadOnlyList<PartyRole> Accretion { get; }

    // The 6 non-Accretion players in their two groups of three (each: one 1/2/3).
    public IReadOnlyList<PartyRole> Supports { get; }   // 2 tanks + the non-Accretion healer
    public IReadOnlyList<PartyRole> Dps { get; }        // the 3 non-Accretion DPS

    public int NumberOf(PartyRole role) => InLine[role];
    public bool IsAccretion(PartyRole role) => Accretion.Contains(role);

    // ---- Black Hole layout + Implosion ----

    // The 4 Black Hole sets, index 0..3.
    public IReadOnlyList<BlackHoleSet> BlackHoleSets { get; }

    // false = Longitudinal Implosion (BAFD), true = Latitudinal (BAFE).
    public bool ImplosionLatitudinal { get; }

    // ---- Kefka ----

    // Per-teleport facing for the giant Kefka (model rotation, radians). Each is an independent
    // random multiple of 45° — repeats allowed (it can warp and reappear facing the same way).
    // Indexed by teleport order. (8 is more than the segment uses.)
    public IReadOnlyList<float> GiantHeadings { get; }

    // The 3 Slap Happy resolutions, in cast order (see SlapHappyInstance).
    public IReadOnlyList<SlapHappyInstance> SlapHappy { get; }

    // ---- Knock Down ----
    // (Stomp-a-Mole's first-tower side is deterministic — 90° CW of Kefka's facing — so no roll here.)

    // The single random player who gets the Knock Down stack head-marker.
    public PartyRole KnockDownStackTarget { get; }

    // The two Damning Edict (BB01) cleave targets, one per cast — an independent random player each.
    // Chaos faces the target at cast start so the 180° frontal cleave aims at them.
    public IReadOnlyList<PartyRole> DamningEdictTargets { get; }

    // ---- Grid position tables (scenario-local offsets from the (100,0,100) origin) ----
    // Vector3(x, 0, z): -Z = north, +Z = south, +X = east, -X = west.

    private static readonly Vector3[] InnerCardinal =
        [new(-9, 0, 0), new(9, 0, 0), new(0, 0, -9), new(0, 0, 9)]; // W9 E9 N9 S9

    // r ≈ 9 diagonals: ±6.36 = 9/√2 (the old ±6 rounding was wrong).
    private static readonly Vector3[] InnerDiagonal =
        [new(-6.36f, 0, -6.36f), new(6.36f, 0, -6.36f), new(-6.36f, 0, 6.36f), new(6.36f, 0, 6.36f)]; // NW9 NE9 SW9 SE9

    // r ≈ 13.5 middle ring. Orientation is FIXED per set (does not flip with the inner roll):
    // Bslash "\" → sets 1 & 3, Slash "/" → sets 2 & 4 (the prior assignment had these
    // swapped). Offsets = absolute coords − 100 on each axis.
    private static readonly Vector3[] MiddleBslash =    // sets 1 & 3
        [new(-12.47f, 0, -5.17f), new(5.17f, 0, -12.47f), new(-5.17f, 0, 12.47f), new(12.47f, 0, 5.17f)]; // WNW NNE SSW ESE

    private static readonly Vector3[] MiddleSlash =     // sets 2 & 4
        [new(-5.17f, 0, -12.47f), new(12.47f, 0, -5.17f), new(-12.47f, 0, 5.17f), new(5.17f, 0, 12.47f)]; // NNW ENE WSW SSE

    public UmadP3EqState(SimParty party, UmadP3EqStateOverrides overrides)
    {
        var inLine = new Dictionary<PartyRole, int>();

        // Accretion pair: one healer (2/3) + one DPS (4..7); one is First, the other Second.
        var accHealer = rng.NextObj((PartyRole)2, (PartyRole)3);
        var accDps = rng.NextObj((PartyRole)4, (PartyRole)5, (PartyRole)6, (PartyRole)7);
        var accHealerIsFirst = rng.NextBool();
        inLine[accHealer] = accHealerIsFirst ? 1 : 2;
        inLine[accDps] = accHealerIsFirst ? 2 : 1;
        Accretion = [accHealer, accDps];

        // Remaining 6 → two groups of three, each getting one First / Second / Third.
        var otherHealer = accHealer == (PartyRole)2 ? (PartyRole)3 : (PartyRole)2;
        Supports = rng.Shuffle((PartyRole)0, (PartyRole)1, otherHealer);
        AssignOneEach(inLine, Supports);

        Dps = rng.Shuffle(new[] { (PartyRole)4, (PartyRole)5, (PartyRole)6, (PartyRole)7 }
            .Where(r => r != accDps).ToArray());
        AssignOneEach(inLine, Dps);

        InLine = inLine;

        // Roll a sequence of giant-Kefka teleport facings: each an independent random 45° multiple.
        var headings = new List<float>();
        for (var i = 0; i < 8; i++)
            headings.Add(rng.NextInt(8) * (MathF.PI / 4f));
        GiantHeadings = headings;

        // Slap Happy: variant sequence fixed to the cast timeline (party, role, role); cone targets
        // random within the role constraints (role stack = 1 random tank + healer + DPS; party stack
        // = 1 random member).
        var slaps = new List<SlapHappyInstance>();
        foreach (var roleStack in new[] { false, true, true })
        {
            IReadOnlyList<PartyRole> targets = roleStack
                ? new[] { RandomTank(), RandomHealer(), RandomDps() }
                : new[] { rng.NextRole() };
            slaps.Add(new SlapHappyInstance(roleStack, targets));
        }
        SlapHappy = slaps;

        // Black Hole layout rolls (honor overrides, else random).
        var innerFlip = overrides.InnerFlip ?? rng.NextBool();   // false = α, true = β

        // α: sets 1/3 inner = diagonal, sets 2/4 inner = cardinal. β swaps.
        var s13Inner = innerFlip ? InnerCardinal : InnerDiagonal;
        var s24Inner = innerFlip ? InnerDiagonal : InnerCardinal;

        // Each set rolls its absent outer INDEPENDENTLY over all 4 cardinals — no fixed-present
        // slot, no S1↔S2 linkage (the old s12/3-way model came
        // from a 4-pull sample and was disproven by the 30-set dataset). OuterPos is N,E,S,W.
        var s1Absent = overrides.S1Absent ?? (OuterPos)rng.NextInt(4);
        var s2Absent = overrides.S2Absent ?? (OuterPos)rng.NextInt(4);
        var s3Absent = overrides.S3Absent ?? (OuterPos)rng.NextInt(4);
        var s4Absent = overrides.S4Absent ?? (OuterPos)rng.NextInt(4);

        BlackHoleSets =
        [
            MakeSet(MiddleBslash, s13Inner, Present(s1Absent), CwPredecessor(s1Absent)),
            MakeSet(MiddleSlash,  s24Inner, Present(s2Absent), null),
            MakeSet(MiddleBslash, s13Inner, Present(s3Absent), null),
            MakeSet(MiddleSlash,  s24Inner, Present(s4Absent), CwPredecessor(s4Absent)),
        ];

        ImplosionLatitudinal = overrides.ImplosionLatitudinal ?? rng.NextBool();

        // Knock Down stack target — random player.
        KnockDownStackTarget = rng.NextRole();

        // Damning Edict targets — one random player per cast (two casts).
        DamningEdictTargets = [rng.NextRole(), rng.NextRole()];
    }

    // Random role within each group, for Slap Happy cone targeting.
    private PartyRole RandomTank()   => rng.NextObj(PartyRole.MainTank, PartyRole.OffTank);
    private PartyRole RandomHealer() => rng.NextObj(PartyRole.RegenHealer, PartyRole.ShieldHealer);
    private PartyRole RandomDps()    => rng.NextObj(PartyRole.MeleeDpsA, PartyRole.MeleeDpsB, PartyRole.PhysRangedDps, PartyRole.CasterDps);

    // group is pre-shuffled, so assigning 1/2/3 in order is a uniform random one-each.
    private static void AssignOneEach(IDictionary<PartyRole, int> inLine, IReadOnlyList<PartyRole> group)
    {
        inLine[group[0]] = 1;
        inLine[group[1]] = 2;
        inLine[group[2]] = 3;
    }

    private static BlackHoleSet MakeSet(Vector3[] middle, Vector3[] inner, IReadOnlyList<OuterPos> present, OuterPos? solo)
    {
        var cosmetic = middle.Concat(inner).ToList();
        var outer = present.Select(p => (Pos: p, Local: OuterLocal(p))).ToList();
        return new BlackHoleSet(cosmetic, outer, solo);
    }

    // The 3 present outer slots = all four cardinals minus the absent one.
    private static IReadOnlyList<OuterPos> Present(OuterPos absent)
        => Enum.GetValues<OuterPos>().Where(p => p != absent).ToList();

    private static Vector3 OuterLocal(OuterPos p) => p switch
    {
        OuterPos.N => new(0, 0, -17),
        OuterPos.S => new(0, 0, 17),
        OuterPos.W => new(-17, 0, 0),
        _ => new(17, 0, 0), // E
    };

    // Clockwise order N → E → S → W; solo = the slot one step CW from the absent slot.
    private static OuterPos CwPredecessor(OuterPos absent) => absent switch
    {
        OuterPos.N => OuterPos.W,
        OuterPos.E => OuterPos.N,
        OuterPos.S => OuterPos.E,
        _ => OuterPos.S, // W
    };
}
