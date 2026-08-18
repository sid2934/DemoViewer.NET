#region

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     The single source of truth for available themes (central theme system, design notes in git history).
///     Holds the built-in <c>dark</c> / <c>light</c> / <c>system</c> (native <see cref="ThemeVariant" />s whose
///     tokens live in <c>DarkPalette.axaml</c>) plus any registered CUSTOM themes (High-Contrast, E-Girl, user
///     drop-ins). A custom theme is registered as a <c>ThemeVariant(id, base)</c> and a token-override
///     <see cref="ResourceDictionary" />; those override dictionaries are collected in one registry-owned
///     dictionary and merged into the app once, so the variant → base → Default fallback chain resolves each
///     token: a custom variant's own token wins, everything else inherits its base Light/Dark palette.
///     <para>
///         User drop-ins (T3) are loaded from <see cref="AppPaths.ThemesDirectory" /> by <see cref="Reload" />
///         — NOT at construction, so a fresh registry (tests, the designer) is hermetic. <see cref="Reloaded" />
///         fires after a reload so the app can repaint code-held surfaces that don't observe a same-variant
///         override edit on their own.
///     </para>
/// </summary>
public sealed class ThemeRegistry
{
    private readonly Dictionary<string, Theme> _byId = new(StringComparer.OrdinalIgnoreCase);

    // One dictionary holding ThemeDictionaries[customVariant] = overrides, merged into Application.Resources.
    private readonly ResourceDictionary _customDicts = new();

    // Ordered store (built-ins first, in registration order, then user drop-ins) + a case-insensitive id
    // lookup over the same Theme instances. Both are kept in sync by Add/Remove.
    private readonly List<Theme> _ordered = [];
    private bool _installed;

    public ThemeRegistry()
    {
        Add(new Theme
        {
            Id = "dark",
            DisplayName = "Dark",
            Variant = ThemeVariant.Dark
        });
        Add(new Theme
        {
            Id = "light",
            DisplayName = "Light",
            Variant = ThemeVariant.Light
        });
        Add(new Theme
        {
            Id = "system",
            DisplayName = "System",
            Variant = ThemeVariant.Default
        });
        LoadBuiltInCustomThemes();
    }

    /// <summary>All registered themes, in registration order (built-ins first, then user drop-ins).</summary>
    public IReadOnlyList<Theme> Themes => _ordered.ToList();

    // Load the embedded built-in custom themes (High-Contrast, E-Girl) from assembly resources (Themes/*.json)
    // via the SAME parser as user drop-ins — the proof that a built-in is pure data too. Hermetic (reflection
    // over embedded resources, no Avalonia asset system), so a fresh registry in a unit test loads them without
    // a running app. Loaded in filename order (the files carry an NN- prefix, so the picker lists them
    // deterministically). A missing/broken built-in is skipped, never fatal.
    private void LoadBuiltInCustomThemes()
    {
        Assembly asm = typeof(ThemeRegistry).Assembly;
        foreach (string resource in asm.GetManifestResourceNames()
                     .Where(n => n.Contains(".Themes.", StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            try
            {
                using Stream? stream = asm.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                using StreamReader reader = new(stream);
                ThemeDefinition? def = ThemeJson.TryParse(reader.ReadToEnd(), resource);
                if (def is not null)
                {
                    Register(def, ThemeSource.BuiltIn);
                }
            }
            catch (IOException)
            {
                // A shipped built-in that fails to read is simply not offered — never crash the app.
            }
        }
    }

    /// <summary>Fires after <see cref="Reload" /> re-scans the drop-in directory, so the app can refresh + repaint.</summary>
    public event EventHandler? Reloaded;

    private void Add(Theme t)
    {
        if (_byId.TryGetValue(t.Id, out Theme? existing))
        {
            _ordered.Remove(existing); // replace in place (re-registering the same id, e.g. a drop-in edit)
        }

        _byId[t.Id] = t;
        _ordered.Add(t);
    }

    /// <summary>
    ///     Registers a custom theme: a <paramref name="baseVariant" /> (Light or Dark) it inherits plus
    ///     <paramref name="tokens" /> it overrides. Idempotent per id (re-registering replaces the overrides,
    ///     e.g. a drop-in edit + reload). Returns the registered <see cref="Theme" />.
    /// </summary>
    public Theme RegisterCustom(
        string id, string displayName, ThemeVariant baseVariant,
        IReadOnlyDictionary<string, Color> tokens, ThemeSource source = ThemeSource.BuiltIn)
    {
        ThemeVariant variant = new(id, baseVariant);
        ResourceDictionary overrides = new();
        foreach ((string key, Color color) in tokens)
        {
            overrides[key] = new SolidColorBrush(color);
        }

        _customDicts.ThemeDictionaries[variant] = overrides;

        Theme theme = new()
        {
            Id = id,
            DisplayName = displayName,
            Variant = variant,
            Source = source
        };
        Add(theme);
        return theme;
    }

    /// <summary>Registers a <see cref="ThemeDefinition" /> (parsed JSON — a built-in or a user drop-in).</summary>
    public Theme Register(ThemeDefinition def, ThemeSource source) =>
        RegisterCustom(def.Id, def.DisplayName, def.BaseVariant, def.Tokens, source);

    /// <summary>
    ///     Re-scans <see cref="AppPaths.ThemesDirectory" /> for <c>*.json</c> drop-ins (T3): drops the previously
    ///     loaded user themes, loads the current ones (malformed files skipped, a user id colliding with a
    ///     built-in skipped to protect it), then raises <see cref="Reloaded" />. No-op on WASM (no filesystem).
    ///     Not called at construction — the app calls it once at startup (before <see cref="Install" />) and again
    ///     from the Settings "Reload themes" affordance.
    /// </summary>
    public void Reload()
    {
        ClearUserThemes();
        LoadUserThemes();
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    // Remove every user drop-in theme (from the ordered list, the id lookup, and the merged override dicts) so a
    // reload reflects deletions/renames rather than accumulating stale variants.
    private void ClearUserThemes()
    {
        foreach (Theme user in _ordered.Where(t => t.Source == ThemeSource.User).ToList())
        {
            _ordered.Remove(user);
            _byId.Remove(user.Id);
            _customDicts.ThemeDictionaries.Remove(user.Variant);
        }
    }

    private void LoadUserThemes()
    {
        string? dir = AppPaths.ThemesDirectory;
        if (dir is null || !Directory.Exists(dir))
        {
            return; // WASM, or the directory could not be created — no drop-ins
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*.json");
        }
        catch (IOException)
        {
            return; // an inaccessible directory yields no drop-ins rather than throwing at startup
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // Deterministic order (by filename) so the picker list is stable across reloads.
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            ThemeDefinition? def = ThemeJson.TryParse(json, file);
            if (def is null)
            {
                continue; // malformed — skip, keep the rest
            }

            // A user file must not shadow a built-in id (that would turn a native theme into a custom variant
            // and could surprise the user). Only ids not already claimed by a BUILT-IN are accepted; a later
            // user file with the same id as an earlier user file replaces it (last write wins).
            if (_byId.TryGetValue(def.Id, out Theme? existing) && existing.Source == ThemeSource.BuiltIn)
            {
                continue;
            }

            Register(def, ThemeSource.User);
        }
    }

    /// <summary>
    ///     Merges the custom-theme override dictionaries into the app so their variants resolve. Added AFTER the
    ///     base palette so — merged dictionaries being searched last-first — a custom variant's own token wins,
    ///     and anything it omits falls through to the base palette via the variant's InheritVariant. Call once.
    /// </summary>
    public void Install(Application app)
    {
        if (_installed)
        {
            return;
        }

        app.Resources.MergedDictionaries.Add(_customDicts);
        _installed = true;
    }

    /// <summary>Removes the merged override dictionaries (test cleanup / teardown).</summary>
    public void Uninstall(Application app)
    {
        if (_installed)
        {
            app.Resources.MergedDictionaries.Remove(_customDicts);
            _installed = false;
        }
    }

    /// <summary>The <see cref="ThemeVariant" /> for a theme id, or <c>Default</c> (System) for an unknown id.</summary>
    public ThemeVariant VariantFor(string? id) =>
        id is not null && _byId.TryGetValue(id, out Theme? t) ? t.Variant : ThemeVariant.Default;

    /// <summary>True when <paramref name="id" /> is a known theme.</summary>
    public bool Contains(string? id) => id is not null && _byId.ContainsKey(id);
}
