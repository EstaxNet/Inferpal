// Language polyfills for the net472 target.
//
// The C# 12 compiler accepts `record`, `init`, `s[..n]` ranges and collection expressions on any
// TFM - provided the support types exist somewhere. On .NET 8 they are in the BCL; on .NET
// Framework 4.7.2 they do not exist, and an assembly is explicitly allowed to declare them itself.
// That is what this file does, so the sources shared with the Core (signal bus, debugger DTOs)
// compile on both sides WITHOUT being rewritten in an older dialect - sharing source is only worth
// it if it forces nobody to write worse code.
//
// Add nothing here that is not a pure compiler support type.
//
// The whole file is net472-only: on the net8 leg (the one the tests use) these types exist in the
// BCL, and redeclaring them would shadow the real ones.
#if NETFRAMEWORK

namespace System.Runtime.CompilerServices
{
    /// <summary>Support for <c>init</c> accessors (and therefore positional <c>record</c>s).</summary>
    internal static class IsExternalInit { }
}

namespace System
{
    /// <summary>Support for the <c>^n</c> index operator.</summary>
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public static Index Start     => new Index(0);
        public static Index End       => new Index(~0);
        public int Value              => _value < 0 ? ~_value : _value;
        public bool IsFromEnd         => _value < 0;

        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        public static implicit operator Index(int value) => new Index(value);

        public bool Equals(Index other)   => _value == other._value;
        public override bool Equals(object? obj) => obj is Index other && Equals(other);
        public override int GetHashCode() => _value;
    }

    /// <summary>Support des plages <c>a..b</c>.</summary>
    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End   { get; }

        public Range(Index start, Index end) { Start = start; End = end; }

        public static Range All                        => new Range(Index.Start, Index.End);
        public static Range StartAt(Index start)       => new Range(start, Index.End);
        public static Range EndAt(Index end)           => new Range(Index.Start, end);

        /// <summary>Projects the range onto a concrete length — called by generated code.</summary>
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end   = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }

        public bool Equals(Range other)   => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range other && Equals(other);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
    }
}

#endif
