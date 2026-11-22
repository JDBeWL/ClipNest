using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// 读取二维 .npy 数组，支持小端 float32、float16 与 float64。
/// RAM++ 的标签嵌入以 float32 存储，直接读入连续内存可避免额外的中间副本。
/// </summary>
internal static partial class NpyFloatArray
{
    public static (float[] Data, int Rows, int Columns) Load(string path)
    {
        using var stream = File.OpenRead(path);

        var magic = new byte[6];
        stream.ReadExactly(magic);
        if (magic[0] != 0x93 || Encoding.ASCII.GetString(magic, 1, 5) != "NUMPY")
        {
            throw new InvalidDataException($"不是有效的 .npy 文件：{path}");
        }

        var version = new byte[2];
        stream.ReadExactly(version);

        var headerLength = version[0] == 1
            ? ReadUInt16(stream)
            : ReadUInt32(stream);
        var headerBytes = new byte[headerLength];
        stream.ReadExactly(headerBytes);
        var header = Encoding.ASCII.GetString(headerBytes);

        var descr = ExtractString(header, "descr");
        var shape = ExtractShape(header);
        if (shape.Length != 2)
        {
            throw new InvalidDataException($"只支持二维数组：{path}");
        }

        if (header.Contains("'fortran_order': True", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"不支持 Fortran 顺序的数组：{path}");
        }

        var rows = shape[0];
        var columns = shape[1];
        var count = checked(rows * columns);
        var data = new float[count];

        switch (descr)
        {
            case "<f4":
                stream.ReadExactly(MemoryMarshal.AsBytes(data.AsSpan()));
                break;
            case "<f2":
                ReadFloat16(stream, data);
                break;
            case "<f8":
                ReadFloat64(stream, data);
                break;
            default:
                throw new InvalidDataException($"不支持的数组类型 {descr}：{path}");
        }

        return (data, rows, columns);
    }

    private static void ReadFloat16(Stream stream, float[] target)
    {
        var buffer = new byte[2];
        for (var i = 0; i < target.Length; i++)
        {
            stream.ReadExactly(buffer);
            target[i] = (float)BitConverter.ToHalf(buffer);
        }
    }

    private static void ReadFloat64(Stream stream, float[] target)
    {
        var buffer = new byte[8];
        for (var i = 0; i < target.Length; i++)
        {
            stream.ReadExactly(buffer);
            target[i] = (float)BitConverter.ToDouble(buffer, 0);
        }
    }

    private static int ReadUInt16(Stream stream)
    {
        var buffer = new byte[2];
        stream.ReadExactly(buffer);
        return BitConverter.ToUInt16(buffer);
    }

    private static int ReadUInt32(Stream stream)
    {
        var buffer = new byte[4];
        stream.ReadExactly(buffer);
        return checked((int)BitConverter.ToUInt32(buffer));
    }

    private static string ExtractString(string header, string key)
    {
        var match = StringValueRegex().Match(header);
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidDataException($"缺少字段 {key}");
    }

    private static int[] ExtractShape(string header)
    {
        var match = ShapeRegex().Match(header);
        if (!match.Success)
        {
            throw new InvalidDataException("缺少 shape 字段");
        }

        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
    }

    [GeneratedRegex("'descr'\\s*:\\s*'([^']+)'")]
    private static partial Regex StringValueRegex();

    [GeneratedRegex("'shape'\\s*:\\s*\\(([^)]*)\\)")]
    private static partial Regex ShapeRegex();
}
