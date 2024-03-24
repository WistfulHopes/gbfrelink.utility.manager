using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

using MessagePack;
using FlatSharp;

using GBFRDataTools;
using GBFRDataTools.Entities;

using gbfrelink.utility.manager.Template;
using gbfrelink.utility.manager.Configuration;

namespace gbfrelink.utility.manager;

/// <summary>
/// Your mod logic goes here.
/// </summary>
public class Mod : ModBase // <= Do not Remove.
{
    /// <summary>
    /// Provides access to the mod loader API.
    /// </summary>
    private readonly IModLoader _modLoader;

    /// <summary>
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

    private IndexFile _index;
    private string _dataPath;

    public const string INDEX_ORIGINAL_CODENAME = "relink";
    public const string INDEX_MODDED_CODENAME = "relink-reloaded-ii-mod";

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        string appLocation = _modLoader.GetAppConfig().AppLocation;
        string gameDir = Path.GetDirectoryName(appLocation)!;

        // Create game's data/ directory if it doesn't exist already
        // Admittedly if it doesn't exist the person should have bigger concerns since bgm & movies are external files in the first place
        _dataPath = Path.Combine(gameDir, "data");

        if (!CreateGameDataDirectoryIfNeeded())
            return;

        if (!UpdateLocalDataIndex())
            return;

        if (!OverwriteGameDataIndexForCleanState())
            return;

        _modLoader.ModLoading += ModLoading;
        _modLoader.ModLoaded += ModLoaded;

        _logger.WriteLine("[GBFRelinkManager] GBFR Mod loader initialized.", _logger.ColorGreen);
    }

    /// <summary>
    /// Creates the game's data/ directory if it's somehow missing.
    /// </summary>
    /// <returns>Whether the task was successful and the directory exists.</returns>
    private bool CreateGameDataDirectoryIfNeeded()
    {
        if (!Directory.Exists(_dataPath))
        {
            try
            {
                Directory.CreateDirectory(_dataPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogError("ERROR: Game's data/ directory is missing and missing permissions to create it?");
                return false;
            }
            catch (Exception ex)
            {
                LogError($"ERROR: Unable to create game data/ directory - {ex.Message}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a copy of the game's data index for local store.
    /// </summary>
    /// <returns>Whether the task is successful and local data.i is original & updated.</returns>
    private bool UpdateLocalDataIndex()
    {
        string appLocation = _modLoader.GetAppConfig().AppLocation;
        string gameDir = Path.GetDirectoryName(appLocation)!;

        string modDir = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId));
        string gameDataIndexPath = Path.Combine(gameDir, "data.i");
        string modLoaderDataIndexPath = Path.Combine(modDir, "orig_data.i");

        if (!File.Exists(gameDataIndexPath))
        {
            LogError($"ERROR: Game's 'data.i' file does not exist. Ensure that this is not a mistake, or verify game files integrity.");
            return false;
        }

        byte[] gameIndexFile = File.ReadAllBytes(gameDataIndexPath);
        IndexFile gameIndex;
        try
        {
            gameIndex = IndexFile.Serializer.Parse(gameIndexFile);
        }
        catch (Exception ex)
        {
            LogError($"ERROR: Unable to parse '{gameDataIndexPath}'. Corrupted or not supported? - {ex.Message}");
            return false;
        }

        // Grab data.i from the game's folder. It'll be treated as the original we're storing locally
        if (!File.Exists(modLoaderDataIndexPath))
        {
            // Two heuristics to check if this is an original index file:
            // - 1. check codename. the mod loader will create an index with "relink-reloaded-ii-mod"
            // - 2. check table offset in flatbuffers file. flatsharp serializes the vtable slightly differently with a negative offset, enough to spot a difference
            //      this one is useful if the index has been tampered by GBFRDataTools from non-reloaded mods

            if (gameIndex.Codename != INDEX_ORIGINAL_CODENAME)
            {
                LogError("ERROR: Game's 'data.i' appears to already be modded even though Reloaded II's data file is missing.");
                LogError("Please verify game files integrity first.");
                return false;
            }
            else if (BinaryPrimitives.ReadInt32LittleEndian(gameIndexFile.AsSpan(4)) != 0)
            {
                LogError("ERROR: Game's 'data.i' appears to already have been tampered with from outside the mod loader");
                LogError("This can happen if you've installed mods that does not use the Reloaded II GBFR Mod Loader.");
                LogError("This is unsupported and Reloaded II mods should be used instead. Verify integrity before using the mod loader.");
                return false;
            }
        }
        else
        {
            if (gameIndex.Codename != INDEX_ORIGINAL_CODENAME || BinaryPrimitives.ReadInt32LittleEndian(gameIndexFile.AsSpan(4)) != 0)
                return true; // data.i is already modded. do not update local original copy.

            LogInfo("Game's data.i is original & different from local copy. Updating it.");
        }

        try
        {
            File.Copy(gameDataIndexPath, modLoaderDataIndexPath, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogError("ERROR: Could not copy game's data.i to mod loader's folder, missing permissions?");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"ERROR: Could not copy game's data.i to mod loader's folder - {ex.Message}");
            return false;
        }
        

        return true;
    }

    /// <summary>
    /// Overwrites the game's data index with the original one to start as a clean state without mods.
    /// </summary>
    /// <returns></returns>
    private bool OverwriteGameDataIndexForCleanState()
    {
        string appLocation = _modLoader.GetAppConfig().AppLocation;
        string gameDir = Path.GetDirectoryName(appLocation)!;

        string modDir = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId));
        string gameDataIndexPath = Path.Combine(gameDir, "data.i");
        string modLoaderDataIndexPath = Path.Combine(modDir, "orig_data.i");

        var origIndex = File.ReadAllBytes(modLoaderDataIndexPath);
        _index = IndexFile.Serializer.Parse(origIndex);

        // Ensure to start with a fresh base, Otherwise if all mods are unloaded, the modded data.i still stays
        File.WriteAllBytes(gameDataIndexPath, origIndex);

        // Also make a backup of the original data.i in the game's directory
        string gameDirOriginalIndexPath = Path.Combine(gameDir, "orig_data.i");
        try
        {
            LogInfo($"Copying original data.i to '{gameDirOriginalIndexPath}' for backup purposes");
            File.Copy(modLoaderDataIndexPath, gameDirOriginalIndexPath, overwrite: true);
        }
        catch (Exception e)
        {
            LogWarn($"WARN: Attempted to create an original data.i backup but copy to {gameDirOriginalIndexPath} failed - {e.Message}");
        }

        return true;
    }

    private void ModLoading(IModV1 mod, IModConfigV1 modConfig)
    {
        var folder = Path.Combine(_modLoader.GetDirectoryForModId(modConfig.ModId), @"GBFR\data");
        if (!Directory.Exists(folder))
            return;

        var allDirectories = Directory.GetDirectories(folder, "*", SearchOption.AllDirectories);
        foreach (string dir in allDirectories)
        {
            string dirToCreate = dir.Replace(folder, _dataPath);
            Directory.CreateDirectory(dirToCreate);
        }

        var allFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
        foreach (string newPath in allFiles)
        {
            ProcessFile(newPath, newPath.Replace(folder, _dataPath));
        }

        string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            string str = file[(folder.Length + 1)..].Replace('\\', '/').ToLower();

            byte[] hashBytes = XxHash64.Hash(Encoding.ASCII.GetBytes(str), 0);
            ulong hash = BinaryPrimitives.ReadUInt64BigEndian(hashBytes);

            long fileSize = new FileInfo(file).Length;
            if (AddOrUpdateExternalFile(hash, (ulong)fileSize))
                LogInfo($"- {modConfig.ModId}: Added {str} as new external file");
            else
                LogInfo($"- {modConfig.ModId}: Updated {str} external file");
            RemoveArchiveFile(hash);
        }

        LogInfo("");
        LogInfo($"-> {files.Length} files have been added or updated to the external file list.");
    }

    private void ProcessFile(string file, string output)
    {
        string ext = Path.GetExtension(file);
        switch (ext)
        {
            case ".minfo":
                UpgradeMInfoIfNeeded(file, output);
                break;

            case ".json":
                ConvertToMsg(file, output);
                break;

            default:
                File.Copy(file, output, overwrite: true);
                break;
        }
    }

    private void UpgradeMInfoIfNeeded(string file, string output)
    {
        // GBFR v1.1.1 upgraded the minfo file - magic/build date changed, two new fields added.
        // Rendered models invisible due to magic check fail.
        // In order to avoid having ALL mod makers rebuild their mods, upgrade the magic as a post process operation

        try
        {
            var minfoBytes = File.ReadAllBytes(file);
            ModelInfo modelInfo = ModelInfo.Serializer.Parse(minfoBytes);

            if (modelInfo.Magic < 20240213)
            {
                modelInfo.Magic = 10000_01_01;

                int size = ModelInfo.Serializer.GetMaxSize(modelInfo);
                byte[] buf = new byte[size];
                ModelInfo.Serializer.Write(buf, modelInfo);
                File.WriteAllBytes(output, buf);

                LogInfo($"-> .minfo file '{file}' magic has been updated to be compatible");
            }
            else
            {
                File.Copy(file, output, overwrite: true);
            }
        }
        catch (Exception e)
        {
            LogError($"Failed to process .minfo file, file will be copied instead - {e.Message}");
            File.Copy(file, output, overwrite: true);
        }
    }

    private void ConvertToMsg(string file, string output)
    {
        LogInfo($"-> Converting .json '{file}' to MessagePack .msg..");
        try
        {
            var json = File.ReadAllText(file);
            File.WriteAllBytes(Path.ChangeExtension(output, ".msg"), MessagePackSerializer.ConvertFromJson(json));
        }
        catch (Exception e)
        {
            LogError($"Failed to process .json file into MessagePack .msg, file will be copied instead - {e.Message}");
            File.Copy(file, output, overwrite: true);
        }
    }

    private bool AddOrUpdateExternalFile(ulong hash, ulong fileSize)
    {
        bool added = false;
        int idx = _index.ExternalFileHashes.BinarySearch(hash);
        if (idx < 0)
        {
            idx = _index.ExternalFileHashes.AddSorted(hash);
            added = true;

            _index.ExternalFileSizes.Insert(idx, fileSize);
        }
        else
        {
            _index.ExternalFileSizes[idx] = fileSize;
        }

        return added;
    }

    private void RemoveArchiveFile(ulong hash)
    {
        int idx = _index.ArchiveFileHashes.BinarySearch(hash);
        if (idx > -1)
        {
            _index.ArchiveFileHashes.RemoveAt(idx);
            _index.FileToChunkIndexers.RemoveAt(idx);
        }
    }

    private void ModLoaded(IModV1 arg1, IModConfigV1 arg2)
    {
        byte[] outBuf = new byte[IndexFile.Serializer.GetMaxSize(_index)];
        _index.Codename = INDEX_MODDED_CODENAME; // Helps us keep of track whether this is an original index or not

        IndexFile.Serializer.Write(outBuf, _index);

        string dataIndexPath = Path.Combine(Path.GetDirectoryName(_modLoader.GetAppConfig().AppLocation)!, "data.i");
        File.WriteAllBytes(dataIndexPath, outBuf);
    }


    private void LogInfo(string str)
    {
        _logger.WriteLine($"[GBFRelinkManager] {str}");
    }

    private void LogError(string str)
    {
        _logger.WriteLine($"[GBFRelinkManager] {str}", _logger.ColorRed);
    }

    private void LogSuccess(string str)
    {
        _logger.WriteLine($"[GBFRelinkManager] {str}", _logger.ColorGreen);
    }

    private void LogWarn(string str)
    {
        _logger.WriteLine($"[GBFRelinkManager] {str}", _logger.ColorYellow);
    }


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