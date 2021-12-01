namespace OnCourse.ABConnect;

public class ABConnectSettings
{
    public const string SectionName = "ABConnect";

    public string ApiUrl { get; set; } = "https://api.abconnect.certicaconnect.com/rest/v4.1/";
    public string? PartnerId { get; set; }
    public string? PartnerKey { get; set; }
}