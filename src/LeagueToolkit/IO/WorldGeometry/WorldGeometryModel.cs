﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using LeagueToolkit.Helpers.Extensions;
using LeagueToolkit.Core.Primitives;
using LeagueToolkit.Core.Memory;
using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Environment;

namespace LeagueToolkit.IO.WorldGeometry
{
    /// <summary>
    /// Represents a <see cref="WorldGeometryModel"/> inside of a <see cref="WorldGeometry"/>
    /// </summary>
    public class WorldGeometryModel
    {
        /// <summary>
        /// Texture Path of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public string Texture { get; set; }

        /// <summary>
        /// Material of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public string Material { get; set; }

        /// <summary>
        /// Bounding Sphere of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public Sphere Sphere { get; private set; }

        /// <summary>
        /// Axis Aligned Bounding Box of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public Box BoundingBox { get; private set; }

        /// <summary>
        /// Vertices of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public List<WorldGeometryVertex> Vertices { get; set; } = new List<WorldGeometryVertex>();

        /// <summary>
        /// Indices of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public List<uint> Indices { get; set; } = new List<uint>();

        /// <summary>
        /// Initializes an empty <see cref="WorldGeometryModel"/>
        /// </summary>
        public WorldGeometryModel() { }

        /// <summary>
        /// Initializes a new <see cref="WorldGeometryModel"/>
        /// </summary>
        /// <param name="texture">Texture Path of this <see cref="WorldGeometryModel"/></param>
        /// <param name="material">Material of this <see cref="WorldGeometryModel"/></param>
        /// <param name="vertices">Vertices of this <see cref="WorldGeometryModel"/></param>
        /// <param name="indices">Indices of this <see cref="WorldGeometryModel"/></param>
        public WorldGeometryModel(
            string texture,
            string material,
            List<WorldGeometryVertex> vertices,
            List<uint> indices
        )
        {
            this.Texture = texture;
            this.Material = material;
            this.Vertices = vertices;
            this.Indices = indices;
            this.BoundingBox = CalculateBoundingBox();
            this.Sphere = CalculateSphere();
        }

        /// <summary>
        /// Initializes a new <see cref="WorldGeometryModel"/> from a <see cref="BinaryReader"/>
        /// </summary>
        /// <param name="br">The <see cref="BinaryReader"/> to read from</param>
        public WorldGeometryModel(BinaryReader br)
        {
            this.Texture = br.ReadPaddedString(260);
            this.Material = br.ReadPaddedString(64);
            this.Sphere = br.ReadSphere();
            this.BoundingBox = br.ReadBox();

            int vertexCount = br.ReadInt32();
            int indexCount = br.ReadInt32();

            VertexBufferDescription vertexDeclaration =
                new(VertexBufferUsage.Static, BakedEnvironmentVertexDescription.BASIC);
            int vertexSize = vertexDeclaration.GetVertexSize();
            MemoryOwner<byte> vertexBufferOwner = MemoryOwner<byte>.Allocate(vertexCount * vertexSize);

            IndexFormat indexFormat = indexCount <= ushort.MaxValue + 1 ? IndexFormat.U16 : IndexFormat.U32;
            int indexFormatSize = IndexBuffer.GetFormatSize(indexFormat);
            MemoryOwner<byte> indexBufferOwner = MemoryOwner<byte>.Allocate(indexCount * indexFormatSize);

            br.Read(vertexBufferOwner.Span);
            br.Read(indexBufferOwner.Span);

            IndexBuffer indexBuffer = IndexBuffer.Create(indexFormat, indexBufferOwner);
            VertexBuffer vertexBuffer = VertexBuffer.Create(
                VertexBufferUsage.Static,
                vertexDeclaration.Elements,
                vertexBufferOwner
            );
        }

        /// <summary>
        /// Writes this <see cref="WorldGeometryModel"/> into the specified <see cref="BinaryWriter"/>
        /// </summary>
        /// <param name="bw">The <see cref="BinaryWriter"/> to write to</param>
        public void Write(BinaryWriter bw)
        {
            bw.Write(Encoding.ASCII.GetBytes(this.Texture.PadRight(260, '\u0000')));
            bw.Write(Encoding.ASCII.GetBytes(this.Material.PadRight(64, '\u0000')));

            Tuple<Sphere, Box> boundingGeometry = CalculateBoundingGeometry();
            bw.WriteSphere(boundingGeometry.Item1);
            bw.WriteBox(boundingGeometry.Item2);

            bw.Write(this.Vertices.Count);
            bw.Write(this.Indices.Count);
            foreach (WorldGeometryVertex vertex in this.Vertices)
            {
                vertex.Write(bw);
            }

            if (this.Indices.Count <= 65536)
            {
                foreach (ushort index in this.Indices)
                {
                    bw.Write(index);
                }
            }
            else
            {
                foreach (uint index in this.Indices)
                {
                    bw.Write(index);
                }
            }
        }

        public Tuple<Sphere, Box> CalculateBoundingGeometry()
        {
            Box box = CalculateBoundingBox();
            Sphere sphere = CalculateSphere(box);
            return new Tuple<Sphere, Box>(sphere, box);
        }

        /// <summary>
        /// Calculates the Axis Aligned Bounding Box of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public Box CalculateBoundingBox()
        {
            if (this.Vertices == null || this.Vertices.Count == 0)
            {
                return new Box(new Vector3(0, 0, 0), new Vector3(0, 0, 0));
            }
            else
            {
                Vector3 min = this.Vertices[0].Position;
                Vector3 max = this.Vertices[0].Position;

                foreach (WorldGeometryVertex vertex in this.Vertices)
                {
                    if (min.X > vertex.Position.X)
                        min.X = vertex.Position.X;
                    if (min.Y > vertex.Position.Y)
                        min.Y = vertex.Position.Y;
                    if (min.Z > vertex.Position.Z)
                        min.Z = vertex.Position.Z;
                    if (max.X < vertex.Position.X)
                        max.X = vertex.Position.X;
                    if (max.Y < vertex.Position.Y)
                        max.Y = vertex.Position.Y;
                    if (max.Z < vertex.Position.Z)
                        max.Z = vertex.Position.Z;
                }

                return new Box(min, max);
            }
        }

        /// <summary>
        /// Calculates the Bounding Sphere of this <see cref="WorldGeometryModel"/>
        /// </summary>
        public Sphere CalculateSphere()
        {
            Box box = CalculateBoundingBox();
            Vector3 centralPoint = new Vector3(
                0.5f * (this.BoundingBox.Max.X + this.BoundingBox.Min.X),
                0.5f * (this.BoundingBox.Max.Y + this.BoundingBox.Min.Y),
                0.5f * (this.BoundingBox.Max.Z + this.BoundingBox.Min.Z)
            );

            return new(centralPoint, Vector3.Distance(centralPoint, box.Max));
        }

        /// <summary>
        /// Calculates the Bounding Sphere of this <see cref="WorldGeometryModel"/> from the specified <see cref="Box"/>
        /// </summary>
        /// <param name="box"><see cref="Box"/> to use for calculation</param>
        public Sphere CalculateSphere(Box box)
        {
            Vector3 centralPoint = new Vector3(
                0.5f * (this.BoundingBox.Max.X + this.BoundingBox.Min.X),
                0.5f * (this.BoundingBox.Max.Y + this.BoundingBox.Min.Y),
                0.5f * (this.BoundingBox.Max.Z + this.BoundingBox.Min.Z)
            );

            return new(centralPoint, Vector3.Distance(centralPoint, box.Max));
        }
    }
}
