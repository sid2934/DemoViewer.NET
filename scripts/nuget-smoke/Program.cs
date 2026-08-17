// Restore+run proof for the packed Cs2DemoKit.* family (docs/distribution/nuget-packaging-plan.md).
//
// This is NOT a benchmark and NOT a functional test of parsing a real demo — it is demo-free by
// design (it never opens a .dem file). Its only job is to prove that a consumer who does nothing
// but `dotnet add package Cs2DemoKit.Analysis` gets a restorable, loadable, runnable dependency
// graph: all three assemblies (Analysis, and — transitively — Parser and Analysis.Rules) actually
// load and execute real code, not just "the DLLs are present."
//
// Exits non-zero on the first failed assertion (or an unhandled exception) so a CI step can gate
// on the exit code alone.

using Cs2DemoKit.Analysis.Rules;
using Cs2DemoKit.Analysis.Rules.Checking;
using Cs2DemoKit.Analysis.Rules.Scopes;
using Cs2DemoKit.Analysis.Yaml;
using Cs2DemoKit.Parser;

int failures = 0;

void Check(bool condition, string description)
{
    if (condition)
    {
        Console.WriteLine($"  ok   {description}");
    }
    else
    {
        Console.WriteLine($"  FAIL {description}");
        failures++;
    }
}

Console.WriteLine("Cs2DemoKit NuGet smoke consumer");
Console.WriteLine("================================");

// 1. Cs2DemoKit.Analysis: the embedded shipped rulesets load with zero errors. This is the
//    package's flagship "no rules/ directory on disk" entry point (see YamlConfigLoader.cs) —
//    exercising it proves YamlDotNet resolves, the embedded resources were packed correctly, and
//    the Rulesets v2 YAML pipeline runs end to end.
Console.WriteLine();
Console.WriteLine("[Cs2DemoKit.Analysis] YamlConfigLoader.LoadShippedEmbedded()");
try
{
    RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedEmbedded();
    Check(loaded.Success, $"load succeeded with no errors (errors: {loaded.Errors.Count})");
    Check(loaded.Rulesets.Count == 14, $"exactly 14 rulesets loaded (got {loaded.Rulesets.Count})");
    if (!loaded.Success)
    {
        foreach (RuleConfigError error in loaded.Errors)
        {
            Console.WriteLine($"       {error}");
        }
    }
}
catch (Exception ex)
{
    Check(false, $"LoadShippedEmbedded threw: {ex}");
}

// 2. Cs2DemoKit.Parser: touch a real type from the parser assembly. ParseWarningCodes is a plain
//    static class of const strings — reading it forces the assembly to load and its static
//    initializer (if any) to run.
Console.WriteLine();
Console.WriteLine("[Cs2DemoKit.Parser] ParseWarningCodes.WarningsTruncated");
try
{
    string code = ParseWarningCodes.WarningsTruncated;
    Console.WriteLine($"       value: \"{code}\"");
    Check(code == "warnings-truncated", $"constant has the expected stable value (got \"{code}\")");
}
catch (Exception ex)
{
    Check(false, $"reading ParseWarningCodes.WarningsTruncated threw: {ex}");
}

// 3. Cs2DemoKit.Analysis.Rules: run the front-half expression pipeline (lex -> parse -> normalize
//    -> resolve -> type-check) over a trivial literal expression. No demo, no engine, no catalog
//    — just the zero-dependency semantic core doing real work.
Console.WriteLine();
Console.WriteLine("[Cs2DemoKit.Analysis.Rules] ExpressionPipeline.Analyze(\"1 + 1 == 2\")");
try
{
    IScopeEnvironment scope = new ScopeEnvironment("smoke:", []);
    LanguageResult<CheckedExpression> result =
        ExpressionPipeline.Analyze("1 + 1 == 2", scope, expectedType: RulesType.Bool);
    Check(result.Success, $"expression analyzed successfully (diagnostics: {result.Diagnostics.Count})");
    if (!result.Success)
    {
        foreach (Diagnostic d in result.Diagnostics)
        {
            Console.WriteLine($"       {d}");
        }
    }
}
catch (Exception ex)
{
    Check(false, $"ExpressionPipeline.Analyze threw: {ex}");
}

Console.WriteLine();
if (failures > 0)
{
    Console.WriteLine($"SMOKE FAILED: {failures} assertion(s) failed.");
    return 1;
}

Console.WriteLine("SMOKE OK: Cs2DemoKit.Parser, Cs2DemoKit.Analysis, Cs2DemoKit.Analysis.Rules all restored, loaded and ran.");
return 0;
