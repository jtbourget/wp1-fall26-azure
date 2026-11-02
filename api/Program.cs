using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Configure structured JSON console logging for container stdout log aggregators
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = false;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions
    {
        Indented = true
    };
});

// Add services to the container
builder.Services.AddOpenApi();
builder.Services.AddSingleton<UploadStore>();

// CORS configuration to allow calls from frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Configure PostgreSQL data source
var pgHost = builder.Configuration["Database:Host"]
    ?? builder.Configuration["Database__Host"]
    ?? "localhost";
var pgPort = int.TryParse(builder.Configuration["Database:Port"] ?? builder.Configuration["Database__Port"], out var parsedPort) ? parsedPort : 5432;
var pgDb = builder.Configuration["Database:Name"]
    ?? builder.Configuration["Database__Name"]
    ?? "student_db";
var pgUser = builder.Configuration["Database:Username"]
    ?? builder.Configuration["Database__Username"]
    ?? "postgres";
var pgPass = builder.Configuration["Database:Password"]
    ?? builder.Configuration["Database__Password"]
    ?? "localdevpassword123";

var pgConnStr = builder.Configuration.GetConnectionString("PostgreSqlConnection")
    ?? builder.Configuration.GetConnectionString("DefaultPostgresConnection");

NpgsqlDataSource dataSource;
if (!string.IsNullOrWhiteSpace(pgConnStr))
{
    var dsBuilder = new NpgsqlDataSourceBuilder(pgConnStr);
    dataSource = dsBuilder.Build();
}
else
{
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = pgHost,
        Port = pgPort,
        Database = pgDb,
        Username = pgUser,
        Password = pgPass,
        SslMode = SslMode.Prefer,
        IncludeErrorDetail = true
    };
    var dsBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);

    // Support Azure Managed Identity if client id is provided and host is azure postgres
    if (!string.IsNullOrWhiteSpace(builder.Configuration["AZURE_CLIENT_ID"]) &&
        pgHost.Contains("postgres.database.azure.com"))
    {
        var credential = new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"]
        });
        dsBuilder.UsePeriodicPasswordProvider(async (settings, cancellationToken) =>
        {
            var token = await credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }),
                cancellationToken);
            return token.Token;
        }, TimeSpan.FromHours(4), TimeSpan.FromSeconds(10));
    }

    dataSource = dsBuilder.Build();
}
builder.Services.AddSingleton(dataSource);

var app = builder.Build();

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Ensure database schema exists on startup if database is reachable
try
{
    await using var startupCmd = dataSource.CreateCommand(@"
        CREATE TABLE IF NOT EXISTS service_records (
            id SERIAL PRIMARY KEY,
            service_name VARCHAR(100) NOT NULL,
            environment VARCHAR(50) NOT NULL,
            status VARCHAR(50) NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        CREATE INDEX IF NOT EXISTS idx_services_env ON service_records(environment);
    ");
    await startupCmd.ExecuteNonQueryAsync();
}
catch (Exception)
{
    // Startup schema creation is best-effort to allow endpoints to capture authentic runtime errors
}

// Liveness / readiness probe endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "wp1-api",
    timestamp = DateTime.UtcNow
}))
.WithName("HealthCheck");

// Configuration & service status endpoint
app.MapGet("/api/status", (IConfiguration config, UploadStore uploadStore) =>
{
    var hasSql = !string.IsNullOrWhiteSpace(config.GetConnectionString("DefaultConnection"));
    var hasBlob = uploadStore.IsConfigured;
    var hasIdentity = !string.IsNullOrWhiteSpace(config["AZURE_CLIENT_ID"]);

    return Results.Ok(new
    {
        service = "wp1-api",
        azureClientIdConfigured = hasIdentity,
        blobStorageConfigured = hasBlob,
        blobContainer = uploadStore.ContainerName,
        sqlDatabaseConfigured = hasSql,
        postgresHost = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host ?? "db",
        postgresDatabase = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Database ?? "student_db",
        environment = app.Environment.EnvironmentName,
        timestamp = DateTime.UtcNow
    });
})
.WithName("GetStatus");

// SQL database connectivity verification
app.MapGet("/api/sql-check", async (IConfiguration config, CancellationToken ct) =>
{
    var connStr = config.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connStr))
    {
        return Results.Ok(new
        {
            connected = false,
            message = "ConnectionStrings:DefaultConnection is not configured on this service."
        });
    }

    try
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION AS SqlVersion, DB_NAME() AS CurrentDb;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        string dbName = "";
        string version = "";
        if (await reader.ReadAsync(ct))
        {
            version = reader.GetString(0);
            dbName = reader.GetString(1);
        }

        return Results.Ok(new
        {
            connected = true,
            database = dbName,
            version = version.Split('\n').FirstOrDefault()?.Trim(),
            message = "Successfully connected to Azure SQL Database."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "SQL Connection Failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }
})
.WithName("CheckDatabase");

// POST /api/services - Create a new service record with structured telemetry
app.MapPost("/api/services", async (
    ServiceRecordDto dto,
    NpgsqlDataSource dataSource,
    ILogger<Program> logger) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var dbHost = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host ?? "db";

    logger.LogDebug("Renting connection from pool for host {DbHost}. Querying environment: {Environment}", dbHost, dto.Environment);

    try
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO service_records (service_name, environment, status, created_at) " +
            "VALUES ($1, $2, $3, NOW()) RETURNING id;");

        cmd.Parameters.AddWithValue(dto.ServiceName);
        cmd.Parameters.AddWithValue(dto.Environment);
        cmd.Parameters.AddWithValue(dto.Status);

        var newId = (int?)await cmd.ExecuteScalarAsync();
        sw.Stop();

        if (sw.ElapsedMilliseconds > 150)
        {
            logger.LogWarning("Slow query detected on {Endpoint}: took {ElapsedMs}ms", "/api/services", sw.ElapsedMilliseconds);
        }

        logger.LogInformation("Successfully created service record {ServiceName} with ID {ServiceId} in {ElapsedMs}ms",
            dto.ServiceName, newId, sw.ElapsedMilliseconds);

        return Results.Created($"/api/services/{newId}", new { id = newId, dto.ServiceName });
    }
    catch (NpgsqlException ex)
    {
        sw.Stop();
        logger.LogError(ex, "Database operation failed against {DbHost} for service {ServiceName}: {ErrorMessage}",
            dbHost, dto.ServiceName, ex.Message);

        return Results.Problem(
            title: "Database Service Unavailable",
            detail: "A persistence error occurred while processing the request.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("CreateServiceRecord");

// GET /api/services - List service records with optional environment filter
app.MapGet("/api/services", async (
    NpgsqlDataSource dataSource,
    ILogger<Program> logger,
    [FromQuery] string? environment) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var dbHost = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host ?? "db";

    logger.LogDebug("Renting connection from pool for host {DbHost}. Querying environment: {Environment}", dbHost, environment ?? "all");

    try
    {
        await using var cmd = dataSource.CreateCommand();
        if (!string.IsNullOrWhiteSpace(environment))
        {
            cmd.CommandText = "SELECT id, service_name, environment, status, created_at FROM service_records WHERE environment = $1 ORDER BY id;";
            cmd.Parameters.AddWithValue(environment);
        }
        else
        {
            cmd.CommandText = "SELECT id, service_name, environment, status, created_at FROM service_records ORDER BY id;";
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        var records = new List<object>();
        while (await reader.ReadAsync())
        {
            records.Add(new
            {
                id = reader.GetInt32(0),
                serviceName = reader.GetString(1),
                environment = reader.GetString(2),
                status = reader.GetString(3),
                createdAt = reader.GetDateTime(4)
            });
        }
        sw.Stop();

        if (sw.ElapsedMilliseconds > 150)
        {
            logger.LogWarning("Slow query detected on {Endpoint}: took {ElapsedMs}ms", "/api/services", sw.ElapsedMilliseconds);
        }

        logger.LogInformation("Successfully retrieved {Count} service records from {DbHost} in {ElapsedMs}ms",
            records.Count, dbHost, sw.ElapsedMilliseconds);

        return Results.Ok(records);
    }
    catch (NpgsqlException ex)
    {
        sw.Stop();
        logger.LogError(ex, "Database operation failed against {DbHost} for GET /api/services: {ErrorMessage}",
            dbHost, ex.Message);

        return Results.Problem(
            title: "Database Service Unavailable",
            detail: "A persistence error occurred while processing the request.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("ListServiceRecords");

// GET /api/services/{id:int} - Retrieve single service record by id
app.MapGet("/api/services/{id:int}", async (
    int id,
    NpgsqlDataSource dataSource,
    ILogger<Program> logger) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var dbHost = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host ?? "db";

    logger.LogDebug("Renting connection from pool for host {DbHost}", dbHost);

    try
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, service_name, environment, status, created_at FROM service_records WHERE id = $1;");
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var record = new
            {
                id = reader.GetInt32(0),
                serviceName = reader.GetString(1),
                environment = reader.GetString(2),
                status = reader.GetString(3),
                createdAt = reader.GetDateTime(4)
            };
            sw.Stop();
            logger.LogInformation("Successfully retrieved service record {ServiceId} from {DbHost} in {ElapsedMs}ms",
                id, dbHost, sw.ElapsedMilliseconds);
            return Results.Ok(record);
        }

        sw.Stop();
        return Results.NotFound(new { message = $"Service record with ID {id} not found." });
    }
    catch (NpgsqlException ex)
    {
        sw.Stop();
        logger.LogError(ex, "Database operation failed against {DbHost} for service ID {ServiceId}: {ErrorMessage}",
            dbHost, id, ex.Message);

        return Results.Problem(
            title: "Database Service Unavailable",
            detail: "A persistence error occurred while processing the request.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetServiceRecordById");

// File upload endpoint (Azure Blob Storage)
app.MapPost("/api/upload", async (HttpRequest request, UploadStore uploadStore, CancellationToken ct) =>
{
    if (!uploadStore.IsConfigured)
    {
        return Results.Problem(
            title: "Storage Unconfigured",
            detail: "BlobServiceUri is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    if (!request.HasFormContentType || request.Form.Files.Count == 0)
    {
        return Results.BadRequest(new { error = "Please provide a file using multipart form-data." });
    }

    var file = request.Form.Files[0];
    if (file.Length == 0)
    {
        return Results.BadRequest(new { error = "Cannot upload empty file." });
    }

    if (file.Length > 5 * 1024 * 1024)
    {
        return Results.BadRequest(new { error = "File exceeds maximum size limit of 5 MiB." });
    }

    try
    {
        await using var stream = file.OpenReadStream();
        var key = await uploadStore.SaveAsync(stream, file.Length, file.ContentType, ct);

        return Results.Created($"/api/download/{key}", new
        {
            key,
            fileName = file.FileName,
            length = file.Length,
            contentType = file.ContentType,
            uploadedUtc = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Upload Failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError
        );
    }
})
.DisableAntiforgery()
.WithName("UploadFile");

// File download endpoint (Azure Blob Storage)
app.MapGet("/api/download/{key}", async (string key, UploadStore uploadStore, CancellationToken ct) =>
{
    if (!uploadStore.IsConfigured)
    {
        return Results.Problem(
            title: "Storage Unconfigured",
            detail: "BlobServiceUri is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    var memoryStream = new MemoryStream();
    try
    {
        await uploadStore.DownloadAsync(key, memoryStream, ct);
        memoryStream.Position = 0;
        return Results.File(memoryStream, "application/octet-stream", fileDownloadName: key);
    }
    catch (Exception ex)
    {
        await memoryStream.DisposeAsync();
        return Results.NotFound(new { error = $"Blob key '{key}' not found or download failed.", detail = ex.Message });
    }
})
.WithName("DownloadFile");

app.Run();

// DTO for service records
public record ServiceRecordDto(string ServiceName, string Environment, string Status);
