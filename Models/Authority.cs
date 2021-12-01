using Newtonsoft.Json;

namespace OnCourse.ABConnect.Models;

public class Authority
{
    public string? Acronym { get; set; }
    [JsonProperty("descr")]
    public string? Description { get; set; }
    public string? Guid { get; set; }
}
