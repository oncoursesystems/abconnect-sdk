namespace OnCourse.ABConnect.Models;

public class Relationships
{
    public Relationship? Parent { get; set; }
    public RelationshipArray? Children { get; set; }
}

public class Relationship
{
    public RelationshipData? Data { get; set; }
}

public class RelationshipArray
{
    public List<RelationshipData>? Data { get; set; }
}

public class RelationshipData
{
    public string? Id { get; set; }
    public string? Type { get; set; }
}
