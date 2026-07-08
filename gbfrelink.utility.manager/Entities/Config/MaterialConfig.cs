using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace gbfrelink.utility.manager.Entities.Config;

public class MaterialConfig
{
    [JsonPropertyName("force_match_constant_buffer_file_list")]
    public HashSet<string> ForceMatchConstantBufferFileList { get; set; }
}
