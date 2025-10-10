using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetCheck.Services;

// this is only needed to ensure ollama has the required model downloaded and ready, otherwise nothing works
public sealed class OllamaService : IOllamaModelService, IDisposable
{
    private readonly string _modelName;
    private readonly string _endpoint;
    private readonly ILogger<OllamaService> _logger;
    private readonly HttpClient _httpClient;

    public OllamaService(IConfiguration configuration, ILogger<OllamaService> logger)
    {
        _logger = logger;
        _endpoint = configuration["AI:Ollama:Endpoint"] ?? "http://localhost:11434";
        _modelName = configuration["AI:Ollama:Model"] ?? "llama3.2:8b";
        _httpClient = new HttpClient();

        _logger.LogInformation("OllamaService initialised with endpoint: {Endpoint}, model: {Model}", _endpoint, _modelName);
    }

    public async Task<bool> EnsureModelIsLoadedAsync()
    {
        try
        {
            _logger.LogInformation("Checking if model {ModelName} is available", _modelName);

            // First check if model is already loaded/available
            if (await IsModelAvailableAsync())
            {
                _logger.LogInformation("Model {ModelName} is already available", _modelName);

                return true;
            }

            // If not available, try to pull it
            _logger.LogWarning("Model {ModelName} not found. Attempting to pull...", _modelName);

            if (await PullModelAsync())
            {
                _logger.LogInformation("Model {ModelName} successfully pulled", _modelName);

                return true;
            }

            _logger.LogError("Failed to ensure model {ModelName} is available", _modelName);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring model {ModelName} is loaded", _modelName);

            return false;
        }
    }

    public async Task<bool> IsModelAvailableAsync()
    {
        try
        {
            // Use HTTP client to check if model exists via direct API call
            HttpResponseMessage response = await _httpClient.GetAsync($"{_endpoint}/api/tags");

            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                JsonElement result = JsonSerializer.Deserialize<JsonElement>(content);

                if (result.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement model in models.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out JsonElement nameProperty))
                        {
                            string modelName = nameProperty.GetString();

                            if (modelName != null &&
                                (modelName.Equals(_modelName, StringComparison.OrdinalIgnoreCase) ||
                                 modelName.StartsWith(_modelName.Split(':')[0], StringComparison.OrdinalIgnoreCase)))
                            {
                                _logger.LogDebug("Model {ModelName} found in available models", _modelName);

                                return true;
                            }
                        }
                    }
                }
            }

            _logger.LogDebug("Model {ModelName} not found in available models", _modelName);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if model {ModelName} is available", _modelName);

            return false;
        }
    }

    public async Task<bool> PullModelAsync()
    {
        try
        {
            _logger.LogInformation("Starting to pull model {ModelName}. This may take several minutes...", _modelName);

            // Use HTTP client to pull model via direct API call
            string jsonContent = JsonSerializer.Serialize(new { name = _modelName });
            StringContent httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync($"{_endpoint}/api/pull", httpContent);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully initiated pull for model {ModelName}", _modelName);

                // Wait a bit and check if model is now available
                await Task.Delay(5000); // Wait 5 seconds

                return await IsModelAvailableAsync();
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();

                _logger.LogError("Failed to pull model {ModelName}. Status: {StatusCode}, Error: {Error}",_modelName, response.StatusCode, errorContent);

                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pulling model {ModelName}", _modelName);

            return false;
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _httpClient?.Dispose();
    }
}
