/*!
 * Cesium.Net bridge — connects .NET (Blazor / Avalonia) to CesiumJS.
 * Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. MIT License.
 *
 * Protocol: dispatch(viewerId, method, argsJson) -> Promise<string|null>
 *   argsJson is a JSON array of arguments; the result is JSON (or null).
 * Events: sink(name, dataJson) is called for clicks, camera moves, drawing results, errors…
 */
(function (global) {
  'use strict';

  const DEFAULT_BASE_URL = 'https://cdn.jsdelivr.net/npm/cesium@1.146.0/Build/Cesium/';
  const instances = new Map();
  let nextViewerId = 1;
  let nextObjectId = 1;
  let cesiumLoading = null;

  // ---------------------------------------------------------------- loading

  function loadCesium(baseUrl) {
    if (global.Cesium) return Promise.resolve(global.Cesium);
    if (cesiumLoading) return cesiumLoading;
    baseUrl = baseUrl || DEFAULT_BASE_URL;
    if (!baseUrl.endsWith('/')) baseUrl += '/';
    global.CESIUM_BASE_URL = baseUrl;
    cesiumLoading = new Promise(function (resolve, reject) {
      const css = document.createElement('link');
      css.rel = 'stylesheet';
      css.href = baseUrl + 'Widgets/widgets.css';
      document.head.appendChild(css);
      const s = document.createElement('script');
      s.src = baseUrl + 'Cesium.js';
      s.async = true;
      s.onload = function () { resolve(global.Cesium); };
      s.onerror = function () { cesiumLoading = null; reject(new Error('Failed to load CesiumJS from ' + baseUrl)); };
      document.head.appendChild(s);
    });
    return cesiumLoading;
  }

  // ---------------------------------------------------------------- helpers

  const lc = (v) => (v == null ? v : String(v).toLowerCase());
  const has = (v) => v !== undefined && v !== null;
  const rad = (d) => (d || 0) * Math.PI / 180;
  const deg = (r) => r * 180 / Math.PI;
  const newId = (prefix) => prefix + '-' + (nextObjectId++);
  const pos = (p) => Cesium.Cartesian3.fromDegrees(p[0], p[1], p[2] || 0);
  function posArr(list) {
    const flat = new Array(list.length * 3);
    for (let i = 0; i < list.length; i++) {
      const p = list[i];
      flat[i * 3] = p[0]; flat[i * 3 + 1] = p[1]; flat[i * 3 + 2] = p[2] || 0;
    }
    return Cesium.Cartesian3.fromDegreesArrayHeights(flat);
  }
  function toGeo(cart) {
    if (!cart) return null;
    const c = Cesium.Cartographic.fromCartesian(cart);
    if (!c) return null;
    return [deg(c.longitude), deg(c.latitude), c.height];
  }
  const color = (s) => (s ? Cesium.Color.fromCssColorString(s) : undefined);
  const jd = (s) => Cesium.JulianDate.fromIso8601(s);
  const iso = (j) => Cesium.JulianDate.toIso8601(j);
  const c2 = (v) => (v ? new Cesium.Cartesian2(v.x, v.y) : undefined);
  const c3 = (v) => (v ? new Cesium.Cartesian3(v.x, v.y, v.z) : undefined);
  const nfs = (v) => (v ? new Cesium.NearFarScalar(v.near, v.nearValue, v.far, v.farValue) : undefined);
  const ddc = (v) => (v ? new Cesium.DistanceDisplayCondition(v.near, v.far) : undefined);
  const rect = (r) => (r ? Cesium.Rectangle.fromDegrees(r.west, r.south, r.east, r.north) : undefined);
  const upperSnake = (s) => String(s).replace(/([a-z0-9])([A-Z])/g, '$1_$2').toUpperCase();

  function heightRef(v) {
    switch (lc(v)) {
      case 'clamptoground': return Cesium.HeightReference.CLAMP_TO_GROUND;
      case 'relativetoground': return Cesium.HeightReference.RELATIVE_TO_GROUND;
      case 'none': return Cesium.HeightReference.NONE;
      default: return undefined;
    }
  }
  function vOrigin(v) {
    switch (lc(v)) {
      case 'center': return Cesium.VerticalOrigin.CENTER;
      case 'bottom': return Cesium.VerticalOrigin.BOTTOM;
      case 'baseline': return Cesium.VerticalOrigin.BASELINE;
      case 'top': return Cesium.VerticalOrigin.TOP;
      default: return undefined;
    }
  }
  function hOrigin(v) {
    switch (lc(v)) {
      case 'center': return Cesium.HorizontalOrigin.CENTER;
      case 'left': return Cesium.HorizontalOrigin.LEFT;
      case 'right': return Cesium.HorizontalOrigin.RIGHT;
      default: return undefined;
    }
  }
  function labelStyle(v) {
    switch (lc(v)) {
      case 'fill': return Cesium.LabelStyle.FILL;
      case 'outline': return Cesium.LabelStyle.OUTLINE;
      case 'fillandoutline': return Cesium.LabelStyle.FILL_AND_OUTLINE;
      default: return undefined;
    }
  }
  function shadowMode(v) {
    switch (lc(v)) {
      case 'disabled': return Cesium.ShadowMode.DISABLED;
      case 'enabled': return Cesium.ShadowMode.ENABLED;
      case 'castonly': return Cesium.ShadowMode.CAST_ONLY;
      case 'receiveonly': return Cesium.ShadowMode.RECEIVE_ONLY;
      default: return undefined;
    }
  }
  function splitDir(v) {
    switch (lc(v)) {
      case 'left': return Cesium.SplitDirection.LEFT;
      case 'right': return Cesium.SplitDirection.RIGHT;
      case 'none': return Cesium.SplitDirection.NONE;
      default: return undefined;
    }
  }
  // Compass heading (deg, clockwise from north) -> Cesium heading (model +X forward).
  function orientationFor(position, h, p, r) {
    return Cesium.Transforms.headingPitchRollQuaternion(position,
      new Cesium.HeadingPitchRoll(rad((h || 0) - 90), rad(p || 0), rad(r || 0)));
  }
  function clean(o) {
    for (const k in o) if (o[k] === undefined) delete o[k];
    return o;
  }
  function safeJson(v) {
    if (v === undefined) return null;
    try { return JSON.stringify(v); } catch (e) { return JSON.stringify(String(v)); }
  }

  function material(m) {
    if (!m) return undefined;
    const c = color(m.color) || Cesium.Color.WHITE;
    const c2nd = color(m.secondaryColor);
    switch (lc(m.kind)) {
      case 'polylineglow':
        return new Cesium.PolylineGlowMaterialProperty({ color: c, glowPower: m.glowPower ?? 0.25, taperPower: m.taperPower ?? 1 });
      case 'polylinedash':
        return new Cesium.PolylineDashMaterialProperty({ color: c, gapColor: c2nd || Cesium.Color.TRANSPARENT, dashLength: m.dashLength ?? 16 });
      case 'polylinearrow':
        return new Cesium.PolylineArrowMaterialProperty(c);
      case 'polylineoutline':
        return new Cesium.PolylineOutlineMaterialProperty({ color: c, outlineColor: c2nd || Cesium.Color.BLACK, outlineWidth: m.outlineWidth ?? 2 });
      case 'stripe':
        return new Cesium.StripeMaterialProperty({
          evenColor: c, oddColor: c2nd || Cesium.Color.BLACK, repeat: m.repeat ?? 8,
          orientation: m.horizontal === false ? Cesium.StripeOrientation.VERTICAL : Cesium.StripeOrientation.HORIZONTAL
        });
      case 'grid':
        return new Cesium.GridMaterialProperty({
          color: c, cellAlpha: m.cellAlpha ?? 0.1,
          lineCount: new Cesium.Cartesian2(m.lineCount ?? 8, m.lineCount ?? 8),
          lineThickness: new Cesium.Cartesian2(m.lineThickness ?? 1, m.lineThickness ?? 1)
        });
      case 'checkerboard':
        return new Cesium.CheckerboardMaterialProperty({
          evenColor: c, oddColor: c2nd || Cesium.Color.BLACK,
          repeat: new Cesium.Cartesian2(m.repeat ?? 4, m.repeatY ?? m.repeat ?? 4)
        });
      case 'image':
        return new Cesium.ImageMaterialProperty({
          image: m.image, color: color(m.color) || Cesium.Color.WHITE, transparent: m.transparent ?? true,
          repeat: new Cesium.Cartesian2(m.repeat ?? 1, m.repeatY ?? m.repeat ?? 1)
        });
      default:
        return new Cesium.ColorMaterialProperty(c);
    }
  }

  function requestRender(inst) {
    if (inst && inst.viewer && !inst.viewer.isDestroyed()) inst.viewer.scene.requestRender();
  }

  // Continuous rendering helper for animations while requestRenderMode is on.
  function setAnimating(inst, key, on) {
    if (on) inst.animating.add(key); else inst.animating.delete(key);
    if (inst.animating.size > 0 && !inst.renderLoop) {
      const loop = function () {
        if (inst.animating.size === 0 || inst.viewer.isDestroyed()) { inst.renderLoop = 0; return; }
        inst.viewer.scene.requestRender();
        inst.renderLoop = requestAnimationFrame(loop);
      };
      inst.renderLoop = requestAnimationFrame(loop);
    }
  }

  // ---------------------------------------------------------------- entities

  function sampled(s) {
    const p = new Cesium.SampledPositionProperty();
    const times = s.times.map(jd);
    const values = posArr(s.positions);
    p.addSamples(times, values);
    const algo = lc(s.interpolation);
    if (algo === 'lagrange' || algo === 'hermite') {
      p.setInterpolationOptions({
        interpolationDegree: s.interpolationDegree || (algo === 'lagrange' ? 5 : 2),
        interpolationAlgorithm: algo === 'lagrange' ? Cesium.LagrangePolynomialApproximation : Cesium.HermitePolynomialApproximation
      });
    }
    if (s.extrapolate) {
      p.forwardExtrapolationType = Cesium.ExtrapolationType.HOLD;
      p.backwardExtrapolationType = Cesium.ExtrapolationType.HOLD;
    }
    return p;
  }

  async function billboardImage(inst, g) {
    if (g.image) return g.image;
    const key = (g.pinColor || '#1F4FE0') + '|' + (g.pinText || '') + '|' + (g.pinIcon || '') + '|' + (g.pinSize || 48);
    const cached = inst.pinCache.get(key);
    if (cached) return cached;
    const pb = inst.pinBuilder || (inst.pinBuilder = new Cesium.PinBuilder());
    const col = color(g.pinColor) || Cesium.Color.fromCssColorString('#1F4FE0');
    const size = g.pinSize || 48;
    let canvas;
    if (g.pinIcon) canvas = await pb.fromMakiIconId(g.pinIcon, col, size);
    else if (g.pinText) canvas = pb.fromText(g.pinText, col, size);
    else canvas = pb.fromColor(col, size);
    const url = canvas.toDataURL();
    inst.pinCache.set(key, url);
    return url;
  }

  const ALWAYS_ON_TOP = Number.POSITIVE_INFINITY;

  async function buildEntity(inst, o) {
    const e = {};
    if (o.id) e.id = o.id;
    if (has(o.name)) e.name = o.name;
    if (has(o.description)) e.description = o.description;
    if (has(o.show)) e.show = o.show;
    if (o.sampledPosition) e.position = sampled(o.sampledPosition);
    else if (o.position) e.position = new Cesium.ConstantPositionProperty(pos(o.position));
    if (o.orientToVelocity && e.position) e.orientation = new Cesium.VelocityOrientationProperty(e.position);
    else if (o.orientation && o.position) e.orientation = orientationFor(pos(o.position), o.orientation.heading, o.orientation.pitch, o.orientation.roll);
    if (o.availability) {
      e.availability = new Cesium.TimeIntervalCollection([
        new Cesium.TimeInterval({ start: jd(o.availability.start), stop: jd(o.availability.stop) })]);
    }
    if (o.parentId) e.parent = inst.viewer.entities.getById(o.parentId);
    if (o.properties) e.properties = o.properties;

    const g = o;
    if (g.point) {
      const p = g.point;
      e.point = clean({
        pixelSize: p.pixelSize ?? 10, color: color(p.color), outlineColor: color(p.outlineColor), outlineWidth: p.outlineWidth,
        heightReference: heightRef(p.heightReference), scaleByDistance: nfs(p.scaleByDistance),
        distanceDisplayCondition: ddc(p.distanceDisplayCondition), disableDepthTestDistance: p.alwaysOnTop ? ALWAYS_ON_TOP : undefined
      });
    }
    if (g.billboard) {
      const b = g.billboard;
      const isPin = !b.image;
      e.billboard = clean({
        image: await billboardImage(inst, b), scale: b.scale, width: b.width, height: b.height, color: color(b.color),
        rotation: has(b.rotation) ? -rad(b.rotation) : undefined, alignedAxis: has(b.rotation) ? Cesium.Cartesian3.UNIT_Z : undefined,
        verticalOrigin: vOrigin(b.verticalOrigin) ?? (isPin ? Cesium.VerticalOrigin.BOTTOM : undefined),
        horizontalOrigin: hOrigin(b.horizontalOrigin), pixelOffset: c2(b.pixelOffset),
        heightReference: heightRef(b.heightReference), scaleByDistance: nfs(b.scaleByDistance),
        distanceDisplayCondition: ddc(b.distanceDisplayCondition), disableDepthTestDistance: b.alwaysOnTop ? ALWAYS_ON_TOP : undefined
      });
    }
    if (g.label) {
      const l = g.label;
      e.label = clean({
        text: l.text, font: l.font || '600 14px "IBM Plex Sans", "Segoe UI", sans-serif',
        fillColor: color(l.fillColor) || Cesium.Color.WHITE, outlineColor: color(l.outlineColor) || Cesium.Color.BLACK,
        outlineWidth: l.outlineWidth ?? 3, style: labelStyle(l.style) ?? Cesium.LabelStyle.FILL_AND_OUTLINE,
        showBackground: l.showBackground, backgroundColor: color(l.backgroundColor), scale: l.scale,
        pixelOffset: c2(l.pixelOffset), verticalOrigin: vOrigin(l.verticalOrigin), horizontalOrigin: hOrigin(l.horizontalOrigin),
        heightReference: heightRef(l.heightReference), scaleByDistance: nfs(l.scaleByDistance),
        distanceDisplayCondition: ddc(l.distanceDisplayCondition), disableDepthTestDistance: l.alwaysOnTop ? ALWAYS_ON_TOP : undefined
      });
    }
    if (g.polyline) {
      const l = g.polyline;
      e.polyline = clean({
        positions: posArr(l.positions), width: l.width ?? 3, material: material(l.material),
        depthFailMaterial: material(l.depthFailMaterial), clampToGround: l.clampToGround,
        arcType: l.geodesic === false ? Cesium.ArcType.NONE : undefined, distanceDisplayCondition: ddc(l.distanceDisplayCondition)
      });
    }
    if (g.polygon) {
      const p = g.polygon;
      const holes = (p.holes || []).map((h) => new Cesium.PolygonHierarchy(posArr(h)));
      e.polygon = clean({
        hierarchy: new Cesium.PolygonHierarchy(posArr(p.positions), holes), material: material(p.material),
        fill: p.fill, outline: p.outline, outlineColor: color(p.outlineColor), height: p.height,
        extrudedHeight: p.extrudedHeight, perPositionHeight: p.perPositionHeight,
        heightReference: heightRef(p.heightReference), extrudedHeightReference: heightRef(p.extrudedHeightReference),
        shadows: shadowMode(p.shadows), classificationType: p.classifyGround ? Cesium.ClassificationType.BOTH : undefined
      });
    }
    if (g.ellipse) {
      const p = g.ellipse;
      e.ellipse = clean({
        semiMajorAxis: p.semiMajorAxis, semiMinorAxis: p.semiMinorAxis, rotation: has(p.rotation) ? -rad(p.rotation) : undefined,
        material: material(p.material), fill: p.fill, outline: p.outline, outlineColor: color(p.outlineColor),
        height: p.height, extrudedHeight: p.extrudedHeight, heightReference: heightRef(p.heightReference)
      });
    }
    if (g.rectangle) {
      const p = g.rectangle;
      e.rectangle = clean({
        coordinates: rect(p.coordinates), material: material(p.material), fill: p.fill, outline: p.outline,
        outlineColor: color(p.outlineColor), height: p.height, extrudedHeight: p.extrudedHeight
      });
    }
    if (g.box) {
      const p = g.box;
      e.box = clean({
        dimensions: c3(p.dimensions), material: material(p.material), fill: p.fill, outline: p.outline,
        outlineColor: color(p.outlineColor), heightReference: heightRef(p.heightReference), shadows: shadowMode(p.shadows)
      });
    }
    if (g.cylinder) {
      const p = g.cylinder;
      e.cylinder = clean({
        length: p.length, topRadius: p.topRadius, bottomRadius: p.bottomRadius, material: material(p.material),
        fill: p.fill, outline: p.outline, outlineColor: color(p.outlineColor), slices: p.slices,
        heightReference: heightRef(p.heightReference), shadows: shadowMode(p.shadows)
      });
    }
    if (g.ellipsoid) {
      const p = g.ellipsoid;
      e.ellipsoid = clean({
        radii: c3(p.radii), innerRadii: c3(p.innerRadii),
        minimumClock: has(p.minimumClock) ? rad(p.minimumClock) : undefined, maximumClock: has(p.maximumClock) ? rad(p.maximumClock) : undefined,
        minimumCone: has(p.minimumCone) ? rad(p.minimumCone) : undefined, maximumCone: has(p.maximumCone) ? rad(p.maximumCone) : undefined,
        material: material(p.material), fill: p.fill, outline: p.outline, outlineColor: color(p.outlineColor),
        stackPartitions: p.stackPartitions, slicePartitions: p.slicePartitions, heightReference: heightRef(p.heightReference)
      });
    }
    if (g.corridor) {
      const p = g.corridor;
      e.corridor = clean({
        positions: posArr(p.positions), width: p.width, material: material(p.material), height: p.height,
        extrudedHeight: p.extrudedHeight, outline: p.outline, outlineColor: color(p.outlineColor)
      });
    }
    if (g.wall) {
      const p = g.wall;
      e.wall = clean({
        positions: posArr(p.positions), minimumHeights: p.minimumHeights, maximumHeights: p.maximumHeights,
        material: material(p.material), outline: p.outline, outlineColor: color(p.outlineColor)
      });
    }
    if (g.model) {
      const p = g.model;
      e.model = clean({
        uri: p.uri, scale: p.scale, minimumPixelSize: p.minimumPixelSize, maximumScale: p.maximumScale,
        runAnimations: p.runAnimations, color: color(p.color), silhouetteColor: color(p.silhouetteColor),
        silhouetteSize: p.silhouetteSize, heightReference: heightRef(p.heightReference), shadows: shadowMode(p.shadows),
        distanceDisplayCondition: ddc(p.distanceDisplayCondition)
      });
    }
    if (g.path) {
      const p = g.path;
      e.path = clean({
        leadTime: p.leadTime, trailTime: p.trailTime, width: p.width ?? 2, resolution: p.resolution ?? 60,
        material: material(p.material) || new Cesium.PolylineGlowMaterialProperty({ color: Cesium.Color.YELLOW, glowPower: 0.15 })
      });
    }
    return e;
  }

  function entityProps(inst, entity) {
    if (!entity.properties) return undefined;
    const v = entity.properties.getValue(inst.viewer.clock.currentTime);
    if (!v) return undefined;
    const out = {};
    for (const k in v) if (v[k] !== undefined && v[k] !== null) out[k] = typeof v[k] === 'object' ? safeJson(v[k]) : String(v[k]);
    return out;
  }

  function findEntity(inst, id) {
    const e = inst.viewer.entities.getById(id);
    if (e) return e;
    for (const ds of inst.dataSources.values()) {
      const x = ds.entities.getById(id);
      if (x) return x;
    }
    return undefined;
  }

  // ---------------------------------------------------------------- imagery / terrain / tiles

  function tpl(url, credit, extra) {
    return new Cesium.UrlTemplateImageryProvider(Object.assign({ url: url, credit: credit }, extra || {}));
  }

  async function createImageryProvider(o) {
    const common = clean({ minimumLevel: o.minimumLevel, maximumLevel: o.maximumLevel, rectangle: rect(o.rectangle), credit: o.credit });
    switch (lc(o.provider)) {
      case 'openstreetmap':
        return new Cesium.OpenStreetMapImageryProvider(Object.assign({ url: o.url || 'https://tile.openstreetmap.org/' }, common));
      case 'arcgisworldimagery':
        return tpl('https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
          o.credit || 'Esri, Maxar, Earthstar Geographics', { maximumLevel: o.maximumLevel ?? 19 });
      case 'arcgisworldstreetmap':
        return tpl('https://services.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}',
          o.credit || 'Esri, HERE, Garmin, © OpenStreetMap contributors', { maximumLevel: o.maximumLevel ?? 19 });
      case 'arcgisworldtopo':
        return tpl('https://services.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}',
          o.credit || 'Esri, HERE, Garmin, FAO, NOAA, USGS', { maximumLevel: o.maximumLevel ?? 19 });
      case 'arcgismapserver':
        return Cesium.ArcGisMapServerImageryProvider.fromUrl(o.url, clean({ layers: o.layers }));
      case 'bing':
        return Cesium.BingMapsImageryProvider.fromUrl(o.url || 'https://dev.virtualearth.net',
          clean({ key: o.key, mapStyle: o.mapStyle ? Cesium.BingMapsStyle[upperSnake(o.mapStyle)] : undefined }));
      case 'wms':
        return new Cesium.WebMapServiceImageryProvider(Object.assign({
          url: o.url, layers: o.layers,
          parameters: Object.assign({ transparent: 'true', format: o.format || 'image/png' }, o.parameters || {})
        }, common));
      case 'wmts': {
        const geographic = o.parameters && lc(o.parameters.tilingScheme) === 'geographic';
        return new Cesium.WebMapTileServiceImageryProvider(Object.assign({
          url: o.url, layer: o.layers, style: o.style || 'default', format: o.format || 'image/png',
          tileMatrixSetID: o.tileMatrixSetId,
          tilingScheme: geographic ? new Cesium.GeographicTilingScheme() : undefined
        }, common));
      }
      case 'tms':
        return Cesium.TileMapServiceImageryProvider.fromUrl(o.url, common);
      case 'urltemplate':
        return new Cesium.UrlTemplateImageryProvider(Object.assign(clean({ url: o.url, subdomains: o.subdomains }), common));
      case 'ion':
        return Cesium.IonImageryProvider.fromAssetId(o.assetId);
      case 'singletile':
        return Cesium.SingleTileImageryProvider.fromUrl(o.url, clean({ rectangle: rect(o.rectangle), credit: o.credit }));
      case 'tilecoordinates':
        return new Cesium.TileCoordinatesImageryProvider({ color: color(o.color) || Cesium.Color.YELLOW });
      case 'grid':
        return new Cesium.GridImageryProvider({ color: color(o.color) || Cesium.Color.WHITE });
      default:
        throw new Error('Unknown imagery provider: ' + o.provider);
    }
  }

  function applyLayer(layer, o) {
    for (const k of ['alpha', 'brightness', 'contrast', 'hue', 'saturation', 'gamma', 'show']) if (has(o[k])) layer[k] = o[k];
    const sd = splitDir(o.splitDirection);
    if (sd !== undefined) layer.splitDirection = sd;
  }

  async function createImageryLayer(o) {
    const layer = new Cesium.ImageryLayer(await createImageryProvider(o));
    applyLayer(layer, o);
    return layer;
  }

  async function createTerrain(o) {
    const opts = clean({ requestVertexNormals: o.requestVertexNormals, requestWaterMask: o.requestWaterMask });
    switch (lc(o.provider)) {
      case 'ellipsoid': return new Cesium.EllipsoidTerrainProvider();
      case 'cesiumworldterrain': return Cesium.createWorldTerrainAsync(opts);
      case 'cesiumterrainserver': return Cesium.CesiumTerrainProvider.fromUrl(o.url, opts);
      case 'ion': return Cesium.CesiumTerrainProvider.fromIonAssetId(o.assetId, opts);
      case 'arcgiselevation':
        return Cesium.ArcGISTiledElevationTerrainProvider.fromUrl(o.url ||
          'https://elevation3d.arcgis.com/arcgis/rest/services/WorldElevation3D/Terrain3D/ImageServer');
      default: throw new Error('Unknown terrain provider: ' + o.provider);
    }
  }

  function tileStyle(s) {
    if (!s) return undefined;
    const st = {};
    if (s.colorConditions) st.color = { conditions: s.colorConditions };
    else if (s.color) st.color = s.color;
    if (s.show) st.show = s.show;
    if (s.pointSize) st.pointSize = s.pointSize;
    if (s.defines) st.defines = s.defines;
    return new Cesium.Cesium3DTileStyle(st);
  }

  function applyTileset(inst, id, ts, o) {
    if (has(o.show)) ts.show = o.show;
    if (has(o.maximumScreenSpaceError)) ts.maximumScreenSpaceError = o.maximumScreenSpaceError;
    if (has(o.debugShowBoundingVolume)) ts.debugShowBoundingVolume = o.debugShowBoundingVolume;
    if (o.style) ts.style = tileStyle(o.style);
    if (o.pointCloudShading) {
      const p = o.pointCloudShading, s = ts.pointCloudShading;
      for (const k of ['attenuation', 'maximumAttenuation', 'eyeDomeLighting', 'eyeDomeLightingStrength', 'eyeDomeLightingRadius'])
        if (has(p[k])) s[k] = p[k];
    }
    if (has(o.heightOffset)) {
      const cart = Cesium.Cartographic.fromCartesian(ts.boundingSphere.center);
      const surface = Cesium.Cartesian3.fromRadians(cart.longitude, cart.latitude, 0);
      const offset = Cesium.Cartesian3.fromRadians(cart.longitude, cart.latitude, o.heightOffset);
      ts.modelMatrix = Cesium.Matrix4.fromTranslation(Cesium.Cartesian3.subtract(offset, surface, new Cesium.Cartesian3()));
    }
    if (o.clippingPolygons) {
      ts.clippingPolygons = new Cesium.ClippingPolygonCollection({
        polygons: o.clippingPolygons.map((p) => new Cesium.ClippingPolygon({ positions: posArr(p) })),
        inverse: !!o.clipInverse
      });
    }
    if (o.customShader && !o.customShader.fragmentShaderText && !o.customShader.vertexShaderText) {
      // An empty CustomShaderOptions removes the current shader.
      const key = 'shader:' + id;
      if (inst.timeUniforms.has(key)) { inst.timeUniforms.get(key)(); inst.timeUniforms.delete(key); }
      setAnimating(inst, key, false);
      ts.customShader = undefined;
    } else if (o.customShader) {
      const cs = o.customShader;
      const uniforms = {};
      for (const k in (cs.uniforms || {})) uniforms[k] = { type: Cesium.UniformType.FLOAT, value: cs.uniforms[k] };
      if (cs.animateTime) uniforms.u_time = { type: Cesium.UniformType.FLOAT, value: 0 };
      const shader = new Cesium.CustomShader(clean({
        lightingModel: cs.lightingModel ? Cesium.LightingModel[String(cs.lightingModel).toUpperCase()] : undefined,
        uniforms: uniforms, fragmentShaderText: cs.fragmentShaderText, vertexShaderText: cs.vertexShaderText
      }));
      ts.customShader = shader;
      const key = 'shader:' + id;
      if (inst.timeUniforms.has(key)) { inst.timeUniforms.get(key)(); inst.timeUniforms.delete(key); }
      if (cs.animateTime) {
        const t0 = performance.now();
        const remove = inst.viewer.scene.preUpdate.addEventListener(() => shader.setUniform('u_time', (performance.now() - t0) / 1000));
        inst.timeUniforms.set(key, remove);
        setAnimating(inst, key, true);
      } else setAnimating(inst, key, false);
    }
  }

  // ---------------------------------------------------------------- picking

  function pickPosition(inst, sp, preferGlobe) {
    const scene = inst.viewer.scene;
    let c;
    if (!preferGlobe && scene.pickPositionSupported) {
      const picked = scene.pick(sp);
      if (Cesium.defined(picked)) c = scene.pickPosition(sp);
    }
    if (!c) {
      const ray = scene.camera.getPickRay(sp);
      if (ray) c = scene.globe.pick(ray, scene);
    }
    if (!c) c = scene.camera.pickEllipsoid(sp, scene.globe.ellipsoid);
    return c;
  }

  function ownerIdOf(inst, obj, map) {
    for (const [k, v] of map) if (v === obj) return k;
    return undefined;
  }

  function pickAt(inst, sp) {
    const scene = inst.viewer.scene;
    const r = { kind: 'None', screen: { x: sp.x, y: sp.y } };
    const picked = scene.pick(sp);
    let c;
    if (Cesium.defined(picked) && scene.pickPositionSupported) c = scene.pickPosition(sp);
    if (!c) {
      const ray = scene.camera.getPickRay(sp);
      if (ray) c = scene.globe.pick(ray, scene);
    }
    if (!c) c = scene.camera.pickEllipsoid(sp, scene.globe.ellipsoid);
    r.position = toGeo(c);
    if (Cesium.defined(picked)) {
      if (picked.id instanceof Cesium.Entity) {
        const e = picked.id;
        r.kind = 'Entity'; r.entityId = e.id; r.entityName = e.name; r.properties = entityProps(inst, e);
        if (e.entityCollection && e.entityCollection.owner && e.entityCollection.owner !== inst.viewer.dataSourceDisplay.defaultDataSource)
          r.ownerId = ownerIdOf(inst, e.entityCollection.owner, inst.dataSources);
      } else if (picked instanceof Cesium.Cesium3DTileFeature) {
        r.kind = 'TileFeature';
        const props = {};
        for (const k of picked.getPropertyIds()) {
          const v = picked.getProperty(k);
          if (v !== undefined && v !== null) props[k] = typeof v === 'object' ? safeJson(v) : String(v);
        }
        r.properties = props;
        r.ownerId = ownerIdOf(inst, picked.tileset, inst.tilesets);
      } else if (picked.id && picked.id.owner) {
        r.kind = 'Primitive'; r.ownerId = picked.id.owner; r.properties = { index: String(picked.id.index) };
      } else {
        r.kind = 'Primitive';
      }
    } else if (r.position) {
      r.kind = 'Globe';
    }
    return r;
  }

  // ---------------------------------------------------------------- events

  function emit(inst, name, data) {
    try { inst.sink(name, data === undefined ? null : JSON.stringify(data)); }
    catch (e) { console.error('[Cesium.Net] event sink failed', e); }
  }

  function cameraState(inst) {
    const cam = inst.viewer.camera;
    const vr = cam.computeViewRectangle(inst.viewer.scene.globe.ellipsoid);
    return {
      position: toGeo(cam.positionWC), heading: deg(cam.heading), pitch: deg(cam.pitch), roll: deg(cam.roll),
      viewRectangle: vr ? { west: deg(vr.west), south: deg(vr.south), east: deg(vr.east), north: deg(vr.north) } : null
    };
  }

  function setupEvents(inst) {
    const v = inst.viewer;
    const h = inst.handler = new Cesium.ScreenSpaceEventHandler(v.scene.canvas);
    const ev = () => inst.events;
    const busy = () => inst.draw || inst.edit;
    h.setInputAction((m) => { if (ev().click && !busy()) emit(inst, 'leftClick', pickAt(inst, m.position)); }, Cesium.ScreenSpaceEventType.LEFT_CLICK);
    h.setInputAction((m) => { if (ev().rightClick && !busy()) emit(inst, 'rightClick', pickAt(inst, m.position)); }, Cesium.ScreenSpaceEventType.RIGHT_CLICK);
    h.setInputAction((m) => { if (ev().doubleClick && !busy()) emit(inst, 'doubleClick', pickAt(inst, m.position)); }, Cesium.ScreenSpaceEventType.LEFT_DOUBLE_CLICK);
    let lastMove = 0;
    h.setInputAction((m) => {
      if (!ev().mouseMove || busy()) return;
      const now = performance.now();
      if (now - lastMove < (ev().mouseMoveThrottleMs || 100)) return;
      lastMove = now;
      emit(inst, 'mouseMove', pickAt(inst, m.endPosition));
    }, Cesium.ScreenSpaceEventType.MOUSE_MOVE);

    v.camera.moveEnd.addEventListener(() => { if (ev().cameraChanged) emit(inst, 'cameraChanged', cameraState(inst)); });
    v.selectedEntityChanged.addEventListener((e) => { if (ev().selectedEntityChanged) emit(inst, 'selectedEntityChanged', e ? e.id : null); });
    let lastTick = 0;
    v.clock.onTick.addEventListener((clock) => {
      if (!ev().clockTick) return;
      const now = performance.now();
      if (now - lastTick < (ev().clockTickThrottleMs || 500)) return;
      lastTick = now;
      emit(inst, 'clockTick', { currentTime: iso(clock.currentTime), multiplier: clock.multiplier, shouldAnimate: clock.shouldAnimate });
    });
    let lastPending = -1;
    v.scene.globe.tileLoadProgressEvent.addEventListener((n) => {
      if (!ev().loadingProgress || n === lastPending) return;
      lastPending = n;
      emit(inst, 'loadingProgress', n);
    });
    v.scene.renderError.addEventListener((scene, err) => emit(inst, 'error', { message: String(err && err.message || err), source: 'render', stack: err && err.stack }));
  }

  // ---------------------------------------------------------------- drawing & measuring

  function geodesicLength(cartesians) {
    let total = 0;
    for (let i = 1; i < cartesians.length; i++) {
      const a = Cesium.Cartographic.fromCartesian(cartesians[i - 1]);
      const b = Cesium.Cartographic.fromCartesian(cartesians[i]);
      total += new Cesium.EllipsoidGeodesic(a, b).surfaceDistance;
    }
    return total;
  }
  function polygonArea(cartesians) {
    if (cartesians.length < 3) return 0;
    const R = 6371007.181; // WGS84 authalic radius
    let total = 0;
    for (let i = 0; i < cartesians.length; i++) {
      const p1 = Cesium.Cartographic.fromCartesian(cartesians[i]);
      const p2 = Cesium.Cartographic.fromCartesian(cartesians[(i + 1) % cartesians.length]);
      total += (p2.longitude - p1.longitude) * (2 + Math.sin(p1.latitude) + Math.sin(p2.latitude));
    }
    return Math.abs(total * R * R / 2);
  }
  function fmtDistance(m) { return m >= 1000 ? (m / 1000).toFixed(2) + ' km' : m.toFixed(1) + ' m'; }
  function fmtArea(a) { return a >= 1e6 ? (a / 1e6).toFixed(2) + ' km²' : a >= 1e4 ? (a / 1e4).toFixed(2) + ' ha' : a.toFixed(1) + ' m²'; }

  function measureLabel(text, position) {
    return {
      position: position,
      label: {
        text: text, font: '600 13px "IBM Plex Mono", Consolas, monospace', fillColor: Cesium.Color.fromCssColorString('#15212A'),
        showBackground: true, backgroundColor: Cesium.Color.fromCssColorString('#F5C518'), backgroundPadding: new Cesium.Cartesian2(8, 5),
        pixelOffset: new Cesium.Cartesian2(0, -18), verticalOrigin: Cesium.VerticalOrigin.BOTTOM,
        disableDepthTestDistance: ALWAYS_ON_TOP, style: Cesium.LabelStyle.FILL
      }
    };
  }

  function cancelDraw(inst, silent) {
    const st = inst.draw;
    if (!st) return;
    st.handler.destroy();
    document.removeEventListener('keydown', st.onKey);
    for (const e of st.temp) inst.viewer.entities.remove(e);
    if (st.prevDblClick) inst.viewer.screenSpaceEventHandler.setInputAction(st.prevDblClick, Cesium.ScreenSpaceEventType.LEFT_DOUBLE_CLICK);
    inst.draw = null;
    if (!silent) emit(inst, 'drawCancelled', null);
    requestRender(inst);
  }

  function startDraw(inst, o) {
    cancelDraw(inst, true);
    endEdit(inst);
    const v = inst.viewer;
    const mode = lc(o.mode);
    const isMeasure = mode === 'measuredistance' || mode === 'measurearea';
    const col = color(o.color) || Cesium.Color.fromCssColorString('#F5C518');
    const fill = color(o.fillColor) || col.withAlpha(0.28);
    const width = o.width || 3;
    const clamp = o.clampToGround !== false;
    const keep = o.keepEntity !== false;
    const showLabel = o.showMeasurementLabel !== false && (isMeasure || mode === 'polyline' || mode === 'polygon');
    const st = inst.draw = { mode, points: [], floating: null, temp: [], handler: new Cesium.ScreenSpaceEventHandler(v.scene.canvas) };
    const active = () => (st.floating ? st.points.concat([st.floating]) : st.points);

    st.prevDblClick = v.screenSpaceEventHandler.getInputAction(Cesium.ScreenSpaceEventType.LEFT_DOUBLE_CLICK);
    v.screenSpaceEventHandler.removeInputAction(Cesium.ScreenSpaceEventType.LEFT_DOUBLE_CLICK);

    const addTemp = (e) => { const x = v.entities.add(e); st.temp.push(x); return x; };
    const isLine = mode === 'polyline' || mode === 'measuredistance';
    const isArea = mode === 'polygon' || mode === 'measurearea';

    if (isLine) {
      addTemp({ polyline: { positions: new Cesium.CallbackProperty(active, false), width: width, material: col, clampToGround: clamp } });
    } else if (isArea) {
      addTemp({ polygon: { hierarchy: new Cesium.CallbackProperty(() => new Cesium.PolygonHierarchy(active()), false), material: fill } });
      addTemp({ polyline: { positions: new Cesium.CallbackProperty(() => { const a = active(); return a.length > 1 ? a.concat([a[0]]) : a; }, false), width: 2, material: col, clampToGround: clamp } });
    } else if (mode === 'rectangle') {
      addTemp({ rectangle: { coordinates: new Cesium.CallbackProperty(() => { const a = active(); return a.length > 1 ? Cesium.Rectangle.fromCartesianArray(a) : undefined; }, false), material: fill, outline: true, outlineColor: col } });
    } else if (mode === 'circle') {
      const radius = () => { const a = active(); return a.length > 1 ? Math.max(1, Cesium.Cartesian3.distance(a[0], a[1])) : 1; };
      addTemp({
        position: new (Cesium.CallbackPositionProperty || Cesium.CallbackProperty)(() => st.points[0], false),
        ellipse: { semiMajorAxis: new Cesium.CallbackProperty(radius, false), semiMinorAxis: new Cesium.CallbackProperty(radius, false), material: fill, outline: true, outlineColor: col }
      });
    }
    let liveLabel = null;
    if (showLabel) {
      liveLabel = addTemp(measureLabel(new Cesium.CallbackProperty(() => {
        const a = active();
        return isArea ? fmtArea(polygonArea(a)) : fmtDistance(geodesicLength(a));
      }, false), new (Cesium.CallbackPositionProperty || Cesium.CallbackProperty)(() => { const a = active(); return a[a.length - 1]; }, false)));
      liveLabel.show = false;
    }

    const finish = () => {
      if (inst.draw !== st) return;
      // A double-click produces two clicks at the same spot; drop duplicates.
      const pts = [];
      for (const p of st.points) if (!pts.length || Cesium.Cartesian3.distance(pts[pts.length - 1], p) > 0.05) pts.push(p);
      const min = mode === 'point' ? 1 : (isArea ? 3 : 2);
      if (pts.length < min) { cancelDraw(inst, false); return; }
      st.floating = null;
      let finalEntity = null;
      let radius;
      if (keep) {
        const e = {};
        if (mode === 'point') {
          e.position = pts[0];
          e.point = { pixelSize: 10, color: col, outlineColor: Cesium.Color.WHITE, outlineWidth: 2, heightReference: clamp ? Cesium.HeightReference.CLAMP_TO_GROUND : undefined };
        } else if (isLine) {
          e.polyline = { positions: pts, width: width, material: col, clampToGround: clamp };
        } else if (isArea) {
          e.polygon = { hierarchy: new Cesium.PolygonHierarchy(pts), material: fill, outline: !clamp, outlineColor: col };
          e.polyline = { positions: pts.concat([pts[0]]), width: 2, material: col, clampToGround: clamp };
        } else if (mode === 'rectangle') {
          e.rectangle = { coordinates: Cesium.Rectangle.fromCartesianArray(pts.slice(0, 2)), material: fill, outline: true, outlineColor: col };
        } else if (mode === 'circle') {
          radius = Cesium.Cartesian3.distance(pts[0], pts[1]);
          e.position = pts[0];
          e.ellipse = { semiMajorAxis: radius, semiMinorAxis: radius, material: fill, outline: true, outlineColor: col };
        }
        if (showLabel) {
          const text = isArea ? fmtArea(polygonArea(pts)) : fmtDistance(geodesicLength(pts));
          const lbl = measureLabel(text, isArea ? Cesium.BoundingSphere.fromPoints(pts).center : pts[pts.length - 1]);
          if (!e.position) { e.position = lbl.position; } else { inst.drawn.add(v.entities.add(lbl)); }
          e.label = lbl.label;
        }
        e.id = newId('draw');
        finalEntity = v.entities.add(e);
        inst.drawn.add(finalEntity);
      } else if (mode === 'circle') {
        radius = Cesium.Cartesian3.distance(pts[0], pts[1]);
      }
      const result = { mode: o.mode, entityId: finalEntity ? finalEntity.id : null, positions: pts.map(toGeo), radius: radius };
      cancelDraw(inst, true);
      emit(inst, 'drawCompleted', result);
    };

    st.handler.setInputAction((m) => {
      const c = pickPosition(inst, m.position);
      if (!c) return;
      st.points.push(c);
      if (liveLabel) liveLabel.show = true;
      addTemp({ position: c, point: { pixelSize: 8, color: col, outlineColor: Cesium.Color.fromCssColorString('#15212A'), outlineWidth: 2, disableDepthTestDistance: ALWAYS_ON_TOP } });
      if (mode === 'point' || ((mode === 'rectangle' || mode === 'circle') && st.points.length >= 2)) finish();
      requestRender(inst);
    }, Cesium.ScreenSpaceEventType.LEFT_CLICK);
    st.handler.setInputAction((m) => {
      if (!st.points.length) return;
      const c = pickPosition(inst, m.endPosition, true);
      if (c) { st.floating = c; requestRender(inst); }
    }, Cesium.ScreenSpaceEventType.MOUSE_MOVE);
    st.handler.setInputAction(finish, Cesium.ScreenSpaceEventType.LEFT_DOUBLE_CLICK);
    st.handler.setInputAction(finish, Cesium.ScreenSpaceEventType.RIGHT_CLICK);
    st.onKey = (k) => { if (k.key === 'Escape') cancelDraw(inst, false); else if (k.key === 'Enter') finish(); };
    document.addEventListener('keydown', st.onKey);
  }

  // ---------------------------------------------------------------- editing

  function beginEdit(inst, id) {
    endEdit(inst);
    cancelDraw(inst, true);
    const v = inst.viewer;
    const e = findEntity(inst, id);
    if (!e) throw new Error('Entity not found: ' + id);
    const time = v.clock.currentTime;
    let pts, kind;
    if (e.polygon) {
      kind = 'polygon';
      pts = e.polygon.hierarchy.getValue(time).positions.slice();
      e.polygon.hierarchy = new Cesium.CallbackProperty(() => new Cesium.PolygonHierarchy(pts), false);
      if (e.polyline) e.polyline.positions = new Cesium.CallbackProperty(() => pts.concat([pts[0]]), false);
    } else if (e.polyline) {
      kind = 'polyline';
      pts = e.polyline.positions.getValue(time).slice();
      e.polyline.positions = new Cesium.CallbackProperty(() => pts, false);
    } else if (e.position) {
      kind = 'position';
      pts = [e.position.getValue(time)];
      e.position = new Cesium.ConstantPositionProperty(pts[0]);
    } else throw new Error('Entity has no editable geometry: ' + id);

    const handles = pts.map((p, i) => v.entities.add({
      position: new Cesium.ConstantPositionProperty(p),
      point: { pixelSize: 13, color: Cesium.Color.fromCssColorString('#F5C518'), outlineColor: Cesium.Color.fromCssColorString('#15212A'), outlineWidth: 2, disableDepthTestDistance: ALWAYS_ON_TOP },
      properties: { __handle: i }
    }));
    const st = inst.edit = { id, e, kind, pts, handles, dragging: -1, handler: new Cesium.ScreenSpaceEventHandler(v.scene.canvas) };
    const ssc = v.scene.screenSpaceCameraController;
    st.handler.setInputAction((m) => {
      const picked = v.scene.pick(m.position);
      if (picked && picked.id instanceof Cesium.Entity) {
        const idx = handles.indexOf(picked.id);
        if (idx >= 0) { st.dragging = idx; ssc.enableInputs = false; }
      }
    }, Cesium.ScreenSpaceEventType.LEFT_DOWN);
    st.handler.setInputAction((m) => {
      if (st.dragging < 0) return;
      const c = pickPosition(inst, m.endPosition, true);
      if (!c) return;
      pts[st.dragging] = c;
      handles[st.dragging].position.setValue(c);
      if (kind === 'position') e.position.setValue(c);
      requestRender(inst);
    }, Cesium.ScreenSpaceEventType.MOUSE_MOVE);
    st.handler.setInputAction(() => {
      if (st.dragging < 0) return;
      st.dragging = -1;
      ssc.enableInputs = true;
      emit(inst, 'entityEdited', { entityId: id, positions: pts.map(toGeo) });
    }, Cesium.ScreenSpaceEventType.LEFT_UP);
  }

  function endEdit(inst) {
    const st = inst.edit;
    if (!st) return;
    st.handler.destroy();
    inst.viewer.scene.screenSpaceCameraController.enableInputs = true;
    for (const h of st.handles) inst.viewer.entities.remove(h);
    const pts = st.pts.slice();
    if (st.kind === 'polygon') {
      st.e.polygon.hierarchy = new Cesium.PolygonHierarchy(pts);
      if (st.e.polyline) st.e.polyline.positions = pts.concat([pts[0]]);
    } else if (st.kind === 'polyline') {
      st.e.polyline.positions = pts;
    }
    inst.edit = null;
    requestRender(inst);
  }

  // ---------------------------------------------------------------- camera animation

  // CesiumJS silently ignores camera flights while the scene is morphing (their promises would
  // never settle), so camera calls wait for a running morph to finish first.
  function afterMorph(inst, timeoutMs) {
    const scene = inst.viewer.scene;
    if (scene.mode !== Cesium.SceneMode.MORPHING) return Promise.resolve();
    return new Promise((resolve) => {
      let remove = null;
      const done = () => { if (remove) { remove(); remove = null; } clearTimeout(timer); resolve(); };
      const timer = setTimeout(done, timeoutMs || 10000);
      remove = scene.morphComplete.addEventListener(done);
    });
  }

  function stopCameraAnimation(inst) {
    if (inst.cameraAnim) { cancelAnimationFrame(inst.cameraAnim); inst.cameraAnim = 0; }
    if (inst.orbiting) { inst.viewer.camera.lookAtTransform(Cesium.Matrix4.IDENTITY); inst.orbiting = false; }
  }

  function flightMode(inst, enable, o) {
    const v = inst.viewer;
    const ssc = v.scene.screenSpaceCameraController;
    if (inst.flight) {
      const f = inst.flight;
      cancelAnimationFrame(f.raf);
      f.handler.destroy();
      document.removeEventListener('keydown', f.down);
      document.removeEventListener('keyup', f.up);
      for (const k of ['enableRotate', 'enableTranslate', 'enableZoom', 'enableTilt', 'enableLook']) ssc[k] = f.saved[k];
      inst.flight = null;
    }
    if (!enable) return;
    o = o || {};
    const saved = {};
    for (const k of ['enableRotate', 'enableTranslate', 'enableZoom', 'enableTilt', 'enableLook']) { saved[k] = ssc[k]; ssc[k] = false; }
    const keys = {};
    const f = inst.flight = { saved, keys, handler: new Cesium.ScreenSpaceEventHandler(v.scene.canvas), looking: false, start: null, cur: null };
    v.scene.canvas.setAttribute('tabindex', '0');
    v.scene.canvas.focus();
    f.down = (e) => { keys[e.code] = true; };
    f.up = (e) => { keys[e.code] = false; };
    document.addEventListener('keydown', f.down);
    document.addEventListener('keyup', f.up);
    f.handler.setInputAction((m) => { f.looking = true; f.start = Cesium.Cartesian2.clone(m.position); f.cur = Cesium.Cartesian2.clone(m.position); }, Cesium.ScreenSpaceEventType.LEFT_DOWN);
    f.handler.setInputAction((m) => { if (f.looking) f.cur = Cesium.Cartesian2.clone(m.endPosition); }, Cesium.ScreenSpaceEventType.MOUSE_MOVE);
    f.handler.setInputAction(() => { f.looking = false; }, Cesium.ScreenSpaceEventType.LEFT_UP);
    let last = performance.now();
    const tick = () => {
      const now = performance.now();
      const dt = Math.min(0.1, (now - last) / 1000);
      last = now;
      const cam = v.camera;
      const canvas = v.scene.canvas;
      if (f.looking && f.start && f.cur) {
        const dx = (f.cur.x - f.start.x) / canvas.clientWidth;
        const dy = -(f.cur.y - f.start.y) / canvas.clientHeight;
        cam.setView({ orientation: { heading: cam.heading + dx * 2.5 * dt * 4, pitch: Cesium.Math.clamp(cam.pitch + dy * 2.5 * dt * 4, -Cesium.Math.PI_OVER_TWO, Cesium.Math.PI_OVER_TWO), roll: 0 } });
      }
      const height = v.scene.globe.ellipsoid.cartesianToCartographic(cam.position).height;
      let speed = o.speed || 120;
      if (o.scaleWithHeight !== false && !has(o.walkHeight)) speed = Math.max(speed, height * 0.6);
      if (keys.ShiftLeft || keys.ShiftRight) speed *= 4;
      const d = speed * dt;
      if (keys.KeyW) cam.moveForward(d);
      if (keys.KeyS) cam.moveBackward(d);
      if (keys.KeyA) cam.moveLeft(d);
      if (keys.KeyD) cam.moveRight(d);
      if (keys.KeyQ) cam.moveDown(d);
      if (keys.KeyE) cam.moveUp(d);
      if (has(o.walkHeight)) {
        const carto = v.scene.globe.ellipsoid.cartesianToCartographic(cam.position);
        const ground = v.scene.globe.getHeight(carto);
        if (ground !== undefined) {
          cam.setView({ destination: Cesium.Cartesian3.fromRadians(carto.longitude, carto.latitude, ground + o.walkHeight), orientation: { heading: cam.heading, pitch: cam.pitch, roll: 0 } });
        }
      }
      v.scene.requestRender();
      f.raf = requestAnimationFrame(tick);
    };
    f.raf = requestAnimationFrame(tick);
  }

  // ---------------------------------------------------------------- weather (post-process)

  const SHADERS = {
    rain: `
uniform sampler2D colorTexture;
in vec2 v_textureCoordinates;
uniform float intensity;
uniform float angle;
float hash(float x) { return fract(sin(x * 133.3) * 13.13); }
void main(void) {
  float time = czm_frameNumber / 60.0;
  vec2 resolution = czm_viewport.zw;
  vec2 uv = (gl_FragCoord.xy * 2.0 - resolution.xy) / min(resolution.x, resolution.y);
  float si = sin(angle), co = cos(angle);
  uv *= mat2(co, -si, si, co);
  uv *= length(uv + vec2(0.0, 4.9)) * 0.3 + 1.0;
  float v = 1.0 - sin(hash(floor(uv.x * 100.0)) * 2.0);
  float b = clamp(abs(sin(20.0 * time * v + uv.y * (5.0 / (2.0 + v)))) - 0.95, 0.0, 1.0) * 20.0;
  vec3 c = vec3(0.65, 0.72, 0.8) * v * b;
  vec4 base = texture(colorTexture, v_textureCoordinates);
  out_FragColor = vec4(base.rgb * (1.0 - 0.18 * intensity) + c * intensity * 0.6, 1.0);
}`,
    snow: `
uniform sampler2D colorTexture;
in vec2 v_textureCoordinates;
uniform float intensity;
uniform float angle;
float snow(vec2 uv, float scale, float time) {
  float w = smoothstep(1.0, 0.0, -uv.y * (scale / 10.0));
  if (w < 0.1) return 0.0;
  uv += time / scale;
  uv.y += time * 2.0 / scale;
  uv.x += sin(uv.y + time * 0.5) / scale + angle * 0.2;
  uv *= scale;
  vec2 s = floor(uv), f = fract(uv), p = vec2(0.0);
  float k = 3.0;
  p = 0.5 + 0.35 * sin(11.0 * fract(sin((s + p + scale) * mat2(7, 3, 6, 5)) * 5.0)) - f;
  float d = length(p);
  k = min(d, k);
  k = smoothstep(0.0, k, sin(f.x + f.y) * 0.01);
  return k * w;
}
void main(void) {
  vec2 resolution = czm_viewport.zw;
  vec2 uv = (gl_FragCoord.xy * 2.0 - resolution.xy) / min(resolution.x, resolution.y);
  float time = czm_frameNumber / 60.0;
  float c = snow(uv, 10.0, time) + snow(uv, 8.0, time) + snow(uv, 6.0, time) + snow(uv, 5.0, time);
  if (intensity > 0.5) c += snow(uv, 15.0, time) + snow(uv, 20.0, time);
  vec4 base = texture(colorTexture, v_textureCoordinates);
  out_FragColor = vec4(base.rgb * (1.0 - 0.1 * intensity) + vec3(c) * intensity, 1.0);
}`,
    fog: `
uniform sampler2D colorTexture;
uniform sampler2D depthTexture;
in vec2 v_textureCoordinates;
uniform float intensity;
uniform float angle;
void main(void) {
  vec4 base = texture(colorTexture, v_textureCoordinates);
  float depth = czm_readDepth(depthTexture, v_textureCoordinates);
  vec4 fogColor = vec4(0.82, 0.85, 0.88, 1.0);
  float f;
  if (depth >= 1.0) {
    f = 0.85 * intensity;
  } else {
    vec4 positionEC = czm_windowToEyeCoordinates(gl_FragCoord.xy, depth);
    float dist = length(positionEC.xyz / positionEC.w);
    f = (1.0 - exp(-dist / (9000.0 * (1.05 - intensity)))) * intensity;
  }
  out_FragColor = mix(base, fogColor, clamp(f, 0.0, 0.95));
}`
  };

  function setWeather(inst, w) {
    const stages = inst.viewer.scene.postProcessStages;
    if (inst.weather && inst.weather.stage) {
      stages.remove(inst.weather.stage);
    }
    setAnimating(inst, 'weather', false);
    const kind = lc(w && w.kind);
    if (!kind || kind === 'none' || !SHADERS[kind]) { inst.weather = null; return; }
    const state = inst.weather = { intensity: w.intensity ?? 0.6, angle: rad(w.windAngle ?? -10) };
    state.stage = stages.add(new Cesium.PostProcessStage({
      name: 'cesiumnet_weather_' + kind + '_' + (nextObjectId++),
      fragmentShader: SHADERS[kind],
      uniforms: { intensity: () => state.intensity, angle: () => state.angle }
    }));
    if (kind !== 'fog') setAnimating(inst, 'weather', true);
  }

  // ---------------------------------------------------------------- scene settings

  function configureScene(inst, s) {
    const v = inst.viewer, scene = v.scene, globe = scene.globe;
    if (has(s.enableLighting)) globe.enableLighting = s.enableLighting;
    if (has(s.showSun) && scene.sun) scene.sun.show = s.showSun;
    if (has(s.showMoon) && scene.moon) scene.moon.show = s.showMoon;
    if (has(s.showSkyBox) && scene.skyBox) scene.skyBox.show = s.showSkyBox;
    if (has(s.showSkyAtmosphere) && scene.skyAtmosphere) scene.skyAtmosphere.show = s.showSkyAtmosphere;
    if (has(s.showGroundAtmosphere)) globe.showGroundAtmosphere = s.showGroundAtmosphere;
    if (has(s.showGlobe)) globe.show = s.showGlobe;
    if (has(s.depthTestAgainstTerrain)) globe.depthTestAgainstTerrain = s.depthTestAgainstTerrain;
    if (s.backgroundColor) scene.backgroundColor = color(s.backgroundColor);
    if (s.globeBaseColor) globe.baseColor = color(s.globeBaseColor);
    if (has(s.shadows)) v.shadows = s.shadows;
    if (has(s.softShadows)) v.shadowMap.softShadows = s.softShadows;
    if (has(s.shadowDarkness)) v.shadowMap.darkness = s.shadowDarkness;
    if (has(s.shadowMapSize)) v.shadowMap.size = s.shadowMapSize;
    if (has(s.terrainShadows)) v.terrainShadows = shadowMode(s.terrainShadows);
    if (has(s.fogEnabled)) scene.fog.enabled = s.fogEnabled;
    if (has(s.fogDensity)) scene.fog.density = s.fogDensity;
    if (has(s.showWaterEffect)) globe.showWaterEffect = s.showWaterEffect;
    const atm = scene.atmosphere;
    if (atm) {
      if (has(s.atmosphereHueShift)) atm.hueShift = s.atmosphereHueShift;
      if (has(s.atmosphereSaturationShift)) atm.saturationShift = s.atmosphereSaturationShift;
      if (has(s.atmosphereBrightnessShift)) atm.brightnessShift = s.atmosphereBrightnessShift;
      if (has(s.atmosphereLightIntensity)) atm.lightIntensity = s.atmosphereLightIntensity;
    }
    if (scene.skyAtmosphere) {
      if (has(s.atmosphereHueShift)) scene.skyAtmosphere.hueShift = s.atmosphereHueShift;
      if (has(s.atmosphereSaturationShift)) scene.skyAtmosphere.saturationShift = s.atmosphereSaturationShift;
      if (has(s.atmosphereBrightnessShift)) scene.skyAtmosphere.brightnessShift = s.atmosphereBrightnessShift;
    }
    if (has(s.cameraLight)) {
      if (inst.cameraLightRemove) { inst.cameraLightRemove(); inst.cameraLightRemove = null; }
      if (s.cameraLight) {
        const light = new Cesium.DirectionalLight({ direction: Cesium.Cartesian3.clone(scene.camera.directionWC) });
        scene.light = light;
        inst.cameraLightRemove = scene.preRender.addEventListener(() => { Cesium.Cartesian3.clone(scene.camera.directionWC, light.direction); });
      } else {
        scene.light = new Cesium.SunLight();
      }
    }
    if (has(s.lightIntensity)) scene.light.intensity = s.lightIntensity;
    if (s.lightColor) scene.light.color = color(s.lightColor);
    if (has(s.highDynamicRange)) scene.highDynamicRange = s.highDynamicRange;
    if (has(s.fxaa)) scene.postProcessStages.fxaa.enabled = s.fxaa;
    if (has(s.msaaSamples)) scene.msaaSamples = s.msaaSamples;
    if (has(s.resolutionScale)) v.resolutionScale = s.resolutionScale;
    if (has(s.showFps)) scene.debugShowFramesPerSecond = s.showFps;
    const bloom = scene.postProcessStages.bloom;
    if (has(s.bloom)) bloom.enabled = s.bloom;
    if (has(s.bloomContrast)) bloom.uniforms.contrast = s.bloomContrast;
    if (has(s.bloomBrightness)) bloom.uniforms.brightness = s.bloomBrightness;
    const ao = scene.postProcessStages.ambientOcclusion;
    if (has(s.ambientOcclusion)) ao.enabled = s.ambientOcclusion;
    if (has(s.ambientOcclusionIntensity)) ao.uniforms.intensity = s.ambientOcclusionIntensity;
    if (has(s.globeTranslucency)) globe.translucency.enabled = s.globeTranslucency;
    if (has(s.globeFrontAlpha)) globe.translucency.frontFaceAlpha = s.globeFrontAlpha;
    if (has(s.verticalExaggeration)) scene.verticalExaggeration = s.verticalExaggeration;
    if (has(s.verticalExaggerationRelativeHeight)) scene.verticalExaggerationRelativeHeight = s.verticalExaggerationRelativeHeight;
    if (has(s.splitPosition)) scene.splitPosition = s.splitPosition;
  }

  // ---------------------------------------------------------------- object lookup

  function resolveObject(inst, id) {
    return findEntity(inst, id) || inst.dataSources.get(id) || inst.tilesets.get(id) || (inst.primitives.get(id) || {}).bounds;
  }

  // ---------------------------------------------------------------- method table

  const M = {
    // --- generic
    'eval': async (inst, script) => {
      let fn;
      try { fn = new Function('viewer', 'Cesium', 'scene', 'return (' + script + '\n);'); }
      catch (e) { fn = new Function('viewer', 'Cesium', 'scene', script); }
      const r = await fn(inst.viewer, Cesium, inst.viewer.scene);
      if (r === undefined || r === null) return null;
      try { return JSON.parse(JSON.stringify(r)); } catch (e) { return String(r); }
    },
    'events.configure': (inst, ev) => { inst.events = Object.assign({}, inst.events, ev); },

    // --- camera
    'camera.flyTo': async (inst, v) => {
      await afterMorph(inst);
      await new Promise((resolve) => {
        stopCameraAnimation(inst);
        inst.viewer.camera.flyTo(clean({
          destination: pos(v.destination),
          orientation: { heading: rad(v.heading ?? 0), pitch: rad(v.pitch ?? -90), roll: rad(v.roll ?? 0) },
          duration: v.duration, complete: resolve, cancel: resolve
        }));
      });
    },
    'camera.setView': (inst, v) => {
      stopCameraAnimation(inst);
      inst.viewer.camera.setView({ destination: pos(v.destination), orientation: { heading: rad(v.heading ?? 0), pitch: rad(v.pitch ?? -90), roll: rad(v.roll ?? 0) } });
    },
    'camera.flyToRectangle': async (inst, r, d) => {
      await afterMorph(inst);
      await new Promise((resolve) => {
        stopCameraAnimation(inst);
        inst.viewer.camera.flyTo(clean({ destination: rect(r), duration: d ?? undefined, complete: resolve, cancel: resolve }));
      });
    },
    'camera.flyHome': (inst, d) => { stopCameraAnimation(inst); inst.viewer.camera.flyHome(d ?? undefined); },
    'camera.flyToObject': async (inst, id, off, d) => {
      stopCameraAnimation(inst);
      const target = resolveObject(inst, id);
      if (!target) throw new Error('Object not found: ' + id);
      const opts = clean({ duration: d ?? undefined, offset: off ? new Cesium.HeadingPitchRange(rad(off.heading), rad(off.pitch), off.range) : undefined });
      if (target instanceof Cesium.BoundingSphere) {
        await new Promise((resolve) => inst.viewer.camera.flyToBoundingSphere(target, Object.assign(opts, { complete: resolve, cancel: resolve })));
      } else {
        await inst.viewer.flyTo(target, opts);
      }
    },
    'camera.lookAt': (inst, t, o) => {
      stopCameraAnimation(inst);
      inst.viewer.camera.lookAt(pos(t), new Cesium.HeadingPitchRange(rad(o.heading), rad(o.pitch), o.range));
    },
    'camera.releaseLookAt': (inst) => { stopCameraAnimation(inst); inst.viewer.camera.lookAtTransform(Cesium.Matrix4.IDENTITY); },
    'camera.track': (inst, id) => { inst.viewer.trackedEntity = id ? findEntity(inst, id) : undefined; },
    'camera.startOrbit': (inst, t, range, pitch, dps) => {
      stopCameraAnimation(inst);
      const center = pos(t);
      let heading = inst.viewer.camera.heading;
      let last = performance.now();
      inst.orbiting = true;
      const step = () => {
        const now = performance.now();
        heading += rad(dps) * (now - last) / 1000;
        last = now;
        inst.viewer.camera.lookAt(center, new Cesium.HeadingPitchRange(heading, rad(pitch), range));
        inst.viewer.scene.requestRender();
        inst.cameraAnim = requestAnimationFrame(step);
      };
      inst.cameraAnim = requestAnimationFrame(step);
    },
    'camera.startSpin': (inst, dps) => {
      stopCameraAnimation(inst);
      let last = performance.now();
      const step = () => {
        const now = performance.now();
        inst.viewer.camera.rotate(Cesium.Cartesian3.UNIT_Z, -rad(dps) * (now - last) / 1000);
        last = now;
        inst.viewer.scene.requestRender();
        inst.cameraAnim = requestAnimationFrame(step);
      };
      inst.cameraAnim = requestAnimationFrame(step);
    },
    'camera.stopAnimation': (inst) => stopCameraAnimation(inst),
    'camera.zoom': (inst, m, zoomIn) => {
      const cam = inst.viewer.camera;
      const h = inst.viewer.scene.globe.ellipsoid.cartesianToCartographic(cam.position).height;
      const amount = m ?? Math.max(10, h * (zoomIn ? 0.2 : 0.25));
      if (zoomIn) cam.zoomIn(amount); else cam.zoomOut(amount);
    },
    'camera.move': (inst, dir, m) => {
      const cam = inst.viewer.camera;
      const h = inst.viewer.scene.globe.ellipsoid.cartesianToCartographic(cam.position).height;
      const amount = m ?? Math.max(10, h * 0.1);
      const fn = { forward: 'moveForward', backward: 'moveBackward', left: 'moveLeft', right: 'moveRight', up: 'moveUp', down: 'moveDown' }[lc(dir)];
      cam[fn](amount);
    },
    'camera.rotate': (inst, dir, d) => {
      const fn = { left: 'rotateLeft', right: 'rotateRight', up: 'rotateUp', down: 'rotateDown', forward: 'rotateUp', backward: 'rotateDown' }[lc(dir)];
      inst.viewer.camera[fn](rad(d));
    },
    'camera.look': (inst, dir, d) => {
      const fn = { left: 'lookLeft', right: 'lookRight', up: 'lookUp', down: 'lookDown', forward: 'lookUp', backward: 'lookDown' }[lc(dir)];
      inst.viewer.camera[fn](rad(d));
    },
    'camera.twist': (inst, d) => inst.viewer.camera.twistRight(rad(d)),
    'camera.getState': (inst) => cameraState(inst),
    'camera.configureController': (inst, o) => {
      const ssc = inst.viewer.scene.screenSpaceCameraController;
      for (const k in o) if (has(o[k])) ssc[k] = o[k];
    },
    'camera.flightMode': (inst, enable, o) => flightMode(inst, enable, o),

    // --- entities
    'entities.add': async (inst, o) => {
      const e = inst.viewer.entities.add(await buildEntity(inst, o));
      return e.id;
    },
    'entities.addMany': async (inst, list) => {
      const col = inst.viewer.entities;
      const built = [];
      for (const o of list) built.push(await buildEntity(inst, o));
      col.suspendEvents();
      try { return built.map((b) => col.add(b).id); }
      finally { col.resumeEvents(); }
    },
    'entities.update': async (inst, id, o) => {
      const e = findEntity(inst, id);
      if (!e) throw new Error('Entity not found: ' + id);
      const b = await buildEntity(inst, Object.assign({}, o, { id: undefined }));
      delete b.id;
      if (b.orientation === undefined && o.orientation && e.position) {
        b.orientation = orientationFor(e.position.getValue(inst.viewer.clock.currentTime), o.orientation.heading, o.orientation.pitch, o.orientation.roll);
      }
      for (const k in b) e[k] = b[k];
    },
    'entities.setPositions': (inst, updates) => {
      const col = inst.viewer.entities;
      col.suspendEvents();
      try {
        for (const u of updates) {
          const e = findEntity(inst, u.id);
          if (!e) continue;
          const c = pos(u.position);
          if (e.position instanceof Cesium.ConstantPositionProperty) e.position.setValue(c);
          else e.position = new Cesium.ConstantPositionProperty(c);
          if (has(u.heading)) {
            if (e.model || e.box || e.cylinder) e.orientation = orientationFor(c, u.heading, 0, 0);
            if (e.billboard) { e.billboard.alignedAxis = Cesium.Cartesian3.UNIT_Z; e.billboard.rotation = -rad(u.heading); }
          }
        }
      } finally { col.resumeEvents(); }
    },
    'entities.addSamples': (inst, id, times, positions) => {
      const e = findEntity(inst, id);
      if (!e || !(e.position instanceof Cesium.SampledPositionProperty)) throw new Error('Entity has no sampled position: ' + id);
      e.position.addSamples(times.map(jd), posArr(positions));
    },
    'entities.show': (inst, id, show) => { const e = findEntity(inst, id); if (e) e.show = show; },
    'entities.remove': (inst, id) => {
      const e = findEntity(inst, id);
      return e && e.entityCollection ? e.entityCollection.remove(e) : false;
    },
    'entities.clear': (inst) => { inst.viewer.entities.removeAll(); inst.drawn.clear(); },
    'entities.select': (inst, id) => { inst.viewer.selectedEntity = id ? findEntity(inst, id) : undefined; },
    'entities.getPosition': (inst, id) => {
      const e = findEntity(inst, id);
      return e && e.position ? toGeo(e.position.getValue(inst.viewer.clock.currentTime)) : null;
    },
    'entities.ids': (inst) => inst.viewer.entities.values.map((e) => e.id),

    // --- imagery
    'imagery.add': async (inst, o) => {
      const layer = await createImageryLayer(o);
      inst.viewer.imageryLayers.add(layer);
      const id = newId('layer');
      inst.layers.set(id, layer);
      return id;
    },
    'imagery.setBase': async (inst, o) => {
      const layers = inst.viewer.imageryLayers;
      const layer = await createImageryLayer(o);
      const old = inst.layers.get('base');
      if (old && layers.contains(old)) layers.remove(old, true);
      layers.add(layer, 0);
      inst.layers.set('base', layer);
      return 'base';
    },
    'imagery.update': (inst, id, o) => { const l = inst.layers.get(id); if (!l) throw new Error('Layer not found: ' + id); applyLayer(l, o); },
    'imagery.remove': (inst, id) => {
      const l = inst.layers.get(id);
      if (l) { inst.viewer.imageryLayers.remove(l, true); inst.layers.delete(id); }
    },
    'imagery.order': (inst, id, op) => {
      const l = inst.layers.get(id), c = inst.viewer.imageryLayers;
      if (!l) return;
      if (op === 'raise') c.raise(l); else if (op === 'lower') c.lower(l); else if (op === 'top') c.raiseToTop(l); else c.lowerToBottom(l);
    },
    'imagery.ids': (inst) => {
      const c = inst.viewer.imageryLayers, out = [];
      for (let i = 0; i < c.length; i++) { const id = ownerIdOf(inst, c.get(i), inst.layers); if (id) out.push(id); }
      return out;
    },

    // --- terrain
    'terrain.set': async (inst, o) => { inst.viewer.terrainProvider = await createTerrain(o); },
    'terrain.sample': async (inst, points) => {
      const tp = inst.viewer.terrainProvider;
      const cartos = points.map((p) => Cesium.Cartographic.fromDegrees(p[0], p[1]));
      if (tp instanceof Cesium.EllipsoidTerrainProvider) return points.map((p) => [p[0], p[1], 0]);
      const out = await Cesium.sampleTerrainMostDetailed(tp, cartos);
      return out.map((c) => [deg(c.longitude), deg(c.latitude), c.height ?? 0]);
    },
    'terrain.clip': (inst, polys, inverse) => {
      const globe = inst.viewer.scene.globe;
      globe.clippingPolygons = polys && polys.length
        ? new Cesium.ClippingPolygonCollection({ polygons: polys.map((p) => new Cesium.ClippingPolygon({ positions: posArr(p) })), inverse: !!inverse })
        : undefined;
    },

    // --- 3D tiles
    'tilesets.add': async (inst, o) => {
      const opts = clean({
        maximumScreenSpaceError: o.maximumScreenSpaceError, skipLevelOfDetail: o.skipLevelOfDetail,
        dynamicScreenSpaceError: o.dynamicScreenSpaceError, shadows: shadowMode(o.shadows)
      });
      let ts;
      switch (lc(o.kind)) {
        case 'ion': ts = await Cesium.Cesium3DTileset.fromIonAssetId(o.assetId, opts); break;
        case 'osmbuildings': ts = await Cesium.createOsmBuildingsAsync(opts); break;
        case 'googlephotorealistic':
          ts = await Cesium.createGooglePhotorealistic3DTileset(clean({ key: o.key, onlyUsingWithGoogleGeocoder: true }), opts);
          break;
        default: ts = await Cesium.Cesium3DTileset.fromUrl(o.url, opts);
      }
      const id = newId('tileset');
      inst.tilesets.set(id, ts);
      inst.viewer.scene.primitives.add(ts);
      applyTileset(inst, id, ts, o);
      if (o.flyTo) await inst.viewer.zoomTo(ts);
      return id;
    },
    'tilesets.style': (inst, id, s) => { const ts = inst.tilesets.get(id); if (ts) ts.style = tileStyle(s); },
    'tilesets.update': (inst, id, o) => { const ts = inst.tilesets.get(id); if (ts) applyTileset(inst, id, ts, o); },
    'tilesets.uniform': (inst, id, name, value) => { const ts = inst.tilesets.get(id); if (ts && ts.customShader) ts.customShader.setUniform(name, value); },
    'tilesets.remove': (inst, id) => {
      const ts = inst.tilesets.get(id);
      if (!ts) return;
      const key = 'shader:' + id;
      if (inst.timeUniforms.has(key)) { inst.timeUniforms.get(key)(); inst.timeUniforms.delete(key); setAnimating(inst, key, false); }
      inst.viewer.scene.primitives.remove(ts);
      inst.tilesets.delete(id);
    },

    // --- data sources
    'dataSources.load': async (inst, o) => {
      const v = inst.viewer;
      const kind = lc(o.kind);
      let ds;
      if (kind === 'geojson' || kind === 'topojson') {
        ds = await Cesium.GeoJsonDataSource.load(o.data ? JSON.parse(o.data) : o.url, clean({
          stroke: color(o.stroke), strokeWidth: o.strokeWidth, fill: color(o.fill), markerColor: color(o.markerColor),
          markerSize: o.markerSize, clampToGround: o.clampToGround
        }));
        if (o.extrudeByProperty || o.randomFill) {
          const now = v.clock.currentTime;
          for (const e of ds.entities.values) {
            if (!e.polygon) continue;
            if (o.randomFill) e.polygon.material = Cesium.Color.fromRandom({ alpha: 0.65, minimumRed: 0.15, minimumGreen: 0.25, minimumBlue: 0.45 });
            if (o.extrudeByProperty && e.properties) {
              const val = e.properties[o.extrudeByProperty] && e.properties[o.extrudeByProperty].getValue(now);
              if (val !== undefined && val !== null && !isNaN(Number(val))) {
                e.polygon.extrudedHeight = Number(val) * (o.extrudeScale || 1);
                e.polygon.outline = false;
              }
            }
          }
        }
      } else if (kind === 'kml') {
        const input = o.data ? new DOMParser().parseFromString(o.data, 'text/xml') : o.url;
        ds = await Cesium.KmlDataSource.load(input, { camera: v.scene.camera, canvas: v.scene.canvas, clampToGround: !!o.clampToGround });
      } else if (kind === 'czml') {
        ds = await Cesium.CzmlDataSource.load(o.data ? JSON.parse(o.data) : o.url);
      } else if (kind === 'gpx') {
        const input = o.data ? new DOMParser().parseFromString(o.data, 'text/xml') : o.url;
        ds = await Cesium.GpxDataSource.load(input, { clampToGround: !!o.clampToGround });
      } else throw new Error('Unknown data source kind: ' + o.kind);
      if (o.name) ds.name = o.name;
      await v.dataSources.add(ds);
      const id = newId('ds');
      inst.dataSources.set(id, ds);
      if (o.flyTo) await v.flyTo(ds);
      return id;
    },
    'dataSources.show': (inst, id, show) => { const ds = inst.dataSources.get(id); if (ds) ds.show = show; },
    'dataSources.remove': (inst, id) => {
      const ds = inst.dataSources.get(id);
      if (ds) { inst.viewer.dataSources.remove(ds, true); inst.dataSources.delete(id); }
    },
    'dataSources.count': (inst, id) => { const ds = inst.dataSources.get(id); return ds ? ds.entities.values.length : 0; },

    // --- primitives
    'primitives.addPoints': (inst, o) => {
      const id = newId('points');
      const coll = new Cesium.PointPrimitiveCollection();
      const defColor = color(o.color) || Cesium.Color.fromCssColorString('#00C7FF');
      const outline = color(o.outlineColor);
      const scale = nfs(o.scaleByDistance);
      const size = o.pixelSize ?? 4;
      const cartesians = posArr(o.positions);
      const colors = o.colors;
      const colorCache = new Map();
      for (let i = 0; i < cartesians.length; i++) {
        let c = defColor;
        if (colors) {
          const key = colors[i];
          c = colorCache.get(key);
          if (!c) { c = Cesium.Color.fromCssColorString(key); colorCache.set(key, c); }
        }
        coll.add(clean({ position: cartesians[i], color: c, pixelSize: size, outlineColor: outline, outlineWidth: o.outlineWidth, scaleByDistance: scale, id: { owner: id, index: i } }));
      }
      inst.viewer.scene.primitives.add(coll);
      inst.primitives.set(id, { coll, bounds: Cesium.BoundingSphere.fromPoints(cartesians) });
      return id;
    },
    'primitives.remove': (inst, id) => {
      const p = inst.primitives.get(id);
      if (p) { inst.viewer.scene.primitives.remove(p.coll); inst.primitives.delete(id); }
    },
    'primitives.show': (inst, id, show) => { const p = inst.primitives.get(id); if (p) p.coll.show = show; },

    // --- clock
    'clock.configure': (inst, o) => {
      const clock = inst.viewer.clock;
      if (o.startTime) clock.startTime = jd(o.startTime);
      if (o.stopTime) clock.stopTime = jd(o.stopTime);
      if (o.currentTime) clock.currentTime = jd(o.currentTime);
      if (has(o.multiplier)) clock.multiplier = o.multiplier;
      if (has(o.clockRange)) clock.clockRange = { unbounded: Cesium.ClockRange.UNBOUNDED, clamped: Cesium.ClockRange.CLAMPED, loopstop: Cesium.ClockRange.LOOP_STOP }[lc(o.clockRange)];
      if (has(o.shouldAnimate)) clock.shouldAnimate = o.shouldAnimate;
      if (o.zoomTimeline && inst.viewer.timeline) inst.viewer.timeline.zoomTo(clock.startTime, clock.stopTime);
      setAnimating(inst, 'clock', clock.shouldAnimate && inst.viewer.scene.requestRenderMode);
    },
    'clock.getState': (inst) => {
      const c = inst.viewer.clock;
      return { currentTime: iso(c.currentTime), multiplier: c.multiplier, shouldAnimate: c.shouldAnimate };
    },

    // --- scene
    'scene.mode': async (inst, mode, d) => {
      const s = inst.viewer.scene;
      await afterMorph(inst);
      const target = { scene2d: Cesium.SceneMode.SCENE2D, columbusview: Cesium.SceneMode.COLUMBUS_VIEW }[lc(mode)] || Cesium.SceneMode.SCENE3D;
      if (s.mode === target) return;
      switch (target) {
        case Cesium.SceneMode.SCENE2D: s.morphTo2D(d); break;
        case Cesium.SceneMode.COLUMBUS_VIEW: s.morphToColumbusView(d); break;
        default: s.morphTo3D(d);
      }
      // Resolve when the morph has finished, so a following camera call is not ignored.
      await afterMorph(inst, ((d ?? 2) * 1000) + 5000);
    },
    'scene.configure': (inst, s) => configureScene(inst, s),
    'scene.weather': (inst, w) => setWeather(inst, w),
    'scene.screenshot': (inst) => {
      const s = inst.viewer.scene;
      s.render();
      return s.canvas.toDataURL('image/png');
    },
    'scene.pick': (inst, x, y) => pickAt(inst, new Cesium.Cartesian2(x, y)),
    'scene.toScreen': (inst, p) => {
      const st = Cesium.SceneTransforms;
      const fn = st.worldToWindowCoordinates || st.wgs84ToWindowCoordinates;
      const r = fn(inst.viewer.scene, pos(p));
      return r ? { x: r.x, y: r.y } : null;
    },
    'scene.requestRender': (inst) => requestRender(inst),

    // --- drawing & editing
    'draw.start': (inst, o) => startDraw(inst, o),
    'draw.cancel': (inst) => cancelDraw(inst, false),
    'draw.clear': (inst) => {
      cancelDraw(inst, true);
      for (const e of inst.drawn) inst.viewer.entities.remove(e);
      inst.drawn.clear();
    },
    'edit.begin': (inst, id) => beginEdit(inst, id),
    'edit.end': (inst) => endEdit(inst)
  };

  // ---------------------------------------------------------------- public API

  async function create(container, options, sink) {
    const o = typeof options === 'string' ? JSON.parse(options) : (options || {});
    await loadCesium(o.cesiumBaseUrl);
    if (o.ionAccessToken) Cesium.Ion.defaultAccessToken = o.ionAccessToken;
    const el = typeof container === 'string' ? document.getElementById(container) : container;
    if (!el) throw new Error('Cesium container element not found.');

    const sceneMode = { scene2d: Cesium.SceneMode.SCENE2D, columbusview: Cesium.SceneMode.COLUMBUS_VIEW }[lc(o.sceneMode)] || Cesium.SceneMode.SCENE3D;
    const baseLayer = o.noBaseLayer ? false : await createImageryLayer(o.baseLayer || { provider: 'OpenStreetMap' });
    const viewerOptions = clean({
      baseLayer: o.baseLayerPicker ? undefined : baseLayer,
      baseLayerPicker: !!o.baseLayerPicker,
      geocoder: !!o.geocoder,
      animation: !!o.animation,
      timeline: !!o.timeline,
      homeButton: !!o.homeButton,
      sceneModePicker: !!o.sceneModePicker,
      navigationHelpButton: !!o.navigationHelpButton,
      fullscreenButton: !!o.fullscreenButton,
      infoBox: o.infoBox !== false,
      selectionIndicator: o.selectionIndicator !== false,
      requestRenderMode: !!o.requestRenderMode,
      maximumRenderTimeChange: o.maximumRenderTimeChange,
      msaaSamples: o.msaaSamples,
      sceneMode: sceneMode,
      shadows: !!o.shadows,
      terrainProvider: o.terrain ? await createTerrain(o.terrain) : undefined
    });
    const viewer = new Cesium.Viewer(el, viewerOptions);
    if (o.showCredits === false) viewer.cesiumWidget.creditContainer.style.display = 'none';

    const id = 'cesium-' + (nextViewerId++);
    const inst = {
      id, viewer, sink: sink || function () { },
      events: Object.assign({ click: true, cameraChanged: true, selectedEntityChanged: true }, o.events || {}),
      layers: new Map(), tilesets: new Map(), dataSources: new Map(), primitives: new Map(),
      pinCache: new Map(), drawn: new Set(), timeUniforms: new Map(), animating: new Set(),
      renderLoop: 0, cameraAnim: 0, draw: null, edit: null, flight: null, weather: null
    };
    if (viewer.imageryLayers.length > 0) inst.layers.set('base', viewer.imageryLayers.get(0));

    configureScene(inst, clean({
      enableLighting: o.enableLighting, depthTestAgainstTerrain: o.depthTestAgainstTerrain, showFps: o.showFps,
      backgroundColor: o.backgroundColor, globeBaseColor: o.globeBaseColor, resolutionScale: o.resolutionScale
    }));
    if (o.initialView) M['camera.setView'](inst, o.initialView);

    setupEvents(inst);
    instances.set(id, inst);
    emit(inst, 'ready', null);
    return id;
  }

  async function dispatch(viewerId, method, argsJson) {
    const inst = instances.get(viewerId);
    if (!inst) throw new Error('Cesium viewer not found: ' + viewerId);
    const fn = M[method];
    if (!fn) throw new Error('Unknown Cesium.Net method: ' + method);
    const args = argsJson ? (typeof argsJson === 'string' ? JSON.parse(argsJson) : argsJson) : [];
    try {
      const result = await fn(inst, ...args);
      return result === undefined || result === null ? null : JSON.stringify(result);
    } finally {
      requestRender(inst);
    }
  }

  // Fire-and-report variant for hosts that cannot await JavaScript promises (native WebViews).
  function dispatchFromHost(viewerId, callId, method, argsJson) {
    const inst = instances.get(viewerId);
    const reply = global.__cesiumNetReply;
    dispatch(viewerId, method, argsJson).then(
      (value) => reply({ type: 'result', viewerId, callId, ok: true, value }),
      (err) => {
        reply({ type: 'result', viewerId, callId, ok: false, error: String(err && err.message || err) });
        if (inst) emit(inst, 'error', { message: String(err && err.message || err), source: method, stack: err && err.stack });
      });
    return true;
  }

  function resize(viewerId) {
    const inst = instances.get(viewerId);
    if (inst) { inst.viewer.resize(); requestRender(inst); }
  }

  function destroy(viewerId) {
    const inst = instances.get(viewerId);
    if (!inst) return;
    try {
      cancelDraw(inst, true);
      endEdit(inst);
      flightMode(inst, false);
      stopCameraAnimation(inst);
      if (inst.renderLoop) cancelAnimationFrame(inst.renderLoop);
      for (const remove of inst.timeUniforms.values()) remove();
      if (inst.cameraLightRemove) inst.cameraLightRemove();
      inst.handler.destroy();
      inst.viewer.destroy();
    } finally {
      instances.delete(viewerId);
    }
  }

  global.CesiumNet = { version: '1.0.0', loadCesium, create, dispatch, dispatchFromHost, resize, destroy, methods: M };
})(window);
