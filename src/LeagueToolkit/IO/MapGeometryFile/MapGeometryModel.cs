﻿using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Helpers.Extensions;
using LeagueToolkit.Helpers.Structures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace LeagueToolkit.IO.MapGeometryFile
{
    /// <summary>
    /// Represents a mesh inside of a <see cref="MapGeometry"/> environment asset
    /// </summary>
    public sealed class MapGeometryModel : IDisposable
    {
        /// <summary>
        /// Gets the mesh instance name
        /// </summary>
        /// <remarks>
        /// This feature is supported only if <c>version &lt; 12</c>
        /// </remarks>
        public string Name { get; private set; }

        /// <summary>
        /// Gets the mesh's <see cref="InstancedVertexBuffer"/>
        /// </summary>
        public InstancedVertexBuffer VertexData => this._vertices;

        /// <summary>
        /// Gets a read-only view into the index buffer
        /// </summary>
        public ReadOnlySpan<ushort> Indices => this._indices.Span;

        private readonly InstancedVertexBuffer _vertices;
        private readonly MemoryOwner<ushort> _indices;

        /// <summary>
        /// Gets a read-only collection of the mesh's primitives
        /// </summary>
        public IReadOnlyList<MapGeometrySubmesh> Submeshes => this._submeshes;
        private readonly List<MapGeometrySubmesh> _submeshes = new();

        /// <summary>
        /// Gets whether to disable backface culling for the mesh
        /// </summary>
        public bool DisableBackfaceCulling { get; private set; }

        /// <summary>
        /// Gets a <see cref="Box"/> which represents the mesh AABB
        /// </summary>
        public Box BoundingBox { get; private set; }

        /// <summary>
        /// Gets the mesh transform
        /// </summary>
        public Matrix4x4 Transform { get; private set; }

        /// <summary>
        /// Tells the game on which "Environment Quality" settings this mesh should be rendered
        /// </summary>
        public MapGeometryEnvironmentQualityFilter EnvironmentQualityFilter { get; private set; } =
            MapGeometryEnvironmentQualityFilter.AllQualities;

        /// <summary>
        /// Tells the game on which Visibility Flags this mesh should be rendered
        /// </summary>
        public MapGeometryVisibilityFlags VisibilityFlags { get; private set; } = MapGeometryVisibilityFlags.AllLayers;

        /// <summary>Gets the render flags of the mesh</summary>
        public MapGeometryMeshRenderFlags RenderFlags { get; private set; }

        /// <remarks>
        /// This feature is supported only if <c>version &lt; 7</c>
        /// </remarks>
        public Vector3? PointLight { get; private set; }

        /// <summary>
        /// For more information about generating light probes,
        /// see <see href="https://docs.unity3d.com/Manual/LightProbes-TechnicalInformation.html">Unity - Light Probes</see>
        /// </summary>
        /// <remarks>
        /// This feature is supported only if <c>version &lt; 9</c><br></br>
        /// Since version 9, terrain meshes use baked light instead
        /// </remarks>
        public IReadOnlyList<Vector3> LightProbes => this._lightProbes;
        private readonly Vector3[] _lightProbes;

        /// <summary>Gets the <c>"STATIONARY_LIGHT"</c> sampler data</summary>
        /// <remarks>Usually contains a diffuse texture</remarks>
        public MapGeometrySamplerData StationaryLight { get; private set; }

        /// <summary>Gets the <c>"BAKED_LIGHT"</c> sampler data</summary>
        /// <remarks>Usually contains a lightmap texture (baked from scene point lights)</remarks>
        public MapGeometrySamplerData BakedLight { get; private set; }

        /// <summary>Gets the <c>"BAKED_PAINT"</c> sampler data</summary>
        /// <remarks>Usually contains a texture with baked diffuse and lightmap data</remarks>
        public MapGeometrySamplerData BakedPaint { get; private set; }

        internal int _baseDescriptionId;
        internal int[] _vertexBufferIds;
        internal int _indexBufferId;

        private bool _isDisposed;

        internal MapGeometryModel(
            int id,
            VertexBuffer vertices,
            MemoryOwner<ushort> indices,
            IEnumerable<MapGeometrySubmesh> submeshes,
            Matrix4x4 transform,
            bool disableBackfaceCulling,
            MapGeometryEnvironmentQualityFilter environmentQualityFilter,
            MapGeometryVisibilityFlags visibilityFlags,
            MapGeometryMeshRenderFlags renderFlags,
            MapGeometrySamplerData stationaryLight,
            MapGeometrySamplerData bakedLight,
            MapGeometrySamplerData bakedPaint
        )
        {
            this.Name = CreateName(id);

            this._vertices = new(new[] { vertices });
            this._indices = indices;
            this._submeshes = new(submeshes);

            this.Transform = transform;

            this.DisableBackfaceCulling = disableBackfaceCulling;
            this.EnvironmentQualityFilter = environmentQualityFilter;
            this.VisibilityFlags = visibilityFlags;
            this.RenderFlags = renderFlags;

            this.StationaryLight = stationaryLight;
            this.BakedLight = bakedLight;
            this.BakedPaint = bakedPaint;

            this.BoundingBox = Box.FromVertices(vertices.GetAccessor(ElementName.Position).AsVector3Array());
        }

        internal MapGeometryModel(
            BinaryReader br,
            int id,
            List<VertexBufferDescription> vertexBufferDescriptions,
            List<long> vertexBufferOffsets,
            List<MemoryOwner<ushort>> indexBuffers,
            bool useSeparatePointLights,
            uint version
        )
        {
            this.Name = version <= 11 ? Encoding.ASCII.GetString(br.ReadBytes(br.ReadInt32())) : CreateName(id);

            int vertexCount = br.ReadInt32();
            uint vertexBufferCount = br.ReadUInt32();

            // This ID is always that of the "instanced" vertex buffer which
            // means that the data of the first vertex buffer is instanced (unique to this mesh instance).
            // (Assuming that this mesh uses at least 2 vertex buffers)
            int baseVertexBufferDescriptionId = br.ReadInt32();

            VertexBuffer[] vertexBuffers = new VertexBuffer[vertexBufferCount];
            for (int i = 0; i < vertexBufferCount; i++)
            {
                int vertexBufferId = br.ReadInt32();
                long returnPosition = br.BaseStream.Position;

                VertexBufferDescription vertexBufferDescription = vertexBufferDescriptions[
                    baseVertexBufferDescriptionId + i
                ];
                MemoryOwner<byte> vertexBufferOwner = VertexBuffer.AllocateForElements(
                    vertexBufferDescription.Elements,
                    vertexCount
                );

                br.BaseStream.Seek(vertexBufferOffsets[vertexBufferId], SeekOrigin.Begin);
                br.Read(vertexBufferOwner.Span);

                vertexBuffers[i] = VertexBuffer.Create(
                    vertexBufferDescription.Usage,
                    vertexBufferDescription.Elements,
                    vertexBufferOwner
                );

                br.BaseStream.Seek(returnPosition, SeekOrigin.Begin);
            }

            this._vertices = new(vertexBuffers);

            uint indexCount = br.ReadUInt32();
            int indexBufferId = br.ReadInt32();
            this._indices = indexBuffers[indexBufferId];

            if (version >= 13)
            {
                this.VisibilityFlags = (MapGeometryVisibilityFlags)br.ReadByte();
            }

            uint submeshCount = br.ReadUInt32();
            for (int i = 0; i < submeshCount; i++)
            {
                this._submeshes.Add(new(br));
            }

            if (version != 5)
            {
                this.DisableBackfaceCulling = br.ReadBoolean();
            }

            this.BoundingBox = br.ReadBox();
            this.Transform = br.ReadMatrix4x4RowMajor();
            this.EnvironmentQualityFilter = (MapGeometryEnvironmentQualityFilter)br.ReadByte();

            if (version >= 7 && version <= 12)
            {
                this.VisibilityFlags = (MapGeometryVisibilityFlags)br.ReadByte();
            }

            if (version >= 11)
            {
                this.RenderFlags = (MapGeometryMeshRenderFlags)br.ReadByte();
            }

            if (useSeparatePointLights && (version < 7))
            {
                this.PointLight = br.ReadVector3();
            }

            if (version < 9)
            {
                this._lightProbes = new Vector3[9];
                for (int i = 0; i < 9; i++)
                {
                    this._lightProbes[i] = br.ReadVector3();
                }

                this.StationaryLight = MapGeometrySamplerData.Read(br);
            }
            else
            {
                this.StationaryLight = MapGeometrySamplerData.Read(br);
                this.BakedLight = MapGeometrySamplerData.Read(br);

                if (version >= 12)
                {
                    this.BakedPaint = MapGeometrySamplerData.Read(br);
                }
            }
        }

        internal void Write(BinaryWriter bw, bool useSeparatePointLights, uint version)
        {
            if (version <= 11)
            {
                bw.Write(this.Name.Length);
                bw.Write(Encoding.ASCII.GetBytes(this.Name));
            }

            bw.Write(this._vertices.VertexCount);
            bw.Write(this._vertexBufferIds.Length);
            bw.Write(this._baseDescriptionId);

            foreach (int vertexBufferId in this._vertexBufferIds)
            {
                bw.Write(vertexBufferId);
            }

            bw.Write(this._indices.Length);
            bw.Write(this._indexBufferId);

            if (version >= 13)
            {
                bw.Write((byte)this.VisibilityFlags);
            }

            bw.Write(this._submeshes.Count);
            foreach (MapGeometrySubmesh submesh in this._submeshes)
            {
                submesh.Write(bw);
            }

            if (version != 5)
            {
                bw.Write(this.DisableBackfaceCulling);
            }

            bw.WriteBox(this.BoundingBox);
            bw.WriteMatrix4x4RowMajor(this.Transform);
            bw.Write((byte)this.EnvironmentQualityFilter);

            if (version >= 7 && version <= 12)
            {
                bw.Write((byte)this.VisibilityFlags);
            }

            if (version >= 11)
            {
                bw.Write((byte)this.RenderFlags);
            }

            if (version < 9)
            {
                if (useSeparatePointLights)
                {
                    bw.WriteVector3(this.PointLight ?? Vector3.Zero);
                }

                foreach (Vector3 pointLight in this.LightProbes)
                {
                    bw.WriteVector3(pointLight);
                }
                for (int i = 0; i < 9 - this.LightProbes.Count; i++)
                {
                    bw.WriteVector3(Vector3.Zero);
                }

                this.StationaryLight.Write(bw);
            }
            else
            {
                this.StationaryLight.Write(bw);
                this.BakedLight.Write(bw);

                if (version >= 12)
                {
                    this.BakedPaint.Write(bw);
                }
            }
        }

        /// <summary>
        /// Creates a name for a <see cref="MapGeometryModel"/> with the specified <paramref name="id"/>
        /// </summary>
        /// <param name="id">The ID of the <see cref="MapGeometryModel"/></param>
        /// <returns>The created name</returns>
        public static string CreateName(int id)
        {
            // League assigns this name to the meshes automatically during reading
            return $"MapGeo_Instance_{id}";
        }

        private void Dispose(bool disposing)
        {
            if (!this._isDisposed)
            {
                if (disposing)
                {
                    this._indices?.Dispose();
                    this._vertices?.Dispose();
                }

                this._isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Used for limiting the visibility of environment meshes based on layer changes
    /// </summary>
    [Flags]
    public enum MapGeometryVisibilityFlags : byte
    {
        /// <summary>
        /// Toggles visibility on no layers
        /// </summary>
        NoLayer = 0,

        Layer1 = 1 << 0,
        Layer2 = 1 << 1,
        Layer3 = 1 << 2,
        Layer4 = 1 << 3,
        Layer5 = 1 << 4,
        Layer6 = 1 << 5,
        Layer7 = 1 << 6,
        Layer8 = 1 << 7,

        /// <summary>
        /// Toggles visibility on all layers
        /// </summary>
        AllLayers = Layer1 | Layer2 | Layer3 | Layer4 | Layer5 | Layer6 | Layer7 | Layer8
    }

    /// <summary>
    /// Used for limiting the visibility of an environment mesh for specific environment quality settings
    /// </summary>
    [Flags]
    public enum MapGeometryEnvironmentQualityFilter : byte
    {
        VeryLow = 1 << 0,
        Low = 1 << 1,
        Medium = 1 << 2,
        High = 1 << 3,
        VeryHigh = 1 << 4,

        /// <summary>
        /// Toggles visibility for all qualities
        /// </summary>
        AllQualities = VeryLow | Low | Medium | High | VeryHigh
    }

    /// <summary>
    /// General render flags
    /// </summary>
    [Flags]
    public enum MapGeometryMeshRenderFlags : byte
    {
        /// <summary>
        /// Mesh will have a higher render priority which causes it to be rendered
        /// on top of certain meshes such as particles with the following properties:
        /// <code>miscRenderFlags: u8 = 1 || isGroundLayer: flag = true || useNavmeshMask: flag = true</code>
        /// </summary>
        HighRenderPriority = 1 << 0,

        /// <summary>
        /// <br>⚠️ SPECULATIVE ⚠️</br>
        /// Tells the renderer to render distortion effects of a mesh's material into a separate buffer
        /// <br>⚠️ SPECULATIVE ⚠️</br>
        /// </summary>
        UseEnvironmentDistortionEffectBuffer = 1 << 1,

        /// <summary>
        /// Mesh will be rendered only if "Hide Eye Candy" option is unchecked
        /// </summary>
        /// <remarks>
        /// If no eye candy flag is set, the mesh will always be rendered
        /// </remarks>
        RenderOnlyIfEyeCandyOn = 1 << 2, // (meshTypeFlags & 4) == 0 || envSettingsFlags)

        /// <summary>
        /// Mesh will be rendered only if "Hide Eye Candy" option is checked
        /// </summary>
        /// <remarks>
        /// If no eye candy flag is set, the mesh will always be rendered
        /// </remarks>
        RenderOnlyIfEyeCandyOff = 1 << 3 // ((meshTypeFlags & 8) == 0 || envSettingsFlags != 1)
    }
}
