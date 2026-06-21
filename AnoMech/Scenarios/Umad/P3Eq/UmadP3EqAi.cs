using System;
using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Umad.P3Eq;

// Party-movement AI ("strat") for the P3 Earthquake segment. Currently a TEST strat: it scatters
// every doppel to a random arena position at the pull start and then leaves them still, so
// mechanics that target players (e.g. Slap Happy cones / role + party stacks) can be checked
// against spread-out, fixed positions. The player's own slot is left alone — you keep controlling
// your character. Replace with the real Black Hole / Implosion solve later; see UmadP4KefkaSaysAi
// for the fuller pattern (SafeSpot solvers, gaze facing, swaps).
public sealed class UmadP3EqAi : IScenarioAi<UmadP3EqState>
{
    public string Name => "Earthquake (WIP)";

    // Keep the scatter comfortably inside the ~20-unit arena ring.
    private const float ScatterRadius = 18f;

    public void Run(UmadP3EqState state, SimWorld world)
    {
        var ai = new AiManager(world);
        // One move at the pull start, no further choreography → each doppel walks to its random
        // spot and stops. jitter:0 because the scatter already covers the whole disc.
        ai.Move(2f, () => RandomScatter(world.Party.PlayerRole), jitter: 0f);
    }

    // Eight independent points sampled uniformly over the arena disc (r·√u for area-uniform
    // density). The player's slot is left null so the AI doesn't yank the real player.
    private static IAiMove RandomScatter(PartyRole playerRole)
    {
        var rng = Random.Shared;
        var coords = new Vector2?[8];
        for (var i = 0; i < 8; i++)
        {
            if (i == (int)playerRole) continue;
            var angle = rng.NextSingle() * MathF.Tau;
            var radius = ScatterRadius * MathF.Sqrt(rng.NextSingle());
            coords[i] = new Vector2(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
        }
        return AiMove.Create(coords).NaturalOrder();
    }
}
