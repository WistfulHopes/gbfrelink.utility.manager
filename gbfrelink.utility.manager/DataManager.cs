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
using gbfrelink.utility.manager.Configuration;

using FlatSharp;

namespace gbfrelink.utility.manager;

public class DataManager : IDataManager
{
    private IModConfig _modConfig;
    private IModLoader _modLoader;
    private ILogger _logger;
    private Config _configuration;

    private string _gameDir;
    private IndexFile _originalIndex;
    private IndexFile _moddedIndex;
    private string _dataPath;

    public ArchiveAccessor _archiveAccessor;

    private bool _initialized;
    public bool Initialized => _initialized;

    public const string INDEX_ORIGINAL_CODENAME = "relink";
    public const string INDEX_MODDED_CODENAME = "relink-reloaded-ii-mod";

    public DataManager(IModConfig modConfig, IModLoader modLoader, ILogger logger, Config configuration)
    {
        _modConfig = modConfig;
        _modLoader = modLoader;
        _logger = logger;
        _configuration = configuration;
    }

    #region Public API
    public bool Initialize()
    {
        if (_initialized)
            throw new InvalidOperationException("Data Manager is already initialized.");

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

        _initialized = true;
        return true;
    }

    private record ModFile(string SourcePath, string TargetPath);
    private List<ModFile> _modFiles = new List<ModFile>();

    /// <summary>
    /// Registers & updates the index with all the potential GBFR files for a mod.
    /// </summary>
    /// <param name="modId">Mod Id (used for logging).</param>
    /// <param name="folder">Folder containing modded files.</param>
    public void RegisterModFiles(string modId, string folder)
    {
        CheckInitialized();

        var allDirectories = Directory.GetDirectories(folder, "*", SearchOption.AllDirectories);
        foreach (string dir in allDirectories)
        {
            string dirToCreate = dir.Replace(folder, _dataPath);
            Directory.CreateDirectory(dirToCreate);
        }

        var allFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
        foreach (string newPath in allFiles)
        {
            ModFile modFile = CookFileToOutput(newPath, newPath.Replace(folder, _dataPath));
            if (modFile is not null)
                _modFiles.Add(modFile);
        }

        foreach (ModFile modFile in _modFiles)
        {
            string gamePath = modFile.SourcePath[(folder.Length + 1)..];

            long fileSize = new FileInfo(modFile.TargetPath).Length;
            if (RegisterExternalFileToIndex(gamePath, (ulong)fileSize))
                LogInfo($"- {modId}: Added {gamePath} as new external file");
            else
                LogInfo($"- {modId}: Updated {gamePath} external file");
        }

        LogInfo("");
        LogInfo($"-> {_modFiles.Count} files have been added or updated to the external file list ({modId}).");

        _modFiles.Clear();
    }

    /// <summary>
    /// Adds or updates a game file as an external file, using the provided data.
    /// </summary>
    /// <param name="gamePath"></param>
    /// <param name="data"></param>
    public void AddOrUpdateExternalFile(string gamePath, byte[] fileData)
    {
        CheckInitialized();

        LogInfo($"-> Adding/Updating file '{gamePath}' ({fileData.Length} bytes)");

        string tempFile = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), @"temp", gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile));
        File.WriteAllBytes(tempFile, fileData);

        string outputPath = Path.Combine(_dataPath, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        RegisterExternalFileToIndex(gamePath, (ulong)fileData.Length);
        CookFileToOutput(tempFile, Path.Combine(_dataPath, gamePath));

        File.Delete(tempFile);
    }


    /// <summary>
    /// Adds or updates a game file as an external file, using a specified local file.
    /// </summary>
    /// <param name="gamePath"></param>
    /// <param name="data"></param>
    public void AddOrUpdateExternalFile(string filePath, string gamePath)
    {
        CheckInitialized();

        LogInfo($"-> Adding/Updating file '{filePath}' from '{filePath}'");

        long fileSize = new FileInfo(filePath).Length;
        RegisterExternalFileToIndex(gamePath, (ulong)fileSize);
        CookFileToOutput(filePath, Path.Combine(_dataPath, gamePath));
    }

    /// <summary>
    /// Saves & overwrites the index file.
    /// </summary>
    public void UpdateIndex()
    {
        CheckInitialized();

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
        CheckInitialized();

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
        CheckInitialized();

        _archiveAccessor ??= new ArchiveAccessor(_gameDir, _originalIndex);
        return _archiveAccessor.GetFileData(fileName);
    }

    public string GetDataPath()
    {
        CheckInitialized();

        return _dataPath;
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
        bool copyGameDataIndex = true;
        if (!File.Exists(modLoaderDataIndexPath))
        {
            // We could not find data.i in manager folder (first time run, or can happen if the user deleted the manager folder),
            // therefore check the game directory's and whether it's original

            // Two heuristics to check if this is an original index file:
            // - 1. check codename. the mod manager will create an index with "relink-reloaded-ii-mod"
            // - 2. check table offset in flatbuffers file. flatsharp serializes the vtable slightly differently with a negative offset, enough to spot a difference
            //      this one is useful if the index has been tampered by GBFRDataTools from non-reloaded mods

            bool checkOrig = false;
            if (gameIndex.Codename != INDEX_ORIGINAL_CODENAME)
            {
                LogError("ERROR: Game's 'data.i' appears to already be modded even though GBFR Mod Manager's local original data file is missing.");
                checkOrig = true;
            }
            else if (BinaryPrimitives.ReadInt32LittleEndian(gameIndexFile.AsSpan(4)) != 0)
            {
                LogError("ERROR: Game's 'data.i' appears to already have been tampered with from outside the mod manager");
                LogError("This can happen if you've installed mods that does not use the GBFR Mod Manager.");
                LogError("This is unsupported and GBFR Mod Manager mods should be used instead.");
                checkOrig = true;
            }

            if (checkOrig)
            {
                // If we're here, the game has a data.i but it's already been modded.
                // Check if orig_data.i was created previously by the manager, and use it instead

                string gameOrigDataIndexPath = Path.Combine(_gameDir, "orig_data.i");
                if (File.Exists(gameOrigDataIndexPath))
                {
                    LogInfo("Found orig_data.i in game folder, using it instead");
                    SafeCopyFile(gameOrigDataIndexPath, modLoaderDataIndexPath, overwrite: true);

                    copyGameDataIndex = false;
                }
                else
                {
                    LogError("Could not find an original data.i to use. Verify integrity before using the mod manager.");
                    return false;
                }
            }
        }
        else
        {
            if (gameIndex.Codename != INDEX_ORIGINAL_CODENAME || BinaryPrimitives.ReadInt32LittleEndian(gameIndexFile.AsSpan(4)) != 0)
                return true; // data.i is already modded. do not update local original copy.

            LogInfo("Game's data.i is original & different from local copy. Updating it.");
        }

        if (copyGameDataIndex)
        {
            try
            {
                File.Copy(gameDataIndexPath, modLoaderDataIndexPath, overwrite: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogError($"ERROR: Could not copy game's data.i to mod manager's folder ({_modConfig.ModId}), missing permissions?");
                return false;
            }
            catch (Exception ex)
            {
                LogError($"ERROR: Could not copy game's data.i to mod manager's folder ({_modConfig.ModId}) - {ex.Message}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Overwrites the game's data index with the original one to start as a clean state without mods.
    /// </summary>
    /// <returns></returns>
    private bool OverwriteGameDataIndexForCleanState()
    {
        string modDir = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId));
        string gameDataIndexPath = Path.Combine(_gameDir, "data.i");
        string modLoaderDataIndexPath = Path.Combine(modDir, "orig_data.i");

        var origIndex = File.ReadAllBytes(modLoaderDataIndexPath);
        _originalIndex = IndexFile.Serializer.Parse(origIndex);
        _moddedIndex = IndexFile.Serializer.Parse(origIndex);

        // Ensure to start with a fresh base, Otherwise if all mods are unloaded, the modded data.i still stays
        File.WriteAllBytes(gameDataIndexPath, origIndex);

        // Also make a backup of the original data.i in the game's directory
        string gameDirOriginalIndexPath = Path.Combine(_gameDir, "orig_data.i");
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

    /// <summary>
    /// Process a file to game output.
    /// </summary>
    /// <param name="file"></param>
    /// <param name="output"></param>
    private ModFile CookFileToOutput(string file, string output)
    {
        string ext = Path.GetExtension(file);
        switch (ext)
        {
            case ".minfo" when _configuration.AutoUpgradeMInfo:
                return UpgradeMInfoIfNeeded(file, output);

            case ".json" when _configuration.AutoConvertJsonToMsg:
                return ConvertToMsg(file, output);

            case ".msg":
                if (_configuration.AutoConvertJsonToMsg && File.Exists(Path.ChangeExtension(file, ".json")))
                {
                    LogWarn($"'{file}' will be ignored - .json file exists & already processed");
                    return null!;
                }
                else
                {
                    SafeCopyFile(file, output, overwrite: true);
                    return new ModFile(file, output);
                }

            default:
                SafeCopyFile(file, output, overwrite: true);
                return new ModFile(file, output);
        }
    }

    private ModFile UpgradeMInfoIfNeeded(string file, string output)
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
                SafeCopyFile(file, output, overwrite: true);
            }
        }
        catch (Exception e)
        {
            LogError($"Failed to process .minfo file, file will be copied instead - {e.Message}");
            SafeCopyFile(file, output, overwrite: true);
        }

        return new ModFile(file, output);
    }

    private ModFile ConvertToMsg(string file, string output)
    {
        LogInfo($"-> Converting .json '{file}' to MessagePack .msg..");
        try
        {
            string outputMsgPath = Path.ChangeExtension(output, ".msg");
            var json = File.ReadAllText(file);
            File.WriteAllBytes(outputMsgPath, MessagePackSerializer.ConvertFromJson(json));
            return new ModFile(Path.ChangeExtension(file, ".msg"), outputMsgPath);
        }
        catch (Exception e)
        {
            LogError($"Failed to process .json file into MessagePack .msg, file will be copied instead - {e.Message} (can be ignored if this is not meant to be a MessagePack file)");
            SafeCopyFile(file, output, overwrite: true);
            return new ModFile(file, output);
        }
    }

    /// <summary>
    /// Adds a game file to the external files of the index, and removes it from the archive list if present.
    /// </summary>
    /// <param name="gamePath"></param>
    /// <param name="fileSize"></param>
    /// <returns></returns>
    private bool RegisterExternalFileToIndex(string gamePath, ulong fileSize)
    {
        ulong hash = Utils.XXHash64Path(gamePath);

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

        RemoveArchiveFileFromIndex(hash);
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

    /// <summary>
    /// Safely copies a file.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    /// <param name="overwrite"></param>
    private void SafeCopyFile(string source, string destination, bool overwrite)
    {
        try
        {
            File.Copy(source, destination, overwrite);
        }
        catch (Exception ex)
        {
            LogError($"Failed to copy file '{source}' to '{destination}' - {ex.Message}");
        }
    }

    private void CheckInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Data Manager is not initialized.");
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
