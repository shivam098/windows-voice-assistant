// Usage: ModelConvert <hugging-face-model-dir> <mel_filters.npz> <output ggml .bin>
// See tools/ModelConvert/README.md for the full recipe (download, convert, quantize, verify).
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ModelConvert <hugging-face-model-dir> <mel_filters.npz> <output.bin>");
    return 1;
}

Converter.Run(args[0], args[1], args[2]);
return 0;
