using Newtonsoft.Json;

namespace OnCourse.ABConnect.Models;

public class Meta
{
    public int Limit { get; set; }
    public int Offset { get; set; }
    public int Count { get; set; }
    public int Took { get; set; }
}

public class MetaWithFacet<T> : Meta
{
    public List<Facet<T>>? Facets { get; set; }
}

public class Facet<T>
{
    [JsonProperty(PropertyName = "facet")]
    public string? FacetType { get; set; }
    public int Count { get; set; }
    public List<FacetDetails<T>>? Details { get; set; }
}

public class FacetDetails<T>
{
    public int Count { get; set; }
    public T? Data { get; set; }
}
