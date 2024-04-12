namespace gbfrelink.utility.manager.Interfaces;

public interface IDataManager
{
    /// <summary>
    /// Returns whether the data manager is initialized.
    /// </summary>
    bool Initialized { get; }

    /// <summary>
    /// Registers & updates the index with all the potential GBFR files for a mod.
    /// </summary>
    /// <param name="modId">Mod Id (used for logging).</param>
    /// <param name="folder">Folder containing modded files.</param>
    void RegisterModFiles(string modId, string folder);

    /// <summary>
    /// Gets the directory where data files (data.i, etc) are contained.
    /// </summary>
    string GetDataPath();

    /// <summary>
    /// Serializes & saves the index file to the data directory. This should be called after adding external files.
    /// </summary>
    void UpdateIndex();

    /// <summary>
    /// Returns whether a file exists in the game data.
    /// </summary>
    /// <returns></returns>
    bool FileExists(string filePath, bool includeExternal = true, bool checkExternalFileExistsOnDisk = true);

    /// <summary>
    /// Gets a game archive file. This does not fetch from external files.
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    byte[] GetArchiveFile(string fileName);

    /// <summary>
    /// Adds or updates a game file as an external file, using the provided data.
    /// </summary>
    /// <param name="gamePath"></param>
    /// <param name="data"></param>
    void AddOrUpdateExternalFile(string gamePath, byte[] data);

    /// <summary>
    /// Adds or updates a game file as an external file, using a specified local file.
    /// </summary>
    /// <param name="gamePath">Game path.</param>
    /// <param name="filePath">Local file to use.</param>
    void AddOrUpdateExternalFile(string gamePath, string filePath);
}
