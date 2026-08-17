#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cs2DemoKit.Parser.Entities;
using Cs2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>
///     Bottom-strip serializer / ServerClass schema view (F8.6). For the selected class
///     it lists each field's name, declared type, encoder, bit count and encode flags,
///     sourced from <see cref="EntityTracker.Schema" /> (the flattened-serializer schema).
/// </summary>
public sealed partial class SerializerSchemaViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _hasSchema;

    [ObservableProperty]
    private string _headerText = "Serializer schema";

    /// <summary>Fields.</summary>
    public ObservableCollection<SchemaFieldRow> Fields { get; } = [];

    /// <summary>Clear.</summary>
    public void Clear() => Show(null, null);

    /// <summary>Rebuilds the field table for <paramref name="className" /> (null clears it).</summary>
    public void Show(EntityTracker? tracker, string? className)
    {
        Fields.Clear();

        if (tracker?.Schema is not { } schema || string.IsNullOrEmpty(className))
        {
            HeaderText = "Serializer schema";
            HasSchema = false;
            return;
        }

        RuntimeSerializer? ser = schema.GetSerializer(className);
        if (ser is null)
        {
            HeaderText = $"{className} — no serializer in schema";
            HasSchema = false;
            return;
        }

        foreach (RuntimeField field in ser.Fields)
        {
            Fields.Add(new SchemaFieldRow
            {
                Name = field.Name,
                TypeName = field.TypeName,
                Encoder = field.Encoder ?? "",
                BitCount = field.BitCount,
                EncodeFlags = field.EncodeFlags,
                Shape = field.Shape.ToString()
            });
        }

        HeaderText = $"{ser.Name}  (v{ser.Version})  •  {ser.Fields.Length} fields";
        HasSchema = true;
    }
}

/// <summary>One serializer field row in the schema table.</summary>
public sealed class SchemaFieldRow
{
    /// <summary>Bit count.</summary>
    public int BitCount { get; init; }

    /// <summary>Bit count text.</summary>
    public string BitCountText => BitCount > 0 ? BitCount.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>Encode flags.</summary>
    public int EncodeFlags { get; init; }

    /// <summary>Encode flags text.</summary>
    public string EncodeFlagsText => EncodeFlags != 0 ? $"0x{EncodeFlags:X}" : "";

    /// <summary>Encoder.</summary>
    public string Encoder { get; init; } = "";

    /// <summary>Name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Shape.</summary>
    public string Shape { get; init; } = "";

    /// <summary>Type name.</summary>
    public string TypeName { get; init; } = "";
}
