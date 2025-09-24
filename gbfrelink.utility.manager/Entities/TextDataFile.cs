using MessagePack;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace gbfrelink.utility.manager.Entities;

public class TextDataFile
{
    [JsonPropertyName("rows_")]
    public List<TextData> Rows { get; set; } = [];

    public static TextDataFile Read(byte[] data, bool isMessagePackFile = false)
    {
        string text;
        if (isMessagePackFile)
            text = MessagePackSerializer.ConvertToJson(data);
        else
            text = Encoding.UTF8.GetString(data);

        JsonDocument doc = JsonDocument.Parse(text);

        var file = new TextDataFile();

        foreach (var elem in doc.RootElement.GetProperty("rows_").EnumerateArray())
        {
            var column = elem.GetProperty("column_");
            TextData textData = column.Deserialize<TextData>(new JsonSerializerOptions()
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // To make sure we're not missing anything
                NumberHandling = JsonNumberHandling.AllowReadingFromString, // Because they're stored as strings sometimes
                WriteIndented = true,
            });
            file.Rows.Add(textData);
        }

        return file;
    }

    public byte[] Write()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WritePropertyName("rows_");
        writer.WriteStartArray();

        foreach (var row in Rows)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("column_");
            writer.WriteStartObject();

            writer.WriteString("id_hash_", row.Id_hash);
            writer.WriteString("subid_hash_", row.Subid_hash);
            writer.WriteString("text_", row.Text);

            writer.WriteEndObject(); // column_
            writer.WriteEndObject(); // row
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        string json = Encoding.UTF8.GetString(stream.ToArray());

        return MessagePackSerializer.ConvertFromJson(json);
    }
}

public class TextData
{
    [JsonPropertyName("text_")]
    public string Text { get; set; }

    [JsonPropertyName("subid_hash_")]
    public string Subid_hash { get; set; }

    [JsonPropertyName("id_hash_")]
    public string Id_hash { get; set; }
}
