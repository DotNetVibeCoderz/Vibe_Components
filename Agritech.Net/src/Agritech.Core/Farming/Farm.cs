using Agritech.Core.Geo;

namespace Agritech.Core.Farming;

/// <summary>A farm / estate composed of fields, owned by a producer.</summary>
public sealed class Farm
{
    private readonly List<Field> _fields = [];

    public Farm(string name, GeoCoordinate location, string? owner = null)
    {
        Name = name; Location = location; Owner = owner;
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; }
    public string? Owner { get; set; }
    public GeoCoordinate Location { get; set; }
    public string TimeZone { get; set; } = "Asia/Jakarta";
    public IReadOnlyList<Field> Fields => _fields;
    public double TotalAreaHa => _fields.Sum(f => f.AreaHa);

    public Field AddField(string name, GeoPolygon boundary, string? cropId = null)
    {
        var f = new Field(name, boundary) { CropId = cropId, FarmId = Id };
        _fields.Add(f);
        return f;
    }

    public Field? FindField(string name) => _fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A managed land parcel.</summary>
public sealed class Field(string name, GeoPolygon boundary)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid FarmId { get; init; }
    public string Name { get; set; } = name;
    public GeoPolygon Boundary { get; set; } = boundary;
    public double AreaHa => Boundary.AreaHectares;
    public string? CropId { get; set; }
    public DateOnly? PlantingDate { get; set; }
    public string? SoilSampleId { get; set; }
    public IrrigationType Irrigation { get; set; } = IrrigationType.Rainfed;
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum IrrigationType { Rainfed, Flood, Furrow, Sprinkler, Drip, Pivot }

/// <summary>A cropping season on a field.</summary>
public sealed record Season(Guid FieldId, string CropId, DateOnly PlantingDate, DateOnly? HarvestDate = null, double? YieldTonPerHa = null);
