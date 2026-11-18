"""
Generates src/GeoPandas.Net.Geometry/Crs/Data/epsg.tsv.gz from PROJ's proj.db (EPSG dataset).

    python tools/epsg/generate_epsg.py <path/to/proj.db>

The EPSG Geodetic Parameter Dataset is (c) IOGP and may be redistributed with attribution
(https://epsg.org/terms-of-use.html). Only CRSs that GeoPandas.Net can represent are exported:
geographic 2D and projected CRSs with a Greenwich prime meridian and a supported projection method.
For each datum the most accurate Helmert transformation to WGS 84 (EPSG:4326) is kept as +towgs84
(position-vector convention); datums without one are treated as WGS 84 compatible (PROJ "ballpark").

Columns (tab separated): code, kind (G|P), name, datum, ellipsoid, a, rf (0 = sphere), towgs84 (7 comma values or empty),
method code, parameters (code=value;… in degrees / metres / unity), linear unit to metre, unit name, west,south,east,north.

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
"""
import gzip, os, sqlite3, sys

SUPPORTED_METHODS = {9807, 9808, 9804, 9805, 1024, 9801, 9802, 9822, 9820, 1027, 9810, 9829, 9809, 9812, 9815, 9806, 1028, 1029, 9835, 9834, 1078}
HELMERT = {9603: 'T', 1031: 'T', 9606: 'PV', 1033: 'PV', 1037: 'PV', 9607: 'CF', 1032: 'CF', 1038: 'CF'}


def main(db_path, out_path):
    c = sqlite3.connect(db_path)
    uom = {(a, str(code)): (name, typ, f) for a, code, name, typ, f in c.execute('select auth_name, code, name, type, conv_factor from unit_of_measure')}

    def sexagesimal_dms(v):  # EPSG 9110: DDD.MMSSsss
        sign = -1 if v < 0 else 1
        v = abs(v)
        d = int(v)
        rest = round((v - d) * 100, 10)
        m = int(rest)
        s = (rest - m) * 100
        return sign * (d + m / 60 + s / 3600)

    def to_degrees(value, ua, uc):
        if str(uc) == '9110':
            return sexagesimal_dms(value)
        if str(uc) in ('9102', '9122'):  # already degrees: avoid radian round-trip noise
            return value
        name, typ, f = uom[(ua, str(uc))]
        return value * f * 180 / 3.141592653589793238 if f else value

    def to_si(value, ua, uc):
        if value is None:
            return None
        name, typ, f = uom[(ua, str(uc))]
        if typ == 'angle':
            return to_degrees(value, ua, uc)
        if typ in ('length', 'scale'):
            return value if str(uc) in ('9001', '9201') else (value * f if f else value)
        return value

    ellipsoids = {}
    for a, code, name, sma, ua, uc, invf, smi in c.execute('select auth_name, code, name, semi_major_axis, uom_auth_name, uom_code, inv_flattening, semi_minor_axis from ellipsoid'):
        f = uom[(ua, str(uc))][2] or 1
        sma *= f
        if invf is None:
            smi = smi * f
            invf = 0 if abs(sma - smi) < 1e-9 else sma / (sma - smi)
        ellipsoids[(a, str(code))] = (name, sma, invf)

    pms = {(a, str(code)): lon for a, code, lon in c.execute('select auth_name, code, longitude from prime_meridian')}
    datums = {}
    for a, code, name, ea, ec, pa, pc in c.execute('select auth_name, code, name, ellipsoid_auth_name, ellipsoid_code, prime_meridian_auth_name, prime_meridian_code from geodetic_datum'):
        datums[(a, str(code))] = (name, ellipsoids[(ea, str(ec))], pms[(pa, str(pc))])

    op_area = {}
    for oa, oc, w, s_, e, n in c.execute('''select u.object_auth_name, u.object_code, x.west_lon, x.south_lat, x.east_lon, x.north_lat
                                            from usage u join extent x on x.auth_name = u.extent_auth_name and x.code = u.extent_code
                                            where u.object_table_name = 'helmert_transformation_table' '''):
        width = (e - w) if e >= w else (e + 360 - w)
        op_area[(oa, str(oc))] = op_area.get((oa, str(oc)), 0) + width * (n - s_)
    # Helmert to WGS 84 per source geodetic CRS: widest area of use first, then best accuracy
    helmert = {}
    q = '''select source_crs_auth_name, source_crs_code, method_code, accuracy, tx, ty, tz, translation_uom_auth_name, translation_uom_code,
                  rx, ry, rz, rotation_uom_auth_name, rotation_uom_code, scale_difference, scale_difference_uom_auth_name, scale_difference_uom_code, code
           from helmert_transformation_table
           where auth_name = 'EPSG' and target_crs_auth_name = 'EPSG' and target_crs_code = '4326' and deprecated = 0 and epoch is null and rate_tx is null'''
    for row in c.execute(q):
        sa, sc, mc, acc, tx, ty, tz, tua, tuc, rx, ry, rz, rua, ruc, ds, dua, duc, tcode = row
        kind = HELMERT.get(mc)
        if kind is None:
            continue
        tf = 1.0 if str(tuc) == '9001' else uom[(tua, str(tuc))][2]
        t = [tx * tf, ty * tf, tz * tf]
        if kind == 'T':
            r = [0.0, 0.0, 0.0]; s = 0.0
        else:
            rf = 1.0 if str(ruc) == '9104' else uom[(rua, str(ruc))][2] / (3.141592653589793238 / 180 / 3600)  # → arc-seconds
            r = [rx * rf, ry * rf, rz * rf]
            if kind == 'CF':
                r = [-v for v in r]
            s = ds if str(duc) == '9202' else ds * uom[(dua, str(duc))][2] * 1e6  # → ppm
        key = (sa, str(sc))
        score = (-round(op_area.get(('EPSG', str(tcode)), 0), 3), acc if acc is not None else 999.0, int(tcode))
        if key not in helmert or score < helmert[key][0]:
            helmert[key] = (score, t + r + [s])

    extents = {}
    for oa, oc, w, s, e, n in c.execute('''select u.object_auth_name, u.object_code, x.west_lon, x.south_lat, x.east_lon, x.north_lat
                                            from usage u join extent x on x.auth_name = u.extent_auth_name and x.code = u.extent_code
                                            where u.object_table_name in ('projected_crs', 'geodetic_crs')'''):
        extents.setdefault((oa, str(oc)), (w, s, e, n))

    def fmt(v):
        return repr(float(v)) if v is not None else ''

    lines = []
    geod = {}
    for a, code, name, typ, da, dc, dep in c.execute("select auth_name, code, name, type, datum_auth_name, datum_code, deprecated from geodetic_crs where auth_name = 'EPSG'"):
        code, dc = str(code), str(dc)
        if (da, dc) not in datums:
            continue
        dname, ell, pm = datums[(da, dc)]
        geod[code] = (name, typ, dname, ell, pm, dep)

    def datum_cols(gcode):
        name, typ, dname, (ename, a, rf), pm, dep = geod[gcode]
        tw = helmert.get(('EPSG', gcode))
        return [dname, ename, fmt(a), fmt(rf), ','.join(fmt(v) for v in tw[1]) if tw else '']

    for code, (name, typ, dname, ell, pm, dep) in geod.items():
        if dep or typ != 'geographic 2D' or pm != 0:
            continue
        ext = extents.get(('EPSG', code), (None,) * 4)
        lines.append('\t'.join([code, 'G', name] + datum_cols(code) + ['', '', '1', 'degree', ','.join(fmt(v) for v in ext)]))

    axis_unit = {}
    for ca, cc, ua, uc in c.execute("select coordinate_system_auth_name, coordinate_system_code, uom_auth_name, uom_code from axis where coordinate_system_order = 1"):
        axis_unit[(ca, str(cc))] = (ua, str(uc))

    conv_cols = ', '.join(f'param{i}_code, param{i}_value, param{i}_uom_auth_name, param{i}_uom_code' for i in range(1, 8))
    convs = {}
    for row in c.execute(f'select auth_name, code, method_code, {conv_cols} from conversion_table'):
        a, code, mc = row[:3]
        if not str(mc).isdigit():
            continue
        params = []
        for i in range(7):
            pc, pv, ua, uc = row[3 + 4 * i: 7 + 4 * i]
            if pc is None or pv is None:
                continue
            params.append((int(pc), to_si(pv, ua, uc)))
        convs[(a, str(code))] = (int(mc), params)

    skipped = {}
    for code, name, csa, csc, ga, gc, cva, cvc in c.execute("select code, name, coordinate_system_auth_name, coordinate_system_code, geodetic_crs_auth_name, geodetic_crs_code, conversion_auth_name, conversion_code from projected_crs where auth_name = 'EPSG' and deprecated = 0"):
        code, gc, csc, cvc = str(code), str(gc), str(csc), str(cvc)
        if ga != 'EPSG' or gc not in geod or (cva, cvc) not in convs:
            continue
        gname, gtyp, dname, ell, pm, gdep = geod[gc]
        mc, params = convs[(cva, cvc)]
        if pm != 0 or mc not in SUPPORTED_METHODS:
            skipped[mc] = skipped.get(mc, 0) + 1
            continue
        ua, uc = axis_unit.get((csa, csc), ('EPSG', '9001'))
        uname, utyp, ufactor = uom[(ua, str(uc))]
        if utyp != 'length':
            continue
        ext = extents.get(('EPSG', code), (None,) * 4)
        lines.append('\t'.join([code, 'P', name] + datum_cols(gc) + [str(mc), ';'.join(f'{pc}={fmt(pv)}' for pc, pv in params),
                                 fmt(ufactor), uname, ','.join(fmt(v) for v in ext)]))

    lines.sort(key=lambda l: int(l.split('\t', 1)[0]))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    meta = c.execute("select value from metadata where key = 'EPSG.VERSION'").fetchone()
    with gzip.open(out_path, 'wt', encoding='utf-8', compresslevel=9) as f:
        f.write(f'# EPSG {meta[0] if meta else "?"} via PROJ proj.db; generated by tools/epsg/generate_epsg.py\n')
        f.write('\n'.join(lines) + '\n')
    print(f'{len(lines)} CRSs written to {out_path} ({os.path.getsize(out_path):,} bytes); unsupported methods skipped: {sorted(skipped.items(), key=lambda kv: -kv[1])[:12]}')


if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, 'src', 'GeoPandas.Net.Geometry', 'Crs', 'Data', 'epsg.tsv.gz'))
