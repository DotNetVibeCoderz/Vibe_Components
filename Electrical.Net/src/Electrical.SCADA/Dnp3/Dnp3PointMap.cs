namespace Electrical.SCADA.Dnp3;

/// <summary>Binding of a <see cref="TagDatabase"/> tag to a DNP3 point.</summary>
/// <param name="Deadband">Analog event deadband [engineering units of the tag].</param>
public sealed record Dnp3TagBinding(string Tag, Dnp3PointType Type, ushort Index, Dnp3PointClass Class = Dnp3PointClass.Class2, double Deadband = 0);

/// <summary>
/// Tag ↔ DNP3 point map. Outstation side: <see cref="CreateDatabase"/> and <see cref="Attach"/> keep a <see cref="Dnp3Database"/> in
/// step with tag changes (generating events), <see cref="AttachCommands"/> writes executed controls back to output tags. Master side:
/// <see cref="ApplyTo"/> stores polled values in tags.
/// </summary>
public sealed class Dnp3PointMap
{
    private readonly List<Dnp3TagBinding> _bindings = [];

    public IReadOnlyList<Dnp3TagBinding> Bindings => _bindings;

    public Dnp3TagBinding Add(Dnp3TagBinding b)
    {
        if (_bindings.Any(x => x.Type == b.Type && x.Index == b.Index)) throw new ArgumentException($"{b.Type} {b.Index} already mapped.");
        _bindings.Add(b);
        return b;
    }

    public Dnp3TagBinding Add(string tag, Dnp3PointType type, ushort index, Dnp3PointClass eventClass = Dnp3PointClass.Class2, double deadband = 0) =>
        Add(new Dnp3TagBinding(tag, type, index, eventClass, deadband));

    /// <summary>DNP3 flags for a tag quality.</summary>
    public static Dnp3Flags Flags(TagQuality q) => q switch
    {
        TagQuality.Good => Dnp3Flags.Online,
        TagQuality.Uncertain => Dnp3Flags.Online | Dnp3Flags.ReferenceError,
        TagQuality.CommFailure => Dnp3Flags.CommLost,
        TagQuality.NotConnected => Dnp3Flags.Restart,
        _ => Dnp3Flags.None,
    };

    /// <summary>Tag quality for received DNP3 flags.</summary>
    public static TagQuality Quality(Dnp3Flags f) =>
        (f & Dnp3Flags.CommLost) != 0 ? TagQuality.CommFailure
        : (f & Dnp3Flags.Online) == 0 ? TagQuality.Bad
        : (f & (Dnp3Flags.ReferenceError | Dnp3Flags.ChatterOrOverRange | Dnp3Flags.LocalForced | Dnp3Flags.RemoteForced)) != 0 ? TagQuality.Uncertain
        : TagQuality.Good;

    /// <summary>Creates an outstation database with one point per binding, initialised from the tags when given.</summary>
    public Dnp3Database CreateDatabase(TagDatabase? tags = null, int eventBufferSize = 200)
    {
        var db = new Dnp3Database(eventBufferSize);
        foreach (var b in _bindings)
        {
            double v = tags is not null && tags.TryGet(b.Tag, out var t) && !double.IsNaN(t!.Value) ? t.Value : 0;
            switch (b.Type)
            {
                case Dnp3PointType.BinaryInput: db.AddBinaryInput(b.Index, v != 0, b.Class); break;
                case Dnp3PointType.AnalogInput: db.AddAnalogInput(b.Index, v, b.Class, b.Deadband); break;
                case Dnp3PointType.BinaryOutput: db.AddBinaryOutput(b.Index, v != 0); break;
                default: db.AddAnalogOutput(b.Index, v); break;
            }
        }
        return db;
    }

    /// <summary>Forwards tag changes into the database (dispose the result to detach).</summary>
    public IDisposable Attach(TagDatabase tags, Dnp3Database db)
    {
        void OnChanged(TagValueChanged e)
        {
            foreach (var b in _bindings)
            {
                if (!string.Equals(b.Tag, e.Tag, StringComparison.OrdinalIgnoreCase) || !db.Contains(b.Type, b.Index)) continue;
                double v = double.IsNaN(e.Value) ? 0 : e.Value;
                db.Update(b.Type, b.Index, b.Type is Dnp3PointType.BinaryInput or Dnp3PointType.BinaryOutput ? (v != 0 ? 1 : 0) : v, Flags(e.Quality), e.Timestamp);
            }
        }
        tags.Changed += OnChanged;
        return new Unsubscribe(() => tags.Changed -= OnChanged);
    }

    /// <summary>Writes successful outstation controls to the tags bound to the commanded output points.</summary>
    public IDisposable AttachCommands(Dnp3Outstation outstation, TagDatabase tags)
    {
        void OnCommand(Dnp3CommandEvent c)
        {
            if (c.Status != Dnp3CommandStatus.Success) return;
            foreach (var b in _bindings.Where(b => b.Type == c.Type && b.Index == c.Index))
            {
                double v = c.Type == Dnp3PointType.BinaryOutput ? (c.Crob?.TargetState == true ? 1 : 0) : c.AnalogValue ?? 0;
                if (tags.TryGet(b.Tag, out _)) tags.UpdateValue(b.Tag, v);
            }
        }
        outstation.Commanded += OnCommand;
        return new Unsubscribe(() => outstation.Commanded -= OnCommand);
    }

    /// <summary>Master side: stores polled values in the bound tags (static values; events update to their latest value).</summary>
    /// <returns>Number of tags updated.</returns>
    public int ApplyTo(TagDatabase tags, Dnp3MeasurementSet values)
    {
        int n = 0;
        foreach (var b in _bindings)
        {
            var dict = b.Type switch
            {
                Dnp3PointType.BinaryInput => values.BinaryInputs,
                Dnp3PointType.AnalogInput => values.AnalogInputs,
                Dnp3PointType.BinaryOutput => values.BinaryOutputs,
                _ => values.AnalogOutputs,
            };
            if (!dict.TryGetValue(b.Index, out var p) || !tags.TryGet(b.Tag, out _)) continue;
            tags.UpdateValue(b.Tag, p.Value, p.Time, Quality(p.Flags));
            n++;
        }
        return n;
    }

    private sealed class Unsubscribe(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }
}
