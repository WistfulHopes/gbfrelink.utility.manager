using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace gbfrelink.utility.manager;

public class Utils
{
    public static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLower();
    }

    public static ulong XXHash64Path(string str)
    {
        str = NormalizePath(str);

        byte[] hashBytes = XxHash64.Hash(Encoding.ASCII.GetBytes(str), 0);
        return BinaryPrimitives.ReadUInt64BigEndian(hashBytes);
    }
}
