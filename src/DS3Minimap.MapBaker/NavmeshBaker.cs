using System.Numerics;
using DS3Minimap.Core;
using HKX2;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

internal sealed class NavmeshBaker(Ds3ArchiveReader archives)
{
    internal void DumpInstanceTransforms(string mapId)
    {
        var resourceId = mapId + "_00_00";
        var compressed = archives.Read($"/map/{resourceId}/{resourceId}.nvmhktbnd.dcx");
        var binder = BND4.Read(DCX.Decompress(compressed));
        foreach (var file in binder.Files.Where(item => item.Bytes.Length > 0 && (item.Name?.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase) ?? true)))
        {
            try
            {
                var root = new PackFileDeserializer().Deserialize(new BinaryReaderEx(false, file.Bytes));
                if (root is not hkRootLevelContainer container) continue;
                foreach (var instance in container.m_namedVariants.Select(item => item.m_variant).OfType<hkaiNavMeshInstance>())
                    Console.WriteLine($"{file.Name}: {instance.m_referenceFrame.m_transform}");
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or KeyNotFoundException) { }
        }
    }

    internal MapRegion Bake(string mapId, string displayName)
    {
        var resourceId = mapId + "_00_00";
        var virtualPath = $"/map/{resourceId}/{resourceId}.nvmhktbnd.dcx";
        var compressed = archives.Read(virtualPath);
        var binder = BND4.Read(DCX.Decompress(compressed));
        var rawPolygons = new List<RawPolygon>();

        var placementFile = NVA.Read(archives.Read($"/map/{resourceId}/{resourceId}.nva.dcx"));
        var files = binder.Files.Where(f => f.Name != null)
            .ToLookup(f => Path.GetFileName(f.Name!), StringComparer.OrdinalIgnoreCase);
        foreach (var group in placementFile.NavmeshInfoEntries.GroupBy(entry => entry.ModelID))
        {
            var name = $"n{resourceId[1..]}_{group.Key:D6}.hkx";
            var file = files[name].FirstOrDefault();
            if (file is null && group.All(entry => entry.NameID == -1 && entry.FaceCount == 0)) continue;
            if (file is null) throw new InvalidDataException($"Missing placed navmesh {name}.");
            var root = new PackFileDeserializer().Deserialize(new BinaryReaderEx(false, file.Bytes));
            var meshes = FindMeshes(root).Distinct().ToArray();
            foreach (var entry in group)
            {
                var transform = Matrix4x4.CreateScale(entry.Scale)
                    * Matrix4x4.CreateRotationX(entry.Rotation.X) * Matrix4x4.CreateRotationZ(entry.Rotation.Z)
                    * Matrix4x4.CreateRotationY(entry.Rotation.Y) * Matrix4x4.CreateTranslation(entry.Position);
                foreach (var mesh in meshes) AppendMesh(mesh, transform, rawPolygons);
            }
        }

        if (rawPolygons.Count == 0) throw new InvalidDataException($"No navigation polygons were decoded for {mapId}.");
        return new MapRegion(mapId, displayName, BuildLayers(rawPolygons));
    }

    private static IEnumerable<hkaiNavMesh> FindMeshes(IHavokObject root)
    {
        if (root is hkaiNavMesh direct) yield return direct;
        if (root is hkRootLevelContainer container)
        {
            foreach (var variant in container.m_namedVariants)
            {
                if (variant.m_variant is hkaiNavMesh mesh) yield return mesh;
                if (variant.m_variant is hkaiNavMeshInstance instance && instance.m_originalMesh is not null) yield return instance.m_originalMesh;
            }
        }
    }

    private static void AppendMesh(hkaiNavMesh mesh, Matrix4x4 transform, ICollection<RawPolygon> destination)
    {
        foreach (var face in mesh.m_faces)
        {
            if (face.m_numEdges < 3 || face.m_numEdges > 32) continue;
            var vertices = new List<Vector3>(face.m_numEdges);
            for (var offset = 0; offset < face.m_numEdges; offset++)
            {
                var edgeIndex = face.m_startEdgeIndex + offset;
                if ((uint)edgeIndex >= mesh.m_edges.Count) break;
                var vertexIndex = mesh.m_edges[edgeIndex].m_a;
                if ((uint)vertexIndex >= mesh.m_vertices.Count) break;
                var value = mesh.m_vertices[vertexIndex];
                vertices.Add(Vector3.Transform(new Vector3(value.X, value.Y, value.Z), transform));
            }
            if (vertices.Count >= 3) destination.Add(new RawPolygon(vertices));
        }
    }

    private static IReadOnlyList<MapLayer> BuildLayers(IReadOnlyList<RawPolygon> source)
    {
        var ordered = source.OrderBy(item => item.MeanY).ToArray();
        var groups = new List<List<RawPolygon>>();
        double heightSum = 0;
        foreach (var polygon in ordered)
        {
            if (groups.Count == 0 || polygon.MeanY - heightSum / groups[^1].Count > 14f)
            {
                groups.Add([]);
                heightSum = 0;
            }
            heightSum += polygon.MeanY;
            groups[^1].Add(polygon);
        }

        return groups.Select((group, index) => new MapLayer(
            $"floor-{index}",
            groups.Count == 1 ? "Main" : $"Floor {index + 1}",
            group.Min(item => item.MinimumY) - 2,
            group.Max(item => item.MaximumY) + 2,
            group.Select((item, polygonIndex) => new MapPolygon(
                $"p-{index}-{polygonIndex}", item.MinimumY, item.MaximumY,
                item.Vertices.Select(vertex => new MapPoint(vertex.X, vertex.Z, vertex.Y)).ToArray())).ToArray())).ToArray();
    }

    private sealed record RawPolygon(IReadOnlyList<Vector3> Vertices)
    {
        internal float MinimumY => Vertices.Min(value => value.Y);
        internal float MaximumY => Vertices.Max(value => value.Y);
        internal float MeanY => Vertices.Average(value => value.Y);
    }
}
