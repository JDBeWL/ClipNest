using System.Runtime.InteropServices;

namespace ClipNest.Data;

/// <summary>
/// 向量以 float16 小端存入 BLOB。512 维占 1KB，比 float32 省一半空间。
/// </summary>
public static class VectorCodec
{
    public static byte[] Encode(IReadOnlyList<float> vector)
    {
        var bytes = new byte[vector.Count * 2];
        for (var i = 0; i < vector.Count; i++)
        {
            var half = BitConverter.HalfToInt16Bits((Half)vector[i]);
            bytes[i * 2] = (byte)(half & 0xFF);
            bytes[i * 2 + 1] = (byte)((half >> 8) & 0xFF);
        }

        return bytes;
    }

    public static float[] Decode(byte[] bytes, int dim)
    {
        var vector = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            vector[i] = (float)BitConverter.Int16BitsToHalf((short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)));
        }

        return vector;
    }
}
