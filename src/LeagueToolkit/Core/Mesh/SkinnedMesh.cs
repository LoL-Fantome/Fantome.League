﻿using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Helpers.Exceptions;
using LeagueToolkit.Helpers.Extensions;
using LeagueToolkit.Helpers.Structures;
using LeagueToolkit.IO.SimpleSkinFile;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LeagueToolkit.Core.Mesh
{
    public sealed class SkinnedMesh : IDisposable
    {
        public Box AABB { get; private set; }
        public R3DSphere BoundingSphere { get; private set; }

        public IReadOnlyList<SkinnedMeshRange> Ranges => this._ranges;
        private readonly SkinnedMeshRange[] _ranges;

        public IVertexBufferView VerticesView => this._vertexBuffer;
        public ReadOnlyMemory<ushort> IndicesView => this._indexBuffer.Memory;

        private readonly VertexBuffer _vertexBuffer;
        private readonly MemoryOwner<ushort> _indexBuffer;

        public bool IsDisposed { get; private set; }

        internal SkinnedMesh(
            IEnumerable<SkinnedMeshRange> ranges,
            VertexBuffer vertexBuffer,
            MemoryOwner<ushort> indexBuffer
        )
        {
            this._ranges = ranges.ToArray();
            this._vertexBuffer = vertexBuffer;
            this._indexBuffer = indexBuffer;

            this.AABB = Box.FromVertices(vertexBuffer.GetAccessor(ElementName.Position).AsVector3Array());
            this.BoundingSphere = this.AABB.GetBoundingSphere();
        }

        public static SkinnedMesh ReadFromSimpleSkin(string fileLocation) =>
            ReadFromSimpleSkin(File.OpenRead(fileLocation));

        public static SkinnedMesh ReadFromSimpleSkin(Stream stream, bool leaveOpen = false)
        {
            using BinaryReader br = new(stream, Encoding.UTF8, leaveOpen);

            uint magic = br.ReadUInt32();
            if (magic != 0x00112233)
                throw new InvalidFileSignatureException();

            ushort major = br.ReadUInt16();
            ushort minor = br.ReadUInt16();
            if (major is not (0 or 2 or 4) && minor is not 1)
                throw new UnsupportedFileVersionException();

            int indexCount = 0;
            int vertexCount = 0;
            VertexBufferDescription vertexBufferDescription = SkinnedMeshVertex.BASIC;
            Box boundingBox = new();
            R3DSphere boundingSphere = R3DSphere.Infinite;
            SkinnedMeshRange[] ranges;
            if (major is 0)
            {
                indexCount = br.ReadInt32();
                vertexCount = br.ReadInt32();

                ranges = new SkinnedMeshRange[] { new("Base", 0, vertexCount, 0, indexCount) };
            }
            else
            {
                uint rangeCount = br.ReadUInt32();
                ranges = new SkinnedMeshRange[rangeCount];
                for (int i = 0; i < rangeCount; i++)
                {
                    ranges[i] = SkinnedMeshRange.ReadFromSimpleSkin(br);
                }

                if (major is 4)
                {
                    uint flags = br.ReadUInt32();
                }

                indexCount = br.ReadInt32();
                vertexCount = br.ReadInt32();

                if (major is 4)
                {
                    uint vertexSize = br.ReadUInt32();
                    SkinnedMeshVertexType vertexType = (SkinnedMeshVertexType)br.ReadUInt32();
                    vertexBufferDescription = (vertexSize, vertexType) switch
                    {
                        (52, SkinnedMeshVertexType.Basic) => SkinnedMeshVertex.BASIC,
                        (56, SkinnedMeshVertexType.Color) => SkinnedMeshVertex.COLOR,
                        (72, SkinnedMeshVertexType.Tangent) => SkinnedMeshVertex.TANGENT,
                        _
                            => throw new InvalidOperationException(
                                $"Vertex size: {vertexSize} is not correct for {vertexType}, "
                                    + $"expected: {GetDescriptionForVertexType(vertexType).GetVertexSize()}"
                            )
                    };

                    boundingBox = br.ReadBox();
                    boundingSphere = new(br);
                }
            }

            MemoryOwner<ushort> indexBufferOwner = MemoryOwner<ushort>.Allocate(indexCount);
            MemoryOwner<byte> vertexBufferOwner = MemoryOwner<byte>.Allocate(
                vertexBufferDescription.GetVertexSize() * vertexCount
            );

            // Read index buffer
            Span<byte> indexBuffer = indexBufferOwner.Span.Cast<ushort, byte>();
            int indexBufferBytesRead = br.Read(indexBuffer);
            if (indexBufferBytesRead != indexBuffer.Length)
                throw new IOException(
                    $"Failed to read index buffer: {nameof(indexBuffer.Length)}: {indexBuffer.Length}"
                        + $" {nameof(indexBufferBytesRead)}: {indexBufferBytesRead}"
                );

            // Read vertex buffer
            int vertexBufferBytesRead = br.Read(vertexBufferOwner.Span);
            if (vertexBufferBytesRead != vertexBufferOwner.Length)
                throw new IOException(
                    $"Failed to read vertex buffer: {nameof(vertexBufferOwner.Length)}: {vertexBufferOwner.Length}"
                        + $" {nameof(vertexBufferBytesRead)}: {vertexBufferBytesRead}"
                );

            VertexBuffer vertexBuffer = VertexBuffer.Create(
                vertexBufferDescription.Usage,
                vertexBufferDescription.Elements,
                vertexBufferOwner
            );

            return new(ranges, vertexBuffer, indexBufferOwner);
        }

        public void WriteSimpleSkin(string fileLocation) => WriteSimpleSkin(File.Create(fileLocation));

        public void WriteSimpleSkin(Stream stream, bool leaveOpen = false)
        {
            using BinaryWriter bw = new(stream, Encoding.UTF8, leaveOpen);

            bw.Write(0x00112233);
            bw.Write((ushort)4);
            bw.Write((ushort)1);
            bw.Write(this.Ranges.Count);

            foreach (SkinnedMeshRange range in this.Ranges)
                range.WriteToSimpleSkin(bw);

            bw.Write((uint)0); // Flags
            bw.Write(this._indexBuffer.Length);
            bw.Write(this._vertexBuffer.VertexCount);
            bw.Write(this._vertexBuffer.VertexStride);
            bw.Write((int)GetVertexTypeForDescription(this._vertexBuffer.Description));

            bw.WriteBox(this.AABB);
            this.BoundingSphere.Write(bw);

            bw.Write(this._indexBuffer.Span.Cast<ushort, byte>());
            bw.Write(this._vertexBuffer.View.Span);

            Span<byte> endTab = stackalloc byte[12];
            endTab.Clear();
            bw.Write(endTab);
        }

        private static VertexBufferDescription GetDescriptionForVertexType(SkinnedMeshVertexType vertexType) =>
            vertexType switch
            {
                SkinnedMeshVertexType.Basic => SkinnedMeshVertex.BASIC,
                SkinnedMeshVertexType.Color => SkinnedMeshVertex.COLOR,
                SkinnedMeshVertexType.Tangent => SkinnedMeshVertex.TANGENT,
                _ => throw new NotImplementedException($"{vertexType} is not a valid {nameof(SkinnedMeshVertexType)}")
            };

        private static SkinnedMeshVertexType GetVertexTypeForDescription(VertexBufferDescription description)
        {
            return description switch
            {
                _ when description == SkinnedMeshVertex.BASIC => SkinnedMeshVertexType.Basic,
                _ when description == SkinnedMeshVertex.COLOR => SkinnedMeshVertexType.Color,
                _ when description == SkinnedMeshVertex.TANGENT => SkinnedMeshVertexType.Tangent,
                _
                    => throw new NotImplementedException(
                        $"The specified {nameof(VertexBufferDescription)} is not a valid skinned mesh vertex description"
                    )
            };
        }

        private void Dispose(bool disposing)
        {
            if (!this.IsDisposed)
            {
                if (disposing)
                {
                    this._vertexBuffer?.Dispose();
                    this._indexBuffer?.Dispose();
                }

                this.IsDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    public static class SkinnedMeshVertex
    {
        public static readonly VertexBufferDescription BASIC =
            new(
                VertexBufferUsage.Static,
                new[]
                {
                    VertexElement.POSITION,
                    VertexElement.BLEND_INDEX,
                    VertexElement.BLEND_WEIGHT,
                    VertexElement.NORMAL,
                    VertexElement.DIFFUSE_UV
                }
            );

        public static readonly VertexBufferDescription COLOR =
            new(
                VertexBufferUsage.Static,
                new[]
                {
                    VertexElement.POSITION,
                    VertexElement.BLEND_INDEX,
                    VertexElement.BLEND_WEIGHT,
                    VertexElement.NORMAL,
                    VertexElement.DIFFUSE_UV,
                    VertexElement.PRIMARY_COLOR
                }
            );

        public static readonly VertexBufferDescription TANGENT =
            new(
                VertexBufferUsage.Static,
                new[]
                {
                    VertexElement.POSITION,
                    VertexElement.BLEND_INDEX,
                    VertexElement.BLEND_WEIGHT,
                    VertexElement.NORMAL,
                    VertexElement.DIFFUSE_UV,
                    VertexElement.PRIMARY_COLOR,
                    VertexElement.TANGENT
                }
            );
    }

    internal enum SkinnedMeshVertexType : int
    {
        Basic,
        Color,
        Tangent
    }
}
