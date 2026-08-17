#region

using System.Runtime.CompilerServices;

#endregion

// Friend grants for the merged parser assembly (parse pipeline + entity tracking + typed
// entity wrappers). FieldPath / FieldPathReader / HuffmanNode and the RuntimeField internal
// ctor are implementation details of the decoder; the test assemblies reach into them
// directly rather than widening the public surface for everyone.
[assembly: InternalsVisibleTo("Cs2DemoKit.Parser.Tests")]
[assembly: InternalsVisibleTo("Cs2DemoKit.Analysis.Tests")]
// App.Tests builds synthetic ParsedDemo fixtures for the Stats-tab headless tests (same pattern
// as the Analysis.Tests projector fixtures).
[assembly: InternalsVisibleTo("DemoViewer.NET.App.Tests")]
// The codegen tool derives the LensState from the SDK package and stamps its CanonicalHash
// (internal setter) before emitting the generated registry — same privilege MigrationReplay
// exercises from inside this assembly.
[assembly: InternalsVisibleTo("DemoViewer.NET.Codegen")]
