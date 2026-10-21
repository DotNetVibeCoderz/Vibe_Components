using System.Collections.Concurrent;
using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Security;
using Blazor.AI.Core.Embeddings;
using Blazor.AI.Core.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Blazor.AI.Core.Providers;

/// <summary>Default <see cref="IAiClientFactory"/>: lazily builds one guarded client per provider and caches it.</summary>
public sealed class AiClientFactory : IAiClientFactory, IDisposable
{
    private readonly BlazorAIOptions _options;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private readonly Lock _gate = new();
    private string _defaultName;

    public AiClientFactory(IOptions<BlazorAIOptions> options, IServiceProvider services)
    {
        _options = options.Value;
        _services = services;

        foreach (var provider in _options.Providers)
        {
            Add(new Entry(provider.Clone()));
        }

        if (_order.Count == 0)
        {
            Add(new Entry(new AiProviderOptions { Name = "simulated", Kind = AiProviderKind.Simulated, DisplayName = "Simulated (offline)" }));
        }

        _defaultName = _options.DefaultProvider is { } name && _entries.ContainsKey(name) ? name : _order[0];
    }

    public event Action? ProvidersChanged;

    public IReadOnlyList<AiProviderOptions> Providers
    {
        get
        {
            lock (_gate)
            {
                return _order.Select(n => _entries[n].Options).ToArray();
            }
        }
    }

    public AiProviderOptions DefaultProvider => _entries[_defaultName].Options;

    public IChatClient GetChatClient(string? provider = null) => Resolve(provider).Chat.Value;

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string? provider = null) => Resolve(provider).Embeddings.Value;

    public void Register(AiProviderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        Replace(new Entry(options.Clone()));
    }

    public void Register(string name, IChatClient chatClient, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null, AiProviderKind kind = AiProviderKind.Custom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var options = new AiProviderOptions
        {
            Name = name,
            Kind = kind,
            Model = chatClient.GetService<ChatClientMetadata>()?.DefaultModelId,
        };
        Replace(new Entry(options, chatClient, embeddings));
    }

    public void SetDefault(string provider)
    {
        if (!_entries.ContainsKey(provider))
        {
            throw new KeyNotFoundException($"Unknown AI provider '{provider}'.");
        }

        _defaultName = provider;
        ProvidersChanged?.Invoke();
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }
    }

    private Entry Resolve(string? provider)
    {
        var name = string.IsNullOrWhiteSpace(provider) ? _defaultName : provider;
        return _entries.TryGetValue(name, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Unknown AI provider '{name}'. Registered: {string.Join(", ", _order)}.");
    }

    private void Add(Entry entry)
    {
        entry.Bind(this);
        _entries[entry.Options.Name] = entry;
        _order.Add(entry.Options.Name);
    }

    private void Replace(Entry entry)
    {
        entry.Bind(this);
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Options.Name, out var old))
            {
                old.Dispose();
            }
            else
            {
                _order.Add(entry.Options.Name);
            }

            _entries[entry.Options.Name] = entry;
        }

        ProvidersChanged?.Invoke();
    }

    private IChatClient Wrap(AiProviderOptions options, IChatClient raw)
    {
        var guardrails = _options.Guardrails;
        return new GuardrailChatClient(
            raw,
            options.Name,
            guardrails,
            _services.GetService(typeof(IPiiDetector)) as IPiiDetector ?? new RegexPiiDetector(),
            _services.GetService(typeof(IContentModerator)) as IContentModerator ?? new KeywordContentModerator(guardrails),
            _services.GetService(typeof(IAuditTrail)) as IAuditTrail,
            _services.GetService(typeof(IAiAuthorization)) as IAiAuthorization,
            _services.GetService(typeof(IAiUserContext)) as IAiUserContext);
    }

    private sealed class Entry : IDisposable
    {
        private readonly IChatClient? _chat;
        private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;

        public Entry(AiProviderOptions options, IChatClient? chat = null, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
        {
            Options = options;
            _chat = chat;
            _embeddings = embeddings;
        }

        public AiProviderOptions Options { get; }

        public Lazy<IChatClient> Chat { get; private set; } = null!;

        public Lazy<IEmbeddingGenerator<string, Embedding<float>>> Embeddings { get; private set; } = null!;

        public void Bind(AiClientFactory factory)
        {
            Chat = new(() => factory.Wrap(Options, _chat ?? ProviderClientBuilder.CreateChatClient(Options)), LazyThreadSafetyMode.ExecutionAndPublication);
            Embeddings = new(
                () => _embeddings
                    ?? ProviderClientBuilder.CreateEmbeddingGenerator(Options, factory._options.OfflineEmbeddingDimensions)
                    ?? new HashingEmbeddingGenerator(factory._options.OfflineEmbeddingDimensions),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public void Dispose()
        {
            if (Chat?.IsValueCreated == true)
            {
                Chat.Value.Dispose();
            }

            if (Embeddings?.IsValueCreated == true)
            {
                Embeddings.Value.Dispose();
            }
        }
    }
}
