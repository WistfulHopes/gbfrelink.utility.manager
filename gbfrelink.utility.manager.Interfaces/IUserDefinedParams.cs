using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace gbfrelink.utility.manager.Interfaces;

public interface IUserDefinedParams
{
    int Language { get; }
    Version ApplicationVersion { get; }
    Version DisplayVersion { get; }
    int NumGraniteTileSets { get; }

    /// <summary>
    /// Whether we are running Endless Ragnarok (including Beta/Demo).
    /// </summary>
    /// <returns></returns>
    bool IsEndlessRagnarok();

    GameVersion GetGameVersion();
}

public enum GameVersion
{
    Relink,
    RelinkEndlessRagnarok_ClosedBeta,
    RelinkEndlessRagnarok_OpenBeta,
    RelinkEndlessRagnarok_Demo,
    RelinkEndlessRagnarok,
}
