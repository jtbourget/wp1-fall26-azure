var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

// Liveness check for frontend app
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "wp1-frontend",
    timestamp = DateTime.UtcNow
}));

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
