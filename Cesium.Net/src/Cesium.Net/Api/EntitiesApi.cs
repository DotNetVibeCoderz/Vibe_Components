using CesiumNet.Interop;

namespace CesiumNet;

/// <summary>Entity management. Ids are strings; when omitted they are generated.</summary>
public sealed class EntitiesApi
{
    private readonly CesiumController _c;
    internal EntitiesApi(CesiumController controller) => _c = controller;

    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Adds an entity and returns its id.</summary>
    public ValueTask<string> AddAsync(EntityOptions entity, CancellationToken cancellationToken = default)
        => _c.CallForIdAsync("entities.add", JsArgs.Rent().Obj(entity, J.EntityOptions).ToJson(), cancellationToken);

    /// <summary>
    /// Adds many entities in a single interop call with entity events suspended —
    /// much faster than calling <see cref="AddAsync"/> in a loop. Returns the ids.
    /// </summary>
    public async ValueTask<string[]> AddRangeAsync(IReadOnlyList<EntityOptions> entities, CancellationToken cancellationToken = default)
        => await _c.CallAsync("entities.addMany", JsArgs.Rent().Obj(entities, J.IReadOnlyListEntityOptions).ToJson(), J.StringArray, cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Replaces graphics/properties of an existing entity (only non-null properties are applied).</summary>
    public ValueTask UpdateAsync(string id, EntityOptions changes)
        => _c.CallVoidAsync("entities.update", JsArgs.Rent().Str(id).Obj(changes, J.EntityOptions).ToJson());

    /// <summary>
    /// Moves many entities in one call — the hot path for real-time tracking (AIS, ADS-B, UAV, IoT).
    /// Combine with <see cref="Realtime.RealtimeEntityFeed"/> to batch high-frequency updates.
    /// </summary>
    public ValueTask UpdatePositionsAsync(IReadOnlyList<EntityPositionUpdate> updates)
        => updates.Count == 0 ? ValueTask.CompletedTask
            : _c.CallVoidAsync("entities.setPositions", JsArgs.Rent().Obj(updates, J.IReadOnlyListEntityPositionUpdate).ToJson());

    /// <summary>Appends time-tagged samples to an entity created with <see cref="EntityOptions.SampledPosition"/>.</summary>
    public ValueTask AddSamplesAsync(string id, SampledPositions samples)
        => _c.CallVoidAsync("entities.addSamples", JsArgs.Rent().Str(id).Obj(samples.Times, J.IReadOnlyListDateTimeOffset).Obj(samples.Positions, J.IReadOnlyListGeoPoint).ToJson());

    /// <summary>Shows or hides an entity.</summary>
    public ValueTask SetVisibleAsync(string id, bool visible)
        => _c.CallVoidAsync("entities.show", JsArgs.Rent().Str(id).Bool(visible).ToJson());

    /// <summary>Removes an entity. Returns false if it did not exist.</summary>
    public async ValueTask<bool> RemoveAsync(string id)
        => await _c.CallAsync("entities.remove", JsArgs.Rent().Str(id).ToJson(), J.Boolean).ConfigureAwait(false);

    /// <summary>Removes all entities created through the entity collection.</summary>
    public ValueTask ClearAsync() => _c.CallVoidAsync("entities.clear", JsArgs.Empty);

    /// <summary>Selects an entity (shows the info box and selection indicator); null clears.</summary>
    public ValueTask SelectAsync(string? id) => _c.CallVoidAsync("entities.select", JsArgs.Rent().Str(id).ToJson());

    /// <summary>Gets an entity's position at the current clock time.</summary>
    public ValueTask<GeoPoint?> GetPositionAsync(string id)
        => _c.CallAsync("entities.getPosition", JsArgs.Rent().Str(id).ToJson(), J.NullableGeoPoint);

    /// <summary>Returns all entity ids.</summary>
    public async ValueTask<string[]> GetIdsAsync()
        => await _c.CallAsync("entities.ids", JsArgs.Empty, J.StringArray).ConfigureAwait(false) ?? [];
}
