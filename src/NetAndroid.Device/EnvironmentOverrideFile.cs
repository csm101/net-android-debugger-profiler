using System.Text;

namespace NetAndroid.Device;

/// <summary>
/// Reader/writer for the .NET for Android "environment override file"
/// (<c>files/.__override__/&lt;abi&gt;/environment</c>), loaded by the Debug
/// flavor of libmonodroid after the baked environment. Format (no newlines):
/// <c>0x%08X\0</c> name width, <c>0x%08X\0</c> value width (both including the
/// terminating NUL), then records of name NUL-padded to name width followed by
/// value NUL-padded to value width. Source: dotnet/android
/// src/native/mono/runtime-base/android-system.cc.
/// </summary>
public static class EnvironmentOverrideFile
{
    private const int HeaderSize = 22; // two "0x%08X\0" fields of 11 bytes

    public static IReadOnlyList<KeyValuePair<string, string>> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("environment override file: header too short");
        int nameWidth = ReadWidth(data, 0);
        int valueWidth = ReadWidth(data, 11);
        int recordWidth = nameWidth + valueWidth;
        if (recordWidth <= 0 || (data.Length - HeaderSize) % recordWidth != 0)
            throw new InvalidDataException("environment override file: invalid data size");
        var list = new List<KeyValuePair<string, string>>();
        for (int off = HeaderSize; off + recordWidth <= data.Length; off += recordWidth)
        {
            string name = ReadZ(data.Slice(off, nameWidth));
            string value = ReadZ(data.Slice(off + nameWidth, valueWidth));
            if (name.Length == 0) throw new InvalidDataException($"environment override file: empty name at offset {off}");
            list.Add(new KeyValuePair<string, string>(name, value));
        }
        return list;
    }

    public static byte[] Serialize(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var vars = variables.ToList();
        int nameWidth = (vars.Count == 0 ? 0 : vars.Max(v => Encoding.UTF8.GetByteCount(v.Key))) + 1;
        int valueWidth = (vars.Count == 0 ? 0 : vars.Max(v => Encoding.UTF8.GetByteCount(v.Value))) + 1;
        using var ms = new MemoryStream();
        WriteWidth(ms, nameWidth);
        WriteWidth(ms, valueWidth);
        foreach (var v in vars)
        {
            WritePadded(ms, v.Key, nameWidth);
            WritePadded(ms, v.Value, valueWidth);
        }
        return ms.ToArray();
    }

    /// <summary>Return a copy of <paramref name="existing"/> with <paramref name="updates"/> applied (null value removes the variable).</summary>
    public static List<KeyValuePair<string, string>> Merge(IEnumerable<KeyValuePair<string, string>> existing, IEnumerable<KeyValuePair<string, string?>> updates)
    {
        var result = existing.ToList();
        foreach (var u in updates)
        {
            result.RemoveAll(v => v.Key == u.Key);
            if (u.Value is not null) result.Add(new KeyValuePair<string, string>(u.Key, u.Value));
        }
        return result;
    }

    private static int ReadWidth(ReadOnlySpan<byte> data, int offset)
    {
        string s = Encoding.ASCII.GetString(data.Slice(offset, 10));
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || data[offset + 10] != 0)
            throw new InvalidDataException("environment override file: malformed header");
        return Convert.ToInt32(s[2..], 16);
    }

    private static void WriteWidth(Stream s, int width)
    {
        var bytes = Encoding.ASCII.GetBytes($"0x{width:X8}");
        s.Write(bytes);
        s.WriteByte(0);
    }

    private static void WritePadded(Stream s, string text, int width)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes);
        for (int i = bytes.Length; i < width; i++) s.WriteByte(0);
    }

    private static string ReadZ(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }
}
