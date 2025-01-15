using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

using Reloaded.Mod.Interfaces.Internal;
using Reloaded.Mod.Interfaces;

using gbfrelink.utility.manager.Interfaces;
using gbfrelink.utility.manager.Configuration;

using Reloaded.Universal.Redirector.Interfaces;

using GBFRDataTools.Entities;
using GBFRDataTools.Files.BinaryXML;

using MessagePack;

using FlatSharp;

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
    private string _dataPath;
    private string _tempPath;
    private string _gameDir;

    public ArchiveAccessor _archiveAccessor;

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

        if (!CheckGameDirectory())
            return false;

        _tempPath = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), "temp");
        Directory.CreateDirectory(_tempPath);

        _initialized = true;
        return true;
    }

    private record ModFile(string SourcePath, string GamePath, string TargetPath);
    private List<ModFile> _modFiles = [];

    private Dictionary<string, string> _filesToModOwner = [];

    /// <summary>
    /// Registers & updates the index with all the potential GBFR files for a mod.
    /// </summary>
    /// <param name="modId">Mod Id (used for logging).</param>
    /// <param name="folder">Folder containing modded files.</param>
    public void RegisterModFiles(string modId, string folder)
    {
        CheckInitialized();

        var allFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
        foreach (string newPath in allFiles)
        {
            ModFile modFile = ProcessFile(newPath, Path.GetRelativePath(folder, newPath), modId);
            if (modFile is not null)
                _modFiles.Add(modFile);
        }

        foreach (ModFile modFile in _modFiles)
        {
            string gamePath = modFile.SourcePath[(folder.Length + 1)..];
            if (_filesToModOwner.TryGetValue(gamePath, out string modIdOwner))
                LogWarn($"Mod Conflict: {modId} edits '{gamePath}' which is already edited by {modIdOwner}");
            else
                _filesToModOwner.Add(gamePath, modId);

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

        string modId = "unspecified";

        string tempFile = Path.Combine(_tempPath, modId, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile));
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

        byte[] outBuf = new byte[IndexFile.Serializer.GetMaxSize(_moddedIndex)];
        _moddedIndex.Codename = INDEX_MODDED_CODENAME; // Helps us keep of track whether this is an original index or not
        IndexFile.Serializer.Write(outBuf, _moddedIndex);

        string tempIndexPath = Path.Combine(_tempPath, "data.i");
        File.WriteAllBytes(tempIndexPath, outBuf);

        _redirectorController.AddRedirect(Path.Combine(_gameDir, "data.i"), tempIndexPath);
        _redirectorController.Enable();

        _redirectorController.Redirecting += Redirect;
    }

    public void Redirect(string oldPath, string newPath)
    {
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
    /// <returns></returns>
    private bool CheckGameDirectory()
    {
        if (!Directory.Exists(_dataPath))
        {
            LogError("ERROR: Game's 'data' folder is somehow missing.  Verify game files integrity.");
            return false;
        }

        string indexFilePath = Path.Combine(_gameDir, "data.i");
        if (!File.Exists(Path.Combine(_gameDir, "data.i")))
        {
            LogError("ERROR: Game's 'data.i' file is somehow missing.  Verify game files integrity.");
            return false;
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
                    return null!;
                }
                else
                {
                    modFile = new ModFile(sourceFile, gamePath, sourceFile);
                }
                break;
            default:
                modFile = new ModFile(sourceFile, gamePath, sourceFile);
                break;
        }

        _redirectorController.AddRedirect(Path.Combine(_dataPath, modFile.GamePath), modFile.TargetPath);
        return modFile;
    }

    private ModFile UpgradeMInfoIfNeeded(string file, string gamePath, string modId)
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
                string outputPath = GetTempFilePathForMod(gamePath, modId);
                File.WriteAllBytes(outputPath, buf);

                LogInfo($"-> .minfo file '{file}' magic has been updated to be compatible");
            }
        }
        catch (Exception e)
        {
            LogError($"Failed to process .minfo file, file will be copied instead - {e.Message}");
        }

        return new ModFile(file, gamePath, file);
    }

    private string GetTempFilePathForMod(string gamePath, string modId)
    {
        string path = Path.Combine(_tempPath, modId, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        return path;
    }

    private ModFile ConvertToMsg(string sourceFile, string gamePath, string modId)
    {
        LogInfo($"-> Converting .json '{sourceFile}' to MessagePack .msg..");
        try
        {
            string msgGamePath = Path.ChangeExtension(gamePath, ".msg");

            var json = File.ReadAllText(sourceFile);
            string outputPath = GetTempFilePathForMod(msgGamePath, modId);
            File.WriteAllBytes(outputPath, MessagePackSerializer.ConvertFromJson(json));
            return new ModFile(sourceFile, msgGamePath, outputPath);
        }
        catch (Exception e)
        {
            LogError($"Failed to process .json file into MessagePack .msg: {e.Message} (can be ignored if this is not meant to be a MessagePack file)");
            return new ModFile(sourceFile, gamePath, sourceFile);
        }
    }

    private ModFile ConvertToBxm(string file, string gamePath, string modId)
    {
        LogInfo($"-> Converting .xml '{file}' to Binary XML .bxm..");
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(file);

            string bxmGamePath = Path.ChangeExtension(gamePath.Replace(".bxm.xml", ".xml"), ".bxm");
            string outputBxmPath = GetTempFilePathForMod(gamePath, modId);
            using var stream = File.OpenWrite(outputBxmPath);
            XmlBin.Write(stream, doc);

            return new ModFile(file, bxmGamePath, outputBxmPath);
        }
        catch (Exception e)
        {
            LogError($"Failed to process .xml file into Binary XML .msg: {e.Message} (can be ignored if this is not meant to be a MessagePack file)");
            return new ModFile(file, gamePath, file);
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
