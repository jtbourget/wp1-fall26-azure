using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Api.Services;

/// <summary>
/// Service encapsulating Azure Blob Storage operations for file uploads and downloads.
/// Uses DefaultAzureCredential, automatically picking up the user-assigned runtime identity
/// configured via AZURE_CLIENT_ID in Azure Container Apps, or local developer credentials.
/// </summary>
public sealed class UploadStore
{
    private readonly BlobContainerClient? _container;
    private readonly string _containerName;
    private readonly string? _blobServiceUri;

    public UploadStore(IConfiguration configuration, ILogger<UploadStore> logger)
    {
        _blobServiceUri = configuration["BlobServiceUri"];
        _containerName = configuration["BlobContainer"] ?? "uploads";
        var runtimeClientId = configuration["AZURE_CLIENT_ID"];

        if (!string.IsNullOrWhiteSpace(_blobServiceUri))
        {
            try
            {
                var credentialOptions = new DefaultAzureCredentialOptions();
                if (!string.IsNullOrWhiteSpace(runtimeClientId))
                {
                    credentialOptions.ManagedIdentityClientId = runtimeClientId;
                }
                var credential = new DefaultAzureCredential(credentialOptions);
                var serviceClient = new BlobServiceClient(new Uri(_blobServiceUri), credential);
                _container = serviceClient.GetBlobContainerClient(_containerName);
                logger.LogInformation("UploadStore initialized with container {Container} at {Uri}", _containerName, _blobServiceUri);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize UploadStore client.");
                _container = null;
            }
        }
        else
        {
            logger.LogWarning("BlobServiceUri is not configured; UploadStore running in unconfigured state.");
        }
    }

    public bool IsConfigured => _container != null;
    public string ContainerName => _containerName;
    public string? BlobServiceUri => _blobServiceUri;

    public async Task<string> SaveAsync(Stream content, long length, string contentType = "application/octet-stream", CancellationToken ct = default)
    {
        if (_container == null)
            throw new InvalidOperationException("Blob storage container is not configured.");

        if (length < 1 || length > 5 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(length), "Upload limit is 5 MiB.");

        string key = Guid.NewGuid().ToString("N");
        await _container.GetBlobClient(key).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);

        return key;
    }

    public async Task DownloadAsync(string key, Stream destination, CancellationToken ct = default)
    {
        if (_container == null)
            throw new InvalidOperationException("Blob storage container is not configured.");

        await _container.GetBlobClient(key).DownloadToAsync(destination, ct);
    }
}
