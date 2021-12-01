using Newtonsoft.Json;

namespace OnCourse.ABConnect.Models;

public class Document
{
    [JsonProperty("adopt_year")]
    public int AdoptYear { get; set; }
    [JsonProperty("descr")]
    public string? Description { get; set; }
    public string? Guid { get; set; }
}
