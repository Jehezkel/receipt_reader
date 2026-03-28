using System.Net.Http.Headers;
using ReceiptReader.Api.Configuration;
using ReceiptReader.Api.Repositories;
using ReceiptReader.Api.Services;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<OcrOptions>(builder.Configuration.GetSection(OcrOptions.SectionName));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));

builder.Services.AddControllers();

builder.Services.AddSingleton<IReceiptRepository, InMemoryReceiptRepository>();
builder.Services.AddSingleton<IStorageService, LocalFileStorageService>();
builder.Services.AddSingleton<IReceiptParser, HeuristicReceiptParser>();
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
builder.Services.AddHttpClient<IAiEnrichmentService, GeminiAiEnrichmentService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());
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
