using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Blazor.AI.Agents;

/// <summary>A live connection to an MCP server.</summary>
public sealed class McpConnection : IAsyncDisposable
{
    internal McpConnection(string name, string endpoint, McpClient client)
    {
        Name = name;
        Endpoint = endpoint;
        Client = client;
    }

    public string Name { get; }

    public string Endpoint { get; }

    public McpClient Client { get; }

    public string ServerName => Client.ServerInfo?.Name ?? Name;

    public string? ServerVersion => Client.ServerInfo?.Version;

    public string? Instructions => Client.ServerInstructions;

    public IReadOnlyList<McpClientTool> Tools { get; internal set; } = [];

    public IReadOnlyList<McpClientPrompt> Prompts { get; internal set; } = [];

    public IReadOnlyList<McpClientResource> Resources { get; internal set; } = [];

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

/// <summary>
/// Connects to Model Context Protocol servers over Streamable HTTP/SSE or stdio, lists their tools, prompts and
/// resources, and publishes the tools into the <see cref="ToolRegistry"/> so agents can call them.
/// </summary>
public sealed class McpConnector(ToolRegistry tools) : IAsyncDisposable
{
    private readonly List<McpConnection> _connections = [];

    public IReadOnlyList<McpConnection> Connections => _connections;

    /// <summary>Allow stdio servers (starts a local process). Off by default; enable only on trusted servers.</summary>
    public bool AllowStdio { get; set; }

    public event Action? Changed;

    public async Task<McpConnection> ConnectHttpAsync(string name, Uri endpoint, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var options = new HttpClientTransportOptions { Endpoint = endpoint, Name = name, TransportMode = HttpTransportMode.AutoDetect };
        if (headers is not null)
        {
            options.AdditionalHeaders = headers;
        }

        var client = await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken).ConfigureAwait(false);
        return await AddAsync(new McpConnection(name, endpoint.ToString(), client), cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpConnection> ConnectStdioAsync(string name, string command, IList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!AllowStdio)
        {
            throw new InvalidOperationException("Stdio MCP servers are disabled. Set McpConnector.AllowStdio = true on trusted servers.");
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = name, Command = command, Arguments = arguments });
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await AddAsync(new McpConnection(name, $"{command} {string.Join(' ', arguments)}", client), cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(McpConnection connection, CancellationToken cancellationToken = default)
    {
        var caps = connection.Client.ServerCapabilities;
        connection.Tools = caps?.Tools is null ? [] : [.. await connection.Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)];
        connection.Prompts = caps?.Prompts is null ? [] : [.. await connection.Client.ListPromptsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)];
        connection.Resources = caps?.Resources is null ? [] : [.. await connection.Client.ListResourcesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)];

        tools.RemoveSource(Source(connection));
        foreach (var tool in connection.Tools)
        {
            tools.Register(tool, $"MCP · {connection.ServerName}", Source(connection));
        }

        Changed?.Invoke();
    }

    public async Task DisconnectAsync(McpConnection connection)
    {
        tools.RemoveSource(Source(connection));
        _connections.Remove(connection);
        await connection.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    public static async Task<string> CallToolAsync(McpClientTool tool, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var args = string.IsNullOrWhiteSpace(argumentsJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];
        var result = await tool.CallAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Render(result.Content, result.IsError == true);
    }

    public static async Task<string> ReadResourceAsync(McpClientResource resource, CancellationToken cancellationToken = default)
    {
        var result = await resource.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return string.Join("\n\n", result.Contents.Select(c => c is TextResourceContents text ? text.Text : $"[{c.GetType().Name}]"));
    }

    private static string Render(IList<ContentBlock> content, bool isError)
    {
        var builder = new StringBuilder(isError ? "Error: " : string.Empty);
        foreach (var block in content)
        {
            builder.AppendLine(block is TextContentBlock text ? text.Text : $"[{block.GetType().Name.Replace("ContentBlock", string.Empty, StringComparison.Ordinal)} content]");
        }

        return builder.ToString().Trim();
    }

    private async Task<McpConnection> AddAsync(McpConnection connection, CancellationToken cancellationToken)
    {
        _connections.Add(connection);
        await RefreshAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string Source(McpConnection connection) => "mcp:" + connection.Name;

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }
}
