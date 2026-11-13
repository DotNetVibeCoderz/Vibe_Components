"""Pulmonary nodule detection for the Medical.Net imaging service.

Two backends:
  * classical  - the same explainable pipeline as Medical.AI.LungNoduleDetector (lung field, soft-tissue candidates,
                 3D shape filter, in-plane principal axes, ACR Lung-RADS v2022 baseline solid-nodule category);
  * monai      - the MONAI Model Zoo bundle ``lung_nodule_ct_detection`` (RetinaNet, LUNA16 / LIDC-IDRI, Apache-2.0
                 code, CC-BY data) when MEDNET_MONAI_LUNG_BUNDLE points to the downloaded bundle folder
                 (``python -m monai.bundle download lung_nodule_ct_detection --bundle_dir models``).

Coordinates are DICOM patient coordinates (LPS, mm).
"""
from __future__ import annotations

import io
import os
import sys
from dataclasses import dataclass

import numpy as np
import pydicom
from scipy import ndimage

BUNDLE = os.environ.get("MEDNET_MONAI_LUNG_BUNDLE")
MIN_SCORE = float(os.environ.get("MEDNET_LUNG_MIN_SCORE", "0.5"))


@dataclass
class Volume:
    hu: np.ndarray            # (z, y, x) Hounsfield units
    spacing: tuple            # (sx, sy, sz) mm
    origin: np.ndarray        # LPS position of voxel (0, 0, 0)
    row: np.ndarray           # LPS direction of +x (columns)
    col: np.ndarray           # LPS direction of +y (rows)
    normal: np.ndarray        # LPS direction of +z (slices)

    def to_patient(self, x: float, y: float, z: float) -> np.ndarray:
        sx, sy, sz = self.spacing
        return self.origin + self.row * x * sx + self.col * y * sy + self.normal * z * sz


def read_volume(blobs: list[bytes]) -> Volume:
    dss = [pydicom.dcmread(io.BytesIO(b)) for b in blobs]
    dss = [d for d in dss if getattr(d, "Modality", "") == "CT"]
    if not dss:
        raise ValueError("No CT instances received.")
    iop = [float(v) for v in getattr(dss[0], "ImageOrientationPatient", [1, 0, 0, 0, 1, 0])]
    row, col = np.array(iop[:3]), np.array(iop[3:])
    normal = np.cross(row, col)
    pos = lambda d: np.array([float(v) for v in getattr(d, "ImagePositionPatient", [0, 0, float(getattr(d, "InstanceNumber", 0))])])
    dss.sort(key=lambda d: float(pos(d) @ normal))
    gaps = np.diff([float(pos(d) @ normal) for d in dss])
    sz = float(np.median(gaps)) if len(gaps) else float(getattr(dss[0], "SliceThickness", 1))
    hu = np.stack([d.pixel_array.astype(np.float32) * float(getattr(d, "RescaleSlope", 1)) + float(getattr(d, "RescaleIntercept", 0)) for d in dss])
    sy, sx = (float(v) for v in getattr(dss[0], "PixelSpacing", [1, 1]))
    return Volume(hu, (sx, sy, sz), pos(dss[0]), row, col, normal)


def lung_rads(mean_diameter_mm: float) -> tuple[str, str, str]:
    """ACR Lung-RADS v2022, baseline, solid nodules (category, management EN, ID)."""
    if mean_diameter_mm < 6:
        return "2", "Continue annual screening with LDCT in 12 months.", "Lanjutkan skrining tahunan dengan LDCT 12 bulan lagi."
    if mean_diameter_mm < 8:
        return "3", "LDCT in 6 months.", "LDCT ulang 6 bulan lagi."
    if mean_diameter_mm < 15:
        return "4A", "LDCT in 3 months; PET/CT may be considered for solid components >= 8 mm.", "LDCT 3 bulan lagi; PET/CT dapat dipertimbangkan untuk komponen solid >= 8 mm."
    return "4B", "Diagnostic chest CT with or without contrast, PET/CT and/or tissue sampling; refer to a multidisciplinary team.", \
        "CT toraks diagnostik dengan/tanpa kontras, PET/CT dan/atau biopsi; rujuk ke tim multidisiplin."


def _axes(ys: np.ndarray, xs: np.ndarray, sx: float, sy: float) -> tuple[float, float]:
    pts = np.stack([xs * sx, ys * sy], 1)
    cov = np.cov(pts.T, bias=True) if len(pts) > 1 else np.zeros((2, 2))
    ev = np.sort(np.linalg.eigvalsh(cov))[::-1]
    return 4 * float(np.sqrt(max(ev[0], 0))), 4 * float(np.sqrt(max(ev[1], 0)))


def classical(v: Volume) -> tuple[list[dict], float]:
    """Port of Medical.AI.LungNoduleDetector. Returns (nodules, lung volume mL)."""
    sx, sy, sz = v.spacing
    field = np.zeros(v.hu.shape, bool)
    lung_voxels = 0
    plane = v.hu.shape[1] * v.hu.shape[2]
    for k in range(v.hu.shape[0]):
        low = v.hu[k] < -400
        lab, n = ndimage.label(low)
        border = set(np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))) - {0}
        lung = low & ~np.isin(lab, list(border))
        lab2, n2 = ndimage.label(lung)
        sizes = ndimage.sum(lung, lab2, range(1, n2 + 1))
        keep = np.isin(lab2, [i + 1 for i, s in enumerate(sizes) if s >= max(20, plane // 200)])
        field[k] = ndimage.binary_fill_holes(keep)
        lung_voxels += int(keep.sum())
    cand = field & (v.hu > -300)
    lab, n = ndimage.label(cand)      # 6-connectivity in 3D
    vox_mm3 = sx * sy * sz
    out = []
    for i, sl in enumerate(ndimage.find_objects(lab), start=1):
        comp = lab[sl] == i
        count = int(comp.sum())
        vol = count * vox_mm3
        eq = 2 * (3 * vol / (4 * np.pi)) ** (1 / 3)
        if eq < 3 or eq > 30:
            continue
        zs, ys, xs = np.nonzero(comp)
        zs, ys, xs = zs + sl[0].start, ys + sl[1].start, xs + sl[2].start
        z_extent = (zs.max() - zs.min() + 1) * sz
        best = np.bincount(zs).argmax()
        long_ax, short_ax = _axes(ys[zs == best], xs[zs == best], sx, sy)
        if z_extent > 3 * max(long_ax, eq) or (short_ax > 0 and long_ax / short_ax > 3):
            continue
        mean_d = round((long_ax + short_ax) / 2, 1)
        cat, en, idn = lung_rads(mean_d)
        c = v.to_patient(xs.mean(), ys.mean(), zs.mean())
        out.append({"id": len(out) + 1, "center_mm": [round(float(t), 1) for t in c], "center_slice": int(round(zs.mean())),
                    "long_axis_mm": round(long_ax, 1), "short_axis_mm": round(short_ax, 1), "mean_diameter_mm": mean_d,
                    "volume_mm3": round(vol), "mean_hu": round(float(v.hu[zs, ys, xs].mean()), 1), "lung_rads": cat,
                    "recommendation_en": en, "recommendation_id": idn, "score": None})
    return out, round(lung_voxels * vox_mm3 / 1000)


_detector = None


def _load_bundle():
    """Builds the RetinaNet detector from the bundle configuration and weights (cached)."""
    global _detector
    if _detector is not None or not BUNDLE:
        return _detector
    import torch  # noqa: PLC0415
    from monai.bundle import ConfigParser  # noqa: PLC0415

    sys.path.insert(0, BUNDLE)        # the bundle's scripts package
    cfg = ConfigParser()
    cfg.read_config(os.path.join(BUNDLE, "configs", "inference.json"))
    cfg["bundle_root"] = BUNDLE
    cfg["device"] = "$torch.device('cuda' if torch.cuda.is_available() else 'cpu')"   # CPU works but is slow (minutes per study)
    net = cfg.get_parsed_content("network")
    net.load_state_dict(torch.load(os.path.join(BUNDLE, "models", "model.pt"), map_location="cuda" if torch.cuda.is_available() else "cpu"))
    net.eval()
    det = cfg.get_parsed_content("detector")
    cfg.get_parsed_content("detector_ops")   # evaluating the expressions configures the detector (thresholds, sliding window)
    det.eval()
    _detector = det
    return _detector


def monai_bundle(v: Volume) -> list[dict]:
    """Runs the MONAI bundle exactly as its inference.json does (RAS, 0.703 x 0.703 x 1.25 mm, HU -1024..300 -> 0..1)."""
    import torch  # noqa: PLC0415
    from monai.data import MetaTensor  # noqa: PLC0415
    from monai.transforms import Orientation, ScaleIntensityRange, Spacing  # noqa: PLC0415

    det = _load_bundle()
    sx, sy, sz = v.spacing
    # array (x, y, z) with an LPS affine, converted to RAS like LoadImaged(affine_lps_to_ras=True)
    lps = np.eye(4)
    lps[:3, 0], lps[:3, 1], lps[:3, 2], lps[:3, 3] = v.row * sx, v.col * sy, v.normal * sz, v.origin
    ras = np.diag([-1, -1, 1, 1]) @ lps
    img = MetaTensor(torch.from_numpy(np.ascontiguousarray(v.hu.transpose(2, 1, 0)))[None], affine=torch.from_numpy(ras))
    img = Orientation(axcodes="RAS")(img)
    img = Spacing(pixdim=(0.703125, 0.703125, 1.25))(img)
    img = ScaleIntensityRange(a_min=-1024.0, a_max=300.0, b_min=0.0, b_max=1.0, clip=True)(img)
    with torch.no_grad():
        out = det([img.as_tensor().float()], use_inferer=True)[0]
    boxes, scores = out["box"].numpy(), out["label_scores"].numpy()
    aff = img.affine.numpy()
    res = []
    for b, s in sorted(zip(boxes, scores), key=lambda t: -t[1]):
        if s < MIN_SCORE:
            continue
        lo, hi = aff @ np.r_[b[:3], 1], aff @ np.r_[b[3:], 1]          # voxel -> RAS mm
        center_ras = (lo[:3] + hi[:3]) / 2
        size = np.abs(hi[:3] - lo[:3])
        center_lps = center_ras * np.array([-1, -1, 1])
        long_ax, short_ax = float(max(size[0], size[1])), float(min(size[0], size[1]))
        mean_d = round((long_ax + short_ax) / 2, 1)
        cat, en, idn = lung_rads(mean_d)
        res.append({"id": len(res) + 1, "center_mm": [round(float(t), 1) for t in center_lps], "center_slice": None,
                    "long_axis_mm": round(long_ax, 1), "short_axis_mm": round(short_ax, 1), "mean_diameter_mm": mean_d,
                    "volume_mm3": round(float(np.pi / 6 * size[0] * size[1] * size[2])), "mean_hu": None, "lung_rads": cat,
                    "recommendation_en": en, "recommendation_id": idn, "score": round(float(s), 3)})
    return res


def detect(blobs: list[bytes]) -> dict:
    v = read_volume(blobs)
    classical_nodules, lung_ml = classical(v)
    use_model = bool(BUNDLE) and _load_bundle() is not None
    nodules = monai_bundle(v) if use_model else classical_nodules
    order = {"2": 2, "3": 3, "4A": 4, "4B": 5}
    category = max((n["lung_rads"] for n in nodules), key=lambda c: order[c], default="1")
    if nodules:
        top = max(nodules, key=lambda n: n["mean_diameter_mm"])
        en = f"{len(nodules)} solid nodule(s); largest {top['mean_diameter_mm']:.1f} mm mean diameter - Lung-RADS {top['lung_rads']}. {top['recommendation_en']}"
        idn = f"{len(nodules)} nodul solid; terbesar diameter rerata {top['mean_diameter_mm']:.1f} mm - Lung-RADS {top['lung_rads']}. {top['recommendation_id']}"
    else:
        en = "No solid pulmonary nodule >= 3 mm detected in the lung fields (Lung-RADS 1)."
        idn = "Tidak tampak nodul paru solid >= 3 mm pada lapang paru (Lung-RADS 1)."
    return {
        "nodules": nodules, "category": category, "lung_volume_ml": lung_ml, "slices_analyzed": int(v.hu.shape[0]),
        "method": "MONAI lung_nodule_ct_detection (RetinaNet, LUNA16)" if use_model else
                  "Lung field (-400 HU, hole fill) + soft-tissue candidates (-300 HU) + 3D shape filter (Python)",
        "impression_en": en, "impression_id": idn,
    }
