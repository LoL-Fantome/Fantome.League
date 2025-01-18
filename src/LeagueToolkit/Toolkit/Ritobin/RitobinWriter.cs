using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using System.Globalization;

namespace LeagueToolkit.Toolkit.Ritobin;

public sealed class RitobinWriter : IDisposable
{
    private int _depth;

    private readonly Dictionary<uint, string> _objects;
    private readonly Dictionary<uint, string> _classes;
    private readonly Dictionary<uint, string> _properties;
    private readonly Dictionary<uint, string> _binHashes;
    private readonly Dictionary<ulong, string> _wadHashes;

    private readonly StringWriter _writer;

    public bool IsDisposed { get; private set; }

    public RitobinWriter(
        IEnumerable<KeyValuePair<uint, string>> objects,
        IEnumerable<KeyValuePair<uint, string>> classes,
        IEnumerable<KeyValuePair<uint, string>> properties,
        IEnumerable<KeyValuePair<uint, string>> binHashes,
        IEnumerable<KeyValuePair<ulong, string>> wadHashes)
    {
        this._writer = new StringWriter(CultureInfo.InvariantCulture);

        this._objects = new(objects);
        this._classes = new(classes);
        this._properties = new(properties);
        this._binHashes = new(binHashes);
        this._wadHashes = new(wadHashes);
    }

    public string WritePropertyBin(BinTree bin)
    {
        this._writer.GetStringBuilder().Clear();

        WriteHeader();
        WriteFileType(bin);
        WriteVersion();
        WriteDependencies(bin.Dependencies);
        if (bin.IsOverride)
            WriteOverrides(bin.DataOverrides);
        WriteObjects(bin.Objects);

        return this._writer.ToString();
    }

    private void WriteHeader() => this._writer.WriteLine("#PROP_text");

    private void WriteFileType(BinTree bin)
    {
        WriteNamedProperty("type", new BinTreeString(0, bin.IsOverride ? "PTCH" : "PROP"));
        this._writer.WriteLine();
    }

    private void WriteVersion()
    {
        WriteNamedProperty("version", new BinTreeU32(0, 3));
        this._writer.WriteLine();
    }

    private void WriteDependencies(IEnumerable<string> dependencies)
    {
        WriteNamedProperty(
            "linked",
            new BinTreeContainer(0, BinPropertyType.String, dependencies.Select(x => new BinTreeString(0, x)))
        );
        this._writer.WriteLine();
    }

    private void WriteOverrides(IReadOnlyList<BinTreeDataOverride> overrides)
    {
        void WritePatch(BinTreeDataOverride dataOverride)
        {
            this._writer.WriteLine($"0x{dataOverride.ObjectPathHash:x} = patch {{");
            IncreaseDepth();

            Indent();
            WriteNamedProperty("path", new BinTreeString(0, dataOverride.PropertyPath));
            this._writer.WriteLine();

            Indent();
            WriteNamedProperty("value", dataOverride.Property);
            this._writer.WriteLine();

            DecreaseDepth();
            Indent();
            this._writer.Write('}');
        }

        this._writer.Write("patches: map[hash,embed] = ");
        if (overrides.Count is 0)
        {
            this._writer.Write("{}");
            return;
        }

        this._writer.WriteLine('{');
        IncreaseDepth();
        foreach (BinTreeDataOverride dataOverride in overrides)
        {
            Indent();
            WritePatch(dataOverride);
            this._writer.WriteLine();
        }
        DecreaseDepth();
        Indent();
        this._writer.Write('}');
    }

    private void WriteObjects(IReadOnlyDictionary<uint, BinTreeObject> objects)
    {
        WriteNamedProperty(
            "entries",
            new BinTreeMap(
                0,
                BinPropertyType.Hash,
                BinPropertyType.Embedded,
                objects.Select(
                    x =>
                        new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                            new BinTreeHash(0, x.Key),
                            new BinTreeEmbedded(0, x.Value.ClassHash, x.Value.Properties.Select(x => x.Value))
                        )
                )
            )
        );
    }

    private void WriteProperty(BinTreeProperty property)
    {
        string name = this._properties.TryGetValue(property.NameHash, out string foundName) switch
        {
            true => foundName,
            false => string.Format("0x{0:x}", property.NameHash)
        };

        WriteNamedProperty(name, property);
    }

    private void WriteNamedProperty(string name, BinTreeProperty property)
    {
        this._writer.Write($"{name}: {GetPropertyType(property)} = ");
        WritePropertyValue(property);
    }

    private void WritePropertyValue(BinTreeProperty property)
    {
        if (property is BinTreeBool boolProperty)
            this._writer.Write(boolProperty.Value.ToString().ToLowerInvariant());
        else if (property is BinTreeI8 i8)
            this._writer.Write(i8);
        else if (property is BinTreeU8 u8)
            this._writer.Write(u8);
        else if (property is BinTreeI16 i16)
            this._writer.Write(i16);
        else if (property is BinTreeU16 u16)
            this._writer.Write(u16);
        else if (property is BinTreeI32 i32)
            this._writer.Write(i32);
        else if (property is BinTreeU32 u32)
            this._writer.Write(u32);
        else if (property is BinTreeI64 i64)
            this._writer.Write(i64);
        else if (property is BinTreeU64 u64)
            this._writer.Write(u64);
        else if (property is BinTreeF32 f32)
            this._writer.Write(f32);
        else if (property is BinTreeVector2 vec2)
            WriteVector2Property(vec2);
        else if (property is BinTreeVector3 vec3)
            WriteVector3Property(vec3);
        else if (property is BinTreeVector4 vec4)
            WriteVector4Property(vec4);
        else if (property is BinTreeMatrix44 matrix44)
            WriteMatrix44Property(matrix44);
        else if (property is BinTreeColor color)
            WriteVector4Property(new(color.NameHash, color.Value));
        else if (property is BinTreeString stringProperty)
            this._writer.Write($@"""{stringProperty.Value}""");
        else if (property is BinTreeHash hash)
            WriteBinHashProperty(hash);
        else if (property is BinTreeWadChunkLink chunkLink)
            WriteWadChunkLinkProperty(chunkLink);
        else if (property is BinTreeUnorderedContainer unorderedContainer)
            WriteContainerProperty(unorderedContainer);
        else if (property is BinTreeContainer container)
            WriteContainerProperty(container);
        else if (property is BinTreeEmbedded embedded)
            WriteStructProperty(embedded);
        else if (property is BinTreeStruct structProperty)
            WriteStructProperty(structProperty);
        else if (property is BinTreeObjectLink objectLink)
            WriteObjectLinkProperty(objectLink);
        else if (property is BinTreeOptional optional)
            WriteOptionalProperty(optional);
        else if (property is BinTreeMap map)
            WriteMapProperty(map);
        else if (property is BinTreeBitBool bitBool)
            this._writer.Write(bitBool.Value.ToString().ToLowerInvariant());
    }

    private void WriteVector2Property(BinTreeVector2 property) =>
        this._writer.Write(
            $"{{ {property.Value.X.ToString(CultureInfo.InvariantCulture)},"
                + $" {property.Value.Y.ToString(CultureInfo.InvariantCulture)} }}"
        );

    private void WriteVector3Property(BinTreeVector3 property) =>
        this._writer.Write(
            $"{{ {property.Value.X.ToString(CultureInfo.InvariantCulture)},"
                + $" {property.Value.Y.ToString(CultureInfo.InvariantCulture)},"
                + $" {property.Value.Z.ToString(CultureInfo.InvariantCulture)} }}"
        );

    private void WriteVector4Property(BinTreeVector4 property) =>
        this._writer.Write(
            $"{{ {property.Value.X.ToString(CultureInfo.InvariantCulture)},"
                + $" {property.Value.Y.ToString(CultureInfo.InvariantCulture)},"
                + $" {property.Value.Z.ToString(CultureInfo.InvariantCulture)},"
                + $" {property.Value.W.ToString(CultureInfo.InvariantCulture)} }}"
        );

    private void WriteMatrix44Property(BinTreeMatrix44 property)
    {
        this._writer.WriteLine("{");
        IncreaseDepth();

        Indent();
        this._writer.WriteLine(
            $"{property.Value.M11.ToString(CultureInfo.InvariantCulture)}, {property.Value.M12.ToString(CultureInfo.InvariantCulture)}, {property.Value.M13.ToString(CultureInfo.InvariantCulture)}, {property.Value.M14.ToString(CultureInfo.InvariantCulture)}"
        );

        Indent();
        this._writer.WriteLine(
            $"{property.Value.M21.ToString(CultureInfo.InvariantCulture)}, {property.Value.M22.ToString(CultureInfo.InvariantCulture)}, {property.Value.M23.ToString(CultureInfo.InvariantCulture)}, {property.Value.M24.ToString(CultureInfo.InvariantCulture)}"
        );

        Indent();
        this._writer.WriteLine(
            $"{property.Value.M31.ToString(CultureInfo.InvariantCulture)}, {property.Value.M32.ToString(CultureInfo.InvariantCulture)}, {property.Value.M33.ToString(CultureInfo.InvariantCulture)}, {property.Value.M34.ToString(CultureInfo.InvariantCulture)}"
        );

        Indent();
        this._writer.WriteLine(
            $"{property.Value.M41.ToString(CultureInfo.InvariantCulture)}, {property.Value.M42.ToString(CultureInfo.InvariantCulture)}, {property.Value.M43.ToString(CultureInfo.InvariantCulture)}, {property.Value.M44.ToString(CultureInfo.InvariantCulture)}"
        );

        DecreaseDepth();
        Indent();
        this._writer.Write("}");
    }

    private void WriteBinHashProperty(BinTreeHash hash)
    {
        string value = this._binHashes.TryGetValue(hash.Value, out string hashValue) switch
        {
            true => $@"""{hashValue}""",
            false => string.Format("0x{0:x}", hash.Value)
        };

        this._writer.Write(value);
    }

    private void WriteWadChunkLinkProperty(BinTreeWadChunkLink chunkLink)
    {
        string value = this._wadHashes.TryGetValue(chunkLink.Value, out string hashValue) switch
        {
            true => $@"""{hashValue}""",
            false => string.Format("0x{0:x}", chunkLink.Value)
        };

        this._writer.Write(value);
    }

    private void WriteContainerProperty(BinTreeContainer container)
    {
        if (container.Elements.Count is 0)
        {
            this._writer.Write("{}");
            return;
        }

        this._writer.WriteLine("{");
        IncreaseDepth();
        foreach (BinTreeProperty property in container.Elements)
        {
            Indent();
            WritePropertyValue(property);
            this._writer.WriteLine();
        }
        DecreaseDepth();
        Indent();
        this._writer.Write("}");
    }

    private void WriteStructProperty(BinTreeStruct structProperty)
    {
        string className = this._classes.TryGetValue(structProperty.ClassHash, out string foundClassName) switch
        {
            true => foundClassName,
            false => string.Format("0x{0:x}", structProperty.ClassHash)
        };

        if (structProperty.Properties.Count is 0)
        {
            this._writer.Write($@"{className} {{}}");
            return;
        }

        this._writer.WriteLine($"{className} {{");
        IncreaseDepth();
        foreach (var (_, property) in structProperty.Properties)
        {
            Indent();
            WriteProperty(property);
            this._writer.WriteLine();
        }
        DecreaseDepth();
        Indent();
        this._writer.Write('}');
    }

    private void WriteObjectLinkProperty(BinTreeObjectLink objectLink)
    {
        string value = this._objects.TryGetValue(objectLink.Value, out string foundValue) switch
        {
            true => $@"""{foundValue}""",
            false => string.Format("0x{0:x}", objectLink.Value)
        };

        this._writer.Write(value);
    }

    private void WriteOptionalProperty(BinTreeOptional optional)
    {
        if (optional.Value is null)
        {
            this._writer.Write("{}");
            return;
        }

        this._writer.WriteLine("{");
        IncreaseDepth();
        Indent();
        WritePropertyValue(optional.Value);
        this._writer.WriteLine();
        DecreaseDepth();
        Indent();
        this._writer.Write("}");
    }

    private void WriteMapProperty(BinTreeMap map)
    {
        if (map.Count is 0)
        {
            this._writer.Write("{}");
            return;
        }

        this._writer.WriteLine('{');
        IncreaseDepth();
        foreach (var (key, value) in map)
        {
            Indent();
            WritePropertyValue(key);
            this._writer.Write(" = ");
            WritePropertyValue(value);
            this._writer.WriteLine();
        }
        DecreaseDepth();
        Indent();
        this._writer.Write('}');
    }

    private void IncreaseDepth() => this._depth++;

    private void DecreaseDepth() => this._depth--;

    private void Indent()
    {
        Span<char> indentation = stackalloc char[this._depth * 4];
        indentation.Fill(' ');

        this._writer.Write(indentation);
    }

    private static string GetPropertyType(BinTreeProperty property)
    {
        return property.Type switch
        {
            BinPropertyType.UnorderedContainer when property is BinTreeUnorderedContainer container
                => $"list2[{GetPrimitivePropertyType(container.ElementType)}]",
            BinPropertyType.Container when property is BinTreeContainer container
                => $"list[{GetPrimitivePropertyType(container.ElementType)}]",
            BinPropertyType.Optional when property is BinTreeOptional option
                => $"option[{GetPrimitivePropertyType(option.ValueType)}]",
            BinPropertyType.Map when property is BinTreeMap map
                => $"map[{GetPrimitivePropertyType(map.KeyType)},{GetPrimitivePropertyType(map.ValueType)}]",
            _ => GetPrimitivePropertyType(property.Type)
        };
    }

    private static string GetPrimitivePropertyType(BinPropertyType type)
    {
        return type switch
        {
            BinPropertyType.Bool => "bool",
            BinPropertyType.I8 => "i8",
            BinPropertyType.U8 => "u8",
            BinPropertyType.I16 => "i16",
            BinPropertyType.U16 => "u16",
            BinPropertyType.I32 => "i32",
            BinPropertyType.U32 => "u32",
            BinPropertyType.I64 => "i64",
            BinPropertyType.U64 => "u64",
            BinPropertyType.F32 => "f32",
            BinPropertyType.Vector2 => "vec2",
            BinPropertyType.Vector3 => "vec3",
            BinPropertyType.Vector4 => "vec4",
            BinPropertyType.Matrix44 => "mtx44",
            BinPropertyType.Color => "rgba",
            BinPropertyType.String => "string",
            BinPropertyType.Hash => "hash",
            BinPropertyType.WadChunkLink => "file",
            BinPropertyType.Struct => "pointer",
            BinPropertyType.Embedded => "embed",
            BinPropertyType.ObjectLink => "link",
            BinPropertyType.BitBool => "flag",
            _ => throw new ArgumentException($"Cannot write property of type {type}", nameof(type)),
        };
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (this.IsDisposed)
            return;

        if (disposing)
            this._writer?.Dispose();

        this.IsDisposed = true;
    }
}
