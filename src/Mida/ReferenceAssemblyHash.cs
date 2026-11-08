using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Mida;

/// <summary>
/// Hashes the metadata of a reference assembly, which is all a dependent compiles against, without the values of
/// <c>AssemblyFileVersion</c> and <c>AssemblyInformationalVersion</c>: tools that stamp a version from git change
/// them on every commit. <c>AssemblyVersion</c> stays, because an assembly built against a higher one fails to load
/// a lower one. The MVID, a hash of the content, is left out with the rest of the GUID heap.
/// </summary>
internal static class ReferenceAssemblyHash
{
    private static readonly string[] MaskedAttributes = ["AssemblyFileVersionAttribute", "AssemblyInformationalVersionAttribute"];

    // ECMA-335 II.22. A blob changes length with the version, which moves every later blob, so blob columns are hashed
    // by content. Everything else is a row number, a fixed value or an offset into a heap these versions leave alone.
    private static readonly Dictionary<TableIndex, string[]> Columns = new Dictionary<string, string>
    {
        ["Module"] = "2 S G G G",
        ["TypeRef"] = "ResolutionScope S S",
        ["TypeDef"] = "4 S S TypeDefOrRef Field MethodDef",
        ["FieldPtr"] = "Field",
        ["Field"] = "2 S B",
        ["MethodPtr"] = "MethodDef",
        ["MethodDef"] = "4 2 2 S B Param",
        ["ParamPtr"] = "Param",
        ["Param"] = "2 2 S",
        ["InterfaceImpl"] = "TypeDef TypeDefOrRef",
        ["MemberRef"] = "MemberRefParent S B",
        ["Constant"] = "2 HasConstant B",
        ["CustomAttribute"] = "HasCustomAttribute CustomAttributeType B",
        ["FieldMarshal"] = "HasFieldMarshal B",
        ["DeclSecurity"] = "2 HasDeclSecurity B",
        ["ClassLayout"] = "2 4 TypeDef",
        ["FieldLayout"] = "4 Field",
        ["StandAloneSig"] = "B",
        ["EventMap"] = "TypeDef Event",
        ["EventPtr"] = "Event",
        ["Event"] = "2 S TypeDefOrRef",
        ["PropertyMap"] = "TypeDef Property",
        ["PropertyPtr"] = "Property",
        ["Property"] = "2 S B",
        ["MethodSemantics"] = "2 MethodDef HasSemantics",
        ["MethodImpl"] = "TypeDef MethodDefOrRef MethodDefOrRef",
        ["ModuleRef"] = "S",
        ["TypeSpec"] = "B",
        ["ImplMap"] = "2 MemberForwarded S ModuleRef",
        ["FieldRva"] = "4 Field",
        ["EncLog"] = "4 4",
        ["EncMap"] = "4",
        ["Assembly"] = "4 2 2 2 2 4 B S S",
        ["AssemblyProcessor"] = "4",
        ["AssemblyOS"] = "4 4 4",
        ["AssemblyRef"] = "2 2 2 2 4 B S S B",
        ["AssemblyRefProcessor"] = "4 AssemblyRef",
        ["AssemblyRefOS"] = "4 4 4 AssemblyRef",
        ["File"] = "4 S B",
        ["ExportedType"] = "4 4 S S Implementation",
        ["ManifestResource"] = "4 4 S Implementation",
        ["NestedClass"] = "TypeDef TypeDef",
        ["GenericParam"] = "2 2 TypeOrMethodDef S",
        ["MethodSpec"] = "MethodDefOrRef B",
        ["GenericParamConstraint"] = "GenericParam TypeDefOrRef",
    }.ToDictionary(c => Enum.Parse<TableIndex>(c.Key), c => c.Value.Split(' '));

    // Tag bits, then the tables a coded index can point to.
    private static readonly Dictionary<string, (int Bits, TableIndex[] Tables)> CodedIndices = new Dictionary<string, string>
    {
        ["TypeDefOrRef"] = "2 TypeDef TypeRef TypeSpec",
        ["HasConstant"] = "2 Field Param Property",
        ["HasCustomAttribute"] = "5 MethodDef Field TypeRef TypeDef Param InterfaceImpl MemberRef Module DeclSecurity Property Event "
            + "StandAloneSig ModuleRef TypeSpec Assembly AssemblyRef File ExportedType ManifestResource GenericParam GenericParamConstraint MethodSpec",
        ["HasFieldMarshal"] = "1 Field Param",
        ["HasDeclSecurity"] = "2 TypeDef MethodDef Assembly",
        ["MemberRefParent"] = "3 TypeDef TypeRef ModuleRef MethodDef TypeSpec",
        ["HasSemantics"] = "1 Event Property",
        ["MethodDefOrRef"] = "1 MethodDef MemberRef",
        ["MemberForwarded"] = "1 Field MethodDef",
        ["Implementation"] = "2 File AssemblyRef ExportedType",
        ["CustomAttributeType"] = "3 MethodDef MemberRef",
        ["ResolutionScope"] = "2 Module ModuleRef AssemblyRef TypeRef",
        ["TypeOrMethodDef"] = "1 TypeDef MethodDef",
    }.ToDictionary(c => c.Key, c =>
    {
        var parts = c.Value.Split(' ');
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1..].Select(Enum.Parse<TableIndex>).ToArray());
    });

    /// <summary>Throws on a file it cannot read as metadata.</summary>
    public static string Compute(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var reader = pe.GetMetadataReader();
        var metadata = pe.GetMetadata().GetContent().AsSpan();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Heap(reader, metadata, HeapIndex.String));
        hash.AppendData(Heap(reader, metadata, HeapIndex.UserString));

        var masked = reader.GetAssemblyDefinition().GetCustomAttributes()
            .Where(a => MaskedAttributes.Contains(AttributeName(reader, reader.GetCustomAttribute(a))))
            .Select(a => MetadataTokens.GetRowNumber(a))
            .ToHashSet();

        foreach (var table in Enum.GetValues<TableIndex>())
        {
            var rows = reader.GetTableRowCount(table);
            if (rows == 0)
            {
                continue;
            }

            // A table this schema does not know, such as a portable PDB's, would go unhashed.
            var columns = Columns.TryGetValue(table, out var known) ? known : throw new BadImageFormatException($"Unexpected table {table}");
            var sizes = columns.Select(c => Size(reader, c)).ToArray();
            var rowSize = reader.GetTableRowSize(table);
            if (sizes.Sum() != rowSize)
            {
                throw new BadImageFormatException($"Rows of {table} are {rowSize} bytes, not {sizes.Sum()}");
            }

            var offset = reader.GetTableMetadataOffset(table);
            for (var row = 1; row <= rows; row++, offset += rowSize)
            {
                var column = offset;
                for (var i = 0; i < columns.Length; column += sizes[i], i++)
                {
                    var bytes = metadata.Slice(column, sizes[i]);
                    if (columns[i] != "B")
                    {
                        hash.AppendData(bytes);
                    }
                    else if (table == TableIndex.CustomAttribute && masked.Contains(row))
                    {
                        AppendLength(hash, -1);
                    }
                    else
                    {
                        var blob = reader.GetBlobBytes(MetadataTokens.BlobHandle(sizes[i] == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt32LittleEndian(bytes)));
                        AppendLength(hash, blob.Length);
                        hash.AppendData(blob);
                    }
                }
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ReadOnlySpan<byte> Heap(MetadataReader reader, ReadOnlySpan<byte> metadata, HeapIndex heap) =>
        metadata.Slice(reader.GetHeapMetadataOffset(heap), reader.GetHeapSize(heap));

    private static void AppendLength(IncrementalHash hash, int length)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        hash.AppendData(bytes);
    }

    private static string? AttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference
            || reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is not { Kind: HandleKind.TypeReference } parent)
        {
            return null;
        }

        var type = reader.GetTypeReference((TypeReferenceHandle)parent);
        return reader.GetString(type.Namespace) == "System.Reflection" ? reader.GetString(type.Name) : null;
    }

    private static int Size(MetadataReader reader, string column) => column switch
    {
        "2" => 2,
        "4" => 4,
        "S" => HeapIndexSize(reader, HeapIndex.String),
        "G" => HeapIndexSize(reader, HeapIndex.Guid),
        "B" => HeapIndexSize(reader, HeapIndex.Blob),
        _ when CodedIndices.TryGetValue(column, out var coded) =>
            coded.Tables.Max(reader.GetTableRowCount) < 1 << (16 - coded.Bits) ? 2 : 4,
        _ => reader.GetTableRowCount(Enum.Parse<TableIndex>(column)) < 1 << 16 ? 2 : 4,
    };

    private static int HeapIndexSize(MetadataReader reader, HeapIndex heap) => reader.GetHeapSize(heap) < 1 << 16 ? 2 : 4;
}
