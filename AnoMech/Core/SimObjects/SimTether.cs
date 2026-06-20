using System;
using System.Numerics;
using AnoMech.Core.Native;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Core.SimObjects;

// Two-ended tether. Each side writes the channeling-sheet id at slot 0 of its
// VfxContainer.Tethers, pointing at the other side's GameObjectId. Optionally
// asks each character to host a matching debuff for the tether's duration —
// SimCharacter.AddStatus(id, duration) owns the countdown and auto-removes on expiry.
//
// SimTether ticks its own elapsed counter so it can clear the tether VFX when
// duration is reached. Both auto-expire and Despawn (called via SimWorld.Reset
// or directly) use a sentinel check: only clear the slot when our TetherId is
// still occupying it. This handles chained tethers that overwrite the same
// slot in the same frame (e.g. P5 Delta prep → real at t=29s) without racing
// our own ClearTether against the new SetTether.
//
// Distance / endpoint-death break logic lives in the owning scenario — SimTether
// only renders the visual and ticks expiry.
public sealed unsafe class SimTether : ISimObject
{
    private const byte Slot = 0;

    private readonly Func<SimCharacter?> source;
    private readonly Func<SimCharacter?> target;
    private SimStatus? statusA;
    private SimStatus? statusB;
    private readonly float duration;
    private readonly ushort debuffStatusId;
    private float elapsed;
    private bool active;
    private SimCharacter? currentSource;
    private SimCharacter? currentTarget;
    private ConditionalStatus? conditionalStatus;
    private bool autoFaceTarget;

    // ── FORK-LOCAL ENGINE ADDITION — NOT upstream (anomek/AnoMech). Flag explicitly if ever
    //    committed; per the working agreement this Core/ change must not ride along with a
    //    scenario-only PR. ──────────────────────────────────────────────────────────────────
    // By default the tether VFX is hosted on the SOURCE end, so the beam emanates from the
    // source. reverseVisual hosts it on the TARGET end instead (beam emanates from the target),
    // for mechanics where the fixed target is the visual originator — e.g. UMAD P3Eq black holes,
    // where TetherPassable makes the migrating player the source but the beam should come from the
    // hole. Coordination is unaffected: SimWorld.TetherPassable now caps holders by logical tether
    // SOURCE (CountPassableHolders), not the player's slot-0 VFX, so reverse-hosted parallel
    // tethers still honor the per-player cap even though the source no longer occupies slot 0.
    private bool reverseVisual;
    private SimCharacter? VfxHost => reverseVisual ? currentTarget : currentSource;
    private SimCharacter? VfxPointAt => reverseVisual ? currentSource : currentTarget;

    public ushort TetherId { get; }

    public SimCharacter? A => currentSource;
    public SimCharacter? B => currentTarget;
    // Set by the scenario when this tether has been resolved (broken or failed) to
    // prevent duplicate processing if multiple triggers fire in the same frame.
    public bool Resolved { get; set; }
    public bool IsActive => active;

    public static bool IsAnyDead(SimTether t) =>
        !t.A.IsAlive() || !t.B.IsAlive();
    public bool StretchGt(float distance) =>
        A is { } a && B is { } b && Vector3.DistanceSquared(a.Position, b.Position) > distance * distance;
    public bool StretchLt(float distance) =>
        A is { } a && B is { } b && Vector3.DistanceSquared(a.Position, b.Position) < distance * distance;

    internal SimTether(SimCharacter? source, Func<SimCharacter?> target, ushort tetherId, ushort debuffStatusId, float duration)
        : this(() => source, target, tetherId, debuffStatusId, duration) { }

    internal SimTether(Func<SimCharacter?> source, Func<SimCharacter?> target, ushort tetherId, ushort debuffStatusId, float duration)
    {
        this.source = source;
        this.target = target;
        TetherId = tetherId;
        this.debuffStatusId = debuffStatusId;
        this.duration = duration;
        currentSource = source();
        currentTarget = target();

        CreateVfx();
        if (debuffStatusId != 0 && duration > 0f)
        {
            statusA = currentSource?.AddStatus(debuffStatusId, duration);
            statusB = currentTarget?.AddStatus(debuffStatusId, duration);
        }
        active = true;
    }

    public void Tick(float deltaSeconds)
    {
        if (!active) return;
        if (duration > 0f)
        {
            elapsed += deltaSeconds;
            if (elapsed >= duration)
            {
                if (conditionalStatus != null)
                {
                    currentSource?.RemoveStatus(conditionalStatus.StatusId);
                    currentTarget?.RemoveStatus(conditionalStatus.StatusId);
                }
                ClearTetherVfxIfOwned();
                active = false;
                return;
            }
        }

        var nextTarget = target();
        if (nextTarget != currentTarget)
        {
            ClearTetherVfxIfOwned();
            statusB?.Despawn();
            currentTarget = nextTarget;
            CreateVfx();
            if (debuffStatusId != 0 && duration > 0f)
            {
                statusB = currentTarget?.AddStatus(debuffStatusId, duration);
            }
        }

        var nextSource = source();
        if (nextSource != currentSource)
        {
            ClearTetherVfxIfOwned();
            statusA?.Despawn();
            currentSource = nextSource;
            CreateVfx();
            if (debuffStatusId != 0 && duration > 0f)
            {
                statusA = currentSource?.AddStatus(debuffStatusId, duration);
            }
        }

        if (conditionalStatus != null)
        {
            if (conditionalStatus.Condition(this))
            {
                currentSource?.AddStatus(conditionalStatus.StatusId);
                currentTarget?.AddStatus(conditionalStatus.StatusId);
            }
            else
            {
                currentSource?.RemoveStatus(conditionalStatus.StatusId);
                currentTarget?.RemoveStatus(conditionalStatus.StatusId);
            }
        }

        if (autoFaceTarget && currentTarget != null)
        {
            currentSource?.Face(currentTarget);
        }
    }
    
    public void Despawn()
    {
        if (active)
        {
            ClearTetherVfxIfOwned();
            active = false;
        }
        // SimStatus.Despawn is idempotent — safe even if already auto-expired.
        statusA?.Despawn();
        statusB?.Despawn();
        if (conditionalStatus != null)
        {
            currentSource?.RemoveStatus(conditionalStatus.StatusId);
            currentTarget?.RemoveStatus(conditionalStatus.StatusId);
        }
    }

    private void CreateVfx()
    {
        if (VfxHost is { } host && VfxPointAt is { } pointAt)
            VfxFunctions.SetTether((Character*)host.BattleCharaPtr, Slot, TetherId, pointAt.GameObjectId, 1);
    }

    // Sentinel-checked clear: only wipe a slot we still own. A chained tether
    // (new SetTether on the same slot via the new SimTether ctor) will have
    // overwritten Vfx.Tethers[slot].Id; we leave that alone.
    private void ClearTetherVfxIfOwned()
    {
        if (VfxHost is { } host)
        {
            var ca = (Character*)host.BattleCharaPtr;
            if (VfxFunctions.GetTetherId(ca, Slot) == TetherId) VfxFunctions.ClearTether(ca, Slot);
        }
    }

    // FORK-LOCAL ENGINE ADDITION (see reverseVisual field). Flip which end hosts the tether VFX
    // so the beam emanates from the target instead of the source. Safe to call right after
    // construction: clears the slot on the old host, then re-hosts on the new end.
    public SimTether SetReverseVisual(bool value = true)
    {
        if (reverseVisual == value) return this;
        ClearTetherVfxIfOwned();
        reverseVisual = value;
        CreateVfx();
        return this;
    }

    public SimTether SetConditionalStatus(ushort statusId, Predicate<SimTether> predicate)
    {
        conditionalStatus = new ConditionalStatus(statusId, predicate);
        return this;
    }
    
    private record ConditionalStatus(ushort StatusId, Predicate<SimTether> Condition);

    public SimTether SetAutoFaceTarget(bool value)
    {
        autoFaceTarget = value;
        return this;
    }
}
