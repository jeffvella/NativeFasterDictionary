using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Experiment
{
    /// <summary>
    /// Just a quick unmanaged string, its got some issues with displaying the string in
    /// Visual studio debugging but the values do come out correctly at runtime.
    /// </summary>
    public unsafe struct StringKey : IComparable<StringKey>, IEquatable<StringKey>
    {
        private fixed byte _bytes[CharCapacity * sizeof(char)];
        private readonly int _usedBytes;
        public readonly int _hash;
        public readonly int CharCount;
        public const int CharCapacity = 20;

        public StringKey(string input)
        {
            CharCount = input.Length > CharCapacity ? CharCapacity : input.Length;
            _usedBytes = CharCount * sizeof(char);

            fixed (char* src = input)
            fixed (byte* dst = _bytes)
            {
                UnsafeUtility.MemCpy(dst, src, _usedBytes);

                unchecked
                {
                    _hash = _bytes[0];
                    for (int i = 1; i < _usedBytes; i++)
                    {
                        _hash = (_hash * 397) ^ _bytes[i];
                    }
                }
            }
        }

        public string Value
        {
            get
            {
                char[] chars = new char[CharCount];
                fixed (byte* src = _bytes)
                fixed (char* dst = chars)
                {
                    UnsafeUtility.MemCpy(dst, src, _usedBytes);
                }
                return new string(chars);
            }
        }

        public int CompareTo(StringKey other)
        {
            fixed (byte* ptr = _bytes)
            {
                return UnsafeUtility.MemCmp(ptr, other._bytes, CharCount);
            }
        }

        public override bool Equals(object obj)
        {
            return !ReferenceEquals(null, obj) && (obj is StringKey other && Equals(other));
        }

        public bool Equals(StringKey other) => CompareTo(other) == 0;

        public override int GetHashCode() => _hash;
    }
}