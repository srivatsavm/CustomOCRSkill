using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Add Document Intelligence client with API key authentication
        services.AddSingleton<DocumentAnalysisClient>(provider =>
        {
            var endpoint = Environment.GetEnvironmentVariable("DOCUMENT_INTELLIGENCE_ENDPOINT");
            var apiKey = Environment.GetEnvironmentVariable("DOCUMENT_INTELLIGENCE_API_KEY");

            if (string.IsNullOrEmpty(endpoint))
                throw new InvalidOperationException("DOCUMENT_INTELLIGENCE_ENDPOINT environment variable is required");

            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("DOCUMENT_INTELLIGENCE_API_KEY environment variable is required");

            return new DocumentAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
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