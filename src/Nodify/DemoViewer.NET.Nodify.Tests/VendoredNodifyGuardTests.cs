#region

using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify;

#endregion

namespace DemoViewer.NET.NodifyTests;

/// <summary>
///     The guard on the vendored Nodify fork (<c>src/Nodify/README.md</c>).
///     <para>
///         The finding this exists for: <b>compiling Nodify proves nothing.</b> The published
///         <c>NodifyAvalonia</c> 6.6.0 package compiles clean against Avalonia 12 and then throws
///         <c>TypeLoadException: Could not load type 'Avalonia.Controls.Primitives.IScrollable'</c>
///         at JIT of the first method that touches a Nodify type, before any XAML is parsed, because
///         a <c>netstandard2.0</c> assembly built against Avalonia 11 carries typerefs Avalonia 12 no
///         longer satisfies. Nothing resolves a typeref until load time, so only running the thing
///         answers the question.
///     </para>
///     <para>
///         Three layers, cheapest first: the assembly's own metadata resolves, the theme's compiled
///         XAML resolves, and an editor realises real node containers and rasterises them.
///     </para>
/// </summary>
public class VendoredNodifyGuardTests
{
    /// <summary>
    ///     Every control type the vendored theme keys a <c>ControlTheme</c> for. Hard-coded rather
    ///     than scraped out of the resource dictionary on purpose: a guard derived from the thing it
    ///     guards passes when that thing empties itself.
    /// </summary>
    private static readonly Type[] _themedControlTypes =
    [
        typeof(NodifyEditor),
        typeof(ItemContainer),
        typeof(DecoratorContainer),
        typeof(Node),
        typeof(StateNode),
        typeof(GroupingNode),
        typeof(KnotNode),
        typeof(Connector),
        typeof(NodeInput),
        typeof(NodeOutput),
        typeof(BaseConnection),
        typeof(Connection),
        typeof(LineConnection),
        typeof(CircuitConnection),
        typeof(StepConnection),
        typeof(PendingConnection),
        typeof(CuttingLine),
        typeof(Minimap),
        typeof(MinimapItem)
    ];

    /// <summary>The theme's three non-type resource keys, which the editor and minimap templates read.</summary>
    private static readonly string[] _themedNamedKeys =
    [
        "NodifyEditor.SelectionRectangleStyle",
        "NodifyEditor.PushedAreaStyle",
        "Minimap.ViewportStyle"
    ];

    /// <summary>
    ///     The cheapest layer, and the one that catches the exact published-package failure. Loading
    ///     every type forces the runtime to resolve each one's base type and interface list, and
    ///     touching every member's signature forces the parameter, return, field and event types
    ///     too. The spike behind <c>docs/rule-graph/design.md</c> §6.5 counted eight type-level and
    ///     six member-level breaks in the published assembly this way; any one of them is fatal.
    ///     <para>No Avalonia application, so this fails in milliseconds rather than behind a session.</para>
    /// </summary>
    [Test]
    public async Task VendoredAssembly_ResolvesEveryTypeAndMemberSignature()
    {
        Assembly nodify = typeof(NodifyEditor).Assembly;
        List<string> breaks = [];

        Type[] types;
        try
        {
            types = nodify.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            await Assert.That(
                    string.Join(
                        Environment.NewLine,
                        ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message)))
                .IsEqualTo(string.Empty);

            return;
        }

        foreach (Type type in types)
        {
            try
            {
                _ = type.BaseType;
                _ = type.GetInterfaces();

                foreach (MemberInfo member in type.GetMembers(
                             BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.Static
                                                 | BindingFlags.DeclaredOnly))
                {
                    TouchSignature(member);
                }
            }
            catch (Exception ex)
            {
                breaks.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Assert.That(string.Join(Environment.NewLine, breaks)).IsEqualTo(string.Empty);

        // The specific typeref that sinks the published package: NodifyEditor implements
        // ILogicalScrollable, whose IScrollable base moved to Avalonia.Base with no forwarder.
        await Assert.That(typeof(NodifyEditor).GetInterfaces().Select(i => i.FullName ?? i.Name))
            .Contains("Avalonia.Controls.Primitives.ILogicalScrollable");
    }

    /// <summary>
    ///     The second layer: the theme's compiled XAML loads and every <c>ControlTheme</c> in it
    ///     resolves, under both theme variants, because <c>Theme.axaml</c> carries separate Light
    ///     and Dark dictionaries and a break in one is invisible from the other.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("Integration")]
    public async Task VendoredTheme_ResolvesEveryControlTheme_InBothVariants()
    {
        await NodifyHeadlessSession.RunOnUi(async () =>
        {
            Application app = Application.Current!;
            List<string> missing = [];

            foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Light, ThemeVariant.Dark])
            {
                foreach (Type controlType in _themedControlTypes)
                {
                    if (!app.TryGetResource(controlType, variant, out object? theme)
                        || theme is not ControlTheme)
                    {
                        missing.Add($"{variant}: ControlTheme for {controlType.Name}");
                    }
                }

                foreach (string key in _themedNamedKeys)
                {
                    if (!app.TryGetResource(key, variant, out object? theme) || theme is not ControlTheme)
                    {
                        missing.Add($"{variant}: ControlTheme for '{key}'");
                    }
                }
            }

            await Assert.That(string.Join(Environment.NewLine, missing)).IsEqualTo(string.Empty);
        });
    }

    /// <summary>
    ///     The third layer, and the one the other two cannot stand in for: an editor with an
    ///     <c>ItemsSource</c> produces real <see cref="ItemContainer" />s, each applies its template
    ///     and holds the <see cref="Node" /> the item template built, the editor's own template
    ///     applies (which is where the <c>ItemsPanelTemplate</c> rebinding that Avalonia 12 changed
    ///     would fail), and the whole thing rasterises to something other than an empty background.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("Integration")]
    [Category("Render")]
    public async Task Editor_RealisesNodeContainers_AndRendersThem()
    {
        await NodifyHeadlessSession.RunOnUi(async () =>
        {
            GuardNode[] nodes =
            [
                new("first", new Point(40, 40)),
                new("second", new Point(260, 40)),
                new("third", new Point(40, 200)),
                new("fourth", new Point(260, 200))
            ];

            NodifyEditor editor = new()
            {
                ItemsSource = nodes,
                ItemTemplate = new FuncDataTemplate<GuardNode>(
                    (node, _) => new Node
                    {
                        Header = node.Title,
                        Content = new TextBlock
                        {
                            Text = node.Title,
                            Margin = new Thickness(8)
                        }
                    },
                    true)
            };

            // Location is the consumer's job in Nodify: the shipped ItemContainer theme templates
            // the container but never says where it goes. Without this every container stacks at
            // the origin, which still realises but renders as one box.
            editor.Styles.Add(new Style(x => x.OfType<ItemContainer>())
            {
                Setters =
                {
                    new Setter(ItemContainer.LocationProperty, new Binding(nameof(GuardNode.Location)))
                }
            });

            Window window = new()
            {
                Width = 640,
                Height = 480,
                Content = editor
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // The editor's own ControlTemplate applied. PART_ItemsPresenter is where the
            // ItemsPanelTemplate lives, and its NodifyCanvas is what binds ItemsExtent back out.
            await Assert.That(editor.IsMeasureValid).IsTrue();

            List<string> problems = [];
            for (int i = 0; i < nodes.Length; i++)
            {
                if (editor.ContainerFromIndex(i) is not ItemContainer container)
                {
                    problems.Add($"item {i} produced no ItemContainer");
                    continue;
                }

                // Through the visual tree rather than ContentControl.Presenter: the shipped
                // ItemContainer template's ContentPresenter carries no PART_ContentPresenter name,
                // so Presenter is null however well the template applied.
                if (container.GetVisualDescendants().OfType<Node>().FirstOrDefault() is not { } node)
                {
                    problems.Add($"item {i}'s container applied no template, or held no Node");
                    continue;
                }

                if (!Equals(node.Header, nodes[i].Title))
                {
                    problems.Add($"item {i}'s Node header was '{node.Header}', not '{nodes[i].Title}'");
                }

                if (container.Location != nodes[i].Location)
                {
                    problems.Add($"item {i}'s container sat at {container.Location}, not {nodes[i].Location}");
                }
            }

            await Assert.That(string.Join(Environment.NewLine, problems)).IsEqualTo(string.Empty);

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            await Assert.That(frame).IsNotNull();

            string outPath = Path.Combine(NodifyHeadlessSession.ArtifactDir, "nodify-guard.png");
            frame!.Save(outPath, new PngBitmapEncoderOptions());

            int drawn = CountDrawnPixels(frame);
            Console.WriteLine($"[nodify-guard] {outPath} drawn={drawn}");

            // Four templated nodes at 640x480. A blank or single-colour frame is what a theme that
            // loaded but resolved nothing looks like, and it is well under this.
            await Assert.That(drawn).IsGreaterThan(5_000);

            window.Close();
        });
    }

    private static void TouchSignature(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo method:
                _ = method.ReturnType;
                _ = method.GetParameters();

                break;
            case ConstructorInfo constructor:
                _ = constructor.GetParameters();

                break;
            case FieldInfo field:
                _ = field.FieldType;

                break;
            case PropertyInfo property:
                _ = property.PropertyType;

                break;
            case EventInfo @event:
                _ = @event.EventHandlerType;

                break;
        }
    }

    /// <summary>
    ///     Counts pixels that are neither transparent nor the window's flat background, which is
    ///     what "the nodes actually drew" reduces to without committing a golden image. This suite
    ///     guards that Nodify renders at all; pixel-level fidelity is not its business.
    /// </summary>
    private static int CountDrawnPixels(WriteableBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            Marshal.Copy(framebuffer.Address, buffer, 0, buffer.Length);
        }

        // The first pixel is background by construction: the editor is inset from the window edge.
        byte bb = buffer[0], bg = buffer[1], br = buffer[2];

        int drawn = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (buffer[i] != bb || buffer[i + 1] != bg || buffer[i + 2] != br)
            {
                drawn++;
            }
        }

        return drawn;
    }

    private sealed record GuardNode(string Title, Point Location);
}
