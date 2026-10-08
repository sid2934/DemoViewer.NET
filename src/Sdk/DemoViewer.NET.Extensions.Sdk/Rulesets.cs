namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     A ruleset the extension ships as YAML. The host loads it between the shipped rules and the user's own, so a
///     user file with the same id overrides it. It runs with the highlights on every demo while the extension is
///     on, never moves the highlights' fingerprint, and its <c>show: tables:</c> become library facts
///     (<see cref="IAnalysisFacts" />) under <see cref="FactKey" />(<see cref="QualifiedId" />, table).
///     <para>
///         The YAML's <c>ruleset:</c> key must be the qualified id. A ruleset that does not load, claims another
///         id, or declares a table another ruleset already declares is left out, and nothing else is affected.
///         List the id under <c>rulesets</c> in <c>extension.json</c> so the host keeps it apart when the
///         extension does not load.
///     </para>
/// </summary>
/// <param name="Id">The extension's own name for the ruleset: lowercase letters, digits and underscores.</param>
/// <param name="Yaml">Opens the YAML, for example an embedded resource. Called once per rules read.</param>
/// <param name="FeatureId">The feature that switches the ruleset; null for the extension's master switch.</param>
public sealed record RulesetContribution(string Id, Func<Stream> Yaml, string? FeatureId = null)
{
    private const string Separator = "__";

    /// <summary>
    ///     The ruleset's id as the host and the YAML spell it: the extension id with every character that is not a
    ///     lowercase letter or a digit made an underscore, two underscores, then <paramref name="id" />.
    ///     <c>("dev.example.hello", "kills")</c> is <c>dev_example_hello__kills</c>.
    /// </summary>
    /// <param name="extensionId">The extension's id.</param>
    /// <param name="id">The extension's own name for the ruleset.</param>
    public static string QualifiedId(string extensionId, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        if (!IsValidId(id))
        {
            throw new ArgumentException($"'{id}' is not a ruleset name: use lowercase letters, digits and underscores.", nameof(id));
        }

        return string.Concat(extensionId.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '_'))
               + Separator + id;
    }

    /// <summary>True when <paramref name="id" /> is a valid ruleset name: lowercase letters, digits and underscores, starting with a letter.</summary>
    /// <param name="id">The candidate.</param>
    public static bool IsValidId(string? id) =>
        id is { Length: > 0 } && id[0] is >= 'a' and <= 'z' && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
}
