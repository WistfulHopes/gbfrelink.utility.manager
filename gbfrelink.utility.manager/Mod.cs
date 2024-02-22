using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using FlatSharp;
using GBFRDataTools;
using GBFRDataTools.Entities;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using gbfrelink.utility.manager.Template;
using gbfrelink.utility.manager.Configuration;
using Reloaded.Mod.Interfaces.Internal;

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

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        // Copy original 
        string appLocation = _modLoader.GetAppConfig().AppLocation;
        string dir = Path.GetDirectoryName(appLocation)!;

        _dataPath = Path.Combine(dir, "data");
        if (!Directory.Exists(_dataPath)) Directory.CreateDirectory(_dataPath);

        if (!File.Exists(Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), "orig_data.i")))
            File.Copy(Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), "data.i"), Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), "orig_data.i"));
        
        var origIndex = File.ReadAllBytes(Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), "orig_data.i"));
        _index = IndexFile.Serializer.Parse(origIndex);

        // Ensure to start with a fresh base, Otherwise if all mods are unloaded, the modded data.i still stays
        File.WriteAllBytes(Path.Combine(dir, "data.i"), origIndex);

        _modLoader.ModLoading += ModLoading;
        _modLoader.ModLoaded += ModLoaded;
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
            File.Copy(newPath, newPath.Replace(folder, _dataPath), true);
        }

        string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            string str = file[(folder.Length + 1)..].Replace('\\', '/');

            byte[] hashBytes = XxHash64.Hash(Encoding.ASCII.GetBytes(str), 0);
            ulong hash = BinaryPrimitives.ReadUInt64BigEndian(hashBytes);

            long fileSize = new FileInfo(file).Length;
            if (AddOrUpdateExternalFile(hash, (ulong)fileSize))
                Console.WriteLine($"- Index: Added {str} as new external file");
            else
                Console.WriteLine($"- Index: Updated {str} external file");
            RemoveArchiveFile(hash);
        }

        string[] files2 = Directory.GetFiles(_dataPath, "*", SearchOption.AllDirectories);
        foreach (var file in files2)
        {
            if (file.Contains("sound")) continue;
            string str = file[(_dataPath.Length + 1)..].Replace('\\', '/');

            byte[] hashBytes = XxHash64.Hash(Encoding.ASCII.GetBytes(str), 0);
            ulong hash = BinaryPrimitives.ReadUInt64BigEndian(hashBytes);

            int idx = _index.ExternalFileHashes.BinarySearch(hash);
            if (idx < 0)
            {
                File.Delete(file);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"-> {files.Length} files have been added or updated to the external file list.");
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
            _index.ExternalFileHashes[idx] = fileSize;
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
        IndexFile.Serializer.Write(outBuf, _index);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(_modLoader.GetAppConfig().AppLocation)!, "data.i"), outBuf);
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