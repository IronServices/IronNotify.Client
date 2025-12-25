# IronNotify.Client

Event notification SDK for .NET applications. Send alerts via push, email, SMS, webhooks, and in-app notifications with offline queue support.

[![NuGet](https://img.shields.io/nuget/v/IronNotify.Client.svg)](https://www.nuget.org/packages/IronNotify.Client)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## Installation

```bash
dotnet add package IronNotify.Client
```

## Quick Start

### Simple Usage

```csharp
using IronNotify.Client;

// Create client with API key
var client = new NotifyClient("your-api-key", appSlug: "my-app");

// Send an event
var result = await client.NotifyAsync(
    eventType: "order.completed",
    title: "New Order Received",
    severity: Severity.Info,
    message: "Order #12345 has been placed"
);

if (result.Success)
{
    Console.WriteLine($"Event sent! ID: {result.EventId}");
}
```

### Fluent Builder

```csharp
var result = await client
    .Event("payment.failed")
    .WithSeverity(Severity.High)
    .WithTitle("Payment Failed")
    .WithMessage("Customer payment was declined")
    .WithEntityId("order-12345")
    .WithMetadata("amount", 99.99)
    .WithMetadata("currency", "USD")
    .WithAction("retry", "Retry Payment", webhookUrl: "https://api.example.com/retry")
    .SendAsync();
```

## Configuration Options

```csharp
var client = new NotifyClient(new NotifyClientOptions
{
    // Required
    ApiKey = "your-api-key",

    // Optional
    DefaultAppSlug = "my-app",
    DefaultSource = "backend-service",
    BaseUrl = "https://ironnotify.com",
    Timeout = TimeSpan.FromSeconds(30),

    // Offline queue (enabled by default)
    EnableOfflineQueue = true,
    MaxOfflineQueueSize = 500,
    OfflineQueueDirectory = null  // Uses LocalApplicationData by default
});
```

## Severity Levels

```csharp
Severity.Info      // Informational events
Severity.Warning   // Warnings that may need attention
Severity.High      // Important events requiring action
Severity.Critical  // Critical events requiring immediate attention
```

## Metadata

Add custom key-value pairs to events:

```csharp
await client.NotifyAsync(new NotifyEventRequest
{
    EventType = "user.signup",
    Title = "New User Registration",
    Severity = Severity.Info,
    Metadata = new Dictionary<string, object>
    {
        ["userId"] = "user-123",
        ["plan"] = "pro",
        ["referrer"] = "google"
    }
});
```

## Actions

Add clickable actions to notifications:

```csharp
await client
    .Event("server.down")
    .WithSeverity(Severity.Critical)
    .WithTitle("Server Unreachable")
    .WithAction("restart", "Restart Server", webhookUrl: "https://api.example.com/restart")
    .WithAction("acknowledge", "Acknowledge")
    .SendAsync();
```

## Offline Queue

Events are automatically queued when the network is unavailable:

```csharp
var result = await client.NotifyAsync("event.type", "Title");

if (result.Queued)
{
    Console.WriteLine("Event queued for retry when online");
}

// Check queue status
if (client.OfflineQueue != null)
{
    Console.WriteLine($"Queued items: {client.OfflineQueue.Count}");
}
```

The offline queue:
- Persists events to disk
- Automatically retries when connectivity is restored
- Respects `MaxOfflineQueueSize` limit
- Works across app restarts

## Real-Time Notifications

For receiving real-time notifications, use `NotifyRealTimeClient`:

```csharp
using IronNotify.Client;

var realtime = new NotifyRealTimeClient(
    hubUrl: "https://ironnotify.com/hubs/events",
    apiKey: "your-api-key"
);

// Subscribe to events
realtime.OnEventReceived += (sender, evt) =>
{
    Console.WriteLine($"Received: {evt.Title} ({evt.Severity})");
};

// Connect
await realtime.ConnectAsync();

// Join app channel
await realtime.JoinAppAsync("my-app");

// Disconnect when done
await realtime.DisconnectAsync();
```

## Dependency Injection

```csharp
// Register in DI container
services.AddSingleton<NotifyClient>(sp =>
    new NotifyClient(new NotifyClientOptions
    {
        ApiKey = configuration["IronNotify:ApiKey"],
        DefaultAppSlug = configuration["IronNotify:AppSlug"]
    }));
```

## Common Event Types

```csharp
// User events
"user.signup", "user.login", "user.password_reset"

// Order events
"order.created", "order.completed", "order.cancelled"

// Payment events
"payment.succeeded", "payment.failed", "payment.refunded"

// System events
"server.error", "deployment.completed", "backup.finished"
```

## Links

- [Documentation](https://www.ironnotify.com/docs)
- [Dashboard](https://www.ironnotify.com)

## License

MIT License - see [LICENSE](LICENSE) for details.
