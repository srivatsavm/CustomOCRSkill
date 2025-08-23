using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Identity;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Add Document Intelligence client
        services.AddSingleton<DocumentAnalysisClient>(provider =>
        {
            var endpoint = Environment.GetEnvironmentVariable("DOCUMENT_INTELLIGENCE_ENDPOINT");
            return new DocumentAnalysisClient(new Uri(endpoint), new DefaultAzureCredential());
        });

        // Add HTTP client
        services.AddHttpClient();

        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddApplicationInsights();
        });
    })
    .Build();

host.Run();