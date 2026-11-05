using System;
using Tryscape.Core.Model;

namespace Tryscape.Content.MovingDay
{
    /// <summary>
    /// Moving Day reference pack 1.0.0 (demo-scope D06). PROVISIONAL demo choice; pack logic lives here, never in
    /// Core. The catalog must stay identical to contracts/v1/fixtures/catalog/moving_day_1.0.0.json (EditMode test).
    /// </summary>
    public static class MovingDayPack
    {
        public const string PackId = "moving_day";
        public const string PackVersion = "1.0.0";
        public const string CatalogVersion = "1.0.0";
        public const string SofaCatalogId = "sofa_standard";
        public const string ConsoleCatalogId = "console_standard";
        public const string DirectRouteId = "direct";
        public const string NorthRouteId = "north_detour";
        public const int DefaultRoomWidthMm = 6000;
        public const int DefaultRoomDepthMm = 5000;
        public const int RoomStepMm = 100;

        public static Catalog CreateCatalog() => new Catalog(PackId, CatalogVersion, new[]
        {
            new CatalogEntry(SofaCatalogId, "Standard sofa", 1800, 800, 900, CollisionParticipation.Payload,
                new[] { EntityRole.Payload }, "sofa_standard_greybox", 50, new[] { 0 }, "tryscape_original_greybox"),
            new CatalogEntry(ConsoleCatalogId, "Console table", 600, 1000, 800, CollisionParticipation.FloorObstruction,
                new[] { EntityRole.Blocker }, "console_standard_greybox", 50, new[] { 0, 90, 180, 270 },
                "tryscape_original_greybox"),
        });
    }

    public enum ObstructionState
    {
        /// <summary>Console centred on the direct route at (2500, 2500).</summary>
        Center,

        /// <summary>Console parked south at (2500, 800).</summary>
        ParkedSouth,
    }

    public sealed class MovingDayParameters
    {
        public MovingDayParameters(int roomWidthMm, int roomDepthMm, ObstructionState obstruction, string routeId)
        {
            RoomWidthMm = roomWidthMm;
            RoomDepthMm = roomDepthMm;
            Obstruction = obstruction;
            RouteId = routeId;
        }

        public static MovingDayParameters Example =>
            new MovingDayParameters(MovingDayPack.DefaultRoomWidthMm, MovingDayPack.DefaultRoomDepthMm,
                ObstructionState.Center, MovingDayPack.DirectRouteId);

        public int RoomWidthMm { get; }
        public int RoomDepthMm { get; }
        public ObstructionState Obstruction { get; }
        public string RouteId { get; }

        /// <summary>Null when valid; otherwise a short reason code for the creation UI.</summary>
        public string Problem()
        {
            if (RoomWidthMm < RoomSpec.MinDimensionMm || RoomWidthMm > RoomSpec.MaxDimensionMm ||
                RoomDepthMm < RoomSpec.MinDimensionMm || RoomDepthMm > RoomSpec.MaxDimensionMm)
                return "room_out_of_range";
            if (RouteId != MovingDayPack.DirectRouteId && RouteId != MovingDayPack.NorthRouteId)
                return "unknown_route";
            return null;
        }

        public MovingDayParameters With(int? widthMm = null, int? depthMm = null, ObstructionState? obstruction = null, string routeId = null) =>
            new MovingDayParameters(widthMm ?? RoomWidthMm, depthMm ?? RoomDepthMm, obstruction ?? Obstruction, routeId ?? RouteId);
    }

    /// <summary>
    /// Deterministic scene assembly for the pack. R0 rule (D06.5): internal route/object coordinates are fixed across
    /// the 5-8 m room range; only the enclosing room changes. This is constrained assembly, not route optimisation.
    /// </summary>
    public static class MovingDayTemplate
    {
        public static SceneSpec Assemble(MovingDayParameters parameters, IIdGenerator ids)
        {
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));
            if (ids == null)
                throw new ArgumentNullException(nameof(ids));
            var problem = parameters.Problem();
            if (problem != null)
                throw new ArgumentException($"Invalid Moving Day parameters: {problem}", nameof(parameters));

            // Fixed allocation order keeps fixtures reproducible: scene, project, sofa, console.
            var sceneId = ids.NewId();
            var projectId = ids.NewId();
            var sofaId = ids.NewId();
            var consoleId = ids.NewId();

            var console = parameters.Obstruction == ObstructionState.Center ? new Pose(2500, 2500, 0) : new Pose(2500, 800, 0);
            var entities = new[]
            {
                new EntitySpec(sofaId, MovingDayPack.SofaCatalogId, EntityRole.Payload, new Pose(1000, 2500, 0), false, "Delivery sofa"),
                new EntitySpec(consoleId, MovingDayPack.ConsoleCatalogId, EntityRole.Blocker, console, true, "Console table"),
            };
            var routes = new[]
            {
                new RouteSpec(MovingDayPack.DirectRouteId, "Direct approach", sofaId, 250,
                    new[] { new Waypoint(1000, 2500), new Waypoint(4000, 2500), new Waypoint(4000, 3800) }),
                new RouteSpec(MovingDayPack.NorthRouteId, "Northern approach", sofaId, 250,
                    new[] { new Waypoint(1000, 2500), new Waypoint(1000, 4000), new Waypoint(4000, 4000), new Waypoint(4000, 3800) }),
            };
            var assumptions = new[]
            {
                new Assumption("illustrative_layout", "Prepared example; dimensions are not measurements of your home.",
                    AssumptionSource.Template, false),
                new Assumption("upright_fixed_orientation",
                    "The sofa remains upright at a fixed orientation; lifting and turning are not assessed.",
                    AssumptionSource.ScenarioModel, false),
            };

            return new SceneSpec(sceneId, projectId, MovingDayPack.PackId, MovingDayPack.PackVersion,
                MovingDayPack.CatalogVersion, 0, new RoomSpec(parameters.RoomWidthMm, parameters.RoomDepthMm),
                entities, routes, parameters.RouteId, assumptions, Provenance.Illustrative, Array.Empty<MeasurementSource>());
        }
    }
}
