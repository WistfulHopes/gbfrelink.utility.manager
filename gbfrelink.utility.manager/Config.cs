using System.ComponentModel;
using gbfrelink.utility.manager.Template.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace gbfrelink.utility.manager.Configuration;

public class Config : Configurable<Config>
{
    /*
        User Properties:
            - Please put all of your configurable properties here.

        By default, configuration saves as "Config.json" in mod user config folder.
        Need more config files/classes? See Configuration.cs

        Available Attributes:
        - Category
        - DisplayName
        - Description
        - DefaultValue

        // Technically Supported but not Useful
        - Browsable
        - Localizable

        The `DefaultValue` attribute is used as part of the `Reset` button in Reloaded-Launcher.
    */

    [Category("Output Options")]
    [DisplayName("Auto-Upgrade .minfo files")]
    [Description("Advanced users only. Whether to auto-upgrade .minfo files to remain compatible with the game.\n\n"
        +"This option should always be enabled.")]
    [DefaultValue(true)]
    public bool AutoUpgradeMInfo { get; set; } = true;

    [Category("Output Options")]
    [DisplayName("Auto-Convert .json to .msg")]
    [Description("Advanced users only. Whether to automatically convert any .json files to .msg (MessagePack) files.\n" +
        "Please only provide .json files if this option is enabled.\n\n" +
        "This option should always be enabled.")]
    [DefaultValue(true)]
    public bool AutoConvertJsonToMsg { get; set; } = true;

    [Category("Output Options")]
    [DisplayName("Auto-Convert .xml to .bxm")]
    [Description("Advanced users only. Whether to automatically convert any .xml files to .bxm (Binary XML) files.\n" +
    "Please only provide .xml files if this option is enabled.\n\n" +
    "This option should always be enabled.")]
    [DefaultValue(true)]
    public bool AutoConvertXmlToBxm { get; set; } = true;

    [Category("Console")]
    [DisplayName("Print Redirected Files")]
    [Description("Whether to print redirected files in the console.\n\n" +
"Useful for modders to see if modded files have been loaded.")]
    [DefaultValue(false)]
    public bool PrintRedirectedFiles { get; set; } = false;

    [Category("Console")]
    [DisplayName("Verbose Logging")]
    [Description("Enables verbose logging. If disabled, files that are being updated aswell as index operations will not be displayed.\n" +
        "May improve load time when loading mods with thousands of files.\n" +
        "You should leave this on unless you know what you're doing.")]
    [DefaultValue(true)]
    public bool VerboseLogging { get; set; } = true;

    [Category("Misc")]
    [DisplayName("Show Mod Loader Info on Title Screen")]
    [Description("Whether to display mod information on the title screen.")]
    [DefaultValue(true)]
    public bool ShowModLoaderInfo { get; set; } = true;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}