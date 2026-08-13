using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace HeadlessGpuKeeper;

class Program
{
    const string MutexName = @"Local\HeadlessGPUKeeper";
    const string StateName = @"Local\HeadlessGPUKeeperState";

    const int ModeIdle = 0;
    const int ModeActive = 1;
    const uint StateMagic = 0x4750_484B; // "GPHK"

    // Loop wakes every few seconds so a newly started llama-server is detected quickly,
    // but in idle mode we only poke after roughly a minute so VRAM can be evicted.
    const int loopIntervalMs = 5000;
    const int idleIntervalMs = 7500;

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr software, uint flags, IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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

    // Fixed offsets within the memory-mapped file shared between the running instance
    // (writer) and an instance that fails the single-instance guard (reader). Using
    // explicit offsets avoids any interop struct layout/padding surprises.
    const int StateSize = 16;
    const int OffsetMagic = 0;
    const int OffsetLastPokeTicks = 4;
    const int OffsetMode = 12;

    static int Main()
    {
        using (var mutex = new Mutex(true, MutexName, out bool createdNew))
        {
            if (!createdNew)
            {
                ShowExistingInstanceInfo();
                return 1;
            }
            Run();
            GC.KeepAlive(mutex);
        }
        return 0;
    }

    static void ShowExistingInstanceInfo()
    {
        string message = "Another HeadlessGPUKeeper is already running.";
        try
        {
            // Open with default (read-write) access: opening with MemoryMappedFileRights.Read
            // throws UnauthorizedAccessException against the writer's mapping.
            using var file = MemoryMappedFile.OpenExisting(StateName);
            using var view = file.CreateViewAccessor(0, StateSize);
            uint magic = view.ReadUInt32(OffsetMagic);
            if (magic == StateMagic)
            {
                long ticks = view.ReadInt64(OffsetLastPokeTicks);
                int mode = view.ReadInt32(OffsetMode);
                string modeStr = mode == ModeActive ? "Active (llama-server running)" : "Idle";
                string lastPokeAgo;
                if (ticks > 0)
                {
                    var elapsed = DateTime.UtcNow.Ticks - ticks;
                    var secondsAgo = Math.Max(0, (long)(elapsed / TimeSpan.TicksPerSecond));
                    lastPokeAgo = $"poked {secondsAgo} seconds ago";
                }
                else
                {
                    lastPokeAgo = "never poked";
                }
                message =
                    $"Another HeadlessGPUKeeper is already running.\n\n" +
                    $"Mode: {modeStr}\n" +
                    $"Last GPU poke: {lastPokeAgo}";
            }
        }
        catch { /* stale or not-yet-created shared state; fall through */ }

        MessageBoxW(IntPtr.Zero, message, "HeadlessGPUKeeper", 0x40); // MB_ICONINFORMATION
    }

    static void Run()
    {
        Guid factoryGuid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
        if (CreateDXGIFactory(ref factoryGuid, out IntPtr factoryPtr) != 0) return;

        var factory = (IDXGIFactory)Marshal.GetObjectForIUnknown(factoryPtr);
        IntPtr targetAdapter = IntPtr.Zero;
        uint index = 0;

        while (factory.EnumAdapters(index++, out IntPtr adapterPtr) == 0)
        {
            var adapter = (IDXGIAdapter)Marshal.GetObjectForIUnknown(adapterPtr);
            if (adapter.GetDesc(out var desc) == 0)
            {
                if (desc.Description.Contains("Radeon") || desc.Description.Contains("7900"))
                {
                    targetAdapter = adapterPtr;
                    break;
                }
            }
            Marshal.Release(adapterPtr);
        }

        if (targetAdapter == IntPtr.Zero) return;

        using var state = new StateFile();
        long lastPokeTicks = 0;

        while (true)
        {
            bool active = LlamaServerRunning();
            long now = DateTime.UtcNow.Ticks;

            if (active)
            {
                Poke(targetAdapter);
                lastPokeTicks = now;
                state.Write(now, ModeActive);
            }
            else if (now - lastPokeTicks >= idleIntervalMs * TimeSpan.TicksPerMillisecond)
            {
                Poke(targetAdapter);
                lastPokeTicks = now;
                state.Write(now, ModeIdle);
            }
            else
            {
                state.Write(lastPokeTicks, ModeIdle);
            }

            Thread.Sleep(loopIntervalMs);
        }
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

    static bool LlamaServerRunning()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("llama-server"); }
        catch { return false; }
        bool running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        return running;
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
