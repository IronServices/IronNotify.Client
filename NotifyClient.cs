using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IronNotify.Client;

public class NotifyClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly NotifyClientOptions _options;
    private readonly NotifyOfflineQueue? _offlineQueue;
    private bool _disposed;

    /// <summary>
    /// Gets the offline queue for accessing queued items.
    /// </summary>
    public NotifyOfflineQueue? OfflineQueue => _offlineQueue;

    public NotifyClient(NotifyClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrEmpty(options.ApiKey))
            throw new ArgumentException("API key is required", nameof(options));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/")
        };

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        if (options.Timeout.HasValue)
            _httpClient.Timeout = options.Timeout.Value;

        // Initialize offline queue if enabled
        if (options.EnableOfflineQueue)
        {
            _offlineQueue = new NotifyOfflineQueue(
                SendQueuedEventAsync,
                options.OfflineQueueDirectory,
                options.MaxOfflineQueueSize,
                enableAutoRetry: true);
        }
    }

    private async Task<bool> SendQueuedEventAsync(QueuedEvent queuedEvent)
    {
        var result = await NotifyAsync(queuedEvent.Request, skipQueue: true);
        return result.Success;
    }

    public NotifyClient(string apiKey, string? appSlug = null)
        : this(new NotifyClientOptions { ApiKey = apiKey, DefaultAppSlug = appSlug })
    {
    }

    /// <summary>
    /// Send an event notification
    /// </summary>
    public Task<EventResult> NotifyAsync(NotifyEventRequest request, CancellationToken cancellationToken = default)
    {
        return NotifyAsync(request, skipQueue: false, cancellationToken);
    }

    private async Task<EventResult> NotifyAsync(NotifyEventRequest request, bool skipQueue, CancellationToken cancellationToken = default)
    {
        var apiRequest = new
        {
            appSlug = request.AppSlug ?? _options.DefaultAppSlug,
            appId = request.AppId,
            eventType = request.EventType,
            severity = request.Severity.ToString(),
            source = request.Source ?? _options.DefaultSource,
            title = request.Title,
            message = request.Message,
            entityId = request.EntityId,
            metadata = request.Metadata,
            actions = request.Actions?.Select(a => new
            {
                actionId = a.ActionId,
                label = a.Label,
                webhookUrl = a.WebhookUrl
            })
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/v1/events", apiRequest, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<EventResponse>(cancellationToken: cancellationToken);
                return new EventResult
                {
                    Success = true,
                    EventId = result?.Id,
                    Status = result?.Status
                };
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);

            // Queue for retry if enabled and not already retrying
            if (!skipQueue && _offlineQueue != null)
            {
                _offlineQueue.Enqueue(request);
            }

            return new EventResult
            {
                Success = false,
                Error = error,
                Queued = !skipQueue && _offlineQueue != null
            };
        }
        catch (Exception ex)
        {
            // Queue for retry on network errors
            if (!skipQueue && _offlineQueue != null)
            {
                _offlineQueue.Enqueue(request);
            }

            return new EventResult
            {
                Success = false,
                Error = ex.Message,
                Queued = !skipQueue && _offlineQueue != null
            };
        }
    }

    /// <summary>
    /// Send a simple event notification
    /// </summary>
    public Task<EventResult> NotifyAsync(
        string eventType,
        string title,
        Severity severity = Severity.Info,
        string? message = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return NotifyAsync(new NotifyEventRequest
        {
            EventType = eventType,
            Title = title,
            Severity = severity,
            Message = message,
            Metadata = metadata
        }, cancellationToken);
    }

    /// <summary>
    /// Create a fluent event builder
    /// </summary>
    public EventBuilder Event(string eventType) => new(this, eventType);

    public void Dispose()
    {
        if (!_disposed)
        {
            _offlineQueue?.Dispose();
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}

public class NotifyClientOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://ironnotify.com";
    public string? DefaultAppSlug { get; set; }
    public string? DefaultSource { get; set; }
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Enable offline queue for failed sends. Default is true.
    /// </summary>
    public bool EnableOfflineQueue { get; set; } = true;

    /// <summary>
    /// Directory to store offline queue. Defaults to LocalApplicationData/IronNotify/Queue.
    /// </summary>
    public string? OfflineQueueDirectory { get; set; }

    /// <summary>
    /// Maximum items to store in offline queue. Default: 500.
    /// </summary>
    public int MaxOfflineQueueSize { get; set; } = 500;
}

public class NotifyEventRequest
{
    public Guid? AppId { get; set; }
    public string? AppSlug { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Severity Severity { get; set; } = Severity.Info;
    public string? Source { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? EntityId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public List<EventAction>? Actions { get; set; }
}

public class EventAction
{
    public string ActionId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? WebhookUrl { get; set; }
}

public enum Severity
{
    Info,
    Warning,
    High,
    Critical
}

public class EventResult
{
    public bool Success { get; set; }
    public Guid? EventId { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Whether the event was queued for later retry (when offline queue is enabled).
    /// </summary>
    public bool Queued { get; set; }
}

internal class EventResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Fluent builder for creating events
/// </summary>
public class EventBuilder
{
    private readonly NotifyClient _client;
    private readonly NotifyEventRequest _request;

    internal EventBuilder(NotifyClient client, string eventType)
    {
        _client = client;
        _request = new NotifyEventRequest { EventType = eventType };
    }

    public EventBuilder WithSeverity(Severity severity)
    {
        _request.Severity = severity;
        return this;
    }

    public EventBuilder WithTitle(string title)
    {
        _request.Title = title;
        return this;
    }

    public EventBuilder WithMessage(string message)
    {
        _request.Message = message;
        return this;
    }

    public EventBuilder WithSource(string source)
    {
        _request.Source = source;
        return this;
    }

    public EventBuilder WithEntityId(string entityId)
    {
        _request.EntityId = entityId;
        return this;
    }

    public EventBuilder WithApp(string slug)
    {
        _request.AppSlug = slug;
        return this;
    }

    public EventBuilder WithApp(Guid appId)
    {
        _request.AppId = appId;
        return this;
    }

    public EventBuilder WithMetadata(string key, object value)
    {
        _request.Metadata ??= new Dictionary<string, object>();
        _request.Metadata[key] = value;
        return this;
    }

    public EventBuilder WithMetadata(Dictionary<string, object> metadata)
    {
        _request.Metadata = metadata;
        return this;
    }

    public EventBuilder WithAction(string actionId, string label, string? webhookUrl = null)
    {
        _request.Actions ??= new List<EventAction>();
        _request.Actions.Add(new EventAction
        {
            ActionId = actionId,
            Label = label,
            WebhookUrl = webhookUrl
        });
        return this;
    }

    public Task<EventResult> SendAsync(CancellationToken cancellationToken = default)
    {
        return _client.NotifyAsync(_request, cancellationToken);
    }
}
