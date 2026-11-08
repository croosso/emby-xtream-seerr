using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class DispatcharrVodMovieDetail
    {
        public int Id { get; set; }
        public string Uuid { get; set; }
    }

    public class DispatcharrVodProvider
    {
        public int Id { get; set; }

        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }

        public string Name { get; set; }

        /// <summary>The relation's provider data; only the provider's own name is read.</summary>
        [JsonPropertyName("custom_properties")]
        public DispatcharrVodProviderProperties CustomProperties { get; set; }

        /// <summary>
        /// The name this provider gives the movie, e.g. "EN - The Odyssey (TELESYNC) (2026)".
        /// Unlike the merged movie's name, it describes this version (ADR-C014). Null when absent.
        /// </summary>
        [JsonIgnore]
        public string ProviderName => CustomProperties?.BasicData?.Name;
    }

    public class DispatcharrVodProviderProperties
    {
        [JsonPropertyName("basic_data")]
        public DispatcharrVodProviderBasicData BasicData { get; set; }
    }

    public class DispatcharrVodProviderBasicData
    {
        [JsonPropertyName("name")]
        [JsonConverter(typeof(TolerantStringConverter))]
        public string Name { get; set; }
    }
}
