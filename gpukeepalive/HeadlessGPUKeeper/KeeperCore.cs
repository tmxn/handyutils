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

    /// <summary>
    /// Idle-mode pokes are suppressed while the adapter's dedicated usage is above
    /// this: right after llama-server exits its VRAM takes a moment to be released,
    /// and we want the GPU back at its idle baseline as fast as possible.
    /// </summary>
    public const double IdlePokeVramLimitMb = 150.0;

    const int StateSize = 24;
    const int LegacyStateSize = 16; // pre-VRAM-check layout
    const int OffsetMagic = 0;
    const int OffsetLastPokeTicks = 4;
    const int OffsetMode = 12;
    const int OffsetLastVramCheckTicks = 16;

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
    long _lastVramCheckTicks;
    bool _idlePoking;
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
    /// True once an idle poke has fired since the last active period. The UI uses
    /// this to close unconditionally after its tail expires: poking itself allocates
    /// enough VRAM to trip the baseline check again, so usage can no longer extend
    /// the linger.
    /// </summary>
    public bool IdlePoking => _idlePoking;

    /// <summary>
    /// One tick of the keep-alive loop. Returns the current mode (active/idle).
    /// vramMb is the adapter's current dedicated usage; when provided and above
    /// IdlePokeVramLimitMb, idle-mode pokes are deferred (e.g. while the VRAM of a
    /// just-exited llama-server is still being released). null means unknown and
    /// keeps the original unconditional idle poke cadence.
    /// </summary>
    public int Tick(double? vramMb = null)
    {
        long now = DateTime.UtcNow.Ticks;
        if (vramMb is not null) _lastVramCheckTicks = now;
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
                // A new active period restarts the post-exit release phase.
                _idlePoking = false;
                _state.Write(_lastPokeTicks, ModeActive, _lastVramCheckTicks);
            }
            else
            {
                // Wait for the adapter to drop back to its idle baseline before
                // poking again (llama-server's VRAM may still be in the process of
                // being released).
                bool atBaseline = vramMb is not double v || v <= IdlePokeVramLimitMb;
                if (atBaseline && now - _lastPokeTicks >= idleIntervalMs * TimeSpan.TicksPerMillisecond)
                {
                    Poke(_targetAdapter);
                    _lastPokeTicks = now;
                    _idlePoking = true;
                }
                _state.Write(_lastPokeTicks, ModeIdle, _lastVramCheckTicks);
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
            // Tolerate a shorter state file left over from an older version.
            MemoryMappedViewAccessor view;
            try
            {
                view = file.CreateViewAccessor(0, StateSize);
            }
            catch (ArgumentException)
            {
                view = file.CreateViewAccessor(0, LegacyStateSize);
            }
            using var v = view;
            long readable = v.Capacity;
            uint magic = v.ReadUInt32(OffsetMagic);
            if (magic == StateMagic)
            {
                long ticks = v.ReadInt64(OffsetLastPokeTicks);
                int mode = v.ReadInt32(OffsetMode);
                string modeStr = mode == ModeActive ? "Active (llama-server running)" : "Idle";
                string lastVramCheck = readable >= OffsetLastVramCheckTicks + 8
                    ? DescribeAgo(v.ReadInt64(OffsetLastVramCheckTicks))
                    : "never checked (older version running)";
                message = $"Another HeadlessGPUKeeper is already running.\n\nMode: {modeStr}\nLast GPU poke: {DescribeAgo(ticks)}\nLast VRAM check: {lastVramCheck}";
            }
        }
        catch { }

        return message;
    }

    static string DescribeAgo(long ticks)
        => ticks > 0
            ? $"{Math.Max(0, (long)((DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerSecond))} seconds ago"
            : "never";

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

        public void Write(long lastPokeTicks, int mode, long lastVramCheckTicks)
        {
            view.Write(OffsetLastPokeTicks, lastPokeTicks);
            view.Write(OffsetMode, mode);
            view.Write(OffsetLastVramCheckTicks, lastVramCheckTicks);
        }

        public void Dispose()
        {
            view.Dispose();
            file.Dispose();
        }
    }
}
