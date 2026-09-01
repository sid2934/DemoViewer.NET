#region

using DemoViewer.NET.Playback2D.Core.Rendering;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     The override grammar and its precedence chain (plans/C2-gpu-provider.md §2.5, §7.1). Pure
///     string-to-enum work, so these run everywhere and in parallel: no probe, no environment, no
///     graphics.
///     <para>
///         The precedence rule is the point: an operator at a terminal beats a stored preference, and CI
///         setting <c>DV2D_RENDER_BACKEND</c> beats whatever a settings file happens to say. Getting it
///         backwards would mean a CI lane silently measuring a backend nobody asked for.
///     </para>
/// </summary>
public class RenderBackendResolutionTests
{
    [Test]
    [Arguments("auto", RenderBackendPreference.Auto)]
    [Arguments("AUTO", RenderBackendPreference.Auto)]
    [Arguments("cpu", RenderBackendPreference.ForceCpu)]
    [Arguments("  Cpu  ", RenderBackendPreference.ForceCpu)]
    [Arguments("gpu", RenderBackendPreference.PreferGpu)]
    [Arguments("GPU", RenderBackendPreference.PreferGpu)]
    [Arguments("angle", RenderBackendPreference.PreferGpu)]
    [Arguments("gl", RenderBackendPreference.PreferGpu)]
    [Arguments("force-gpu", RenderBackendPreference.ForceGpu)]
    [Arguments("Force-GPU", RenderBackendPreference.ForceGpu)]
    public async Task TryParse_KnownValues_MapToPreference(string value, RenderBackendPreference expected)
    {
        bool parsed = RenderBackendPreferenceParser.TryParse(value, out RenderBackendPreference actual);

        await Assert.That(parsed).IsTrue();
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    [Arguments("vulkan")]
    [Arguments("metal")]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task TryParse_Garbage_ReturnsFalseAndAuto(string? value)
    {
        bool parsed = RenderBackendPreferenceParser.TryParse(value, out RenderBackendPreference actual);

        await Assert.That(parsed).IsFalse();
        await Assert.That(actual).IsEqualTo(RenderBackendPreference.Auto);
    }

    [Test]
    public async Task FromEnvironment_Unset_IsAuto()
    {
        RenderBackendPreference preference =
            RenderBackendPreferenceParser.FromEnvironment("DV2D_RENDER_BACKEND_DOES_NOT_EXIST");

        await Assert.That(preference).IsEqualTo(RenderBackendPreference.Auto);
    }

    [Test]
    public async Task Resolve_ExplicitArgument_BeatsEverythingBelowIt()
    {
        RenderBackendPreference resolved = RenderBackendPreferenceParser.Resolve(
            RenderBackendPreference.ForceGpu, "cpu", "cpu", "cpu");

        await Assert.That(resolved).IsEqualTo(RenderBackendPreference.ForceGpu);
    }

    [Test]
    public async Task Resolve_CommandLine_BeatsEnvironmentAndSetting()
    {
        RenderBackendPreference resolved =
            RenderBackendPreferenceParser.Resolve(null, "cpu", "gpu", "gpu");

        await Assert.That(resolved).IsEqualTo(RenderBackendPreference.ForceCpu);
    }

    [Test]
    public async Task Resolve_Environment_BeatsSetting()
    {
        RenderBackendPreference resolved =
            RenderBackendPreferenceParser.Resolve(null, null, "cpu", "gpu");

        await Assert.That(resolved).IsEqualTo(RenderBackendPreference.ForceCpu);
    }

    [Test]
    public async Task Resolve_Setting_IsUsedWhenNothingElseSpeaks()
    {
        RenderBackendPreference resolved =
            RenderBackendPreferenceParser.Resolve(null, null, null, "gpu");

        await Assert.That(resolved).IsEqualTo(RenderBackendPreference.PreferGpu);
    }

    [Test]
    public async Task Resolve_AllNull_IsAuto()
    {
        RenderBackendPreference resolved =
            RenderBackendPreferenceParser.Resolve(null, null, null, null);

        await Assert.That(resolved).IsEqualTo(RenderBackendPreference.Auto);
    }

    /// <summary>
    ///     A typo in one source must not swallow a valid value in a lower one. The alternative, treating
    ///     "present but unparseable" as a decision, would let a stale settings file veto a working
    ///     <c>--cpu</c>, which is the opposite of what the precedence chain is for.
    /// </summary>
    [Test]
    public async Task Resolve_UnparseableSource_FallsThroughToTheNextOne()
    {
        RenderBackendPreference resolved =
            RenderBackendPreferenceParser.Resolve(null, "vulkan", "not-a-backend", "cpu");

        await Assert.That(resolved).IsEqualTo(RenderBackendPreference.ForceCpu);
    }
}
