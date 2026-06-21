using System;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Umad.P3Eq;

// ImGui panel rendered in the main window's "Scenario config" pane while this scenario is
// active. Owns the StateOverrides instance and writes user choices into it. Each row is a
// tri/quad-state: Auto = random at scenario start, the others pin the roll.
public sealed class UmadP3EqSettingsWindow
{
    public UmadP3EqStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("Auto")) ResetAll();
        if (SettingsGrid.Begin("##umadp3eq"))
        {
            BoolRow("Inner ring:", "inner", "α", "β", Overrides.InnerFlip, v => Overrides.InnerFlip = v);
            DirRow("S1 absent:", "s1", Overrides.S1Absent, v => Overrides.S1Absent = v);
            DirRow("S2 absent:", "s2", Overrides.S2Absent, v => Overrides.S2Absent = v);
            DirRow("S3 absent:", "s3", Overrides.S3Absent, v => Overrides.S3Absent = v);
            DirRow("S4 absent:", "s4", Overrides.S4Absent, v => Overrides.S4Absent = v);
            BoolRow("Implosion:", "impl", "Long", "Lat", Overrides.ImplosionLatitudinal, v => Overrides.ImplosionLatitudinal = v);
            SettingsGrid.End();
        }
    }

    private void ResetAll()
    {
        Overrides.InnerFlip = null;
        Overrides.S1Absent = null;
        Overrides.S2Absent = null;
        Overrides.S3Absent = null;
        Overrides.S4Absent = null;
        Overrides.ImplosionLatitudinal = null;
    }

    // Auto / <falseLabel> / <trueLabel> tri-state writing a nullable bool
    // (null = Auto/random, false = falseLabel, true = trueLabel).
    private static void BoolRow(string label, string id, string falseLabel, string trueLabel, bool? value, Action<bool?> set)
    {
        SettingsGrid.Row(label);
        if (ImGui.RadioButton($"Auto##{id}", value == null)) set(null);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{falseLabel}##{id}", value == false)) set(false);
        ImGui.SameLine();
        if (ImGui.RadioButton($"{trueLabel}##{id}", value == true)) set(true);
    }

    // Auto / N / E / S / W state writing a nullable OuterPos (null = Auto/random).
    private static readonly OuterPos[] Dirs = [OuterPos.N, OuterPos.E, OuterPos.S, OuterPos.W];

    private static void DirRow(string label, string id, OuterPos? value, Action<OuterPos?> set)
    {
        SettingsGrid.Row(label);
        if (ImGui.RadioButton($"Auto##{id}", value == null)) set(null);
        foreach (var dir in Dirs)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{dir}##{id}", value == dir)) set(dir);
        }
    }
}
