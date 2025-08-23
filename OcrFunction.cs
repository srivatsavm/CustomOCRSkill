using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CustomOcrSkill;

public class OcrFunction
{
    private readonly ILogger<OcrFunction> _logger;
    private readonly DocumentAnalysisClient _documentClient;
    private readonly HttpClient _httpClient;

    public OcrFunction(ILogger<OcrFunction> logger, DocumentAnalysisClient documentClient, HttpClient httpClient)
    {
        _logger = logger;
        _documentClient = documentClient;
        _httpClient = httpClient;
    }

    [Function("ProcessDocument")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("OCR processing started at {StartTime}", startTime);

        try
        {
            // Read and parse request
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var skillRequest = JsonSerializer.Deserialize<SkillRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (skillRequest?.Values == null || !skillRequest.Values.Any())
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid request format");
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            var skillResponse = new SkillResponse
            {
                Values = new List<SkillResponseRecord>()
            };

            // Process each document
            foreach (var record in skillRequest.Values)
            {
                var responseRecord = await ProcessSingleDocument(record);
                skillResponse.Values.Add(responseRecord);
            }

            var responseJson = JsonSerializer.Serialize(skillResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await response.WriteStringAsync(responseJson);

            var duration = (DateTime.UtcNow - startTime).TotalSeconds;
            _logger.LogInformation("OCR processing completed in {Duration} seconds", duration);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OCR request");
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Internal server error");
        }
    }

    private async Task<SkillResponseRecord> ProcessSingleDocument(SkillRequestRecord record)
    {
        var responseRecord = new SkillResponseRecord
        {
            RecordId = record.RecordId,
            Data = new Dictionary<string, object>(),
            Errors = new List<SkillError>(),
            Warnings = new List<SkillWarning>()
        };

        try
        {
            if (string.IsNullOrEmpty(record.Data?.Url))
            {
                responseRecord.Errors.Add(new SkillError { Message = "Document URL is required" });
                return responseRecord;
            }

            _logger.LogInformation("Processing document: {Url}", record.Data.Url);

            // Download document
            var documentBytes = await DownloadDocument(record.Data.Url);
            if (documentBytes == null)
            {
                responseRecord.Errors.Add(new SkillError { Message = "Failed to download document" });
                return responseRecord;
            }

            // Process with Document Intelligence
            using var documentStream = new MemoryStream(documentBytes);
            var operation = await _documentClient.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-read", documentStream);

            var result = operation.Value;

            // Extract text content
            var extractedText = new StringBuilder();
            foreach (var page in result.Pages)
            {
                foreach (var line in page.Lines)
                {
                    extractedText.AppendLine(line.Content);
                }
            }

            // Calculate confidence score
            var confidenceScores = result.Pages
                .SelectMany(p => p.Lines)
                .SelectMany(l => l.Words)
                .Where(w => w.Confidence.HasValue)
                .Select(w => w.Confidence.Value)
                .ToList();

            var avgConfidence = confidenceScores.Any() ? confidenceScores.Average() : 0.0;

            // Check for handwriting
            var handwritingDetected = result.Styles?.Any(s => s.IsHandwritten == true) == true;

            // Count tables
            var tableCount = result.Tables?.Count ?? 0;

            // Populate response data
            responseRecord.Data = new Dictionary<string, object>
            {
                ["extractedText"] = extractedText.ToString(),
                ["pageCount"] = result.Pages.Count,
                ["confidenceScore"] = Math.Round(avgConfidence, 3),
                ["handwritingDetected"] = handwritingDetected,
                ["tableCount"] = tableCount,
                ["processingStatus"] = "completed"
            };

            _logger.LogInformation("Successfully processed document {RecordId}: {PageCount} pages, confidence {Confidence}",
                record.RecordId, result.Pages.Count, avgConfidence);

        }
        catch (TaskCanceledException)
        {
            responseRecord.Errors.Add(new SkillError { Message = "Document processing timeout" });
            _logger.LogWarning("Processing timeout for document {RecordId}", record.RecordId);
        }
        catch (Exception ex)
        {
            responseRecord.Errors.Add(new SkillError { Message = $"Processing error: {ex.Message}" });
            _logger.LogError(ex, "Error processing document {RecordId}", record.RecordId);
        }

        return responseRecord;
    }

    private async Task<byte[]?> DownloadDocument(string url)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }

            _logger.LogError("Failed to download document: {StatusCode}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading document from {Url}", url);
            return null;
        }
    }

    private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json");

        var errorResponse = new { error = new { message } };
        var json = JsonSerializer.Serialize(errorResponse);
        await response.WriteStringAsync(json);

        return response;
    }
}

// Request/Response Models
public class SkillRequest
{
    public List<SkillRequestRecord> Values { get; set; } = new();
}

public class SkillRequestRecord
{
    public string RecordId { get; set; } = string.Empty;
    public SkillRequestData? Data { get; set; }
}

public class SkillRequestData
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, object>? Metadata { get; set; }
}

public class SkillResponse
{
    public List<SkillResponseRecord> Values { get; set; } = new();
}

public class SkillResponseRecord
{
    public string RecordId { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public List<SkillError> Errors { get; set; } = new();
    public List<SkillWarning> Warnings { get; set; } = new();
}

public class SkillError
{
    public string Message { get; set; } = string.Empty;
}

public class SkillWarning
{
    public string Message { get; set; } = string.Empty;
}