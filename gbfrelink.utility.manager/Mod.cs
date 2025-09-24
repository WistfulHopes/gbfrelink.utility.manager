using FlatSharp;

using GBFRDataTools;
using GBFRDataTools.Entities;

using gbfrelink.utility.manager.Configuration;
using gbfrelink.utility.manager.Entities;
using gbfrelink.utility.manager.Interfaces;
using gbfrelink.utility.manager.Template;

using MessagePack;

using Reloaded.Hooks.Definitions;
using Reloaded.Memory.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using Reloaded.Universal.Redirector.Interfaces;

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace gbfrelink.utility.manager;

/// <summary>
/// Your mod logic goes here.
/// </summary>
public class Mod : ModBase, IExports // <= Do not Remove.
{
    /// <summary>
    /// Provides access to the mod loader API.
    /// </summary>
    private readonly IModLoader _modLoader;

    /// Provides access to the Reloaded.Hooks API.
    /// </summary>
    /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
    private readonly IReloadedHooks? _hooks;

    /// <summary>
    /// Provides access to the Reloaded logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Entry point into the mod, instance that created this class.
    /// </summary>
    private readonly IMod _owner;

    /// <summary>
    /// Provides access to this mod's configuration.
    /// </summary>
    private Config _configuration;

    /// <summary>
    /// The configuration of the currently executing mod.
    /// </summary>
    private readonly IModConfig _modConfig;

    public DataManager _dataManager;

    private readonly IRedirectorController _redirectorController;
    private static IStartupScanner? _startupScanner = null!;

    private IHook<QuerySizeDelegate> _queryFileSizeHook;
    private unsafe delegate uint QuerySizeDelegate(StringSpan* fileName);

    private Stopwatch _sw;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;
        _hooks = context.Hooks;

        _logger.WriteLine($"[{_modConfig.ModId}] Initializing...");

#if DEBUG
        Debugger.Launch();
#endif

        var redirectorControllerRef = _modLoader.GetController<IRedirectorController>();
        if (redirectorControllerRef == null || !redirectorControllerRef.TryGetTarget(out _redirectorController))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Failed to initialize. Unable to get redirector controller.", _logger.ColorRed);
            return;
        }

        var startupScannerController = _modLoader.GetController<IStartupScanner>();
        if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Failed to initialize. Unable to get IStartupScanner controller.", _logger.ColorRed);
            return;
        }

        HookFileSizeQuery();

        _dataManager = new DataManager(_modConfig, _modLoader, _logger, _redirectorController, _configuration);
        if (!_dataManager.Initialize())
            _logger.WriteLine($"[{_modConfig.ModId}] Failed to initialize. Mods will still be attempted to be loaded.", _logger.ColorRed);

        _modLoader.AddOrReplaceController<IDataManager>(_owner, _dataManager);

        _sw = Stopwatch.StartNew();

        _modLoader.ModLoading += ModLoading;
        _modLoader.OnModLoaderInitialized += AllModsLoaded;

        _logger.WriteLine($"[{_modConfig.ModId}] GBFR Mod loader initialized.", _logger.ColorGreen);
    }

    private unsafe void HookFileSizeQuery()
    {
        // So the redirector does not take into account QueryDirectory calls
        // Such a call is made when fetching the file size for data.i. A trivial call to std::filesystem::file_size (aka _std_fs_get_stats) is made.

        // If *our* index in a folder outside the game folder is updated and is smaller/larger than the original
        // We risk the game crashing because it fetches the file size through that call. The game allocates the buffer through the create file call,
        // but only reads as much as what std::filesystem::file_size/QueryDirectory returns.

        // It would be especially annoying when the index was larger and it struggled to find files in the index. Why?
        // Because part of the index buffer was zeroed. So binary search for a hash would eventually just search zeroes as it reached the bottom of the file.

        SigScan("55 41 57 41 56 41 54 56 57 53 48 81 EC ?? ?? ?? ?? 48 8D AC 24 ?? ?? ?? ?? 48 83 E4 ?? 48 89 E3 48 89 AB ?? ?? ?? ?? 48 C7 45 ?? ?? ?? ?? ?? C5 F8 57 C0",
            "FileRaw::QuerySize", address =>
        {
            _queryFileSizeHook = _hooks!.CreateHook<QuerySizeDelegate>(QueryFileSizeImpl, address).Activate();
            _logger.WriteLine($"[{_modConfig.ModId}] Successfully hooked FileRaw::QuerySize (0x{address:X8})", _logger.ColorGreen);
        });
    }

    struct StringSpan
    {
        public nint StringPtr;
        public ulong Size;
    }

    private unsafe uint QueryFileSizeImpl(StringSpan* fileNamePtr)
    {
        string fileName = Marshal.PtrToStringAnsi(fileNamePtr->StringPtr, (int)fileNamePtr->Size);
        if (fileName.EndsWith("data.i"))
        {
            string tempIndexFilePath = _dataManager.GetTempIndexFilePath();
            return (uint)new FileInfo(tempIndexFilePath).Length;
        }

        return _queryFileSizeHook.OriginalFunction(fileNamePtr);
    }

    private static void SigScan(string pattern, string name, Action<nint> action)
    {
        var baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;
        _startupScanner?.AddMainModuleScan(pattern, result =>
        {
            if (!result.Found)
            {
                return;
            }
            action(result.Offset + baseAddress);
        });
    }

    private void ModLoading(IModV1 mod, IModConfigV1 modConfig)
    {
        var modDir = Path.Combine(_modLoader.GetDirectoryForModId(modConfig.ModId), @"GBFR\data");
        if (!Directory.Exists(modDir))
            return;

        _dataManager.RegisterModFiles(modConfig.ModId, modDir);
    }

    private void AllModsLoaded()
    {
        if (_configuration.ShowModLoaderInfo)
        {
            var modDir = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), @"GBFR\data");
            foreach (var region in new string[] { "bp", "cs", "ct", "en", "es", "fr", "ge", "it", "jp", "ko" })
            {
                byte[] msgFile = _dataManager.GetModdedOrAchiveFile($"system/table/text/{region}/text_temp.msg");
                var textData = TextDataFile.Read(msgFile, true);
                var versionColumn = textData.Rows.FirstOrDefault(e => e.Id_hash == "TXT_TITLE_VERSION");
                versionColumn.Text += $"\n";
                versionColumn.Text += $"Reloaded-II {_modLoader.GetLoaderVersion()} by Sewer76\n";
                versionColumn.Text += $"Granblue Fantasy Relink - Mod Loader {_modConfig.ModVersion} by Nenkai\n";
                versionColumn.Text += $"Modding Website: nenkai.github.io/relink-modding/\n";
                versionColumn.Text += $"Support: ko-fi.com/nenkai\n";
                versionColumn.Text += $"Discord: discord.gg/gbfr / discord.gg/KRm6WtQkVR";

                byte[] msgBytes = textData.Write();

                string outputDir = Path.Combine(modDir, $"system/table/text/{region}/text_temp.msg");
                Directory.CreateDirectory(Path.GetDirectoryName(outputDir));
                File.WriteAllBytes(outputDir, msgBytes);
            }

            _dataManager.RegisterModFiles(_modConfig.ModId, modDir);
        }

        _dataManager.UpdateIndex();
        _logger.WriteLine($"[{_modConfig.ModId}] Index updated, all mods loaded. Time taken: {_sw.Elapsed}", _logger.ColorGreen);
    }


    public Type[] GetTypes() => [typeof(IDataManager)];

    #region Standard Overrides

    public override void ConfigurationUpdated(Config configuration)
    {
        // Apply settings from configuration.
        // ... your code here.
        _configuration = configuration;
        _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
    }


    #endregion

    #region For Exports, Serialization etc.

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod()
    {
    }
#pragma warning restore CS8618

    #endregion
}