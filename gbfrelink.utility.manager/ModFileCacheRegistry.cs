using Reloaded.Mod.Interfaces;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace gbfrelink.utility.manager;

public class ModFileCacheRegistry
{
    private Dictionary<string, Dictionary<string, CachedModFileInfo>> _modIdsToFiles = [];

    private ILogger _logger;
    private IModConfig _modConfig;

    public ModFileCacheRegistry(ILogger logger, IModConfig modConfig)
    {
        _logger = logger;
        _modConfig = modConfig;
    }

    public CachedModFileInfo AddEntry(string modId, string filePath, DateTime lastModified)
    {
        filePath = filePath.Replace('\\', '/');

        if (!_modIdsToFiles.TryGetValue(modId, out var files))
        {
            files = [];
            _modIdsToFiles[modId] = files;
        }

        var entry = new CachedModFileInfo
        {
            FilePath = filePath,
            LastModified = lastModified,
        };

        files[filePath] = entry;
        return entry;
    }

    public bool TryGetFileIfUpToDate(string modId, string gamePath, DateTime current, out CachedModFileInfo modFile)
    {
        modFile = null;

        current = new DateTime(current.Year, current.Month, current.Day, current.Hour, current.Minute, current.Second, current.Kind); // Remove milliseconds
        gamePath = gamePath.Replace('\\', '/'); // Normalize

        if (!_modIdsToFiles.TryGetValue(modId, out var files))
            return false;

        if (files.TryGetValue(gamePath, out modFile))
        {
            if (current == modFile.LastModified)
                return true;
        }

        return false;
    }

    public void ReadCache(string cacheFilePath)
    {
        int lineNumber = 1;
        using var reader = new StreamReader(cacheFilePath);

        string modId = string.Empty;
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                continue;

            string[] parts = line.Split('|');
            if (parts.Length < 1)
                continue;

            switch (parts[0])
            {
                case "mod_id":
                    if (parts.Length < 2)
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] Warning: cache file registry has malformed 'mod_id' - '{line}' at line {lineNumber}");
                        continue;
                    }

                    modId = parts[1];
                    break;

                case "file":
                    if (parts.Length < 3)
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] Warning: cache file registry has malformed 'file' - '{line}' at line {lineNumber}");
                        continue;
                    }

                    if (!DateTime.TryParseExact(parts[2], "dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out DateTime dateTime))
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] Warning: cache file registry has malformed 'file' - could not parse datetime for '{line}' at line {lineNumber}");
                        continue;
                    }

                    AddEntry(modId, parts[1].Replace('\\', '/'), dateTime);
                    break;
            }

            lineNumber++;
        }
    }

    public void WriteCache(string cacheFilePath)
    {
        using var sw = new StreamWriter(cacheFilePath);
        sw.WriteLine("// gbfrelink.utility.manager mod file cache");

        foreach (var modName in _modIdsToFiles)
        {
            if (modName.Value.Count == 0)
                continue;

            sw.WriteLine($"mod_id|{modName.Key}");

            foreach (var file in modName.Value)
            {
                if (file.Value.IsUsed)
                    sw.WriteLine($"file|{file.Value.FilePath}|{file.Value.LastModified.ToString("dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)}");
            }

            sw.WriteLine();
        }
    }
}

public class CachedModFileInfo
{
    public string FilePath { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsUsed { get; set; }
}
