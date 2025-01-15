﻿using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using System.Diagnostics;

using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

using MessagePack;
using FlatSharp;

using GBFRDataTools;
using GBFRDataTools.Entities;

using gbfrelink.utility.manager.Template;
using gbfrelink.utility.manager.Configuration;
using gbfrelink.utility.manager.Interfaces;
using Reloaded.Universal.Redirector.Interfaces;

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

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        _logger.WriteLine("[GBFRelinkManager] Initializing...");

#if DEBUG
        Debugger.Launch();
#endif

        var redirectorControllerRef = _modLoader.GetController<IRedirectorController>();
        if (redirectorControllerRef == null || !redirectorControllerRef.TryGetTarget(out _redirectorController))
        {
            _logger.WriteLine("[GBFRelinkManager] Failed to initialize. Unable to get redirector controller.", _logger.ColorRed);
            return;
        }


        _dataManager = new DataManager(_modConfig, _modLoader, _logger, _redirectorController, _configuration);
        if (!_dataManager.Initialize())
            _logger.WriteLine("[GBFRelinkManager] Failed to initialize. Mods will still be attempted to be loaded.", _logger.ColorRed);

        _modLoader.AddOrReplaceController<IDataManager>(_owner, _dataManager);

        _modLoader.ModLoading += ModLoading;
        _modLoader.OnModLoaderInitialized += AllModsLoaded;

        _logger.WriteLine("[GBFRelinkManager] GBFR Mod loader initialized.", _logger.ColorGreen);
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
        _dataManager.UpdateIndex();
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