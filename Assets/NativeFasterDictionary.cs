using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Experiment
{
    public struct NativeFasterDictionaryData
    {
        public int FreeValueCellIndex;
        public int Collisions;
        public int Capacity;
        public NativeFasterDictionaryLayout Layout;
        public NativeSpan Values;
        public NativeSpan Nodes;
        public NativeSpan Buckets;
    }

    public struct NativeFasterDictionaryLayout
    {
        public int HeaderOffset;
        public int HeaderBytes;
        public int NodesOffset;
        public int NodesBytes;
        public int NodeItemSize;
        public int NodesCount;
        public int BucketsOffset;
        public int BucketsBytes;
        public int BucketItemSize;
        public int BucketsCount;
        public int ValuesOffset;
        public int ValuesBytes;
        public int ValueItemSize;
        public int ValuesCount;
        public int AllocationBytes;
    }

    // The original design of this dictionary is from https://github.com/sebas77/Svelto.ECS by Sebas77
    // It was modified to native code so that it can run in burst compiled jobs.

    // The intention with this design is that the values data is stored sequentially together in a continuous area so
    // that it can be quickly directly accessed by index and extracted to a NativeArray.

    [DebuggerTypeProxy(typeof(NativeFasterDictionary<,>.NativeFasterDictionaryDebugView <,>))]
    public unsafe struct NativeFasterDictionary<TKey, TValue> : IDisposable where TKey : struct, IComparable<TKey> where TValue : struct
    {
        [NativeDisableUnsafePtrRestriction]
        public NativeFasterDictionaryData* Data;
        private readonly Allocator _allocator;
        private static TValue _defaultValue;
        private readonly int _isCreated;

        [DebuggerDisplay("Key={Key} Hash={Hashcode} [{Previous}|{Next}]")]
        public struct Node
        {
            public readonly TKey Key;
            public readonly int Hashcode;
            public int Previous;
            public int Next;

            public Node(ref TKey key, int hash, int previousNode)
            {
                Key = key;
                Hashcode = hash;
                Previous = previousNode;
                Next = -1;
            }

            public Node(ref TKey key, int hash)
            {
                Key = key;
                Hashcode = hash;
                Previous = -1;
                Next = -1;
            }
        }

        public NativeFasterDictionary(int size, Allocator allocator) : this()
        {
            Data = Allocate(size, allocator);
            _allocator = allocator;
            _isCreated = 1;
        }

        private void Copy(NativeFasterDictionaryData* src, NativeFasterDictionaryData* dst)
        {
            if (src->Layout.AllocationBytes > dst->Layout.AllocationBytes)
                throw new Exception("Shrinking allocation is not supported");

            dst->FreeValueCellIndex = src->FreeValueCellIndex;
            dst->Collisions = src->Collisions;

            UnsafeUtility.MemCpy(dst->Nodes.GetUnsafePtr(), src->Nodes.GetUnsafePtr(), src->Layout.NodesBytes);
            UnsafeUtility.MemCpy(dst->Values.GetUnsafePtr(), src->Values.GetUnsafePtr(), src->Layout.ValuesBytes);
            UnsafeUtility.MemCpy(dst->Buckets.GetUnsafePtr(), src->Buckets.GetUnsafePtr(), src->Layout.BucketsBytes);
        }

        private NativeFasterDictionaryData* Allocate(int size, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            var layout = CalculateLayout(size);

            var ptr = UnsafeUtility.Malloc(layout.AllocationBytes, UnsafeUtility.AlignOf<int>(), allocator);

            if (options == NativeArrayOptions.ClearMemory)
            {
                UnsafeUtility.MemClear(ptr, layout.AllocationBytes);
            }

            var header = new NativeFasterDictionaryData
            {
                //BaseAddress = ptr,
                FreeValueCellIndex = 0,
                Collisions = 0,
                Capacity = size,
                Layout = layout,
                Nodes = NativeSpan.Assign<Node>((IntPtr)ptr + layout.NodesOffset, layout.NodesCount),
                Values = NativeSpan.Assign<TValue>((IntPtr)ptr + layout.ValuesOffset, layout.ValuesCount),
                Buckets = NativeSpan.Assign<int>((IntPtr)ptr + layout.BucketsOffset, layout.BucketsCount),
            };

            UnsafeUtility.CopyStructureToPtr(ref header, ptr);
            return CastPtr<NativeFasterDictionaryData>(ptr, 0);
        }

        public NativeFasterDictionaryLayout CalculateLayout(int size)
        {
            // - Header
            // - NodesData
            // - ValuesData
            // - BucketsData

            NativeFasterDictionaryLayout layout;

            layout.NodeItemSize = UnsafeUtility.SizeOf<Node>();
            layout.ValueItemSize = UnsafeUtility.SizeOf<TValue>();
            layout.BucketItemSize = UnsafeUtility.SizeOf<int>();

            layout.ValuesCount = size;
            layout.NodesCount = size;
            layout.BucketsCount = HashHelpers.GetPrime(size);

            layout.HeaderBytes = UnsafeUtility.SizeOf<NativeFasterDictionaryData>();
            layout.NodesBytes = layout.NodeItemSize * layout.NodesCount;
            layout.ValuesBytes = layout.ValueItemSize * layout.ValuesCount;
            layout.BucketsBytes = layout.BucketItemSize * layout.BucketsCount;

            layout.HeaderOffset = 0;
            layout.ValuesOffset = layout.HeaderBytes;
            layout.NodesOffset = layout.ValuesOffset + layout.ValuesBytes;
            layout.BucketsOffset = layout.NodesOffset + layout.NodesBytes;

            layout.AllocationBytes = layout.HeaderBytes + layout.ValuesBytes + layout.NodesBytes + layout.BucketsBytes;

            return layout;
        }

        public T* CastPtr<T>(void* ptr, int offset) where T : unmanaged
        {
            return (T*)((IntPtr)ptr + offset);
        }

        public TValue[] GetValuesArray(out int count)
        {
            count = Data->FreeValueCellIndex;
            return Data->Values.ToArray<TValue>();
        }

        public int Length => Data->FreeValueCellIndex;

        public void Add(TKey key, TValue value)
        {
            Add(key, ref value);
        }

        public void Add(TKey key, ref TValue value)
        {
            if (AddValue(key, ref value) == false)
            {
                throw new ArgumentException("Key already present");
            }
        }

        public void Expand(int newSize)
        {
            if (newSize <= Data->Capacity)
                return;

            var newData = Allocate(newSize, _allocator);
            var oldData = Data;

            Copy(oldData, newData);

            Data = newData;

            UnsafeUtility.Free(oldData, _allocator);
        }

        public void Clear()
        {
            if (Data->FreeValueCellIndex == 0)
                return;

            Data->FreeValueCellIndex = 0;
            Data->Buckets.Clear();
            Data->Values.Clear();
            Data->Nodes.Clear();
        }

        public bool ContainsKey(TKey key)
        {
            return TryFindIndex(key, out int findIndex);
        }

        public bool TryGetValue(TKey key, out TValue result)
        {
            if (TryFindIndex(key, out var findIndex))
            {
                result = Data->Values.GetItem<TValue>(findIndex);
                return true;
            }
            result = default;
            return false;
        }

        public TryResult<TValue> TryGetValue(TKey key)
        {
            if (TryFindIndex(key, out var findIndex))
            {
                return new TryResult<TValue>
                {
                    HasValue = true,
                    Ptr = Data->Values.AsPtr(findIndex)
                };
            }
            return default;
        }

        public ref struct TryResult<T> where T : struct
        {
            public bool HasValue;
            internal void* Ptr;

            public ref T Value => ref UnsafeUtilityEx.AsRef<T>(Ptr);
        }

        public TValue this[TKey key]
        {
            get => GetValue(key);
            set => AddValue(key, ref value);
        }

        public ref TValue GetValue(TKey key)
        {
            if (TryFindIndex(key, out var findIndex))
            {
                return ref Data->Values.AsRef<TValue>(findIndex);
            }
            throw new KeyNotFoundException();
        }

        public ref TValue GetValue(int index)
        {
            return ref Data->Values.AsRef<TValue>(index);
        }

        static int Hash(TKey key)
        {
            return key.GetHashCode() & 0x7FFFFFFF;
        }

        static uint Reduce(uint x, uint n)
        {
            if (x >= n)
            {
                var hash = (11400714819323198485 * x);
                hash >>= 32;

                return (uint)((hash * n) >> 32);
            }

            return x;
        }

        private bool AddValue(TKey key, ref TValue value)
        {
            int hash = Hash(key);
            int bucketIndex = (int)Reduce((uint)hash, (uint)Data->Buckets.Length);
            int valueIndex = Data->Buckets.GetItem<int>(bucketIndex) - 1;

            if (valueIndex == -1)
            {
                GetFreeNodeRef() = new Node(ref key, hash);
            }
            else //collision or already exists
            {
                int currentValueIndex = valueIndex;

                for (int i = 0; i < Data->Nodes.Length; i++)
                {
                    if (currentValueIndex == -1)
                        break;

                    ref var node = ref Data->Nodes.AsRef<Node>(currentValueIndex);
                    if (node.Hashcode == hash && node.Key.CompareTo(key) == 0)
                    {
                        Data->Values.AsRef<TValue>(currentValueIndex) = value;
                        return false;
                    }
                    currentValueIndex = node.Previous;
                }

                Data->Collisions++;

                GetFreeNodeRef() = new Node(ref key, hash, valueIndex);

                Data->Nodes.AsRef<Node>(valueIndex).Next = Data->FreeValueCellIndex;
            }

            Data->Buckets.AsRef<int>(bucketIndex) = Data->FreeValueCellIndex + 1;
            GetFreeValueRef() = value;
            Data->FreeValueCellIndex++;

            if (Data->FreeValueCellIndex == Data->Values.Length)
            {
                RemapCollisions();
            }
            if (Data->Collisions > Data->Buckets.Length)
            {
                RemapCollisions();
            }
            return true;
        }

        private void RemapCollisions()
        {
            Expand(HashHelpers.ExpandPrime(Data->Capacity));

            Data->Collisions = 0;

            for (int i = 0; i < Data->FreeValueCellIndex; i++)
            {
                ref var node = ref Data->Nodes.AsRef<Node>(i);

                var bucketIndex = (int)Reduce((uint)node.Hashcode, (uint)Data->Buckets.Length - 1);
                ref var bucketValue = ref Data->Buckets.AsRef<int>(bucketIndex);
                int existingValueIndex = bucketValue - 1;

                bucketValue = i + 1;

                if (existingValueIndex != -1)
                {
                    Data->Collisions++;
                    node.Previous = existingValueIndex;
                    node.Next = -1;
                    Data->Nodes.AsRef<Node>(existingValueIndex).Next = i;
                }
                else
                {
                    node.Next = -1;
                    node.Previous = -1;
                }
            }
        }

        public bool Remove(TKey key)
        {
            int hash = Hash(key);
            int bucketIndex = (int)Reduce((uint)hash, (uint)Data->Buckets.Length);
            ref var bucketIndexRef = ref Data->Buckets.AsRef<int>(bucketIndex);

            int indexToValueToRemove = bucketIndexRef - 1;
            while (indexToValueToRemove != -1)
            {
                ref var nodeToRemove = ref Data->Nodes.AsRef<Node>(indexToValueToRemove);

                if (nodeToRemove.Hashcode == hash && nodeToRemove.Key.CompareTo(key) == 0)
                {
                    if (bucketIndexRef - 1 == indexToValueToRemove)
                    {
                        int value = nodeToRemove.Previous;
                        bucketIndexRef = value + 1;
                    }

                    int next = nodeToRemove.Next;
                    int previous = nodeToRemove.Previous;

                    if (next != -1)
                    {
                        Data->Nodes.AsRef<Node>(next).Previous = previous;
                    }
                    if (previous != -1)
                    {
                        Data->Nodes.AsRef<Node>(previous).Next = next;
                    }
                    break;
                }
                indexToValueToRemove = nodeToRemove.Previous;
            }

            if (indexToValueToRemove == -1)
                return false; //not found!

            Data->FreeValueCellIndex--;

            if (indexToValueToRemove != Data->FreeValueCellIndex)
            {
                ref var freeNode = ref GetFreeNodeRef();
                int movingBucketIndex = (int)Reduce((uint)freeNode.Hashcode, (uint)Data->Buckets.Length);

                ref var tmpBucketIndex = ref Data->Buckets.AsRef<int>(movingBucketIndex);
                if (tmpBucketIndex - 1 == Data->FreeValueCellIndex)
                    tmpBucketIndex = indexToValueToRemove + 1;

                if (freeNode.Next != -1)
                {
                    Data->Nodes.AsRef<Node>(freeNode.Next).Previous = indexToValueToRemove;
                }
                if (freeNode.Previous != -1)
                {
                    Data->Nodes.AsRef<Node>(freeNode.Previous).Next = indexToValueToRemove;
                }
                Data->Nodes.AsRef<Node>(indexToValueToRemove) = freeNode;
                Data->Values.AsRef<TValue>(indexToValueToRemove) = GetFreeValueRef();
            }
            return true;
        }

        public ref Node GetFreeNodeRef() => ref Data->Nodes.AsRef<Node>(Data->FreeValueCellIndex);

        public ref TValue GetFreeValueRef() => ref Data->Values.AsRef<TValue>(Data->FreeValueCellIndex);

        private int GetValueIndexFromHash(int hash)
        {
            // All the index are stored with an offset + 1, so that in the bucket list 0 means actually not existing.
            // When read the offset must be offset by -1 again to be the real one. In this way I avoid to initialize the array to -1
            int bucketIndex = (int)Reduce((uint)hash, (uint)Data->Buckets.Length);
            return Data->Buckets.GetItem<int>(bucketIndex) - 1;
        }

        public bool TryFindIndex(TKey key, out int findIndex)
        {
            int hash = Hash(key);
            int valueIndex = GetValueIndexFromHash(hash);
            if (valueIndex > Data->Nodes.m_MaxIndex)
            {
                throw new IndexOutOfRangeException($"Index: {valueIndex} is outside the valid range [0-{Data->Nodes.m_MaxIndex}]");
            }

            for (int i = 0; i < Data->Nodes.Length; i++)
            {
                if (valueIndex < 0)
                    break;

                ref var node = ref Data->Nodes.AsRef<Node>(valueIndex);
                if (node.Hashcode == hash && node.Key.CompareTo(key) == 0)
                {
                    findIndex = valueIndex;
                    return true;
                }
                valueIndex = node.Previous;
            }
            findIndex = 0;
            return false;
        }

        public void Dispose()
        {
            if (_isCreated == 1)
            {
                UnsafeUtility.Free(Data, _allocator);
            }
            Data = null;
        }

        internal sealed class NativeFasterDictionaryDebugView<TKey, TValue> where TKey : struct, IComparable<TKey> where TValue : struct
        {
            private NativeFasterDictionary<TKey, TValue> _source;

            public NativeFasterDictionaryDebugView(NativeFasterDictionary<TKey, TValue> source)
            {
                _source = source;
            }

            public NativeFasterDictionaryData Header => *_source.Data;

            public TValue[] Values => _source.Data->Values.ToArray<TValue>();

            public NativeFasterDictionary<TKey, TValue>.Node[] Nodes
            {
                get => _source.Data->Nodes.ToArray<NativeFasterDictionary<TKey, TValue>.Node>();
            }

            public int[] Buckets => _source.Data->Buckets.ToArray<int>();

            public int Length => _source.Length;
        }

        public static class HashHelpers
        {
            public const int MaxPrimeArrayLength = 0x7FEFFFFD;
            public const int HashCollisionThreshold = 100;
            public const int HashPrime = 101;

            public static readonly int[] Primes =
            {
                3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
                1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
                17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
                187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
                1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369
            };

            public static bool IsPrime(int candidate)
            {
                if ((candidate & 1) != 0)
                {
                    int limit = (int)Math.Sqrt(candidate);
                    for (int divisor = 3; divisor <= limit; divisor += 2)
                    {
                        if ((candidate % divisor) == 0)
                            return false;
                    }
                    return true;
                }
                return (candidate == 2);
            }

            public static int GetPrime(int min)
            {
                if (min < 0)
                {
                    throw new ArgumentException();
                }
                for (int i = 0; i < Primes.Length; i++)
                {
                    int prime = Primes[i];
                    if (prime >= min)
                        return prime;
                }
                for (int i = (min | 1); i < int.MaxValue; i += 2)
                {
                    if (IsPrime(i) && ((i - 1) % HashPrime != 0))
                        return i;
                }
                return min;
            }

            public static int ExpandPrime(int oldSize)
            {
                int newSize = 2 * oldSize;
                if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize)
                {
                    return MaxPrimeArrayLength;
                }
                return GetPrime(newSize);
            }
        }

    }

}