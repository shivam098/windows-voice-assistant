using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

/// <summary>
/// C# port of whisper.cpp/models/convert-h5-to-ggml.py: Hugging Face Whisper checkpoint (safetensors) -> ggml.
/// Mirrors the script exactly, including casting every tensor through f16 and keeping small ones as f32.
/// </summary>
internal static class Converter
{
    private static readonly Dictionary<string, string> ConvMap = new()
    {
        ["self_attn.k_proj"] = "attn.key",
        ["self_attn.q_proj"] = "attn.query",
        ["self_attn.v_proj"] = "attn.value",
        ["self_attn.out_proj"] = "attn.out",
        ["self_attn_layer_norm"] = "attn_ln",
        ["encoder_attn.q_proj"] = "cross_attn.query",
        ["encoder_attn.v_proj"] = "cross_attn.value",
        ["encoder_attn.out_proj"] = "cross_attn.out",
        ["encoder_attn_layer_norm"] = "cross_attn_ln",
        ["fc1"] = "mlp.0",
        ["fc2"] = "mlp.2",
        ["final_layer_norm"] = "mlp_ln",
        ["encoder.layer_norm.bias"] = "encoder.ln_post.bias",
        ["encoder.layer_norm.weight"] = "encoder.ln_post.weight",
        ["encoder.embed_positions.weight"] = "encoder.positional_embedding",
        ["decoder.layer_norm.bias"] = "decoder.ln.bias",
        ["decoder.layer_norm.weight"] = "decoder.ln.weight",
        ["decoder.embed_positions.weight"] = "decoder.positional_embedding",
        ["decoder.embed_tokens.weight"] = "decoder.token_embedding.weight",
        ["proj_out.weight"] = "decoder.proj.weight",
    };

    public static void Run(string hfDir, string melNpz, string outFile)
    {
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(hfDir, "config.json"))).RootElement;
        int Get(string key) => config.GetProperty(key).GetInt32();
        // n_text_ctx must match decoder.embed_positions; config "max_length" is often only a generation limit.
        var maxLength = Get("max_target_positions");
        var nMels = Get("num_mel_bins");
        var filters = ReadNpz(melNpz, $"mel_{nMels}.npy", out var filterShape);

        using var fout = new BinaryWriter(File.Create(outFile));
        fout.Write(0x67676d6c);
        foreach (var v in new[]
        {
            Get("vocab_size"), Get("max_source_positions"), Get("d_model"), Get("encoder_attention_heads"), Get("encoder_layers"),
            maxLength, Get("d_model"), Get("decoder_attention_heads"), Get("decoder_layers"), nMels, 1,
        })
        {
            fout.Write(v);
        }

        fout.Write(filterShape[0]);
        fout.Write(filterShape[1]);
        foreach (var f in filters)
        {
            fout.Write(f);
        }

        var byteDecoder = BytesToUnicode().ToDictionary(kv => kv.Value, kv => (byte)kv.Key);
        var vocab = JsonDocument.Parse(File.ReadAllText(Path.Combine(hfDir, "vocab.json"), Encoding.UTF8)).RootElement
            .EnumerateObject().Select(p => (Token: p.Name, Id: p.Value.GetInt32())).OrderBy(t => t.Id).ToList();
        fout.Write(vocab.Count);
        foreach (var (token, _) in vocab)
        {
            var bytes = token.EnumerateRunes().Select(r => byteDecoder[r.Value]).ToArray();
            fout.Write(bytes.Length);
            fout.Write(bytes);
        }

        var written = 0;
        foreach (var file in Directory.GetFiles(hfDir, "*.safetensors").OrderBy(f => f))
        {
            using var st = File.OpenRead(file);
            var headerLen = (long)ReadU64(st);
            var headerBytes = new byte[headerLen];
            st.ReadExactly(headerBytes);
            var dataStart = 8 + headerLen;
            var header = JsonDocument.Parse(headerBytes).RootElement;
            foreach (var t in header.EnumerateObject().Where(p => p.Name != "__metadata__"))
            {
                var src = t.Name;
                if (src == "proj_out.weight")
                {
                    continue;
                }

                var name = MapName(src);
                var dtype = t.Value.GetProperty("dtype").GetString()!;
                var shape = t.Value.GetProperty("shape").EnumerateArray().Select(e => e.GetInt64()).Where(d => d != 1).ToList();
                var offsets = t.Value.GetProperty("data_offsets").EnumerateArray().Select(e => e.GetInt64()).ToArray();
                var raw = new byte[offsets[1] - offsets[0]];
                st.Position = dataStart + offsets[0];
                st.ReadExactly(raw);
                var halves = ToHalf(raw, dtype);

                if (name is "encoder.conv1.bias" or "encoder.conv2.bias")
                {
                    shape = [shape[0], 1];
                }

                var nDims = shape.Count;
                var asF32 = nDims < 2 || name is "encoder.conv1.bias" or "encoder.conv2.bias" or "encoder.positional_embedding" or "decoder.positional_embedding";
                var nameBytes = Encoding.UTF8.GetBytes(name);
                fout.Write(nDims);
                fout.Write(nameBytes.Length);
                fout.Write(asF32 ? 0 : 1);
                for (var i = 0; i < nDims; i++)
                {
                    fout.Write((int)shape[nDims - 1 - i]);
                }

                fout.Write(nameBytes);
                if (asF32)
                {
                    var floats = Array.ConvertAll(halves, h => (float)h);
                    fout.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()));
                }
                else
                {
                    fout.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(halves.AsSpan()));
                }

                written++;
            }
        }

        Console.WriteLine($"wrote {written} tensors, {vocab.Count} tokens, n_mels={nMels} -> {outFile}");
    }

    private static string MapName(string src)
    {
        var parts = src.Split('.');
        var nn = (src.StartsWith("model.", StringComparison.Ordinal) ? parts[1..] : parts).ToList();
        if (nn.Count > 1 && nn[1] == "layers")
        {
            nn[1] = "blocks";
            var key = string.Join('.', nn[3..^1]);
            var mapped = key == "encoder_attn.k_proj" ? (nn[0] == "encoder" ? "attn.key" : "cross_attn.key") : ConvMap[key];
            return string.Join('.', nn[..3].Append(mapped).Append(nn[^1]));
        }

        var name = string.Join('.', nn);
        return ConvMap.GetValueOrDefault(name, name);
    }

    private static Half[] ToHalf(byte[] raw, string dtype) => dtype switch
    {
        "F16" => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(raw).ToArray(),
        "F32" => Array.ConvertAll(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw).ToArray(), f => (Half)f),
        "BF16" => Enumerable.Range(0, raw.Length / 2).Select(i => (Half)BitConverter.Int32BitsToSingle(BitConverter.ToUInt16(raw, i * 2) << 16)).ToArray(),
        _ => throw new NotSupportedException(dtype),
    };

    private static ulong ReadU64(Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    private static float[] ReadNpz(string npz, string entry, out int[] shape)
    {
        using var zip = ZipFile.OpenRead(npz);
        using var s = zip.GetEntry(entry)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var b = ms.ToArray();
        var major = b[6];
        var headerLen = major == 1 ? BitConverter.ToUInt16(b, 8) : (int)BitConverter.ToUInt32(b, 8);
        var headerStart = major == 1 ? 10 : 12;
        var header = Encoding.ASCII.GetString(b, headerStart, headerLen);
        if (!header.Contains("'<f4'") || header.Contains("'fortran_order': True"))
        {
            throw new InvalidDataException(header);
        }

        var dims = header[(header.IndexOf("'shape': (", StringComparison.Ordinal) + 10)..];
        shape = dims[..dims.IndexOf(')')].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x.Trim())).ToArray();
        var start = headerStart + headerLen;
        return Enumerable.Range(0, shape[0] * shape[1]).Select(i => BitConverter.ToSingle(b, start + (i * 4))).ToArray();
    }

    private static Dictionary<int, int> BytesToUnicode()
    {
        var bs = Enumerable.Range('!', '~' - '!' + 1).Concat(Enumerable.Range('¡', '¬' - '¡' + 1)).Concat(Enumerable.Range('®', 'ÿ' - '®' + 1)).ToList();
        var cs = bs.ToList();
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }

        return bs.Zip(cs).ToDictionary(p => p.First, p => p.Second);
    }
}
