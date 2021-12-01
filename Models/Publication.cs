using Newtonsoft.Json;

namespace OnCourse.ABConnect.Models;

public class Publication
{
    public string? Type { get; set; }
    public string? Code { get; set; }
    [JsonProperty("descr")]
    public string? Description { get; set; }
    public string? Guid { get; set; }
    [JsonProperty(PropertyName = "source_url")]
    public string? SourceUrl { get; set; }
}
