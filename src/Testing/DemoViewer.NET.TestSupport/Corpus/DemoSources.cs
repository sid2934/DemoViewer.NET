namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     Turns one configured <c>DEMO_SOURCES</c> entry into an <see cref="IDemoSource" />.
///     <para>
///         An <c>http(s)://</c> entry yields a <see cref="FileBrowserDemoSource" /> and needs the
///         token; anything else goes to <see cref="DirectoryDemoSource.FromSpecifier" />, which
///         takes a plain path or a <c>file://</c> URI and rejects every other scheme.
///     </para>
///     <para>
///         The scheme dispatch lives here rather than on <see cref="DirectoryDemoSource" /> because
///         it needs the token, which that type has no business seeing.
///     </para>
/// </summary>
public static class DemoSources
{
    /// <summary>
    ///     The source one entry names. Throws rather than returning null for an unusable entry:
    ///     silently dropping a configured source reads as "nothing to fetch from" and skips the
    ///     tests the configuration says to run.
    /// </summary>
    /// <param name="specifier">One entry, already split off <c>DEMO_SOURCES</c>.</param>
    /// <param name="token">The API token for HTTP entries, from <c>DEMO_SOURCES_TOKEN</c>.</param>
    public static IDemoSource FromSpecifier(string specifier, string? token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specifier);
        string trimmed = specifier.Trim();
        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return DirectoryDemoSource.FromSpecifier(trimmed);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? instance))
        {
            // Not echoed. An unparsable entry has no sanitized form to report, and the raw text is
            // where a credential would be: an entry like https://user:secret@host:notaport/X fails
            // here, and this message is cached by the static Lazy that built the corpus, so it
            // would be reprinted for every corpus test in the run and into the CI log.
            throw new DemoCorpusConfigurationException(
                $"an http(s) entry in {DemoTestHelper.DemoSourcesEnvVar} is not a valid absolute "
                + "URI. An HTTP entry is https://host/SourceName, optionally with a directory "
                + "inside it. The entry is not quoted here in case it carries a credential.");
        }

        return new FileBrowserDemoSource(instance, token ?? string.Empty);
    }
}
