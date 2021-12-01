using Newtonsoft.Json;

namespace OnCourse.ABConnect.Models;

public class Standard
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public Relationships? Relationships { get; set; }
    public StandardAttributes? Attributes { get; set; }
}

public class StandardAttributes
{
    [JsonProperty(PropertyName = "seq")]
    public int Sequence { get; set; }
    public string? Guid { get; set; }
    public StandardNumber? Number { get; set; }
    public string? Status { get; set; }
    public string? Label { get; set; }
    public int Level { get; set; }
    public StandardStatement? Statement { get; set; }
    [JsonProperty(PropertyName = "education_levels")]
    public StandardEducationLevels? EducationLevels { get; set; }
    public StandardDocument? Document { get; set; }
    public StandardSection? Section { get; set; }
}

public class StandardNumber
{
    public string? Raw { get; set; }
    public string? Enhanced { get; set; }
    [JsonProperty(PropertyName = "prefix_enhanced")]
    public string? PrefixEnhanced { get; set; }
}

public class StandardDocument
{
    [JsonProperty(PropertyName = "adopt_year")]
    public string? AdoptYear { get; set; }
    [JsonProperty(PropertyName = "revision_year")]
    public string? RevisionYear { get; set; }
    [JsonProperty(PropertyName = "implementation_year")]
    public string? ImplementationYear { get; set; }
    [JsonProperty(PropertyName = "assessment_year")]
    public string? AssessmentYear { get; set; }
    [JsonProperty(PropertyName = "obsolete_year")]
    public string? ObsoleteYear { get; set; }
    [JsonProperty(PropertyName = "source_url")]
    public string? SourceUrl { get; set; }

    [JsonProperty(PropertyName = "date_modified_utc")]
    public DateTime DateModifiedUtc { get; set; }
    [JsonProperty("descr")]
    public string? Description { get; set; }
    public string? Guid { get; set; }
    public StandardDocumentPublication? Publication { get; set; }
}

public class StandardDocumentPublication : Publication
{
    [JsonProperty("publication_type")]
    public string? PublicationType { get; set; }
    public List<Region>? Regions { get; set; }
    public List<Authority>? Authorities { get; set; }
}

public class StandardSection
{
    [JsonProperty("descr")]
    public string? Description { get; set; }
}

public class StandardStatement
{
    [JsonProperty("descr")]
    public string? Description { get; set; }
    [JsonProperty("combined_descr")]
    public string? CombinedDescription { get; set; }
}

public class StandardEducationLevels
{
    public List<StandardEducationLevelGrade>? Grades { get; set; }
}

public class StandardEducationLevelGrade
{
    [JsonProperty(PropertyName = "seq")]
    public int Sequence { get; set; }
    public string? Code { get; set; }
    public string? Guid { get; set; }
    [JsonProperty("descr")]
    public string? Description { get; set; }
}
