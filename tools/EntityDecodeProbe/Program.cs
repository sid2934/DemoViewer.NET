// EntityDecodeProbe — replays a single .dem through EntityTracker and dumps
// LastEntityError if any. Used to confirm whether the entity-state decoder
// bit misalignment (cured by the 2026-06-08 decode fix)
// affects a given demo.
//
// Usage:
//   dotnet run -c Release --project tools/EntityDecodeProbe -- <path/to/demo.dem>
//   dotnet run -c Release --project tools/EntityDecodeProbe -- --schema <path/to/demo.dem> [class1,class2,...]
//
// --schema dumps the demo's CSVCMsg_FlattenedSerializer fields for the named
// classes (defaults to CBodyComponent, CCSPlayerPawn, CCSPlayer_WeaponServices,
// CCSPlayer_MovementServices — the classes implicated in the bit-misalignment
// investigation) and exits
// without replaying. Used to diff bench vs Furia schemas in the hunt for
// element-type mis-detection.
//
// Exit code: 0 if no decode error, 1 if any.

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using Google.Protobuf;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: EntityDecodeProbe [--schema] <demo.dem> [classes-csv]");
    return 2;
}

bool schemaMode = args.Length > 0 && args[0] == "--schema";
bool descsMode  = args.Length > 0 && args[0] == "--descriptors";
bool fieldBytesMode = args.Length > 0 && args[0] == "--field-bytes";
int pathArg = (schemaMode || descsMode || fieldBytesMode) ? 1 : 0;

if (fieldBytesMode)
{
    // Dump the raw ProtoFlattenedSerializerField_t bytes (plus symbol-resolved field/serializer
    // metadata) for every field whose VarName matches any of the provided names. Goal: side-by-side
    // diff of two "look-alike" fields (e.g. m_networkAnimTiming vs m_SerializePoseRecipeAG2Dynamic)
    // at the raw proto level to identify the discriminator that distinguishes their wire shapes.
    if (pathArg >= args.Length) { Console.Error.WriteLine("Missing demo path"); return 2; }
    string fpath = args[pathArg];
    if (!File.Exists(fpath)) { Console.Error.WriteLine($"File not found: {fpath}"); return 2; }
    if (args.Length <= pathArg + 1) { Console.Error.WriteLine("Missing field-names CSV"); return 2; }
    var wanted = new HashSet<string>(args[pathArg + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    byte[] fbytes = File.ReadAllBytes(fpath);
    ParsedDemo fparsed = DemoParser.Parse(fbytes.AsMemory());

    // Find the first CDemoSendTables and extract the embedded CSVCMsg_FlattenedSerializer.
    CSVCMsg_FlattenedSerializer? flat = null;
    foreach (var frame in fparsed.Frames)
    {
        foreach (var nm in frame.InnerMessages)
        {
            if (nm.Payload is CDemoSendTables st)
            {
                BitBuffer buf = new(st.Data.ToByteArray());
                int size = (int)buf.ReadUVarInt32();
                byte[] raw = buf.ReadBytes(size);
                flat = CSVCMsg_FlattenedSerializer.Parser.ParseFrom(raw);
                break;
            }
            if (nm.Payload is CSVCMsg_FlattenedSerializer fs) { flat = fs; break; }
        }
        if (flat is not null)
        {
            break;
        }
    }
    if (flat is null) { Console.Error.WriteLine("No CSVCMsg_FlattenedSerializer found"); return 2; }

    string[] symbols = flat.Symbols.ToArray();
    string Sym(int i) => i >= 0 && i < symbols.Length ? symbols[i] : $"<sym{i}>";

    // Map flat-field index → list of (serializer name, version, index-within-serializer).
    var fieldToOwners = new Dictionary<int, List<(string, int, int)>>();
    foreach (var s in flat.Serializers)
    {
        string sn = Sym(s.SerializerNameSym);
        int sv = s.SerializerVersion;
        for (int i = 0; i < s.FieldsIndex.Count; i++)
        {
            int fi = s.FieldsIndex[i];
            if (!fieldToOwners.TryGetValue(fi, out var list)) { list = new(); fieldToOwners[fi] = list; }
            list.Add((sn, sv, i));
        }
    }

    for (int i = 0; i < flat.Fields.Count; i++)
    {
        var f = flat.Fields[i];
        string name = Sym(f.VarNameSym);
        if (!wanted.Contains(name))
        {
            continue;
        }

        Console.WriteLine($"=== flat-field#{i}  name='{name}'  type='{Sym(f.VarTypeSym)}' ===");
        Console.WriteLine($"  raw proto bytes (base64): {Convert.ToBase64String(f.ToByteArray())}");
        Console.WriteLine($"  raw proto bytes (hex):    {Convert.ToHexString(f.ToByteArray())}");
        Console.WriteLine($"  raw proto size: {f.CalculateSize()} bytes");
        Console.WriteLine($"  ToString:");
        foreach (var line in f.ToString().Split('\n'))
        {
            Console.WriteLine($"    {line.TrimEnd()}");
        }
        Console.WriteLine($"  Has bits:");
        Console.WriteLine($"    var_type_sym={f.HasVarTypeSym} var_name_sym={f.HasVarNameSym} bit_count={f.HasBitCount} low_value={f.HasLowValue} high_value={f.HasHighValue}");
        Console.WriteLine($"    encode_flags={f.HasEncodeFlags} field_serializer_name_sym={f.HasFieldSerializerNameSym} field_serializer_version={f.HasFieldSerializerVersion}");
        Console.WriteLine($"    send_node_sym={f.HasSendNodeSym} var_encoder_sym={f.HasVarEncoderSym} var_serializer_sym={f.HasVarSerializerSym}");
        Console.WriteLine($"    polymorphic_types.Count={f.PolymorphicTypes.Count}");
        if (f.HasSendNodeSym)
        {
            Console.WriteLine($"    send_node='{Sym(f.SendNodeSym)}'");
        }
        if (f.HasVarEncoderSym)
        {
            Console.WriteLine($"    var_encoder='{Sym(f.VarEncoderSym)}'");
        }
        if (f.HasVarSerializerSym)
        {
            Console.WriteLine($"    var_serializer='{Sym(f.VarSerializerSym)}'");
        }
        if (fieldToOwners.TryGetValue(i, out var owners))
        {
            Console.WriteLine($"  Referenced by {owners.Count} serializer(s):");
            foreach (var (sn, sv, idx) in owners)
            {
                Console.WriteLine($"    {sn} v{sv} at index [{idx}]");
            }
        }
        Console.WriteLine();
    }
    return 0;
}

if (descsMode)
{
    if (pathArg >= args.Length) { Console.Error.WriteLine("Missing demo path"); return 2; }
    string dpath = args[pathArg];
    if (!File.Exists(dpath)) { Console.Error.WriteLine($"File not found: {dpath}"); return 2; }
    string[] classes = args.Length > pathArg + 1
        ? args[pathArg + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : new[] { "CCSPlayerPawn" };

    byte[] dbytes = File.ReadAllBytes(dpath);
    ParsedDemo dparsed = DemoParser.Parse(dbytes.AsMemory());
    EntityTracker dtracker = new();
    dtracker.Replay(dparsed.Frames);

    foreach (string entry in classes)
    {
        // Support "CCSPlayerPawn[9]" syntax to descend into nested descriptor tree.
        string cls = entry;
        var idxPath = new List<int>();
        int br;
        while ((br = cls.IndexOf('[', StringComparison.Ordinal)) >= 0)
        {
            int end = cls.IndexOf(']', br);
            if (end < 0)
            {
                break;
            }
            idxPath.Add(int.Parse(cls.AsSpan(br + 1, end - br - 1), System.Globalization.CultureInfo.InvariantCulture));
            cls = cls.Remove(br, end - br + 1);
        }

        var descs = dtracker.DebugDescriptors(cls, idxPath.ToArray());
        string indices = idxPath.Count > 0 ? "[" + string.Join("][", idxPath) + "]" : "";
        Console.WriteLine($"# Descriptors for {cls}{indices} ({descs.Count} entries)");
        for (int i = 0; i < descs.Count; i++)
        {
            var d = descs[i];
            Console.WriteLine($"  [{i,3}] {d.Path,-60} type={d.TypeName ?? "-",-40} enc={d.Encoder ?? "-",-15} bc={d.BitCount,3} ef={d.EncodeFlags} children={d.ChildCount}");
        }
        Console.WriteLine();
    }
    return 0;
}
if (pathArg >= args.Length)
{
    Console.Error.WriteLine("Missing demo path");
    return 2;
}

string path = args[pathArg];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 2;
}

byte[] bytes = File.ReadAllBytes(path);
ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());

if (schemaMode)
{
    string[] defaults = { "CBodyComponent", "CCSPlayerPawn", "CCSPlayer_WeaponServices", "CCSPlayer_MovementServices", "CCSPlayerPawnBase" };
    string[] classes = args.Length > pathArg + 1
        ? args[pathArg + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : defaults;

    // Replay just enough to surface the schema. The schema lands on DEM_SendTables, which
    // appears very early in the demo — but we don't know exactly which frame, so step until
    // tracker.Schema is non-null. Replay() processes every frame; cheaper to walk.
    EntityTracker schemaTracker = new();
    foreach (var frame in parsed.Frames)
    {
        schemaTracker.ReplayToIndex(frame.FrameNumber, parsed.Frames);
        if (schemaTracker.Schema is not null)
        {
            break;
        }
    }

    RuntimeSchema? schema = schemaTracker.Schema;
    if (schema is null)
    {
        Console.Error.WriteLine("No schema found in demo (no DEM_SendTables / CSVCMsg_FlattenedSerializer).");
        return 2;
    }

    foreach (string cls in classes)
    {
        bool any = false;
        // Dump every version of a serializer with this name so we can spot version skew.
        foreach (var kv in schema.Serializers)
        {
            if (kv.Key.Name != cls)
            {
                continue;
            }
            any = true;
            RuntimeSerializer ser = kv.Value;
            Console.WriteLine($"# {ser.Name} v{ser.Version} ({ser.Fields.Length} fields)");
            for (int i = 0; i < ser.Fields.Length; i++)
            {
                RuntimeField f = ser.Fields[i];
                string low  = f.LowValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                string high = f.HighValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                string childInfo = f.ChildSerializer is null ? "-" : $"{f.ChildSerializer.Name}/v{f.ChildSerializer.Version}";
                Console.WriteLine($"  [{i,3}] {f.Name,-45} type={f.TypeName,-50} enc={f.Encoder ?? "-",-15} bc={f.BitCount,3} ef={f.EncodeFlags} low={low} high={high} child={childInfo}");
            }
            Console.WriteLine();
        }
        if (!any)
        {
            Console.WriteLine($"# {cls} : NOT FOUND");
        }
    }

    return 0;
}

EntityTracker tracker = new();

var createdAt = new Dictionary<int, int>();
tracker.EntityCreated += (idx, state) =>
{
    if (!createdAt.ContainsKey(idx))
    {
        createdAt[idx] = tracker.CurrentFrameIndex;
    }
};

tracker.Replay(parsed.Frames);

Console.WriteLine($"Total entities created: {createdAt.Count}");
Console.WriteLine($"Delta-on-unknown count: {tracker.DeltaUnknownCount}");

if (tracker.LastEntityError is { } err)
{
    Console.WriteLine("=== LastEntityError ===");
    Console.WriteLine(err);
    return 1;
}

Console.WriteLine($"No entity decode error. Frames: {parsed.Frames.Count}, ticks: {parsed.TickCount}.");
return 0;
