#region

using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using Google.Protobuf;
using Google.Protobuf.Reflection;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>Payload node builder.</summary>
public static class PayloadNodeBuilder
{
    // ── player_info_t decoder ─────────────────────────────────────────────
    // Layout (Source engine, little-endian):
    //   offset   8  (8): xuid   — uint64 SteamID64
    //   offset  16 (128): name  — char[128] null-terminated UTF-8
    //   offset 144  (4): userId — int32 (in-game userid)
    //   offset 316  (1): fakePlayer — non-zero = bot
    private const int MinPlayerInfoBytes = 318;
    // ── Embedded-bytes dispatch ───────────────────────────────────────────

    // Lookup table for bytes fields that contain a single known proto type.
    // Use Direct(parser)         for fields whose bytes are a raw proto serialization.
    // Use VarIntPrefixed(parser) for fields whose bytes are [uvarint size][proto bytes]
    //   (e.g. CDemoSendTables.data — see demofile-net DemoParser.Entities.cs).
    // Add one line here whenever a new embedded-proto field is identified.
    private static readonly Dictionary<(string Parent, string Field), Func<ByteString, IMessage?>> _knownEmbeds =
        new()
        {
            {
                ("CMsgServerUserCmd", "data"), Direct(CSGOUserCmdPB.Parser)
            },
            {
                ("CDemoSendTables", "data"), VarIntPrefixed(CSVCMsg_FlattenedSerializer.Parser)
            },
            {
                ("CDemoUserCmd", "data"), Direct(CMsgServerUserCmd.Parser)
            }
        };

    /// <summary>Build.</summary>
    public static IReadOnlyList<PayloadNode> Build(IMessage? message)
    {
        // Deferred payloads (svc_UserCmds) carry raw bytes, not a field graph — materialize the real
        // message before reflecting over its fields. This is the inspector drill-in path; the field
        // accessors below operate on the concrete proto instance, not the DeferredMessage wrapper.
        if (message is DeferredMessage deferred)
        {
            message = deferred.Materialize();
        }

        return message is null ? [] : BuildFields(message);
    }

    /// <summary>
    ///     Build the node tree and annotate top-level nodes with byte ranges from
    ///     <paramref name="rawBytes" /> (Step 2).  Pass null to skip annotation.
    /// </summary>
    public static IReadOnlyList<PayloadNode> Build(IMessage? message, byte[]? rawBytes)
    {
        IReadOnlyList<PayloadNode> nodes = Build(message);
        if (rawBytes is not { Length: > 0 } || nodes.Count == 0)
        {
            return nodes;
        }

        // Recursively annotate all nodes with byte ranges relative to rawBytes[0].
        AnnotateRecursively(nodes, rawBytes, 0);
        return nodes;
    }

    /// <summary>
    ///     Decodes raw proto-wire <paramref name="bytes" /> into a flat top-level
    ///     <see cref="PayloadNode" /> list using the generic scanner — field number, wire type,
    ///     value, and recursive nested-message decode. Used to surface the structure of UNKNOWN
    ///     net-messages the parser could not decode, so they can be reverse-engineered. Each
    ///     top-level node is annotated with its byte range (relative to <paramref name="bytes" />[0])
    ///     so a standalone hex view can highlight it. Best-effort: returns whatever fields the
    ///     scanner recovers even if the trailing bytes don't decode as clean proto.
    /// </summary>
    public static IReadOnlyList<PayloadNode> BuildFromRawProto(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return [];
        }

        List<DownstreamUtilities.FieldSpan> spans = DownstreamUtilities.Scan(bytes);
        if (spans.Count == 0)
        {
            return [];
        }

        // Pre-count occurrences of each field number so repeats can be suffixed with [i].
        Dictionary<int, int> repeatCount = new();
        foreach (DownstreamUtilities.FieldSpan s in spans)
        {
            repeatCount[s.FieldNumber] = repeatCount.TryGetValue(s.FieldNumber, out int c) ? c + 1 : 1;
        }

        Dictionary<int, int> fieldIdx = new();
        List<PayloadNode> nodes = new(spans.Count);
        foreach (DownstreamUtilities.FieldSpan span in spans)
        {
            int fn = span.FieldNumber;
            int idx = fieldIdx.TryGetValue(fn, out int ci) ? ci : 0;
            fieldIdx[fn] = idx + 1;

            string name = repeatCount[fn] > 1 ? $"#{fn}[{idx}]" : $"#{fn}";
            PayloadNode node = BuildGenericSpanNode(name, fn, bytes, span, 0, 5);
            node.ByteStart = span.Start;
            node.ByteLength = span.Length;
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    ///     Builds a flat decoded-property <see cref="PayloadNode" /> list from a typed
    ///     <see cref="GameEvent" /> record using its statically-defined
    ///     <c>GetDecodedFields()</c> override.  No byte-range annotation.
    /// </summary>
    public static IReadOnlyList<PayloadNode> BuildDecodedEvent(GameEvent evt)
    {
        IReadOnlyList<(string Name, string Value, string WireType)> fields = evt.GetDecodedFields();
        List<PayloadNode> nodes = new(fields.Count + 3);
        foreach ((string name, string value, string wireType) in fields)
        {
            nodes.Add(new PayloadNode
            {
                Name = name,
                Value = value,
                WireTypeName = wireType,
                Depth = 0
            });
        }

        // Append temporal context — always present on every GameEvent.
        nodes.Add(new PayloadNode
        {
            Name = "FrameNumber",
            Value = evt.FrameNumber.ToString(CultureInfo.InvariantCulture),
            WireTypeName = "int",
            Depth = 0
        });
        nodes.Add(new PayloadNode
        {
            Name = "ServerTick",
            Value = evt.ServerTick.ToString(CultureInfo.InvariantCulture),
            WireTypeName = "int",
            Depth = 0
        });
        nodes.Add(new PayloadNode
        {
            Name = "GameTick",
            Value = evt.GameTick.ToString(CultureInfo.InvariantCulture),
            WireTypeName = "int",
            Depth = 0
        });

        return nodes;
    }

    /// <summary>
    ///     Scans <paramref name="slice" /> and annotates every node in <paramref name="nodes" />
    ///     (and their descendants) with absolute byte offsets within the root message bytes.
    ///     <paramref name="absoluteBase" /> is the offset of slice[0] within the root buffer.
    /// </summary>
    private static void AnnotateRecursively(
        IReadOnlyList<PayloadNode> nodes,
        byte[] slice,
        int absoluteBase)
    {
        List<DownstreamUtilities.FieldSpan> spans = DownstreamUtilities.Scan(slice);
        if (spans.Count == 0)
        {
            return;
        }

        // Group by field number (order preserved — important for repeated fields)
        Dictionary<int, List<DownstreamUtilities.FieldSpan>> byField = new();
        foreach (DownstreamUtilities.FieldSpan s in spans)
        {
            if (!byField.TryGetValue(s.FieldNumber, out List<DownstreamUtilities.FieldSpan>? lst))
            {
                byField[s.FieldNumber] = lst = [];
            }

            lst.Add(s);
        }

        foreach (PayloadNode node in nodes)
        {
            if (node.FieldNumber <= 0)
            {
                continue;
            }

            if (!byField.TryGetValue(node.FieldNumber, out List<DownstreamUtilities.FieldSpan>? fieldSpans) || fieldSpans.Count == 0)
            {
                continue;
            }

            // Annotate this node with the full extent of all spans for this field.
            node.ByteStart = absoluteBase + fieldSpans[0].Start;
            node.ByteLength = fieldSpans[^1].End - fieldSpans[0].Start;

            if (!node.HasChildren)
            {
                continue;
            }

            // Children are either positional items ([0],[1],...) from a repeated field,
            // or named fields of a sub-message.
            bool isRepeatedContainer = node.Children[0].Name.StartsWith('[');

            if (isRepeatedContainer)
            {
                // Match [i] to fieldSpans[i] positionally.
                int count = Math.Min(node.Children.Count, fieldSpans.Count);
                for (int i = 0; i < count; i++)
                {
                    PayloadNode child = node.Children[i];
                    DownstreamUtilities.FieldSpan span = fieldSpans[i];
                    child.ByteStart = absoluteBase + span.Start;
                    child.ByteLength = span.Length;

                    // If [i] is itself a message, recurse into its fields.
                    if (child.HasChildren && span.WireType == 2 &&
                        DownstreamUtilities.TryGetPayloadRange(
                            slice, span, out int pStart, out int pLen) && pLen > 0)
                    {
                        AnnotateRecursively(child.Children,
                            slice.AsSpan(pStart, pLen).ToArray(),
                            absoluteBase + pStart);
                    }
                }
            }
            else
            {
                // Single (possibly message) field — recurse into its named children.
                DownstreamUtilities.FieldSpan span = fieldSpans[0];
                if (span.WireType == 2 &&
                    DownstreamUtilities.TryGetPayloadRange(
                        slice, span, out int pStart, out int pLen) && pLen > 0)
                {
                    AnnotateRecursively(node.Children,
                        slice.AsSpan(pStart, pLen).ToArray(),
                        absoluteBase + pStart);
                }
            }
        }
    }

    // ── IMessage reflection ───────────────────────────────────────────────

    private static List<PayloadNode> BuildFields(IMessage message, int depth = 0)
    {
        List<PayloadNode> nodes = new();
        foreach (FieldDescriptor? field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            // Skip default/empty values
            if (field.HasPresence)
            {
                if (!field.Accessor.HasValue(message))
                {
                    continue;
                }
            }
            else if (field.IsRepeated)
            {
                IList? list = (IList)field.Accessor.GetValue(message);
                if (list.Count == 0)
                {
                    continue;
                }
            }
            else
            {
                object? val = field.Accessor.GetValue(message);
                if (Equals(val, GetDefaultValue(field)))
                {
                    continue;
                }
            }

            // Special case: ByteString fields that embed another proto message
            if (field is { IsRepeated: false, FieldType: FieldType.Bytes })
            {
                ByteString? bs = (ByteString)field.Accessor.GetValue(message);
                PayloadNode? special = TryBuildEmbeddedBytes(message, field.Name, bs, depth);
                if (special != null)
                {
                    special.FieldNumber = field.FieldNumber;
                    special.WireTypeName = "length-delimited";
                    nodes.Add(special);
                    continue;
                }
            }

            PayloadNode node = BuildNode(field.Name, field, field.Accessor.GetValue(message), depth);
            node.FieldNumber = field.FieldNumber;
            node.WireTypeName = WireTypeNameFor(field);
            nodes.Add(node);
        }

        return nodes;
    }

    private static PayloadNode BuildGenericSpanNode(
        string name, int fieldNumber, byte[] bytes,
        DownstreamUtilities.FieldSpan span,
        int depth, int maxDepth)
    {
        PayloadNode child;
        switch (span.WireType)
        {
            case 0: // varint
                if (DownstreamUtilities.TryReadVarintValue(bytes, span, out ulong vi))
                {
                    // Render as signed int64 when the value looks like a negative int32.
                    string viStr = vi > uint.MaxValue
                        ? $"{(long)vi}"
                        : vi.ToString(CultureInfo.InvariantCulture);
                    child = new PayloadNode
                    {
                        Name = name,
                        Value = viStr,
                        Depth = depth
                    };
                }
                else
                {
                    child = new PayloadNode
                    {
                        Name = name,
                        Value = "<varint?>",
                        Depth = depth
                    };
                }

                break;

            case 1: // fixed64
                if (DownstreamUtilities.TryReadFixed64Value(bytes, span, out ulong f64))
                {
                    double d = BitConverter.Int64BitsToDouble((long)f64);
                    child = new PayloadNode
                    {
                        Name = name,
                        Value = $"0x{f64:X16} / {d:G}",
                        Depth = depth
                    };
                }
                else
                {
                    child = new PayloadNode
                    {
                        Name = name,
                        Value = "<fixed64?>",
                        Depth = depth
                    };
                }

                break;

            case 5: // fixed32
                if (DownstreamUtilities.TryReadFixed32Value(bytes, span, out uint f32))
                {
                    float f = BitConverter.Int32BitsToSingle((int)f32);
                    child = new PayloadNode
                    {
                        Name = name,
                        Value = $"0x{f32:X8} / {f:G}",
                        Depth = depth
                    };
                }
                else
                {
                    child = new PayloadNode
                    {
                        Name = name,
                        Value = "<fixed32?>",
                        Depth = depth
                    };
                }

                break;

            case 2: // length-delimited
                if (DownstreamUtilities.TryGetPayloadRange(bytes, span, out int pStart, out int pLen))
                {
                    byte[] payload = bytes.AsSpan(pStart, pLen).ToArray();

                    // Try as UTF-8 text.
                    if (IsLikelyString(payload))
                    {
                        child = new PayloadNode
                        {
                            Name = name,
                            Value = $"\"{Encoding.UTF8.GetString(payload)}\"",
                            Depth = depth
                        };
                        break;
                    }

                    // Try recursive generic proto decode.
                    if (maxDepth > 0)
                    {
                        PayloadNode? sub = TryBuildGenericProto(name, payload, depth, maxDepth - 1);
                        if (sub is not null)
                        {
                            child = sub;
                            break;
                        }
                    }

                    child = new PayloadNode
                    {
                        Name = name,
                        Value = $"<{pLen} bytes>",
                        Depth = depth
                    };
                }
                else
                {
                    child = new PayloadNode
                    {
                        Name = name,
                        Value = "<?>",
                        Depth = depth
                    };
                }

                break;

            default:
                child = new PayloadNode
                {
                    Name = name,
                    Value = $"<wire{span.WireType}>",
                    Depth = depth
                };
                break;
        }

        child.FieldNumber = fieldNumber;
        return child;
    }

    private static PayloadNode BuildItem(string name, FieldDescriptor field, object value, int depth)
    {
        if (field.FieldType == FieldType.Message && value is IMessage nested)
        {
            List<PayloadNode> children = BuildFields(nested, depth + 1);
            return children.Count > 0
                ? new PayloadNode
                {
                    Name = name,
                    Children = children,
                    Depth = depth
                }
                : new PayloadNode
                {
                    Name = name,
                    Value = "{ }",
                    Depth = depth
                };
        }

        if (value is ByteString bs)
        {
            return new PayloadNode
            {
                Name = name,
                Value = $"<{bs.Length} bytes>",
                Depth = depth
            };
        }

        return new PayloadNode
        {
            Name = name,
            Value = value.ToString() ?? "",
            Depth = depth
        };
    }

    private static PayloadNode BuildNode(string name, FieldDescriptor field, object? rawValue, int depth)
    {
        if (rawValue is null)
        {
            return new PayloadNode
            {
                Name = name,
                Value = "(null)",
                Depth = depth
            };
        }

        if (field.IsRepeated)
        {
            IList list = (IList)rawValue;
            List<PayloadNode> children = new();
            for (int i = 0; i < list.Count; i++)
            {
                children.Add(BuildItem($"[{i}]", field, list[i]!, depth + 1));
            }

            return new PayloadNode
            {
                Name = name,
                Children = children,
                Depth = depth
            };
        }

        return BuildItem(name, field, rawValue, depth);
    }

    private static Func<ByteString, IMessage?> Direct(MessageParser parser) =>
        bs => parser.ParseFrom(bs);

    private static object? GetDefaultValue(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Bool => false,
        FieldType.String => "",
        FieldType.Bytes => ByteString.Empty,
        FieldType.Float => 0f,
        FieldType.Double => 0d,
        FieldType.Int32 or FieldType.Fixed32 => 0,
        FieldType.Int64 or FieldType.Fixed64 => 0L,
        FieldType.UInt32 => 0u,
        FieldType.UInt64 => 0uL,
        FieldType.Enum => field.EnumType.FindValueByNumber(0),
        _ => null
    };

    /// <summary>Returns true when <paramref name="data" /> is likely human-readable UTF-8 text.</summary>
    private static bool IsLikelyString(byte[] data)
    {
        if (data.Length == 0 || data.Length > 4096)
        {
            return false;
        }

        int printable = 0;
        foreach (byte b in data)
        {
            if (b is >= 32 and < 127 or (byte)'\n' or (byte)'\r' or (byte)'\t')
            {
                printable++;
            }
        }

        return printable >= (int)(data.Length * 0.85);
    }

    private static PayloadNode MakeMessageNode(string name, IMessage message, int depth)
    {
        List<PayloadNode> children = BuildFields(message, depth + 1);
        return new PayloadNode
        {
            Name = name,
            Depth = depth,
            Children = children.Count > 0
                ? children
                :
                [
                    new PayloadNode
                    {
                        Name = "(empty)",
                        Value = "",
                        Depth = depth + 1
                    }
                ]
        };
    }

    private static string ReadNullTerminatedUtf8(byte[] data, int offset, int maxLen)
    {
        int end = offset;
        int limit = Math.Min(offset + maxLen, data.Length);
        while (end < limit && data[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(data, offset, end - offset);
    }

    /// <summary>
    ///     If the ByteString field is a known embedded proto, returns a decoded node;
    ///     otherwise returns null to fall back to the default "&lt;N bytes&gt;" rendering.
    /// </summary>
    private static PayloadNode? TryBuildEmbeddedBytes(IMessage parent, string fieldName, ByteString bs, int depth)
    {
        if (bs.IsEmpty)
        {
            return null;
        }

        string? parentType = parent.Descriptor.Name;

        // Fixed-type embeddings: look up by (parent message name, field name)
        if (_knownEmbeds.TryGetValue((parentType, fieldName), out Func<ByteString, IMessage?>? parse))
        {
            IMessage? inner = null;
            try
            {
                inner = parse(bs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PayloadNodeBuilder] Embedded proto decode failed for ({parentType}, {fieldName}): {ex.Message}");
            }

            if (inner is not null)
            {
                return MakeMessageNode(fieldName, inner, depth);
            }
        }

        // CDemoStringTables.Types.items_t.data — binary string table entry.
        // In CDemoStringTables snapshots the data blob is already decoded (no bitstream);
        // items from the userinfo table carry a player_info_t binary struct.
        if (parentType == "items_t" && fieldName == "data")
        {
            if (bs.Length >= MinPlayerInfoBytes)
            {
                PayloadNode? playerNode = TryDecodePlayerInfoNode(bs.ToByteArray(), depth);
                if (playerNode is not null)
                {
                    return playerNode;
                }
            }

            // Small / non-playerinfo blobs: show as hex instead of "<N bytes>".
            return new PayloadNode
            {
                Name = fieldName,
                Value = bs.Length <= 64
                    ? Convert.ToHexString(bs.ToByteArray())
                    : $"<{bs.Length} bytes (binary)>",
                Depth = depth
            };
        }

        // CSVCMsg_UserMessage.msg_data → dispatch by the sibling msg_type field
        if (parentType == "CSVCMsg_UserMessage" && fieldName == "msg_data")
        {
            FieldDescriptor? msgTypeField = parent.Descriptor.Fields.InFieldNumberOrder()
                .FirstOrDefault(f => f.Name == "msg_type");
            if (msgTypeField != null)
            {
                int msgType = (int)msgTypeField.Accessor.GetValue(parent);
                IMessage? inner = TryParseUserMessage(msgType, bs);
                if (inner is not null)
                {
                    return MakeMessageNode(fieldName, inner, depth);
                }
            }
        }

        // Generic fallback: scan unknown bytes as raw proto wire format.
        {
            PayloadNode? generic = TryBuildGenericProto(fieldName, bs.ToByteArray(), depth);
            if (generic is not null)
            {
                return generic;
            }
        }

        return null;
    }

    /// <summary>
    ///     Attempts to decode <paramref name="bytes" /> as raw proto wire format.
    ///     Returns a node tree if all bytes are consumed cleanly; otherwise null.
    /// </summary>
    private static PayloadNode? TryBuildGenericProto(string fieldName, byte[] bytes, int depth, int maxDepth = 5)
    {
        if (bytes.Length == 0 || maxDepth < 0)
        {
            return null;
        }

        List<DownstreamUtilities.FieldSpan> spans = DownstreamUtilities.Scan(bytes);
        // Confidence: scanner must have consumed all bytes with no leftover.
        if (spans.Count == 0 || spans[^1].End != bytes.Length)
        {
            return null;
        }

        // Pre-count occurrences of each field number so we can suffix with [i] for repeats.
        Dictionary<int, int> repeatCount = new();
        foreach (DownstreamUtilities.FieldSpan s in spans)
        {
            repeatCount[s.FieldNumber] = repeatCount.TryGetValue(s.FieldNumber, out int c) ? c + 1 : 1;
        }

        Dictionary<int, int> fieldIdx = new();

        List<PayloadNode> children = new();
        foreach (DownstreamUtilities.FieldSpan span in spans)
        {
            int fn = span.FieldNumber;
            int idx = fieldIdx.TryGetValue(fn, out int ci) ? ci : 0;
            fieldIdx[fn] = idx + 1;

            string name = repeatCount[fn] > 1 ? $"#{fn}[{idx}]" : $"#{fn}";

            PayloadNode child = BuildGenericSpanNode(name, fn, bytes, span, depth + 1, maxDepth);
            children.Add(child);
        }

        return new PayloadNode
        {
            Name = fieldName,
            Depth = depth,
            Children = children
        };
    }

    private static PayloadNode? TryDecodePlayerInfoNode(byte[] data, int depth)
    {
        if (data.Length < MinPlayerInfoBytes)
        {
            return null;
        }

        try
        {
            ulong xuid = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8, 8));
            string name = ReadNullTerminatedUtf8(data, 16, 128);
            int userId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(144, 4));
            bool isBot = data[316] != 0;

            if (xuid == 0 && !isBot && name.Length == 0)
            {
                return null;
            }

            return new PayloadNode
            {
                Name = "data",
                Depth = depth,
                Children =
                [
                    new PayloadNode
                    {
                        Name = "name",
                        Value = $"\"{name}\"",
                        Depth = depth + 1
                    },
                    new PayloadNode
                    {
                        Name = "xuid",
                        Value = xuid.ToString(CultureInfo.InvariantCulture),
                        Depth = depth + 1
                    },
                    new PayloadNode
                    {
                        Name = "userId",
                        Value = userId.ToString(CultureInfo.InvariantCulture),
                        Depth = depth + 1
                    },
                    new PayloadNode
                    {
                        Name = "isBot",
                        Value = isBot ? "true" : "false",
                        Depth = depth + 1
                    }
                ]
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PayloadNodeBuilder] TryDecodePlayerInfoNode failed: {ex.Message}");
            return null;
        }
    }

    // ── CSVCMsg_UserMessage dispatch ──────────────────────────────────────

    private static IMessage? TryParseUserMessage(int msgType, ByteString bs)
    {
        // ECstrike15UserMessages values (301–386)
        Func<IMessage>? factory = msgType switch
        {
            301 => () => CCSUsrMsg_VGUIMenu.Parser.ParseFrom(bs),
            302 => () => CCSUsrMsg_Geiger.Parser.ParseFrom(bs),
            303 => () => CCSUsrMsg_Train.Parser.ParseFrom(bs),
            304 => () => CCSUsrMsg_HudText.Parser.ParseFrom(bs),
            308 => () => CCSUsrMsg_HudMsg.Parser.ParseFrom(bs),
            309 => () => CCSUsrMsg_ResetHud.Parser.ParseFrom(bs),
            310 => () => CCSUsrMsg_GameTitle.Parser.ParseFrom(bs),
            312 => () => CCSUsrMsg_Shake.Parser.ParseFrom(bs),
            313 => () => CCSUsrMsg_Fade.Parser.ParseFrom(bs),
            314 => () => CCSUsrMsg_Rumble.Parser.ParseFrom(bs),
            315 => () => CCSUsrMsg_CloseCaption.Parser.ParseFrom(bs),
            316 => () => CCSUsrMsg_CloseCaptionDirect.Parser.ParseFrom(bs),
            317 => () => CCSUsrMsg_SendAudio.Parser.ParseFrom(bs),
            318 => () => CCSUsrMsg_RawAudio.Parser.ParseFrom(bs),
            319 => () => CCSUsrMsg_VoiceMask.Parser.ParseFrom(bs),
            320 => () => CCSUsrMsg_RequestState.Parser.ParseFrom(bs),
            321 => () => CCSUsrMsg_Damage.Parser.ParseFrom(bs),
            322 => () => CCSUsrMsg_RadioText.Parser.ParseFrom(bs),
            323 => () => CCSUsrMsg_HintText.Parser.ParseFrom(bs),
            324 => () => CCSUsrMsg_KeyHintText.Parser.ParseFrom(bs),
            325 => () => CCSUsrMsg_ProcessSpottedEntityUpdate.Parser.ParseFrom(bs),
            326 => () => CCSUsrMsg_ReloadEffect.Parser.ParseFrom(bs),
            327 => () => CCSUsrMsg_AdjustMoney.Parser.ParseFrom(bs),
            329 => () => CCSUsrMsg_StopSpectatorMode.Parser.ParseFrom(bs),
            330 => () => CCSUsrMsg_KillCam.Parser.ParseFrom(bs),
            331 => () => CCSUsrMsg_DesiredTimescale.Parser.ParseFrom(bs),
            332 => () => CCSUsrMsg_CurrentTimescale.Parser.ParseFrom(bs),
            333 => () => CCSUsrMsg_AchievementEvent.Parser.ParseFrom(bs),
            334 => () => CCSUsrMsg_MatchEndConditions.Parser.ParseFrom(bs),
            335 => () => CCSUsrMsg_DisconnectToLobby.Parser.ParseFrom(bs),
            336 => () => CCSUsrMsg_PlayerStatsUpdate.Parser.ParseFrom(bs),
            // 338: CCSUsrMsg_WarmupHasEnded — not in canonical SteamDatabase protos
            339 => () => CCSUsrMsg_ClientInfo.Parser.ParseFrom(bs),
            340 => () => CCSUsrMsg_XRankGet.Parser.ParseFrom(bs),
            341 => () => CCSUsrMsg_XRankUpd.Parser.ParseFrom(bs),
            345 => () => CCSUsrMsg_CallVoteFailed.Parser.ParseFrom(bs),
            346 => () => CCSUsrMsg_VoteStart.Parser.ParseFrom(bs),
            347 => () => CCSUsrMsg_VotePass.Parser.ParseFrom(bs),
            348 => () => CCSUsrMsg_VoteFailed.Parser.ParseFrom(bs),
            349 => () => CCSUsrMsg_VoteSetup.Parser.ParseFrom(bs),
            350 => () => CCSUsrMsg_ServerRankRevealAll.Parser.ParseFrom(bs),
            351 => () => CCSUsrMsg_SendLastKillerDamageToClient.Parser.ParseFrom(bs),
            352 => () => CCSUsrMsg_ServerRankUpdate.Parser.ParseFrom(bs),
            353 => () => CCSUsrMsg_ItemPickup.Parser.ParseFrom(bs),
            354 => () => CCSUsrMsg_ShowMenu.Parser.ParseFrom(bs),
            355 => () => CCSUsrMsg_BarTime.Parser.ParseFrom(bs),
            356 => () => CCSUsrMsg_AmmoDenied.Parser.ParseFrom(bs),
            357 => () => CCSUsrMsg_MarkAchievement.Parser.ParseFrom(bs),
            358 => () => CCSUsrMsg_MatchStatsUpdate.Parser.ParseFrom(bs),
            359 => () => CCSUsrMsg_ItemDrop.Parser.ParseFrom(bs),
            // 360: CCSUsrMsg_GlowPropTurnOff — not in canonical SteamDatabase protos
            361 => () => CCSUsrMsg_SendPlayerItemDrops.Parser.ParseFrom(bs),
            362 => () => CCSUsrMsg_RoundBackupFilenames.Parser.ParseFrom(bs),
            363 => () => CCSUsrMsg_SendPlayerItemFound.Parser.ParseFrom(bs),
            364 => () => CCSUsrMsg_ReportHit.Parser.ParseFrom(bs),
            365 => () => CCSUsrMsg_XpUpdate.Parser.ParseFrom(bs),
            366 => () => CCSUsrMsg_QuestProgress.Parser.ParseFrom(bs),
            367 => () => CCSUsrMsg_ScoreLeaderboardData.Parser.ParseFrom(bs),
            368 => () => CCSUsrMsg_PlayerDecalDigitalSignature.Parser.ParseFrom(bs),
            369 => () => CCSUsrMsg_WeaponSound.Parser.ParseFrom(bs),
            370 => () => CCSUsrMsg_UpdateScreenHealthBar.Parser.ParseFrom(bs),
            371 => () => CCSUsrMsg_EntityOutlineHighlight.Parser.ParseFrom(bs),
            372 => () => CCSUsrMsg_SSUI.Parser.ParseFrom(bs),
            373 => () => CCSUsrMsg_SurvivalStats.Parser.ParseFrom(bs),
            375 => () => CCSUsrMsg_EndOfMatchAllPlayersData.Parser.ParseFrom(bs),
            376 => () => CCSUsrMsg_PostRoundDamageReport.Parser.ParseFrom(bs),
            379 => () => CCSUsrMsg_RoundEndReportData.Parser.ParseFrom(bs),
            380 => () => CCSUsrMsg_CurrentRoundOdds.Parser.ParseFrom(bs),
            381 => () => CCSUsrMsg_DeepStats.Parser.ParseFrom(bs),
            383 => () => CCSUsrMsg_ShootInfo.Parser.ParseFrom(bs),
            385 => () => CCSUsrMsg_CounterStrafe.Parser.ParseFrom(bs),
            386 => () => CCSUsrMsg_DamagePrediction.Parser.ParseFrom(bs),
            _ => null
        };

        if (factory is null)
        {
            return null;
        }

        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PayloadNodeBuilder] UserMessage decode failed for msg_type={msgType}: {ex.Message}");
            return null;
        }
    }

    // CDemoSendTables.data (and potentially others) prefix the proto bytes with a
    // UVarInt32 byte-count — skip it before handing the remaining bytes to the parser.
    private static Func<ByteString, IMessage?> VarIntPrefixed(MessageParser parser) => bs =>
    {
        ReadOnlySpan<byte> span = bs.Span;
        if (!Leb128Utils.TryReadUInt32(ref span, out uint declaredLen))
        {
            return null;
        }

        if (span.Length != (int)declaredLen)
        {
            return null;
        }

        return parser.ParseFrom(span.ToArray());
    };

    private static string WireTypeNameFor(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Bool or FieldType.Int32 or FieldType.Int64
            or FieldType.UInt32 or FieldType.UInt64
            or FieldType.SInt32 or FieldType.SInt64 or FieldType.Enum => "varint",
        FieldType.Fixed32 or FieldType.SFixed32 or FieldType.Float => "fixed32",
        FieldType.Fixed64 or FieldType.SFixed64 or FieldType.Double => "fixed64",
        FieldType.Bytes or FieldType.String or FieldType.Message
            or FieldType.Group => "length-delimited",
        _ => ""
    };
}
