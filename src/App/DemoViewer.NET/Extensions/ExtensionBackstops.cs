#region

using System.Text;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Threading;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The last line behind the call-site guards: an exception that reaches the UI dispatcher, a faulted task
///     nobody observed, and a binding error, each attributed to the extension whose code is on its stack (or
///     whose view model or control it came from). An attributed dispatcher exception is reported and marked
///     handled, and the switch-off removes the content that threw; anything the host threw itself is left
///     exactly as before, so it still crashes into safe mode. A binding error is logged against the
///     extension and never counted.
/// </summary>
public static class ExtensionBackstops
{
    /// <summary>Installs the backstops for <paramref name="faults" />; disposing removes them.</summary>
    /// <param name="faults">Where attributed faults go.</param>
    /// <param name="dispatcher">The UI dispatcher; null for <see cref="Dispatcher.UIThread" />.</param>
    public static IDisposable Install(ExtensionFaults faults, Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(faults);
        dispatcher ??= Dispatcher.UIThread;

        // The filter runs before the stack unwinds: only an exception an extension threw is caught at all, so
        // the host's own crashes take the same path they always did.
        void Filter(object? sender, DispatcherUnhandledExceptionFilterEventArgs e) =>
            e.RequestCatch = faults.Attribute(e.Exception) is not null;

        void Handle(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (faults.Attribute(e.Exception) is { } scope)
            {
                faults.Report(scope, "UI thread", e.Exception);
                e.Handled = true;
            }
        }

        void Unobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (faults.Attribute(e.Exception) is { } scope)
            {
                faults.Report(scope, "background task", e.Exception);
                e.SetObserved();
            }
        }

        dispatcher.UnhandledExceptionFilter += Filter;
        dispatcher.UnhandledException += Handle;
        TaskScheduler.UnobservedTaskException += Unobserved;
        ILogSink? inner = Logger.Sink;
        ExtensionBindingSink sink = new(inner, faults);
        Logger.Sink = sink;

        return new Removal(() =>
        {
            dispatcher.UnhandledExceptionFilter -= Filter;
            dispatcher.UnhandledException -= Handle;
            TaskScheduler.UnobservedTaskException -= Unobserved;
            if (ReferenceEquals(Logger.Sink, sink))
            {
                Logger.Sink = inner;
            }
        });
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}

/// <summary>
///     Forwards every Avalonia log event to the sink it wraps, and logs a binding error from an extension's
///     view against that extension: by the bound element's data context type, else the element's own type.
/// </summary>
/// <param name="inner">The sink Avalonia had, or null.</param>
/// <param name="faults">Where extension binding errors go.</param>
public sealed class ExtensionBindingSink(ILogSink? inner, ExtensionFaults faults) : ILogSink
{
    /// <inheritdoc />
    public bool IsEnabled(LogEventLevel level, string area) =>
        IsBindingError(level, area) || (inner?.IsEnabled(level, area) ?? false);

    /// <inheritdoc />
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (inner?.IsEnabled(level, area) == true)
        {
            inner.Log(level, area, source, messageTemplate);
        }

        Attribute(level, area, source, messageTemplate, []);
    }

    /// <inheritdoc />
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (inner?.IsEnabled(level, area) == true)
        {
            inner.Log(level, area, source, messageTemplate, propertyValues);
        }

        Attribute(level, area, source, messageTemplate, propertyValues);
    }

    private static bool IsBindingError(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning && string.Equals(area, LogArea.Binding, StringComparison.Ordinal);

    private void Attribute(LogEventLevel level, string area, object? source, string template, object?[] values)
    {
        if (!IsBindingError(level, area))
        {
            return;
        }

        ExtensionScope? scope = source is StyledElement { DataContext: { } context }
            ? faults.Owner(context.GetType().Assembly)
            : null;
        scope ??= source is null ? null : faults.Owner(source.GetType().Assembly);
        if (scope is not null)
        {
            faults.ReportBinding(scope, Format(template, values));
        }
    }

    // Fills "{Name}" holes in order, the way Avalonia's own sinks render a template.
    private static string Format(string template, object?[] values)
    {
        StringBuilder text = new(template.Length + 32);
        int next = 0;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            int close = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (close > i && next < values.Length)
            {
                text.Append(values[next++]);
                i = close;
                continue;
            }

            text.Append(c);
        }

        return text.ToString();
    }
}
