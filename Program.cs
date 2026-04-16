using AudioToTranscript.Configuration;
using AudioToTranscript.Services;
using AudioToTranscript.Utils;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((ctx, config) =>
    {
        config.AddJsonFile("Configuration/CallTypeMappings.json", optional: false, reloadOnChange: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<PipelineOptions>(ctx.Configuration.GetSection("Pipeline"));
        services.Configure<SalesforceOptions>(ctx.Configuration.GetSection("Salesforce"));
        services.Configure<EmailOptions>(ctx.Configuration.GetSection("Email"));

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
