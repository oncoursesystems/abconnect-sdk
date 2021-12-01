using Newtonsoft.Json;

namespace OnCourse.ABConnect.Models;

public class Event
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public EventRelationships? Relationships { get; set; }
    public EventAttributes? Attributes { get; set; }
}

public class EventAttributes
{
    [JsonProperty(PropertyName = "seq")]
    public int Sequence { get; set; }
    public string? Guid { get; set; }
    [JsonProperty(PropertyName = "document_guid")]
    public string? DocumentGuid { get; set; }
    public string? Target { get; set; }
    [JsonProperty(PropertyName = "change_type")]
    public string? ChangeType { get; set; }
    [JsonProperty(PropertyName = "date_utc")]
    public DateTime DateUtc { get; set; }
}

public class EventRelationships
{

}
