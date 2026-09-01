namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     Draw-state for the planted-C4 timer ring (A4 overlay). Present only while a live ticking
///     <c>CPlantedC4</c> exists. The viewport draws a depleting detonation ring at the reconstructed world
///     position (on the floor slice the bomb sits on), plus a second depleting ring during an active defuse:
///     the defuse-vs-detonation race made spatial. Reuses the bomb data the game-info panel already reads.
/// </summary>
public readonly record struct BombMarker(
    float WorldX,
    float WorldY,
    float WorldZ,
    double DetonationFraction, // 1 = just planted, 0 = blown: drives the depleting ring sweep
    bool BeingDefused,
    double DefuseFraction); // 1 = just started, 0 = defused (only meaningful when BeingDefused)
