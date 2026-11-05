# ModelConvert

Converts a Hugging Face Whisper checkpoint (`model.safetensors` + `config.json` + `vocab.json`) into the ggml format
that whisper.cpp / Whisper.net load. It is a C# port of whisper.cpp's
[`models/convert-h5-to-ggml.py`](https://github.com/ggml-org/whisper.cpp/blob/master/models/convert-h5-to-ggml.py), so no
Python or PyTorch is needed.

## Recipe: the Hindi + English dictation model

The app's `ggml-hinglish-swift-q8_0.bin` is
[Oriserve/Whisper-Hindi2Hinglish-Swift](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Swift) (Apache-2.0)
converted and quantized like this:

```powershell
# 1. Model files from Hugging Face (model.safetensors SHA-256 must match the LFS id on its file page)
$hf = 'https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Swift/resolve/main'
mkdir swift; foreach ($f in 'config.json','vocab.json','model.safetensors') { curl.exe -sSLfo "swift\$f" "$hf/$f" }

# 2. Mel filters from OpenAI's Whisper repository
curl.exe -sSLfo mel_filters.npz https://raw.githubusercontent.com/openai/whisper/main/whisper/assets/mel_filters.npz

# 3. Convert to ggml f16
dotnet run --project tools/ModelConvert -c Release -- swift mel_filters.npz ggml-hinglish-swift-f16.bin

# 4. Quantize with whisper.cpp v1.9.2's whisper-quantize.exe (whisper-bin-x64.zip from its GitHub release)
whisper-quantize.exe ggml-hinglish-swift-f16.bin ggml-hinglish-swift-q8_0.bin q8_0

# 5. Verify: SHA-256 0cf6e7077971d9aa43963b04bf764a47600e873f7e0f3f0f9742323560171245 (81,768,585 bytes)
Get-FileHash ggml-hinglish-swift-q8_0.bin
```

The result is attached to the
[`models-hinglish-swift-1`](https://github.com/shivam098/windows-voice-assistant/releases/tag/models-hinglish-swift-1)
release, which the app downloads (and checks against that hash) the first time Hindi + English dictation is switched on.

Check a converted model by transcribing the sample clips on its Hugging Face card: the output should match the card.

## Recipe: the accurate Hindi + English model

`ggml-hinglish-apex-q8_0.bin` is [Oriserve/Whisper-Hindi2Hinglish-Apex](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Apex)
(Apache-2.0, fine-tuned from Whisper large-v3-turbo), made with the same steps using `…/Whisper-Hindi2Hinglish-Apex/resolve/main`:

- `model.safetensors` SHA-256 `5560830fe8fa09b8cdf1b5926d0cc39a2586eadaad685d809e156432c4ebca80` (1,617,825,448 bytes)
- Result: SHA-256 `0b4324d2c1ad64f20883ee7fcd5d2bb0a8466287dc70d74bc47066200c28c719` (874,188,075 bytes), attached to the
  [`models-hinglish-apex-1`](https://github.com/shivam098/windows-voice-assistant/releases/tag/models-hinglish-apex-1) release.
