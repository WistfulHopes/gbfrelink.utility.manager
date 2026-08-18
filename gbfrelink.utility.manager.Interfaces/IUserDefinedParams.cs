using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace gbfrelink.utility.manager.Interfaces;

/// <summary>
/// Basic user defined params and application information.
/// </summary>
public interface IUserDefinedParams
{
    /// <summary>
    /// Game language.
    /// </summary>
    int Language { get; }

    /// <summary>
    /// Application version. May control enabled features.
    /// </summary>
    Version ApplicationVersion { get; }

    /// <summary>
    /// Display version.
    /// </summary>
    Version DisplayVersion { get; }

    /// <summary>
    /// Number of granite tile sets (.gts) that the game will load.
    /// </summary>
    int NumGraniteTileSets { get; }

    /// <summary>
    /// Whether we are running Endless Ragnarok (including Beta/Demo).
    /// </summary>
    /// <returns></returns>
    bool IsEndlessRagnarok();

    /// <summary>
    /// Returns the current running game version/type.
    /// </summary>
    /// <returns></returns>
    GameVersion GetGameVersion();
}

/// <summary>
/// Relink game version.
/// </summary>
public enum GameVersion
{
    /// <summary>
    /// Granblue Fantasy: Relink
    /// </summary>
    Relink,

    /// <summary>
    /// Granblue Fantasy: Relink - Endless Ragnarok Closed Beta Test
    /// </summary>
    RelinkEndlessRagnarok_ClosedBeta,

    /// <summary>
    /// Granblue Fantasy: Relink - Endless Ragnarok Open Beta Test
    /// </summary>
    RelinkEndlessRagnarok_OpenBeta,

    /// <summary>
    /// Granblue Fantasy: Relink - Endless Ragnarok Demo
    /// </summary>
    RelinkEndlessRagnarok_Demo,

    /// <summary>
    /// Granblue Fantasy: Relink - Endless Ragnarok
    /// </summary>
    RelinkEndlessRagnarok,
}
