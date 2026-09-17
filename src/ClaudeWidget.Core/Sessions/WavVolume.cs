namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// Głośność pliku WAV przez przeskalowanie próbek w pamięci. Dzięki temu WAV gra lekkie
/// PlaySound z winmm, które samo głośności nie ma — MediaPlayer ładuje odtwarzacz Windows
/// Media (+~40 MB, kilkanaście wątków, ~70 ms CPU na dźwięk) i jest potrzebny tylko dla MP3/WMA/M4A.
/// </summary>
public static class WavVolume
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>
    /// Wzmocnienie dla głośności w procentach. Słuch jest logarytmiczny: z krzywą kwadratową
    /// połowa suwaka brzmi mniej więcej o połowę ciszej, a nie prawie tak samo głośno.
    /// </summary>
    public static double Gain(int percent)
    {
        var fraction = Math.Clamp(percent, 0, 100) / 100.0;
        return fraction * fraction;
    }

    /// <summary>Nieskompresowany WAV, który <see cref="Scale"/> umie ściszyć, a PlaySound zagrać wprost.</summary>
    public static bool IsPlain(ReadOnlySpan<byte> wav) => TryFindFormat(wav, out _, out _, out _, out _);

    /// <summary>
    /// Kopia pliku z próbkami przemnożonymi przez <paramref name="gain"/> albo null, gdy to nie jest
    /// nieskompresowany WAV (PCM 8/16/24/32 bit albo float 32 bit) — taki trzeba zagrać inaczej.
    /// Wzmocnienie tylko ścisza (0–1), więc próbki nigdy się nie przesterują.
    /// </summary>
    public static byte[]? Scale(ReadOnlySpan<byte> wav, double gain)
    {
        if (!TryFindFormat(wav, out var format, out var bits, out var dataOffset, out var dataLength)) return null;
        gain = Math.Clamp(gain, 0, 1);
        var bytesPerSample = bits / 8;
        var result = wav.ToArray();
        var data = result.AsSpan(dataOffset, dataLength - dataLength % bytesPerSample);
        switch (format, bits)
        {
            case (FormatPcm, 8):
                // 8-bit PCM jest bez znaku, cisza to 128.
                for (var i = 0; i < data.Length; i++) data[i] = (byte)Math.Round((data[i] - 128) * gain + 128);
                break;
            case (FormatPcm, 16):
                for (var i = 0; i < data.Length; i += 2)
                {
                    var sample = (short)(data[i] | data[i + 1] << 8);
                    var scaled = (short)Math.Round(sample * gain);
                    data[i] = (byte)scaled;
                    data[i + 1] = (byte)(scaled >> 8);
                }
                break;
            case (FormatPcm, 24):
                for (var i = 0; i < data.Length; i += 3)
                {
                    var sample = (data[i] | data[i + 1] << 8 | data[i + 2] << 16) << 8 >> 8; // rozszerzenie znaku
                    var scaled = (int)Math.Round(sample * gain);
                    data[i] = (byte)scaled;
                    data[i + 1] = (byte)(scaled >> 8);
                    data[i + 2] = (byte)(scaled >> 16);
                }
                break;
            case (FormatPcm, 32):
                for (var i = 0; i < data.Length; i += 4)
                {
                    var sample = BitConverter.ToInt32(data.Slice(i, 4));
                    BitConverter.TryWriteBytes(data.Slice(i, 4), (int)Math.Round(sample * gain));
                }
                break;
            case (FormatFloat, 32):
                for (var i = 0; i < data.Length; i += 4)
                {
                    var sample = BitConverter.ToSingle(data.Slice(i, 4));
                    BitConverter.TryWriteBytes(data.Slice(i, 4), (float)(sample * gain));
                }
                break;
            default:
                return null;
        }
        return result;
    }

    private static bool TryFindFormat(ReadOnlySpan<byte> wav, out ushort format, out int bits, out int dataOffset, out int dataLength)
    {
        format = 0;
        bits = 0;
        dataOffset = 0;
        dataLength = 0;
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = BitConverter.ToUInt32(wav.Slice(offset + 4, 4));
            var body = offset + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > wav.Length) return false;
                format = BitConverter.ToUInt16(wav.Slice(body, 2));
                bits = BitConverter.ToUInt16(wav.Slice(body + 14, 2));
                // WAVE_FORMAT_EXTENSIBLE: prawdziwy format to pierwsze dwa bajty GUID-u podformatu.
                if (format == FormatExtensible)
                {
                    if (size < 40 || body + 40 > wav.Length) return false;
                    format = BitConverter.ToUInt16(wav.Slice(body + 24, 2));
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format == 0) return false; // „fmt ” musi być przed danymi
                // Ucięty plik odpada: PlaySound nie dostaje długości bufora i czytałby dalej wg nagłówka,
                // poza koniec danych — taki plik zagra MediaPlayer.
                if (size > (uint)(wav.Length - body)) return false;
                dataOffset = body;
                dataLength = (int)size;
                return (format is FormatPcm && bits is 8 or 16 or 24 or 32) || (format is FormatFloat && bits == 32);
            }
            // Fragmenty mają parzystą długość — nieparzysty rozmiar dopełnia bajt zerowy.
            var next = (long)body + size + (size & 1);
            if (next > wav.Length) return false;
            offset = (int)next;
        }
        return false;
    }
}
