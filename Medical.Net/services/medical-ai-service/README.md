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
| `GET /health` | — | `{"status":"ok","model":"classical"|"monai-unet"}` |
| `POST /v1/ct/hemorrhage` | multipart `files` = DICOM instances of one non-contrast head CT | `suspected`, `volume_ml`, `slices_analyzed`, `method`, `impression_en`, `impression_id`, `findings[]` |

**Learned model:** install `torch` and `monai`, then set `MEDNET_MONAI_WEIGHTS=/models/ich_unet.pt` — a `state_dict` for `monai.networks.nets.UNet(spatial_dims=3, in_channels=1, out_channels=2, channels=(16,32,64,128), strides=(2,2,2), num_res_units=2)` trained on HU clipped to [0, 100] and scaled to [0, 1]. Without weights the service runs the classical pipeline, which gives the same results as the C# `HemorrhageScreener`.

The output is a triage aid that prioritises studies for radiologist review, not a diagnosis. Train and validate any model on local, ethically approved data.
