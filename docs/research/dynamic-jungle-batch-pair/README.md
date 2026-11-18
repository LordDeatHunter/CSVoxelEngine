# Historical crowded Jungle batch pair

DynamicJungleBatchPairTests.cs preserves the exploratory source from commit
7fd7d6fba000770d2a2636bac268d4fc76aa3c4d, byte for byte (SHA-256
5ac93ba64ca42d585214bc3c081f4c35575e52d997fdbecdf5f2b9c0f7d92b90).

At seed 1337 Dynamic, the admitted giant at (4004,32,811) was suppressed by
normal batch crowding: its leaf top was 38 while the ordinary neighbor's was 50.
The final strict height assertion failed. The accepted design permits this
outcome, so the assertion is not a supported product invariant.

This file is retained outside OpenGLTest.Tests and is not compiled or discovered
as a test. Do not restore it to the test project or change generation to satisfy
the obsolete assertion. Its original failure, logs and source remain preserved
in Nexus task 5a963b4e-7d70-4471-a555-1a10f7ca398e, report d337ccede0472,
raw archive tfcece37588b0e909a and the original Git history.

Durable evidence is the complete positive/negative cohort in
DynamicJungleBatchCohortDiagnostic and the selected normal-fill/species-owner
witness in DynamicJungleGiantWitnessTests. The cohort retained all eleven
giants, including six emergent and five suppressed outcomes; it reproduced this
same negative pair. No global frequency or universal emergence claim follows.
