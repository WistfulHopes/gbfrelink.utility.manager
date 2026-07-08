using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace gbfrelink.utility.manager;

public class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint FindResource(IntPtr hModule, nint lpName, string lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint LoadResource(nint hModule, nint hResInfo);

    [DllImport("kernel32.dll")]
    public static extern nint LockResource(nint hResData);

    [DllImport("kernel32.dll")]
    public static extern uint SizeofResource(nint hModule, nint hResInfo);
}
