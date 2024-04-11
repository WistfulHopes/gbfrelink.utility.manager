using GBFRDataTools.Entities;
using MessagePack;
using Reloaded.Mod.Interfaces.Internal;
using Reloaded.Mod.Interfaces;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using gbfrelink.utility.manager.Interfaces;
using FlatSharp;

namespace gbfrelink.utility.manager;

public class DataManager : IDataManager
{
    private IModConfig _modConfig;
    private IModLoader _modLoader;
    private ILogger _logger;

    private string _gameDir;
    private IndexFile _originalIndex;
    private IndexFile _moddedIndex;
    private string _dataPath;

    public ArchiveAccessor _archiveAccessor;

    public const string INDEX_ORIGINAL_CODENAME = "relink";
    public const string INDEX_MODDED_CODENAME = "relink-reloaded-ii-mod";

    public DataManager(IModConfig modConfig, IModLoader modLoader, ILogger logger)
    {
        _modConfig = modConfig;
        _modLoader = modLoader;
        _logger = logger;
    }

    #region Public API
    public bool Initialize()
    {
        string appLocation = _modLoader.GetAppConfig().AppLocation;
        _gameDir = Path.GetDirectoryName(appLocation)!;

        // Create game's data/ directory if it doesn't exist already
        // Admittedly if it doesn't exist the person should have bigger concerns since bgm & movies are external files in the first place
        _dataPath = Path.Combine(_gameDir, "data");

        if (!CreateGameDataDirectoryIfNeeded())
            return false;

        if (!UpdateLocalDataIndex())
            return false;

        if (!OverwriteGameDataIndexForCleanState())
            return false;

        return true;
    }

    /// <summary>
    /// Registers & updates the index with all the potential GBFR files for a mod.
    /// </summary>
    /// <param name="modConfig"></param>
    public void RegisterModFiles(IModConfigV1 modConfig)
    {
        ArgumentNullException.ThrowIfNull(modConfig, nameof(modConfig));

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
            string str = file[(folder.Length + 1)..].Replace(".json", ".msg");
            ulong hash = Utils.XXHash64Path(str);

            string outputFile = file;
            if (Path.GetExtension(file) == ".json")
                outputFile = Path.Combine(_dataPath, str);

            long fileSize = new FileInfo(outputFile).Length;
            if (AddOrUpdateExternalFile(hash, (ulong)fileSize))
                LogInfo($"- {modConfig.ModId}: Added {str} as new external file");
            else
                LogInfo($"- {modConfig.ModId}: Updated {str} external file");
            RemoveArchiveFileFromIndex(hash);
        }

        LogInfo("");
        LogInfo($"-> {files.Length} files have been added or updated to the external file list.");
    }

    /// <summary>
    /// Adds or updates a game file as an external file, using the provided data.
    /// </summary>
    /// <param name="gamePath"></param>
    /// <param name="data"></param>
    public void AddOrUpdateExternalFile(string fileName, byte[] fileData)
    {
        LogInfo($"-> Adding/Updating file '{fileName}' ({fileData.Length} bytes)");

        string tempFile = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), @"temp", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile));
        File.WriteAllBytes(tempFile, fileData);

        ProcessFile(tempFile, Path.Combine(_dataPath, fileName));

        File.Delete(tempFile);
    }


    /// <summary>
    /// Adds or updates a game file as an external file, using a specified local file.
    /// </summary>
    /// <param name="gamePath"></param>
    /// <param name="data"></param>
    public void AddOrUpdateExternalFile(string filePath, string gamePath)
    {
        LogInfo($"-> Adding/Updating file '{filePath}' from '{filePath}'");
        ProcessFile(filePath, Path.Combine(_dataPath, gamePath));
    }

    /// <summary>
    /// Saves & overwrites the index file.
    /// </summary>
    public void SaveIndex()
    {
        byte[] outBuf = new byte[IndexFile.Serializer.GetMaxSize(_moddedIndex)];
        _moddedIndex.Codename = INDEX_MODDED_CODENAME; // Helps us keep of track whether this is an original index or not

        IndexFile.Serializer.Write(outBuf, _moddedIndex);

        string dataIndexPath = Path.Combine(_gameDir, "data.i");
        File.WriteAllBytes(dataIndexPath, outBuf);
    }

    /// <summary>
    /// Returns whether a game file exists.
    /// </summary>
    /// <param name="filePath">File.</param>
    /// <param name="includeExternal">Whether to check external files aswell.</param>
    /// <param name="checkExternalFileExistsOnDisk">Whether to check if the external files also physically exist on the disk</param>
    /// <returns>Whether the file exists in the game data.</returns>
    public bool FileExists(string filePath, bool includeExternal = true, bool checkExternalFileExistsOnDisk = true)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        ulong hash = Utils.XXHash64Path(filePath);
        if (includeExternal)
        {
            int idx = _moddedIndex.ExternalFileHashes.BinarySearch(hash);

            if (idx >= 0)
            {
                string file = Path.Combine(_dataPath, filePath);
                if (checkExternalFileExistsOnDisk)
                {
                    if (File.Exists(file))
                        return true;
                }
                else
                    return true;
            }
        }

        return _moddedIndex.ArchiveFileHashes.BinarySearch(hash) >= 0;
    }

    public byte[] GetArchiveFile(string fileName)
    {
        _archiveAccessor ??= new ArchiveAccessor(_gameDir, _originalIndex);
        return _archiveAccessor.GetFileData(fileName);
    }

    #endregion

    #region Private
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
        string modDir = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId));
        string gameDataIndexPath = Path.Combine(_gameDir, "data.i");
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
        _originalIndex = IndexFile.Serializer.Parse(origIndex);
        _moddedIndex = IndexFile.Serializer.Parse(origIndex);

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
        int idx = _moddedIndex.ExternalFileHashes.BinarySearch(hash);
        if (idx < 0)
        {
            idx = _moddedIndex.ExternalFileHashes.AddSorted(hash);
            added = true;

            _moddedIndex.ExternalFileSizes.Insert(idx, fileSize);
        }
        else
        {
            _moddedIndex.ExternalFileSizes[idx] = fileSize;
        }
        return added;
    }

    private void RemoveArchiveFileFromIndex(ulong hash)
    {
        int idx = _moddedIndex.ArchiveFileHashes.BinarySearch(hash);
        if (idx > -1)
        {
            _moddedIndex.ArchiveFileHashes.RemoveAt(idx);
            _moddedIndex.FileToChunkIndexers.RemoveAt(idx);
        }
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
    #endregion

}
