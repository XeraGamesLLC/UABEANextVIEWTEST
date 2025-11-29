using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UABEANext4.Logic.Mesh;

/// <summary>
/// Information about a submesh within a combined mesh.
/// </summary>
public class SubMeshInfo
{
    public uint FirstByte { get; set; }
    public uint IndexCount { get; set; }
    public int Topology { get; set; } // 0 = Triangles, 1 = Quads, 2 = Lines, etc.
    public uint FirstVertex { get; set; }
    public uint VertexCount { get; set; }
}

// based on https://github.com/Perfare/AssetStudio/blob/master/AssetStudio/Classes/Mesh.cs
// this is not in a plugin due to being needed by the previewer, plus it's shared by multiple plugins
public class MeshObj
{
    public ushort[] Indices;
    public List<Channel> Channels;
    public float[] Vertices;
    public float[] Normals;
    public float[] Tangents;
    public float[] Colors;
    public float[][] UVs;

    // Submesh information for combined meshes
    public List<SubMeshInfo> SubMeshes;

    public MeshObj()
    {
        Indices = [];
        Channels = [];
        Vertices = [];
        Normals = [];
        Tangents = [];
        Colors = [];
        UVs = [];
        SubMeshes = [];
    }

    public MeshObj(AssetsFileInstance fileInst, AssetTypeValueField baseField, UnityVersion version)
    {
        Indices = [];
        Channels = [];
        Vertices = [];
        Normals = [];
        Tangents = [];
        Colors = [];
        UVs = [];
        SubMeshes = [];

        Read(fileInst, baseField, version);
    }

    private void Read(AssetsFileInstance fileInst, AssetTypeValueField baseField, UnityVersion version)
    {
        ReadSubMeshes(baseField);
        ReadIndicesData(baseField);
        ReadChannels(baseField);
        ReadVertexData(fileInst, baseField, version);
    }

    private void ReadSubMeshes(AssetTypeValueField baseField)
    {
        var subMeshesField = baseField["m_SubMeshes.Array"];
        if (subMeshesField.IsDummy) return;

        foreach (var subMesh in subMeshesField)
        {
            var info = new SubMeshInfo
            {
                FirstByte = subMesh["firstByte"].AsUInt,
                IndexCount = subMesh["indexCount"].AsUInt,
                Topology = subMesh["topology"].AsInt,
                FirstVertex = subMesh["firstVertex"].AsUInt,
                VertexCount = subMesh["vertexCount"].AsUInt
            };
            SubMeshes.Add(info);
        }
    }

    private void ReadIndicesData(AssetTypeValueField baseField)
    {
        var indicesField = baseField["m_IndexBuffer.Array"].AsByteArray;

        // Check if indices are 32-bit (for large meshes) by looking at index format
        var indexFormatField = baseField["m_IndexFormat"];
        bool is32Bit = !indexFormatField.IsDummy && indexFormatField.AsInt == 1;

        if (is32Bit)
        {
            // 32-bit indices - convert to ushort (may lose precision for very large meshes)
            var uintArray = new uint[indicesField.Length / 4];
            for (var i = 0; i < indicesField.Length; i += 4)
            {
                uintArray[i / 4] = (uint)(indicesField[i] | indicesField[i + 1] << 8 |
                                         indicesField[i + 2] << 16 | indicesField[i + 3] << 24);
            }
            // Convert to ushort (clamping to prevent overflow)
            Indices = new ushort[uintArray.Length];
            for (var i = 0; i < uintArray.Length; i++)
            {
                Indices[i] = (ushort)Math.Min(uintArray[i], ushort.MaxValue);
            }
        }
        else
        {
            // 16-bit indices (standard)
            var ushortArray = new ushort[indicesField.Length / 2];
            for (var i = 0; i < indicesField.Length; i += 2)
            {
                ushortArray[i / 2] = (ushort)(indicesField[i + 1] << 8 | indicesField[i]);
            }
            Indices = ushortArray;
        }

        // If we have submeshes, filter to only triangle submeshes
        if (SubMeshes.Count > 0)
        {
            FilterToTriangleSubmeshes(is32Bit);
        }
    }

    private void FilterToTriangleSubmeshes(bool is32Bit)
    {
        // Topology: 0 = Triangles, 1 = Quads, 2 = Lines, 3 = LineStrip, 4 = Points
        // We only want triangles (topology 0)
        var triangleIndices = new List<ushort>();
        int bytesPerIndex = is32Bit ? 4 : 2;

        foreach (var subMesh in SubMeshes)
        {
            // Only include triangle submeshes (topology 0)
            if (subMesh.Topology == 0)
            {
                int startIndex = (int)(subMesh.FirstByte / bytesPerIndex);
                int count = (int)subMesh.IndexCount;

                for (int i = 0; i < count && startIndex + i < Indices.Length; i++)
                {
                    triangleIndices.Add(Indices[startIndex + i]);
                }
            }
        }

        if (triangleIndices.Count > 0)
        {
            Indices = triangleIndices.ToArray();
        }
    }

    private void ReadChannels(AssetTypeValueField baseField)
    {
        var channelFields = baseField["m_VertexData"]["m_Channels.Array"];
        var channels = new List<Channel>();
        foreach (var channelField in channelFields)
        {
            channels.Add(new Channel(channelField));
        }
        Channels = channels;
    }

    private List<int> GetStreamLengths(UnityVersion version)
    {
        var streamLengths = new List<int>();
        var streamCount = Channels.Max(c => c.stream) + 1;
        for (var i = 0; i < streamCount; i++)
        {
            var maxEndOffset = 0;
            for (var j = 0; j < Channels.Count; j++)
            {
                if (Channels[j].stream == i)
                {
                    var channel = Channels[j];
                    var size = GetFormatSize(ToVertexFormatV2(channel.format, version));
                    var endOffset = channel.offset + (channel.dimension & 0xf) * size;
                    maxEndOffset = endOffset > maxEndOffset ? endOffset : maxEndOffset;
                }
            }
            streamLengths.Add(maxEndOffset);
        }

        return streamLengths;
    }

    private static int GetFormatSize(VertexFormatV2 format)
    {
        return format switch
        {
            VertexFormatV2.Float => 4,
            VertexFormatV2.Float16 => 2,
            VertexFormatV2.UNorm8 => 1,
            VertexFormatV2.SNorm8 => 1,
            VertexFormatV2.UNorm16 => 2,
            VertexFormatV2.SNorm16 => 2,
            VertexFormatV2.UInt8 => 1,
            VertexFormatV2.SInt8 => 1,
            VertexFormatV2.UInt16 => 2,
            VertexFormatV2.SInt16 => 2,
            VertexFormatV2.UInt32 => 4,
            VertexFormatV2.SInt32 => 4,
            _ => throw new Exception($"Unknown format {format}")
        };
    }

    private static VertexFormatV2 ToVertexFormatV2(int format, UnityVersion version)
    {
        if (version.major >= 2019)
        {
            return (VertexFormatV2)format;
        }
        else if (version.major >= 2017)
        {
            return (VertexFormatV1)format switch
            {
                VertexFormatV1.Float => VertexFormatV2.Float,
                VertexFormatV1.Float16 => VertexFormatV2.Float16,
                VertexFormatV1.Color or
                VertexFormatV1.UNorm8 => VertexFormatV2.UNorm8,
                VertexFormatV1.SNorm8 => VertexFormatV2.SNorm8,
                VertexFormatV1.UNorm16 => VertexFormatV2.UNorm16,
                VertexFormatV1.SNorm16 => VertexFormatV2.SNorm16,
                VertexFormatV1.UInt8 => VertexFormatV2.UInt8,
                VertexFormatV1.SInt8 => VertexFormatV2.SInt8,
                VertexFormatV1.UInt16 => VertexFormatV2.UInt16,
                VertexFormatV1.SInt16 => VertexFormatV2.SInt16,
                VertexFormatV1.UInt32 => VertexFormatV2.UInt32,
                VertexFormatV1.SInt32 => VertexFormatV2.SInt32,
                _ => throw new Exception($"Unknown format {format}")
            };
        }
        else
        {
            return (VertexChannelFormat)format switch
            {
                VertexChannelFormat.Float => VertexFormatV2.Float,
                VertexChannelFormat.Float16 => VertexFormatV2.Float16,
                VertexChannelFormat.Color => VertexFormatV2.UNorm8,
                VertexChannelFormat.Byte => VertexFormatV2.UInt8,
                VertexChannelFormat.UInt32 => VertexFormatV2.UInt32,
                _ => throw new Exception($"Unknown format {format}")
            };
        }
    }

    private static bool IsFormatInt(VertexFormatV2 format)
    {
        return format switch
        {
            VertexFormatV2.UInt8 => true,
            VertexFormatV2.SInt8 => true,
            VertexFormatV2.UInt16 => true,
            VertexFormatV2.SInt16 => true,
            VertexFormatV2.UInt32 => true,
            VertexFormatV2.SInt32 => true,
            _ => false
        };
    }

    private static byte[] GetVertexData(AssetsFileInstance fileInst, AssetTypeValueField baseField)
    {
        var usesStreamData = false;
        var offset = 0U;
        var size = 0U;
        var path = string.Empty;

        var streamData = baseField["m_StreamData"];
        if (!streamData.IsDummy)
        {
            offset = streamData["offset"].AsUInt;
            size = streamData["size"].AsUInt;
            path = streamData["path"].AsString;
            usesStreamData = size > 0 && path != string.Empty;
        }

        if (usesStreamData)
        {
            if (fileInst.parentBundle != null && path.StartsWith("archive:/"))
            {
                var archiveTrimmedPath = path;
                if (archiveTrimmedPath.StartsWith("archive:/"))
                    archiveTrimmedPath = archiveTrimmedPath.Substring(9);

                archiveTrimmedPath = Path.GetFileName(archiveTrimmedPath);

                AssetBundleFile bundle = fileInst.parentBundle.file;

                AssetsFileReader reader = bundle.DataReader;
                List<AssetBundleDirectoryInfo> dirInf = bundle.BlockAndDirInfo.DirectoryInfos;
                for (int i = 0; i < dirInf.Count; i++)
                {
                    AssetBundleDirectoryInfo info = dirInf[i];
                    if (info.Name == archiveTrimmedPath)
                    {
                        byte[] meshData;
                        lock (bundle.DataReader)
                        {
                            reader.Position = info.Offset + offset;
                            meshData = reader.ReadBytes((int)size);
                        }
                        return meshData;
                    }
                }
            }

            var rootPath = Path.GetDirectoryName(fileInst.path)
                ?? throw new FileNotFoundException("Can't find resS for mesh");

            var fixedStreamPath = path;

            // user may have extracted serialized file and resS from bundle to disk
            var bundleInst = fileInst.parentBundle;
            if (bundleInst == null && path.StartsWith("archive:/"))
            {
                fixedStreamPath = Path.GetFileName(fixedStreamPath);
            }
            if (!Path.IsPathRooted(fixedStreamPath) && rootPath != null)
            {
                fixedStreamPath = Path.Combine(rootPath, fixedStreamPath);
            }

            if (File.Exists(fixedStreamPath))
            {
                var stream = File.OpenRead(fixedStreamPath);
                stream.Position = offset;
                var data = new byte[size];
                stream.Read(data, 0, (int)size);
                return data;
            }
            // we still haven't found it yet. maybe a data.unity3d bundle?
            // in this case, we won't have the archive:/ prefix, so use the original path
            else if (bundleInst != null && TryGetBundleFileIndex(bundleInst.file, path, out var fileIdx))
            {
                var bundle = bundleInst.file;
                bundle.GetFileRange(fileIdx, out var bunOffset, out var _);
                var reader = bundle.DataReader;
                reader.Position = bunOffset + offset;
                return reader.ReadBytes((int)size);
            }
            else
            {
                throw new FileNotFoundException("Can't find resS for mesh");
            }
        }
        else
        {
            return baseField["m_VertexData"]["m_DataSize"].AsByteArray;
        }
    }

    private static bool TryGetBundleFileIndex(AssetBundleFile bunFile, string name, out int dirInf)
    {
        dirInf = bunFile.BlockAndDirInfo.DirectoryInfos.FindIndex(i => i.Name == name);
        return dirInf != -1;
    }

    private void ReadVertexData(AssetsFileInstance fileInst, AssetTypeValueField baseField, UnityVersion version)
    {
        var vertexCount = baseField["m_VertexData"]["m_VertexCount"].AsInt;
        var vertexData = GetVertexData(fileInst, baseField);
        var streamLengths = GetStreamLengths(version);
        var startPos = 0;
        for (var strIdx = 0; strIdx < streamLengths.Count; strIdx++)
        {
            var streamLength = streamLengths[strIdx];
            for (var chnIdx = 0; chnIdx < Channels.Count; chnIdx++)
            {
                var channel = Channels[chnIdx];
                if (channel.stream != strIdx)
                    continue;

                var dimension = channel.dimension & 0xf;
                var vertexFormat = ToVertexFormatV2(channel.format, version);
                var offset = channel.offset + startPos;
                var size = GetFormatSize(vertexFormat) * dimension;
                var data = new byte[size * vertexCount];
                for (var i = 0; i < vertexCount; i++)
                {
                    Buffer.BlockCopy(vertexData, offset + i * streamLength, data, i * size, size);
                }

                int[]? intItems = null;
                float[]? floatItems = null;
                if (IsFormatInt(vertexFormat))
                    intItems = ConvertIntArray(data, dimension, vertexFormat);
                else
                    floatItems = ConvertFloatArray(data, dimension, vertexFormat);

                SetCorrectArray(intItems!, floatItems!, chnIdx, version);
            }
            startPos += streamLengths[strIdx] * vertexCount;
        }
    }

    private void SetCorrectArray(int[] intItems, float[] floatItems, int channelIndex, UnityVersion version)
    {
        if (version.major >= 2018)
        {
            var channelType = (ChannelTypeV3)channelIndex;
            switch (channelType)
            {
                case ChannelTypeV3.Vertex: Vertices = floatItems; break;
                case ChannelTypeV3.Normal: Normals = floatItems; break;
                case ChannelTypeV3.Tangent: Tangents = floatItems; break;
                case ChannelTypeV3.Color: Colors = floatItems; break;
                case ChannelTypeV3.TexCoord0:
                case ChannelTypeV3.TexCoord1:
                case ChannelTypeV3.TexCoord2:
                case ChannelTypeV3.TexCoord3:
                case ChannelTypeV3.TexCoord4:
                case ChannelTypeV3.TexCoord5:
                case ChannelTypeV3.TexCoord6:
                case ChannelTypeV3.TexCoord7:
                {
                    if (UVs.Length == 0)
                    {
                        UVs = new float[8][];
                    }
                    UVs[(int)channelType - (int)ChannelTypeV3.TexCoord0] = floatItems;
                    break;
                }
                case ChannelTypeV3.BlendWeight:
                case ChannelTypeV3.BlendIndices:
                {
                    // ignore for now
                    break;
                }
            }
        }
        else // if (version.major >= 5)
        {
            var channelType = (ChannelTypeV2)channelIndex;
            switch (channelType)
            {
                case ChannelTypeV2.Vertex: Vertices = floatItems; break;
                case ChannelTypeV2.Normal: Normals = floatItems; break;
                case ChannelTypeV2.Color: Colors = floatItems; break;
                case ChannelTypeV2.TexCoord0:
                case ChannelTypeV2.TexCoord1:
                case ChannelTypeV2.TexCoord2:
                case ChannelTypeV2.TexCoord3:
                {
                    if (UVs.Length == 0)
                    {
                        UVs = new float[4][];
                    }
                    UVs[(int)channelType - (int)ChannelTypeV2.TexCoord0] = floatItems;
                    break;
                }
                case ChannelTypeV2.Tangent: Tangents = floatItems; break;
            }
        }
    }

    private static int[] ConvertIntArray(byte[] data, int dims, VertexFormatV2 format)
    {
        var size = GetFormatSize(format);
        var count = data.Length / size;
        var items = new int[count];
        switch (format)
        {
            case VertexFormatV2.UInt8:
            case VertexFormatV2.SInt8:
            {
                for (var i = 0; i < count; i++)
                {
                    items[i] = data[i];
                }
                return items;
            }
            case VertexFormatV2.UInt16:
            case VertexFormatV2.SInt16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = data[src] | data[src + 1] << 8;
                }
                return items;
            }
            case VertexFormatV2.UInt32:
            case VertexFormatV2.SInt32:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 4)
                {
                    items[i] = data[src] | data[src + 1] << 8 | data[src + 2] << 16 | data[src + 3] << 24;
                }
                return items;
            }
            default:
                throw new Exception($"Unknown format {format}");
        }
    }

    private static float[] ConvertFloatArray(byte[] data, int dims, VertexFormatV2 format)
    {
        var size = GetFormatSize(format);
        var count = data.Length / size;
        var items = new float[count];
        switch (format)
        {
            case VertexFormatV2.Float:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 4)
                {
                    items[i] = BitConverter.ToSingle(data, src);
                }
                return items;
            }
            case VertexFormatV2.Float16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = (float)BitConverter.UInt16BitsToHalf((ushort)(data[src] | data[src + 1] << 8));
                }
                return items;
            }
            case VertexFormatV2.UNorm8:
            {
                for (var i = 0; i < count; i++)
                {
                    items[i] = data[i] / 255f;
                }
                return items;
            }
            case VertexFormatV2.SNorm8:
            {
                for (var i = 0; i < count; i++)
                {
                    items[i] = Math.Max((sbyte)data[i] / 127f, -1f);
                }
                return items;
            }
            case VertexFormatV2.UNorm16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = (data[src] | data[src + 1] << 8) / 65535f;
                }
                return items;
            }
            case VertexFormatV2.SNorm16:
            {
                var src = 0;
                for (var i = 0; i < count; i++, src += 2)
                {
                    items[i] = Math.Max((short)(data[src] | data[src + 1] << 8) / 32767f, -1f);
                }
                return items;
            }
            default:
                throw new Exception($"Unknown format {format}");
        }
    }
}
