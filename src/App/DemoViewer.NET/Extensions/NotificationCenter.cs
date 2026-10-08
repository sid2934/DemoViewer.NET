#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Features;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The notifications every extension posts, for the session. A post from any thread lands in a small
///     per-extension buffer and one coalesced UI-thread drain applies them, so a flood costs one dispatcher
///     item and never more than <see cref="PerExtensionCap" /> cards for its extension. Cards show only while
///     their extension's feature is on.
/// </summary>
internal sealed class NotificationCenter
{
    /// <summary>The most cards one extension holds; past it the oldest goes.</summary>
    public const int PerExtensionCap = 3;

    // Pending operations per extension between drains. Dismisses are kept too, so bound the whole list.
    private const int PendingCap = PerExtensionCap * 4;

    private readonly object _gate = new();
    private readonly Action<Action> _toUiThread;
    private readonly IFeatureGate? _features;
    private readonly TimeProvider _time;
    private readonly List<Source> _sources = [];
    private ITimer? _expiry;
    private bool _drainPending;

    /// <param name="toUiThread">Runs a drain on the UI thread. Called at most once per drain.</param>
    /// <param name="features">The gate a card's feature is read from; null shows every card.</param>
    /// <param name="time">The clock a time to live counts on.</param>
    public NotificationCenter(Action<Action> toUiThread, IFeatureGate? features = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(toUiThread);
        _toUiThread = toUiThread;
        _features = features;
        _time = time ?? TimeProvider.System;
        if (features is not null)
        {
            features.Changed += (_, _) => _toUiThread(Recheck);
        }
    }

    /// <summary>The shown cards, newest first. Changed on the UI thread only.</summary>
    public ObservableCollection<NotificationCardViewModel> Cards { get; } = [];

    /// <summary>How many drains were posted to the UI thread. A flood should move this by one.</summary>
    internal int DrainsPosted { get; private set; }

    /// <summary>The extension's handle on the center, stamped with its id, name and feature.</summary>
    public IExtensionNotifications For(ExtensionGuard guard, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        Source source = new(this, guard, log);
        lock (_gate)
        {
            _sources.Add(source);
        }

        return source;
    }

    private bool IsOn(string featureId) => _features?.IsEnabled(featureId) ?? true;

    private void ScheduleDrain()
    {
        lock (_gate)
        {
            if (_drainPending)
            {
                return;
            }

            _drainPending = true;
            DrainsPosted++;
        }

        _toUiThread(Drain);
    }

    /// <summary>Applies every pending post and dismiss. UI thread.</summary>
    internal void Drain()
    {
        List<(Source Source, List<Pending> Ops)> work = [];
        lock (_gate)
        {
            _drainPending = false;
            foreach (Source source in _sources)
            {
                if (source.TakePending() is { } ops)
                {
                    work.Add((source, ops));
                }
            }
        }

        foreach ((Source source, List<Pending> ops) in work)
        {
            bool on = IsOn(source.FeatureId);
            foreach (Pending op in ops)
            {
                if (op.Notification is { } notification)
                {
                    if (on)
                    {
                        Show(source, notification);
                    }
                }
                else
                {
                    Remove(source, op.Id);
                }
            }
        }

        ArmExpiry();
    }

    private void Show(Source source, Notification notification)
    {
        DateTimeOffset? expires = notification.TimeToLive is { } ttl && ttl > TimeSpan.Zero ? _time.GetUtcNow() + ttl : null;
        if (Cards.FirstOrDefault(c => c.Owner == source && c.Id == notification.Id) is { } shown)
        {
            shown.Apply(notification, expires);
            return;
        }

        Cards.Insert(0, new NotificationCardViewModel(this, source, notification, expires));
        NotificationCardViewModel[] own = [.. Cards.Where(c => c.Owner == source)];
        for (int i = PerExtensionCap; i < own.Length; i++)
        {
            Cards.Remove(own[i]);
        }
    }

    private void Remove(Source source, string id)
    {
        if (Cards.FirstOrDefault(c => c.Owner == source && c.Id == id) is { } card)
        {
            Cards.Remove(card);
        }
    }

    /// <summary>Closes a card. UI thread.</summary>
    internal void Close(NotificationCardViewModel card) => Cards.Remove(card);

    /// <summary>Closes every card whose extension's feature is off. UI thread.</summary>
    internal void Recheck()
    {
        foreach (NotificationCardViewModel card in Cards.ToArray())
        {
            if (!IsOn(card.Owner.FeatureId))
            {
                Cards.Remove(card);
            }
        }
    }

    /// <summary>Closes every card whose time to live ran out. UI thread.</summary>
    internal void Expire()
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (NotificationCardViewModel card in Cards.ToArray())
        {
            if (card.ExpiresAt is { } at && at <= now)
            {
                Cards.Remove(card);
            }
        }

        ArmExpiry();
    }

    // One timer, set for the soonest expiry; it fires onto the UI thread and re-arms from there.
    private void ArmExpiry()
    {
        DateTimeOffset? next = Cards.Where(c => c.ExpiresAt is not null).Select(c => c.ExpiresAt).Min();
        if (next is not { } at)
        {
            _expiry?.Dispose();
            _expiry = null;
            return;
        }

        TimeSpan due = at - _time.GetUtcNow();
        if (due < TimeSpan.Zero)
        {
            due = TimeSpan.Zero;
        }

        if (_expiry is null)
        {
            _expiry = _time.CreateTimer(_ => _toUiThread(Expire), null, due, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _expiry.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    internal readonly record struct Pending(string Id, Notification? Notification);

    /// <summary>One extension's notifications. Every member is safe from any thread and never throws.</summary>
    internal sealed class Source(NotificationCenter center, ExtensionGuard guard, ILogger? log) : IExtensionNotifications
    {
        private readonly List<Pending> _pending = [];
        private int _dropped;
        private bool _droppedLogged;

        public ExtensionGuard Guard { get; } = guard;

        public string FeatureId => Guard.Scope.FeatureId;

        public string Name => Guard.Scope.Name;

        public void Post(Notification notification)
        {
            if (notification is null || string.IsNullOrWhiteSpace(notification.Id) || string.IsNullOrWhiteSpace(notification.Title))
            {
                Log(() => NotificationLog.Malformed(log));
                return;
            }

            Enqueue(new Pending(notification.Id, notification));
        }

        public void Dismiss(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            Enqueue(new Pending(id, null));
        }

        private void Enqueue(Pending op)
        {
            bool dropped = false;
            lock (center._gate)
            {
                _pending.RemoveAll(p => p.Id == op.Id);
                _pending.Add(op);
                while (_pending.Count(p => p.Notification is not null) > PerExtensionCap)
                {
                    _pending.RemoveAt(_pending.FindIndex(p => p.Notification is not null));
                    dropped = true;
                }

                while (_pending.Count > PendingCap)
                {
                    dropped |= _pending[0].Notification is not null;
                    _pending.RemoveAt(0);
                }

                if (dropped)
                {
                    _dropped++;
                }
            }

            if (dropped)
            {
                LogDroppedOnce();
            }

            center.ScheduleDrain();
        }

        // Called under the center's lock.
        public List<Pending>? TakePending()
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            List<Pending> ops = [.. _pending];
            _pending.Clear();
            return ops;
        }

        // A flood is not a fault: one line per session says the cap is working.
        private void LogDroppedOnce()
        {
            lock (center._gate)
            {
                if (_droppedLogged)
                {
                    return;
                }

                _droppedLogged = true;
            }

            Log(() => NotificationLog.Capped(log, PerExtensionCap));
        }

        private static void Log(Action write)
        {
            try
            {
                write();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A logger that throws must not reach the extension's post.
            }
        }

        /// <summary>How many posts dropped something to the cap before it was shown.</summary>
        internal int Dropped => _dropped;
    }
}

internal static partial class NotificationLog
{
    public static void Malformed(ILogger? logger)
    {
        if (logger is not null)
        {
            MalformedCore(logger);
        }
    }

    public static void Capped(ILogger? logger, int cap)
    {
        if (logger is not null)
        {
            CappedCore(logger, cap);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notification without an id or a title was dropped.")]
    private static partial void MalformedCore(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifications are posted faster than they are shown; past {Cap} the oldest are dropped.")]
    private static partial void CappedCore(ILogger logger, int cap);
}

/// <summary>One notification card in the stack above the status strip.</summary>
public sealed partial class NotificationCardViewModel : ObservableObject
{
    private readonly NotificationCenter _center;
    private NotificationAction? _action;

    internal NotificationCardViewModel(NotificationCenter center, NotificationCenter.Source owner, Notification notification,
        DateTimeOffset? expiresAt)
    {
        _center = center;
        Owner = owner;
        Id = notification.Id;
        SourceName = owner.Name;
        Apply(notification, expiresAt);
    }

    internal NotificationCenter.Source Owner { get; }

    /// <summary>The extension's id for the notification.</summary>
    public string Id { get; }

    /// <summary>The extension that posted it, stamped by the host.</summary>
    public string SourceName { get; }

    /// <summary>How the card is tinted: info, success, warning or error.</summary>
    [ObservableProperty] private NotificationSeverity _severity;

    /// <summary>The card's one-line headline.</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>The detail under the title, or null for a title-only card.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBody))]
    private string? _body;

    /// <summary>The action button's label, or null when the card has no action.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAction))]
    private string? _actionLabel;

    /// <summary>When the host closes it, or null to keep it.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>True when <see cref="Body" /> has text to show.</summary>
    public bool HasBody => !string.IsNullOrEmpty(Body);

    /// <summary>True when the card shows an action button.</summary>
    public bool HasAction => ActionLabel is not null;

    /// <summary>True for an <see cref="NotificationSeverity.Info" /> card.</summary>
    public bool IsInfo => Severity == NotificationSeverity.Info;

    /// <summary>True for a <see cref="NotificationSeverity.Success" /> card.</summary>
    public bool IsSuccess => Severity == NotificationSeverity.Success;

    /// <summary>True for a <see cref="NotificationSeverity.Warning" /> card.</summary>
    public bool IsWarning => Severity == NotificationSeverity.Warning;

    /// <summary>True for an <see cref="NotificationSeverity.Error" /> card.</summary>
    public bool IsError => Severity == NotificationSeverity.Error;

    partial void OnSeverityChanged(NotificationSeverity value)
    {
        OnPropertyChanged(nameof(IsInfo));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsError));
    }

    internal void Apply(Notification notification, DateTimeOffset? expiresAt)
    {
        Severity = notification.Severity;
        Title = notification.Title;
        Body = notification.Body;
        _action = notification.Action;
        ActionLabel = notification.Action is { } action ? string.IsNullOrWhiteSpace(action.Label) ? "Open" : action.Label : null;
        ExpiresAt = expiresAt;
    }

    /// <summary>Runs the extension's action under its guard, then closes the card.</summary>
    [RelayCommand]
    private void RunAction()
    {
        if (_action?.Run is { } run)
        {
            Owner.Guard.Run("notification action", run);
        }

        _center.Close(this);
    }

    /// <summary>Closes the card without running its action.</summary>
    [RelayCommand]
    private void Dismiss() => _center.Close(this);
}
