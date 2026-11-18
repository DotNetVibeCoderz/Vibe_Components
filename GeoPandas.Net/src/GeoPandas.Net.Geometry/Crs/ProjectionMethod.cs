namespace GeoPandas.Net.Geometry.Crs;

internal enum ParamUnit { Degree, Metre, Unity }

internal sealed record MethodParam(string Name, int Code, double Value, ParamUnit Unit);

/// <summary>EPSG-style description of a projection: method name/code, parameters, and OGC WKT1 parameter names.</summary>
internal sealed record ProjectionMethod(string Name, int? EpsgCode, MethodParam[] Parameters, (string Name, double Value)[] Wkt1Parameters);
