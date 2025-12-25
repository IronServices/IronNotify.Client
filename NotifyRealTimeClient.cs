using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;

namespace IronNotify.Client;

/// <summary>
/// Real-time notification client using SignalR for receiving live notifications
/// </summary>
public class NotifyRealTimeClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly NotifyRealTimeOptions _options;
    private bool _disposed;

    /// <summary>
    /// Fired when a new notification is received
    /// </summary>
    public event EventHandler<NotificationReceivedEventArgs>? NotificationReceived;

    /// <summary>
    /// Fired when the unread count changes
    /// </summary>
    public event EventHandler<UnreadCountChangedEventArgs>? UnreadCountChanged;

    /// <summary>
    /// Fired when a notification is marked as read
    /// </summary>
    public event EventHandler<NotificationReadEventArgs>? NotificationRead;

    /// <summary>
    /// Fired when an event status changes (acknowledged, resolved)
    /// </summary>
    public event EventHandler<EventStatusChangedEventArgs>? EventStatusChanged;

    /// <summary>
    /// Fired when the connection state changes
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Current connection state
    /// </summary>
    public HubConnectionState State => _connection.State;

    /// <summary>
    /// Whether the client is currently connected
    /// </summary>
    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public NotifyRealTimeClient(NotifyRealTimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var hubUrl = options.BaseUrl.TrimEnd('/') + "/hubs/notifications";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, httpOptions =>
            {
                httpOptions.Headers.Add("Authorization", $"Bearer {options.ApiKey}");
            })
            .WithAutomaticReconnect(new RetryPolicy(options.MaxReconnectAttempts))
            .Build();

        SetupEventHandlers();
    }

    public NotifyRealTimeClient(string apiKey, string baseUrl = "https://ironnotify.com")
        : this(new NotifyRealTimeOptions { ApiKey = apiKey, BaseUrl = baseUrl })
    {
    }

    private void SetupEventHandlers()
    {
        _connection.On<RealTimeNotification>("NotificationReceived", notification =>
        {
            NotificationReceived?.Invoke(this, new NotificationReceivedEventArgs(notification));
        });

        _connection.On<int>("UnreadCountChanged", count =>
        {
            UnreadCountChanged?.Invoke(this, new UnreadCountChangedEventArgs(count));
        });

        _connection.On<NotificationReadPayload>("NotificationRead", payload =>
        {
            NotificationRead?.Invoke(this, new NotificationReadEventArgs(payload.EventId, payload.ReadAt));
        });

        _connection.On<EventStatusPayload>("EventStatusChanged", payload =>
        {
            EventStatusChanged?.Invoke(this, new EventStatusChangedEventArgs(payload.EventId, payload.Status, payload.UpdatedAt));
        });

        _connection.Reconnecting += error =>
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(
                HubConnectionState.Reconnecting, error?.Message));
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(
                HubConnectionState.Connected, null));

            // Re-subscribe to user notifications after reconnect
            if (_options.UserId.HasValue)
            {
                _ = SubscribeToUserAsync(_options.UserId.Value);
            }

            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(
                HubConnectionState.Disconnected, error?.Message));
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Connect to the notification hub
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection.State == HubConnectionState.Disconnected)
        {
            await _connection.StartAsync(cancellationToken);
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(
                HubConnectionState.Connected, null));
        }
    }

    /// <summary>
    /// Disconnect from the notification hub
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection.State == HubConnectionState.Connected)
        {
            await _connection.StopAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Subscribe to notifications for a specific user
    /// </summary>
    public async Task SubscribeToUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await _connection.InvokeAsync("SubscribeToUser", userId, cancellationToken);
        _options.UserId = userId;
    }

    /// <summary>
    /// Unsubscribe from user notifications
    /// </summary>
    public async Task UnsubscribeFromUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await _connection.InvokeAsync("UnsubscribeFromUser", userId, cancellationToken);
    }

    /// <summary>
    /// Subscribe to notifications for a specific app
    /// </summary>
    public async Task SubscribeToAppAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await _connection.InvokeAsync("SubscribeToApp", appId, cancellationToken);
    }

    /// <summary>
    /// Unsubscribe from app notifications
    /// </summary>
    public async Task UnsubscribeFromAppAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await _connection.InvokeAsync("UnsubscribeFromApp", appId, cancellationToken);
    }

    /// <summary>
    /// Mark a notification as read (triggers server-side update and broadcasts to other clients)
    /// </summary>
    public async Task MarkAsReadAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await _connection.InvokeAsync("MarkAsRead", userId, eventId, cancellationToken);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection.State == HubConnectionState.Disconnected)
        {
            await ConnectAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _connection.DisposeAsync();
            _disposed = true;
        }
    }

    private class RetryPolicy : IRetryPolicy
    {
        private readonly int _maxAttempts;

        public RetryPolicy(int maxAttempts) => _maxAttempts = maxAttempts;

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (retryContext.PreviousRetryCount >= _maxAttempts)
                return null;

            // Exponential backoff: 0s, 2s, 4s, 8s, 16s, max 30s
            var delay = Math.Min(Math.Pow(2, retryContext.PreviousRetryCount), 30);
            return TimeSpan.FromSeconds(delay);
        }
    }
}

public class NotifyRealTimeOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://ironnotify.com";
    public Guid? UserId { get; set; }
    public int MaxReconnectAttempts { get; set; } = 5;
}

#region Event Args

public class NotificationReceivedEventArgs : EventArgs
{
    public RealTimeNotification Notification { get; }

    public NotificationReceivedEventArgs(RealTimeNotification notification)
    {
        Notification = notification;
    }
}

public class UnreadCountChangedEventArgs : EventArgs
{
    public int UnreadCount { get; }

    public UnreadCountChangedEventArgs(int unreadCount)
    {
        UnreadCount = unreadCount;
    }
}

public class NotificationReadEventArgs : EventArgs
{
    public Guid EventId { get; }
    public DateTime ReadAt { get; }

    public NotificationReadEventArgs(Guid eventId, DateTime readAt)
    {
        EventId = eventId;
        ReadAt = readAt;
    }
}

public class EventStatusChangedEventArgs : EventArgs
{
    public Guid EventId { get; }
    public string Status { get; }
    public DateTime UpdatedAt { get; }

    public EventStatusChangedEventArgs(Guid eventId, string status, DateTime updatedAt)
    {
        EventId = eventId;
        Status = status;
        UpdatedAt = updatedAt;
    }
}

public class ConnectionStateChangedEventArgs : EventArgs
{
    public HubConnectionState State { get; }
    public string? Error { get; }

    public ConnectionStateChangedEventArgs(HubConnectionState state, string? error)
    {
        State = state;
        Error = error;
    }
}

#endregion

#region DTOs

public class RealTimeNotification
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; set; }

    [JsonPropertyName("deliveryId")]
    public Guid DeliveryId { get; set; }

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("appId")]
    public Guid AppId { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("appSlug")]
    public string? AppSlug { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object>? Data { get; set; }

    [JsonPropertyName("actions")]
    public List<RealTimeAction>? Actions { get; set; }
}

public class RealTimeAction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; set; }
}

internal class NotificationReadPayload
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; set; }

    [JsonPropertyName("readAt")]
    public DateTime ReadAt { get; set; }
}

internal class EventStatusPayload
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

#endregion
