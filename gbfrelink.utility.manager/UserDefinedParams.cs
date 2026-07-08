
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using gbfrelink.utility.manager.Interfaces;

namespace gbfrelink.utility.manager;

public class UserDefinedParams : IUserDefinedParams
{
    public static UserDefinedParams? Instance { get; private set; }
    private Version _ERMinimumVersion = new Version(2, 0, 0);

    public int Language { get; private set; }
    public Version ApplicationVersion { get; private set; }
    public Version DisplayVersion { get; private set; }
    public int NumGraniteTileSets { get; private set; }

    public bool IsEndlessRagnarok() => ApplicationVersion >= _ERMinimumVersion;

    public static void InitializeInstance()
    {
        Instance = LoadFromExecutableResource();
    }

    public static unsafe UserDefinedParams LoadFromExecutableResource()
    {
        var res = Native.FindResource(nint.Zero, 108, "USER_DEFINED_PARAMS");
        if (res == IntPtr.Zero)
            throw new FileNotFoundException("USER_DEFINED_PARAMS resource is missing in executable?");

        nint loadedRes = Native.LoadResource(nint.Zero, res);
        uint size = Native.SizeofResource(nint.Zero, res);
        nint dataPtr = Native.LockResource(loadedRes);

        if (size < 0x10)
            throw new InvalidDataException("USER_DEFINED_PARAMS size is lower than 0x10?");

        var userDefinedParams = new UserDefinedParams();

        Span<byte> bytes = new Span<byte>((void*)dataPtr, (int)size);
        userDefinedParams.Load(bytes);
        return userDefinedParams;
    }

    public void Load(Span<byte> bytes)
    {
        Language = BinaryPrimitives.ReadInt32LittleEndian(bytes[0x00..]);
        ApplicationVersion = new Version(bytes[0x06], bytes[0x05], bytes[0x04]);
        DisplayVersion = new Version(bytes[0x0A], bytes[0x09], bytes[0x08]);
        NumGraniteTileSets = BinaryPrimitives.ReadInt32LittleEndian(bytes[0x0C..]);
    }

    public GameVersion GetGameVersion()
    {
        if (ApplicationVersion < new Version(2, 0, 0))
            return GameVersion.Relink;
        else if (ApplicationVersion == new Version(2, 0, 0))
            return GameVersion.RelinkEndlessRagnarok_ClosedBeta;
        else if (ApplicationVersion == new Version(2, 0, 1))
        {
            if (Process.GetCurrentProcess().ProcessName.Contains("_demo"))
                return GameVersion.RelinkEndlessRagnarok_Demo;
            else
                return GameVersion.RelinkEndlessRagnarok_OpenBeta;
        }
        else
            return GameVersion.RelinkEndlessRagnarok;
    }
}
