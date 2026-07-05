using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace DevCompanion.Agent.Integration;

public class SseClient
{
    public HttpListenerResponse Response { get; }
    public StreamWriter Writer { get; }
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    public SseClient(HttpListenerResponse response, StreamWriter writer)
    {
        Response = response;
        Writer = writer;
    }
}

public class WebSocketLogQueue
{
    private readonly ConcurrentDictionary<StreamWriter, SseClient> _clients = new();

    public void AddClient(HttpListenerResponse response, StreamWriter writer)
    {
        _clients.TryAdd(writer, new SseClient(response, writer));
    }

    public void RemoveClient(StreamWriter writer)
    {
        _clients.TryRemove(writer, out _);
    }

    public void Enqueue(string level, string category, string message)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        var payload = new
        {
            type = "log",
            time,
            level,
            category,
            message
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Broadcast($"data: {json}\n\n");
    }

    public void BroadcastStatusUpdate()
    {
        var payload = new { type = "status_update" };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Broadcast($"data: {json}\n\n");
    }

    private void Broadcast(string message)
    {
        foreach (var client in _clients.Values)
        {
            _ = Task.Run(async () =>
            {
                await client.Semaphore.WaitAsync();
                try
                {
                    await client.Writer.WriteAsync(message);
                    await client.Writer.FlushAsync();
                }
                catch
                {
                    RemoveClient(client.Writer);
                }
                finally
                {
                    client.Semaphore.Release();
                }
            });
        }
    }
}

public class WebSocketLoggerProvider : ILoggerProvider
{
    private readonly WebSocketLogQueue _queue;

    public WebSocketLoggerProvider(WebSocketLogQueue queue)
    {
        _queue = queue;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new WebSocketLogger(categoryName, _queue);
    }

    public void Dispose() {}
}

public class WebSocketLogger : ILogger
{
    private readonly string _categoryName;
    private readonly WebSocketLogQueue _queue;

    public WebSocketLogger(string categoryName, WebSocketLogQueue queue)
    {
        _categoryName = categoryName;
        _queue = queue;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception != null)
        {
            message += $"\n{exception}";
        }

        // Standardize category name for readability
        var category = _categoryName;
        var dotIdx = category.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx < category.Length - 1)
        {
            category = category.Substring(dotIdx + 1);
        }

        _queue.Enqueue(logLevel.ToString(), category, message);
    }
}
