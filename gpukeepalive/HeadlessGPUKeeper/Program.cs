using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HeadlessGpuKeeper;

class Program
{
    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr software, uint flags, IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    // IDXGIFactory IID: 7b7166ec-21c7-44ae-b21a-c9ae321ae369
    [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory
    {
        // IDXGIObject vtable slots
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);

        // IDXGIFactory vtable slots
        [PreserveSig] int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
    }

    // FIXED: True IDXGIAdapter IID ends with 4DC0, not 4D40
    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        // IDXGIObject vtable slots
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);

        // IDXGIAdapter vtable slots
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

    static void Main()
    {
        Guid factoryGuid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
        if (CreateDXGIFactory(ref factoryGuid, out IntPtr factoryPtr) != 0) return;

        var factory = (IDXGIFactory)Marshal.GetObjectForIUnknown(factoryPtr);
        IntPtr targetAdapter = IntPtr.Zero;
        uint index = 0;

        // The QueryInterface call will now succeed flawlessly
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

        // Keep-Alive System Loop
        while (true)
        {
            int hr = D3D11CreateDevice(targetAdapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out IntPtr devicePtr, out _, out IntPtr contextPtr);
            if (hr == 0)
            {
                Marshal.Release(devicePtr);
                Marshal.Release(contextPtr);
            }
            Thread.Sleep(5000);
        }
    }
}