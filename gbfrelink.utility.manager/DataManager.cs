using FlatSharp;

using GBFRDataTools.Entities;
using GBFRDataTools.Files.BinaryXML;

using gbfrelink.utility.manager.Configuration;
using gbfrelink.utility.manager.Interfaces;

using MessagePack;

using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using Reloaded.Universal.Redirector.Interfaces;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace gbfrelink.utility.manager;

public class DataManager : IDataManager
{
    private IModConfig _modConfig;
    private IModLoader _modLoader;
    private ILogger _logger;
    private IRedirectorController _redirectorController;
    private Config _configuration;

    private IndexFile _indexFile;
    private IndexFile _moddedIndex;
    private string _loaderDir;
    private string _dataPath;
    private string _tempPath;
    private string _gameDir;

    public ArchiveAccessor _archiveAccessor;
    private ModFileCacheRegistry _cacheRegistry;

    private bool _initialized;
    public bool Initialized => _initialized;

    public const string INDEX_ORIGINAL_CODENAME = "relink";
    public const string INDEX_MODDED_CODENAME = "relink-reloaded-ii-mod";

    public DataManager(IModConfig modConfig, IModLoader modLoader, ILogger logger, IRedirectorController redirectorController, Config configuration)
    {
        _modConfig = modConfig;
        _modLoader = modLoader;
        _logger = logger;
        _redirectorController = redirectorController;
        _configuration = configuration;

        _cacheRegistry = new ModFileCacheRegistry(_logger, _modConfig);
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
        _loaderDir = _modLoader.GetDirectoryForModId(_modConfig.ModId);

        if (!CheckGameDirectory())
            return false;

        string cacheFilePath = Path.Combine(_loaderDir, "cached_files.txt");
        if (File.Exists(cacheFilePath))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Reading cache registry found...");
            _cacheRegistry.ReadCache(cacheFilePath);
        }
        else
            _logger.WriteLine($"[{_modConfig.ModId}] No cache registry file found, one will be created..");

        _tempPath = Path.Combine(_loaderDir, "temp");
        Directory.CreateDirectory(_tempPath);

        _initialized = true;
        return true;
    }

    private record ModFile(string ModId, string SourcePath, string GamePath, string TargetPath);
    private List<ModFile> _modFiles = [];

    private Dictionary<string, ModFile> _filesToModOwner = [];

    /// <summary>
    /// Registers & updates the index with all the potential GBFR files for a mod.
    /// </summary>
    /// <param name="modId">Mod Id (used for logging).</param>
    /// <param name="folder">Folder containing modded files.</param>
    public void RegisterModFiles(string modId, string folder)
    {
        CheckInitialized();

        ArgumentException.ThrowIfNullOrWhiteSpace(modId, nameof(modId));
        ArgumentException.ThrowIfNullOrWhiteSpace(folder, nameof(folder));

        _logger.WriteLine($"[{_modConfig.ModId}] Registering files for {modId}...");

        int numFilesForMod = 0;
        var allFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
        foreach (string newPath in allFiles)
        {
            ModFile modFile = ProcessFile(newPath, Path.GetRelativePath(folder, newPath).Replace('\\', '/'), modId);
            if (modFile is not null)
            {
                _modFiles.Add(modFile);
                numFilesForMod++;
            }
        }

        foreach (ModFile modFile in _modFiles)
        {
            if (_filesToModOwner.TryGetValue(modFile.GamePath, out ModFile modIdOwner))
            {
                LogWarn($"Note: Potential conflict - {modId} edits '{modFile.GamePath}' which is already edited by {modIdOwner.ModId}");
                _filesToModOwner[modFile.GamePath] = modFile;
            }
            else
                _filesToModOwner.Add(modFile.GamePath, modFile);

            long fileSize = new FileInfo(modFile.TargetPath).Length;
            if (RegisterExternalFileToIndex(modFile.GamePath, (ulong)fileSize))
            {
                if (_configuration.VerboseLogging)
                    LogInfo($"- {modId}: Added {modFile.GamePath} as new external file");
            }
            else
            {
                if (_configuration.VerboseLogging)
                    LogInfo($"- {modId}: Updated {modFile.GamePath} external file");
            }
        }

        LogInfo("");
        LogInfo($"-> {numFilesForMod} files have been added or updated to the external file list ({modId}).");

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

        gamePath = gamePath.Replace('\\', '/');

        LogInfo($"-> Adding/Updating file '{gamePath}' ({fileData.Length} bytes)");

        string modId = "unspecified";

        string tempFile = GetTempFilePathForMod(gamePath, modId);
        File.WriteAllBytes(tempFile, fileData);

        RegisterExternalFileToIndex(gamePath, (ulong)fileData.Length);
        ProcessFile(tempFile, gamePath, modId);
    }


    /// <summary>
    /// Adds or updates a game file as an external file, using a specified local file.
    /// </summary>
    /// <param name="gamePath"></param>
    /// <param name="data"></param>
    public void AddOrUpdateExternalFile(string filePath, string gamePath)
    {
        CheckInitialized();

        gamePath = gamePath.Replace('\\', '/');

        LogInfo($"-> Adding/Updating file '{filePath}' from '{filePath}'");

        string modId = "unspecified";

        long fileSize = new FileInfo(filePath).Length;
        RegisterExternalFileToIndex(gamePath, (ulong)fileSize);
        ProcessFile(filePath, gamePath, modId);
    }

    /// <summary>
    /// Saves & overwrites the index file.
    /// </summary>
    public void UpdateIndex()
    {
        CheckInitialized();

        _logger.WriteLine($"[{_modConfig.ModId}] Serializing index.");

        byte[] outBuf = new byte[IndexFile.Serializer.GetMaxSize(_moddedIndex)];
        _moddedIndex.Codename = INDEX_MODDED_CODENAME; // Helps us keep of track whether this is an original index or not
        IndexFile.Serializer.Write(outBuf, _moddedIndex);

        string tempIndexPath = GetTempIndexFilePath();
        File.WriteAllBytes(tempIndexPath, outBuf);

        // Write cache
        _cacheRegistry.WriteCache(Path.Combine(_loaderDir, "cached_files.txt"));

        _redirectorController.AddRedirect(Path.Combine(_gameDir, "data.i"), tempIndexPath);
        _redirectorController.Enable();

        _redirectorController.Redirecting += Redirect;

        _logger.WriteLine($"[{_modConfig.ModId}] Done. Index redirected to {tempIndexPath}.");
    }

    public void Redirect(string oldPath, string newPath)
    {
        if (_configuration.PrintRedirectedFiles)
            LogInfo("Redirect: " + newPath);
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

        _archiveAccessor ??= new ArchiveAccessor(_gameDir, _indexFile);
        return _archiveAccessor.GetFileData(fileName);
    }

    public byte[] GetModdedOrAchiveFile(string fileName)
    {
        CheckInitialized();

        fileName = fileName.Replace('\\', '/').ToLower();

        if (_filesToModOwner.TryGetValue(fileName, out ModFile modFile))
        {
            byte[] fileData = File.ReadAllBytes(modFile.TargetPath);
            return fileData;
        }

        _archiveAccessor ??= new ArchiveAccessor(_gameDir, _indexFile);
        return _archiveAccessor.GetFileData(fileName);
    }

    public string GetDataPath()
    {
        CheckInitialized();

        return _dataPath;
    }

    #endregion

    public string GetTempIndexFilePath()
        => Path.Combine(_tempPath, "data.i");

    #region Private
    /// <summary>
    /// Creates the game's data/ directory if it's somehow missing.
    /// </summary>
    /// <returns></returns>
    private bool CheckGameDirectory()
    {
        if (!Directory.Exists(_dataPath))
        {
            LogError("ERROR: Game's 'data' folder is somehow missing. Verify game files integrity.");
            return false;
        }

        // Pre-1.3.0 loader which tampered with the game dir's index and didn't use the redirector. restore it if it still exists.
        string origDataIndexPath = Path.Combine(_gameDir, "orig_data.i");
        string indexFilePath = Path.Combine(_gameDir, "data.i");
        if (File.Exists(origDataIndexPath))
        {
            LogWarn($"Found old deprecated orig_data.i file. Replacing data.i with it.");
            File.Copy(origDataIndexPath, indexFilePath, overwrite: true);
            File.Delete(origDataIndexPath);
        }

        _indexFile = IndexFile.Serializer.Parse(File.ReadAllBytes(indexFilePath));
        _moddedIndex = IndexFile.Serializer.Parse(File.ReadAllBytes(indexFilePath));

        return true;
    }

    /// <summary>
    /// Process a file to game output.
    /// </summary>
    /// <param name="sourceFile"></param>
    /// <param name="output"></param>
    private ModFile ProcessFile(string sourceFile, string gamePath, string modId)
    {
        string ext = Path.GetExtension(sourceFile);

        ModFile modFile;
        switch (ext)
        {
            case ".minfo" when _configuration.AutoUpgradeMInfo:
                modFile = UpgradeMInfoIfNeeded(sourceFile, gamePath, modId);
                break;

            case ".json" when _configuration.AutoConvertJsonToMsg:
                modFile = ConvertToMsg(sourceFile, gamePath, modId);
                break;

            case ".xml" when _configuration.AutoConvertXmlToBxm:
                modFile = ConvertToBxm(sourceFile, gamePath, modId); // Weird '.bxm.xml' produced by nier_cli..
                break;

            case ".msg":
                if (_configuration.AutoConvertJsonToMsg && File.Exists(Path.ChangeExtension(sourceFile, ".json")))
                {
                    LogWarn($"'{sourceFile}' will be ignored - .json file exists & already processed");
                    AddRedirectPath(Path.Combine(_dataPath, gamePath), sourceFile);

                    return null!;
                }
                else
                {
                    modFile = new ModFile(modId, sourceFile, gamePath, sourceFile);
                    var cachedEntry = _cacheRegistry.AddEntry(modId, gamePath, File.GetLastWriteTime(sourceFile));
                    cachedEntry.IsUsed = true;
                }
                break;
            default:
                {
                    modFile = new ModFile(modId, sourceFile, gamePath, sourceFile);
                    var cachedEntry = _cacheRegistry.AddEntry(modId, gamePath, File.GetLastWriteTime(sourceFile));
                    cachedEntry.IsUsed = true;
                }

                break;
        }

        AddRedirectPath(Path.Combine(_dataPath, modFile.GamePath), modFile.TargetPath);
        return modFile;
    }

    /// <summary>
    /// Redirects a source path (original file) to use the target path (modded file) instead.
    /// </summary>
    /// <param name="sourcePath"></param>
    /// <param name="targetPath"></param>
    private void AddRedirectPath(string sourcePath, string targetPath)
    {
        // Normalization matters for redirector controller for some reason.
        sourcePath = sourcePath.Replace('/', '\\');
        targetPath = targetPath.Replace('/', '\\');

        _redirectorController.AddRedirect(sourcePath, targetPath);
    }

    private ModFile UpgradeMInfoIfNeeded(string sourceFile, string gamePath, string modId)
    {
        // GBFR v1.1.1 upgraded the minfo file - magic/build date changed, two new fields added.
        // Rendered models invisible due to magic check fail.
        // In order to avoid having ALL mod makers rebuild their mods, upgrade the magic as a post process operation

        try
        {
            var minfoBytes = File.ReadAllBytes(sourceFile);
            ModelInfo modelInfo = ModelInfo.Serializer.Parse(minfoBytes);

            if (modelInfo.Magic < 20240213)
            {
                modelInfo.Magic = 10000_01_01;

                int size = ModelInfo.Serializer.GetMaxSize(modelInfo);
                byte[] buf = new byte[size];
                ModelInfo.Serializer.Write(buf, modelInfo);
                string outputPath = GetTempFilePathForMod(gamePath, modId);
                File.WriteAllBytes(outputPath, buf);

                LogInfo($"-> .minfo file '{sourceFile}' magic has been updated to be compatible");

                var cachedEntry = _cacheRegistry.AddEntry(modId, gamePath, File.GetLastWriteTime(sourceFile));
                cachedEntry.IsUsed = true;

                return new ModFile(modId, sourceFile, gamePath, outputPath);
            }
        }
        catch (Exception e)
        {
            LogError($"Failed to process .minfo file, file will be copied instead - {e.Message}");
        }


        return new ModFile(modId, sourceFile, gamePath, sourceFile);
    }

    private string GetTempFilePathForMod(string gamePath, string modId)
    {
        string path = Path.Combine(_tempPath, modId, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        return path;
    }

    private ModFile ConvertToMsg(string sourceFile, string gamePath, string modId)
    {
        string msgGamePath = Path.ChangeExtension(gamePath, ".msg").Replace('\\', '/');

        try
        {
            string outputPath = GetTempFilePathForMod(msgGamePath, modId);

            var lastModified = File.GetLastWriteTime(sourceFile);
            if (File.Exists(outputPath) && _cacheRegistry.TryGetFileIfUpToDate(modId, gamePath, lastModified, out CachedModFileInfo cachedFileInfo))
            {
                cachedFileInfo.IsUsed = true;

                if (_configuration.VerboseLogging)
                    LogInfo($"-> {msgGamePath} ({modId}) - already up to date.");

                return new ModFile(modId, sourceFile, msgGamePath, outputPath);
            }

            LogInfo($"-> Converting .json '{sourceFile}' ({modId}) to MessagePack .msg..");

            var json = File.ReadAllText(sourceFile);
            File.WriteAllBytes(outputPath, MessagePackSerializer.ConvertFromJson(json));

            var cachedEntry = _cacheRegistry.AddEntry(modId, gamePath, lastModified);
            cachedEntry.IsUsed = true;
            return new ModFile(modId, sourceFile, msgGamePath, outputPath);
        }
        catch (Exception e)
        {
            LogError($"Failed to process .json file into MessagePack .msg: {e.Message} (can be ignored if this is not meant to be a MessagePack file)");

            var cachedEntry = _cacheRegistry.AddEntry(modId, gamePath, File.GetLastWriteTime(sourceFile));
            cachedEntry.IsUsed = true;
            return new ModFile(modId, sourceFile, msgGamePath, sourceFile);
        }
    }

    private ModFile ConvertToBxm(string sourceFile, string gamePath, string modId)
    {
        try
        {
            string bxmGamePath = Path.ChangeExtension(gamePath.Replace(".bxm.xml", ".xml"), ".bxm").Replace('\\', '/');
            string outputBxmPath = GetTempFilePathForMod(gamePath, modId);

            var lastModified = File.GetLastWriteTime(sourceFile);
            if (File.Exists(outputBxmPath) && _cacheRegistry.TryGetFileIfUpToDate(modId, gamePath, lastModified, out CachedModFileInfo cachedFileInfo))
            {
                cachedFileInfo.IsUsed = true;

                if (_configuration.VerboseLogging)
                    LogInfo($"-> {bxmGamePath} ({modId}) - already up to date.");

                return new ModFile(modId, sourceFile, bxmGamePath, outputBxmPath);
            }

            LogInfo($"-> Converting .xml '{sourceFile}' ({modId}) to Binary XML .bxm..");

            XmlDocument doc = new XmlDocument();
            doc.Load(sourceFile);

            using var stream = File.OpenWrite(outputBxmPath);
            XmlBin.Write(stream, doc);

            var cachedEntry = _cacheRegistry.AddEntry(modId, gamePath, lastModified);
            cachedEntry.IsUsed = true;
            return new ModFile(modId, sourceFile, bxmGamePath, outputBxmPath);
        }
        catch (Exception e)
        {
            LogError($"Failed to process .xml file into Binary XML .msg: {e.Message} (can be ignored if this is not meant to be a MessagePack file)");

            var cachedEntry = _cacheRegistry.AddEntry(modId, gamePath, File.GetLastWriteTime(sourceFile));
            cachedEntry.IsUsed = true;
            return new ModFile(modId, sourceFile, gamePath, sourceFile);
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

    private void CheckInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Data Manager is not initialized.");
    }

    private void LogInfo(string str)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] {str}");
    }

    private void LogError(string str)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] {str}", _logger.ColorRed);
    }

    private void LogSuccess(string str)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] {str}", _logger.ColorGreen);
    }

    private void LogWarn(string str)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] {str}", _logger.ColorYellow);
    }
    #endregion

}
