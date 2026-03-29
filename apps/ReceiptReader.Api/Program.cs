using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using ReceiptReader.Api.Configuration;
using ReceiptReader.Api.Repositories;
using ReceiptReader.Api.Services;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<OcrOptions>(builder.Configuration.GetSection(OcrOptions.SectionName));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection(CorsOptions.SectionName));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddSingleton<IReceiptRepository, InMemoryReceiptRepository>();
builder.Services.AddSingleton<IStorageService, LocalFileStorageService>();
builder.Services.AddSingleton<IReceiptParser, HeuristicReceiptParser>();
builder.Services.AddSingleton<IReceiptConsistencyValidator, ReceiptConsistencyValidator>();
builder.Services.AddSingleton<IReceiptRepairService, DeterministicReceiptRepairService>();
builder.Services.AddSingleton<IReceiptProcessingQueue, ReceiptProcessingQueue>();
builder.Services.AddHostedService<ReceiptProcessingWorker>();
builder.Services.AddHttpClient<IOcrClient, OcrClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IConfiguration>()
        .GetSection(OcrOptions.SectionName)
        .Get<OcrOptions>() ?? new OcrOptions();
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient<IReceiptImagePreparationClient, ReceiptImagePreparationClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IConfiguration>()
        .GetSection(OcrOptions.SectionName)
        .Get<OcrOptions>() ?? new OcrOptions();
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient<IAiEnrichmentService, GeminiAiEnrichmentService>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IConfiguration>()
        .GetSection(GeminiOptions.SectionName)
        .Get<GeminiOptions>() ?? new GeminiOptions();
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

var configuredOrigins = (builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions())
    .AllowedOrigins
    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (configuredOrigins.Length > 0)
        {
            policy.WithOrigins(configuredOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader();
            return;
        }

        if (builder.Environment.IsDevelopment())
        {
            policy.WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

var storageOptions = app.Services.GetRequiredService<IConfiguration>()
    .GetSection(StorageOptions.SectionName)
    .Get<StorageOptions>() ?? new StorageOptions();
Directory.CreateDirectory(storageOptions.UploadRootPath);

app.UseCors();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storageOptions.UploadRootPath),
    RequestPath = "/uploads"
});

app.MapControllers();

app.Run();
