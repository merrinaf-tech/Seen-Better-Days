using Colossal.Mathematics;
using Game.Objects;
using Game.Prefabs;
using Unity.Mathematics;

namespace SeenBetterDays.Geometry
{
    /// <summary>
    /// One flat rectangle standing in for one side of a building, in world space.
    ///
    /// This is the deliberately crude half of the proof of concept. Everything here is
    /// derived from the prefab's axis-aligned local bounds (<see cref="ObjectGeometryData"/>)
    /// and the instance's <see cref="Transform"/> - no mesh reading, no submesh inspection,
    /// no real facade analysis. See the development notes for what a real implementation
    /// would need instead, and why the box is good enough to prove the rendering question.
    /// </summary>
    public struct BuildingFacade
    {
        /// <summary>World position of the middle of the facade rectangle.</summary>
        public float3 Center;

        /// <summary>World-space outward normal of the facade (horizontal, unit length).</summary>
        public float3 Normal;

        /// <summary>World-space horizontal direction along the facade (unit length).</summary>
        public float3 Tangent;

        /// <summary>World-space up direction of the facade (the building's own up).</summary>
        public float3 Up;

        /// <summary>Facade extent along <see cref="Tangent"/>, in metres.</summary>
        public float Width;

        /// <summary>Facade extent along <see cref="Up"/>, in metres.</summary>
        public float Height;

        public FacadeSide Side;

        /// <summary>True when the bounds produced a facade with usable area.</summary>
        public bool IsValid => Width > 0.5f && Height > 0.5f;

        /// <summary>
        /// A point on the facade. <paramref name="u"/> runs 0..1 across the width,
        /// <paramref name="v"/> runs 0..1 from the bottom of the box to the top.
        /// </summary>
        public float3 PointAt(float u, float v)
        {
            return Center
                 + Tangent * ((u - 0.5f) * Width)
                 + Up * ((v - 0.5f) * Height);
        }

        /// <summary>
        /// Builds the facade rectangle for one side of a building instance.
        /// Returns false when the prefab has no usable geometry.
        /// </summary>
        public static bool TryBuild(
            in Transform transform,
            in ObjectGeometryData geometry,
            FacadeSide side,
            out BuildingFacade facade)
        {
            facade = default;

            Bounds3 b = geometry.m_Bounds;
            float3 size = b.max - b.min;
            if (math.any(size <= 0f))
            {
                return false;
            }

            float3 localCenter = (b.min + b.max) * 0.5f;

            // Local-space frame for the requested side. localNormal points out of the box,
            // localTangent runs horizontally along the face, and up is always local +Y.
            float3 localNormal;
            float3 localTangent;
            float width;

            switch (side)
            {
                case FacadeSide.Right:
                    localNormal = new float3(1f, 0f, 0f);
                    localTangent = new float3(0f, 0f, 1f);
                    localCenter.x = b.max.x;
                    width = size.z;
                    break;
                case FacadeSide.Back:
                    localNormal = new float3(0f, 0f, -1f);
                    localTangent = new float3(-1f, 0f, 0f);
                    localCenter.z = b.min.z;
                    width = size.x;
                    break;
                case FacadeSide.Left:
                    localNormal = new float3(-1f, 0f, 0f);
                    localTangent = new float3(0f, 0f, -1f);
                    localCenter.x = b.min.x;
                    width = size.z;
                    break;
                default: // Front
                    localNormal = new float3(0f, 0f, 1f);
                    localTangent = new float3(1f, 0f, 0f);
                    localCenter.z = b.max.z;
                    width = size.x;
                    break;
            }

            facade = new BuildingFacade
            {
                Center = transform.m_Position + math.rotate(transform.m_Rotation, localCenter),
                Normal = math.normalizesafe(math.rotate(transform.m_Rotation, localNormal)),
                Tangent = math.normalizesafe(math.rotate(transform.m_Rotation, localTangent)),
                Up = math.normalizesafe(math.rotate(transform.m_Rotation, new float3(0f, 1f, 0f))),
                Width = width,
                Height = size.y,
                Side = side,
            };

            return facade.IsValid;
        }

        /// <summary>
        /// The rotation an axis-aligned Cities: Skylines II decal needs in order to project
        /// into this facade.
        ///
        /// A decal's projector box points down its own local -Y and lays its texture out in
        /// the local XZ plane. To hang one on a wall we want local +Y along the wall's
        /// outward normal (so the projection runs into the wall), local +Z along the
        /// building's up (so the texture stands upright) and local +X along the wall.
        /// <c>LookRotationSafe(forward, up)</c> gives exactly that when we pass the facade's
        /// up as "forward" and its normal as "up".
        /// </summary>
        /// <param name="invert">
        /// Projects the other way. Which sign is correct depends on whether the decal shader
        /// projects down its local -Y or up its +Y, and that is not something the game's code
        /// states outright - so the proof of concept can try both rather than guess once.
        /// </param>
        public quaternion DecalRotation(bool invert)
        {
            return quaternion.LookRotationSafe(Up, invert ? -Normal : Normal);
        }
    }
}
