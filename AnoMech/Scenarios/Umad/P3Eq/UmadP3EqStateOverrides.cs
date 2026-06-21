namespace AnoMech.Scenarios.Umad.P3Eq;

// User-controlled overrides for UmadP3EqState's randomized fields. Bound by the settings UI;
// a null value leaves the field randomized at scenario start.
public sealed class UmadP3EqStateOverrides
{
    // Inner-ring type. null = random; false = α (sets 1/3 diagonal), true = β (sets 1/3 cardinal).
    public bool? InnerFlip { get; set; }

    // Per-set absent outer slot. null = random; otherwise pins which cardinal is missing for that
    // set (each rolls independently).
    public OuterPos? S1Absent { get; set; }
    public OuterPos? S2Absent { get; set; }
    public OuterPos? S3Absent { get; set; }
    public OuterPos? S4Absent { get; set; }

    // Implosion variant. null = random; false = Longitudinal, true = Latitudinal.
    public bool? ImplosionLatitudinal { get; set; }
}
