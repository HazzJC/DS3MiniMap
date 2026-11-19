using System.Numerics;
using DS3Minimap.Core;
using HKX2;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

internal sealed class CollisionGroundBaker(Ds3ArchiveReader archives)
{
    internal MapRegion Bake(MapRegion region, MSB3 msb)
    {
        var resource = region.MapId + "_00_00";
        var prefix = $"/map/{resource}/h{resource[1..]}";
        using var binder = new BXF4Reader(archives.Read(prefix + ".hkxbhd"), archives.Read(prefix + ".hkxbdt"));
        // Use ordinary static collision. Special-purpose blockers and kill volumes also have flat tops.
        var placements = msb.Parts.Collisions.Where(part => part.HitFilterID == 8).ToLookup(part => part.ModelName);
        var grid = new GroundGrid(region);
        foreach (var file in binder.Files)
        {
            var name = Path.GetFileName(file.Name);
            var suffix = name.Split('_').Last().Split('.')[0];
            var transforms = placements["h" + suffix].Select(part =>
            {
                var rotation = part.Rotation * (MathF.PI / 180);
                return Matrix4x4.CreateScale(part.Scale)
                    * Matrix4x4.CreateRotationX(rotation.X) * Matrix4x4.CreateRotationZ(rotation.Z)
                    * Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateTranslation(part.Position);
            }).Distinct().ToArray();
            if (transforms.Length == 0) continue;
            var bytes = DCX.Decompress(binder.ReadFile(file));
            var root = (hkRootLevelContainer)new PackFileDeserializer().Deserialize(new BinaryReaderEx(false, bytes));
            foreach (var scene in root.m_namedVariants.Select(v => v.m_variant).OfType<hknpPhysicsSceneData>())
            foreach (var body in scene.m_systemDatas.SelectMany(system => system.m_bodyCinfos))
            {
                if (body.m_shape is not hknpCompressedMeshShape mesh) continue;
                var placement = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(body.m_orientation))
                    * Matrix4x4.CreateTranslation(body.m_position.X, body.m_position.Y, body.m_position.Z);
                foreach (var transform in transforms) Append(mesh.m_data, placement * transform, grid);
            }
        }
        return grid.AssignFloors(region);
    }

    private static void Append(hknpCompressedMeshShapeData mesh, Matrix4x4 transform, GroundGrid grid)
    {
        var tree = mesh.m_meshTree;
        foreach (var section in tree.m_sections)
        {
            var offset = new Vector3(section.m_codecParms_0, section.m_codecParms_1, section.m_codecParms_2);
            var scale = new Vector3(section.m_codecParms_3, section.m_codecParms_4, section.m_codecParms_5);
            int packedCount = (int)(section.m_sharedVertices.m_data & 255);
            int sharedStart = (int)(section.m_sharedVertices.m_data >> 8);
            var vertices = new Vector3[packedCount + section.m_numSharedIndices];
            for (var i = 0; i < vertices.Length; i++)
            {
                var local = i < packedCount
                    ? mesh.DecompressPackedVertex(tree.m_packedVertices[(int)section.m_firstPackedVertex + i], scale, offset)
                    : mesh.DecompressSharedVertex(tree.m_sharedVertices[tree.m_sharedVerticesIndex[sharedStart + i - packedCount]],
                        tree.m_domain.m_min, tree.m_domain.m_max);
                vertices[i] = Vector3.Transform(local, transform);
            }
            var start = (int)(section.m_primitives.m_data >> 8);
            var count = (int)(section.m_primitives.m_data & 255);
            for (var i = start; i < start + count; i++)
            {
                var face = tree.m_primitives[i];
                // Some sections end in a 0xDEADDEAD padding primitive.
                if (face.m_indices_0 == face.m_indices_2) continue;
                if (Math.Max(Math.Max(face.m_indices_0, face.m_indices_1), Math.Max(face.m_indices_2, face.m_indices_3)) >= vertices.Length)
                    throw new InvalidDataException($"Invalid primitive {i}: {face.m_indices_0},{face.m_indices_1},{face.m_indices_2},{face.m_indices_3}; vertices {vertices.Length}, packed {packedCount} / {section.m_numPackedVertices}, shared {section.m_numSharedIndices}, sharedBase {sharedStart}.");
                var a = vertices[face.m_indices_0];
                var c = vertices[face.m_indices_2];
                grid.AddSurface(a, vertices[face.m_indices_1], c);
                if (face.m_indices_2 != face.m_indices_3) grid.AddSurface(a, c, vertices[face.m_indices_3]);
            }
        }
    }
}
