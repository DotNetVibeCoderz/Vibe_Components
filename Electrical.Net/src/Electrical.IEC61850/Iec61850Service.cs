using Electrical.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.IEC61850;

/// <summary>Facade over the IEC 61850 codecs for dependency injection.</summary>
public interface IIec61850Service
{
    SclDocument ParseScl(string xml);
    byte[] EncodeGoose(GooseFrame frame);
    GooseFrame DecodeGoose(byte[] frame);
    byte[] EncodeSampledValues(SvFrame frame);
    SvFrame DecodeSampledValues(byte[] frame);
}

internal sealed class Iec61850Service : IIec61850Service
{
    public SclDocument ParseScl(string xml) => SclParser.Parse(xml);
    public byte[] EncodeGoose(GooseFrame frame) => GooseCodec.Encode(frame);
    public GooseFrame DecodeGoose(byte[] frame) => GooseCodec.Decode(frame);
    public byte[] EncodeSampledValues(SvFrame frame) => SampledValuesCodec.Encode(frame);
    public SvFrame DecodeSampledValues(byte[] frame) => SampledValuesCodec.Decode(frame);
}

public static class Iec61850BuilderExtensions
{
    /// <summary>Registers <see cref="IIec61850Service"/> (SCL parsing, GOOSE and SV codecs).</summary>
    public static ElectricalBuilder AddIec61850(this ElectricalBuilder b)
    {
        if (b.TryAddModule("IEC61850")) b.Services.TryAddSingleton<IIec61850Service, Iec61850Service>();
        return b;
    }
}
