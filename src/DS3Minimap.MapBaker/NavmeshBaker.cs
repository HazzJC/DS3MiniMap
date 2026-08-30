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

        foreach (var file in binder.Files.Where(item => item.Bytes.Length > 0 && (item.Name?.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase) ?? true)))
        {
            try
            {
                var reader = new BinaryReaderEx(false, file.Bytes);
                var root = new PackFileDeserializer().Deserialize(reader);
                foreach (var mesh in FindMeshes(root)) AppendMesh(mesh, rawPolygons);
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or KeyNotFoundException)
            {
                Console.WriteLine($"  skipped unsupported HKX {file.Name ?? file.ID.ToString()}: {exception.Message}");
            }
        }

        if (rawPolygons.Count == 0) throw new InvalidDataException($"No navigation polygons were decoded for {mapId}.");
        // Visual FLVER map pieces include sky domes, horizon cards and distant
        // scenery. Their top-down convex hulls are not reliable floor geometry.
        // Keep the distributable package navigation-only until player collision
        // extraction is implemented.
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

    private static void AppendMesh(hkaiNavMesh mesh, ICollection<RawPolygon> destination)
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
                vertices.Add(new Vector3(value.X, value.Y, value.Z));
            }
            if (vertices.Count >= 3) destination.Add(new RawPolygon(vertices));
        }
    }

    private static IReadOnlyList<MapLayer> BuildLayers(IReadOnlyList<RawPolygon> source)
    {
        var ordered = source.OrderBy(item => item.MeanY).ToArray();
        var groups = new List<List<RawPolygon>>();
        foreach (var polygon in ordered)
        {
            if (groups.Count == 0 || polygon.MeanY - groups[^1].Average(item => item.MeanY) > 14f) groups.Add([]);
            groups[^1].Add(polygon);
        }

        return groups.Select((group, index) => new MapLayer(
            $"floor-{index}",
            groups.Count == 1 ? "Main" : $"Floor {index + 1}",
            group.Min(item => item.MinimumY) - 2,
            group.Max(item => item.MaximumY) + 2,
            group.Select((item, polygonIndex) => new MapPolygon(
                $"p-{index}-{polygonIndex}", item.MinimumY, item.MaximumY,
                item.Vertices.Select(vertex => new MapPoint(vertex.X, vertex.Z)).ToArray())).ToArray())).ToArray();
    }

    private sealed record RawPolygon(IReadOnlyList<Vector3> Vertices)
    {
        internal float MinimumY => Vertices.Min(value => value.Y);
        internal float MaximumY => Vertices.Max(value => value.Y);
        internal float MeanY => Vertices.Average(value => value.Y);
    }
}
