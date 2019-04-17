using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Experiment
{
    /// <summary>
    /// This is pretty much just a NativeArray with all the safety removed
    /// and with the ability to assign it on top of an existing data allocation.
    /// </summary>
    [NativeContainer]
    [DebuggerDisplay("Length = {Length}")]
    [NativeContainerSupportsMinMaxWriteRestriction]
    public unsafe struct NativeSpan
    {
        [NativeDisableUnsafePtrRestriction]
        internal unsafe void* m_Buffer;
        internal int m_Length;
        internal int m_MinIndex;
        internal int m_MaxIndex;
        internal int _itemSize;

        public int Length => m_Length;

        public static unsafe NativeSpan Assign(void* ptr, int itemSize, int length)
        {
            NativeSpan buffer;
            buffer.m_Buffer = ptr;
            buffer.m_Length = length;
            buffer._itemSize = itemSize;
            buffer.m_MinIndex = 0;
            buffer.m_MaxIndex = length - 1;
            return buffer;
        }

        public static unsafe NativeSpan Assign<T>(void* ptr, int length) where T : struct
        {
            return Assign(ptr, UnsafeUtility.SizeOf<T>(), length);
        }

        public static unsafe NativeSpan Assign<T>(IntPtr ptr, int length) where T : struct
        {
            return Assign((void*)ptr, UnsafeUtility.SizeOf<T>(), length);
        }

        public unsafe void Clear()
        {
            UnsafeUtility.MemClear(m_Buffer, (long)Length * _itemSize);
        }

        [BurstDiscard]
        internal static void IsBlittableAndThrow<T>() where T : struct
        {
            if (!UnsafeUtility.IsBlittable<T>())
                throw new InvalidOperationException(string.Format("{0} used in NativeArray2<{1}> must be blittable.", (object)typeof(T), (object)typeof(T)));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private unsafe void CheckElementReadAccess(int index)
        {
            if (index < m_MinIndex || index > m_MaxIndex)
                FailOutOfRangeError(index);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private unsafe void CheckElementWriteAccess(int index)
        {
            if (index < m_MinIndex || index > m_MaxIndex)
                FailOutOfRangeError(index);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private unsafe void CheckElementWriteAccess(int index, int offset)
        {
            if (index < m_MinIndex || index > m_MaxIndex)
                FailOutOfRangeError(index);

            if (offset >= _itemSize)
                throw new ArgumentOutOfRangeException("Offset within an item cannot be larger than the item size");
        }

        public unsafe int Count<T>() where T : struct => m_Length * UnsafeUtility.SizeOf<T>();

        public unsafe T GetItem<T>(int index)
        {
            CheckElementReadAccess(index);
            return UnsafeUtility.ReadArrayElement<T>(m_Buffer, index);
        }

        public unsafe ref T AsRef<T>(int index) where T : struct
        {
            return ref UnsafeUtilityEx.AsRef<T>((void*)((IntPtr)m_Buffer + (UnsafeUtility.SizeOf<T>() * index)));
        }

        public unsafe Enumerator<T> GetEnumerator<T>() where T : struct
            => new Enumerator<T>((int*)m_Buffer, Length);

        public unsafe struct Enumerator<T> where T : struct
        {
            private readonly int* _ptr;
            private readonly int _length;
            private int _index;

            public Enumerator(int* offsetPtr, int length)
            {
                _ptr = offsetPtr;
                _length = length;
                _index = -1;
            }

            public T Current => UnsafeUtilityEx.ArrayElementAsRef<T>((byte*)_ptr, _index);

            public bool MoveNext() => ++_index < _length;
        }

        public unsafe ref T AsRef<T>(int index, int offset) where T : struct
        {
            return ref UnsafeUtilityEx.AsRef<T>((void*)((IntPtr)m_Buffer + (UnsafeUtility.SizeOf<T>() * index) + offset));
        }

        public unsafe T* AsPtr<T>(int index) where T : unmanaged
        {
            return (T*)((IntPtr)m_Buffer + UnsafeUtility.SizeOf<T>() * index);
        }

        public unsafe T* AsPtr<T>(int index, int offset) where T : unmanaged
        {
            return (T*)((IntPtr)m_Buffer + (UnsafeUtility.SizeOf<T>() * index) + offset);
        }

        public unsafe void* AsPtr(int index)
        {
            return (void*)((IntPtr)m_Buffer + _itemSize * index);
        }

        public unsafe void SetItem<T>(int index, T value)
        {
            CheckElementWriteAccess(index);
            UnsafeUtility.WriteArrayElement(m_Buffer, index, value);
        }

        public unsafe void SetItem<T>(int index, T value, int offset)
        {
            CheckElementWriteAccess(index, offset);
            UnsafeUtility.WriteArrayElement((void*)((IntPtr)m_Buffer + offset), index, value);
        }

        public void CopyTo<T>(T[] array) where T : struct
        {
            Copy(this, array);
        }

        public void CopyTo<T>(NativeArray<T> array) where T : struct
        {
            Copy(this, array);
        }

        public void CopyTo<T>(NativeSpan buffer) where T : struct
        {
            Copy<T>(this, buffer);
        }

        public T[] ToArray<T>() where T : struct
        {
            T[] dst = new T[Length];
            Copy(this, dst);
            return dst;
        }

        public T[] ToArray<T>(int length) where T : struct
        {
            T[] dst = new T[length];
            Copy(this, dst, length);
            return dst;
        }

        private void FailOutOfRangeError(int index)
        {
            if (index < Length && (m_MinIndex != 0 || m_MaxIndex != Length - 1))
                throw new IndexOutOfRangeException(string.Format("Index {0} is out of restricted IJobParallelFor range [{1}...{2}] in ReadWriteBuffer.\n", (object)index, (object)m_MinIndex, (object)m_MaxIndex) + "ReadWriteBuffers are restricted to only read & write the element at the job index. You can use double buffering strategies to avoid race conditions due to reading & writing in parallel to the same elements from a job.");
            throw new IndexOutOfRangeException(string.Format("Index {0} is out of range of '{1}' Length.", (object)index, (object)Length));
        }

        public static void Copy<T>(NativeSpan src, NativeSpan dst) where T : struct
        {
            if (src.Length != dst.Length)
                throw new ArgumentException("source and destination length must be the same");

            Copy<T>(src, 0, dst, 0, src.Length);
        }

        public static void Copy<T>(NativeSpan src, NativeArray<T> dst) where T : struct
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(dst));
#endif
            if (src.Length != dst.Length)
                throw new ArgumentException("source and destination length must be the same");

            Copy(src, 0, dst, 0, src.Length);
        }

        public static void Copy<T>(T[] src, NativeSpan dst) where T : struct
        {
            if (src.Length != dst.Length)
                throw new ArgumentException("source and destination length must be the same");
            Copy(src, 0, dst, 0, src.Length);
        }

        public static void Copy<T>(NativeSpan src, T[] dst) where T : struct
        {
            if (src.Length != dst.Length)
                throw new ArgumentException("source and destination length must be the same");

            Copy(src, 0, dst, 0, src.Length);
        }

        public static void Copy<T>(NativeSpan src, NativeSpan dst, int length) where T : struct
        {
            Copy<T>(src, 0, dst, 0, length);
        }

        public static void Copy<T>(T[] src, NativeSpan dst, int length) where T : struct
        {
            Copy(src, 0, dst, 0, length);
        }

        public static void Copy<T>(NativeSpan src, T[] dst, int length) where T : struct
        {
            Copy(src, 0, dst, 0, length);
        }

        public static unsafe void Copy<T>(NativeSpan src, int srcIndex, NativeSpan dst, int dstIndex, int length) where T : struct
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "length must be equal or greater than zero.");
            if (srcIndex < 0 || srcIndex > src.Length || srcIndex == src.Length && src.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(srcIndex), "srcIndex is outside the range of valid indexes for the source NativeArray2.");
            if (dstIndex < 0 || dstIndex > dst.Length || dstIndex == dst.Length && dst.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(dstIndex), "dstIndex is outside the range of valid indexes for the destination NativeArray2.");
            if (srcIndex + length > src.Length)
                throw new ArgumentException("length is greater than the number of elements from srcIndex to the end of the source NativeArray2.", nameof(length));
            if (dstIndex + length > dst.Length)
                throw new ArgumentException("length is greater than the number of elements from dstIndex to the end of the destination NativeArray2.", nameof(length));
            UnsafeUtility.MemCpy((void*)((IntPtr)dst.m_Buffer + (dstIndex * UnsafeUtility.SizeOf<T>())), (void*)((IntPtr)src.m_Buffer + (srcIndex * UnsafeUtility.SizeOf<T>())), (long)(length * UnsafeUtility.SizeOf<T>()));
        }

        public static unsafe void Copy<T>(NativeSpan src, int srcIndex, NativeArray<T> dst, int dstIndex, int length) where T : struct
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(dst));
#endif
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "length must be equal or greater than zero.");
            if (srcIndex < 0 || srcIndex > src.Length || srcIndex == src.Length && src.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(srcIndex), "srcIndex is outside the range of valid indexes for the source NativeArray2.");
            if (dstIndex < 0 || dstIndex > dst.Length || dstIndex == dst.Length && dst.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(dstIndex), "dstIndex is outside the range of valid indexes for the destination NativeArray2.");
            if (srcIndex + length > src.Length)
                throw new ArgumentException("length is greater than the number of elements from srcIndex to the end of the source NativeArray2.", nameof(length));
            if (dstIndex + length > dst.Length)
                throw new ArgumentException("length is greater than the number of elements from dstIndex to the end of the destination NativeArray2.", nameof(length));
            UnsafeUtility.MemCpy((void*)((IntPtr)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(dst) + (dstIndex * UnsafeUtility.SizeOf<T>())), (void*)((IntPtr)src.m_Buffer + (srcIndex * UnsafeUtility.SizeOf<T>())), (long)(length * UnsafeUtility.SizeOf<T>()));
        }

        public static unsafe void Copy<T>(T[] src, int srcIndex, NativeSpan dst, int dstIndex, int length) where T : struct
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "length must be equal or greater than zero.");
            if (srcIndex < 0 || srcIndex > src.Length || srcIndex == src.Length && src.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(srcIndex), "srcIndex is outside the range of valid indexes for the source array.");
            if (dstIndex < 0 || dstIndex > dst.Length || dstIndex == dst.Length && dst.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(dstIndex), "dstIndex is outside the range of valid indexes for the destination NativeArray2.");
            if (srcIndex + length > src.Length)
                throw new ArgumentException("length is greater than the number of elements from srcIndex to the end of the source array.", nameof(length));
            if (dstIndex + length > dst.Length)
                throw new ArgumentException("length is greater than the number of elements from dstIndex to the end of the destination NativeArray2.", nameof(length));
            GCHandle gcHandle = GCHandle.Alloc((object)src, GCHandleType.Pinned);
            IntPtr num = gcHandle.AddrOfPinnedObject();
            UnsafeUtility.MemCpy((void*)((IntPtr)dst.m_Buffer + (dstIndex * UnsafeUtility.SizeOf<T>())), (void*)(num + (srcIndex * UnsafeUtility.SizeOf<T>())), (long)(length * UnsafeUtility.SizeOf<T>()));
            gcHandle.Free();
        }

        public static unsafe void Copy<T>(NativeSpan src, int srcIndex, T[] dst, int dstIndex, int length) where T : struct
        {
            if (dst == null)
                throw new ArgumentNullException(nameof(dst));
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "length must be equal or greater than zero.");
            if (srcIndex < 0 || srcIndex > src.Length || srcIndex == src.Length && src.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(srcIndex), "srcIndex is outside the range of valid indexes for the source NativeArray2.");
            if (dstIndex < 0 || dstIndex > dst.Length || dstIndex == dst.Length && dst.Length > 0)
                throw new ArgumentOutOfRangeException(nameof(dstIndex), "dstIndex is outside the range of valid indexes for the destination array.");
            if (srcIndex + length > src.Length)
                throw new ArgumentException("length is greater than the number of elements from srcIndex to the end of the source NativeArray2.", nameof(length));
            if (dstIndex + length > dst.Length)
                throw new ArgumentException("length is greater than the number of elements from dstIndex to the end of the destination array.", nameof(length));
            GCHandle gcHandle = GCHandle.Alloc((object)dst, GCHandleType.Pinned);
            UnsafeUtility.MemCpy((void*)((IntPtr)(void*)gcHandle.AddrOfPinnedObject() + (dstIndex * UnsafeUtility.SizeOf<T>())), (void*)((IntPtr)src.m_Buffer + (srcIndex * UnsafeUtility.SizeOf<T>())), (long)(length * UnsafeUtility.SizeOf<T>()));
            gcHandle.Free();
        }

        /// <summary>
        /// Calculate an element index based on its memory address
        /// </summary>
        public unsafe int IndexOf(void* elementPtr)
        {
            if (elementPtr == null)
                throw new ArgumentNullException(nameof(elementPtr));

            int offset = (int)elementPtr - (int)(IntPtr)m_Buffer;
            int index = offset / _itemSize;

            if (index < m_MinIndex || index > m_MaxIndex)
                throw new ArgumentOutOfRangeException($"Index '{index}' is out of range ({m_MinIndex}-{m_MaxIndex})");

            return index;
        }

        public unsafe void* GetUnsafePtr()
        {
            return m_Buffer;
        }

        public bool IsValid() => (IntPtr)m_Buffer != IntPtr.Zero;



    }
}