using System.Runtime.InteropServices;
using System.Threading;

namespace HeadlessGpuKeeper;

static class Program
{
    const string MutexName = @"Local\HeadlessGPUKeeper";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    static int Main()
    {
        using (var mutex = new Mutex(true, MutexName, out bool createdNew))
        {
            if (!createdNew)
            {
                MessageBoxW(IntPtr.Zero, KeeperCore.DescribeState(), "HeadlessGPUKeeper", 0x40);
                return 1;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new GpuMonitorForm());
            GC.KeepAlive(mutex);
        }
        return 0;
    }
}
