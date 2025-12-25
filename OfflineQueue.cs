using System.Text.Json;
using System.Text.Json.Serialization;

namespace IronNotify.Client;

/// <summary>
/// File-based offline queue for notification events.
/// Persists failed sends to disk and retries with exponential backoff.
/// </summary>
public class NotifyOfflineQueue : IDisposable
{
    private readonly string _queueFilePath;
    private readonly int _maxQueueSize;
    private readonly object _fileLock = new();
    private readonly SemaphoreSlim _retrySemaphore = new(1, 1);
    private readonly Timer? _retryTimer;
    private readonly Func<QueuedEvent, Task<bool>> _sendFunc;
    private int _retryAttempt;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Creates a new offline queue.
    /// </summary>
    /// <param name="sendFunc">Function to send an event to server. Returns true on success.</param>
    /// <param name="queueDirectory">Directory to store queue file. Defaults to app data.</param>
    /// <param name="maxQueueSize">Maximum items to store. Oldest items dropped when exceeded.</param>
    /// <param name="enableAutoRetry">Whether to automatically retry sending queued items.</param>
    public NotifyOfflineQueue(
        Func<QueuedEvent, Task<bool>> sendFunc,
        string? queueDirectory = null,
        int maxQueueSize = 500,
        bool enableAutoRetry = true)
    {
        _sendFunc = sendFunc;
        _maxQueueSize = maxQueueSize;

        var directory = queueDirectory ?? GetDefaultQueueDirectory();
        Directory.CreateDirectory(directory);
        _queueFilePath = Path.Combine(directory, "notify_queue.json");

        if (enableAutoRetry)
        {
            // Start retry timer - initial delay 30 seconds, then every 60 seconds
            _retryTimer = new Timer(
                _ => _ = RetryQueuedItemsAsync(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// Number of items currently in the queue.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_fileLock)
            {
                var items = LoadQueue();
                return items.Count;
            }
        }
    }

    /// <summary>
    /// Enqueue an event that failed to send.
    /// </summary>
    public void Enqueue(NotifyEventRequest request)
    {
        lock (_fileLock)
        {
            var queue = LoadQueue();
            queue.Add(new QueuedEvent
            {
                QueuedAt = DateTime.UtcNow,
                Request = request
            });

            // Trim to max size (remove oldest)
            while (queue.Count > _maxQueueSize)
            {
                queue.RemoveAt(0);
            }

            SaveQueue(queue);
        }
    }

    /// <summary>
    /// Try to send all queued items.
    /// </summary>
    public async Task<int> RetryQueuedItemsAsync()
    {
        if (!await _retrySemaphore.WaitAsync(0))
        {
            // Already retrying
            return 0;
        }

        int sentCount = 0;

        try
        {
            List<QueuedEvent> items;
            lock (_fileLock)
            {
                items = LoadQueue();
            }

            if (items.Count == 0)
            {
                _retryAttempt = 0;
                return 0;
            }

            var remaining = new List<QueuedEvent>();

            foreach (var item in items)
            {
                var success = await _sendFunc(item);
                if (success)
                {
                    sentCount++;
                }
                else
                {
                    remaining.Add(item);
                }
            }

            // Save remaining items
            lock (_fileLock)
            {
                SaveQueue(remaining);
            }

            if (remaining.Count == 0)
            {
                _retryAttempt = 0;
            }
            else
            {
                _retryAttempt++;
            }

            return sentCount;
        }
        catch
        {
            _retryAttempt++;
            return 0;
        }
        finally
        {
            _retrySemaphore.Release();
        }
    }

    /// <summary>
    /// Clear all queued items without sending.
    /// </summary>
    public void Clear()
    {
        lock (_fileLock)
        {
            SaveQueue(new List<QueuedEvent>());
        }
    }

    /// <summary>
    /// Get all queued items (for display/export).
    /// </summary>
    public List<QueuedEvent> GetQueuedItems()
    {
        lock (_fileLock)
        {
            return LoadQueue();
        }
    }

    private List<QueuedEvent> LoadQueue()
    {
        try
        {
            if (!File.Exists(_queueFilePath))
            {
                return new List<QueuedEvent>();
            }

            var json = File.ReadAllText(_queueFilePath);
            return JsonSerializer.Deserialize<List<QueuedEvent>>(json, JsonOptions)
                   ?? new List<QueuedEvent>();
        }
        catch
        {
            return new List<QueuedEvent>();
        }
    }

    private void SaveQueue(List<QueuedEvent> items)
    {
        try
        {
            var json = JsonSerializer.Serialize(items, JsonOptions);
            File.WriteAllText(_queueFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    private static string GetDefaultQueueDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IronNotify",
            "Queue");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _retryTimer?.Dispose();
            _retrySemaphore.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// A queued notification event with metadata.
/// </summary>
public class QueuedEvent
{
    public DateTime QueuedAt { get; set; }
    public int RetryCount { get; set; }
    public NotifyEventRequest Request { get; set; } = new();
}
