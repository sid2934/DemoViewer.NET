#region

using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     A location field (docs/ui/design-system.md, "Location field"): shows callouts, stores canonical places, accepts
///     free text, and while focused lists the map's callouts, filtered as you type, above or below the field as room
///     allows. <see cref="IsMulti" /> makes it a list: one chip per entry, in order, each removable and movable, over an
///     add field that appends. The pick button runs <see cref="PickCommand" />, which arms the canvas for this field.
///     Logic lives in <see cref="PlaceFieldModel" />.
/// </summary>
public partial class PlaceField : UserControl
{
    /// <summary>The list's height limit; it opens up when less than this fits below and more fits above.</summary>
    public const double MaxListHeight = 200;

    // ListBoxItem MinHeight 22 plus the border: enough to decide a side without measuring the popup.
    private const double RowHeight = 22;

    public static readonly StyledProperty<IReadOnlyList<PlaceRef>?> ValueProperty =
        AvaloniaProperty.Register<PlaceField, IReadOnlyList<PlaceRef>?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<CalloutResolver?> CalloutsProperty =
        AvaloniaProperty.Register<PlaceField, CalloutResolver?>(nameof(Callouts));

    public static readonly StyledProperty<bool> IsMultiProperty = AvaloniaProperty.Register<PlaceField, bool>(nameof(IsMulti));

    public static readonly StyledProperty<ICommand?> PickCommandProperty =
        AvaloniaProperty.Register<PlaceField, ICommand?>(nameof(PickCommand));

    public static readonly StyledProperty<object?> PickCommandParameterProperty =
        AvaloniaProperty.Register<PlaceField, object?>(nameof(PickCommandParameter));

    public static readonly StyledProperty<bool> IsPickingProperty = AvaloniaProperty.Register<PlaceField, bool>(nameof(IsPicking));

    public static readonly StyledProperty<object?> ArmedTargetProperty = AvaloniaProperty.Register<PlaceField, object?>(nameof(ArmedTarget));

    public static readonly StyledProperty<double?> CurrentLevelMinZProperty =
        AvaloniaProperty.Register<PlaceField, double?>(nameof(CurrentLevelMinZ));

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<PlaceField, string?>(nameof(PlaceholderText));

    public static readonly DirectProperty<PlaceField, bool> IsDropDownOpenProperty =
        AvaloniaProperty.RegisterDirect<PlaceField, bool>(nameof(IsDropDownOpen), f => f.IsDropDownOpen);

    private bool _isDropDownOpen;
    private TopLevel? _keysFrom;

    // The key the open list took, so the field's own handler does not act on it again once the list has closed.
    private KeyEventArgs? _listKey;
    private PlaceFieldModel _model = new();
    private bool _syncing;

    // What the chips were built from, so typing does not rebuild them.
    private IReadOnlyList<PlaceRef>? _chipsOf;
    private CalloutResolver? _chipsBy;

    public PlaceField()
    {
        InitializeComponent();
        _model.Changed += Sync;

        FieldBox.GotFocus += (_, _) => OpenList();
        FieldBox.LostFocus += (_, _) =>
        {
            CommitText();
            if (!IsDropDownOpen)
            {
                UnhookKeys();
            }
        };
        FieldBox.TextChanged += OnTextChanged;
        FieldBox.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Bubble, true);
        OptionList.AddHandler(PointerPressedEvent, OnListPressed, RoutingStrategies.Tunnel);
        ClearButton.Click += (_, _) => Store(_model.Clear());
        PickButton.Click += (_, _) => RunPick();
    }

    /// <summary>The stored locations: at most one unless <see cref="IsMulti" />. Written on a commit, a pick from the list or a clear.</summary>
    public IReadOnlyList<PlaceRef>? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The owner's words over the map's places; the list and the typed-text resolution come from it.</summary>
    public CalloutResolver? Callouts
    {
        get => GetValue(CalloutsProperty);
        set => SetValue(CalloutsProperty, value);
    }

    /// <summary>A list of locations (watching, via, lurk areas) shown as chips, rather than one.</summary>
    public bool IsMulti
    {
        get => GetValue(IsMultiProperty);
        set => SetValue(IsMultiProperty, value);
    }

    /// <summary>Arms the map for this field; the button is hidden without one.</summary>
    public ICommand? PickCommand
    {
        get => GetValue(PickCommandProperty);
        set => SetValue(PickCommandProperty, value);
    }

    public object? PickCommandParameter
    {
        get => GetValue(PickCommandParameterProperty);
        set => SetValue(PickCommandParameterProperty, value);
    }

    /// <summary>True while a map click will write this field: the pick button shows it, and Esc here cancels it.</summary>
    public bool IsPicking
    {
        get => GetValue(IsPickingProperty);
        set => SetValue(IsPickingProperty, value);
    }

    /// <summary>What the map is armed for, if anything: <see cref="IsPicking" /> follows it equalling <see cref="PickCommandParameter" />.</summary>
    public object? ArmedTarget
    {
        get => GetValue(ArmedTargetProperty);
        set => SetValue(ArmedTargetProperty, value);
    }

    /// <summary>The level key a typed coordinate takes when the field holds no point to take it from.</summary>
    public double? CurrentLevelMinZ
    {
        get => GetValue(CurrentLevelMinZProperty);
        set => SetValue(CurrentLevelMinZProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Whether the callout list is showing.</summary>
    public bool IsDropDownOpen
    {
        get => _isDropDownOpen;
        private set => SetAndRaise(IsDropDownOpenProperty, ref _isDropDownOpen, value);
    }

    /// <summary>Which side the list opened on: true for above the field.</summary>
    public bool OpensUp { get; private set; }

    /// <summary>The field's logic, for tests.</summary>
    internal PlaceFieldModel Model => _model;

    /// <summary>A multi field's chips, in order.</summary>
    internal IReadOnlyList<Border> Chips => [.. ChipPanel.Children.OfType<Border>()];

    /// <summary>
    ///     Whether the list opens above the field: when the rows it wants do not fit below and there is more room
    ///     above than below.
    /// </summary>
    /// <param name="above">Room above the field.</param>
    /// <param name="below">Room below it.</param>
    /// <param name="wanted">The list's height.</param>
    public static bool ChooseUp(double above, double below, double wanted) => below < wanted && above > below;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsMultiProperty)
        {
            _model.Changed -= Sync;
            _model = new PlaceFieldModel(IsMulti) { Options = _model.Options, CurrentLevelMinZ = CurrentLevelMinZ };
            _model.Changed += Sync;
            _model.Load(Value);
        }
        else if (change.Property == CalloutsProperty)
        {
            _model.Options = Callouts is { } callouts ? PlaceFieldOptions.For(callouts) : null;
            if (!FieldBox.IsFocused)
            {
                _model.Load(Value);
            }
        }
        else if (change.Property == ValueProperty && IsMulti)
        {
            // Even while storing: the written list comes back as the document holds it.
            _model.Reload(Value);
        }
        else if (change.Property == ValueProperty && !_syncing && (!FieldBox.IsFocused || !_model.HasPendingEdit))
        {
            _model.Load(Value);
        }
        else if (change.Property == PickCommandProperty)
        {
            PickButton.IsVisible = PickCommand is not null;
        }
        else if (change.Property == ArmedTargetProperty || change.Property == PickCommandParameterProperty)
        {
            SetCurrentValue(IsPickingProperty, ArmedTarget is not null && Equals(ArmedTarget, PickCommandParameter));
        }
        else if (change.Property == IsPickingProperty)
        {
            PseudoClasses.Set(":picking", IsPicking);
        }
        else if (change.Property == CurrentLevelMinZProperty)
        {
            _model.CurrentLevelMinZ = CurrentLevelMinZ;
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            FieldBox.PlaceholderText = PlaceholderText;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _model.Dismiss();
        UnhookKeys();
        base.OnDetachedFromVisualTree(e);
    }

    private void OpenList()
    {
        if (!_syncing)
        {
            _model.Load(Value);
            _model.Open();
        }

        if (IsMulti)
        {
            HookKeys();
        }
    }

    private void CommitText()
    {
        if (_model.Commit() is { } value)
        {
            Store(value);
        }
    }

    // Only a change is written: a pick of the stored callout leaves the bound value alone.
    private void Store(IReadOnlyList<PlaceRef> value)
    {
        IReadOnlyList<PlaceRef> stored = Value ?? [];
        if (stored.Count == value.Count && stored.Zip(value).All(p => StratLocations.Same(p.First, p.Second)))
        {
            return;
        }

        _syncing = true;
        try
        {
            SetCurrentValue(ValueProperty, value);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RunPick()
    {
        if (PickCommand is { } command && command.CanExecute(PickCommandParameter))
        {
            command.Execute(PickCommandParameter);
        }
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_syncing && FieldBox.IsFocused && !string.Equals(FieldBox.Text ?? "", _model.Text, StringComparison.Ordinal))
        {
            _model.Type(FieldBox.Text);
        }
    }

    // Closed list: Down opens it; Esc while this field is being picked on the map cancels the pick. The row's own
    // Esc handler runs first and marks the key handled, so this listens to handled keys too.
    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsDropDownOpen || e.KeyModifiers != KeyModifiers.None || ReferenceEquals(e, _listKey))
        {
            return;
        }

        if (e.Key == Key.Down && !e.Handled)
        {
            _model.Open(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsPicking)
        {
            RunPick();
        }
    }

    // While the list is open its keys are taken at the window, before any ancestor's tunnel handler: a step row adds a
    // step on Enter and leaves the field on Esc, and neither may see a key meant for the list. A multi field's add
    // field also takes Backspace when empty, and Enter on typed text with no list showing.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!FieldBox.IsFocused || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (IsMulti && e.Key == Key.Back && _model.Text.Length == 0 && Chips.Count > 0)
        {
            _model.Dismiss();
            Chips[^1].Focus(NavigationMethod.Directional);
            e.Handled = true;
            return;
        }

        if (IsMulti && !IsDropDownOpen && e.Key == Key.Enter && _model.HasPendingEdit && _model.Text.Trim().Length > 0)
        {
            CommitText();
            _listKey = e;
            e.Handled = true;
            return;
        }

        if (!IsDropDownOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                _model.Move(-1);
                break;
            case Key.Down:
                _model.Move(1);
                break;
            // Enter and Esc are the list's only once the user has typed or moved in it; before that they are the host's.
            case Key.Enter when !_model.HasChoice:
                _model.Dismiss();
                return;
            case Key.Enter:
                if (_model.Accept() is { } picked)
                {
                    Store(picked);
                }
                else
                {
                    CommitText();
                }

                break;
            case Key.Escape when !_model.HasChoice:
                _model.Dismiss();
                return;
            case Key.Escape:
                _model.Dismiss();
                break;
            default:
                return;
        }

        _listKey = e;
        e.Handled = true;
    }

    private void OnListPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.DataContext is PlaceOption)
            ?.DataContext is PlaceOption option)
        {
            Store(_model.Choose(option));
            e.Handled = true;
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            if (!string.Equals(FieldBox.Text ?? "", _model.Text, StringComparison.Ordinal))
            {
                FieldBox.Text = _model.Text;
                // Only while focused: moving the caret of an unfocused box scrolls the editor to it.
                if (FieldBox.IsFocused)
                {
                    FieldBox.CaretIndex = _model.Text.Length;
                }
            }

            ClearButton.IsVisible = _model.HasPointOnly;
            BuildChips();
            if (!ReferenceEquals(OptionList.ItemsSource, _model.Items))
            {
                OptionList.ItemsSource = _model.Items;
            }

            OptionList.SelectedIndex = _model.Highlight;
            if (_model.Highlight >= 0 && _model.Highlight < _model.Items.Count)
            {
                OptionList.ScrollIntoView(_model.Highlight);
            }

            bool open = _model.IsOpen && _model.Items.Count > 0;
            if (open && !DropDown.IsOpen)
            {
                PlaceList();
                Avalonia.Threading.Dispatcher.UIThread.Post(Replace, Avalonia.Threading.DispatcherPriority.Loaded);
            }

            DropDown.IsOpen = open;
            IsDropDownOpen = open;
            if (open || (IsMulti && FieldBox.IsFocused))
            {
                HookKeys();
            }
            else
            {
                UnhookKeys();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void BuildChips()
    {
        IReadOnlyList<PlaceRef> value = IsMulti ? _model.Value : [];
        CalloutResolver? callouts = _model.Options?.Resolver;
        if (ReferenceEquals(value, _chipsOf) && ReferenceEquals(callouts, _chipsBy))
        {
            return;
        }

        _chipsOf = value;
        _chipsBy = callouts;
        ChipPanel.Children.Clear();
        for (int i = 0; i < value.Count; i++)
        {
            ChipPanel.Children.Add(Chip(i, PlaceFieldModel.DisplayOf([value[i]], callouts)));
        }

        ChipPanel.IsVisible = value.Count > 0;
    }

    private Border Chip(int index, string text)
    {
        Button remove = new()
        {
            Content = "✕", Focusable = false, IsTabStop = false, Classes = { "placeFieldBtn" }
        };
        AutomationProperties.SetName(remove, "Remove " + text);
        ToolTip.SetTip(remove, "Remove");
        remove.Click += (_, _) => RemoveChip(index, false);
        DockPanel.SetDock(remove, Dock.Right);

        Border chip = new()
        {
            Classes = { "placeChip" }, Focusable = true, IsTabStop = false,
            Child = new DockPanel { Children = { remove, new TextBlock { Text = text } } }
        };
        AutomationProperties.SetName(chip, text);
        ToolTip.SetTip(chip, text + "\nAlt+Left or Alt+Right moves it, Backspace removes it");
        chip.KeyDown += (_, e) => OnChipKeyDown(index, e);
        chip.PointerPressed += (_, e) =>
        {
            if (e.Source is not Visual v || v.FindAncestorOfType<Button>(true) is null)
            {
                chip.Focus(NavigationMethod.Pointer);
            }
        };
        chip.ContextMenu = new ContextMenu
        {
            Items =
            {
                MenuEntry("Move left", new KeyGesture(Key.Left, KeyModifiers.Alt), index > 0, () => ShiftChip(index, -1)),
                MenuEntry("Move right", new KeyGesture(Key.Right, KeyModifiers.Alt), index < _model.Value.Count - 1, () => ShiftChip(index, 1)),
                MenuEntry("Remove", new KeyGesture(Key.Back), true, () => RemoveChip(index, false))
            }
        };
        return chip;
    }

    private static MenuItem MenuEntry(string header, KeyGesture gesture, bool enabled, Action run)
    {
        MenuItem item = new() { Header = header, InputGesture = gesture, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    // A chip has the focus after Backspace in the empty add field, a click, or Left from the next chip.
    private void OnChipKeyDown(int index, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.Right when e.KeyModifiers == KeyModifiers.Alt:
                ShiftChip(index, e.Key == Key.Left ? -1 : 1);
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.None:
                Chips[Math.Max(0, index - 1)].Focus(NavigationMethod.Directional);
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.None:
                if (index + 1 < Chips.Count)
                {
                    Chips[index + 1].Focus(NavigationMethod.Directional);
                }
                else
                {
                    FieldBox.Focus(NavigationMethod.Directional);
                }

                break;
            case Key.Back or Key.Delete when e.KeyModifiers == KeyModifiers.None:
                RemoveChip(index, true);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void RemoveChip(int index, bool byKey)
    {
        Store(_model.Remove(index));
        if (byKey)
        {
            Dispatcher.UIThread.Post(() => FieldBox.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
        }
    }

    private void ShiftChip(int index, int delta)
    {
        if (_model.Shift(index, delta) is not { } moved)
        {
            return;
        }

        Store(moved);
        int to = index + delta;
        Dispatcher.UIThread.Post(() =>
        {
            if (to < Chips.Count)
            {
                Chips[to].Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
    }

    // A field focused by Tab is scrolled into view after it opened its list: choose the side again once it has moved.
    private void Replace()
    {
        if (!DropDown.IsOpen)
        {
            return;
        }

        bool up = OpensUp;
        PlaceList();
        if (up != OpensUp)
        {
            DropDown.IsOpen = false;
            DropDown.IsOpen = true;
        }

        if (_model.Highlight >= 0 && _model.Highlight < _model.Items.Count)
        {
            OptionList.ScrollIntoView(_model.Highlight);
        }
    }

    private void PlaceList()
    {
        DropDown.MinWidth = FieldBox.Bounds.Width;
        if (TopLevel.GetTopLevel(this) is not { } top || FieldBox.TranslatePoint(new Point(0, 0), top) is not { } at)
        {
            OpensUp = false;
        }
        else
        {
            double wanted = Math.Min(MaxListHeight, _model.Items.Count * RowHeight + 2);
            OpensUp = ChooseUp(at.Y, top.Bounds.Height - at.Y - FieldBox.Bounds.Height, wanted);
        }

        DropDown.Placement = OpensUp ? PlacementMode.TopEdgeAlignedLeft : PlacementMode.BottomEdgeAlignedLeft;
    }

    private void HookKeys()
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (ReferenceEquals(top, _keysFrom))
        {
            return;
        }

        UnhookKeys();
        _keysFrom = top;
        _keysFrom?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void UnhookKeys()
    {
        _keysFrom?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _keysFrom = null;
    }
}
