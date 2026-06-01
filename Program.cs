using AudioToTranscript.Configuration;
using AudioToTranscript.Services;
using AudioToTranscript.Utils;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((ctx, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile("Configuration/CallTypeMappings.json", optional: false, reloadOnChange: true);
        config.AddEnvironmentVariables();

        // Key Vault config provider — only active when hosted in Azure (KeyVaultUri set in App Settings).
        // Local dev with Azurite skips this block entirely.
        var builtConfig = config.Build();
        var kvUri = builtConfig["KeyVaultUri"];
        if (!string.IsNullOrWhiteSpace(kvUri))
            config.AddAzureKeyVault(new Uri(kvUri), new DefaultAzureCredential());
    })
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<PipelineOptions>(ctx.Configuration.GetSection("Pipeline"));
        services.Configure<SalesforceOptions>(ctx.Configuration.GetSection("Salesforce"));
        services.Configure<EmailOptions>(ctx.Configuration.GetSection("Email"));

        // Managed Identity storage clients — shared across all services via DI.
        // Local dev uses Azurite via AzureWebJobsStorage connection string in local.settings.json;
        // in Azure, StorageAccountName + DefaultAzureCredential is used instead.
        var storageAccountName = ctx.Configuration["StorageAccountName"];
        if (!string.IsNullOrWhiteSpace(storageAccountName))
        {
            services.AddSingleton(_ =>
                new BlobServiceClient(
                    new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                    new DefaultAzureCredential()));

            services.AddSingleton(_ =>
                new TableServiceClient(
                    new Uri($"https://{storageAccountName}.table.core.windows.net"),
                    new DefaultAzureCredential()));

            services.AddSingleton(_ =>
                new QueueServiceClient(
                    new Uri($"https://{storageAccountName}.queue.core.windows.net"),
                    new DefaultAzureCredential(),
                    new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None }));
        }
        else
        {
            // Local development: fall back to connection string (Azurite or real storage)
            var connStr = ctx.Configuration["AzureWebJobsStorage"]
                ?? throw new InvalidOperationException(
                    "Either StorageAccountName (Managed Identity) or AzureWebJobsStorage (connection string) must be set.");

            services.AddSingleton(_ => new BlobServiceClient(connStr));
            services.AddSingleton(_ => new TableServiceClient(connStr));
            services.AddSingleton(_ => new QueueServiceClient(connStr,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None }));
        }

        services.AddHttpClient<ITranscriptionService, TranscriptionService>();
        services.AddHttpClient<ISalesforceService, SalesforceService>();
        services.AddSingleton<IBlobService, BlobService>();
        services.AddSingleton<IAuditService, AuditService>();
        services.AddSingleton<IEmailService, EmailService>();
        services.AddSingleton<ICallTypeMapper, CallTypeMapper>();

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

await host.RunAsync();
