using System.Globalization;
using System.Text;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Data;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace AquaCulture.IoT.OpcUa;

/// <summary>Shared OPC UA application configuration (PKI under %LOCALAPPDATA%/AquaCulture.Net/opcua).</summary>
internal static class OpcUaApp
{
    public static string DefaultPkiRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AquaCulture.Net", "opcua");

    public static ITelemetryContext Telemetry { get; } = DefaultTelemetry.Create(_ => { });

    public static ApplicationConfiguration Create(ApplicationType type, string name, string pkiRoot, bool autoAccept) => new()
    {
        ApplicationName = name,
        ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:AquaCulture.Net:{name.Replace(' ', '-')}",
        ApplicationType = type,
        ProductUri = "https://github.com/DotNetVibeCoderz/Vibe_Components/tree/main/AquaCulture.Net",
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "own"),
                SubjectName = $"CN={name}, O=Gravicode Studios, DC={System.Net.Dns.GetHostName()}",
            },
            TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "trusted") },
            TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "issuer") },
            RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "rejected") },
            AutoAcceptUntrustedCertificates = autoAccept,
            AddAppCertToTrustedStore = true,
        },
        TransportQuotas = new TransportQuotas { OperationTimeout = 15_000, MaxMessageSize = 4 * 1024 * 1024 },
        ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
    };
}

/// <summary>Connection options for <see cref="OpcUaReader"/>.</summary>
public sealed record OpcUaReaderOptions(string EndpointUrl)
{
    public bool UseSecurity { get; init; }
    public bool AcceptUntrustedCertificates { get; init; } = true;
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public string ApplicationName { get; init; } = "AquaCulture.Net OPC UA client";
    public string? PkiPath { get; init; }
}

/// <summary>
/// <see cref="IOpcUaReader"/> over the OPC Foundation .NET Standard stack: connects to a PLC or SCADA server, reads mapped node
/// values (numeric, boolean as 0/1) and reconnects when the session drops. Use it with <see cref="OpcUaSensorPoller"/>.
/// </summary>
public sealed class OpcUaReader(OpcUaReaderOptions options) : IOpcUaReader, IAsyncDisposable
{
    private Opc.Ua.Client.ISession? _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsConnected => _session is { Connected: true };

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        var config = OpcUaApp.Create(ApplicationType.Client, options.ApplicationName, options.PkiPath ?? Path.Combine(OpcUaApp.DefaultPkiRoot, "client"), options.AcceptUntrustedCertificates);
        await config.ValidateAsync(ApplicationType.Client, ct).ConfigureAwait(false);
        if (options.AcceptUntrustedCertificates)
            config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted || e.Accept;
        var app = new ApplicationInstance(config, OpcUaApp.Telemetry);
        if (options.UseSecurity) await app.CheckApplicationInstanceCertificatesAsync(true, null, ct).ConfigureAwait(false);
        var description = await CoreClientUtils.SelectEndpointAsync(config, options.EndpointUrl, options.UseSecurity, OpcUaApp.Telemetry, ct).ConfigureAwait(false);
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));
        IUserIdentity identity = options.UserName is { } u ? new UserIdentity(u, Encoding.UTF8.GetBytes(options.Password ?? "")) : new UserIdentity();
        _session = await new DefaultSessionFactory(OpcUaApp.Telemetry).CreateAsync(config, endpoint, false, false, options.ApplicationName, 60_000, identity, null, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the values of nodes such as <c>ns=2;s=Ponds/K-01/DissolvedOxygen</c>. Bad or non-numeric values are omitted.</summary>
    public async Task<IReadOnlyDictionary<string, double>> ReadAsync(IEnumerable<string> nodeIds, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
            var ids = nodeIds.ToList();
            var request = new ReadValueIdCollection(ids.Select(id => new ReadValueId { NodeId = NodeId.Parse(id), AttributeId = Attributes.Value }));
            var response = await _session!.ReadAsync(null, 0, TimestampsToReturn.Source, request, ct).ConfigureAwait(false);
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
            {
                var dv = response.Results[i];
                if (StatusCode.IsBad(dv.StatusCode)) continue;
                double? v = dv.WrappedValue.Value switch
                {
                    bool b => b ? 1 : 0,
                    IConvertible c when dv.WrappedValue.Value is not string => Convert.ToDouble(c, CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (v is { } d) result[ids[i]] = d;
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Writes a boolean or numeric value (e.g. an aerator relay). Returns false when the server rejects the write.</summary>
    public async Task<bool> WriteAsync(string nodeId, object value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
            var response = await _session!.WriteAsync(null, [new WriteValue { NodeId = NodeId.Parse(nodeId), AttributeId = Attributes.Value, Value = new DataValue(new Variant(value)) }], ct).ConfigureAwait(false);
            return StatusCode.IsGood(response.Results[0]);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try { await _session.CloseAsync(2000, true).ConfigureAwait(false); } catch (ServiceResultException) { }
            _session.Dispose();
            _session = null;
        }
        _gate.Dispose();
    }
}

/// <summary>
/// OPC UA server simulating the ponds of a farm (for tests, demos and SCADA integration work). Address space
/// <c>Objects/Ponds/{pondId}</c> with variables Temperature, DissolvedOxygen, Ph, Salinity, Ammonia (read-only, updated every
/// <see cref="Interval"/>) and a writable Aerator boolean that raises DO. Node ids: <c>ns=2;s=Ponds/K-01/DissolvedOxygen</c>.
/// </summary>
public sealed class PondOpcUaSimulator : IAsyncDisposable
{
    public const string NamespaceUri = "urn:aquaculture-net:ponds";
    private readonly string[] _ponds;
    private ApplicationInstance? _app;
    private PondServer? _server;

    public PondOpcUaSimulator(IEnumerable<string> pondIds, int port = 4841, string host = "localhost")
    {
        _ponds = pondIds.ToArray();
        Port = port; Host = host;
    }

    public int Port { get; }
    public string Host { get; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    public string? PkiPath { get; init; }
    public string EndpointUrl => $"opc.tcp://{Host}:{Port}/aquaculture/ponds";
    public ushort NamespaceIndex => _server?.Ponds?.NamespaceIndex ?? 2;

    /// <summary>Node id of a pond variable, e.g. <c>NodeId("K-01", "DissolvedOxygen")</c>.</summary>
    public string NodeId(string pondId, string variable) => $"ns={NamespaceIndex};s=Ponds/{pondId}/{variable}";

    /// <summary>Ready-made node map for <see cref="OpcUaSensorPoller"/>.</summary>
    public IReadOnlyList<OpcUaNodeMap> NodeMap() => _ponds.SelectMany(p => new[]
    {
        new OpcUaNodeMap(NodeId(p, "Temperature"), $"opcua-{p}", p, SensorKind.Temperature),
        new OpcUaNodeMap(NodeId(p, "DissolvedOxygen"), $"opcua-{p}", p, SensorKind.DissolvedOxygen),
        new OpcUaNodeMap(NodeId(p, "Ph"), $"opcua-{p}", p, SensorKind.Ph),
        new OpcUaNodeMap(NodeId(p, "Salinity"), $"opcua-{p}", p, SensorKind.Salinity),
        new OpcUaNodeMap(NodeId(p, "Ammonia"), $"opcua-{p}", p, SensorKind.Ammonia),
        new OpcUaNodeMap(NodeId(p, "Aerator"), $"opcua-{p}", p, SensorKind.AeratorCurrent),
    }).ToList();

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_server is not null) return;
        var config = OpcUaApp.Create(ApplicationType.Server, "AquaCulture.Net Pond Simulator", PkiPath ?? Path.Combine(OpcUaApp.DefaultPkiRoot, "server"), true);
        var server = new ServerConfiguration
        {
            BaseAddresses = { EndpointUrl }, MinRequestThreadCount = 2, MaxRequestThreadCount = 20, MaxQueuedRequestCount = 200,
            MinPublishingInterval = 50, MinSubscriptionLifetime = 1000,
        };
        server.SecurityPolicies.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None });
        server.UserTokenPolicies.Add(new UserTokenPolicy(UserTokenType.Anonymous));
        config.ServerConfiguration = server;
        await config.ValidateAsync(ApplicationType.Server, ct).ConfigureAwait(false);
        config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted || e.Accept;
        _app = new ApplicationInstance(config, OpcUaApp.Telemetry);
        await _app.CheckApplicationInstanceCertificatesAsync(true, null, ct).ConfigureAwait(false);
        _server = new PondServer(_ponds, Interval);
        await _app.StartAsync(_server).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync().ConfigureAwait(false);
        _server?.Dispose();
        (_app, _server) = (null, null);
    }

    private sealed class PondServer(string[] ponds, TimeSpan interval) : StandardServer
    {
        public PondNodeManager? Ponds { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            Ponds = new PondNodeManager(server, configuration, ponds, interval);
            return new MasterNodeManager(server, configuration, null, Ponds);
        }

        protected override ServerProperties LoadServerProperties() => new()
        {
            ManufacturerName = "Gravicode Studios", ProductName = "AquaCulture.Net OPC UA Pond Simulator",
            ProductUri = "https://github.com/DotNetVibeCoderz/Vibe_Components/tree/main/AquaCulture.Net",
            SoftwareVersion = Core.AquaInfo.Version, BuildNumber = Core.AquaInfo.Version, BuildDate = DateTime.UtcNow,
        };
    }

    private sealed class PondNodeManager : CustomNodeManager2
    {
        private readonly string[] _ponds;
        private readonly TimeSpan _interval;
        private readonly Dictionary<string, BaseDataVariableState> _vars = new(StringComparer.Ordinal);
        private readonly DeterministicRandom _rng = new(11);
        private Timer? _timer;
        private double _hour = 18;

        public PondNodeManager(IServerInternal server, ApplicationConfiguration configuration, string[] ponds, TimeSpan interval) : base(server, configuration, NamespaceUri)
        {
            _ponds = ponds; _interval = interval;
            SystemContext.NodeIdFactory = this;
        }

        public override NodeId New(ISystemContext context, NodeState node) => node.NodeId;

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs)) externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();
                var root = Folder(null, "Ponds", "Ponds");
                root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
                foreach (var p in _ponds)
                {
                    var f = Folder(root, $"Ponds/{p}", p);
                    Var(f, $"Ponds/{p}/Temperature", "Temperature", DataTypeIds.Double, 29.5, false);
                    Var(f, $"Ponds/{p}/DissolvedOxygen", "DissolvedOxygen", DataTypeIds.Double, 5.5, false);
                    Var(f, $"Ponds/{p}/Ph", "Ph", DataTypeIds.Double, 8.0, false);
                    Var(f, $"Ponds/{p}/Salinity", "Salinity", DataTypeIds.Double, 18.0, false);
                    Var(f, $"Ponds/{p}/Ammonia", "Ammonia", DataTypeIds.Double, 0.6, false);
                    Var(f, $"Ponds/{p}/Aerator", "Aerator", DataTypeIds.Boolean, false, true);
                }
                AddPredefinedNode(SystemContext, root);
            }
            _timer = new Timer(_ => Step(), null, _interval, _interval);
        }

        private FolderState Folder(NodeState? parent, string path, string name)
        {
            var f = new FolderState(parent)
            {
                SymbolicName = name, ReferenceTypeId = ReferenceTypeIds.Organizes, TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(path, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex), DisplayName = name, EventNotifier = EventNotifiers.None,
            };
            parent?.AddChild(f);
            return f;
        }

        private void Var(NodeState parent, string path, string name, NodeId type, object value, bool writable)
        {
            var access = writable ? (byte)(AccessLevels.CurrentRead | AccessLevels.CurrentWrite) : AccessLevels.CurrentRead;
            var v = new BaseDataVariableState(parent)
            {
                SymbolicName = name, ReferenceTypeId = ReferenceTypeIds.Organizes, TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId(path, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex), DisplayName = name,
                DataType = type, ValueRank = ValueRanks.Scalar, AccessLevel = access, UserAccessLevel = access, Value = value,
                StatusCode = StatusCodes.Good, Timestamp = DateTime.UtcNow,
            };
            parent.AddChild(v);
            _vars[path] = v;
        }

        private void Set(string path, double value)
        {
            var v = _vars[path];
            v.Value = Math.Round(value, 3); v.Timestamp = DateTime.UtcNow;
            v.ClearChangeMasks(SystemContext, false);
        }

        /// <summary>Each tick is 10 simulated minutes: diurnal DO and temperature, aerators add oxygen.</summary>
        private void Step()
        {
            lock (Lock)
            {
                _hour = (_hour + 1.0 / 6) % 24;
                double day = Math.Sin(2 * Math.PI * (_hour - 9) / 24);
                for (int i = 0; i < _ponds.Length; i++)
                {
                    string p = _ponds[i];
                    bool aer = _vars[$"Ponds/{p}/Aerator"].Value is true;
                    Set($"Ponds/{p}/Temperature", 29.5 + 1.5 * Math.Sin(2 * Math.PI * (_hour - 10) / 24) + 0.1 * _rng.NextGaussian());
                    Set($"Ponds/{p}/DissolvedOxygen", Math.Max(0.3, 5.2 - 0.3 * i + 2.2 * day + (aer ? 1.3 : 0) + 0.1 * _rng.NextGaussian()));
                    Set($"Ponds/{p}/Ph", 8.0 + 0.3 * day + 0.02 * _rng.NextGaussian());
                    Set($"Ponds/{p}/Salinity", 18 + 0.05 * _rng.NextGaussian());
                    Set($"Ponds/{p}/Ammonia", Math.Max(0, 0.6 + 0.2 * i + 0.03 * _rng.NextGaussian()));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }
    }
}

public static class OpcUaBuilderExtensions
{
    public static AquaBuilder AddOpcUa(this AquaBuilder b) => b.TryAddModule("IoT.OpcUa", _ => { });
}
