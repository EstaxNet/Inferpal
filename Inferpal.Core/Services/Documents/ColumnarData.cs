using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;

namespace Inferpal.Services.Documents;

/// <summary>
/// A Parquet file or an Arrow IPC file (<c>.arrow</c>, <c>.feather</c> v2, <c>.ipc</c>) — what pandas, Polars, Spark and
/// DuckDB write — read as one table: its column names, the type each declares, and its rows, each value as the text
/// <c>query_data</c> loads.
/// </summary>
/// <remarks>
/// <para>
/// A value's text is the one SQLite reads: an integer or a real as written (invariant), a boolean 1/0, a date
/// <c>yyyy-MM-dd</c>, a timestamp <c>yyyy-MM-dd HH:mm:ss</c> (fractions kept, in UTC when the file says it is), a decimal as a
/// number, a category (dictionary) as its text, a list or a structure as JSON (<c>json_extract</c> reads it), binary named by
/// its length — never its bytes. Null is <see cref="Null"/>, never an empty string: these files tell the two apart. NaN and
/// infinities are null, counted and said.
/// </para>
/// <para>
/// ⚠ The file comes from the workspace (a cloned repository) and is read without a card: everything it can make us hold is
/// bounded — the values' total text (<see cref="MaxTextChars"/>), Parquet's declared uncompressed size, what Arrow allocates
/// (<see cref="BudgetAllocator"/>), the cells. A small file can declare gigabytes.
/// </para>
/// <para>
/// Every reader is managed. Arrow's own compression package is not taken (a second compression stack): an Arrow file
/// compressed with LZ4 or Zstd is decoded here (<see cref="Codecs"/>), on the block codecs Parquet's reader already brings.
/// </para>
/// </remarks>
internal static class ColumnarData
{
    internal static readonly string[] Extensions = [".parquet", ".arrow", ".feather", ".ipc"];

    /// <summary>What a null value is in <see cref="Table.Rows"/>: no file holds this text.</summary>
    internal const string Null = "\0null";

    /// <summary>The most text all values together may make.</summary>
    internal const long MaxTextChars = 256L * 1024 * 1024;

    /// <summary>The most a file may declare or allocate, uncompressed.</summary>
    internal const long MaxDecodedBytes = 1024L * 1024 * 1024;

    /// <summary>A table read: its column names, the SQLite type each declares (<c>null</c>: inferred), its rows, notes.</summary>
    internal sealed record Table(List<string> Columns, List<string?> Types, List<List<string>> Rows, List<string> Notes, string Kind);

    /// <summary>The table <paramref name="path"/> holds, within <paramref name="maxCells"/> values.</summary>
    /// <exception cref="FormatException">The file is not one this reads, said in its message.</exception>
    /// <exception cref="DocumentLimitException">It holds more than the bounds allow.</exception>
    internal static async Task<Table> ReadAsync(string path, long maxCells, int maxColumns, CancellationToken ct) =>
        Path.GetExtension(path).Equals(".parquet", StringComparison.OrdinalIgnoreCase)
            ? await ReadParquetAsync(path, maxCells, maxColumns, ct)
            : ReadArrow(path, maxCells, maxColumns, ct);

    /// <summary>What a read counts as it goes: text made, values that were NaN, columns of a type it does not read.</summary>
    private sealed class Tally
    {
        public long Chars;
        public int NotFinite;
        public readonly SortedSet<string> Unread = new(StringComparer.Ordinal);

        public string Add(string text)
        {
            if ((Chars += text.Length) > MaxTextChars)
                throw new DocumentLimitException($"its values make more than {MaxTextChars / (1024 * 1024)} MB of text; ask the user for a smaller extract");
            return text;
        }

        public List<string> Notes()
        {
            var notes = new List<string>();
            if (NotFinite > 0) notes.Add($"{NotFinite} value(s) NaN or infinite were loaded as NULL.");
            if (Unread.Count > 0) notes.Add($"Not read (their type), loaded as NULL: {string.Join(", ", Unread)}.");
            return notes;
        }
    }

    // ── Parquet ──

    private static async Task<Table> ReadParquetAsync(string path, long maxCells, int maxColumns, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (stream.Length < 12 || stream.Read(magic) < 4 || !magic.SequenceEqual("PAR1"u8))
            throw new FormatException("it is not a Parquet file (no PAR1 mark)");
        stream.Position = 0;
        var options = new Parquet.ParquetOptions { UseDateOnlyTypeForDates = true, UseBigDecimal = true };
        List<string> columns;
        List<string?> types;
        var flats = new List<Flat>();
        int groups;
        await using (var reader = await Parquet.ParquetReader.CreateAsync(stream, options, leaveStreamOpen: true, cancellationToken: ct))
        {
            columns = [.. reader.Schema.Fields.Select(f => f.Name)];
            types = [.. reader.Schema.Fields.Select(f => f is Parquet.Schema.DataField d ? Declared(d.ClrType) : null)];
            for (var c = 0; c < reader.Schema.Fields.Count; c++)
                if (reader.Schema.Fields[c] is Parquet.Schema.StructField s)
                    foreach (var child in s.Fields.OfType<Parquet.Schema.DataField>())
                    {
                        flats.Add(new Flat(c, child.Name));
                        columns.Add($"{s.Name}_{child.Name}");
                        types.Add(Declared(child.ClrType));
                    }
            if (columns.Count > maxColumns) throw new DocumentLimitException($"it has more than {maxColumns} columns");
            var rows = reader.RowGroups.Sum(g => g.RowCount);
            // Counted before anything is decoded: a small file can hold millions of rows of repeated values.
            if (rows * Math.Max(1, columns.Count) > maxCells)
                throw new DocumentLimitException($"it holds {rows:N0} rows of {columns.Count} columns, more than {maxCells:N0} values; ask the user for a smaller extract");
            var declared = reader.Metadata?.RowGroups?.Sum(g => g.TotalByteSize) ?? 0;
            if (declared > MaxDecodedBytes)
                throw new DocumentLimitException($"it declares {declared / (1024 * 1024):N0} MB once uncompressed; ask the user for a smaller extract");
            groups = reader.RowGroupCount;
        }
        var tally = new Tally();
        var body = new List<List<string>>();
        // One row group at a time: a group's decoded values are let go once they are text.
        for (var g = 0; g < groups; g++)
        {
            stream.Position = 0;
            var result = await Parquet.Serialization.ParquetSerializer.DeserializeUntypedAsync(stream, options, g, ct);
            var i = 0;
            var top = columns.Count - flats.Count;
            foreach (var record in result.Data)
            {
                if ((++i & 1023) == 0) ct.ThrowIfCancellationRequested();
                body.Add(Row([.. columns.Take(top).Select(c => record.TryGetValue(c, out var v) ? v : null)], flats, tally));
            }
        }
        return new Table(columns, types, body, tally.Notes(), "Parquet");
    }

    /// <summary>A leaf of a structure column, loaded beside it as <c>column_field</c>.</summary>
    /// <remarks>A model counts <c>user_id</c> before it thinks of <c>json_extract(user, '$.id')</c>: on the structure
    /// itself, <c>COUNT(DISTINCT user)</c> counts the distinct pairs, never the users. One level, the JSON column kept.</remarks>
    private sealed record Flat(int Column, string Field);

    /// <summary>A row's texts: its columns, then the leaves of its structure columns.</summary>
    private static List<string> Row(IReadOnlyList<object?> values, List<Flat> flats, Tally tally)
    {
        var row = new List<string>(values.Count + flats.Count);
        foreach (var v in values) row.Add(tally.Add(Text(v, tally)));
        foreach (var f in flats)
            row.Add(tally.Add(Text(values[f.Column] is IDictionary d && d.Contains(f.Field) ? d[f.Field] : null, tally)));
        return row;
    }

    /// <summary>The SQLite type a CLR type a reader gives declares; <c>null</c> for one whose text says it best.</summary>
    private static string? Declared(Type? type)
    {
        var t = type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(float) || t == typeof(double) || t == typeof(decimal) || t == typeof(Half) ? "REAL"
             : t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(sbyte) || t == typeof(byte)
               || t == typeof(ushort) || t == typeof(uint) || t == typeof(bool) ? "INTEGER"
             : t == typeof(string) ? "TEXT"
             : null;
    }

    // ── Arrow IPC ──

    private static Table ReadArrow(string path, long maxCells, int maxColumns, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[6];
        var read = stream.Read(magic);
        var isFile = read == 6 && magic.SequenceEqual("ARROW1"u8);
        stream.Position = 0;
        if (!isFile && read >= 4 && magic[..4].SequenceEqual("FEA1"u8))
            throw new FormatException("it is a Feather version 1 file, which this does not read: save it again with pyarrow or pandas (Feather version 2)");
        var allocator = new BudgetAllocator(MaxDecodedBytes);
        using ArrowStreamReader reader = isFile ? new ArrowFileReader(stream, allocator, new Codecs()) : new ArrowStreamReader(stream, allocator, new Codecs(), false);
        var tally = new Tally();
        List<string>? columns = null;
        List<string?>? types = null;
        var flats = new List<Flat>();
        var body = new List<List<string>>();
        long cells = 0;
        while (reader.ReadNextRecordBatch() is { } batch)
        {
            using (batch)
            {
                ct.ThrowIfCancellationRequested();
                if (columns is null)
                {
                    (columns, types) = Schema(batch.Schema, flats);
                    if (columns.Count > maxColumns) throw new DocumentLimitException($"it has more than {maxColumns} columns");
                }
                if ((cells += (long)batch.Length * Math.Max(1, columns.Count)) > maxCells)
                    throw new DocumentLimitException($"it holds more than {maxCells:N0} values; ask the user for a smaller extract");
                var arrays = Enumerable.Range(0, batch.ColumnCount).Select(batch.Column).ToList();
                for (var r = 0; r < batch.Length; r++)
                {
                    if ((r & 1023) == 1023) ct.ThrowIfCancellationRequested();
                    body.Add(Row([.. arrays.Select((a, c) => Value(a, r, columns[c], tally))], flats, tally));
                }
            }
        }
        // A file with a schema and no batch is an empty table, not an error.
        if (columns is null) (columns, types) = Schema(reader.Schema ?? throw new FormatException("it holds no schema"), flats);
        return new Table(columns, types!, body, tally.Notes(), "Arrow");
    }

    /// <summary>An Arrow schema's columns and declared types, the leaves of its structure columns added (<see cref="Flat"/>).</summary>
    private static (List<string> Columns, List<string?> Types) Schema(Apache.Arrow.Schema schema, List<Flat> flats)
    {
        List<string> columns = [.. schema.FieldsList.Select(f => f.Name)];
        List<string?> types = [.. schema.FieldsList.Select(f => Declared(f.DataType))];
        for (var c = 0; c < schema.FieldsList.Count; c++)
            if (schema.FieldsList[c].DataType is StructType s)
                foreach (var child in s.Fields.Where(f => f.DataType is not NestedType))
                {
                    flats.Add(new Flat(c, child.Name));
                    columns.Add($"{schema.FieldsList[c].Name}_{child.Name}");
                    types.Add(Declared(child.DataType));
                }
        return (columns, types);
    }

    private static string? Declared(IArrowType type) => type switch
    {
        FloatType or DoubleType or HalfFloatType or Decimal128Type or Decimal256Type => "REAL",
        Int8Type or Int16Type or Int32Type or Int64Type or UInt8Type or UInt16Type or UInt32Type or BooleanType => "INTEGER",
        StringType or LargeStringType or StringViewType => "TEXT",
        DictionaryType d => Declared(d.ValueType),
        _ => null,
    };

    /// <summary>The value at <paramref name="i"/> of an Arrow array, as a CLR value (a list or a dictionary for nested
    /// types); a type this does not read is null, and its column named.</summary>
    private static object? Value(IArrowArray array, int i, string column, Tally tally)
    {
        if (array.IsNull(i)) return null;
        switch (array)
        {
            case BooleanArray a: return a.GetValue(i);
            case Int8Array a: return a.GetValue(i);
            case Int16Array a: return a.GetValue(i);
            case Int32Array a: return a.GetValue(i);
            case Int64Array a: return a.GetValue(i);
            case UInt8Array a: return a.GetValue(i);
            case UInt16Array a: return a.GetValue(i);
            case UInt32Array a: return a.GetValue(i);
            case UInt64Array a: return a.GetValue(i);
            case FloatArray a: return a.GetValue(i);
            case DoubleArray a: return a.GetValue(i);
            case HalfFloatArray a: return (double?)a.GetValue(i);
            case Decimal128Array a:
                // Spark's DECIMAL(38, 18) can hold more than System.Decimal: its own text then.
                try { return a.GetValue(i); }
                catch (OverflowException) { return a.GetString(i); }
            case Decimal256Array a:
                try { return a.GetValue(i); }
                catch (OverflowException) { return a.GetString(i); }
            case StringArray a: return a.GetString(i);
            case LargeStringArray a: return a.GetString(i);
            case StringViewArray a: return a.GetString(i);
            case Date32Array a: return a.GetDateOnly(i);
            case Date64Array a: return a.GetDateOnly(i);
            // Typed object: a DateTime made a DateTimeOffset by the conditional would take this machine's offset.
            case TimestampArray a: return ((TimestampType)a.Data.DataType).Timezone is { Length: > 0 } ? (object?)a.GetTimestamp(i) : a.GetTimestamp(i)?.DateTime;
            case Time32Array a: return a.GetTime(i);
            case Time64Array a: return a.GetTime(i);
            case DurationArray a: return a.GetValue(i) is { } d ? Duration(d, ((DurationType)a.Data.DataType).Unit) : null;
            case BinaryArray a: return new Binary(a.GetBytes(i).Length);
            case LargeBinaryArray a: return new Binary(a.GetBytes(i).Length);
            case BinaryViewArray a: return new Binary(a.GetBytes(i).Length);
            case DictionaryArray a when Index(a.Indices, i) is { } k: return Value(a.Dictionary, k, column, tally);
            case ListArray a: return Items(a.GetSlicedValues(i));
            case LargeListArray a: return Items(a.GetSlicedValues(i));
            case FixedSizeListArray a: return Items(a.GetSlicedValues(i));
            case StructArray a:
                return ((StructType)a.Data.DataType).Fields.Select((f, k) => (f.Name, Value: Value(a.Fields[k], i, column, tally)))
                       .ToDictionary(p => p.Name, p => p.Value);
            default:
                tally.Unread.Add($"{column} ({array.Data.DataType.Name})");
                return null;
        }

        List<object?> Items(IArrowArray values) => [.. Enumerable.Range(0, values.Length).Select(k => Value(values, k, column, tally))];
    }

    private static int? Index(IArrowArray indices, int i) => indices switch
    {
        Int8Array a => a.GetValue(i), Int16Array a => a.GetValue(i), Int32Array a => a.GetValue(i),
        Int64Array a => (int?)a.GetValue(i), UInt8Array a => a.GetValue(i), UInt16Array a => a.GetValue(i),
        UInt32Array a => (int?)a.GetValue(i), UInt64Array a => (int?)a.GetValue(i),
        _ => throw new FormatException($"its dictionary indices are of type {indices.Data.DataType.Name}, which this does not read"),
    };

    /// <summary>An Arrow duration (pandas' timedelta) in its unit, as a time span.</summary>
    private static TimeSpan Duration(long value, TimeUnit unit) => unit switch
    {
        TimeUnit.Second => TimeSpan.FromSeconds(value),
        TimeUnit.Millisecond => TimeSpan.FromMilliseconds(value),
        TimeUnit.Microsecond => TimeSpan.FromTicks(value * 10),
        _ => TimeSpan.FromTicks(value / 100),
    };

    /// <summary>A binary value, named by its length: its bytes are never text for the model.</summary>
    private readonly record struct Binary(int Length);

    // ── Values ──

    /// <summary>A value as the text <c>query_data</c> loads (see the class remarks); <see cref="Null"/> for null.</summary>
    private static string Text(object? v, Tally tally)
    {
        switch (v)
        {
            case null: return Null;
            case double d when !double.IsFinite(d): tally.NotFinite++; return Null;
            case float f when !float.IsFinite(f): tally.NotFinite++; return Null;
        }
        return Leaf(v) ?? JsonSerializer.Serialize(Plain(v, 0, tally));
    }

    /// <summary>A value that is no container as its text; <c>null</c> for a list or a structure.</summary>
    private static string? Leaf(object v) => v switch
    {
        string s => s,
        bool b => b ? "1" : "0",
        byte or sbyte or short or ushort or int or uint or long or ulong or System.Numerics.BigInteger => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset o => Leaf(o.UtcDateTime),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        Binary b => string.Create(CultureInfo.InvariantCulture, $"(binary, {b.Length} bytes)"),
        byte[] bytes => string.Create(CultureInfo.InvariantCulture, $"(binary, {bytes.Length} bytes)"),
        Guid g => g.ToString(),
        IDictionary or IEnumerable => null,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>A nested value made of what JSON writes: dictionaries, lists, and each leaf as its text or number.</summary>
    /// <remarks>⚠ A leaf goes to <see cref="Leaf"/>, which never recurses, and the depth is bounded: a type nothing knows
    /// cannot send two methods back and forth until the stack overflows (which ends the host). NaN, which JSON cannot hold,
    /// is null.</remarks>
    private static object? Plain(object? v, int depth, Tally tally) => v switch
    {
        null => null,
        double d when !double.IsFinite(d) => Counted(tally),
        float f when !float.IsFinite(f) => Counted(tally),
        string or bool or int or long or double or float or short or byte or decimal => v,
        _ when depth > 32 => "…",
        byte[] or Binary => Leaf(v),
        IDictionary map => map.Keys.Cast<object>().ToDictionary(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? "", k => Plain(map[k], depth + 1, tally)),
        IEnumerable items => items.Cast<object?>().Select(x => Plain(x, depth + 1, tally)).ToList(),
        _ => Leaf(v),
    };

    private static object? Counted(Tally tally)
    {
        tally.NotFinite++;
        return null;
    }

    /// <summary>An allocator that refuses past a budget: an Arrow file declares the size of each buffer once decompressed,
    /// and a few megabytes can declare gigabytes.</summary>
    private sealed class BudgetAllocator(long budget) : NativeMemoryAllocator
    {
        private long _allocated;

        protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
        {
            if (Interlocked.Add(ref _allocated, length) > budget)
                throw new DocumentLimitException($"it declares more than {budget / (1024 * 1024):N0} MB once uncompressed; ask the user for a smaller extract");
            return base.AllocateInternal(length, out bytesAllocated);
        }
    }

    /// <summary>The two codecs an Arrow IPC file may be compressed with, decoded on the block codecs already shipped.</summary>
    private sealed class Codecs : ICompressionCodecFactory
    {
        public ICompressionCodec CreateCodec(CompressionCodecType type) => type switch
        {
            CompressionCodecType.Lz4Frame => new Lz4Frame(),
            CompressionCodecType.Zstd => new Zstd(),
            _ => throw new FormatException($"it is compressed with {type}, which this does not read"),
        };

        public ICompressionCodec CreateCodec(CompressionCodecType type, int? compressionLevel) => CreateCodec(type);
    }

    private sealed class Zstd : ICompressionCodec
    {
        private readonly ZstdSharp.Decompressor _decompressor = new();
        public int Decompress(ReadOnlyMemory<byte> source, Memory<byte> destination) => _decompressor.Unwrap(source.Span, destination.Span);
        public void Compress(ReadOnlyMemory<byte> source, Stream destination) => throw new NotSupportedException("read only");
        public void Compress(ReadOnlyMemory<byte> source, Stream destination, int? compressionLevel) => throw new NotSupportedException("read only");
        public void Dispose() => _decompressor.Dispose();
    }

    /// <summary>The LZ4 frame format (magic, frame descriptor, blocks, end mark), its blocks decoded by K4os's codec.</summary>
    /// <remarks>⚠ A frame's blocks may be LINKED (the default of lz4 and of Arrow C++ for anything past one 64 KB block):
    /// a block then copies bytes from those before it, and is decoded with what is already written as its dictionary —
    /// decoded alone, the second block of any column past 64 KB fails.</remarks>
    private sealed class Lz4Frame : ICompressionCodec
    {
        private const int Window = 64 * 1024;

        public int Decompress(ReadOnlyMemory<byte> source, Memory<byte> destination)
        {
            var src = source.Span;
            var dst = destination.Span;
            if (src.Length < 7 || BinaryPrimitives.ReadUInt32LittleEndian(src) != 0x184D2204) throw new FormatException("its LZ4 data has no frame mark");
            var flags = src[4];
            var linked = (flags & 0x20) == 0;
            var pos = 6 + ((flags & 0x08) != 0 ? 8 : 0) + ((flags & 0x01) != 0 ? 4 : 0) + 1;   // FLG, BD, content size, dict id, HC
            var blockChecksum = (flags & 0x10) != 0;
            var written = 0;
            while (pos + 4 <= src.Length)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(src[pos..]);
                pos += 4;
                if (size == 0) break;   // end mark
                var stored = (size & 0x80000000) != 0;
                var length = (int)(size & 0x7FFFFFFF);
                if (pos + length > src.Length) throw new FormatException("its LZ4 data is cut short");
                var block = src.Slice(pos, length);
                if (stored)
                {
                    if (written + length > dst.Length) throw new FormatException("its LZ4 data is longer than it declares");
                    block.CopyTo(dst[written..]);
                    written += length;
                }
                else
                {
                    var dictionary = linked ? dst[Math.Max(0, written - Window)..written] : [];
                    var n = K4os.Compression.LZ4.LZ4Codec.Decode(block, dst[written..], dictionary);
                    if (n < 0) throw new FormatException("its LZ4 data does not decode");
                    written += n;
                }
                pos += length + (blockChecksum ? 4 : 0);
            }
            return written;
        }

        public void Compress(ReadOnlyMemory<byte> source, Stream destination) => throw new NotSupportedException("read only");
        public void Compress(ReadOnlyMemory<byte> source, Stream destination, int? compressionLevel) => throw new NotSupportedException("read only");
        public void Dispose() { }
    }
}
