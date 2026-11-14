using System.Globalization;
using System.Xml;

namespace Electrical.CIM;

/// <summary>CGMES profiles (IEC 61970-600) an attribute or object belongs to.</summary>
public enum CimProfile
{
    /// <summary>Equipment (static network data).</summary>
    Equipment,
    /// <summary>Topology (TopologicalNode assignment).</summary>
    Topology,
    /// <summary>Steady-state hypothesis (set-points, p/q injections, switch and tap positions).</summary>
    SteadyStateHypothesis,
    /// <summary>State variables (solved voltages).</summary>
    StateVariables,
}

/// <summary>XML namespaces used by the CIM writer/reader.</summary>
public static class CimNamespaces
{
    /// <summary>CIM 16 (CGMES 2.4.15) namespace.</summary>
    public const string Cim = "http://iec.ch/TC57/2013/CIM-schema-cim16#";
    /// <summary>RDF syntax namespace.</summary>
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    /// <summary>Model header namespace (IEC 61970-552).</summary>
    public const string Md = "http://iec.ch/TC57/61970-552/ModelDescription/1#";
    /// <summary>Electrical.Net extension namespace (bus ids, base MVA, details CIM has no standard attribute for).</summary>
    public const string Eln = "http://gravicode.studio/electrical-net/cim-ext#";

    internal static string For(string prefix) => prefix switch
    {
        "cim" => Cim,
        "rdf" => Rdf,
        "md" => Md,
        "eln" => Eln,
        _ => throw new ArgumentException($"Unknown prefix '{prefix}'."),
    };
}

internal enum CimValueKind { Literal, Reference, Enum }

internal sealed record CimProperty(CimProfile Profile, string Name, string Value, CimValueKind Kind);

/// <summary>An RDF object being serialized (class, rdf:ID, properties tagged with their profile).</summary>
internal sealed class CimObject(string cls, string id, CimProfile home)
{
    public string Class { get; } = cls;
    public string Id { get; } = id;
    public CimProfile Home { get; } = home;
    public List<CimProperty> Properties { get; } = [];

    public CimObject Lit(string name, string value, CimProfile? p = null) { Properties.Add(new(p ?? Home, name, value, CimValueKind.Literal)); return this; }
    public CimObject Lit(string name, double value, CimProfile? p = null) => Lit(name, value.ToString("R", CultureInfo.InvariantCulture), p);
    public CimObject Lit(string name, int value, CimProfile? p = null) => Lit(name, value.ToString(CultureInfo.InvariantCulture), p);
    public CimObject Lit(string name, bool value, CimProfile? p = null) => Lit(name, value ? "true" : "false", p);
    public CimObject Ref(string name, string id, CimProfile? p = null) { Properties.Add(new(p ?? Home, name, id, CimValueKind.Reference)); return this; }
    public CimObject Enum(string name, string value, CimProfile? p = null) { Properties.Add(new(p ?? Home, name, value, CimValueKind.Enum)); return this; }
}

internal static class CimXmlWriter
{
    public static void Write(XmlWriter w, IEnumerable<CimObject> objects, Func<CimProfile, bool> include, IReadOnlyList<CimObject> header, bool aboutForForeign)
    {
        w.WriteStartDocument();
        w.WriteStartElement("rdf", "RDF", CimNamespaces.Rdf);
        w.WriteAttributeString("xmlns", "cim", null, CimNamespaces.Cim);
        w.WriteAttributeString("xmlns", "md", null, CimNamespaces.Md);
        w.WriteAttributeString("xmlns", "eln", null, CimNamespaces.Eln);
        foreach (var h in header) WriteObject(w, h, h.Properties, about: false, urn: true);
        foreach (var o in objects)
        {
            var props = o.Properties.Where(p => include(p.Profile)).ToList();
            bool home = include(o.Home);
            if (!home && props.Count == 0) continue;
            WriteObject(w, o, props, about: !home && aboutForForeign, urn: false);
        }
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WriteObject(XmlWriter w, CimObject o, List<CimProperty> props, bool about, bool urn)
    {
        var (prefix, local) = Split(o.Class);
        w.WriteStartElement(prefix, local, CimNamespaces.For(prefix));
        if (urn) w.WriteAttributeString("rdf", "about", CimNamespaces.Rdf, o.Id);
        else if (about) w.WriteAttributeString("rdf", "about", CimNamespaces.Rdf, "#" + o.Id);
        else w.WriteAttributeString("rdf", "ID", CimNamespaces.Rdf, o.Id);
        foreach (var p in props)
        {
            var (pp, pl) = Split(p.Name);
            w.WriteStartElement(pp, pl, CimNamespaces.For(pp));
            switch (p.Kind)
            {
                case CimValueKind.Literal: w.WriteString(p.Value); break;
                case CimValueKind.Reference: w.WriteAttributeString("rdf", "resource", CimNamespaces.Rdf, "#" + p.Value); break;
                case CimValueKind.Enum: w.WriteAttributeString("rdf", "resource", CimNamespaces.Rdf, CimNamespaces.Cim + p.Value); break;
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static (string Prefix, string Local) Split(string qname)
    {
        int i = qname.IndexOf(':');
        return i < 0 ? ("cim", qname) : (qname[..i], qname[(i + 1)..]);
    }
}

internal sealed class Utf8StringWriter : StringWriter
{
    public Utf8StringWriter() : base(CultureInfo.InvariantCulture) { }
    public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
}
