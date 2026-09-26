using System.Collections.Generic;
using Game.Objects;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace SeenBetterDays.Geometry
{
    /// <summary>
    /// Finds where a ray meets a building's actual geometry.
    ///
    /// This exists because of the single mistake that cost this proof of concept more time than
    /// everything else put together: decals were placed on a plane derived from
    /// <c>ObjectGeometryData.m_Bounds</c>, which is the building's *outermost* extent - balconies,
    /// cornices, canopies, roof plant. A decal is a projector, a box about half a metre deep that
    /// paints only the geometry it contains, so a wall standing behind that outer plane is outside
    /// the box and nothing appears. Placing one at a genuine raycast hit made it appear first try.
    ///
    /// The game does the same thing: <c>Snap.ObjectSurface</c> puts the decal at
    /// <c>controlPoint.m_HitPosition</c>, the point where a ray met the real mesh.
    ///
    /// The intersection is written out here rather than borrowed. `Game.Objects.RaycastJobs` has
    /// exactly the routine needed, but it is private; the mesh buffers it reads - `MeshVertex` and
    /// `MeshIndex` on each submesh - are ordinary public components. Depending on our own thirty
    /// lines is better than depending on a private method that can change between patches.
    /// </summary>
    public static class BuildingSurfaceProbe
    {
        /// <summary>
        /// Finds a real wall triangle near a desired point on one side of the building.
        ///
        /// This is the safe recovery path when a ray aimed through the bounding rectangle lands
        /// in a window, an archway or the gap between two wings. The returned point is the
        /// centroid of an actual mesh triangle; it never substitutes the bounding rectangle for
        /// geometry. Preference is given to triangles close to the requested point whose plane
        /// faces the requested facade direction.
        /// </summary>
        public static bool TryFindFacadePoint(
            EntityManager entityManager,
            Entity building,
            float3 target,
            float3 facadeNormal,
            out float3 point,
            out float3 normal)
        {
            point = default;
            normal = default;

            if (!entityManager.HasComponent<Transform>(building)
                || !entityManager.HasComponent<PrefabRef>(building))
            {
                return false;
            }

            Entity prefab = entityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!entityManager.HasBuffer<SubMesh>(prefab))
            {
                return false;
            }

            Transform transform = entityManager.GetComponentData<Transform>(building);
            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(prefab, true);
            float3 wantedNormal = math.normalizesafe(facadeNormal, math.forward());

            float bestScore = float.MaxValue;
            bool found = false;

            for (int i = 0; i < subMeshes.Length; i++)
            {
                SubMesh subMesh = subMeshes[i];
                Entity mesh = subMesh.m_SubMesh;

                if (!entityManager.HasBuffer<MeshVertex>(mesh) || !entityManager.HasBuffer<MeshIndex>(mesh))
                {
                    continue;
                }

                DynamicBuffer<MeshVertex> vertices = entityManager.GetBuffer<MeshVertex>(mesh, true);
                DynamicBuffer<MeshIndex> indices = entityManager.GetBuffer<MeshIndex>(mesh, true);

                bool hasOwnTransform = (subMesh.m_Flags & (SubMeshFlags.HasTransform
                                                         | SubMeshFlags.IsStackStart
                                                         | SubMeshFlags.IsStackMiddle
                                                         | SubMeshFlags.IsStackEnd)) != 0;

                int triangles = indices.Length / 3;
                for (int triangle = 0; triangle < triangles; triangle++)
                {
                    int i0 = indices[triangle * 3].m_Index;
                    int i1 = indices[triangle * 3 + 1].m_Index;
                    int i2 = indices[triangle * 3 + 2].m_Index;

                    if (i0 < 0 || i1 < 0 || i2 < 0
                        || i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                    {
                        continue;
                    }

                    float3 a = vertices[i0].m_Vertex;
                    float3 b = vertices[i1].m_Vertex;
                    float3 c = vertices[i2].m_Vertex;

                    if (hasOwnTransform)
                    {
                        a = math.mul(subMesh.m_Rotation, a) + subMesh.m_Position;
                        b = math.mul(subMesh.m_Rotation, b) + subMesh.m_Position;
                        c = math.mul(subMesh.m_Rotation, c) + subMesh.m_Position;
                    }

                    a = transform.m_Position + math.mul(transform.m_Rotation, a);
                    b = transform.m_Position + math.mul(transform.m_Rotation, b);
                    c = transform.m_Position + math.mul(transform.m_Rotation, c);

                    float3 cross = math.cross(b - a, c - a);
                    float areaTwice = math.length(cross);
                    if (areaTwice < 0.0001f)
                    {
                        continue;
                    }

                    float3 candidateNormal = cross / areaTwice;
                    float alignment = math.dot(candidateNormal, wantedNormal);
                    if (alignment < 0f)
                    {
                        candidateNormal = -candidateNormal;
                        alignment = -alignment;
                    }

                    // Roof and floor triangles are useless to a facade projector. The loose
                    // threshold keeps chamfered and articulated walls eligible.
                    if (alignment < 0.25f)
                    {
                        continue;
                    }

                    float3 candidate = (a + b + c) / 3f;
                    float3 delta = candidate - target;
                    float depth = math.dot(delta, wantedNormal);
                    float3 inPlane = delta - wantedNormal * depth;

                    // Proximity dominates. Alignment and depth only break ties in favour of the
                    // requested outer wall instead of a perpendicular or internal surface.
                    float score = math.lengthsq(inPlane)
                                + depth * depth * 0.15f
                                + (1f - alignment) * 16f
                                - math.min(areaTwice, 20f) * 0.01f;

                    if (score >= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    point = candidate;
                    normal = candidateNormal;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>One piece of real wall in prefab-local space: position, normal and area.</summary>
        public struct SurfacePoint
        {
            public float3 m_Position;
            public float3 m_Normal;
            public float m_Area;
            public FacadeSide m_Side;
        }

        /// <summary>
        /// Walks a building prefab's geometry once and collects every piece of wall worth marking.
        ///
        /// This replaces casting a separate ray for every mark. Both answer the same question -
        /// where is there real surface here - but one asks it once per building and the other asks
        /// it once per mark, and the difference is the whole cost of the detail layer: a tower of
        /// two thousand triangles with ten marks was twenty thousand triangle tests, and is now
        /// two thousand plus ten. Positions and normals remain in the building's local space, so
        /// this result can be cached and shared by every instance of the same prefab. The renderer
        /// transforms only the handful of candidates it samples for an individual building.
        ///
        /// Only near-vertical faces are kept. A decal projector on a roof or a soffit is either
        /// invisible or wrong, and filtering here means nothing downstream has to think about it.
        /// Area comes along because a mark should be as likely to land on a piece of wall as that
        /// wall is large - otherwise a facade made of many small panels attracts marks away from a
        /// plain one beside it, for no reason a player could see.
        /// </summary>
        public static int CollectLocalFacadePoints(
            EntityManager entityManager,
            Entity building,
            List<SurfacePoint> into,
            int limit = 6000)
        {
            into.Clear();

            if (!entityManager.HasComponent<Transform>(building)
                || !entityManager.HasComponent<PrefabRef>(building))
            {
                return 0;
            }

            var surveyor = new FacadeSurveyor(
                entityManager, entityManager.GetComponentData<PrefabRef>(building).m_Prefab, limit);
            surveyor.Step(entityManager, double.PositiveInfinity);
            into.AddRange(surveyor.Points);
            return into.Count;
        }

        /// <summary>
        /// <see cref="CollectLocalFacadePoints"/> as work that can stop and resume.
        ///
        /// Walking a detailed prefab's triangles on the main thread took up to 60 ms in one go -
        /// a visible hitch every time the camera reached a building type it had not seen yet. The
        /// walk is identical; it just keeps its place (submesh, triangle) between calls, so it can
        /// be spread over frames under a time budget. Buffers are fetched again on every call: a
        /// DynamicBuffer handle does not survive the structural changes other systems make between
        /// frames.
        /// </summary>
        public sealed class FacadeSurveyor
        {
            /// <summary>Triangles between clock checks: reading the clock per triangle costs more
            /// than the triangle.</summary>
            private const int TrianglesPerCheck = 128;

            public readonly Entity Prefab;
            public readonly List<SurfacePoint> Points = new List<SurfacePoint>(2048);

            private readonly float3 m_Center;
            private readonly int m_PointsPerSide;
            private readonly int m_AcceptedLimit;
            private readonly int[] m_SideCounts = new int[4];
            private int m_SubMesh;
            private int m_Triangle;

            public bool Done { get; private set; }

            public FacadeSurveyor(EntityManager entityManager, Entity prefab, int limit = 6000)
            {
                Prefab = prefab;
                if (entityManager.HasComponent<ObjectGeometryData>(prefab))
                {
                    ObjectGeometryData geometry = entityManager.GetComponentData<ObjectGeometryData>(prefab);
                    m_Center = (geometry.m_Bounds.min + geometry.m_Bounds.max) * 0.5f;
                }

                // Do not let submesh order spend the whole sample budget on the first wall it
                // happens to contain. Detailed row houses can exceed the cap before their street
                // facade is reached, which left only the two gable ends in the cache. Reserve an
                // equal share for every local side and keep walking after one side is full.
                m_PointsPerSide = math.max(1, limit / 4);
                m_AcceptedLimit = m_PointsPerSide * 4;
                Done = !entityManager.HasBuffer<SubMesh>(prefab);
            }

            /// <summary>Walks triangles until done or until <paramref name="budgetMs"/> has been
            /// spent. Returns true when the survey is complete.</summary>
            public bool Step(EntityManager entityManager, double budgetMs)
            {
                if (Done)
                {
                    return true;
                }

                if (!entityManager.Exists(Prefab) || !entityManager.HasBuffer<SubMesh>(Prefab))
                {
                    Done = true;
                    return true;
                }

                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000d;
                DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(Prefab, true);
                int sinceCheck = 0;

                for (; m_SubMesh < subMeshes.Length && Points.Count < m_AcceptedLimit; m_SubMesh++, m_Triangle = 0)
                {
                    SubMesh subMesh = subMeshes[m_SubMesh];
                    Entity mesh = subMesh.m_SubMesh;

                    if (!entityManager.HasBuffer<MeshVertex>(mesh) || !entityManager.HasBuffer<MeshIndex>(mesh))
                    {
                        continue;
                    }

                    DynamicBuffer<MeshVertex> vertices = entityManager.GetBuffer<MeshVertex>(mesh, true);
                    DynamicBuffer<MeshIndex> indices = entityManager.GetBuffer<MeshIndex>(mesh, true);

                    bool hasOwnTransform = (subMesh.m_Flags & (SubMeshFlags.HasTransform
                                                             | SubMeshFlags.IsStackStart
                                                             | SubMeshFlags.IsStackMiddle
                                                             | SubMeshFlags.IsStackEnd)) != 0;

                    int triangles = indices.Length / 3;
                    for (; m_Triangle < triangles && Points.Count < m_AcceptedLimit; m_Triangle++)
                    {
                        if (++sinceCheck >= TrianglesPerCheck)
                        {
                            sinceCheck = 0;
                            if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) / ticksPerMs >= budgetMs)
                            {
                                return false;
                            }
                        }

                        int t = m_Triangle;
                        int i0 = indices[t * 3].m_Index;
                        int i1 = indices[t * 3 + 1].m_Index;
                        int i2 = indices[t * 3 + 2].m_Index;

                        if (i0 < 0 || i1 < 0 || i2 < 0
                            || i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                        {
                            continue;
                        }

                        float3 a = vertices[i0].m_Vertex;
                        float3 b = vertices[i1].m_Vertex;
                        float3 c = vertices[i2].m_Vertex;

                        if (hasOwnTransform)
                        {
                            a = math.mul(subMesh.m_Rotation, a) + subMesh.m_Position;
                            b = math.mul(subMesh.m_Rotation, b) + subMesh.m_Position;
                            c = math.mul(subMesh.m_Rotation, c) + subMesh.m_Position;
                        }

                        float3 cross = math.cross(b - a, c - a);
                        float twiceArea = math.length(cross);

                        // A quarter of a square metre. Smaller than this is window frames and
                        // mouldings, where a decal reads as a mistake rather than as dirt.
                        if (twiceArea < 0.5f)
                        {
                            continue;
                        }

                        float3 normal = cross / twiceArea;
                        if (math.abs(normal.y) > 0.35f)
                        {
                            continue;
                        }

                        float3 position = (a + b + c) / 3f;

                        // Mesh winding is not consistent across custom assets. Orient the cached
                        // normal away from the prefab centre so both sides can pass the renderer's
                        // facing test instead of one direction disappearing solely because its
                        // triangles were wound the other way round.
                        float3 fromCenter = position - m_Center;
                        fromCenter.y = 0f;
                        if (math.dot(normal, fromCenter) < 0f)
                        {
                            normal = -normal;
                        }

                        int side;
                        if (math.abs(normal.z) >= math.abs(normal.x))
                        {
                            side = normal.z >= 0f ? (int)FacadeSide.Front : (int)FacadeSide.Back;
                        }
                        else
                        {
                            side = normal.x >= 0f ? (int)FacadeSide.Right : (int)FacadeSide.Left;
                        }

                        if (m_SideCounts[side] >= m_PointsPerSide)
                        {
                            continue;
                        }

                        m_SideCounts[side]++;

                        Points.Add(new SurfacePoint
                        {
                            m_Position = position,
                            m_Normal = normal,
                            m_Area = twiceArea * 0.5f,
                            m_Side = (FacadeSide)side,
                        });
                    }
                }

                Done = true;
                return true;
            }
        }

        /// <summary>
        /// Whether this building has any triangles to cast at right now.
        ///
        /// Asked before placing rather than discovered by failing. A building whose mesh the game
        /// has not loaded is not an error to report - it is simply not ready yet.
        /// </summary>
        public static bool HasMeshGeometry(EntityManager entityManager, Entity building)
        {
            if (!entityManager.HasComponent<PrefabRef>(building))
            {
                return false;
            }

            Entity prefab = entityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!entityManager.HasBuffer<SubMesh>(prefab))
            {
                return false;
            }

            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(prefab, true);
            for (int i = 0; i < subMeshes.Length; i++)
            {
                Entity mesh = subMeshes[i].m_SubMesh;
                if (entityManager.HasBuffer<MeshVertex>(mesh)
                    && entityManager.HasBuffer<MeshIndex>(mesh)
                    && entityManager.GetBuffer<MeshIndex>(mesh, true).Length >= 3)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Says what mesh data this building actually has right now.
        ///
        /// Written because a building missed every one of thirty-two rays four times running and
        /// then took four hits out of four seconds later, with nothing about the building changed.
        /// A ray that misses *everything* is not an inaccurate ray - it is a ray cast at nothing,
        /// and the question is whether there were any triangles there to hit at all.
        ///
        /// `MeshVertex` and `MeshIndex` live on the render prefab, and the game has no obligation
        /// to keep them resident for a prefab it is not currently drawing up close. If that is what
        /// is happening, every placement decision this mod makes silently depends on where the
        /// camera is - which would be a far worse bug than a missed ray, and is worth one log line
        /// to find out.
        /// </summary>
        public static string DescribeMeshResidency(EntityManager entityManager, Entity building)
        {
            if (!entityManager.HasComponent<PrefabRef>(building))
            {
                return "no PrefabRef";
            }

            Entity prefab = entityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!entityManager.HasBuffer<SubMesh>(prefab))
            {
                return "prefab has no SubMesh buffer";
            }

            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(prefab, true);

            int withGeometry = 0;
            int withoutBuffers = 0;
            int empty = 0;
            int triangles = 0;

            for (int i = 0; i < subMeshes.Length; i++)
            {
                Entity mesh = subMeshes[i].m_SubMesh;

                if (!entityManager.HasBuffer<MeshVertex>(mesh) || !entityManager.HasBuffer<MeshIndex>(mesh))
                {
                    withoutBuffers++;
                    continue;
                }

                int count = entityManager.GetBuffer<MeshIndex>(mesh, true).Length / 3;
                if (count == 0)
                {
                    empty++;
                    continue;
                }

                withGeometry++;
                triangles += count;
            }

            return subMeshes.Length + " submesh(es): " + withGeometry + " with geometry ("
                 + triangles + " triangles), " + withoutBuffers + " with no mesh buffers, "
                 + empty + " empty";
        }

        /// <summary>
        /// Casts a ray at a building and reports where it lands on the real surface.
        /// </summary>
        /// <param name="origin">Where the ray starts, in world space - outside the building.</param>
        /// <param name="direction">Which way it travels. Need not be normalised.</param>
        /// <param name="maxDistance">How far to look before giving up.</param>
        /// <returns>False when the ray misses, or the building has no mesh to test.</returns>
        public static bool TryHit(
            EntityManager entityManager,
            Entity building,
            float3 origin,
            float3 direction,
            float maxDistance,
            out float3 point,
            out float3 normal)
        {
            point = default;
            normal = default;

            if (!entityManager.HasComponent<Transform>(building)
                || !entityManager.HasComponent<PrefabRef>(building))
            {
                return false;
            }

            Entity prefab = entityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            if (!entityManager.HasBuffer<SubMesh>(prefab))
            {
                return false;
            }

            Transform transform = entityManager.GetComponentData<Transform>(building);
            quaternion inverseRotation = math.inverse(transform.m_Rotation);

            // Into the building's own space, where the mesh vertices live.
            float3 localOrigin = math.mul(inverseRotation, origin - transform.m_Position);
            float3 localDirection = math.mul(inverseRotation, math.normalizesafe(direction, math.forward()));

            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(prefab, true);

            float nearest = maxDistance;
            bool found = false;
            float3 bestPoint = default;
            float3 bestNormal = default;

            for (int i = 0; i < subMeshes.Length; i++)
            {
                SubMesh subMesh = subMeshes[i];
                Entity mesh = subMesh.m_SubMesh;

                if (!entityManager.HasBuffer<MeshVertex>(mesh) || !entityManager.HasBuffer<MeshIndex>(mesh))
                {
                    continue;
                }

                float3 subOrigin = localOrigin;
                float3 subDirection = localDirection;

                // The engine only applies a submesh's own transform when it declares one, and this
                // has to match or every hit on such a submesh lands in the wrong place.
                bool hasOwnTransform = (subMesh.m_Flags & (SubMeshFlags.HasTransform
                                                         | SubMeshFlags.IsStackStart
                                                         | SubMeshFlags.IsStackMiddle
                                                         | SubMeshFlags.IsStackEnd)) != 0;
                if (hasOwnTransform)
                {
                    quaternion inverseSub = math.inverse(subMesh.m_Rotation);
                    subOrigin = math.mul(inverseSub, localOrigin - subMesh.m_Position);
                    subDirection = math.mul(inverseSub, localDirection);
                }

                DynamicBuffer<MeshVertex> vertices = entityManager.GetBuffer<MeshVertex>(mesh, true);
                DynamicBuffer<MeshIndex> indices = entityManager.GetBuffer<MeshIndex>(mesh, true);

                float distance;
                float3 hitNormal;
                if (!TryHitMesh(vertices, indices, subOrigin, subDirection, nearest, out distance, out hitNormal))
                {
                    continue;
                }

                float3 localHit = subOrigin + subDirection * distance;

                if (hasOwnTransform)
                {
                    localHit = math.mul(subMesh.m_Rotation, localHit) + subMesh.m_Position;
                    hitNormal = math.mul(subMesh.m_Rotation, hitNormal);
                }

                nearest = distance;
                bestPoint = localHit;
                bestNormal = hitNormal;
                found = true;
            }

            if (!found)
            {
                return false;
            }

            point = transform.m_Position + math.mul(transform.m_Rotation, bestPoint);
            normal = math.mul(transform.m_Rotation, bestNormal);

            // Always report the face the ray arrived on. Triangle winding is not something to rely
            // on across arbitrary custom assets, and a normal pointing into the wall would put the
            // decal behind it - the same failure as before, by a different route.
            if (math.dot(normal, math.normalizesafe(direction, math.forward())) > 0f)
            {
                normal = -normal;
            }

            return true;
        }

        /// <summary>
        /// Nearest ray-triangle intersection over one mesh, by Möller-Trumbore.
        /// </summary>
        private static bool TryHitMesh(
            DynamicBuffer<MeshVertex> vertices,
            DynamicBuffer<MeshIndex> indices,
            float3 origin,
            float3 direction,
            float maxDistance,
            out float distance,
            out float3 normal)
        {
            distance = maxDistance;
            normal = default;

            bool found = false;
            int triangles = indices.Length / 3;

            for (int t = 0; t < triangles; t++)
            {
                int i0 = indices[t * 3].m_Index;
                int i1 = indices[t * 3 + 1].m_Index;
                int i2 = indices[t * 3 + 2].m_Index;

                if (i0 < 0 || i1 < 0 || i2 < 0
                    || i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                {
                    continue;
                }

                float3 a = vertices[i0].m_Vertex;
                float3 edge1 = vertices[i1].m_Vertex - a;
                float3 edge2 = vertices[i2].m_Vertex - a;

                float3 pvec = math.cross(direction, edge2);
                float determinant = math.dot(edge1, pvec);

                // Near zero means the ray runs parallel to the triangle's plane. Both signs are
                // accepted: a building is a closed shell seen from outside, but custom assets are
                // not reliably wound, and rejecting back faces would silently skip them.
                if (math.abs(determinant) < 1e-7f)
                {
                    continue;
                }

                float inverseDeterminant = 1f / determinant;
                float3 tvec = origin - a;

                float u = math.dot(tvec, pvec) * inverseDeterminant;
                if (u < 0f || u > 1f)
                {
                    continue;
                }

                float3 qvec = math.cross(tvec, edge1);
                float v = math.dot(direction, qvec) * inverseDeterminant;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }

                float hit = math.dot(edge2, qvec) * inverseDeterminant;
                if (hit <= 0.01f || hit >= distance)
                {
                    continue;
                }

                distance = hit;
                normal = math.normalizesafe(math.cross(edge1, edge2), math.up());
                found = true;
            }

            return found;
        }
    }
}
