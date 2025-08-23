using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure;
using System.Text;
using System.Linq;

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
        var requestId = Guid.NewGuid().ToString("N")[..8]; // Short request ID for tracking

        _logger.LogInformation("=== AZURE FUNCTION REQUEST START [{RequestId}] ===", requestId);
        _logger.LogInformation("Request started at: {StartTime}", startTime);
        _logger.LogInformation("Request URL: {Url}", req.Url);
        _logger.LogInformation("Request Method: {Method}", req.Method);

        // Log request headers
        _logger.LogInformation("=== REQUEST HEADERS ===");
        foreach (var header in req.Headers)
        {
            // Don't log sensitive auth headers in full
            var headerValue = header.Key.ToLower().Contains("auth") || header.Key.ToLower().Contains("key")
                ? "[REDACTED]"
                : string.Join(", ", header.Value);
            _logger.LogInformation("Header: {Name} = {Value}", header.Key, headerValue);
        }

        try
        {
            // Read and parse request
            _logger.LogInformation("Reading request body...");
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogInformation("Request body length: {Length} characters", requestBody.Length);
            _logger.LogInformation("Request body preview (first 500 chars): {Preview}",
                requestBody.Length > 500 ? requestBody[..500] + "..." : requestBody);

            var skillRequest = JsonSerializer.Deserialize<SkillRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (skillRequest?.Values == null || !skillRequest.Values.Any())
            {
                _logger.LogError("Invalid request format - missing or empty values array");
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid request format");
            }

            _logger.LogInformation("Valid skill request received with {DocumentCount} document(s)", skillRequest.Values.Count);

            // Log each document to be processed
            for (int i = 0; i < skillRequest.Values.Count; i++)
            {
                var record = skillRequest.Values[i];
                _logger.LogInformation("Document {Index}: RecordId='{RecordId}', URL='{Url}'",
                    i + 1, record.RecordId, record.Data?.Url ?? "[NULL]");
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            var skillResponse = new SkillResponse
            {
                Values = new List<SkillResponseRecord>()
            };

            // Process each document
            _logger.LogInformation("Starting document processing...");
            var processedCount = 0;
            var errorCount = 0;

            foreach (var record in skillRequest.Values)
            {
                _logger.LogInformation("Processing document {Current}/{Total}: {RecordId}",
                    ++processedCount, skillRequest.Values.Count, record.RecordId);

                var responseRecord = await ProcessSingleDocument(record);
                skillResponse.Values.Add(responseRecord);

                if (responseRecord.Errors.Any())
                {
                    errorCount++;
                    _logger.LogWarning("Document {RecordId} processing completed with {ErrorCount} error(s)",
                        record.RecordId, responseRecord.Errors.Count);
                }
                else
                {
                    _logger.LogInformation("Document {RecordId} processing completed successfully", record.RecordId);
                }
            }

            _logger.LogInformation("All documents processed - Success: {SuccessCount}, Errors: {ErrorCount}",
                processedCount - errorCount, errorCount);

            var responseJson = JsonSerializer.Serialize(skillResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            _logger.LogInformation("Response JSON length: {Length} characters", responseJson.Length);
            await response.WriteStringAsync(responseJson);

            var duration = (DateTime.UtcNow - startTime).TotalSeconds;
            _logger.LogInformation("=== AZURE FUNCTION REQUEST SUCCESS [{RequestId}] ===", requestId);
            _logger.LogInformation("Total processing time: {Duration:F2} seconds", duration);

            return response;
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError(jsonEx, "JSON deserialization error [{RequestId}]", requestId);
            _logger.LogError("JSON error details: {Message} at position {Position}", jsonEx.Message, jsonEx.BytePositionInLine);
            return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid JSON format");
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalSeconds;
            _logger.LogError(ex, "=== AZURE FUNCTION REQUEST FAILED [{RequestId}] ===", requestId);
            _logger.LogError("Request failed after {Duration:F2} seconds", duration);
            _logger.LogError("Exception type: {ExceptionType}", ex.GetType().Name);
            _logger.LogError("Exception details: {ExceptionDetails}", ex.ToString());
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Internal server error");
        }
    }

    private async Task<SkillResponseRecord> ProcessSingleDocument(SkillRequestRecord record)
    {
        _logger.LogInformation("=== PROCESSING DOCUMENT START ===");
        _logger.LogInformation("Processing document with RecordId: {RecordId}", record.RecordId);

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
                _logger.LogError("Document URL is missing for RecordId: {RecordId}", record.RecordId);
                responseRecord.Errors.Add(new SkillError { Message = "Document URL is required" });
                return responseRecord;
            }

            _logger.LogInformation("Document URL: {Url}", record.Data.Url);

            // Log metadata if available
            if (record.Data.Metadata != null)
            {
                _logger.LogInformation("Document metadata: {Metadata}",
                    string.Join(", ", record.Data.Metadata.Select(kv => $"{kv.Key}={kv.Value}")));
            }

            // Download document
            _logger.LogInformation("Starting document download...");
            var documentBytes = await DownloadDocument(record.Data.Url);
            if (documentBytes == null)
            {
                _logger.LogError("Document download failed for RecordId: {RecordId}, URL: {Url}",
                    record.RecordId, record.Data.Url);
                responseRecord.Errors.Add(new SkillError { Message = "Failed to download document" });
                return responseRecord;
            }

            _logger.LogInformation("Document downloaded successfully - Size: {Size} bytes", documentBytes.Length);

            // Process with Document Intelligence
            _logger.LogInformation("=== DOCUMENT INTELLIGENCE PROCESSING START ===");
            _logger.LogInformation("Starting OCR processing with Azure Document Intelligence...");

            using var documentStream = new MemoryStream(documentBytes);
            _logger.LogInformation("Created document stream - Length: {Length} bytes", documentStream.Length);

            var processingStartTime = DateTime.UtcNow;
            _logger.LogInformation("Calling Document Intelligence AnalyzeDocumentAsync at: {StartTime}", processingStartTime);

            var operation = await _documentClient.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-read", documentStream);

            var processingEndTime = DateTime.UtcNow;
            var processingDuration = (processingEndTime - processingStartTime).TotalSeconds;
            _logger.LogInformation("Document Intelligence processing completed in: {Duration} seconds", processingDuration);

            var result = operation.Value;
            _logger.LogInformation("=== OCR RESULTS ANALYSIS ===");
            _logger.LogInformation("Pages found: {PageCount}", result.Pages.Count);
            _logger.LogInformation("Tables found: {TableCount}", result.Tables?.Count ?? 0);
            _logger.LogInformation("Styles found: {StyleCount}", result.Styles?.Count ?? 0);

            // Extract text content
            var extractedText = new StringBuilder();
            var totalLines = 0;
            var totalWords = 0;

            foreach (var page in result.Pages)
            {
                _logger.LogInformation("Processing page {PageNumber} - Lines: {LineCount}, Words: {WordCount}",
                    page.PageNumber, page.Lines.Count, page.Words.Count);

                foreach (var line in page.Lines)
                {
                    extractedText.AppendLine(line.Content);
                    totalLines++;
                }
                totalWords += page.Words.Count;
            }

            _logger.LogInformation("Text extraction completed - Total lines: {TotalLines}, Total words: {TotalWords}",
                totalLines, totalWords);
            _logger.LogInformation("Extracted text length: {TextLength} characters", extractedText.Length);

            // Calculate confidence score
            var confidenceScores = result.Pages
                .SelectMany(p => p.Words)
                .Select(w => w.Confidence)
                .ToList();

            var avgConfidence = confidenceScores.Any() ? confidenceScores.Average() : 0.0;
            _logger.LogInformation("Confidence analysis - Words with confidence: {WordCount}, Average: {AvgConfidence:F3}, Min: {MinConfidence:F3}, Max: {MaxConfidence:F3}",
                confidenceScores.Count, avgConfidence,
                confidenceScores.Any() ? confidenceScores.Min() : 0.0,
                confidenceScores.Any() ? confidenceScores.Max() : 0.0);

            // Check for handwriting
            var handwritingDetected = result.Styles?.Any(s => s.IsHandwritten == true) == true;
            if (handwritingDetected)
            {
                var handwritingStyles = result.Styles?.Where(s => s.IsHandwritten == true).Count() ?? 0;
                _logger.LogInformation("Handwriting detected in {StyleCount} style(s)", handwritingStyles);
            }
            else
            {
                _logger.LogInformation("No handwriting detected");
            }

            // Count tables
            var tableCount = result.Tables?.Count ?? 0;
            if (tableCount > 0)
            {
                _logger.LogInformation("Tables found: {TableCount}", tableCount);
                for (int i = 0; i < tableCount; i++)
                {
                    var table = result.Tables[i];
                    _logger.LogInformation("Table {TableIndex}: {RowCount} rows x {ColumnCount} columns",
                        i + 1, table.RowCount, table.ColumnCount);
                }
            }

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

            _logger.LogInformation("=== PROCESSING DOCUMENT SUCCESS ===");
            _logger.LogInformation("Successfully processed document {RecordId}: {PageCount} pages, confidence {Confidence:F3}, processing time {Duration:F1}s",
                record.RecordId, result.Pages.Count, avgConfidence, processingDuration);

        }
        catch (TaskCanceledException timeoutEx)
        {
            _logger.LogError(timeoutEx, "Document processing timeout for RecordId: {RecordId}", record.RecordId);
            _logger.LogError("Timeout details - Is cancellation requested: {IsCancelled}",
                timeoutEx.CancellationToken.IsCancellationRequested);
            responseRecord.Errors.Add(new SkillError { Message = "Document processing timeout" });
        }
        catch (Azure.RequestFailedException azureEx)
        {
            _logger.LogError(azureEx, "Azure Document Intelligence API error for RecordId: {RecordId}", record.RecordId);
            _logger.LogError("Azure error details - Status: {Status}, ErrorCode: {ErrorCode}, Message: {Message}",
                azureEx.Status, azureEx.ErrorCode, azureEx.Message);
            responseRecord.Errors.Add(new SkillError { Message = $"Document Intelligence API error: {azureEx.Message}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing document {RecordId}", record.RecordId);
            _logger.LogError("Exception type: {ExceptionType}, Message: {Message}", ex.GetType().Name, ex.Message);
            _logger.LogError("Stack trace: {StackTrace}", ex.StackTrace);
            responseRecord.Errors.Add(new SkillError { Message = $"Processing error: {ex.Message}" });
        }

        _logger.LogInformation("=== PROCESSING DOCUMENT END ===");
        return responseRecord;
    }

    private async Task<byte[]?> DownloadDocument(string url)
    {
        _logger.LogInformation("=== DOCUMENT DOWNLOAD START ===");
        _logger.LogInformation("Attempting to download document from URL: {Url}", url);

        try
        {
            // Log URL details
            var uri = new Uri(url);
            _logger.LogInformation("URL Components - Host: {Host}, Path: {Path}, Query: {Query}",
                uri.Host, uri.AbsolutePath, uri.Query);

            // Configure HttpClient with detailed logging
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _logger.LogInformation("Created HTTP GET request for: {RequestUri}", request.RequestUri);

            // Add headers for better blob storage compatibility
            request.Headers.Add("User-Agent", "AzureFunction-CustomOCRSkill/1.0");
            request.Headers.Add("Accept", "*/*");

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Sending HTTP request at: {StartTime}", startTime);

            using var response = await _httpClient.SendAsync(request);
            var endTime = DateTime.UtcNow;
            var duration = (endTime - startTime).TotalMilliseconds;

            _logger.LogInformation("HTTP Response received - Status: {StatusCode} ({StatusDescription}), Duration: {Duration}ms",
                (int)response.StatusCode, response.StatusCode, duration);

            // Log all response headers
            _logger.LogInformation("=== RESPONSE HEADERS ===");
            foreach (var header in response.Headers)
            {
                _logger.LogInformation("Header: {Name} = {Value}", header.Key, string.Join(", ", header.Value));
            }
            foreach (var header in response.Content.Headers)
            {
                _logger.LogInformation("Content Header: {Name} = {Value}", header.Key, string.Join(", ", header.Value));
            }

            if (response.IsSuccessStatusCode)
            {
                var contentLength = response.Content.Headers.ContentLength;
                _logger.LogInformation("Success! Content-Length: {ContentLength} bytes", contentLength ?? -1);

                var content = await response.Content.ReadAsByteArrayAsync();
                _logger.LogInformation("Successfully downloaded {ActualBytes} bytes", content.Length);
                _logger.LogInformation("=== DOCUMENT DOWNLOAD SUCCESS ===");

                return content;
            }
            else
            {
                // Log detailed error information
                _logger.LogError("=== DOWNLOAD FAILED ===");
                _logger.LogError("Status Code: {StatusCode} ({StatusCodeNumber})", response.StatusCode, (int)response.StatusCode);
                _logger.LogError("Reason Phrase: {ReasonPhrase}", response.ReasonPhrase);

                // Try to read error content
                try
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Error Response Body: {ErrorContent}", errorContent);

                    // Check if it's an Azure Storage error (XML format)
                    if (errorContent.Contains("<?xml") && errorContent.Contains("Error"))
                    {
                        _logger.LogError("This appears to be an Azure Storage XML error response");
                        if (errorContent.Contains("BlobNotFound"))
                            _logger.LogError("Blob not found - check if file exists in storage");
                        if (errorContent.Contains("AuthenticationFailed"))
                            _logger.LogError("Authentication failed - check blob permissions or SAS token");
                        if (errorContent.Contains("ContainerNotFound"))
                            _logger.LogError("Container not found - check container name and permissions");
                        if (errorContent.Contains("ResourceTypeMismatch"))
                            _logger.LogError("Resource type mismatch - URL might point to container instead of blob");
                    }
                }
                catch (Exception readEx)
                {
                    _logger.LogError(readEx, "Could not read error response content");
                }

                // Specific handling for 409 Conflict
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    _logger.LogError("=== 409 CONFLICT ANALYSIS ===");
                    _logger.LogError("409 Conflict typically indicates:");
                    _logger.LogError("1. Blob is being written to (upload in progress)");
                    _logger.LogError("2. Lease conflict (blob is locked)");
                    _logger.LogError("3. Container/blob name conflict");
                    _logger.LogError("4. Operation already in progress");
                    _logger.LogError("5. Blob metadata conflict");

                    // Check for specific Azure Storage error codes in headers
                    if (response.Headers.Contains("x-ms-error-code"))
                    {
                        var errorCode = response.Headers.GetValues("x-ms-error-code").FirstOrDefault();
                        _logger.LogError("Azure Storage Error Code: {ErrorCode}", errorCode);

                        switch (errorCode)
                        {
                            case "BlobBeingRewritten":
                                _logger.LogError("Blob is currently being written - retry after a delay");
                                break;
                            case "LeaseIdMissing":
                                _logger.LogError("Blob has a lease but no lease ID provided");
                                break;
                            case "LeaseIdMismatch":
                                _logger.LogError("Lease ID mismatch");
                                break;
                            case "LeaseLocked":
                                _logger.LogError("Blob is locked with a lease");
                                break;
                        }
                    }
                }

                _logger.LogInformation("=== DOCUMENT DOWNLOAD FAILED ===");
                return null;
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP request exception downloading document from {Url}", url);
            _logger.LogError("HTTP Exception Details - Message: {Message}, Data: {Data}",
                httpEx.Message, httpEx.Data);
            return null;
        }
        catch (TaskCanceledException timeoutEx)
        {
            _logger.LogError(timeoutEx, "Request timeout downloading document from {Url}", url);
            _logger.LogError("Timeout Details - Cancelled: {IsCancelled}, Message: {Message}",
                timeoutEx.CancellationToken.IsCancellationRequested, timeoutEx.Message);
            return null;
        }
        catch (UriFormatException uriEx)
        {
            _logger.LogError(uriEx, "Invalid URL format: {Url}", url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading document from {Url}", url);
            _logger.LogError("Exception Type: {ExceptionType}", ex.GetType().Name);
            _logger.LogError("Exception Details: {Details}", ex.ToString());
            return null;
        }
    }

    private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
    {
        _logger.LogError("Creating error response - Status: {StatusCode}, Message: {Message}", statusCode, message);

        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json");

        var errorResponse = new { error = new { message, statusCode = (int)statusCode, timestamp = DateTime.UtcNow } };
        var json = JsonSerializer.Serialize(errorResponse);

        _logger.LogInformation("Error response JSON: {Json}", json);
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