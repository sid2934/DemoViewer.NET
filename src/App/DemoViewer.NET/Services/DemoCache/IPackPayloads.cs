#region

using System.Text.Json;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     Typed access to one pack's payload on the demo cache records (<see cref="DemoCacheRecord.Packs" />),
///     obtained from <see cref="DemoCacheStore.Payloads" />. Core keeps the payload as an opaque JSON object
///     keyed by pack id; the pack reads and writes it as its own type through this, serialised with the same
///     options as the record so a payload round-trips exactly as any other member does.
///     <para>
///         The record overloads work on a record the caller already holds, which is how a tier fill writes
///         its payload and its <see cref="PackStamp" /> in the one <see cref="DemoCacheStore.UpdateExisting" />;
///         the path overloads load and, for a write, save the record themselves.
///     </para>
/// </summary>
public interface IPackPayloads
{
    /// <summary>The pack whose payload this reads and writes.</summary>
    string PackId { get; }

    /// <summary>The pack's payload on <paramref name="record" />, or null when it has none or it does not read as <typeparamref name="T" />.</summary>
    /// <param name="record">A record the caller holds.</param>
    T? Read<T>(DemoCacheRecord record) where T : class;

    /// <summary>The pack's payload on a demo's record, or null when the demo is not cached or has none.</summary>
    /// <param name="demoPath">The demo's path.</param>
    T? Read<T>(string demoPath) where T : class;

    /// <summary>Replaces the pack's payload on <paramref name="record" />; null removes it. The caller saves the record.</summary>
    /// <param name="record">A record the caller is about to save.</param>
    /// <param name="payload">The whole payload.</param>
    void Write<T>(DemoCacheRecord record, T? payload) where T : class;

    /// <summary>Loads a demo's record, replaces the pack's payload and saves it (<see cref="DemoCacheStore.UpdateExisting" />).</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="payload">The whole payload; null removes it.</param>
    void Write<T>(string demoPath, T? payload) where T : class;
}

/// <summary>The store's <see cref="IPackPayloads" />: one instance per pack id, stateless beyond the two.</summary>
internal sealed class PackPayloads(DemoCacheStore store, string packId, JsonSerializerOptions options) : IPackPayloads
{
    /// <inheritdoc />
    public string PackId { get; } = packId;

    /// <inheritdoc />
    public T? Read<T>(DemoCacheRecord record) where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.Packs.TryGetValue(PackId, out JsonElement element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>(options);
        }
        catch (JsonException)
        {
            // A payload the pack cannot read is "not cached", the rule every sidecar reader follows.
            return null;
        }
    }

    /// <inheritdoc />
    public T? Read<T>(string demoPath) where T : class =>
        store.TryLoadRecord(demoPath) is { } record ? Read<T>(record) : null;

    /// <inheritdoc />
    public void Write<T>(DemoCacheRecord record, T? payload) where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        if (payload is null)
        {
            record.Packs.Remove(PackId);
            return;
        }

        record.Packs[PackId] = JsonSerializer.SerializeToElement(payload, options);
    }

    /// <inheritdoc />
    public void Write<T>(string demoPath, T? payload) where T : class =>
        store.UpdateExisting(demoPath, record => Write(record, payload));
}
