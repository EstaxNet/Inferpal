# PaddleOCR recognition models — Cyrillic, Japanese, Korean

Three text recognizers of [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) (Apache-2.0, see `LICENSE`). Inferpal reads a
scanned PDF with the engine embedded in `Sdcb.SimdPaddleOCR` (PP-OCRv6 tiny: Latin and Chinese); a page in another alphabet is
read again by these, the one most sure of its reading kept. Embedded in `Inferpal.Core` as resources: nothing is fetched at run
time.

| File | Model | Taken from | SHA-256 of the file taken |
|---|---|---|---|
| `eslav.onnx`, `eslav.txt` | `eslav_PP-OCRv5_mobile_rec` (Russian, Ukrainian, Belarusian, Bulgarian, English) | huggingface.co/monkt/paddleocr-onnx, `languages/eslav/` | `dc6bf0e855247decce214ba6dae5bc135fa0ad725a5918a7fcfb59fad6c9cdee` |
| `japan.onnx` | `japan_PP-OCRv3_mobile_rec` | huggingface.co/snowfluke/ppu-paddle-ocr-models, `recognition/multi/japan/v3/` | `45b0872d8e93ea6293c9f7a5aed101b4c73b94e3c5f9076ac6176bdd0506ef22` |
| `japan.txt` | its dictionary | PaddleOCR, `release/2.7/ppocr/utils/dict/japan_dict.txt` | — |
| `korean.onnx`, `korean.txt` | `korean_PP-OCRv5_mobile_rec` (Korean, English) | huggingface.co/monkt/paddleocr-onnx, `languages/korean/` | `322f140154c820fcb83c3d24cfe42c9ec70dd1a1834163306a7338136e4f1eaa` |

**Modified** (Apache-2.0 §4b) so that the pure C# interpreter of `Sdcb.SimdPaddleOCR` runs them; the weights are unchanged:
HardSwish and HardSigmoid rewritten with Mul, Add, Relu and Sub; Gemm rewritten as MatMul then Add; the Japanese model brought
from opset 9 to 14; the input fixed to 1×3×48×960 and every shape computation folded (onnxsim); Constant nodes turned into
initializers. The Cyrillic and Korean dictionaries as published; the Japanese one is PaddleOCR's own (the one published beside
the model starts with an empty line, which shifts every character). Rebuilt by the scripts in `conversion/` (Python, `onnx` and
`onnxsim`): `hardswish.py`, then `simplify.py <in> <out> 960`.
