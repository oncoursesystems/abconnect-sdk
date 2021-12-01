using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OnCourse.ABConnect.Models;

namespace OnCourse.ABConnect;

public interface IABConnectClient
{
    Task<Regions?> GetRegions(int offset = 0);
    Task<Publications?> GetPublications(string authorityId, int offset = 0);
    Task<Authorities?> GetAuthorities(int offset = 0);
    Task<Documents?> GetDocuments(string? publicationId = null, int offset = 0);
    Task<Events?> GetEvents(int lastSequence = 0, int offset = 0);
    Task<Standards?> GetStandardsByPublication(string publicationId, int offset = 0, int limit = 100);
    Task<Standards?> GetStandards(string documentId, int offset = 0, int limit = 100);
    int ParseOffset(string nextLink);
}

public class ABConnectClient : IABConnectClient
{
    private readonly ILogger<ABConnectClient> _logger;
    private readonly ABConnectSettings _settings;
    private readonly HttpClient _httpClient;

    public ABConnectClient(ILogger<ABConnectClient> logger, IOptions<ABConnectSettings> settings, HttpClient httpClient)
    {
        _logger = logger;
        _settings = settings.Value;

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(_settings.ApiUrl);
    }

    private string GetAuthentication()
    {
        // Seconds since epoch. Example is 24 hours.
        var expires = (long)Math.Floor(
            (DateTime.UtcNow.AddHours(24) - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds
        );

        var message = string.Format("{0}\n\nGET", expires);

        var keyBytes = Encoding.UTF8.GetBytes(_settings.PartnerKey ?? "");
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using (var hmac = new HMACSHA256(keyBytes))
        {
            var signature = Convert.ToBase64String(hmac.ComputeHash(messageBytes));
            return $"partner.id={_settings.PartnerId}&auth.signature={signature}&auth.expires={expires}";
        }
    }

    public async Task<Regions?> GetRegions(int offset = 0)
    {
        try
        {
            var url = $"standards?{GetAuthentication()}&offset={offset}&facet=document.publication.regions";
            var response = await _httpClient.GetStringAsync(url);
            return JsonConvert.DeserializeObject<Regions>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving regions");
        }

        return new Regions();
    }

    public async Task<Publications?> GetPublications(string authorityId, int offset = 0)
    {
        try
        {
            var url = $"standards?{GetAuthentication()}&offset={offset}&facet=document.publication&filter[standards]=(document.publication.authorities.guid eq '{authorityId}')";
            var response = await _httpClient.GetStringAsync(url);
            return JsonConvert.DeserializeObject<Publications>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving publications");
        }

        return new Publications();
    }

    public async Task<Authorities?> GetAuthorities(int offset = 0)
    {
        try
        {
            var url = $"standards?{GetAuthentication()}&offset={offset}&facet=document.publication.authorities";
            var response = await _httpClient.GetStringAsync(url);
            return JsonConvert.DeserializeObject<Authorities>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving authorities");
        }

        return new Authorities();
    }

    public async Task<Documents?> GetDocuments(string? publicationId = null, int offset = 0)
    {
        try
        {
            var url = $"standards?{GetAuthentication()}&offset={offset}&facet=document";

            if (!string.IsNullOrEmpty(publicationId))
            {
                url += $"&filter[standards]=(document.publication.guid eq '{publicationId}')";
            }

            var response = await _httpClient.GetStringAsync(url);
            return JsonConvert.DeserializeObject<Documents>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error retrieving documents for publication '{publicationId}'");
        }

        return new Documents();
    }

    public async Task<Events?> GetEvents(int lastSequence = 0, int offset = 0)
    {
        try
        {
            var url = $"events?{GetAuthentication()}&offset={offset}&filter[events]=(seq GT {lastSequence})&sort[events]=seq&fields[events]=seq,target,guid,document_guid,change_type,date_utc";
            var response = await _httpClient.GetStringAsync(url);
            return JsonConvert.DeserializeObject<Events>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error retrieving events from sequence {lastSequence}");
        }

        return new Events();
    }

    public async Task<Standards?> GetStandardsByPublication(string publicationId, int offset = 0, int limit = 100)
    {
        try
        {
            var url = $"standards?{GetAuthentication()}&limit={limit}&offset={offset}&fields[standards]=*&filter[standards]=(document.publication.id EQ '{publicationId}')&sort[standards]=level";
            var response = await _httpClient.GetStringAsync(url);
            return JsonConvert.DeserializeObject<Standards>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error retrieving standards for publication '{publicationId}'");
        }

        return new Standards();
    }

    public async Task<Standards?> GetStandards(string documentId, int offset = 0, int limit = 100)
    {
        try
        {
            var url = $"standards?{GetAuthentication()}&limit={limit}&offset={offset}&fields[standards]=*&filter[standards]=(document.id EQ '{documentId}')&sort[standards]=level";
            var response = await _httpClient.GetStringAsync(url);
            return JsonConvert.DeserializeObject<Standards>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error retrieving standards for document '{documentId}'");
        }

        return new Standards();
    }

    public int ParseOffset(string nextLink)
    {
        if (!string.IsNullOrEmpty(nextLink))
        {
            try
            {
                var uri = new Uri(nextLink);
                var query = QueryHelpers.ParseQuery(uri.Query);

                return Convert.ToInt32(query["offset"][0]);
            }
            catch
            {
                return -1;
            }
        }

        return -1;
    }

}
