using Agritech.Core.Geo;

namespace Agritech.Soil;

/// <summary>Laboratory soil test result for the topsoil (0–20 cm) unless stated.</summary>
public sealed record SoilSample
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public GeoCoordinate? Location { get; init; }
    public DateOnly SampledOn { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public double Ph { get; init; } = 6.5;
    /// <summary>Organic carbon (%).</summary>
    public double OrganicCarbonPct { get; init; } = 1.5;
    /// <summary>Total nitrogen (%).</summary>
    public double TotalNitrogenPct { get; init; } = 0.15;
    /// <summary>Available phosphorus (mg/kg, Bray-1 or Olsen).</summary>
    public double AvailablePMgKg { get; init; } = 15;
    /// <summary>Exchangeable potassium (cmol(+)/kg).</summary>
    public double ExchangeableKCmolKg { get; init; } = 0.4;
    /// <summary>Cation exchange capacity (cmol(+)/kg).</summary>
    public double CecCmolKg { get; init; } = 15;
    /// <summary>Electrical conductivity of saturation extract (dS/m).</summary>
    public double EcDsM { get; init; } = 0.5;
    public double SandPct { get; init; } = 40;
    public double SiltPct { get; init; } = 40;
    public double ClayPct { get; init; } = 20;
    /// <summary>Bulk density (g/cm³).</summary>
    public double BulkDensity { get; init; } = 1.3;
    /// <summary>Volumetric moisture (m³/m³), if measured.</summary>
    public double? VolumetricMoisture { get; init; }

    public double OrganicMatterPct => OrganicCarbonPct * 1.724;
    public double CnRatio => TotalNitrogenPct <= 0 ? double.NaN : OrganicCarbonPct / TotalNitrogenPct;
    public SoilTextureClass Texture => SoilTexture.Classify(SandPct, SiltPct, ClayPct);
}
