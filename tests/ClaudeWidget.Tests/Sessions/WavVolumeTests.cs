using ClaudeWidget.Core.Sessions;

namespace ClaudeWidget.Tests.Sessions;

public class WavVolumeTests
{
    // Minimalny WAV: RIFF, opcjonalny fragment przed „fmt ”, fmt (16 albo 40 bajtów), data.
    private static byte[] Wav(ushort format, ushort bits, byte[] data, byte[]? extraChunk = null, ushort? subFormat = null)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
        if (extraChunk is not null)
        {
            w.Write("LIST"u8); w.Write(extraChunk.Length); w.Write(extraChunk);
            if (extraChunk.Length % 2 == 1) w.Write((byte)0);
        }
        var extensible = subFormat is not null;
        w.Write("fmt "u8); w.Write(extensible ? 40 : 16);
        w.Write(extensible ? (ushort)0xFFFE : format); w.Write((ushort)1); w.Write(44100);
        w.Write(44100 * bits / 8); w.Write((ushort)(bits / 8)); w.Write(bits);
        if (extensible)
        {
            w.Write((ushort)22); w.Write(bits); w.Write(0);
            w.Write(subFormat!.Value); w.Write(new byte[14]);
        }
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        return stream.ToArray();
    }

    // Fragment „data” jest w tych plikach ostatni, więc jego próbki to koniec pliku.
    private static byte[] DataOf(byte[] wav) => wav[(IndexOfData(wav) + 8)..];

    private static int IndexOfData(byte[] wav) => wav.AsSpan().IndexOf("data"u8);

    [Fact]
    public void Gain_follows_a_squared_curve()
    {
        Assert.Equal(1, WavVolume.Gain(100));
        Assert.Equal(0.25, WavVolume.Gain(50));
        Assert.Equal(0, WavVolume.Gain(0));
        Assert.Equal(1, WavVolume.Gain(300));
    }

    [Fact]
    public void Scales_16_bit_samples_including_negative_ones()
    {
        var data = new byte[8];
        BitConverter.TryWriteBytes(data.AsSpan(0), (short)20000);
        BitConverter.TryWriteBytes(data.AsSpan(2), (short)-20000);
        BitConverter.TryWriteBytes(data.AsSpan(4), short.MinValue);
        BitConverter.TryWriteBytes(data.AsSpan(6), (short)1);
        var wav = Wav(1, 16, data);

        var scaled = DataOf(WavVolume.Scale(wav, 0.5)!);

        Assert.Equal(10000, BitConverter.ToInt16(scaled, 0));
        Assert.Equal(-10000, BitConverter.ToInt16(scaled, 2));
        Assert.Equal(-16384, BitConverter.ToInt16(scaled, 4));
        Assert.Equal(0, BitConverter.ToInt16(scaled, 6)); // 0,5 zaokrąglone do parzystej
    }

    [Fact]
    public void Leaves_the_header_and_the_original_untouched()
    {
        var wav = Wav(1, 16, [0x10, 0x27]);
        var original = wav.ToArray();

        var scaled = WavVolume.Scale(wav, 0.25)!;

        Assert.Equal(original, wav);
        Assert.Equal(wav[..IndexOfData(wav)], scaled[..IndexOfData(wav)]);
        Assert.Equal(wav.Length, scaled.Length);
    }

    [Fact]
    public void Scales_8_bit_samples_around_the_unsigned_midpoint()
    {
        var scaled = DataOf(WavVolume.Scale(Wav(1, 8, [228, 28, 128]), 0.5)!);

        Assert.Equal(new byte[] { 178, 78, 128 }, scaled);
    }

    [Fact]
    public void Scales_24_bit_samples_with_sign_extension()
    {
        // -1 000 000 i +1 000 000 w 24 bitach little-endian
        byte[] data = [0xC0, 0xBD, 0xF0, 0x40, 0x42, 0x0F];

        var scaled = DataOf(WavVolume.Scale(Wav(1, 24, data), 0.5)!);

        int Read(int i) => (scaled[i] | scaled[i + 1] << 8 | scaled[i + 2] << 16) << 8 >> 8;
        Assert.Equal(-500_000, Read(0));
        Assert.Equal(500_000, Read(3));
    }

    [Fact]
    public void Scales_32_bit_integer_and_float_samples()
    {
        var ints = DataOf(WavVolume.Scale(Wav(1, 32, BitConverter.GetBytes(-2_000_000_000)), 0.5)!);
        var floats = DataOf(WavVolume.Scale(Wav(3, 32, BitConverter.GetBytes(0.8f)), 0.5)!);

        Assert.Equal(-1_000_000_000, BitConverter.ToInt32(ints));
        Assert.Equal(0.4f, BitConverter.ToSingle(floats), 5);
    }

    [Fact]
    public void Reads_the_real_format_from_an_extensible_header()
    {
        var wav = Wav(0, 16, BitConverter.GetBytes((short)1000), subFormat: 1);

        Assert.True(WavVolume.IsPlain(wav));
        Assert.Equal(500, BitConverter.ToInt16(DataOf(WavVolume.Scale(wav, 0.5)!)));
    }

    [Fact]
    public void Skips_other_chunks_including_odd_sized_ones()
    {
        var wav = Wav(1, 16, BitConverter.GetBytes((short)1000), extraChunk: [1, 2, 3]);

        Assert.Equal(500, BitConverter.ToInt16(DataOf(WavVolume.Scale(wav, 0.5)!)));
    }

    [Theory]
    [InlineData(2, 4)]   // ADPCM
    [InlineData(0x55, 16)] // MP3 w kontenerze WAV
    [InlineData(1, 12)]
    [InlineData(3, 64)]
    public void Compressed_or_unusual_formats_are_left_to_MediaPlayer(ushort format, ushort bits)
    {
        var wav = Wav(format, bits, new byte[16]);

        Assert.False(WavVolume.IsPlain(wav));
        Assert.Null(WavVolume.Scale(wav, 0.5));
    }

    [Fact]
    public void Garbage_and_truncated_files_are_rejected_without_throwing()
    {
        var wav = Wav(1, 16, new byte[16]);

        Assert.Null(WavVolume.Scale([], 0.5));
        Assert.Null(WavVolume.Scale("ID3 not a wav"u8, 0.5));
        Assert.Null(WavVolume.Scale(wav.AsSpan(0, 30), 0.5));
        for (var length = 0; length < wav.Length; length++) WavVolume.Scale(wav.AsSpan(0, length), 0.5);
    }

    [Fact]
    public void A_data_chunk_longer_than_the_file_is_rejected_so_PlaySound_never_reads_past_the_buffer()
    {
        var wav = Wav(1, 16, BitConverter.GetBytes((short)1000));
        BitConverter.TryWriteBytes(wav.AsSpan(IndexOfData(wav) + 4), 1_000_000);

        Assert.False(WavVolume.IsPlain(wav));
        Assert.Null(WavVolume.Scale(wav, 0.5));
    }

    [Fact]
    public void The_builtin_sounds_can_be_scaled()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ClaudeWidget", "Assets", "Sounds");
        foreach (var name in new[] { "need.wav", "done.wav" })
        {
            var wav = File.ReadAllBytes(Path.Combine(dir, name));
            Assert.True(WavVolume.IsPlain(wav), name);
            Assert.NotNull(WavVolume.Scale(wav, 0.3));
        }
    }
}
