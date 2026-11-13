# Medical.Net imaging-AI service

FastAPI service behind `Medical.AI.ImagingAiClient`. Made by Gravicode Studios, led by Kang Fadhil.

```bash
pip install -r requirements.txt
uvicorn app:app --port 8000
# or
docker build -t medicalnet-imaging-ai . && docker run -p 8000:8000 medicalnet-imaging-ai
```

| Endpoint | Request | Response |
|---|---|---|
| `GET /health` | — | `{"status":"ok","model":"classical"|"monai-unet","lung_model":"classical"|"monai-retinanet"}` |
| `POST /v1/ct/hemorrhage` | multipart `files` = DICOM instances of one non-contrast head CT | `suspected`, `volume_ml`, `slices_analyzed`, `method`, `impression_en`, `impression_id`, `findings[]` |
| `POST /v1/ct/lung-nodules` | multipart `files` = DICOM instances of one chest CT | `nodules[]` (`center_mm` LPS, `long_axis_mm`, `short_axis_mm`, `mean_diameter_mm`, `volume_mm3`, `mean_hu`, `lung_rads`, `score`), `category`, `lung_volume_ml`, `method`, `impression_en`, `impression_id` |

**Learned model:** install `torch` and `monai`, then set `MEDNET_MONAI_WEIGHTS=/models/ich_unet.pt` — a `state_dict` for `monai.networks.nets.UNet(spatial_dims=3, in_channels=1, out_channels=2, channels=(16,32,64,128), strides=(2,2,2), num_res_units=2)` trained on HU clipped to [0, 100] and scaled to [0, 1]. Without weights the service runs the classical pipeline, which gives the same results as the C# `HemorrhageScreener`.

**Lung nodules (published weights):** install `torch`, `torchvision` and `monai`, download the MONAI Model Zoo bundle and point the service at it:

```bash
python -m monai.bundle download lung_nodule_ct_detection --bundle_dir models   # RetinaNet, LUNA16 / LIDC-IDRI (Apache-2.0 code, CC-BY data)
export MEDNET_MONAI_LUNG_BUNDLE=models/lung_nodule_ct_detection
export MEDNET_LUNG_MIN_SCORE=0.5                                              # detection score threshold
```

The service reproduces the bundle's preprocessing (RAS orientation, 0.703 × 0.703 × 1.25 mm, HU −1024…300 → 0…1) and box post-processing, and returns boxes in DICOM patient coordinates with the ACR Lung-RADS v2022 category of the mean diameter. Use a GPU: on CPU one study takes tens of minutes. Without the bundle, a classical detector identical to the C# `LungNoduleDetector` runs. On Windows, download the bundle into a short path (the Hugging Face cache names exceed 260 characters).

The output is a triage aid that prioritises studies for radiologist review, not a diagnosis. Train and validate any model on local, ethically approved data.
