using VIP_Postprocessing;
int checks = 0;
void Check(bool ok) { checks++; if (!ok) throw new Exception($"Check {checks} failed"); }
Check(!PostprocessingPolicy.HasExplicitEffect(null));
Check(!PostprocessingPolicy.HasExplicitEffect(""));
Check(!PostprocessingPolicy.HasExplicitEffect("off"));
Check(PostprocessingPolicy.HasExplicitEffect("warm"));
for (int owner = 0; owner < 64; owner++)
    for (int shown = -1; shown <= 64; shown++)
        Check(PostprocessingPolicy.CanTransmit(owner, shown) == (owner == shown));
Check(!PostprocessingPolicy.CanTransmit(-1, -1));
Check(!PostprocessingPolicy.CanTransmit(64, 64));
Console.WriteLine($"{checks} checks passed: explicit opt-in and exhaustive recipient isolation.");
