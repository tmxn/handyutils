using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace HeadlessGpuKeeper;

/// <summary>
/// Encapsulates the original HeadlessGPUKeeper behaviour: detect whether llama-server
/// is running, and periodically "poke" the target GPU via a throwaway D3D11 device so
/// its VRAM is not evicted while headless. Shared mode state is exposed through a
/// memory-mapped file for other instances/tools to read.
/// </summary>
public sealed class KeeperCore
{
    public const string StateName = @"Local\HeadlessGPUKeeperState";
    public const int ModeIdle = 0;
    public const int ModeActive = 1;
    public const uint StateMagic = 0x4750_484B; // "GPHK"

    const int loopIntervalMs = 5000;
    const int idleIntervalMs = 7500;
    const int processCheckIntervalMs = 1000;
    const int adapterRetryMs = 1000;

    const int StateSize = 16;
    const int OffsetMagic = 0;
    const int OffsetLastPokeTicks = 4;
    const int OffsetMode = 12;

    const uint TH32CS_SNAPPROCESS = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);


    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr software, uint flags, IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    // IDXGIFactory IID: 7b7166ec-21c7-44ae-b21a-c9ae321ae369
    [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory
    {
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumOutputs(uint Output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC pDesc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId; public uint DeviceId; public uint SubSysId; public uint Revision;
        public UIntPtr DedicatedVideoMemory; public UIntPtr DedicatedSystemMemory; public UIntPtr SharedSystemMemory;
        public uint AdapterLuidLow; public int AdapterLuidHigh;
    }

    readonly StateFile _state = new();
    IntPtr _targetAdapter;
    long _lastPokeTicks;
    bool _llamaRunning;
    long _lastProcessCheckTicks;
    long _lastAdapterSearchTicks;

    public KeeperCore()
    {
        _lastAdapterSearchTicks = DateTime.UtcNow.Ticks;
        _targetAdapter = FindTargetAdapter();
    }

    public IntPtr TargetAdapter => _targetAdapter;

    /// <summary>
    /// One tick of the keep-alive loop. Returns the current mode (active/idle).
    /// </summary>
    public int Tick()
    {
        long now = DateTime.UtcNow.Ticks;
        bool active = LlamaServerRunning(now);

        if (_targetAdapter == IntPtr.Zero &&
            now - _lastAdapterSearchTicks >= adapterRetryMs * TimeSpan.TicksPerMillisecond)
        {
            // The target GPU may not have been enumerable yet (driver still
            // initializing, adapter description not present). Retry briefly.
            _lastAdapterSearchTicks = now;
            _targetAdapter = FindTargetAdapter();
        }

        if (_targetAdapter != IntPtr.Zero)
        {
            if (active)
            {
                // Only poke every loopIntervalMs even though the caller's UI timer
                // ticks more frequently, preserving the original keep-alive cadence.
                if (now - _lastPokeTicks >= loopIntervalMs * TimeSpan.TicksPerMillisecond)
                {
                    Poke(_targetAdapter);
                    _lastPokeTicks = now;
                }
                _state.Write(_lastPokeTicks, ModeActive);
            }
            else if (now - _lastPokeTicks >= idleIntervalMs * TimeSpan.TicksPerMillisecond)
            {
                Poke(_targetAdapter);
                _lastPokeTicks = now;
                _state.Write(now, ModeIdle);
            }
            else
            {
                _state.Write(_lastPokeTicks, ModeIdle);
            }
        }

        return active ? ModeActive : ModeIdle;
    }

    public static string DescribeState()
    {
        string message = "Another HeadlessGPUKeeper is already running.";
        try
        {
            using var file = MemoryMappedFile.OpenExisting(StateName);
            using var view = file.CreateViewAccessor(0, StateSize);
            uint magic = view.ReadUInt32(OffsetMagic);
            if (magic == StateMagic)
            {
                long ticks = view.ReadInt64(OffsetLastPokeTicks);
                int mode = view.ReadInt32(OffsetMode);
                string modeStr = mode == ModeActive ? "Active (llama-server running)" : "Idle";
                string lastPokeAgo = ticks > 0
                    ? $"poked {Math.Max(0, (long)((DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerSecond))} seconds ago"
                    : "never poked";
                message = $"Another HeadlessGPUKeeper is already running.\n\nMode: {modeStr}\nLast GPU poke: {lastPokeAgo}";
            }
        }
        catch { }

        return message;
    }

    IntPtr FindTargetAdapter()
    {
        Guid factoryGuid = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
        if (CreateDXGIFactory(ref factoryGuid, out IntPtr factoryPtr) != 0) return IntPtr.Zero;

        var factory = (IDXGIFactory)Marshal.GetObjectForIUnknown(factoryPtr);
        uint index = 0;
        while (factory.EnumAdapters(index++, out IntPtr adapterPtr) == 0)
        {
            var adapter = (IDXGIAdapter)Marshal.GetObjectForIUnknown(adapterPtr);
            if (adapter.GetDesc(out var desc) == 0)
            {
                if (desc.Description.Contains("Radeon") || desc.Description.Contains("7900"))
                {
                    return adapterPtr;
                }
            }
            Marshal.Release(adapterPtr);
        }
        return IntPtr.Zero;
    }

    static void Poke(IntPtr targetAdapter)
    {
        int hr = D3D11CreateDevice(targetAdapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out IntPtr devicePtr, out _, out IntPtr contextPtr);
        if (hr == 0)
        {
            Marshal.Release(devicePtr);
            Marshal.Release(contextPtr);
        }
    }

    /// <summary>
    /// Returns whether llama-server is running, re-checking the process table at most
    /// once per processCheckIntervalMs. The check is allocation-free (toolhelp32 snapshot),
    /// so it runs on the UI thread at 1 s intervals without GC pressure.
    /// </summary>
    bool LlamaServerRunning(long now)
    {
        if (now - _lastProcessCheckTicks < processCheckIntervalMs * TimeSpan.TicksPerMillisecond)
            return _llamaRunning;

        _lastProcessCheckTicks = now;
        _llamaRunning = CheckLlamaServer();
        return _llamaRunning;
    }

    static bool CheckLlamaServer()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero) return false;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return false;

            do
            {
                if (entry.szExeFile.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            while (Process32Next(snapshot, ref entry));

            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    sealed class StateFile : IDisposable
    {
        readonly MemoryMappedFile file;
        readonly MemoryMappedViewAccessor view;

        public StateFile()
        {
            file = MemoryMappedFile.CreateOrOpen(StateName, StateSize);
            view = file.CreateViewAccessor(0, StateSize);
            view.Write(OffsetMagic, StateMagic);
        }

        public void Write(long lastPokeTicks, int mode)
        {
            view.Write(OffsetLastPokeTicks, lastPokeTicks);
            view.Write(OffsetMode, mode);
        }

        public void Dispose()
        {
            view.Dispose();
            file.Dispose();
        }
    }
}
