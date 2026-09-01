#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DemoViewer.NET.ViewModels.Tutorial;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Tags a control as the live anchor for a walkthrough <see cref="TutorialTarget" /> region, so the
///     tour overlay can spotlight it. Set in XAML, <c>controls:TutorialAnchor.Target="StatsContent"</c>, on
///     the coarse region a step points at (a tab's content root, the Open-Demo button, the NavStrip transport
///     cluster). The overlay resolves the target to this control and measures its on-screen rectangle.
///     <para>
///         <b>Lifecycle-correct by design.</b> Registration happens on <c>AttachedToVisualTree</c>
///         and unregisters on detach, NOT at construction. Inactive workspace tabs unload their content
///         (<c>WorkspaceTabDescriptor</c> drops the realized view on deactivation), so a construction-time
///         registry would fill with dead references; attach/detach keeps the registry pointing only at the
///         control currently live in the tree. Only one content tab is active at a time, so at most one control
///         is registered per target.
///     </para>
/// </summary>
public sealed class TutorialAnchor
{
    /// <summary>The region this control anchors. <see cref="TutorialTarget.None" /> (default) = not an anchor.</summary>
    public static readonly AttachedProperty<TutorialTarget> TargetProperty =
        AvaloniaProperty.RegisterAttached<TutorialAnchor, Control, TutorialTarget>("Target");

    // The currently-attached anchor control per target (weak, so it never keeps a torn-down view alive).
    private static readonly Dictionary<TutorialTarget, WeakReference<Control>> Registry = new();

    static TutorialAnchor()
    {
        TargetProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            control.AttachedToVisualTree -= OnAttached;
            control.DetachedFromVisualTree -= OnDetached;

            if (GetTarget(control) == TutorialTarget.None)
            {
                return;
            }

            control.AttachedToVisualTree += OnAttached;
            control.DetachedFromVisualTree += OnDetached;

            // If the property is set after the control is already in the tree, register now.
            if (control.GetVisualRoot() is not null)
            {
                Register(GetTarget(control), control);
            }
        });
    }

    private TutorialAnchor()
    {
    }

    /// <summary>XAML setter for <see cref="TargetProperty" />.</summary>
    public static void SetTarget(Control control, TutorialTarget value) => control.SetValue(TargetProperty, value);

    /// <summary>Reads <see cref="TargetProperty" />.</summary>
    public static TutorialTarget GetTarget(Control control) => control.GetValue(TargetProperty);

    /// <summary>
    ///     Resolves the live control currently anchoring <paramref name="target" />, or false when none is in
    ///     the visual tree (e.g. the target's tab isn't active). The overlay measures the returned control.
    /// </summary>
    public static bool TryResolve(TutorialTarget target, out Control control)
    {
        control = null!;
        if (target != TutorialTarget.None
            && Registry.TryGetValue(target, out WeakReference<Control>? weak)
            && weak.TryGetTarget(out Control? live)
            && live.GetVisualRoot() is not null)
        {
            control = live;
            return true;
        }

        return false;
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control c)
        {
            Register(GetTarget(c), c);
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control c)
        {
            Unregister(GetTarget(c), c);
        }
    }

    private static void Register(TutorialTarget target, Control control)
    {
        if (target != TutorialTarget.None)
        {
            Registry[target] = new WeakReference<Control>(control);
        }
    }

    private static void Unregister(TutorialTarget target, Control control)
    {
        // Only clear if THIS control is the registered one (a fast tab flip could register the new before the
        // old detaches; last-writer-wins on register, and this guard avoids the old detach clearing the new).
        if (Registry.TryGetValue(target, out WeakReference<Control>? weak)
            && weak.TryGetTarget(out Control? cur)
            && ReferenceEquals(cur, control))
        {
            Registry.Remove(target);
        }
    }
}
