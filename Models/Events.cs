namespace OnCourse.ABConnect.Models;

public class Events
{
    public Links? Links { get; set; } = new Links();
    public Meta? Meta { get; set; } = new Meta();
    public List<Event>? Data { get; set; } = new List<Event>();
    public Relationships? Relationships { get; set; } = new Relationships();
}
