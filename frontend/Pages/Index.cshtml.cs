using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frontend.Pages;

public class IndexModel : PageModel
{
    private readonly IConfiguration _config;

    public IndexModel(IConfiguration config)
    {
        _config = config;
    }

    public string AppName => "wp1-s01-pilot";
    public string ApiBaseUrl { get; private set; } = "";
    public string EnvironmentName { get; private set; } = "Production";
    public bool HasBlobConfig { get; private set; }
    public string BlobContainer { get; private set; } = "";
    public bool HasIdentityConfig { get; private set; }

    public void OnGet()
    {
        ApiBaseUrl = _config["ApiBaseUrl"] ?? _config["API_URL"] ?? "https://wp1-s01-api.azurecontainerapps.io";
        EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        HasBlobConfig = !string.IsNullOrWhiteSpace(_config["BlobServiceUri"]);
        BlobContainer = _config["BlobContainer"] ?? "uploads";
        HasIdentityConfig = !string.IsNullOrWhiteSpace(_config["AZURE_CLIENT_ID"]);
    }
}
