using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenSceneGraph.Net.Geo;

namespace OpenSceneGraph.Net.Simulation;

/// <summary>DIS entity identifier (site, application, entity).</summary>
public readonly record struct EntityId(ushort Site, ushort Application, ushort Entity)
{
    public override string ToString() => $"{Site}:{Application}:{Entity}";
}

/// <summary>DIS entity type (kind, domain, country, category, subcategory, specific, extra).</summary>
public readonly record struct EntityType(byte Kind, byte Domain, ushort Country, byte Category, byte Subcategory, byte Specific = 0, byte Extra = 0)
{
    // common SISO-REF-010 values
    public static EntityType Tank(ushort country = 225) => new(1, 1, country, 1, 1);
    public static EntityType FighterAircraft(ushort country = 225) => new(1, 2, country, 1, 1);
    public static EntityType Uav(ushort country = 225) => new(1, 2, country, 50, 1);
    public static EntityType Ship(ushort country = 225) => new(1, 3, country, 1, 1);
    public static EntityType Munition(ushort country = 225) => new(2, 2, country, 1, 1);
}

public enum ForceId : byte { Other = 0, Friendly = 1, Opposing = 2, Neutral = 3 }

/// <summary>Dead reckoning algorithms from IEEE 1278.1 (subset).</summary>
public enum DeadReckoningAlgorithm : byte { Static = 1, Fpw = 2, Rpw = 3, Rvw = 4, Fvw = 5 }

/// <summary>
/// Entity State PDU (IEEE 1278.1-2012, PDU type 1), the core message of distributed simulation. Location is ECEF
/// metres (double), orientation is Euler psi/theta/phi in radians relative to ECEF as defined by the standard.
/// </summary>
public sealed class EntityStatePdu
{
    public const byte PduType = 1;
    public const byte ProtocolVersion = 7;

    public byte ExerciseId { get; set; } = 1;
    public EntityId Id { get; set; }
    public ForceId Force { get; set; } = ForceId.Friendly;
    public EntityType Type { get; set; }
    public Vector3d Velocity { get; set; }
    public Vector3d Location { get; set; }
    public (float Psi, float Theta, float Phi) Orientation { get; set; }
    public uint Appearance { get; set; }
    public DeadReckoningAlgorithm DeadReckoning { get; set; } = DeadReckoningAlgorithm.Fpw;
    public (float X, float Y, float Z) LinearAcceleration { get; set; }
    public string Marking { get; set; } = "";
    public uint Timestamp { get; set; }

    /// <summary>Fixed size with zero articulation parameters.</summary>
    public const int Size = 144;

    public byte[] Encode()
    {
        var b = new byte[Size];
        var s = b.AsSpan();
        // PDU header (12 bytes)
        s[0] = ProtocolVersion; s[1] = ExerciseId; s[2] = PduType; s[3] = 1; // family: entity information
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], Timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], Size);
        // entity id (6)
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], Id.Site);
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], Id.Application);
        BinaryPrimitives.WriteUInt16BigEndian(s[16..], Id.Entity);
        s[18] = (byte)Force;
        s[19] = 0; // articulation parameter count
        WriteType(s[20..], Type);
        WriteType(s[28..], Type); // alternative entity type
        BinaryPrimitives.WriteSingleBigEndian(s[36..], (float)Velocity.X);
        BinaryPrimitives.WriteSingleBigEndian(s[40..], (float)Velocity.Y);
        BinaryPrimitives.WriteSingleBigEndian(s[44..], (float)Velocity.Z);
        BinaryPrimitives.WriteDoubleBigEndian(s[48..], Location.X);
        BinaryPrimitives.WriteDoubleBigEndian(s[56..], Location.Y);
        BinaryPrimitives.WriteDoubleBigEndian(s[64..], Location.Z);
        BinaryPrimitives.WriteSingleBigEndian(s[72..], Orientation.Psi);
        BinaryPrimitives.WriteSingleBigEndian(s[76..], Orientation.Theta);
        BinaryPrimitives.WriteSingleBigEndian(s[80..], Orientation.Phi);
        BinaryPrimitives.WriteUInt32BigEndian(s[84..], Appearance);
        // dead reckoning parameters (40): algorithm, 15 bytes other, linear accel (12), angular velocity (12)
        s[88] = (byte)DeadReckoning;
        BinaryPrimitives.WriteSingleBigEndian(s[104..], LinearAcceleration.X);
        BinaryPrimitives.WriteSingleBigEndian(s[108..], LinearAcceleration.Y);
        BinaryPrimitives.WriteSingleBigEndian(s[112..], LinearAcceleration.Z);
        // marking (12): character set + 11 chars
        s[128] = 1; // ASCII
        var mark = Encoding.ASCII.GetBytes(Marking.Length > 11 ? Marking[..11] : Marking);
        mark.CopyTo(s[129..]);
        BinaryPrimitives.WriteUInt32BigEndian(s[140..], 0); // capabilities
        return b;
    }

    public static EntityStatePdu Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length < Size || s[2] != PduType) throw new InvalidDataException("Not an Entity State PDU.");
        var p = new EntityStatePdu
        {
            ExerciseId = s[1],
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
            Id = new EntityId(BinaryPrimitives.ReadUInt16BigEndian(s[12..]), BinaryPrimitives.ReadUInt16BigEndian(s[14..]), BinaryPrimitives.ReadUInt16BigEndian(s[16..])),
            Force = (ForceId)s[18],
            Type = ReadType(s[20..]),
            Velocity = new Vector3d(BinaryPrimitives.ReadSingleBigEndian(s[36..]), BinaryPrimitives.ReadSingleBigEndian(s[40..]), BinaryPrimitives.ReadSingleBigEndian(s[44..])),
            Location = new Vector3d(BinaryPrimitives.ReadDoubleBigEndian(s[48..]), BinaryPrimitives.ReadDoubleBigEndian(s[56..]), BinaryPrimitives.ReadDoubleBigEndian(s[64..])),
            Orientation = (BinaryPrimitives.ReadSingleBigEndian(s[72..]), BinaryPrimitives.ReadSingleBigEndian(s[76..]), BinaryPrimitives.ReadSingleBigEndian(s[80..])),
            Appearance = BinaryPrimitives.ReadUInt32BigEndian(s[84..]),
            DeadReckoning = (DeadReckoningAlgorithm)s[88],
            LinearAcceleration = (BinaryPrimitives.ReadSingleBigEndian(s[104..]), BinaryPrimitives.ReadSingleBigEndian(s[108..]), BinaryPrimitives.ReadSingleBigEndian(s[112..])),
            Marking = Encoding.ASCII.GetString(s.Slice(129, 11)).TrimEnd('\0'),
        };
        return p;
    }

    private static void WriteType(Span<byte> s, EntityType t)
    {
        s[0] = t.Kind; s[1] = t.Domain;
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], t.Country);
        s[4] = t.Category; s[5] = t.Subcategory; s[6] = t.Specific; s[7] = t.Extra;
    }

    private static EntityType ReadType(ReadOnlySpan<byte> s) => new(s[0], s[1], BinaryPrimitives.ReadUInt16BigEndian(s[2..]), s[4], s[5], s[6], s[7]);

    /// <summary>Extrapolates location with the PDU's dead reckoning algorithm (DRM F/R P/V W).</summary>
    public Vector3d Extrapolate(double seconds) => DeadReckoning switch
    {
        DeadReckoningAlgorithm.Static => Location,
        DeadReckoningAlgorithm.Rvw or DeadReckoningAlgorithm.Fvw =>
            Location + Velocity * seconds + new Vector3d(LinearAcceleration.X, LinearAcceleration.Y, LinearAcceleration.Z) * (0.5 * seconds * seconds),
        _ => Location + Velocity * seconds,
    };

    /// <summary>
    /// DIS timestamp: units of 3600/2^31 seconds past the hour, low bit set for absolute time.
    /// </summary>
    public static uint MakeTimestamp(DateTime utc, bool absolute = true)
    {
        double secondsPastHour = utc.Minute * 60 + utc.Second + utc.Millisecond / 1000.0;
        uint units = (uint)(secondsPastHour / 3600.0 * (1u << 31));
        return (units << 1) | (absolute ? 1u : 0u);
    }
}

/// <summary>
/// UDP transport for DIS PDUs (broadcast or multicast, default port 3000). Received Entity State PDUs are exposed
/// through <see cref="EntityUpdated"/>; other PDU types are counted and ignored.
/// </summary>
public sealed class DisNetwork : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receive;

    public DisNetwork(int port = 3000, IPAddress? target = null, bool listen = true)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.EnableBroadcast = true;
        _target = new IPEndPoint(target ?? IPAddress.Broadcast, port);
        if (listen)
        {
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            if (target is not null && IsMulticast(target)) _udp.JoinMulticastGroup(target);
            _receive = Task.Run(ReceiveLoop);
        }
    }

    public event Action<EntityStatePdu, IPEndPoint>? EntityUpdated;
    public long Sent { get; private set; }
    public long Received { get; private set; }
    public long Ignored { get; private set; }

    private static bool IsMulticast(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork && (a.GetAddressBytes()[0] & 0xF0) == 0xE0;

    public async ValueTask SendAsync(EntityStatePdu pdu, CancellationToken ct = default)
    {
        pdu.Timestamp = EntityStatePdu.MakeTimestamp(DateTime.UtcNow);
        await _udp.SendAsync(pdu.Encode(), _target, ct);
        Sent++;
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                if (r.Buffer.Length >= EntityStatePdu.Size && r.Buffer[2] == EntityStatePdu.PduType)
                {
                    Received++;
                    EntityUpdated?.Invoke(EntityStatePdu.Decode(r.Buffer), r.RemoteEndPoint);
                }
                else Ignored++;
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; }
            catch (InvalidDataException) { Ignored++; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        try { _receive?.Wait(500); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
