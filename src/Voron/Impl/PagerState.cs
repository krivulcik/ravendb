using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

using Voron.Impl.Paging;


namespace Voron.Impl
{
    public unsafe class PagerState
    {
        public sealed class Comparer : IEqualityComparer<PagerState>
        {
            public readonly static Comparer Instance = new Comparer();

            public bool Equals(PagerState x, PagerState y)
            {
                return x == y;
            }

            public int GetHashCode(PagerState obj)
            {
                return obj._pager.GetHashCode();
            }
        }

        private readonly AbstractPager _pager;

        public bool DisposeFilesOnDispose = true;

        public class AllocationInfo
        {
            public MemoryMappedFile MappedFile;
            public byte* BaseAddress;
            public long Size;

            public override string ToString()
            {
                return $"{nameof(BaseAddress)}: {new IntPtr(BaseAddress).ToString("x")} - {new IntPtr(BaseAddress + Size).ToString("x")} {nameof(Size)}: {Size}";
            }
        }

#if DEBUG_PAGER_STATE
        public static ConcurrentDictionary<PagerState, string> Instances = new ConcurrentDictionary<PagerState, string>();
#endif

        public PagerState(AbstractPager pager)
        {
            _pager = pager;
#if DEBUG_PAGER_STATE
            Instances[this] = Environment.StackTrace;
#endif
        }

        private int _refs;

        public MemoryMappedFile[] Files;

        public AllocationInfo[] AllocationInfos;

        public byte* MapBase { get; set; }

        public bool Released;

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0)
                return;

#if DEBUG_PAGER_STATE
            String value;
            Instances.TryRemove(this, out value);
#endif
         
            ReleaseInternal();
        }

        private void ReleaseInternal()
        {
            if (AllocationInfos != null)
            {
                foreach (var allocationInfo in AllocationInfos)
                    _pager.ReleaseAllocationInfo(allocationInfo.BaseAddress, allocationInfo.Size);
            }

            if (Files != null && DisposeFilesOnDispose)
            {
                foreach (var file in Files)
                    file.Dispose();

                Files = null;
            }

            Released = true;
        }

#if DEBUG_PAGER_STATE
        public ConcurrentQueue<string> AddedRefs = new ConcurrentQueue<string>();
#endif

        public void AddRef()
        {
            Interlocked.Increment(ref _refs);
#if DEBUG_PAGER_STATE
            AddedRefs.Enqueue(Environment.StackTrace);
            while (AddedRefs.Count > 500)
            {
                String trace;
                AddedRefs.TryDequeue(out trace);
            }
#endif
        }

        [Conditional("VALIDATE")]
        public void DebugVerify(long size)
        {
            if (AllocationInfos == null)
                return;

            foreach (var allocationInfo in AllocationInfos)
            {
                for (int i = 0; i < allocationInfo.Size; i++)
                {
                    var b = *(allocationInfo.BaseAddress + i);
                    *(allocationInfo.BaseAddress + i) = b;
                }
            }

            for (int i = 0; i < size; i++)
            {
                var b = *(MapBase + i);
                *(MapBase + i) = b;
            }
        }
    }
}