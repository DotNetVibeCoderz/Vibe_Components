"""Medical.Net imaging-AI service (FastAPI).

Contract used by Medical.AI.ImagingAiClient:
  GET  /health                      -> {"status": "ok", "model": ...}
  POST /v1/ct/hemorrhage            multipart field "files": DICOM instances of one non-contrast head CT series
                                    -> {suspected, volume_ml, slices_analyzed, method, impression_en, impression_id,
                                        findings: [{slice, area_pixels, area_mm2, mean_hu, centroid_x, centroid_y}]}
  POST /v1/ct/lung-nodules          multipart field "files": DICOM instances of one chest CT series
                                    -> {nodules: [{id, center_mm (LPS), long_axis_mm, short_axis_mm, mean_diameter_mm,
                                        volume_mm3, mean_hu, lung_rads, recommendation_en, recommendation_id, score}],
                                        category, lung_volume_ml, slices_analyzed, method, impression_en, impression_id}

Backends:
  * MONAI / PyTorch 3D UNet when MEDNET_MONAI_WEIGHTS points to a state_dict trained for 2-class haemorrhage
    segmentation (input: HU clipped to [0, 100] and scaled to [0, 1]).
  * Lung nodules: the MONAI Model Zoo bundle lung_nodule_ct_detection (RetinaNet trained on LUNA16) when
    MEDNET_MONAI_LUNG_BUNDLE points to the downloaded bundle; otherwise the classical detector (see lung.py).
  * Otherwise the same explainable classical pipeline as Medical.AI.HemorrhageScreener
    (3x3 smoothing, 50-90 HU, bone/air-edge suppression, connected components).

A triage aid that prioritises studies for radiologist review - not a diagnosis.
Made by Gravicode Studios, led by Kang Fadhil.
"""
from __future__ import annotations

import io
import os
from dataclasses import dataclass

import numpy as np
import pydicom
from fastapi import FastAPI, File, HTTPException, UploadFile
from scipy import ndimage

import lung

app = FastAPI(title="Medical.Net imaging AI", version="1.2.0")

WEIGHTS = os.environ.get("MEDNET_MONAI_WEIGHTS")
_model = None


def _load_model():
    """Lazily loads a MONAI UNet when weights are configured; returns None otherwise."""
    global _model
    if _model is not None or not WEIGHTS:
        return _model
    import torch  # noqa: PLC0415 - optional dependency
    from monai.networks.nets import UNet  # noqa: PLC0415

    net = UNet(spatial_dims=3, in_channels=1, out_channels=2, channels=(16, 32, 64, 128), strides=(2, 2, 2), num_res_units=2)
    net.load_state_dict(torch.load(WEIGHTS, map_location="cpu"))
    net.eval()
    _model = net
    return _model


@dataclass
class Slice:
    instance: int
    hu: np.ndarray
    px_area_mm2: float
    thickness: float


def _read(blobs: list[bytes]) -> list[Slice]:
    out: list[Slice] = []
    for b in blobs:
        ds = pydicom.dcmread(io.BytesIO(b))
        if getattr(ds, "Modality", "") != "CT":
            raise HTTPException(422, "Only CT instances are supported.")
        arr = ds.pixel_array.astype(np.float32) * float(getattr(ds, "RescaleSlope", 1)) + float(getattr(ds, "RescaleIntercept", 0))
        sp = [float(v) for v in getattr(ds, "PixelSpacing", [1, 1])]
        out.append(Slice(int(getattr(ds, "InstanceNumber", len(out) + 1)), arr, sp[0] * sp[1], float(getattr(ds, "SliceThickness", 1))))
    return sorted(out, key=lambda s: s.instance)


def _classical_mask(hu: np.ndarray) -> np.ndarray:
    smooth = ndimage.uniform_filter(hu, size=3, mode="nearest")
    edge = (hu > 300) | (hu < -200)
    near_edge = ndimage.binary_dilation(edge, structure=np.ones((7, 7), bool))
    return (smooth >= 50) & (smooth <= 90) & ~near_edge


def _monai_masks(slices: list[Slice]) -> list[np.ndarray]:
    import torch  # noqa: PLC0415
    from monai.inferers import sliding_window_inference  # noqa: PLC0415

    vol = np.stack([s.hu for s in slices])                       # (D, H, W)
    x = torch.from_numpy(np.clip(vol, 0, 100) / 100.0)[None, None].float()
    with torch.no_grad():
        logits = sliding_window_inference(x, roi_size=(16, 128, 128), sw_batch_size=2, predictor=_model)
    pred = logits.argmax(1)[0].numpy().astype(bool)
    return [pred[i] for i in range(pred.shape[0])]


@app.get("/health")
def health():
    return {"status": "ok", "model": "monai-unet" if WEIGHTS else "classical",
            "lung_model": "monai-retinanet" if lung.BUNDLE else "classical", "service": "Medical.Net imaging AI"}


@app.post("/v1/ct/lung-nodules")
async def lung_nodules(files: list[UploadFile] = File(...)):
    blobs = [await f.read() for f in files]
    if not blobs:
        raise HTTPException(400, "No DICOM instances received.")
    try:
        return lung.detect(blobs)
    except ValueError as e:
        raise HTTPException(422, str(e)) from e


@app.post("/v1/ct/hemorrhage")
async def hemorrhage(files: list[UploadFile] = File(...), min_area_pixels: int = 12):
    slices = _read([await f.read() for f in files])
    if not slices:
        raise HTTPException(400, "No DICOM instances received.")
    use_model = _load_model() is not None
    masks = _monai_masks(slices) if use_model else [_classical_mask(s.hu) for s in slices]

    findings, volume_mm3 = [], 0.0
    for s, mask in zip(slices, masks):
        labels, n = ndimage.label(mask)  # 4-connectivity, as in the C# screener
        for k in range(1, n + 1):
            ys, xs = np.nonzero(labels == k)
            if len(xs) < min_area_pixels:
                continue
            findings.append({
                "slice": s.instance, "area_pixels": int(len(xs)), "area_mm2": round(len(xs) * s.px_area_mm2, 1),
                "mean_hu": round(float(s.hu[ys, xs].mean()), 1),
                "centroid_x": round(float(xs.mean()), 1), "centroid_y": round(float(ys.mean()), 1),
            })
            volume_mm3 += len(xs) * s.px_area_mm2 * s.thickness

    ml = round(volume_mm3 / 1000, 2)
    suspected = ml >= 0.1
    n_slices = len({f["slice"] for f in findings})
    return {
        "suspected": suspected,
        "volume_ml": ml,
        "slices_analyzed": len(slices),
        "method": "MONAI 3D UNet" if use_model else "HU-threshold 50-90 + bone suppression + connected components (Python)",
        "impression_en": f"Hyperdense collection suspicious for acute haemorrhage, ≈ {ml:.1f} mL across {n_slices} slice(s) — prioritise for radiologist review."
        if suspected else "No hyperdense collection above threshold.",
        "impression_id": f"Area hiperdens mencurigakan perdarahan akut, ≈ {ml:.1f} mL pada {n_slices} irisan — prioritaskan untuk dibaca radiolog."
        if suspected else "Tidak tampak area hiperdens di atas ambang.",
        "findings": findings,
    }
